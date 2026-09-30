using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

namespace GolfCamBridge
{
    /// <summary>System-tray front end. The bridge itself runs on background threads inside BridgeHost.</summary>
    internal sealed class TrayContext : ApplicationContext
    {
        private readonly BridgeHost _host;
        private readonly string _cfgPath;
        private readonly NotifyIcon _icon;
        private readonly ContextMenuStrip _menu;
        private readonly ToolStripMenuItem _alwaysOnItem, _autostartItem;
        private readonly List<ToolStripMenuItem> _statusItems = new List<ToolStripMenuItem>();
        private readonly ToolStripSeparator _statusEnd = new ToolStripSeparator();
        private readonly Timer _timer;
        private readonly Icon _iconIdle, _iconLive, _iconError;
        private bool _exiting;
        private readonly System.Threading.SynchronizationContext _ui;
        private SettingsForm _settings;   // at most one settings window

        public TrayContext(BridgeHost host, string cfgPath)
        {
            _host = host;
            _cfgPath = cfgPath;

            _iconIdle = MakeIcon(Color.FromArgb(150, 150, 150));
            _iconLive = MakeIcon(Color.FromArgb(40, 190, 90));
            _iconError = MakeIcon(Color.FromArgb(220, 60, 50));

            _menu = new ContextMenuStrip();
            _ui = System.Threading.SynchronizationContext.Current;   // installed by the first WinForms control
            _menu.Items.Add(new ToolStripMenuItem("GolfCamBridge") { Enabled = false, Font = new Font(SystemFonts.MenuFont, FontStyle.Bold) });
            _menu.Items.Add(_statusEnd);

            _alwaysOnItem = new ToolStripMenuItem("Keep cameras on (AlwaysOn)", null, (s, e) =>
            {
                _host.SetAlwaysOn(!_alwaysOnItem.Checked);
                _alwaysOnItem.Checked = _host.Config.AlwaysOn;
            });
            _autostartItem = new ToolStripMenuItem("Start with Windows", null, (s, e) =>
            {
                Autostart.Enabled = !Autostart.Enabled;
                _autostartItem.Checked = Autostart.Enabled;
            });
            _menu.Items.Add(_alwaysOnItem);
            _menu.Items.Add(_autostartItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("Settings...", null, (s, e) => OpenSettings());
            _menu.Items.Add("Edit settings (golfcam.json)", null, (s, e) => Open("notepad.exe", $"\"{_cfgPath}\""));
            _menu.Items.Add("Reload settings", null, (s, e) => ReloadAsync());
            _menu.Items.Add("Show connected cameras", null, (s, e) =>
                MessageBox.Show(_host.DescribeConnectedCameras(), "GolfCamBridge - cameras", MessageBoxButtons.OK, MessageBoxIcon.Information));
            _menu.Items.Add("Open log", null, (s, e) => Open("notepad.exe", $"\"{Log.FilePath}\""));
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("Exit", null, (s, e) => ExitApp());
            _menu.Opening += (s, e) =>
            {
                _alwaysOnItem.Checked = _host.Config?.AlwaysOn ?? false;
                _autostartItem.Checked = Autostart.Enabled;
                RefreshStatus();
            };

            _icon = new NotifyIcon
            {
                Icon = _iconIdle,
                Text = "GolfCamBridge",
                ContextMenuStrip = _menu,
                Visible = true,
            };
            _icon.DoubleClick += (s, e) => Open("notepad.exe", $"\"{Log.FilePath}\"");

            _timer = new Timer { Interval = 1000 };
            _timer.Tick += (s, e) => RefreshStatus();
            _timer.Start();

            SystemEvents.SessionEnding += (s, e) => ExitApp();
            Application.ApplicationExit += (s, e) => ExitApp();

            if (!string.IsNullOrEmpty(_host.LastError))
                _icon.ShowBalloonTip(5000, "GolfCamBridge", _host.LastError, ToolTipIcon.Warning);
            else
                _icon.ShowBalloonTip(3000, "GolfCamBridge", "Golf Cams are ready. Right-click the tray icon for options.", ToolTipIcon.Info);
        }

        private void RefreshStatus()
        {
            if (_exiting) return;
            List<ChannelStatus> st = _host.Snapshot();

            // rebuild the status lines (count may change after a reload)
            while (_statusItems.Count < st.Count)
            {
                var item = new ToolStripMenuItem { Enabled = false };
                _menu.Items.Insert(_menu.Items.IndexOf(_statusEnd), item);
                _statusItems.Add(item);
            }
            while (_statusItems.Count > st.Count)
            {
                _menu.Items.Remove(_statusItems[_statusItems.Count - 1]);
                _statusItems.RemoveAt(_statusItems.Count - 1);
            }
            for (int i = 0; i < st.Count; i++)
                _statusItems[i].Text = $"Golf Cam {st[i].VirtualCam} ({st[i].Name}): {Describe(st[i])}";

            bool anyError = st.Any(c => c.State == ChannelState.Faulted || c.State == ChannelState.NoCamera) || !string.IsNullOrEmpty(_host.LastError);
            bool anyLive = st.Any(c => c.State == ChannelState.Streaming);
            _icon.Icon = anyError ? _iconError : anyLive ? _iconLive : _iconIdle;

            string tip = "GolfCamBridge - " + (st.Count == 0 ? "no cameras" :
                string.Join(" | ", st.Select(c => $"{c.VirtualCam}:{Short(c)}")));
            _icon.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;   // NotifyIcon limit
        }

        internal static string Describe(ChannelStatus c)
        {
            switch (c.State)
            {
                case ChannelState.Streaming: return $"streaming {c.Fps:0} fps" + (c.AppConnected ? " (in use)" : "");
                case ChannelState.Standby: return c.AppConnected ? "starting..." : "standby";
                case ChannelState.NoCamera: return "camera not found";
                default: return "error - " + c.Detail;
            }
        }

        private static string Short(ChannelStatus c)
        {
            switch (c.State)
            {
                case ChannelState.Streaming: return $"{c.Fps:0}fps";
                case ChannelState.Standby: return "standby";
                case ChannelState.NoCamera: return "no cam";
                default: return "error";
            }
        }

        private void ReloadAsync()
        {
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                _host.Reload();
                string msg = string.IsNullOrEmpty(_host.LastError) ? "Settings reloaded." : _host.LastError;
                _ui?.Post(__ => { if (!_exiting) _icon.ShowBalloonTip(3000, "GolfCamBridge", msg, ToolTipIcon.Info); }, null);
            });
        }

