using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using SpinnakerNET;
using SpinnakerNET.GenApi;

namespace GolfCamBridge
{
    internal sealed class ChannelStatus
    {
        public string Name;
        public int VirtualCam;
        public ChannelState State;
        public string Detail;
        public double Fps;
        public bool AppConnected;
    }

    /// <summary>Values and ranges read from an open camera (settings window).</summary>
    internal sealed class LiveInfo
    {
        public double Exposure, ExposureMin, ExposureMax;
        public double Gain, GainMin, GainMax;
        public double FpsSet, FpsMax, FpsResulting;
        public long WidthMax, HeightMax;
    }

    /// <summary>One settings-window poll of one camera.</summary>
    internal sealed class CamPoll
    {
        public int Version;           // BridgeHost.ConfigVersion at poll time
        public ChannelStatus Status;  // null: this Golf Cam could not be created
        public LiveInfo Live;         // null: physical camera not open
        public byte[] Pixels;         // preview, top-down BGR24, PreviewW x PreviewH; null when no live frames
        public int PreviewW, PreviewH;
        public int[] Hist;            // 256-bin luma histogram of the preview
        public string Error;
    }

    /// <summary>Owns the Spinnaker system and all Golf Cam channels; runs the supervisor loop.</summary>
    internal sealed class BridgeHost : IDisposable
    {
        private readonly string _cfgPath;
        private readonly object _gate = new object();
        private ManagedSystem _system;
        private List<CameraChannel> _channels = new List<CameraChannel>();
        private Thread _supervisor;
        private volatile bool _stop;

        public BridgeConfig Config { get; private set; }
        public string LastError { get; private set; } = "";

        public BridgeHost(string cfgPath) { _cfgPath = cfgPath; }

        public void Start()
        {
            _system = new ManagedSystem();
            LibraryVersion v = _system.GetLibraryVersion();
            Log.Info($"GolfCamBridge started - Spinnaker {v.major}.{v.minor}.{v.type}.{v.build}");
            Reload();
            _supervisor = new Thread(SupervisorLoop) { IsBackground = true, Name = "supervisor" };
            _supervisor.Start();
        }

        /// <summary>Bumped on every reload so an open settings window can tell its camera indexes went stale.</summary>
        public int ConfigVersion { get; private set; }

        /// <summary>(Re)reads golfcam.json and recreates the virtual cams.</summary>
        public void Reload()
        {
            lock (_gate)
            {
                Log.Prefix = null;
                DisposeChannels();
                LastError = "";
                ConfigVersion++;
                try
                {
                    Config = BridgeConfig.Load(_cfgPath);
                }
                catch (Exception ex)
                {
                    LastError = "golfcam.json: " + ex.Message;
                    Log.Error(LastError);
                    Config = Config ?? new BridgeConfig();
                    UpdateSnapshot();
                    return;
                }
                BuildChannels();
            }
        }

        /// <summary>Same as Reload but from an in-memory config (nothing is read from or written to disk). Caller holds _gate.</summary>
        private void ReloadLocked(BridgeConfig cfg)
        {
            Log.Prefix = null;
            DisposeChannels();
            LastError = "";
            ConfigVersion++;
            Config = cfg;
            BuildChannels();
        }

        private void BuildChannels()
        {
            if (!Enum.TryParse(Config.ColorAlgorithm, true, out ColorProcessingAlgorithm algo))
            {
                Log.Error($"Unknown ColorAlgorithm '{Config.ColorAlgorithm}', using HQ_LINEAR");
                algo = ColorProcessingAlgorithm.HQ_LINEAR;
            }
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string softcamDir = Path.GetFullPath(Path.Combine(exeDir, Config.SoftcamDir ?? @"..\bin"));

            foreach (CameraConfig cc in Config.Cameras ?? new CameraConfig[0])
            {
                try
                {
                    string dll = Path.Combine(softcamDir, $"softcam_golfcam{cc.VirtualCam}.dll");
                    _channels.Add(new CameraChannel(cc, dll, algo, Config.StandbyFps));
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    Log.Error($"{cc.Name}: {ex.Message}");
                }
            }
            UpdateSnapshot();
            Log.Info($"Mode: {(Config.AlwaysOn ? "AlwaysOn" : $"OnDemand (release after {Config.IdleReleaseSeconds}s idle)")}");
        }

