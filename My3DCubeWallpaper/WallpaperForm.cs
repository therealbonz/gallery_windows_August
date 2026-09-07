using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace My3DCubeWallpaper
{
    public class WallpaperForm : Form
    {
        private readonly WebView2 _webView;
        private bool _isAttached = false;
        private string _targetUrl = "http://162.35.101.183:5173/?wallpaper=true";

        public WallpaperForm(string? customUrl = null)
        {
            if (!string.IsNullOrWhiteSpace(customUrl))
            {
                _targetUrl = customUrl;
            }

            // Form presentation settings
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Bounds = SystemInformation.VirtualScreen;
            BackColor = Color.FromArgb(7, 9, 14); // Match deep space 3D background

            // WebView2 component
            _webView = new WebView2
            {
                Dock = DockStyle.Fill,
                DefaultBackgroundColor = Color.FromArgb(7, 9, 14)
            };

            Controls.Add(_webView);

            // Handle display resolution or monitor layout changes
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        }

        protected override async void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            // 1. Attach behind desktop icons
            _isAttached = DesktopHook.AttachToDesktop(Handle);

            // 2. Initialize WebView2
            try
            {
                var env = await CoreWebView2Environment.CreateAsync();
                await _webView.EnsureCoreWebView2Async(env);

                if (_webView.CoreWebView2 != null)
                {
                    _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                    _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                    _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                    _webView.CoreWebView2.Settings.IsZoomControlEnabled = false;

                    _webView.Source = new Uri(_targetUrl);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Unable to initialize WebView2 wallpaper engine:\n{ex.Message}\n\nPlease ensure Edge WebView2 Runtime is installed.",
                    "My-3D-Cube Wallpaper Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnDisplaySettingsChanged(sender, e)));
                return;
            }

            Bounds = SystemInformation.VirtualScreen;
            if (_isAttached)
            {
                DesktopHook.AttachToDesktop(Handle);
            }
        }

        public async Task RefreshMediaAsync()
        {
            if (_webView.CoreWebView2 != null)
            {
                await _webView.CoreWebView2.ExecuteScriptAsync("window.postMessage({ action: 'refresh' }, '*');");
            }
        }

        public async Task SetRotationSpeedAsync(double speed)
        {
            if (_webView.CoreWebView2 != null)
            {
                await _webView.CoreWebView2.ExecuteScriptAsync($"window.postMessage({{ action: 'setSpeed', speed: {speed.ToString(System.Globalization.CultureInfo.InvariantCulture)} }}, '*');");
            }
        }

        public async Task ToggleRotationAsync(bool? spin = null)
        {
            if (_webView.CoreWebView2 != null)
            {
                string arg = spin.HasValue ? (spin.Value ? "true" : "false") : "undefined";
                await _webView.CoreWebView2.ExecuteScriptAsync($"window.postMessage({{ action: 'toggleSpin', spin: {arg} }}, '*');");
            }
        }

        public void Reload()
        {
            _webView.Reload();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            DesktopHook.DetachFromDesktop(Handle);
            base.OnFormClosing(e);
        }
    }
}
