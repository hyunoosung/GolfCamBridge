using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using SpinnakerNET;

namespace GolfCamBridge
{
    internal enum ChannelState { Standby, Streaming, NoCamera, Faulted }

    /// <summary>
    /// One Golf Cam: the virtual webcam lives for the whole app lifetime (so Premier can always find it),
    /// while the physical camera is opened only when needed and re-opened automatically after failures.
    /// All methods except the standby thread run on the supervisor thread.
    /// </summary>
    internal sealed class CameraChannel : IDisposable
    {
        private const double StallSeconds = 3.0;    // no frames this long => restart the camera
        private const double RetrySeconds = 3.0;    // wait between reconnect attempts

        public CameraConfig Cfg { get; }
        public ChannelState State { get; private set; } = ChannelState.Standby;
        public string Detail { get; private set; } = "";
        public bool AppConnected { get; private set; }

        private readonly Softcam _softcam;
        private volatile int _standbyFps;

        /// <summary>Debayer algorithm for the next camera start (see RestartCamera).</summary>
        public ColorProcessingAlgorithm Algo { get; set; }

        /// <summary>Standby image rate; takes effect immediately.</summary>
        public int StandbyFps
        {
            get => _standbyFps;
            set => _standbyFps = Math.Max(1, Math.Min(30, value));
        }

        public int OutWidth => _softcam.Width;
        public int OutHeight => _softcam.Height;
        private CameraWorker _worker;
        private IntPtr _standbyFrame = IntPtr.Zero;
        private Thread _standbyThread;
        private volatile bool _disposed;
        private volatile CameraWorker _activeWorker;   // read by the standby thread

        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _lastConnectedAt = double.NegativeInfinity;
        private double _nextRetryAt;
        private long _fpsLastCount;
        private double _fpsLastTime;
        public double Fps { get; private set; }

        public CameraChannel(CameraConfig cfg, string softcamDll, ColorProcessingAlgorithm algo, int standbyFps)
        {
            Cfg = cfg;
            Algo = algo;
            StandbyFps = standbyFps;

            int w = cfg.Width & ~3, h = cfg.Height & ~3;
            if (w <= 0 || h <= 0)
                throw new InvalidOperationException($"{cfg.Name}: Width/Height must be set in golfcam.json (e.g. 1280 x 720).");
            if (!File.Exists(softcamDll))
                throw new FileNotFoundException("Virtual camera DLL not found", softcamDll);

            _softcam = new Softcam(softcamDll, w, h, (float)cfg.FrameRate);
            _standbyFrame = RenderStandbyFrame(w, h, $"Golf Cam {cfg.VirtualCam}  ({cfg.Name})", "Camera starting...");
            _standbyThread = new Thread(StandbyLoop) { IsBackground = true, Name = cfg.Name + "-standby" };
            _standbyThread.Start();
            Log.Info($"Golf Cam {cfg.VirtualCam} ready ({w}x{h}) for {cfg.Name}");
        }

        /// <summary>Called every ~250 ms by the supervisor.</summary>
        public void Tick(ManagedSystem system, BridgeConfig bc)
        {
            Log.Prefix = Cfg.Name;
            double now = _clock.Elapsed.TotalSeconds;
            AppConnected = _softcam.IsConnected;
            if (AppConnected) _lastConnectedAt = now;

            bool wanted = bc.AlwaysOn || (now - _lastConnectedAt) < Math.Max(1, bc.IdleReleaseSeconds);

            if (_worker != null)
            {
                UpdateFps(now);
                if (!wanted)
                {
                    Log.Info("No app using the Golf Cam - releasing camera");
                    StopWorker();
                    State = ChannelState.Standby;
                    Detail = "";
                }
                else if (_worker.SecondsSinceLastFrame > StallSeconds)
                {
                    Log.Error($"No frames for {StallSeconds:0}s - restarting camera");
                    StopWorker();
                    State = ChannelState.Faulted;
                    Detail = "no frames, retrying";
                    _nextRetryAt = now + RetrySeconds;
                }
                return;
            }

            if (wanted && now >= _nextRetryAt)
                TryStart(system, now);
            else if (!wanted && State != ChannelState.Standby)
            {
                State = ChannelState.Standby;
                Detail = "";
            }
        }

        private void TryStart(ManagedSystem system, double now)
        {
            ManagedCameraList list = null;
            try
            {
                list = system.GetCameras();
                IManagedCamera cam = FindCamera(list, Cfg);
                if (cam == null)
                {
                    list.Clear();
                    if (State != ChannelState.NoCamera) Log.Error($"Camera not found (serial '{Cfg.Serial}', index {Cfg.Index}) - will keep trying");
                    State = ChannelState.NoCamera;
                    Detail = "camera not found";
                    _nextRetryAt = now + RetrySeconds;
                    return;
                }

                var worker = new CameraWorker(cam, list, Cfg, Algo, _softcam);
                list = null;   // owned by the worker now
                try
                {
                    worker.Setup();
                    worker.Start();
                }
                catch
                {
                    worker.Dispose();
                    throw;
                }
                _worker = worker;
                _activeWorker = worker;
                _fpsLastCount = 0;
                _fpsLastTime = now;
                Fps = 0;
                State = ChannelState.Streaming;
                Detail = "";
            }
            catch (Exception ex)
            {
                try { list?.Clear(); } catch { }
                Log.Error("Could not open camera: " + ex.Message);
                State = ChannelState.Faulted;
                Detail = ex.Message.Length > 60 ? ex.Message.Substring(0, 60) : ex.Message;
                _nextRetryAt = now + RetrySeconds;
            }
        }