        public void SetAlwaysOn(bool on)
        {
            lock (_gate)
            {
                Config.Mode = on ? "AlwaysOn" : "OnDemand";
                try { Config.Save(_cfgPath); } catch (Exception ex) { Log.Error("Could not save golfcam.json: " + ex.Message); }
                Log.Prefix = null;
                Log.Info("Mode changed to " + Config.Mode);
            }
        }

        private volatile List<ChannelStatus> _snapshot = new List<ChannelStatus>();

        /// <summary>Latest status (lock-free so the tray UI never waits on a camera opening).</summary>
        public List<ChannelStatus> Snapshot() => _snapshot;

        private void UpdateSnapshot() => _snapshot = _channels.Select(StatusOf).ToList();

        private static ChannelStatus StatusOf(CameraChannel c) => new ChannelStatus
        {
            Name = c.Cfg.Name,
            VirtualCam = c.Cfg.VirtualCam,
            State = c.State,
            Detail = c.Detail,
            Fps = c.Fps,
            AppConnected = c.AppConnected,
        };

        // ---------------------------------------------------------------------------------------------
        // Settings window API. Every method takes _gate, so camera/worker/channel state is only touched
        // under the supervisor lock. Cameras are addressed by index into Config.Cameras. A camera that is
        // not open (OnDemand idle, unplugged) is never touched: values go into Config and are used on its
        // next start. Call these from a worker thread - _gate can be held for seconds while a camera opens.
        // ---------------------------------------------------------------------------------------------

        public BridgeConfig CloneConfig(out int version)
        {
            lock (_gate)
            {
                version = ConfigVersion;
                return Config.Clone();
            }
        }

        private CameraConfig CamCfg(int cam) =>
            Config?.Cameras != null && cam >= 0 && cam < Config.Cameras.Length ? Config.Cameras[cam] : null;

        private CameraChannel ChannelOf(int cam)
        {
            CameraConfig cc = CamCfg(cam);
            return cc == null ? null : _channels.FirstOrDefault(c => c.Cfg == cc);
        }

        /// <summary>Immediate group. Returns the camera readback, or null when the camera is not open (value kept for its next start).</summary>
        public LiveInfo ApplyExposureGain(int cam, double exposureUs, double gainDb)
        {
            lock (_gate)
            {
                CameraConfig cc = CamCfg(cam) ?? throw new ArgumentOutOfRangeException(nameof(cam));
                Log.Prefix = cc.Name;
                cc.ExposureUs = exposureUs;
                cc.GainDb = gainDb;
                LiveInfo live = ChannelOf(cam)?.ApplyExposureGain();
                if (live != null)
                {
                    // keep what the camera actually accepted (clamped); rounded so it survives the UI's decimal round trip
                    if (!double.IsNaN(live.Exposure)) cc.ExposureUs = Math.Round(live.Exposure, 2);
                    if (!double.IsNaN(live.Gain)) cc.GainDb = Math.Round(live.Gain, 2);
                }
                return live;
            }
        }

        /// <summary>Common immediate group: the supervisor reads these every tick, the standby thread every frame.</summary>
        public void ApplyCommon(string mode, int idleReleaseSeconds, int standbyFps)
        {
            lock (_gate)
            {
                Config.Mode = string.Equals(mode, "AlwaysOn", StringComparison.OrdinalIgnoreCase) ? "AlwaysOn" : "OnDemand";
                Config.IdleReleaseSeconds = Math.Max(1, idleReleaseSeconds);
                Config.StandbyFps = Math.Max(1, Math.Min(30, standbyFps));
                foreach (var ch in _channels) ch.StandbyFps = Config.StandbyFps;
            }
        }

