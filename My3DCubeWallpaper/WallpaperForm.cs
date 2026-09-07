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
        private readonly Screen _screen;
        private readonly int _monitorIndex;
        private bool _isAttached = false;
        private readonly string _targetUrl;

        public Screen TargetScreen => _screen;
        public int MonitorIndex => _monitorIndex;

        public WallpaperForm(Screen screen, int monitorIndex, string? baseUrl = null)
        {
            _screen = screen;
            _monitorIndex = monitorIndex;

            string baseUri = string.IsNullOrWhiteSpace(baseUrl) ? "http://162.35.101.183:5173" : baseUrl.TrimEnd('/');
            string separator = baseUri.Contains('?') ? "&" : "?";
            _targetUrl = $"{baseUri}{separator}wallpaper=true&monitorIndex={_monitorIndex}&minimal=true";

            // Presentation settings
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Bounds = _screen.Bounds;
            BackColor = Color.FromArgb(7, 9, 14);

            // WebView2 component
            _webView = new WebView2
            {
                Dock = DockStyle.Fill,
                DefaultBackgroundColor = Color.FromArgb(7, 9, 14)
            };

            Controls.Add(_webView);
        }

        protected override async void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            // 1. Attach behind desktop icons covering this monitor's bounds
            _isAttached = DesktopHook.AttachToDesktop(Handle, _screen.Bounds);

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
                    $"Unable to initialize WebView2 on monitor {_monitorIndex + 1}:\n{ex.Message}\n\nPlease ensure Edge WebView2 Runtime is installed.",
                    "My-3D-Cube Wallpaper Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        public void RealignToBounds(Rectangle bounds)
        {
            Bounds = bounds;
            if (_isAttached)
            {
                DesktopHook.AttachToDesktop(Handle, bounds);
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

        public async Task SendDragRotateAsync(int deltaX, int deltaY)
        {
            if (_webView.CoreWebView2 != null)
            {
                await _webView.CoreWebView2.ExecuteScriptAsync($"window.postMessage({{ action: 'dragRotate', deltaX: {deltaX}, deltaY: {deltaY} }}, '*');");
            }
        }

        public void Reload()
        {
            _webView.Reload();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            DesktopHook.DetachFromDesktop(Handle);
            base.OnFormClosing(e);
        }
    }
}
