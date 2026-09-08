using System;
using System.Drawing;
using System.Linq;
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

        private static CoreWebView2Environment? _sharedEnv;
        private static readonly System.Threading.SemaphoreSlim _envLock = new(1, 1);

        public Screen TargetScreen => _screen;
        public int MonitorIndex => _monitorIndex;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                return cp;
            }
        }

        private static async Task<CoreWebView2Environment> GetSharedEnvironmentAsync()
        {
            if (_sharedEnv != null) return _sharedEnv;
            await _envLock.WaitAsync();
            try
            {
                if (_sharedEnv == null)
                {
                    string userDataDir = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "My3DCubeWallpaper", "WebView2");
                    System.IO.Directory.CreateDirectory(userDataDir);
                    _sharedEnv = await CoreWebView2Environment.CreateAsync(null, userDataDir);
                }
                return _sharedEnv;
            }
            finally
            {
                _envLock.Release();
            }
        }

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

            AppLogger.Log($"WallpaperForm OnLoad for Monitor {_monitorIndex} ({_screen.DeviceName})...");
            _isAttached = DesktopHook.AttachToDesktop(Handle, _screen.Bounds);

            // 2. Initialize WebView2 with shared environment
            try
            {
                var env = await GetSharedEnvironmentAsync();
                AppLogger.Log($"Shared WebView2 environment ready. Initializing WebView2 on Monitor {_monitorIndex}...");
                await _webView.EnsureCoreWebView2Async(env);
                AppLogger.Log($"WebView2 initialized successfully on Monitor {_monitorIndex}. Navigating to {_targetUrl}...");

                if (_webView.CoreWebView2 != null)
                {
                    _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                    _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                    _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                    _webView.CoreWebView2.Settings.IsZoomControlEnabled = false;

                    _webView.CoreWebView2.WebMessageReceived += (s, args) =>
                    {
                        try
                        {
                            var rawJson = args.TryGetWebMessageAsString();
                            if (!string.IsNullOrEmpty(rawJson))
                            {
                                WebMessageInbound?.Invoke(this, rawJson);
                            }
                        }
                        catch { }
                    };

                    _webView.Source = new Uri(_targetUrl);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"ERROR initializing WebView2 on Monitor {_monitorIndex}: {ex}");
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

        public async Task SendMouseMoveAsync(int clientX, int clientY)
        {
            if (_webView.CoreWebView2 != null)
            {
                await _webView.CoreWebView2.ExecuteScriptAsync($"window.postMessage({{ action: 'mouseMove', clientX: {clientX}, clientY: {clientY} }}, '*');");
            }
        }

        public async Task SetWeatherAsync(string weather)
        {
            if (_webView.CoreWebView2 != null)
            {
                await _webView.CoreWebView2.ExecuteScriptAsync($"window.postMessage({{ action: 'setWeather', weather: '{weather}' }}, '*');");
            }
        }

        public async Task SetAudioModeAsync(string mode)
        {
            if (_webView.CoreWebView2 != null)
            {
                await _webView.CoreWebView2.ExecuteScriptAsync($"window.postMessage({{ action: 'setAudioMode', mode: '{mode}' }}, '*');");
            }
        }

        public async Task SendScreenCrackAsync(int clientX, int clientY)
        {
            if (_webView.CoreWebView2 != null)
            {
                await _webView.CoreWebView2.ExecuteScriptAsync($"window.postMessage({{ action: 'screenCrack', clientX: {clientX}, clientY: {clientY} }}, '*');");
            }
        }

        public void SendSystemAudio(float[] bands, float bass, float mid, float treble)
        {
            if (!IsHandleCreated || IsDisposed) return;

            try
            {
                BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (_webView?.CoreWebView2 != null)
                        {
                            string bStr = string.Join(",", bands.Select(b => b.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)));
                            string msg = $"{{\"action\":\"systemAudio\",\"bass\":{bass.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)},\"mid\":{mid.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)},\"treble\":{treble.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)},\"bands\":[{bStr}]}}";
                            _webView.CoreWebView2.PostWebMessageAsString(msg);
                        }
                    }
                    catch { }
                }));
            }
            catch { }
        }

        public event EventHandler<string>? WebMessageInbound;

        public async Task<string?> SendStreamOfferAsync(int faceIndex, string sdp, int timeoutMs = 8000)
        {
            if (!IsHandleCreated || IsDisposed || _webView?.CoreWebView2 == null) return null;

            var tcs = new TaskCompletionSource<string?>();

            EventHandler<string>? handler = null;
            handler = (s, json) =>
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("action", out var action) && action.GetString() == "webrtcAnswer")
                    {
                        if (root.TryGetProperty("sdp", out var sdpProp))
                        {
                            tcs.TrySetResult(sdpProp.GetString());
                        }
                    }
                }
                catch { }
            };

            WebMessageInbound += handler;

            try
            {
                var escapedSdp = System.Text.Json.JsonSerializer.Serialize(sdp);
                var msg = $"{{\"action\":\"webrtcOffer\",\"faceIndex\":{faceIndex},\"sdp\":{escapedSdp}}}";

                BeginInvoke(new Action(() =>
                {
                    try
                    {
                        _webView?.CoreWebView2?.PostWebMessageAsString(msg);
                    }
                    catch { }
                }));

                var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
                if (completed == tcs.Task)
                {
                    return await tcs.Task;
                }
                AppLogger.Log($"SendStreamOfferAsync timed out waiting for answer ({timeoutMs}ms).");
                return null;
            }
            finally
            {
                WebMessageInbound -= handler;
            }
        }

        public void SendIceCandidate(int faceIndex, string candidateJson)
        {
            if (!IsHandleCreated || IsDisposed || _webView?.CoreWebView2 == null) return;
            BeginInvoke(new Action(() =>
            {
                try
                {
                    _webView?.CoreWebView2?.PostWebMessageAsString($"{{\"action\":\"webrtcCandidate\",\"faceIndex\":{faceIndex},\"candidate\":{candidateJson}}}");
                }
                catch { }
            }));
        }

        public void StopStream(int faceIndex)
        {
            if (!IsHandleCreated || IsDisposed || _webView?.CoreWebView2 == null) return;
            BeginInvoke(new Action(() =>
            {
                try
                {
                    _webView?.CoreWebView2?.PostWebMessageAsString($"{{\"action\":\"stopChromeStream\",\"faceIndex\":{faceIndex}}}");
                }
                catch { }
            }));
        }

        public void Reload()
        {
            _webView.Reload();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            AppLogger.Log($"WallpaperForm OnFormClosing on Monitor {_monitorIndex}: CloseReason={e.CloseReason}, Cancel={e.Cancel}");
            DesktopHook.DetachFromDesktop(Handle);
            base.OnFormClosing(e);
        }
    }
}