        private void UpdateFps(double now)
        {
            double dt = now - _fpsLastTime;
            if (dt < 1.0) return;
            long c = _worker.Grabbed;
            Fps = (c - _fpsLastCount) / dt;
            _fpsLastCount = c;
            _fpsLastTime = now;
        }

        private void StopWorker()
        {
            var w = _worker;
            _worker = null;
            _activeWorker = null;
            Fps = 0;
            try { w?.Dispose(); } catch (Exception ex) { Log.Error("Error releasing camera: " + ex.Message); }
        }

        /// <summary>Stops the physical camera now (used before reload/exit). The virtual cam stays.</summary>
        public void ReleaseCamera() => StopWorker();

        /// <summary>
        /// Re-opens the physical camera with the current Cfg (FrameRate, PixelFormat, Reverse, Algo) on the next Tick.
        /// The virtual cam (softcam) stays, so apps like Premier stay connected. If no app wants the camera
        /// (OnDemand idle) it simply stays off and the new values are used on the next start.
        /// </summary>
        public void RestartCamera()
        {
            bool wasOpen = _worker != null;
            StopWorker();
            if (wasOpen) { State = ChannelState.Standby; Detail = "restarting"; }
            _nextRetryAt = 0;
        }

        // Settings window hooks. All run on a thread holding the supervisor lock; null/false when the camera is not open.
        public LiveInfo ApplyExposureGain() => _worker?.ApplyExposureGain();
        public LiveInfo ReadLive() => _worker?.ReadLive();

        public bool SamplePreview(byte[] dst, int w, int h, int[] hist)
        {
            if (_worker == null || !_worker.IsDelivering) return false;
            _worker.SamplePreview(dst, w, h, hist);
            return true;
        }

        private void StandbyLoop()
        {
            while (!_disposed)
            {
                var w = _activeWorker;
                if (w == null || !w.IsDelivering)
                {
                    try { _softcam.Send(_standbyFrame); } catch { }
                }
                Thread.Sleep(1000 / _standbyFps);
            }
        }

        public static IManagedCamera FindCamera(ManagedCameraList list, CameraConfig cc)
        {
            string wanted = (cc.Serial ?? "").Trim();
            int i = 0;
            IManagedCamera byIndex = null;
            foreach (IManagedCamera c in list)
            {
                if (wanted.Length > 0 && Nodes.GetString(c.GetTLDeviceNodeMap(), "DeviceSerialNumber") == wanted)
                    return c;
                if (wanted.Length == 0 && i == cc.Index) byIndex = c;
                i++;
            }
            return byIndex;
        }

        /// <summary>Dark placeholder image with a label, as top-down BGR24.</summary>
        private static IntPtr RenderStandbyFrame(int w, int h, string title, string subtitle)
        {
            IntPtr buf = Marshal.AllocHGlobal(w * h * 3);
            using (var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
            {
                using (var g = Graphics.FromImage(bmp))
                using (var big = new Font("Segoe UI", Math.Max(12, h / 18f), FontStyle.Bold, GraphicsUnit.Pixel))
                using (var small = new Font("Segoe UI", Math.Max(10, h / 30f), GraphicsUnit.Pixel))
                using (var fg = new SolidBrush(Color.FromArgb(200, 200, 200)))
                using (var dim = new SolidBrush(Color.FromArgb(130, 130, 130)))
                {
                    g.Clear(Color.FromArgb(24, 26, 30));
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                    var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString(title, big, fg, new RectangleF(0, 0, w, h * 0.92f), fmt);
                    g.DrawString(subtitle, small, dim, new RectangleF(0, h * 0.12f, w, h), fmt);
                }
                var data = bmp.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                try
                {
                    var row = new byte[w * 3];
                    for (int y = 0; y < h; y++)
                    {
                        Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                        Marshal.Copy(row, 0, buf + y * w * 3, row.Length);
                    }
                }
                finally { bmp.UnlockBits(data); }
            }
            return buf;
        }

        public void Dispose()
        {
            _disposed = true;
            _standbyThread?.Join(1000);
            StopWorker();
            _softcam.Dispose();
            if (_standbyFrame != IntPtr.Zero) { Marshal.FreeHGlobal(_standbyFrame); _standbyFrame = IntPtr.Zero; }
        }
    }
}
