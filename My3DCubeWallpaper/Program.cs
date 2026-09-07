using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace My3DCubeWallpaper;

static class Program
{
    private static Mutex? _singleInstanceMutex;

    [STAThread]
    static void Main(string[] args)
    {
        try
        {
            const string mutexName = "Local\\My3DCubeWallpaper_SingleInstance_Mutex";
            _singleInstanceMutex = new Mutex(true, mutexName, out bool isNewInstance);

            if (!isNewInstance)
            {
                MessageBox.Show(
                    "My-3D-Cube Wallpaper is already running in your System Tray.\n\nLook for the 3D Cube icon near your Windows clock.",
                    "Already Running",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            ApplicationConfiguration.Initialize();

            string? targetUrl = args.Length > 0 ? args[0] : null;

            Application.Run(new TrayApplicationContext(targetUrl));

            GC.KeepAlive(_singleInstanceMutex);
        }
        catch (Exception ex)
        {
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