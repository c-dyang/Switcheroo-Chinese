using System;
using System.IO;
using System.Text;

namespace Switcheroo.Core
{
    /// <summary>
    /// 极简诊断日志：写 %AppData%\Switcheroo\diag\yyyyMMdd.log。
    /// 同时把 Console.Out 重定向到同一文件 —— 本程序是 WinExe，没有控制台，
    /// 原有的 Console.WriteLine 全部落在日志里，不丢任何既有调试输出。
    /// 所有写入都带异常吞掉：日志失败绝不影响主功能。
    /// </summary>
    public static class DiagLog
    {
        private static readonly object Gate = new object();
        private static string _path;
        private static int _pid;

        public static bool Enabled { get; private set; }
        public static string LogPath { get { return _path; } }

        /// <summary>
        /// 用户开关（设置里的「启用诊断日志」）。关掉后不再写文件，也不重定向 Console。
        /// 由 Program 在读取设置后设置；默认 true 以保持旧行为。
        /// </summary>
        public static bool UserEnabled = true;

        public static void Init()
        {
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Switcheroo", "diag");
                Directory.CreateDirectory(dir);
                _path = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd") + ".log");
                _pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                Enabled = true;

                var writer = new SafeTextWriter(_path);
                Console.SetOut(writer);
                Console.SetError(writer);

                string exe = "?";
                try { exe = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName; } catch { }

                Info("=== DiagLog ready | pid=" + _pid + " | exe=" + exe
                     + " | os=" + Environment.OSVersion.VersionString
                     + " | " + (Environment.Is64BitProcess ? "x64" : "x86") + " | log=" + _path);
            }
            catch
            {
                Enabled = false;
            }
        }

        public static void Info(string message)
        {
            Raw("INFO", message);
        }

        public static void Error(string message, Exception ex)
        {
            Raw("FAIL", message + (ex == null ? "" : " :: " + ex.GetType().Name + ": " + ex.Message));
            if (ex != null)
            {
                try { Raw("FAIL", ex.StackTrace); } catch { }
            }
        }

        internal static void Raw(string level, string line)
        {
            if (!Enabled || !UserEnabled || _path == null || line == null) return;
            try
            {
                lock (Gate)
                {
                    File.AppendAllText(
                        _path,
                        DateTime.Now.ToString("HH:mm:ss.fff") + " [" + level + "] " + line + Environment.NewLine,
                        Encoding.UTF8);
                }
            }
            catch
            {
                // 日志写不进去也绝不抛
            }
        }

        private sealed class SafeTextWriter : TextWriter
        {
            private readonly string _file;

            public SafeTextWriter(string file) { _file = file; }

            public override Encoding Encoding { get { return Encoding.UTF8; } }

            public override void Write(string value)
            {
                if (string.IsNullOrEmpty(value)) return;
                WriteRaw(value);
            }

            public override void WriteLine(string value)
            {
                WriteRaw(value ?? "");
            }

            private void WriteRaw(string value)
            {
                if (!UserEnabled) return;
                try
                {
                    lock (Gate)
                    {
                        File.AppendAllText(_file, value + Environment.NewLine, Encoding.UTF8);
                    }
                }
                catch
                {
                }
            }
        }
    }
}
