using System;
using System.IO;

namespace GolfCamBridge
{
    /// <summary>Thread-safe logger: %LOCALAPPDATA%\GolfCamBridge\bridge.log (+ console when one is attached).</summary>
    internal static class Log
    {
        [ThreadStatic] public static string Prefix;

        private static readonly object Gate = new object();
        private const long MaxBytes = 5 * 1024 * 1024;

        public static readonly string Dir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GolfCamBridge");
        public static readonly string FilePath = Path.Combine(Dir, "bridge.log");

        public static bool ConsoleEnabled { get; set; }

        public static void Info(string msg) => Write(msg, null);
        public static void Set(string node, string v) => Info($"  {node} = {v}");
        public static void Skip(string node, string why) => Write($"  {node}: skipped ({why})", ConsoleColor.DarkYellow);
        public static void Error(string msg) => Write(msg, ConsoleColor.Red);

        private static void Write(string msg, ConsoleColor? color)
        {
            string line = $"{DateTime.Now:HH:mm:ss.fff} [{Prefix ?? "main"}] {msg}";
            lock (Gate)
            {
                try
                {
                    Directory.CreateDirectory(Dir);
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > MaxBytes)
                    {
                        string old = FilePath + ".1";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(FilePath, old);
                    }
                    File.AppendAllText(FilePath, line + Environment.NewLine);
                }
                catch { /* logging must never crash the bridge */ }

                if (ConsoleEnabled)
                {
                    if (color.HasValue) Console.ForegroundColor = color.Value;
                    Console.WriteLine(line);
                    if (color.HasValue) Console.ResetColor();
                }
            }
        }
    }
}