        /// <summary>Non-modal; a second click just brings the open window to the front.</summary>
        private void OpenSettings()
        {
            if (_exiting) return;
            if (_settings != null && !_settings.IsDisposed)
            {
                if (_settings.WindowState == FormWindowState.Minimized) _settings.WindowState = FormWindowState.Normal;
                _settings.Activate();
                return;
            }
            try
            {
                _settings = new SettingsForm(_host);
                _settings.FormClosed += (s, e) => _settings = null;
                _settings.Show();
            }
            catch (Exception ex)
            {
                _settings = null;
                Log.Error("Settings window: " + ex);
                MessageBox.Show("Could not open settings:\n" + ex.Message, "GolfCamBridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static void Open(string exe, string args)
        {
            try { Process.Start(exe, args); } catch (Exception ex) { MessageBox.Show(ex.Message, "GolfCamBridge"); }
        }

        /// <summary>Thread-safe exit request (e.g. Ctrl+C in console mode).</summary>
        public void RequestExit()
        {
            if (_ui != null) _ui.Post(_ => ExitApp(), null);
            else ExitApp();
        }

        private void ExitApp()
        {
            if (_exiting) return;
            _exiting = true;
            _timer.Stop();
            try { _settings?.CloseForExit(); } catch (Exception ex) { Log.Error("Settings close: " + ex.Message); }
            _icon.Visible = false;
            try { _host.Dispose(); } catch (Exception ex) { Log.Error("Shutdown: " + ex.Message); }
            _icon.Dispose();
            ExitThread();
        }

        private static Icon MakeIcon(Color color)
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                using (var body = new SolidBrush(Color.FromArgb(235, 235, 235)))
                using (var dot = new SolidBrush(color))
                using (var outline = new Pen(Color.FromArgb(60, 60, 60), 2))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    // simple camera glyph + status dot
                    g.FillRectangle(body, 3, 9, 20, 16);
                    g.DrawRectangle(outline, 3, 9, 20, 16);
                    g.FillPolygon(body, new[] { new Point(23, 14), new Point(30, 10), new Point(30, 24), new Point(23, 20) });
                    g.DrawPolygon(outline, new[] { new Point(23, 14), new Point(30, 10), new Point(30, 24), new Point(23, 20) });
                    g.FillEllipse(dot, 7, 12, 11, 11);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }
    }

    /// <summary>HKCU Run key - no admin rights needed.</summary>
    internal static class Autostart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "GolfCamBridge";

        public static bool Enabled
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k?.GetValue(ValueName) is string s && s.IndexOf(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            set
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) k.SetValue(ValueName, $"\"{Application.ExecutablePath}\"");
                    else k.DeleteValue(ValueName, false);
                }
            }
        }
    }
}
