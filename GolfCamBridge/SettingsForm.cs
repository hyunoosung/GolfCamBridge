using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace GolfCamBridge
{
    /// <summary>
    /// Non-modal settings window (tray menu "Settings...").
    ///
    /// Threads: the UI thread only touches controls. Every BridgeHost call that changes something runs on
    /// _opsThread, one at a time in click order (so e.g. Revert can never be overtaken by a late slider write);
    /// read-only status/preview polling runs on _pollThread. BridgeHost takes the supervisor lock inside each
    /// call. Results come back to the UI thread with _ui.Post (same pattern as TrayContext).
    ///
    /// Grades: immediate (Exposure/Gain, Mode/IdleReleaseSeconds/StandbyFps) - applied while you edit;
    /// restart (FrameRate/PixelFormat/Reverse, ColorAlgorithm) - Apply button, camera re-opens, Premier stays;
    /// reload (Width/Height) - Apply button, virtual cams are recreated, Premier drops.
    /// golfcam.json is written only by Save.
    /// </summary>
    internal sealed class SettingsForm : Form
    {
        private const int PreviewW = 320, PreviewH = 180;
        private const int PollMs = 125;   // <= 8 fps preview/status
        // Used only while no camera is open; the real ranges come from the camera nodes.
        private const double DefExpMin = 10, DefExpMax = 100000, DefGainMin = 0, DefGainMax = 48;
        private const int DefSizeMax = 4096;
        private static readonly string[] PixelFormats = { "BayerRG8", "BayerGB8", "BayerGR8", "BayerBG8", "Mono8" };
        private static readonly Color RestartTint = Color.FromArgb(255, 246, 222);
        private static readonly Color ReloadTint = Color.FromArgb(253, 230, 230);

        private readonly BridgeHost _host;
        private SynchronizationContext _ui;
        private readonly BridgeConfig _original;   // Revert target: values when the window opened
        private readonly string _originalJson;
        private BridgeConfig _applied;             // what the bridge is running with now
        private BridgeConfig _saved;               // last written to golfcam.json (initially: opened with)
        private string _savedJson;
        private int _version;                      // BridgeHost.ConfigVersion this window is in sync with
        private bool _stale, _allowClose, _forExit;
        private int _busy;                         // queued apply/save/revert operations
        private bool _syncing;                     // code (not the user) is setting control values
        private bool _commonDirty, _commonQueued;

        private readonly BlockingCollection<Action> _ops = new BlockingCollection<Action>();
        private Thread _opsThread, _pollThread;
        private volatile bool _closed;
        private volatile int _selectedCam;         // camera whose preview is drawn, -1 = none
        private volatile bool _minimized;
        private int _pollPending;                  // 1 while a poll result waits for the UI thread

        private readonly CamPage[] _cams;
        private readonly TabControl _tabs;
        private ComboBox _mode, _algo;
        private NumericUpDown _idle, _standbyFps;
        private Button _algoBtn;
        private readonly Button _save, _revert, _close;
        private readonly Label _status;
        private readonly System.Windows.Forms.Timer _applyTimer;

        public SettingsForm(BridgeHost host)
        {
            _host = host;
            _original = host.CloneConfig(out _version);
            _originalJson = _original.ToJson();
            _applied = _original.Clone();
            _saved = _original.Clone();
            _savedJson = _originalJson;

            Text = "GolfCamBridge Settings";
            Font = new Font("Segoe UI", 9f);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(790, 584);

            _tabs = new TabControl { Location = new Point(8, 8), Size = new Size(774, 522) };
            Controls.Add(_tabs);
            int n = _original.Cameras?.Length ?? 0;
            _cams = new CamPage[n];
            for (int i = 0; i < n; i++)
            {
                CameraConfig cc = _original.Cameras[i];
                var page = new TabPage($"Golf Cam {cc.VirtualCam} ({cc.Name})");
                _tabs.TabPages.Add(page);
                _cams[i] = BuildCamPage(page, i, cc.Name);
            }
            var common = new TabPage("Common");
            _tabs.TabPages.Add(common);
            BuildCommonPage(common);
            _tabs.SelectedIndexChanged += (s, e) => _selectedCam = _tabs.SelectedIndex < _cams.Length ? _tabs.SelectedIndex : -1;
            _selectedCam = n > 0 ? 0 : -1;

            _status = AddLabel(this, "", 12, 538, 456, 40);
            _save = AddButton(this, "Save", 478, 542, 96, 30, (s, e) => DoSave(false));
            _revert = AddButton(this, "Revert", 580, 542, 96, 30, (s, e) => DoRevert(_original, true, false));
            _close = AddButton(this, "Close", 682, 542, 96, 30, (s, e) => Close());

            SetControls(_original);
            UpdateButtons();
            SetStatus("Loaded the current settings. Changes are written to golfcam.json only when you press Save.", false);

            _applyTimer = new System.Windows.Forms.Timer { Interval = 100 };
            _applyTimer.Tick += (s, e) => FlushImmediate();
            Resize += (s, e) => _minimized = WindowState == FormWindowState.Minimized;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _ui = SynchronizationContext.Current;   // WinForms context of the tray UI thread
            _opsThread = new Thread(OpsLoop) { IsBackground = true, Name = "settings-ops" };
            _opsThread.Start();
            _pollThread = new Thread(PollLoop) { IsBackground = true, Name = "settings-poll" };
            _pollThread.Start();
            _applyTimer.Start();
        }

        /// <summary>Tray Exit: close without prompts (the host is disposed right after).</summary>
        public void CloseForExit()
        {
            _forExit = true;
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_allowClose && !_forExit && !_stale && e.CloseReason != CloseReason.WindowsShutDown)
            {
                if (_busy > 0)
                {
                    e.Cancel = true;
                    SetStatus("Still applying. Close the window when it has finished.", true);
                    return;
                }
                if (IsDirty())
                {
                    bool roi = BridgeHost.NeedsReload(_applied, _saved);
                    DialogResult ans = MessageBox.Show(this,
                        "You have unsaved changes.\n\n" +
                        "Yes: save and close\n" +
                        "No: go back to the last saved values and close" +
                        (roi ? "\n      (the ROI differs, so the Golf Cams are recreated - Premier will be disconnected)" : "") +
                        "\nCancel: keep editing",
                        Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
                    e.Cancel = true;
                    if (ans == DialogResult.Yes) DoSave(true);
                    else if (ans == DialogResult.No) DoRevert(_saved, false, true);
                    return;
                }
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _closed = true;   // stops the poll thread -> no more preview work
            _applyTimer.Stop();
            _ops.CompleteAdding();
            foreach (CamPage cp in _cams)
            {
                Image img = cp.Preview.Image;
                cp.Preview.Image = null;
                img?.Dispose();
            }
            base.OnFormClosed(e);
        }

        // ----------------------------------------------------------------------------------- threads

        private void OpsLoop()
        {
            foreach (Action op in _ops.GetConsumingEnumerable()) op();
        }

        private void Post(Action a)
        {
            try { _ui?.Post(_ => { if (!IsDisposed) a(); }, null); }
            catch (Exception) { /* UI thread already gone (app exiting) */ }
        }

        /// <summary>Runs <paramref name="work"/> on the ops thread; done/failed run on the UI thread. Never throws into the UI.</summary>
        private void Enqueue<T>(Func<T> work, Action<T> done, Action failed = null)
        {
            if (_closed || _ops.IsAddingCompleted) { failed?.Invoke(); return; }
            try
            {
                _ops.Add(() =>
                {
                    T result = default(T);
                    Exception error = null;
                    try { result = work(); }
                    catch (Exception ex) { error = ex; Log.Error("Settings: " + ex.Message); }
                    Post(() =>
                    {
                        if (error == null) done?.Invoke(result);
                        else { failed?.Invoke(); SetStatus("Error: " + error.Message, true); }
                    });
                });
            }
            catch (InvalidOperationException) { failed?.Invoke(); }
        }

        /// <summary>Enqueue for button actions: disables the buttons until it finishes.</summary>
        private void RunBusy<T>(Func<T> work, Action<T> done)
        {
            _busy++;
            UpdateButtons();
            Enqueue(work, r => { _busy--; done(r); UpdateButtons(); }, () => { _busy--; UpdateButtons(); });
        }

        /// <summary>Called on the ops thread: refuse to write by index into a config that was reloaded from the tray.</summary>
        private void CheckVersion(int version)
        {
            if (_host.ConfigVersion != version)
                throw new InvalidOperationException("golfcam.json was reloaded from the tray menu. Close this window and open it again.");
        }

        private void PollLoop()
        {
            int n = _cams.Length;
            while (!_closed)
            {
                if (n > 0 && Interlocked.CompareExchange(ref _pollPending, 1, 0) == 0)
                {
                    var rs = new CamPoll[n];
                    int sel = _minimized ? -1 : _selectedCam;
                    for (int i = 0; i < n; i++)
                    {
                        try { rs[i] = _host.Poll(i, i == sel ? PreviewW : 0); }
                        catch (Exception) { rs[i] = null; }
                    }
                    try { _ui.Post(_ => { Interlocked.Exchange(ref _pollPending, 0); if (!IsDisposed) ApplyPoll(rs); }, null); }
                    catch (Exception) { return; }
                }
                Thread.Sleep(PollMs);
            }
        }

        // ----------------------------------------------------------------------------------- immediate group

        private void MarkLive(CamPage cp)
        {
            cp.LiveDirty = true;
            UpdateButtons();
        }

        /// <summary>Timer: sends the latest immediate values, at most one request in flight per camera.</summary>
        private void FlushImmediate()
        {
            if (_stale || _busy > 0) return;
            foreach (CamPage cp in _cams)
            {
                if (!cp.LiveDirty || cp.LiveQueued) continue;
                cp.LiveDirty = false;
                cp.LiveQueued = true;
                int i = cp.Index, ver = _version;
                double exp = (double)cp.Exp.Value, gain = (double)cp.Gain.Value;
                Enqueue(() => { CheckVersion(ver); return _host.ApplyExposureGain(i, exp, gain); },
                    live =>
                    {
                        cp.LiveQueued = false;
                        CameraConfig a = _applied.Cameras[i];
                        a.ExposureUs = live != null && !double.IsNaN(live.Exposure) ? Math.Round(live.Exposure, 2) : exp;
                        a.GainDb = live != null && !double.IsNaN(live.Gain) ? Math.Round(live.Gain, 2) : gain;
                        if (live != null && !cp.LiveDirty) SetLiveValues(cp, a.ExposureUs, a.GainDb);   // show what the camera accepted
                        UpdateButtons();
                    },
                    () => cp.LiveQueued = false);
            }

            if (_commonDirty && !_commonQueued)
            {
                _commonDirty = false;
                _commonQueued = true;
                string mode = _mode.SelectedItem as string ?? "OnDemand";
                int idle = (int)_idle.Value, sfps = (int)_standbyFps.Value, ver = _version;
                Enqueue(() => { CheckVersion(ver); _host.ApplyCommon(mode, idle, sfps); return true; },
                    _ =>
                    {
                        _commonQueued = false;
                        _applied.Mode = mode;
                        _applied.IdleReleaseSeconds = idle;
                        _applied.StandbyFps = sfps;
                        UpdateButtons();
                    },
                    () => _commonQueued = false);
            }
        }

        // ----------------------------------------------------------------------------------- restart / reload groups

        private void ApplyRestart(CamPage cp)
        {
            int i = cp.Index, ver = _version;
            double fps = (double)cp.Fps.Value;
            string fmt = cp.PixFmt.SelectedItem as string ?? "BayerRG8";
            bool rx = cp.RevX.Checked, ry = cp.RevY.Checked;
            RunBusy(() => { CheckVersion(ver); return _host.ApplyCameraRestart(i, fps, fmt, rx, ry); },
                cfg => { _applied = cfg; SetStatus($"{cp.Name}: camera restarted (Golf Cam kept - Premier stays connected).", false); });
        }

        private void ApplyAlgorithm()
        {
            string algo = _algo.SelectedItem as string;
            int ver = _version;
            RunBusy(() => { CheckVersion(ver); return _host.ApplyColorAlgorithm(algo); },
                cfg => { _applied = cfg; SetStatus("ColorAlgorithm applied - both cameras restarted.", false); });
        }

        private void ApplyRoi()
        {
            string list = string.Join("\n", _cams.Where(RoiPending).Select(cp =>
                $"  {cp.Name}: {_applied.Cameras[cp.Index].Width}x{_applied.Cameras[cp.Index].Height} -> {(int)cp.W.Value & ~3}x{(int)cp.H.Value & ~3}"));
            if (MessageBox.Show(this,
                    "Changing the ROI recreates both virtual cameras (Golf Cam 1 and 2).\n" +
                    "Premier will be disconnected if it is connected (you may have to select the cameras again in Premier).\n\n" +
                    list + "\n\nContinue?",
                    Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;

            int[] w = _cams.Select(cp => (int)cp.W.Value & ~3).ToArray();
            int[] h = _cams.Select(cp => (int)cp.H.Value & ~3).ToArray();
            int ver = _version;
            RunBusy(() =>
                {
                    CheckVersion(ver);
                    BridgeConfig cfg = _host.ApplyRoi(w, h, out int v);
                    return Tuple.Create(cfg, v);
                },
                r =>
                {
                    _applied = r.Item1;
                    _version = r.Item2;
                    _syncing = true;
                    try { foreach (CamPage cp in _cams) { SetNudExpand(cp.W, _applied.Cameras[cp.Index].Width); SetNudExpand(cp.H, _applied.Cameras[cp.Index].Height); } }
                    finally { _syncing = false; }
                    SetStatus("ROI applied - Golf Cams recreated.", false);
                });
        }

        // ----------------------------------------------------------------------------------- save / revert

        private void DoSave(bool closeAfter)
        {
            BridgeConfig form = FormValues();
            string pending = PendingText();
            if (pending != null && MessageBox.Show(this,
                    "Some changes have not been applied yet:\n" + pending +
                    "\n\nThey are only written to the file and take effect when the app restarts or on Reload settings in the tray menu. Save?",
                    Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

            foreach (CamPage cp in _cams) cp.LiveDirty = false;   // flushed below, in order
            _commonDirty = false;
            int ver = _version;
            RunBusy(() =>
                {
                    CheckVersion(ver);
                    // Immediate values first, so the file gets what the cameras actually accepted.
                    for (int i = 0; i < form.Cameras.Length; i++)
                        _host.ApplyExposureGain(i, form.Cameras[i].ExposureUs, form.Cameras[i].GainDb);
                    _host.ApplyCommon(form.Mode, form.IdleReleaseSeconds, form.StandbyFps);
                    BridgeConfig applied = _host.CloneConfig(out _);
                    // ...plus the restart/reload values as shown in the window (applied or not).
                    BridgeConfig file = applied.Clone();
                    file.ColorAlgorithm = form.ColorAlgorithm;
                    for (int i = 0; i < file.Cameras.Length; i++)
                    {
                        CameraConfig f = file.Cameras[i], s = form.Cameras[i];
                        f.FrameRate = s.FrameRate; f.PixelFormat = s.PixelFormat;
                        f.ReverseX = s.ReverseX; f.ReverseY = s.ReverseY;
                        f.Width = s.Width; f.Height = s.Height;
                    }
                    _host.SaveConfig(file);
                    return Tuple.Create(file, applied);
                },
                r =>
                {
                    _saved = r.Item1;
                    _savedJson = _saved.ToJson();
                    _applied = r.Item2;
                    foreach (CamPage cp in _cams)
                        SetLiveValues(cp, _applied.Cameras[cp.Index].ExposureUs, _applied.Cameras[cp.Index].GainDb);
                    SetStatus("Saved to golfcam.json.", false);
                    if (closeAfter) { _allowClose = true; Close(); }
                });
        }

        private void DoRevert(BridgeConfig target, bool confirmReload, bool closeAfter)
        {
            if (confirmReload && BridgeHost.NeedsReload(_applied, target) && MessageBox.Show(this,
                    "The ROI differs from when the window was opened, so the Golf Cams are recreated.\nPremier will be disconnected if it is connected. Continue?",
                    Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;

            foreach (CamPage cp in _cams) cp.LiveDirty = false;   // drop edits not sent yet; queued ones run before the revert
            _commonDirty = false;
            int ver = _version;
            BridgeConfig t = target.Clone();
            RunBusy(() =>
                {
                    CheckVersion(ver);
                    BridgeConfig cfg = _host.RevertTo(t, out int v);
                    return Tuple.Create(cfg, v);
                },
                r =>
                {
                    _applied = r.Item1;
                    _version = r.Item2;
                    SetControls(_applied);
                    SetStatus("Reverted (including values already written to the cameras).", false);
                    if (closeAfter) { _allowClose = true; Close(); }
                });
        }

        // ----------------------------------------------------------------------------------- state

        /// <summary>The config as shown in the window.</summary>
        private BridgeConfig FormValues()
        {
            BridgeConfig c = _applied.Clone();
            c.Mode = _mode.SelectedItem as string ?? c.Mode;
            c.IdleReleaseSeconds = (int)_idle.Value;
            c.StandbyFps = (int)_standbyFps.Value;
            c.ColorAlgorithm = _algo.SelectedItem as string ?? c.ColorAlgorithm;
            foreach (CamPage cp in _cams)
            {
                CameraConfig cc = c.Cameras[cp.Index];
                cc.ExposureUs = (double)cp.Exp.Value;
                cc.GainDb = (double)cp.Gain.Value;
                cc.FrameRate = (double)cp.Fps.Value;
                cc.PixelFormat = cp.PixFmt.SelectedItem as string ?? cc.PixelFormat;
                cc.ReverseX = cp.RevX.Checked;
                cc.ReverseY = cp.RevY.Checked;
                cc.Width = (int)cp.W.Value & ~3;
                cc.Height = (int)cp.H.Value & ~3;
            }
            return c;
        }

        private bool IsDirty() => FormValues().ToJson() != _savedJson;

        private bool RestartPending(CamPage cp)
        {
            CameraConfig a = _applied.Cameras[cp.Index];
            return (double)cp.Fps.Value != a.FrameRate
                   || !string.Equals(cp.PixFmt.SelectedItem as string, a.PixelFormat, StringComparison.OrdinalIgnoreCase)
                   || cp.RevX.Checked != a.ReverseX || cp.RevY.Checked != a.ReverseY;
        }

        private bool RoiPending(CamPage cp)
        {
            CameraConfig a = _applied.Cameras[cp.Index];
            return ((int)cp.W.Value & ~3) != a.Width || ((int)cp.H.Value & ~3) != a.Height;
        }

        private bool AlgoPending() => !string.Equals(_algo.SelectedItem as string, _applied.ColorAlgorithm, StringComparison.OrdinalIgnoreCase);

        private string PendingText()
        {
            var items = _cams.Where(RestartPending).Select(cp => $"  {cp.Name}: FrameRate/PixelFormat/Reverse")
                .Concat(_cams.Where(RoiPending).Select(cp => $"  {cp.Name}: ROI (Width/Height)"))
                .Concat(AlgoPending() ? new[] { "  ColorAlgorithm" } : new string[0])
                .ToList();
            return items.Count == 0 ? null : string.Join("\n", items);
        }

        private void UpdateButtons()
        {
            if (_save == null || _algoBtn == null) return;   // still constructing
            bool idle = !_stale && _busy == 0;
            bool anyRoi = _cams.Any(RoiPending);
            foreach (CamPage cp in _cams)
            {
                bool rp = RestartPending(cp);
                cp.RestartBtn.Enabled = idle && rp;
                cp.RestartGroup.Text = "Camera restart — this camera pauses ~1 s, Premier stays connected" + (rp ? "   ● pending" : "");
                cp.RoiBtn.Enabled = idle && anyRoi;
                cp.RoiGroup.Text = "Reload — virtual camera size (ROI)" + (RoiPending(cp) ? "   ● pending" : "");
            }
            _algoBtn.Enabled = idle && AlgoPending();
            bool dirty = IsDirty();
            _save.Enabled = idle && dirty;
            _revert.Enabled = idle && FormValues().ToJson() != _originalJson;
            _tabs.Enabled = !_stale;
            Text = "GolfCamBridge Settings" + (dirty ? " *" : "") + (_busy > 0 ? " (applying...)" : "");
        }

        private void SetStatus(string text, bool error)
        {
            _status.Text = text;
            _status.ForeColor = error ? Color.Firebrick : SystemColors.ControlText;
        }

        // ----------------------------------------------------------------------------------- poll results

        private void ApplyPoll(CamPoll[] rs)
        {
            for (int i = 0; i < rs.Length && i < _cams.Length; i++)
            {
                CamPoll r = rs[i];
                CamPage cp = _cams[i];
                if (r == null) continue;
                // A version change while none of our own operations is running = someone pressed Reload in the tray.
                if (r.Version != _version && _busy == 0 && !_stale)
                {
                    _stale = true;
                    SetStatus("golfcam.json was reloaded from the tray menu. Close this window and open it again.", true);
                    UpdateButtons();
                }

                string st = r.Status == null ? "this Golf Cam could not be created (see the log)" : TrayContext.Describe(r.Status);
                if (r.Live == null) st += "   —   camera not open: changes are kept and applied when the camera starts";
                if (r.Error != null) st += "   (" + r.Error + ")";
                cp.Status.Text = "Status: " + st;
                cp.Status.ForeColor = r.Live == null ? Color.DarkOrange : Color.ForestGreen;

                UpdateRanges(cp, r.Live);
                ShowFps(cp, r);
                if (i == _selectedCam) ShowPreview(cp, r);
            }
        }

        private void UpdateRanges(CamPage cp, LiveInfo live)
        {
            double emin = DefExpMin, emax = DefExpMax, gmin = DefGainMin, gmax = DefGainMax;
            if (live != null && live.ExposureMin > 0 && live.ExposureMax > live.ExposureMin) { emin = live.ExposureMin; emax = live.ExposureMax; }
            else { emin = Math.Min(emin, (double)cp.Exp.Value); emax = Math.Max(emax, (double)cp.Exp.Value); }   // never clip a configured value without a camera
            if (live != null && !double.IsNaN(live.GainMin) && live.GainMax > live.GainMin) { gmin = live.GainMin; gmax = live.GainMax; }
            else { gmin = Math.Min(gmin, (double)cp.Gain.Value); gmax = Math.Max(gmax, (double)cp.Gain.Value); }
            string src = live == null ? "  (default range — camera not open)" : "  (from camera)";
            cp.ExpRange.Text = $"Range {emin:0.#} – {emax:#,0.#} µs{src}";
            cp.GainRange.Text = $"Range {gmin:0.##} – {gmax:0.##} dB{src}";

            if (emin != cp.ExpMin || emax != cp.ExpMax || gmin != cp.GainMin || gmax != cp.GainMax)
            {
                decimal e0 = cp.Exp.Value, g0 = cp.Gain.Value;
                cp.ExpMin = emin; cp.ExpMax = emax; cp.GainMin = gmin; cp.GainMax = gmax;
                _syncing = true;
                try
                {
                    SetRange(cp.Exp, emin, emax);
                    SetRange(cp.Gain, gmin, gmax);
                    SyncBars(cp);
                }
                finally { _syncing = false; }
                // Out-of-range value got clamped by the new range: send it so config matches the camera.
                if (live != null && (cp.Exp.Value != e0 || cp.Gain.Value != g0)) MarkLive(cp);
            }

            if (live != null && live.WidthMax > 0 && live.HeightMax > 0)
            {
                _syncing = true;
                try
                {
                    cp.W.Maximum = Math.Max(live.WidthMax, cp.W.Value);
                    cp.H.Maximum = Math.Max(live.HeightMax, cp.H.Value);
                }
                finally { _syncing = false; }
            }
        }

        private void ShowFps(CamPage cp, CamPoll r)
        {
            CameraConfig a = _applied.Cameras[cp.Index];
            if (r.Live == null || double.IsNaN(r.Live.FpsMax))
            {
                cp.FpsInfo.Text = $"Max FPS: — (camera not open)   ·   FrameRate set {F(a.FrameRate)}";
                cp.FpsWarn.Text = "";
                return;
            }
            cp.FpsInfo.Text = $"Max FPS {F(r.Live.FpsMax)}  ·  camera actual {F(r.Live.FpsResulting)}  ·  output {F(r.Status?.Fps ?? double.NaN)}  ·  set {F(a.FrameRate)}";
            bool over = a.FrameRate > r.Live.FpsMax + 0.05;
            cp.FpsWarn.Text = over ? $"⚠ FrameRate {F(a.FrameRate)} is above the maximum {F(r.Live.FpsMax)} at this exposure → shorten the exposure or lower FrameRate" : "";
        }

        private void ShowPreview(CamPage cp, CamPoll r)
        {
            Image old = cp.Preview.Image;
            if (r.Pixels != null)
            {
                cp.Preview.Image = ToBitmap(r.Pixels, r.PreviewW, r.PreviewH);
                cp.Hist.Set(r.Hist);
                cp.HistInfo.Text = HistText(r.Hist);
            }
            else
            {
                cp.Preview.Image = null;
                cp.Hist.Set(null);
                cp.HistInfo.Text = "No image — shown when the camera is running";
            }
            old?.Dispose();
        }

        private static string HistText(int[] h)
        {
            long total = 0, sum = 0, dark = 0, bright = 0;
            for (int i = 0; i < 256; i++)
            {
                total += h[i];
                sum += (long)i * h[i];
                if (i <= 4) dark += h[i];
                if (i >= 251) bright += h[i];
            }
            if (total == 0) return "";
            return $"Mean {sum / (double)total:0} / 255   ·   black (≤4) {100.0 * dark / total:0.0}%   ·   clipped (≥251) {100.0 * bright / total:0.0}%";
        }

        private static Bitmap ToBitmap(byte[] bgr, int w, int h)
        {
            var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            BitmapData d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            try
            {
                for (int y = 0; y < h; y++) Marshal.Copy(bgr, y * w * 3, d.Scan0 + y * d.Stride, w * 3);
            }
            finally { bmp.UnlockBits(d); }
            return bmp;
        }

        private static string F(double v) => double.IsNaN(v) ? "—" : v.ToString("0.#");

        // ----------------------------------------------------------------------------------- control values

        private void SetControls(BridgeConfig c)
        {
            _syncing = true;
            try
            {
                SelectCombo(_mode, c.AlwaysOn ? "AlwaysOn" : "OnDemand");
                SetNudExpand(_idle, c.IdleReleaseSeconds);
                SetNudExpand(_standbyFps, c.StandbyFps);
                SelectCombo(_algo, c.ColorAlgorithm);
                foreach (CamPage cp in _cams)
                {
                    CameraConfig cc = c.Cameras[cp.Index];
                    SetNudExpand(cp.Exp, cc.ExposureUs);
                    SetNudExpand(cp.Gain, cc.GainDb);
                    SyncBars(cp);
                    SetNudExpand(cp.Fps, cc.FrameRate);
                    SelectCombo(cp.PixFmt, cc.PixelFormat);
                    cp.RevX.Checked = cc.ReverseX;
                    cp.RevY.Checked = cc.ReverseY;
                    SetNudExpand(cp.W, cc.Width);
                    SetNudExpand(cp.H, cc.Height);
                }
            }
            finally { _syncing = false; }
            UpdateButtons();
        }

        private void SetLiveValues(CamPage cp, double exp, double gain)
        {
            _syncing = true;
            try
            {
                SetNudExpand(cp.Exp, exp);
                SetNudExpand(cp.Gain, gain);
                SyncBars(cp);
            }
            finally { _syncing = false; }
        }

        private static void SelectCombo(ComboBox cb, string value)
        {
            int idx = -1;
            for (int i = 0; i < cb.Items.Count; i++)
                if (string.Equals(cb.Items[i] as string, value, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
            if (idx < 0 && !string.IsNullOrEmpty(value)) idx = cb.Items.Add(value);
            cb.SelectedIndex = idx;
        }

        /// <summary>Sets a value, widening the range if needed (never silently changes a configured value).</summary>
        private static void SetNudExpand(NumericUpDown nud, double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return;
            decimal d = (decimal)v;
            if (d > nud.Maximum) nud.Maximum = d;
            if (d < nud.Minimum) nud.Minimum = d;
            nud.Value = d;
        }

        private static void SetRange(NumericUpDown nud, double min, double max)
        {
            nud.Maximum = (decimal)max;   // NumericUpDown clamps Value (and keeps Minimum <= Maximum) itself
            nud.Minimum = (decimal)min;
        }

        private static void SetNudClamped(NumericUpDown nud, double v)
        {
            if (double.IsNaN(v)) return;
            nud.Value = Math.Max(nud.Minimum, Math.Min(nud.Maximum, (decimal)v));
        }

        // Exposure slider is logarithmic so short exposures stay adjustable with a range of up to seconds.
        private static int ExpToBar(double v, double min, double max)
        {
            if (!(max > min) || min <= 0) return 0;
            double t = Math.Log(Math.Max(v, min) / min) / Math.Log(max / min);
            return (int)Math.Round(Math.Max(0, Math.Min(1, t)) * 1000);
        }

        private static double BarToExp(int t, double min, double max) =>
            !(max > min) || min <= 0 ? min : min * Math.Pow(max / min, t / 1000.0);

        private static void SyncBars(CamPage cp)
        {
            cp.ExpBar.Value = ExpToBar((double)cp.Exp.Value, cp.ExpMin, cp.ExpMax);
            cp.GainBar.Maximum = Math.Max(1, (int)Math.Round((cp.GainMax - cp.GainMin) * 10));
            int g = (int)Math.Round(((double)cp.Gain.Value - cp.GainMin) * 10);
            cp.GainBar.Value = Math.Max(0, Math.Min(cp.GainBar.Maximum, g));
        }

        /// <summary>`GolfCamBridge.exe --selftest`: pure-logic checks (no camera, no virtual cam). Exit code 0 = OK.</summary>
        public static int SelfCheck()
        {
            int fails = 0;
            void Check(bool ok, string what) { if (!ok) { fails++; Log.Error("selftest FAILED: " + what); } }

            double min = 9.3, max = 30000000;
            Check(Math.Abs(BarToExp(0, min, max) - min) < 1e-9 && Math.Abs(BarToExp(1000, min, max) - max) < 1e-3, "exposure slider ends");
            for (int t = 0; t <= 1000; t++)
                if (ExpToBar(BarToExp(t, min, max), min, max) != t) { Check(false, "exposure slider round trip at " + t); break; }
            Check(ExpToBar(1, min, max) == 0 && ExpToBar(1e9, min, max) == 1000, "exposure slider clamps");

            var cfg = new BridgeConfig { Cameras = new[] { new CameraConfig { Name = "FO" }, new CameraConfig { Name = "DTL", VirtualCam = 2 } } };
            BridgeConfig copy = cfg.Clone();
            Check(copy.ToJson() == cfg.ToJson() && !ReferenceEquals(copy.Cameras[0], cfg.Cameras[0]), "config clone");
            Check(!BridgeHost.NeedsReload(cfg, copy), "same ROI needs no reload");
            copy.Cameras[1].Width = 640;
            Check(BridgeHost.NeedsReload(cfg, copy), "ROI change needs reload");

            double v = Math.Round(1999.8712, 2);   // camera readback as stored by BridgeHost.ApplyExposureGain
            Check((double)(decimal)v == v, "readback survives the NumericUpDown decimal round trip");

            Log.Info(fails == 0 ? "selftest OK" : $"selftest: {fails} failure(s)");
            return fails == 0 ? 0 : 1;
        }

        // ----------------------------------------------------------------------------------- layout

        private CamPage BuildCamPage(TabPage page, int index, string name)
        {
            var cp = new CamPage { Index = index, Name = name };
            cp.Status = AddLabel(page, "Status: ...", 10, 8, 740, 20);
            cp.Status.Font = new Font(Font, FontStyle.Bold);

            // --- immediate
            var live = new GroupBox { Text = "Immediate — applied while streaming (no restart)", Location = new Point(10, 32), Size = new Size(400, 218) };
            page.Controls.Add(live);
            AddLabel(live, "Exposure (µs)", 10, 22, 200);
            cp.ExpBar = new TrackBar { Location = new Point(6, 40), Size = new Size(260, 30), Minimum = 0, Maximum = 1000, TickStyle = TickStyle.None, SmallChange = 5, LargeChange = 50 };
            live.Controls.Add(cp.ExpBar);
            cp.Exp = AddNud(live, 274, 42, 112, (decimal)DefExpMin, (decimal)DefExpMax, 0, 10);
            cp.ExpRange = AddLabel(live, "", 10, 72, 380);
            cp.ExpRange.ForeColor = SystemColors.GrayText;

            AddLabel(live, "Gain (dB)", 10, 96, 200);
            cp.GainBar = new TrackBar { Location = new Point(6, 114), Size = new Size(260, 30), Minimum = 0, Maximum = 480, TickStyle = TickStyle.None, SmallChange = 1, LargeChange = 10 };
            live.Controls.Add(cp.GainBar);
            cp.Gain = AddNud(live, 274, 116, 112, (decimal)DefGainMin, (decimal)DefGainMax, 1, 0.5m);
            cp.GainRange = AddLabel(live, "", 10, 146, 380);
            cp.GainRange.ForeColor = SystemColors.GrayText;

            cp.FpsInfo = AddLabel(live, "", 10, 170, 384);
            cp.FpsWarn = AddLabel(live, "", 10, 186, 384, 28);
            cp.FpsWarn.ForeColor = Color.Firebrick;
            cp.FpsWarn.Font = new Font(Font, FontStyle.Bold);

            cp.ExpBar.Scroll += (s, e) =>
            {
                if (_syncing) return;
                _syncing = true;
                try { SetNudClamped(cp.Exp, BarToExp(cp.ExpBar.Value, cp.ExpMin, cp.ExpMax)); }
                finally { _syncing = false; }
                MarkLive(cp);
            };
            cp.Exp.ValueChanged += (s, e) =>
            {
                if (_syncing) return;
                _syncing = true;
                try { SyncBars(cp); }
                finally { _syncing = false; }
                MarkLive(cp);
            };
            cp.GainBar.Scroll += (s, e) =>
            {
                if (_syncing) return;
                _syncing = true;
                try { SetNudClamped(cp.Gain, cp.GainMin + cp.GainBar.Value / 10.0); }
                finally { _syncing = false; }
                MarkLive(cp);
            };
            cp.Gain.ValueChanged += (s, e) =>
            {
                if (_syncing) return;
                _syncing = true;
                try { SyncBars(cp); }
                finally { _syncing = false; }
                MarkLive(cp);
            };

            // --- restart
            cp.RestartGroup = new GroupBox { Location = new Point(10, 258), Size = new Size(400, 120), BackColor = RestartTint };
            page.Controls.Add(cp.RestartGroup);
            AddLabel(cp.RestartGroup, "FrameRate", 10, 28, 80);
            cp.Fps = AddNud(cp.RestartGroup, 92, 25, 80, 1, 1000, 1, 1);
            AddLabel(cp.RestartGroup, "PixelFormat", 190, 28, 84);
            cp.PixFmt = new ComboBox { Location = new Point(276, 24), Size = new Size(112, 24), DropDownStyle = ComboBoxStyle.DropDownList };
            cp.PixFmt.Items.AddRange(PixelFormats);
            cp.RestartGroup.Controls.Add(cp.PixFmt);
            cp.RevX = new CheckBox { Text = "ReverseX", Location = new Point(12, 56), Size = new Size(100, 22) };
            cp.RevY = new CheckBox { Text = "ReverseY", Location = new Point(118, 56), Size = new Size(100, 22) };
            cp.RestartGroup.Controls.Add(cp.RevX);
            cp.RestartGroup.Controls.Add(cp.RevY);
            cp.RestartBtn = AddButton(cp.RestartGroup, "Apply (restart camera)", 10, 84, 378, 28, (s, e) => ApplyRestart(cp));

            // --- reload
            cp.RoiGroup = new GroupBox { Location = new Point(10, 386), Size = new Size(400, 98), BackColor = ReloadTint };
            page.Controls.Add(cp.RoiGroup);
            AddLabel(cp.RoiGroup, "Width", 10, 28, 50);
            cp.W = AddNud(cp.RoiGroup, 62, 25, 80, 64, DefSizeMax, 0, 4);
            AddLabel(cp.RoiGroup, "Height", 160, 28, 50);
            cp.H = AddNud(cp.RoiGroup, 214, 25, 80, 64, DefSizeMax, 0, 4);
            AddLabel(cp.RoiGroup, "(multiple of 4)", 302, 28, 90).ForeColor = SystemColors.GrayText;
            cp.RoiBtn = AddButton(cp.RoiGroup, "Apply ROI (Reload) — disconnects Premier", 10, 58, 378, 30, (s, e) => ApplyRoi());
            cp.RoiBtn.ForeColor = Color.Firebrick;

            foreach (Control c in new Control[] { cp.Fps, cp.W, cp.H })
                ((NumericUpDown)c).ValueChanged += (s, e) => { if (!_syncing) UpdateButtons(); };
            cp.PixFmt.SelectedIndexChanged += (s, e) => { if (!_syncing) UpdateButtons(); };
            cp.RevX.CheckedChanged += (s, e) => { if (!_syncing) UpdateButtons(); };
            cp.RevY.CheckedChanged += (s, e) => { if (!_syncing) UpdateButtons(); };

            // --- preview + histogram
            AddLabel(page, "Preview (output frames, downscaled, max 8 fps)", 424, 38, 330);
            cp.Preview = new PictureBox
            {
                Location = new Point(424, 58), Size = new Size(PreviewW + 2, PreviewH + 2),
                BorderStyle = BorderStyle.FixedSingle, BackColor = Color.FromArgb(24, 26, 30), SizeMode = PictureBoxSizeMode.Zoom,
            };
            page.Controls.Add(cp.Preview);
            AddLabel(page, "Brightness histogram (red = 0 / 255)", 424, 250, 330);
            cp.Hist = new HistPanel { Location = new Point(424, 270), Size = new Size(PreviewW + 2, 120), BorderStyle = BorderStyle.FixedSingle };
            page.Controls.Add(cp.Hist);
            cp.HistInfo = AddLabel(page, "", 424, 396, 330, 36);
            return cp;
        }

        private void BuildCommonPage(TabPage page)
        {
            var imm = new GroupBox { Text = "Immediate", Location = new Point(10, 12), Size = new Size(744, 124) };
            page.Controls.Add(imm);
            AddLabel(imm, "Mode", 12, 28, 130);
            _mode = new ComboBox { Location = new Point(150, 24), Size = new Size(140, 24), DropDownStyle = ComboBoxStyle.DropDownList };
            _mode.Items.AddRange(new object[] { "OnDemand", "AlwaysOn" });
            imm.Controls.Add(_mode);
            AddLabel(imm, "OnDemand: camera runs only while an app (Premier) uses it   /   AlwaysOn: always", 300, 28, 436).ForeColor = SystemColors.GrayText;
            AddLabel(imm, "IdleReleaseSeconds", 12, 60, 136);
            _idle = AddNud(imm, 150, 57, 90, 1, 86400, 0, 5);
            AddLabel(imm, "OnDemand: release the camera this many seconds after the last app disconnects", 300, 60, 436).ForeColor = SystemColors.GrayText;
            AddLabel(imm, "StandbyFps", 12, 92, 136);
            _standbyFps = AddNud(imm, 150, 89, 90, 1, 30, 0, 1);
            AddLabel(imm, "Frame rate of the standby image while a camera is off", 300, 92, 436).ForeColor = SystemColors.GrayText;
            _mode.SelectedIndexChanged += (s, e) => { if (!_syncing) { _commonDirty = true; UpdateButtons(); } };
            _idle.ValueChanged += (s, e) => { if (!_syncing) { _commonDirty = true; UpdateButtons(); } };
            _standbyFps.ValueChanged += (s, e) => { if (!_syncing) { _commonDirty = true; UpdateButtons(); } };

            var rst = new GroupBox { Text = "Camera restart — both cameras pause ~1 s, Premier stays connected", Location = new Point(10, 146), Size = new Size(744, 92), BackColor = RestartTint };
            page.Controls.Add(rst);
            AddLabel(rst, "ColorAlgorithm", 12, 30, 136);
            _algo = new ComboBox { Location = new Point(150, 26), Size = new Size(240, 24), DropDownStyle = ComboBoxStyle.DropDownList };
            _algo.Items.AddRange(Enum.GetNames(typeof(SpinnakerNET.ColorProcessingAlgorithm)));
            rst.Controls.Add(_algo);
            _algoBtn = AddButton(rst, "Apply (restart both cameras)", 400, 24, 330, 28, (s, e) => ApplyAlgorithm());
            AddLabel(rst, "HQ_LINEAR: best quality   /   NEAREST_NEIGHBOR: lowest CPU load (use it if the output FPS drops)", 12, 60, 720).ForeColor = SystemColors.GrayText;
            _algo.SelectedIndexChanged += (s, e) => { if (!_syncing) UpdateButtons(); };

            AddLabel(page,
                "• Immediate: applied as you edit. If a camera is off (OnDemand idle), the value is kept and used when it starts.\n" +
                "• Camera restart: press Apply. The virtual camera is kept, so Premier stays connected.\n" +
                "• Reload (ROI on the camera tabs): recreates the virtual cameras, so Premier is disconnected.\n" +
                "• Nothing is written to golfcam.json until you press Save. Revert restores the values from when the window was opened, including values already sent to the cameras.",
                12, 252, 744, 96);
        }

        private static Label AddLabel(Control parent, string text, int x, int y, int w, int h = 18)
        {
            var l = new Label { Text = text, Location = new Point(x, y), Size = new Size(w, h) };
            parent.Controls.Add(l);
            return l;
        }

        private static Button AddButton(Control parent, string text, int x, int y, int w, int h, EventHandler click)
        {
            var b = new Button { Text = text, Location = new Point(x, y), Size = new Size(w, h), UseVisualStyleBackColor = true };
            b.Click += click;
            parent.Controls.Add(b);
            return b;
        }

        private static NumericUpDown AddNud(Control parent, int x, int y, int w, decimal min, decimal max, int decimals, decimal inc)
        {
            var n = new NumericUpDown { Location = new Point(x, y), Size = new Size(w, 24), DecimalPlaces = decimals, Increment = inc, ThousandsSeparator = true };
            n.Maximum = max;
            n.Minimum = min;
            parent.Controls.Add(n);
            return n;
        }

        private sealed class CamPage
        {
            public int Index;
            public string Name;
            public Label Status, ExpRange, GainRange, FpsInfo, FpsWarn, HistInfo;
            public TrackBar ExpBar, GainBar;
            public NumericUpDown Exp, Gain, Fps, W, H;
            public ComboBox PixFmt;
            public CheckBox RevX, RevY;
            public GroupBox RestartGroup, RoiGroup;
            public Button RestartBtn, RoiBtn;
            public PictureBox Preview;
            public HistPanel Hist;
            public double ExpMin = DefExpMin, ExpMax = DefExpMax, GainMin = DefGainMin, GainMax = DefGainMax;
            public bool LiveDirty, LiveQueued;
        }

        private sealed class HistPanel : Panel
        {
            private int[] _h;

            public HistPanel()
            {
                DoubleBuffered = true;
                ResizeRedraw = true;
                BackColor = Color.FromArgb(24, 26, 30);
            }

            public void Set(int[] h)
            {
                _h = h;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                if (_h == null) return;
                int max = 1;
                for (int i = 1; i < 255; i++) max = Math.Max(max, _h[i]);   // ends excluded so a clipped spike does not flatten the rest
                float bw = ClientSize.Width / 256f;
                int height = ClientSize.Height;
                using (var bar = new SolidBrush(Color.FromArgb(200, 200, 200)))
                using (var clip = new SolidBrush(Color.FromArgb(230, 80, 60)))
                {
                    for (int i = 0; i < 256; i++)
                    {
                        float bh = Math.Min(1f, _h[i] / (float)max) * (height - 2);
                        if (bh <= 0) continue;
                        e.Graphics.FillRectangle(i == 0 || i == 255 ? clip : bar, i * bw, height - bh, Math.Max(1f, bw), bh);
                    }
                }
            }
        }
    }
}
