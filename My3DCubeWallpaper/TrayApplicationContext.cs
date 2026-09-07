using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;

namespace My3DCubeWallpaper
{
    public class TrayApplicationContext : ApplicationContext
    {
        private readonly NotifyIcon _notifyIcon;
        private readonly WallpaperForm _wallpaperForm;
        private const string AppRegistryKey = "My3DCubeWallpaper";
        private bool _isSpinning = true;

        public TrayApplicationContext(string? targetUrl = null)
        {
            // 1. Create and show WallpaperForm
            _wallpaperForm = new WallpaperForm(targetUrl);
            _wallpaperForm.Show();

            // 2. Build ContextMenuStrip
            var contextMenu = new ContextMenuStrip();
            contextMenu.Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);

            // Title header
            var titleItem = new ToolStripMenuItem("🎲 My-3D-Cube Wallpaper")
            {
                Enabled = false,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
            };
            contextMenu.Items.Add(titleItem);
            contextMenu.Items.Add(new ToolStripSeparator());

            // Refresh item
            var refreshItem = new ToolStripMenuItem("🔄 Refresh Cube Media", null, async (s, e) =>
            {
                await _wallpaperForm.RefreshMediaAsync();
                _notifyIcon?.ShowBalloonTip(2000, "My-3D-Cube", "Checking server for new uploads...", ToolTipIcon.Info);
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
                await _wallpaperForm.ToggleRotationAsync(_isSpinning);
            });
            contextMenu.Items.Add(pauseResumeItem);

            // Rotation Speed submenu
            var speedMenu = new ToolStripMenuItem("⚡ Rotation Speed");
            var speedSlow = new ToolStripMenuItem("Slow (0.4x)", null, async (s, e) => await SetSpeed(0.4, s));
            var speedNormal = new ToolStripMenuItem("Normal (0.8x)", null, async (s, e) => await SetSpeed(0.8, s)) { Checked = true };
            var speedFast = new ToolStripMenuItem("Fast (1.6x)", null, async (s, e) => await SetSpeed(1.6, s));

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

            // 3. Initialize NotifyIcon
            _notifyIcon = new NotifyIcon
            {
                Text = "My-3D-Cube Live Wallpaper",
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

            _notifyIcon.ShowBalloonTip(3000, "My-3D-Cube Active", "3D Cube Wallpaper running behind desktop icons. Right-click this tray icon for options.", ToolTipIcon.Info);
        }

        private async System.Threading.Tasks.Task SetSpeed(double speed, object? sender)
        {
            if (sender is ToolStripMenuItem item && item.OwnerItem is ToolStripMenuItem parent)
            {
                foreach (ToolStripItem child in parent.DropDownItems)
                {
                    if (child is ToolStripMenuItem mi) mi.Checked = false;
                }
                item.Checked = true;
            }
            await _wallpaperForm.SetRotationSpeedAsync(speed);
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
                string iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
                if (System.IO.File.Exists(iconPath))
                {
                    return new Icon(iconPath);
                }
            }
            catch { }

            return SystemIcons.Application;
        }

        private void ExitApp()
        {
            _notifyIcon.Visible = false;
            _wallpaperForm.Close();
            Application.Exit();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _notifyIcon?.Dispose();
                _wallpaperForm?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
