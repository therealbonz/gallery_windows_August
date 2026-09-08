using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace My3DCubeWallpaper
{
    public class TrayApplicationContext : ApplicationContext
    {
        private readonly NotifyIcon _notifyIcon;
        private readonly List<WallpaperForm> _wallpaperForms = new();
        private readonly GlobalMouseHook _mouseHook;
        private readonly SystemAudioCapture _systemAudio;
        private readonly LocalStreamBridge _streamBridge;
        private readonly string? _baseUrl;
        private const string AppRegistryKey = "My3DCubeWallpaper";
        private bool _isSpinning = true;
        private string _audioMode = "system";

        public TrayApplicationContext(string? baseUrl = null)
        {
            _baseUrl = baseUrl;
            AppLogger.Log($"TrayApplicationContext starting. Found {Screen.AllScreens.Length} screens.");

            // 1. Spawn a WallpaperForm for every connected display
            InitializeMonitors();

            // 2. Listen to display changes (plug/unplug monitors)
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

            // 3. Initialize low-level desktop drag and hover hook
            _mouseHook = new GlobalMouseHook();
            _mouseHook.DragRotate += OnGlobalDragRotate;
            _mouseHook.MouseHover += OnGlobalMouseMove;
            _mouseHook.DesktopClick += OnGlobalDesktopClick;

            // 4. Initialize WASAPI System Audio Loopback for Spotify & YouTube
            _systemAudio = new SystemAudioCapture();
            _systemAudio.AudioDataAvailable += OnAudioDataAvailable;
            _systemAudio.Start();

            // 4.5. Initialize LocalStreamBridge for Chrome Extension WebRTC video streaming
            _streamBridge = new LocalStreamBridge();
            _streamBridge.OfferHandler = async (faceIndex, sdp) =>
            {
                var targetForm = _wallpaperForms.FirstOrDefault(f => f.MonitorIndex == 0) ?? _wallpaperForms.FirstOrDefault();
                if (targetForm != null)
                {
                    return await targetForm.SendStreamOfferAsync(faceIndex, sdp);
                }
                return null;
            };
            _streamBridge.CandidateHandler = (faceIndex, candidateJson) =>
            {
                var targetForm = _wallpaperForms.FirstOrDefault(f => f.MonitorIndex == 0) ?? _wallpaperForms.FirstOrDefault();
                targetForm?.SendIceCandidate(faceIndex, candidateJson);
            };
            _streamBridge.StopHandler = (faceIndex) =>
            {
                foreach (var form in _wallpaperForms)
                {
                    form.StopStream(faceIndex);
                }
            };
            _streamBridge.Start();

            // 5. Build ContextMenuStrip
            var contextMenu = new ContextMenuStrip();
            contextMenu.Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);

            // Title header showing monitor count
            int screenCount = Screen.AllScreens.Length;
            var titleItem = new ToolStripMenuItem($"🎲 My-3D-Cube ({screenCount} Monitor{(screenCount > 1 ? "s" : "")})")
            {
                Enabled = false,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
            };
            contextMenu.Items.Add(titleItem);
            contextMenu.Items.Add(new ToolStripSeparator());

            // Refresh item
            var refreshItem = new ToolStripMenuItem("🔄 Refresh All Cubes", null, async (s, e) =>
            {
                foreach (var form in _wallpaperForms)
                {
                    await form.RefreshMediaAsync();
                }
                _notifyIcon?.ShowBalloonTip(2000, "My-3D-Cube", "Refreshed all cubes with latest server uploads.", ToolTipIcon.Info);
            });
            contextMenu.Items.Add(refreshItem);

            // Open Web Gallery in browser
            var openWebItem = new ToolStripMenuItem("🌐 Open Web Gallery", null, (s, e) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo("http://162.35.101.183:5173") { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Unable to open browser: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            });
            contextMenu.Items.Add(openWebItem);

            // Pause / Resume toggle
            var pauseResumeItem = new ToolStripMenuItem("⏯️ Pause / Resume Rotation", null, async (s, e) =>
            {
                _isSpinning = !_isSpinning;
                foreach (var form in _wallpaperForms)
                {
                    await form.ToggleRotationAsync(_isSpinning);
                }
            });
            contextMenu.Items.Add(pauseResumeItem);

            // Rotation Speed submenu
            var speedMenu = new ToolStripMenuItem("⚡ Rotation Speed");
            var speedSlow = new ToolStripMenuItem("Slow (0.4x)", null, async (s, e) => await SetSpeedAll(0.4, s));
            var speedNormal = new ToolStripMenuItem("Normal (0.8x)", null, async (s, e) => await SetSpeedAll(0.8, s)) { Checked = true };
            var speedFast = new ToolStripMenuItem("Fast (1.6x)", null, async (s, e) => await SetSpeedAll(1.6, s));

            speedMenu.DropDownItems.AddRange(new ToolStripItem[] { speedSlow, speedNormal, speedFast });
            contextMenu.Items.Add(speedMenu);

            // Weather submenu
            var weatherMenu = new ToolStripMenuItem("🌧️ Weather FX");
            var weatherRain = new ToolStripMenuItem("🌧️ Rain & Glass Droplets", null, async (s, e) => await SetWeatherAll("rain", s)) { Checked = true };
            var weatherSnow = new ToolStripMenuItem("❄️ Snow Flurries", null, async (s, e) => await SetWeatherAll("snow", s));
            var weatherCloudy = new ToolStripMenuItem("☁️ Overcast Mist", null, async (s, e) => await SetWeatherAll("cloudy", s));
            var weatherClear = new ToolStripMenuItem("✨ Clear Stardust", null, async (s, e) => await SetWeatherAll("clear", s));
            var weatherAuto = new ToolStripMenuItem("⚡ Auto-Detect Live Weather", null, async (s, e) => await SetWeatherAll("auto", s));
            weatherMenu.DropDownItems.AddRange(new ToolStripItem[] { weatherRain, weatherSnow, weatherCloudy, weatherClear, weatherAuto });
            contextMenu.Items.Add(weatherMenu);

            // Audio Visualizer submenu
            var audioMenu = new ToolStripMenuItem("🎵 Music Visualizer");
            var audioSystem = new ToolStripMenuItem("🎛️ Sync with System Audio (Spotify / YouTube)", null, async (s, e) => await SetAudioAll("system", s)) { Checked = true };
            var audioBeat = new ToolStripMenuItem("🥁 Synth Beat Demo", null, async (s, e) => await SetAudioAll("beat", s));
            var audioMic = new ToolStripMenuItem("🎤 Microphone / Music Listen", null, async (s, e) => await SetAudioAll("mic", s));
            var audioOff = new ToolStripMenuItem("Off", null, async (s, e) => await SetAudioAll("off", s));
            audioMenu.DropDownItems.AddRange(new ToolStripItem[] { audioSystem, audioBeat, audioMic, audioOff });
            contextMenu.Items.Add(audioMenu);

            contextMenu.Items.Add(new ToolStripSeparator());

            // Check for Updates item
            var updateItem = new ToolStripMenuItem("🔄 Check for App Updates", null, async (s, e) =>
            {
                _notifyIcon?.ShowBalloonTip(2000, "My-3D-Cube", "Checking server for wallpaper updates...", ToolTipIcon.Info);
                await AppUpdater.CheckAndUpdateAsync(showUpToDateMessage: true);
            });
            contextMenu.Items.Add(updateItem);

            contextMenu.Items.Add(new ToolStripSeparator());

            // Start with Windows toggle
            var startupItem = new ToolStripMenuItem("🚀 Start with Windows")
            {
                CheckOnClick = true,
                Checked = IsStartupEnabled()
            };
            startupItem.CheckedChanged += (s, e) => SetStartupEnabled(startupItem.Checked);
            contextMenu.Items.Add(startupItem);

            contextMenu.Items.Add(new ToolStripSeparator());

            // Exit
            var exitItem = new ToolStripMenuItem("❌ Exit Wallpaper", null, (s, e) => ExitApp());
            contextMenu.Items.Add(exitItem);

            // 5. Initialize NotifyIcon
            _notifyIcon = new NotifyIcon
            {
                Text = $"My-3D-Cube Wallpaper ({screenCount} Screens)",
                Icon = CreateCubeIcon(),
                ContextMenuStrip = contextMenu,
                Visible = true
            };

            _notifyIcon.DoubleClick += (s, e) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo("http://162.35.101.183:5173") { UseShellExecute = true });
                }
                catch { }
            };

            _notifyIcon.ShowBalloonTip(
                3500,
                "My-3D-Cube Active",
                $"Running across {screenCount} displays with interactive drag-rotation enabled!",
                ToolTipIcon.Info);

            // 6. Silent background update check after startup
            Task.Run(async () =>
            {
                await Task.Delay(6000);
                await AppUpdater.CheckAndUpdateAsync(showUpToDateMessage: false);
            });
        }

        private void InitializeMonitors()
        {
            // Clean up existing forms if re-initializing
            foreach (var form in _wallpaperForms)
            {
                try
                {
                    form.Close();
                    form.Dispose();
                }
                catch { }
            }
            _wallpaperForms.Clear();

            int monitorIndex = 0;
            foreach (var screen in Screen.AllScreens)
            {
                var form = new WallpaperForm(screen, monitorIndex++, _baseUrl);
                form.Show();
                _wallpaperForms.Add(form);
            }
        }

        private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        {
            // Re-spawn wallpapers on new monitor layout
            InitializeMonitors();
        }

        private void OnGlobalDragRotate(Point pt, int deltaX, int deltaY)
        {
            try
            {
                // Identify which monitor the mouse is currently on
                var screen = Screen.FromPoint(pt);
                var targetForm = _wallpaperForms.FirstOrDefault(f => f.TargetScreen.DeviceName == screen.DeviceName);
                if (targetForm != null)
                {
                    _ = targetForm.SendDragRotateAsync(deltaX, deltaY);
                }
            }
            catch
            {
                // Non-critical drag failure ignored
            }
        }

        private void OnGlobalMouseMove(Point pt)
        {
            try
            {
                var screen = Screen.FromPoint(pt);
                var targetForm = _wallpaperForms.FirstOrDefault(f => f.TargetScreen.DeviceName == screen.DeviceName);
                if (targetForm != null)
                {
                    int localX = pt.X - screen.Bounds.X;
                    int localY = pt.Y - screen.Bounds.Y;
                    _ = targetForm.SendMouseMoveAsync(localX, localY);
                }
            }
            catch
            {
                // Non-critical hover failure ignored
            }
        }

        private void OnGlobalDesktopClick(Point pt)
        {
            try
            {
                var screen = Screen.FromPoint(pt);
                var targetForm = _wallpaperForms.FirstOrDefault(f => f.TargetScreen.DeviceName == screen.DeviceName);
                if (targetForm != null)
                {
                    int localX = pt.X - screen.Bounds.X;
                    int localY = pt.Y - screen.Bounds.Y;
                    _ = targetForm.SendScreenCrackAsync(localX, localY);
                }
            }
            catch
            {
                // Non-critical crack failure ignored
            }
        }

        private void OnAudioDataAvailable(float[] bands, float bass, float mid, float treble)
        {
            if (_audioMode != "system") return;
            foreach (var form in _wallpaperForms)
            {
                form.SendSystemAudio(bands, bass, mid, treble);
            }
        }

        private async Task SetSpeedAll(double speed, object? sender)
        {
            if (sender is ToolStripMenuItem item && item.OwnerItem is ToolStripMenuItem parent)
            {
                foreach (ToolStripItem child in parent.DropDownItems)
                {
                    if (child is ToolStripMenuItem mi) mi.Checked = false;
                }
                item.Checked = true;
            }

            foreach (var form in _wallpaperForms)
            {
                await form.SetRotationSpeedAsync(speed);
            }
        }

        private async Task SetWeatherAll(string weather, object? sender)
        {
            if (sender is ToolStripMenuItem item && item.OwnerItem is ToolStripMenuItem parent)
            {
                foreach (ToolStripItem child in parent.DropDownItems)
                {
                    if (child is ToolStripMenuItem mi) mi.Checked = false;
                }
                item.Checked = true;
            }

            foreach (var form in _wallpaperForms)
            {
                await form.SetWeatherAsync(weather);
            }
        }

        private async Task SetAudioAll(string mode, object? sender)
        {
            _audioMode = mode;
            if (mode == "system")
            {
                _systemAudio.Start();
            }
            else
            {
                _systemAudio.Stop();
            }

            if (sender is ToolStripMenuItem item && item.OwnerItem is ToolStripMenuItem parent)
            {
                foreach (ToolStripItem child in parent.DropDownItems)
                {
                    if (child is ToolStripMenuItem mi) mi.Checked = false;
                }
                item.Checked = true;
            }

            foreach (var form in _wallpaperForms)
            {
                await form.SetAudioModeAsync(mode);
            }
        }

        private bool IsStartupEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
                return key?.GetValue(AppRegistryKey) != null;
            }
            catch
            {
                return false;
            }
        }

        private void SetStartupEnabled(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (key != null)
                {
                    if (enable)
                    {
                        key.SetValue(AppRegistryKey, $"\"{Environment.ProcessPath}\"");
                    }
                    else
                    {
                        key.DeleteValue(AppRegistryKey, false);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not update Windows startup setting: {ex.Message}", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static Icon CreateCubeIcon()
        {
            try
            {
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
                if (File.Exists(iconPath))
                {
                    return new Icon(iconPath);
                }
            }
            catch { }

            return SystemIcons.Application;
        }

        private void ExitApp()
        {
            AppLogger.Log("ExitApp invoked.");
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _mouseHook?.Dispose();
            _systemAudio?.Dispose();
            _streamBridge?.Dispose();

            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
            }

            foreach (var form in _wallpaperForms)
            {
                try
                {
                    form.Close();
                }
                catch { }
            }

            Application.Exit();
        }

        protected override void Dispose(bool disposing)
        {
            AppLogger.Log($"TrayApplicationContext Dispose: disposing={disposing}");
            if (disposing)
            {
                SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                _mouseHook?.Dispose();
                _systemAudio?.Dispose();
                _streamBridge?.Dispose();
                _notifyIcon?.Dispose();

                foreach (var form in _wallpaperForms)
                {
                    form?.Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }
}