        /// <summary>Restart group: the camera re-opens, the virtual cam stays (Premier stays connected).</summary>
        public BridgeConfig ApplyCameraRestart(int cam, double frameRate, string pixelFormat, bool reverseX, bool reverseY)
        {
            lock (_gate)
            {
                CameraConfig cc = CamCfg(cam) ?? throw new ArgumentOutOfRangeException(nameof(cam));
                Log.Prefix = cc.Name;
                cc.FrameRate = Math.Max(1, frameRate);
                cc.PixelFormat = pixelFormat;
                cc.ReverseX = reverseX;
                cc.ReverseY = reverseY;
                Log.Info($"Settings: restart camera (FrameRate {cc.FrameRate}, {cc.PixelFormat}, ReverseX {reverseX}, ReverseY {reverseY})");
                ChannelOf(cam)?.RestartCamera();
                return Config.Clone();
            }
        }

        /// <summary>Restarts every camera with a new debayer algorithm (virtual cams stay).</summary>
        public BridgeConfig ApplyColorAlgorithm(string name)
        {
            lock (_gate)
            {
                Log.Prefix = null;
                if (!Enum.TryParse(name, true, out ColorProcessingAlgorithm algo))
                    throw new ArgumentException("Unknown ColorAlgorithm: " + name);
                Config.ColorAlgorithm = algo.ToString();
                Log.Info("Settings: ColorAlgorithm " + Config.ColorAlgorithm + " - restarting cameras");
                foreach (var ch in _channels) { ch.Algo = algo; ch.RestartCamera(); }
                return Config.Clone();
            }
        }

        /// <summary>ROI = virtual cam size, so all Golf Cams are recreated (connected apps drop). Not saved to disk.</summary>
        public BridgeConfig ApplyRoi(int[] widths, int[] heights, out int version)
        {
            lock (_gate)
            {
                BridgeConfig cfg = Config.Clone();
                for (int i = 0; i < cfg.Cameras.Length && i < widths.Length; i++)
                {
                    cfg.Cameras[i].Width = widths[i] & ~3;
                    cfg.Cameras[i].Height = heights[i] & ~3;
                }
                Log.Prefix = null;
                Log.Info("Settings: ROI changed - recreating Golf Cams");
                ReloadLocked(cfg);
                version = ConfigVersion;
                return Config.Clone();
            }
        }

        /// <summary>
        /// Puts the running bridge back to <paramref name="target"/>: live values are written to open cameras,
        /// cameras whose restart-group values differ are restarted, and only an ROI difference recreates the virtual cams.
        /// </summary>
        public BridgeConfig RevertTo(BridgeConfig target, out int version)
        {
            lock (_gate)
            {
                if (NeedsReload(Config, target))
                {
                    Log.Prefix = null;
                    Log.Info("Settings: revert - recreating Golf Cams (ROI differs)");
                    ReloadLocked(target.Clone());
                }
                else
                {
                    ApplyCommon(target.Mode, target.IdleReleaseSeconds, target.StandbyFps);
                    ColorProcessingAlgorithm algo = ColorProcessingAlgorithm.HQ_LINEAR;
                    bool algoChanged = !string.Equals(Config.ColorAlgorithm, target.ColorAlgorithm, StringComparison.OrdinalIgnoreCase)
                                       && Enum.TryParse(target.ColorAlgorithm, true, out algo);
                    if (algoChanged)
                    {
                        Config.ColorAlgorithm = target.ColorAlgorithm;
                        foreach (var ch in _channels) ch.Algo = algo;
                    }
                    for (int i = 0; i < Config.Cameras.Length; i++)
                    {
                        CameraConfig cc = Config.Cameras[i], tc = target.Cameras[i];
                        Log.Prefix = cc.Name;
                        bool restart = algoChanged || cc.FrameRate != tc.FrameRate || cc.PixelFormat != tc.PixelFormat
                                       || cc.ReverseX != tc.ReverseX || cc.ReverseY != tc.ReverseY;
                        cc.FrameRate = tc.FrameRate;
                        cc.PixelFormat = tc.PixelFormat;
                        cc.ReverseX = tc.ReverseX;
                        cc.ReverseY = tc.ReverseY;
                        cc.ExposureUs = tc.ExposureUs;
                        cc.GainDb = tc.GainDb;
                        CameraChannel chn = ChannelOf(i);
                        if (restart) chn?.RestartCamera();
                        else chn?.ApplyExposureGain();
                    }
                    Log.Prefix = null;
                    Log.Info("Settings: reverted");
                }
                version = ConfigVersion;
                return Config.Clone();
            }
        }

