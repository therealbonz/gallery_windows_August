using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace My3DCubeWallpaper
{
    public class WindowCastForm : Form
    {
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private readonly WindowCaptureService _captureService;
        private readonly Func<int, bool, string, int, Task> _onStartCast;
        private readonly Func<int, bool, int, Task> _onStopCast;
        private readonly Func<IEnumerable<object>> _getMonitors;

        // UI Controls
        private ComboBox _comboWindows = null!;
        private Button _btnRefresh = null!;
        private PictureBox _previewBox = null!;
        private ComboBox _comboFace = null!;
        private ComboBox _comboMonitor = null!;
        private ComboBox _comboFps = null!;
        private Button _btnCast = null!;
        private Label _lblStatus = null!;
        private Label _lblPreviewTitle = null!;

        private List<WindowInfo> _currentWindows = new();
        private System.Windows.Forms.Timer? _previewTimer;
        private bool _isStreaming = false;

        public WindowCastForm(
            WindowCaptureService captureService,
            Func<int, bool, string, int, Task> onStartCast,
            Func<int, bool, int, Task> onStopCast,
            Func<IEnumerable<object>> getMonitors)
        {
            _captureService = captureService;
            _onStartCast = onStartCast;
            _onStopCast = onStopCast;
            _getMonitors = getMonitors;

            InitializeCustomUi();
            LoadMonitors();
            RefreshWindowList();

            // Subscribe to live frame updates for the preview box
            _captureService.FrameCaptured += OnLiveFrameCaptured;
        }

        private void InitializeCustomUi()
        {
            // Form styling
            Text = "My-3D-Cube: Windows Window Sync";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(440, 600);
            TopMost = true;
            ShowInTaskbar = true;
            BackColor = Color.FromArgb(11, 15, 25);
            ForeColor = Color.FromArgb(248, 250, 252);
            Font = new Font("Segoe UI", 9F, FontStyle.Regular);

            Shown += (s, e) =>
            {
                AppLogger.Log($"WindowCastForm SHOWN at Bounds={Bounds}, Handle=0x{Handle:X}");
                SetForegroundWindow(Handle);
            };

            try
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
            }
            catch { }

            // Header Panel
            var headerPanel = new Panel
            {
                Location = new Point(0, 0),
                Size = new Size(440, 56),
                BackColor = Color.FromArgb(16, 22, 38)
            };
            headerPanel.Paint += (s, e) =>
            {
                using var pen = new Pen(Color.FromArgb(36, 48, 79), 1);
                e.Graphics.DrawLine(pen, 0, 55, 440, 55);
            };

            var lblHeaderTitle = new Label
            {
                Text = "🎲 My-3D-Cube - Windows Window Sync",
                Font = new Font("Segoe UI", 11.5F, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 240, 255),
                Location = new Point(14, 10),
                AutoSize = true
            };

            var lblHeaderSubtitle = new Label
            {
                Text = "Cast any active Windows app or screen directly onto your 3D cube",
                Font = new Font("Segoe UI", 8F, FontStyle.Regular),
                ForeColor = Color.FromArgb(148, 163, 184),
                Location = new Point(16, 32),
                AutoSize = true
            };

            headerPanel.Controls.Add(lblHeaderTitle);
            headerPanel.Controls.Add(lblHeaderSubtitle);
            Controls.Add(headerPanel);

            int curY = 68;

            // 1. Window Selection Label & Refresh Button
            var lblSelectWindow = new Label
            {
                Text = "SELECT APPLICATION OR SCREEN TO CAST:",
                Font = new Font("Segoe UI", 8F, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 240, 255),
                Location = new Point(16, curY),
                AutoSize = true
            };
            Controls.Add(lblSelectWindow);

            _btnRefresh = new Button
            {
                Text = "🔄 Refresh",
                Font = new Font("Segoe UI", 8F, FontStyle.Regular),
                ForeColor = Color.FromArgb(200, 220, 240),
                BackColor = Color.FromArgb(24, 32, 54),
                FlatStyle = FlatStyle.Flat,
                Size = new Size(76, 22),
                Location = new Point(348, curY - 3),
                Cursor = Cursors.Hand
            };
            _btnRefresh.FlatAppearance.BorderColor = Color.FromArgb(45, 60, 95);
            _btnRefresh.Click += (s, e) => RefreshWindowList();
            Controls.Add(_btnRefresh);

            curY += 22;

            // Window Dropdown
            _comboWindows = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(16, curY),
                Size = new Size(408, 26),
                BackColor = Color.FromArgb(20, 27, 45),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Regular)
            };
            _comboWindows.SelectedIndexChanged += (s, e) => UpdatePreviewFromSelection();
            Controls.Add(_comboWindows);

            curY += 34;

            // 2. Live Preview Box
            _lblPreviewTitle = new Label
            {
                Text = "LIVE CAPTURE PREVIEW:",
                Font = new Font("Segoe UI", 7.5F, FontStyle.Bold),
                ForeColor = Color.FromArgb(148, 163, 184),
                Location = new Point(16, curY),
                AutoSize = true
            };
            Controls.Add(_lblPreviewTitle);

            curY += 18;

            _previewBox = new PictureBox
            {
                Location = new Point(16, curY),
                Size = new Size(408, 180),
                BackColor = Color.FromArgb(14, 18, 30),
                SizeMode = PictureBoxSizeMode.Zoom,
                BorderStyle = BorderStyle.FixedSingle
            };
            _previewBox.Paint += (s, e) =>
            {
                using var pen = new Pen(Color.FromArgb(36, 48, 79), 1);
                e.Graphics.DrawRectangle(pen, 0, 0, _previewBox.Width - 1, _previewBox.Height - 1);
            };
            Controls.Add(_previewBox);

            curY += 190;

            // 3. Grid for Target Options: Face, Monitor, FPS
            // Face
            var lblFace = new Label
            {
                Text = "CUBE FACE:",
                Font = new Font("Segoe UI", 8F, FontStyle.Bold),
                ForeColor = Color.FromArgb(148, 163, 184),
                Location = new Point(16, curY),
                AutoSize = true
            };
            Controls.Add(lblFace);

            _comboFace = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(16, curY + 18),
                Size = new Size(196, 25),
                BackColor = Color.FromArgb(20, 27, 45),
                ForeColor = Color.White
            };
            _comboFace.Items.Add("🌟 All 6 Faces (Full Cube)");
            _comboFace.Items.Add("🔲 Face 0 (Front)");
            _comboFace.Items.Add("🔲 Face 1 (Back)");
            _comboFace.Items.Add("🔲 Face 2 (Top)");
            _comboFace.Items.Add("🔲 Face 3 (Bottom)");
            _comboFace.Items.Add("🔲 Face 4 (Right)");
            _comboFace.Items.Add("🔲 Face 5 (Left)");
            _comboFace.SelectedIndex = 0;
            Controls.Add(_comboFace);

            // Target Display
            var lblMonitor = new Label
            {
                Text = "TARGET DISPLAY:",
                Font = new Font("Segoe UI", 8F, FontStyle.Bold),
                ForeColor = Color.FromArgb(148, 163, 184),
                Location = new Point(228, curY),
                AutoSize = true
            };
            Controls.Add(lblMonitor);

            _comboMonitor = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(228, curY + 18),
                Size = new Size(196, 25),
                BackColor = Color.FromArgb(20, 27, 45),
                ForeColor = Color.White
            };
            Controls.Add(_comboMonitor);

            curY += 52;

            // FPS Selector
            var lblFps = new Label
            {
                Text = "STREAM FPS / PERFORMANCE:",
                Font = new Font("Segoe UI", 8F, FontStyle.Bold),
                ForeColor = Color.FromArgb(148, 163, 184),
                Location = new Point(16, curY),
                AutoSize = true
            };
            Controls.Add(lblFps);

            _comboFps = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(16, curY + 18),
                Size = new Size(408, 25),
                BackColor = Color.FromArgb(20, 27, 45),
                ForeColor = Color.White
            };
            _comboFps.Items.Add("⚡ 30 FPS (Balanced - Recommended)");
            _comboFps.Items.Add("🚀 60 FPS (Ultra Smooth - High Motion)");
            _comboFps.Items.Add("🌱 15 FPS (Eco Mode - Low CPU)");
            _comboFps.SelectedIndex = 0;
            Controls.Add(_comboFps);

            curY += 52;

            // 4. Status Bar
            _lblStatus = new Label
            {
                Text = "🟢 Ready - Select an application above and click Cast",
                Font = new Font("Segoe UI", 8.5F, FontStyle.Regular),
                ForeColor = Color.FromArgb(16, 185, 129),
                Location = new Point(16, curY),
                Size = new Size(408, 20),
                TextAlign = ContentAlignment.MiddleLeft
            };
            Controls.Add(_lblStatus);

            curY += 26;

            // 5. Action Button (Cast / Stop)
            _btnCast = new Button
            {
                Text = "🚀 Cast Window to 3D Cube",
                Font = new Font("Segoe UI", 11F, FontStyle.Bold),
                ForeColor = Color.FromArgb(7, 9, 14),
                BackColor = Color.FromArgb(0, 240, 255),
                FlatStyle = FlatStyle.Flat,
                Size = new Size(408, 44),
                Location = new Point(16, curY),
                Cursor = Cursors.Hand
            };
            _btnCast.FlatAppearance.BorderSize = 0;
            _btnCast.Click += async (s, e) => await ToggleCastAsync();
            Controls.Add(_btnCast);

            // Preview update timer (refreshes idle preview every 2 seconds when idle)
            _previewTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            _previewTimer.Tick += (s, e) =>
            {
                if (!_isStreaming)
                {
                    UpdatePreviewFromSelection();
                }
            };
            _previewTimer.Start();
        }

        public void LoadMonitors()
        {
            _comboMonitor.Items.Clear();
            _comboMonitor.Items.Add("🌟 All Displays (Sync All Cubes)");

            try
            {
                var monitors = _getMonitors?.Invoke();
                if (monitors != null)
                {
                    foreach (var m in monitors)
                    {
                        var propName = m.GetType().GetProperty("name");
                        var propIdx = m.GetType().GetProperty("index");
                        if (propName != null)
                        {
                            _comboMonitor.Items.Add($"🖥️ {propName.GetValue(m)}");
                        }
                    }
                }
            }
            catch { }

            if (_comboMonitor.Items.Count == 1)
            {
                // Fallback to local Screen.AllScreens
                for (int i = 0; i < Screen.AllScreens.Length; i++)
                {
                    var s = Screen.AllScreens[i];
                    _comboMonitor.Items.Add($"🖥️ Monitor {i + 1} ({s.Bounds.Width}x{s.Bounds.Height}){(s.Primary ? " [Primary]" : "")}");
                }
            }

            _comboMonitor.SelectedIndex = 0;
        }

        public void RefreshWindowList()
        {
            var selectedHwnd = (_comboWindows.SelectedItem as WindowInfo)?.Hwnd;

            _currentWindows = WindowCaptureService.GetAvailableCaptureSources();
            _comboWindows.Items.Clear();

            int selectIndex = 0;
            for (int i = 0; i < _currentWindows.Count; i++)
            {
                var win = _currentWindows[i];
                _comboWindows.Items.Add(win);

                if (selectedHwnd.HasValue && win.Hwnd == selectedHwnd.Value)
                {
                    selectIndex = i;
                }
            }

            if (_comboWindows.Items.Count > 0)
            {
                _comboWindows.SelectedIndex = selectIndex;
            }

            UpdatePreviewFromSelection();
        }

        private void UpdatePreviewFromSelection()
        {
            if (_isStreaming) return; // Live frames handled via OnLiveFrameCaptured

            if (_comboWindows.SelectedItem is WindowInfo win)
            {
                Task.Run(() =>
                {
                    var bmp = WindowCaptureService.CaptureSnapshot(win, 640);
                    if (bmp != null)
                    {
                        try
                        {
                            BeginInvoke(new Action(() =>
                            {
                                var old = _previewBox.Image;
                                _previewBox.Image = bmp;
                                old?.Dispose();
                            }));
                        }
                        catch { }
                    }
                });
            }
        }

        private void OnLiveFrameCaptured(byte[] jpegBytes)
        {
            if (!_isStreaming) return;

            try
            {
                using var ms = new MemoryStream(jpegBytes);
                var bmp = new Bitmap(ms);

                BeginInvoke(new Action(() =>
                {
                    var old = _previewBox.Image;
                    _previewBox.Image = bmp;
                    old?.Dispose();
                }));
            }
            catch { }
        }

        private async Task ToggleCastAsync()
        {
            if (_isStreaming)
            {
                // Stop Casting
                _btnCast.Enabled = false;
                _btnCast.Text = "Stopping Cast...";

                int faceIdx = _comboFace.SelectedIndex == 0 ? -1 : _comboFace.SelectedIndex - 1;
                bool allFaces = _comboFace.SelectedIndex == 0;
                int monitorIdx = _comboMonitor.SelectedIndex == 0 ? -1 : _comboMonitor.SelectedIndex - 1;

                await _onStopCast(faceIdx, allFaces, monitorIdx);
                _captureService.StopCapture();

                SetIdleUi();
                _btnCast.Enabled = true;
            }
            else
            {
                // Start Casting
                if (_comboWindows.SelectedItem is not WindowInfo targetSource)
                {
                    MessageBox.Show("Please select an application or screen to cast.", "No Window Selected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                _btnCast.Enabled = false;
                _btnCast.Text = "Connecting Stream...";

                int faceIdx = _comboFace.SelectedIndex == 0 ? -1 : _comboFace.SelectedIndex - 1;
                bool allFaces = _comboFace.SelectedIndex == 0;
                int monitorIdx = _comboMonitor.SelectedIndex == 0 ? -1 : _comboMonitor.SelectedIndex - 1;

                int fps = 30;
                if (_comboFps.SelectedIndex == 1) fps = 60;
                if (_comboFps.SelectedIndex == 2) fps = 15;

                // 1. Start continuous capture service
                _captureService.StartCapture(targetSource, fps);

                // 2. Dispatch start message to wallpapers
                string streamUrl = $"http://127.0.0.1:{LocalStreamBridge.Port}/api/stream/window.mjpg?t={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
                await _onStartCast(faceIdx, allFaces, streamUrl, monitorIdx);

                SetStreamingUi(targetSource.Title, allFaces ? "All 6 Faces" : $"Face {faceIdx}");
                _btnCast.Enabled = true;
            }
        }

        private void SetStreamingUi(string title, string faceDesc)
        {
            _isStreaming = true;
            _comboWindows.Enabled = false;
            _comboFace.Enabled = false;
            _comboMonitor.Enabled = false;
            _comboFps.Enabled = false;
            _btnRefresh.Enabled = false;

            _lblStatus.Text = $"🔴 CASTING LIVE: {title} ➔ {faceDesc}";
            _lblStatus.ForeColor = Color.FromArgb(239, 68, 68);

            _btnCast.Text = "⏹️ Stop Casting to 3D Cube";
            _btnCast.BackColor = Color.FromArgb(239, 68, 68);
            _btnCast.ForeColor = Color.White;
        }

        private void SetIdleUi()
        {
            _isStreaming = false;
            _comboWindows.Enabled = true;
            _comboFace.Enabled = true;
            _comboMonitor.Enabled = true;
            _comboFps.Enabled = true;
            _btnRefresh.Enabled = true;

            _lblStatus.Text = "🟢 Ready - Select an application above and click Cast";
            _lblStatus.ForeColor = Color.FromArgb(16, 185, 129);

            _btnCast.Text = "🚀 Cast Window to 3D Cube";
            _btnCast.BackColor = Color.FromArgb(0, 240, 255);
            _btnCast.ForeColor = Color.FromArgb(7, 9, 14);

            UpdatePreviewFromSelection();
        }

        public void StopCurrentCast()
        {
            if (_isStreaming)
            {
                _captureService.StopCapture();
                SetIdleUi();
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                // Hide instead of disposing so casting continues smoothly in background
                e.Cancel = true;
                Hide();
            }
            else
            {
                _previewTimer?.Stop();
                _previewTimer?.Dispose();
                _captureService.FrameCaptured -= OnLiveFrameCaptured;
                base.OnFormClosing(e);
            }
        }
    }
}
