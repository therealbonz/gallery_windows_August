using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace My3DCubeWallpaper
{
    public static class AppUpdater
    {
        private const string DownloadUrl = "http://162.35.101.183:5173/My3DCubeWallpaper.exe";
        private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

        public static async Task CheckAndUpdateAsync(bool showUpToDateMessage = false)
        {
            try
            {
                AppLogger.Log("Checking for app update from server...");
                string currentExePath = Process.GetCurrentProcess().MainModule?.FileName
                    ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "My3DCubeWallpaper.exe");

                var currentFileInfo = new FileInfo(currentExePath);
                long currentSize = currentFileInfo.Exists ? currentFileInfo.Length : 0;

                // 1. Send HEAD request to inspect server binary size
                using var headReq = new HttpRequestMessage(HttpMethod.Head, DownloadUrl);
                using var headResp = await _httpClient.SendAsync(headReq);
                if (!headResp.IsSuccessStatusCode)
                {
                    AppLogger.Log($"Update check failed: Server returned {headResp.StatusCode}");
                    if (showUpToDateMessage)
                    {
                        MessageBox.Show("Unable to reach update server.", "Update Check", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                    return;
                }

                long? serverContentLength = headResp.Content.Headers.ContentLength;

                // Download to temporary path
                string tempDir = Path.Combine(Path.GetTempPath(), "My3DCubeWallpaperUpdate");
                Directory.CreateDirectory(tempDir);
                string tempExe = Path.Combine(tempDir, "My3DCubeWallpaper_new.exe");

                AppLogger.Log($"Downloading update from {DownloadUrl} to {tempExe}...");
                using (var downloadResp = await _httpClient.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    downloadResp.EnsureSuccessStatusCode();
                    using var stream = await downloadResp.Content.ReadAsStreamAsync();
                    using var fs = new FileStream(tempExe, FileMode.Create, FileAccess.Write, FileShare.None);
                    await stream.CopyToAsync(fs);
                }

                var newFileInfo = new FileInfo(tempExe);
                if (!newFileInfo.Exists || newFileInfo.Length < 100000)
                {
                    AppLogger.Log("Downloaded file is invalid or too small.");
                    return;
                }

                // If identical size and not a manual force check, consider up to date
                if (currentSize == newFileInfo.Length && !showUpToDateMessage)
                {
                    AppLogger.Log("Current binary matches server binary. Already up to date.");
                    return;
                }

                AppLogger.Log($"Update downloaded ({newFileInfo.Length} bytes). Relaunching application...");

                // 2. Prepare restart batch script
                string batchScript = Path.Combine(tempDir, "update_and_restart.bat");
                int currentPid = Environment.ProcessId;

                string scriptContent = $@"@echo off
timeout /t 1 /nobreak >nul
:loop
tasklist /fi ""PID eq {currentPid}"" | find ""{currentPid}"" >nul
if not errorlevel 1 (
    timeout /t 1 /nobreak >nul
    goto loop
)
timeout /t 1 /nobreak >nul
copy /y ""{tempExe}"" ""{currentExePath}"" >nul
start """" ""{currentExePath}""
del ""%~f0""
";
                File.WriteAllText(batchScript, scriptContent);

                // 3. Launch updater batch detached
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"{batchScript}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                Process.Start(psi);
                AppLogger.Log("Updater helper started. Exiting current process for seamless relaunch.");

                // 4. Terminate current instance immediately
                Application.Exit();
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"Update error: {ex}");
                if (showUpToDateMessage)
                {
                    MessageBox.Show($"Update check encountered an error:\n{ex.Message}", "Update Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }
    }
}
