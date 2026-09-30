using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using SpinnakerNET;
using SpinnakerNET.GenApi;

namespace GolfCamBridge
{
    /// <summary>
    /// Owns one opened Blackfly for one streaming session: configures it, grabs frames,
    /// debayers to BGR8 and pushes them into the channel's Softcam.
    /// Created when streaming starts, disposed when it stops (which releases the camera for other apps).
    /// </summary>
    internal sealed class CameraWorker : IDisposable
    {
        private readonly IManagedCamera _cam;
        private readonly ManagedCameraList _list;   // keeps the camera handle alive
        private readonly CameraConfig _cfg;
        private readonly ColorProcessingAlgorithm _algo;
        private readonly Softcam _sink;
        private readonly int _outW, _outH;
        private IntPtr _frame = IntPtr.Zero;
        private Thread _thread;
        private volatile bool _stop;
        private bool _acquiring;

        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _lastFrameMs = -1;
        private long _startedMs;
        private long _grabbed, _incomplete;

        public string Serial { get; }
        public string Model { get; }

        public CameraWorker(IManagedCamera cam, ManagedCameraList list, CameraConfig cfg,
                            ColorProcessingAlgorithm algo, Softcam sink)
        {
            _cam = cam;
            _list = list;
            _cfg = cfg;
            _algo = algo;
            _sink = sink;
            _outW = sink.Width;
            _outH = sink.Height;
            INodeMap tl = cam.GetTLDeviceNodeMap();
            Serial = Nodes.GetString(tl, "DeviceSerialNumber");
            Model = Nodes.GetString(tl, "DeviceModelName");
        }

        /// <summary>Opens and configures the camera. Call from the supervisor thread.</summary>
        public void Setup()
        {
            _cam.Init();
            INodeMap m = _cam.GetNodeMap();
            Log.Info($"Opening {Model}  S/N {Serial}");

            // --- Image format (must be done while not streaming) ---
            Nodes.SetEnum(m, "AcquisitionMode", "Continuous");
            Nodes.SetEnum(m, "BalanceWhiteAuto", "Off");
            Nodes.SetBool(m, "IspEnable", false);                 // on-camera ISP caps the frame rate
            Nodes.SetEnum(m, "PixelFormat", _cfg.PixelFormat, "BayerRG8", "BayerGB8", "BayerGR8", "BayerBG8", "Mono8");
            Nodes.SetEnum(m, "AdcBitDepth", "Bit8", "Bit10", "Bit12");
            Nodes.SetBool(m, "ReverseX", _cfg.ReverseX);
            Nodes.SetBool(m, "ReverseY", _cfg.ReverseY);
            ApplyRoi(m);

            // --- Exposure / gain ---
            Nodes.SetEnum(m, "ExposureAuto", "Off");
            Nodes.SetEnum(m, "ExposureMode", "Timed");
            Nodes.SetFloat(m, "ExposureTime", _cfg.ExposureUs);
            Nodes.SetEnum(m, "GainAuto", "Off");
            Nodes.SetFloat(m, "Gain", _cfg.GainDb);

            // --- Bandwidth / frame rate ---
            Nodes.SetIntMax(m, "DeviceLinkThroughputLimit");
            Nodes.SetBool(m, "AcquisitionFrameRateEnable", true);
            Nodes.SetFloat(m, "AcquisitionFrameRate", _cfg.FrameRate);
            double resulting = Nodes.GetFloat(m, "AcquisitionResultingFrameRate", double.NaN);
            if (!double.IsNaN(resulting))
                Log.Info($"  -> camera reports {resulting:0.#} fps achievable");

            Nodes.SetEnum(_cam.GetTLStreamNodeMap(), "StreamBufferHandlingMode", "NewestOnly");

            int w = (int)Nodes.GetInt(m, "Width", 0), h = (int)Nodes.GetInt(m, "Height", 0);
            if (w != _outW || h != _outH)
                Log.Error($"  camera is {w}x{h} but Golf Cam {_cfg.VirtualCam} is {_outW}x{_outH} - image will be cropped/padded. Use a size the camera accepts.");

            _frame = Marshal.AllocHGlobal(_outW * _outH * 3);
            // black background for any area the camera does not cover
            unsafe
            {
                byte* p = (byte*)_frame;
                for (long i = 0, n = (long)_outW * _outH * 3; i < n; i++) p[i] = 0;
            }
        }

        private void ApplyRoi(INodeMap m)
        {
            if (_cfg.Width <= 0 && _cfg.Height <= 0) return;
            Nodes.SetInt(m, "OffsetX", 0);
            Nodes.SetInt(m, "OffsetY", 0);
            if (_cfg.Width > 0) Nodes.SetInt(m, "Width", _cfg.Width);
            if (_cfg.Height > 0) Nodes.SetInt(m, "Height", _cfg.Height);
            long w = Nodes.GetInt(m, "Width", 0), h = Nodes.GetInt(m, "Height", 0);
            long maxW = Nodes.GetInt(m, "WidthMax", w), maxH = Nodes.GetInt(m, "HeightMax", h);
            Nodes.SetInt(m, "OffsetX", (maxW - w) / 2);
            Nodes.SetInt(m, "OffsetY", (maxH - h) / 2);
        }

