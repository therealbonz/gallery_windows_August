using System;
using System.IO;

namespace My3DCubeWallpaper
{
    public static class AppLogger
    {
        private static readonly object _lock = new object();
        private static readonly string LogFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.log");

        public static void Log(string message)
        {
            try
            {
                lock (_lock)
                {
                    File.AppendAllText(LogFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}\n");
                }
            }
            catch { }
        }
    }
}
