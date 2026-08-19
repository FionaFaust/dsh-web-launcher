using System;
using System.IO;

namespace DshWebLauncher
{
    /// <summary>落盘日志（exe 同目录 logs\launcher.log，2MB 轮转）。日志失败不影响主流程。</summary>
    internal static class Log
    {
        private static readonly object Lock = new object();
        private static string _path;
        private static bool _enabled = true;

        /// <summary>初始化日志（仅一次）。</summary>
        public static void Init(bool enabled)
        {
            _enabled = enabled;
            try
            {
                if (!_enabled) return;
                string dir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                if (string.IsNullOrEmpty(dir)) return;
                string logs = Path.Combine(dir, "logs");
                Directory.CreateDirectory(logs);
                _path = Path.Combine(logs, "launcher.log");
            }
            catch { _path = null; }
        }

        public static void Info(string message) { Write("INFO", message); }
        public static void Warn(string message) { Write("WARN", message); }
        public static void Error(string message) { Write("ERROR", message); }

        private static void Write(string level, string message)
        {
            if (!_enabled || string.IsNullOrEmpty(_path)) return;
            try
            {
                lock (Lock)
                {
                    string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + level + " " + message + Environment.NewLine;
                    File.AppendAllText(_path, line);
                    FileInfo fi = new FileInfo(_path);
                    if (fi.Length > 2 * 1024 * 1024)
                    {
                        string old = _path + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(_path, old);
                    }
                }
            }
            catch { }
        }
    }
}