        public void Start()
        {
            _cam.BeginAcquisition();
            _acquiring = true;
            _startedMs = _clock.ElapsedMilliseconds;
            _thread = new Thread(Run) { IsBackground = true, Name = _cfg.Name, Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }

        /// <summary>Seconds since the last good frame (or since start if none yet).</summary>
        public double SecondsSinceLastFrame
        {
            get
            {
                long last = Interlocked.Read(ref _lastFrameMs);
                long reference = last >= 0 ? last : _startedMs;
                return (_clock.ElapsedMilliseconds - reference) / 1000.0;
            }
        }

        /// <summary>True while real frames are flowing (used to pause the standby image).</summary>
        public bool IsDelivering => Interlocked.Read(ref _lastFrameMs) >= 0 && SecondsSinceLastFrame < 0.5;

        public long Grabbed => Interlocked.Read(ref _grabbed);
        public long Incomplete => Interlocked.Read(ref _incomplete);

        /// <summary>Writes ExposureUs/GainDb from the config while streaming (no restart). Caller holds the supervisor lock.</summary>
        public LiveInfo ApplyExposureGain()
        {
            INodeMap m = _cam.GetNodeMap();
            Nodes.SetFloat(m, "ExposureTime", _cfg.ExposureUs);
            Nodes.SetFloat(m, "Gain", _cfg.GainDb);
            // Exposure moves the frame-rate ceiling; ask for the configured rate again (clamped to the new max).
            Nodes.SetFloat(m, "AcquisitionFrameRate", _cfg.FrameRate);
            return ReadLive();
        }

        /// <summary>Current values and ranges straight from the camera. Caller holds the supervisor lock.</summary>
        public LiveInfo ReadLive()
        {
            INodeMap m = _cam.GetNodeMap();
            var li = new LiveInfo();
            Nodes.GetFloatInfo(m, "ExposureTime", out li.Exposure, out li.ExposureMin, out li.ExposureMax);
            Nodes.GetFloatInfo(m, "Gain", out li.Gain, out li.GainMin, out li.GainMax);
            Nodes.GetFloatInfo(m, "AcquisitionFrameRate", out li.FpsSet, out _, out li.FpsMax);
            li.FpsResulting = Nodes.GetFloat(m, "AcquisitionResultingFrameRate", double.NaN);
            li.WidthMax = Nodes.GetInt(m, "WidthMax", 0);
            li.HeightMax = Nodes.GetInt(m, "HeightMax", 0);
            return li;
        }

        /// <summary>
        /// Nearest-neighbour downscale of the last frame already produced for the virtual cam, plus a luma histogram.
        /// No extra grab. Caller holds the supervisor lock, which is what keeps _frame alive (Dispose runs under it too).
        /// ponytail: reads _frame while the grab thread may be overwriting it - a preview can tear, which is fine for this purpose.
        /// </summary>
        public unsafe void SamplePreview(byte[] dst, int dw, int dh, int[] hist)
        {
            if (_frame == IntPtr.Zero) return;
            byte* src = (byte*)_frame;
            int o = 0;
            for (int y = 0; y < dh; y++)
            {
                byte* row = src + (long)(y * (long)_outH / dh) * _outW * 3;
                for (int x = 0; x < dw; x++)
                {
                    byte* p = row + (long)(x * (long)_outW / dw) * 3;
                    byte b = p[0], g = p[1], r = p[2];
                    dst[o++] = b; dst[o++] = g; dst[o++] = r;
                    hist[(r * 77 + g * 150 + b * 29) >> 8]++;
                }
            }
        }

        private unsafe void Run()
        {
            Log.Prefix = _cfg.Name;
            IManagedImageProcessor processor = new ManagedImageProcessor();
            processor.SetColorProcessing(_algo);
            int rowBytes = _outW * 3;
            int errorsLogged = 0;

            while (!_stop)
            {
                try
                {
                    using (IManagedImage raw = _cam.GetNextImage(1000))
                    {
                        if (raw.IsIncomplete) { Interlocked.Increment(ref _incomplete); continue; }

                        using (IManagedImage bgr = processor.Convert(raw, PixelFormatEnums.BGR8))
                        {
                            int srcW = (int)bgr.Width;
                            int srcStride = srcW * 3;                    // BGR8 output is tightly packed
                            byte* src = (byte*)bgr.DataPtr;
                            byte* dst = (byte*)_frame;
                            int rows = Math.Min(_outH, (int)bgr.Height);
                            int copy = Math.Min(rowBytes, srcStride);
                            for (int y = 0; y < rows; y++)
                                Buffer.MemoryCopy(src + (long)y * srcStride, dst + (long)y * rowBytes, rowBytes, copy);
                        }
                    }
                    _sink.Send(_frame);   // top-down BGR24; softcam converts to what the host negotiated
                    Interlocked.Increment(ref _grabbed);
                    Interlocked.Exchange(ref _lastFrameMs, _clock.ElapsedMilliseconds);
                    errorsLogged = 0;
                }
                catch (SpinnakerException ex)
                {
                    if (_stop) break;
                    // Log the first few errors of a burst; the supervisor restarts the camera if frames stop.
                    if (errorsLogged++ < 3) Log.Error("Grab error: " + ex.Message);
                }
                catch (Exception ex)
                {
                    if (errorsLogged++ < 3) Log.Error("Unexpected: " + ex.Message);
                    Thread.Sleep(100);
                }
            }
        }

        public void Dispose()
        {
            _stop = true;
            _thread?.Join(3000);
            if (_acquiring) { try { _cam.EndAcquisition(); } catch (SpinnakerException) { } }
            try { _cam.DeInit(); } catch (SpinnakerException) { }
            try { _cam.Dispose(); } catch (SpinnakerException) { }
            try { _list.Clear(); } catch (SpinnakerException) { }
            if (_frame != IntPtr.Zero) { Marshal.FreeHGlobal(_frame); _frame = IntPtr.Zero; }
            Log.Info($"Released {Model}  S/N {Serial}");
        }
    }
}