        public static bool NeedsReload(BridgeConfig a, BridgeConfig b)
        {
            if ((a.Cameras?.Length ?? 0) != (b.Cameras?.Length ?? 0)) return true;
            for (int i = 0; i < a.Cameras.Length; i++)
                if (a.Cameras[i].Width != b.Cameras[i].Width || a.Cameras[i].Height != b.Cameras[i].Height) return true;
            return false;
        }

        public void SaveConfig(BridgeConfig cfg)
        {
            lock (_gate)
            {
                cfg.Save(_cfgPath);
                Log.Prefix = null;
                Log.Info("Settings saved to " + _cfgPath);
            }
        }

        /// <summary>Status + camera readback + (optionally) a downscaled copy of the latest frame. No extra grab.</summary>
        public CamPoll Poll(int cam, int previewWidth)
        {
            var r = new CamPoll();
            lock (_gate)
            {
                r.Version = ConfigVersion;
                CameraChannel ch = ChannelOf(cam);
                if (ch == null) return r;
                r.Status = StatusOf(ch);
                try
                {
                    r.Live = ch.ReadLive();
                    if (previewWidth > 0 && ch.OutWidth > 0)
                    {
                        int pw = Math.Min(previewWidth, ch.OutWidth);
                        int ph = Math.Max(1, (int)((long)pw * ch.OutHeight / ch.OutWidth));
                        var px = new byte[pw * ph * 3];
                        var hist = new int[256];
                        if (ch.SamplePreview(px, pw, ph, hist))
                        {
                            r.Pixels = px; r.PreviewW = pw; r.PreviewH = ph; r.Hist = hist;
                        }
                    }
                }
                catch (Exception ex) { r.Error = ex.Message; }
            }
            return r;
        }

        /// <summary>Lists cameras the SDK can see (cameras currently opened by this app are still listed).</summary>
        public string DescribeConnectedCameras()
        {
            lock (_gate)
            {
                var sb = new StringBuilder();
                ManagedCameraList list = _system.GetCameras();
                try
                {
                    int i = 0;
                    foreach (IManagedCamera c in list)
                    {
                        INodeMap tl = c.GetTLDeviceNodeMap();
                        sb.AppendLine($"[{i++}] {Nodes.GetString(tl, "DeviceModelName")}   serial {Nodes.GetString(tl, "DeviceSerialNumber")}");
                    }
                    if (i == 0) sb.AppendLine("No cameras found.");
                }
                finally { list.Clear(); }
                return sb.ToString();
            }
        }

        private void SupervisorLoop()
        {
            while (!_stop)
            {
                lock (_gate)
                {
                    foreach (var ch in _channels)
                    {
                        try { ch.Tick(_system, Config); }
                        catch (Exception ex) { Log.Error("Supervisor: " + ex.Message); }
                    }
                    UpdateSnapshot();
                }
                Thread.Sleep(250);
            }
        }

        private void DisposeChannels()
        {
            foreach (var ch in _channels)
            {
                try { ch.Dispose(); } catch (Exception ex) { Log.Error("Dispose: " + ex.Message); }
            }
            _channels = new List<CameraChannel>();
        }

        public void Dispose()
        {
            _stop = true;
            _supervisor?.Join(3000);
            lock (_gate)
            {
                Log.Prefix = null;
                DisposeChannels();
                try { _system?.Dispose(); } catch { }
                _system = null;
                Log.Info("GolfCamBridge stopped");
            }
        }
    }
}
