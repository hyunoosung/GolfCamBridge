using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace GolfCamBridge
{
    internal static class Program
    {
        [DllImport("kernel32.dll")] private static extern bool AllocConsole();

        // Usage:
        //   GolfCamBridge.exe                 -> tray app (normal use / autostart)
        //   GolfCamBridge.exe --console       -> same, plus a console window with the live log
        //   GolfCamBridge.exe list            -> print connected cameras (model, serial) and exit
        //   GolfCamBridge.exe --config x.json -> use another settings file
        //   GolfCamBridge.exe --selftest      -> settings-window logic checks, exit code 0 = OK (no camera)
        [STAThread]
        private static int Main(string[] args)
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string cfgPath = Path.Combine(exeDir, "golfcam.json");
            bool console = false, list = false;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "--console") console = true;
                else if (a == "list") list = true;
                else if (a == "--config" && i + 1 < args.Length) cfgPath = Path.GetFullPath(args[++i]);
            }

            if (console || list)
            {
                AllocConsole();
                Log.ConsoleEnabled = true;
            }

            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Error("Unhandled: " + e.ExceptionObject);
            Application.ThreadException += (s, e) => Log.Error("UI: " + e.Exception);

            if (list) return ListCameras();
            if (Array.IndexOf(args, "--selftest") >= 0) return SettingsForm.SelfCheck();   // settings-window logic only, touches no camera

            // One bridge per user session: a second instance could not create the virtual cams anyway.
            using (var mutex = new Mutex(true, @"Local\GolfCamBridge.SingleInstance", out bool first))
            {
                if (!first)
                {
                    MessageBox.Show("GolfCamBridge is already running (see the tray icon).", "GolfCamBridge",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                var host = new BridgeHost(cfgPath);
                try
                {
                    host.Start();
                }
                catch (Exception ex)
                {
                    Log.Error("Startup failed: " + ex);
                    MessageBox.Show("GolfCamBridge could not start:\n\n" + ex.Message + "\n\nLog: " + Log.FilePath,
                        "GolfCamBridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    try { host.Dispose(); } catch { }
                    return 1;
                }

                var tray = new TrayContext(host, cfgPath);
                if (console)
                    Console.CancelKeyPress += (s, e) => { e.Cancel = true; tray.RequestExit(); };

                Application.Run(tray);
                return 0;
            }
        }

        private static int ListCameras()
        {
            try
            {
                var system = new SpinnakerNET.ManagedSystem();
                var camList = system.GetCameras();
                int i = 0;
                foreach (SpinnakerNET.IManagedCamera c in camList)
                {
                    var tl = c.GetTLDeviceNodeMap();
                    Console.WriteLine($"[{i++}] {Nodes.GetString(tl, "DeviceModelName")}   serial={Nodes.GetString(tl, "DeviceSerialNumber")}");
                    c.Dispose();
                }
                if (i == 0) Console.WriteLine("No cameras found.");
                camList.Clear();
                system.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: " + ex.Message);
            }
            Console.WriteLine("\nPress any key to close...");
            Console.ReadKey();
            return 0;
        }
    }
}
