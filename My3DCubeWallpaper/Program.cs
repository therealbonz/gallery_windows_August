using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace My3DCubeWallpaper;

static class Program
{
    private static Mutex? _singleInstanceMutex;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

    [STAThread]
    static void Main(string[] args)
    {
        try
        {
            DesktopHook.EnsureDefaultDesktop();

            const string mutexName = "Local\\My3DCubeWallpaper_SingleInstance_Mutex";
            bool isNewInstance;
            try
            {
                _singleInstanceMutex = new Mutex(true, mutexName, out isNewInstance);
            }
            catch (AbandonedMutexException)
            {
                isNewInstance = true;
            }

            if (!isNewInstance)
            {
                var currentPid = Environment.ProcessId;
                var others = System.Diagnostics.Process.GetProcessesByName("My3DCubeWallpaper")
                    .Where(p => p.Id != currentPid)
                    .ToArray();

                if (others.Length > 0)
                {
                    MessageBox.Show(
                        "My-3D-Cube Wallpaper is already running in your System Tray.\n\nLook for the 3D Cube icon near your Windows clock.",
                        "Already Running",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }
            }

            // Enable true Per-Monitor V2 DPI Awareness to prevent display gaps across scaled monitors
            try
            {
                SetProcessDpiAwarenessContext(new IntPtr(-4));
            }
            catch { }

            ApplicationConfiguration.Initialize();

            Application.ThreadException += (s, e) => AppLogger.Log($"Application.ThreadException: {e.Exception}");
            AppDomain.CurrentDomain.UnhandledException += (s, e) => AppLogger.Log($"AppDomain.UnhandledException: {e.ExceptionObject}");
            Application.ApplicationExit += (s, e) => AppLogger.Log("Application.ApplicationExit triggered.");

            string? targetUrl = args.Length > 0 ? args[0] : null;
            AppLogger.Log($"Starting TrayApplicationContext (targetUrl: {targetUrl ?? "default"})...");

            Application.Run(new TrayApplicationContext(targetUrl));

            AppLogger.Log("Application.Run returned.");
            GC.KeepAlive(_singleInstanceMutex);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"FATAL Main Exception: {ex}");
            string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");
            File.WriteAllText(logPath, ex.ToString());
            MessageBox.Show(
                $"Error launching My-3D-Cube Live Wallpaper:\n\n{ex.Message}\n\nDetails saved to crash.log",
                "Startup Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}