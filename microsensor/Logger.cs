using System;
using System.IO;
using System.Text;

namespace microsensor
{
    /// <summary>简单的线程安全文件日志（WinExe 无控制台，输出到 microsensor.log 便于调试）。</summary>
    public static class Logger
    {
        private static readonly object Lock = new object();
        private static string _path = "microsensor.log";

        public static void SetPath(string path) => _path = path;

        public static void Log(string message)
        {
            try
            {
                lock (Lock)
                {
                    File.AppendAllText(_path,
                        $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}",
                        new UTF8Encoding(false));
                }
            }
            catch
            {
                // 日志失败不影响主流程
            }
        }
    }
}
