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
                if (args.Any(a => a.Equals("--cast", StringComparison.OrdinalIgnoreCase) || a.Equals("-cast", StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        using var client = new System.Net.Http.HttpClient();
                        client.Timeout = TimeSpan.FromSeconds(2);
                        client.GetAsync($"http://127.0.0.1:{LocalStreamBridge.Port}/api/window-caster/show").GetAwaiter().GetResult();
                        AppLogger.Log("Dispatched /api/window-caster/show to existing wallpaper instance.");
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Log($"Could not trigger existing window caster: {ex.Message}");
                    }
                    return;
                }

                var currentPid = Environment.ProcessId;
                var others = System.Diagnostics.Process.GetProcessesByName("My3DCubeWallpaper")
                    .Where(p => p.Id != currentPid)
                    .ToArray();

                if (others.Length > 0)
                {
                    AppLogger.Log($"Found {others.Length} running instance(s). Closing previous instance to apply update/relaunch...");
                    foreach (var p in others)
                    {
                        try
                        {
                            p.CloseMainWindow();
                            if (!p.WaitForExit(1500))
                            {
                                p.Kill();
                                p.WaitForExit(1000);
                            }
                        }
                        catch { }
                    }
                    Thread.Sleep(400);
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