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
        private readonly string? _baseUrl;
        private const string AppRegistryKey = "My3DCubeWallpaper";
        private bool _isSpinning = true;

        public TrayApplicationContext(string? baseUrl = null)
        {
            _baseUrl = baseUrl;

            // 1. Spawn a WallpaperForm for every connected display
            InitializeMonitors();

            // 2. Listen to display changes (plug/unplug monitors)
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

            // 3. Initialize low-level desktop drag hook
            _mouseHook = new GlobalMouseHook();
            _mouseHook.DragRotate += OnGlobalDragRotate;

            // 4. Build ContextMenuStrip
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
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _mouseHook?.Dispose();

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
            if (disposing)
            {
                SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                _mouseHook?.Dispose();
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
