using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace My3DCubeWallpaper
{
    public class WindowInfo
    {
        public IntPtr Hwnd { get; set; } = IntPtr.Zero;
        public string Title { get; set; } = "";
        public string ProcessName { get; set; } = "";
        public Rectangle Bounds { get; set; }
        public bool IsScreen { get; set; } = false;
        public int ScreenIndex { get; set; } = -1;

        public string DisplayName
        {
            get
            {
                if (IsScreen)
                {
                    return $"🖥️ Full Screen: {Title}";
                }
                string proc = string.IsNullOrWhiteSpace(ProcessName) ? "" : $" [{ProcessName}]";
                return $"🪟 {Title}{proc}";
            }
        }

        public override string ToString() => DisplayName;
    }

    public class WindowCaptureService : IDisposable
    {
        #region Win32 P/Invoke

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern IntPtr GetWindowLongPtr32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
        {
            return IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : GetWindowLongPtr32(hWnd, nIndex);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindowDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(IntPtr hObject, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hObjectSource, int nXSrc, int nYSrc, int dwRop);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;

            public int Width => Right - Left;
            public int Height => Bottom - Top;
            public Rectangle ToRectangle() => new Rectangle(Left, Top, Width, Height);
        }

        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        private const int WS_VISIBLE = 0x10000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_APPWINDOW = 0x00040000;
        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        private const int DWMWA_CLOAKED = 14;
        private const uint PW_RENDERFULLCONTENT = 2;
        private const int SRCCOPY = 0x00CC0020;

        #endregion

        private CancellationTokenSource? _captureCts;
        private Task? _captureTask;
        private readonly object _stateLock = new();

        private bool _isCapturing = false;
        private WindowInfo? _currentSource;
        private int _targetFps = 30;
        private byte[]? _latestJpegFrame;

        public bool IsCapturing => _isCapturing;
        public WindowInfo? CurrentSource => _currentSource;
        public int TargetFps => _targetFps;
        public byte[]? LatestJpegFrame => _latestJpegFrame;

        public event Action<byte[]>? FrameCaptured;
        public event Action<bool, WindowInfo?>? CaptureStateChanged;

        private static readonly ImageCodecInfo? JpegCodec = GetEncoder(ImageFormat.Jpeg);
        private static readonly EncoderParameters JpegParams = CreateEncoderParams(75);

        private static ImageCodecInfo? GetEncoder(ImageFormat format)
        {
            return ImageCodecInfo.GetImageDecoders().FirstOrDefault(codec => codec.FormatID == format.Guid);
        }

        private static EncoderParameters CreateEncoderParams(long quality)
        {
            var p = new EncoderParameters(1);
            p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
            return p;
        }

        /// <summary>
        /// Enumerates all currently accessible open application windows plus physical monitors.
        /// </summary>
        public static List<WindowInfo> GetAvailableCaptureSources()
        {
            DesktopHook.EnsureDefaultDesktop();
            var results = new List<WindowInfo>();

            // 1. Add all screens / monitors first
            var screens = Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++)
            {
                var s = screens[i];
                results.Add(new WindowInfo
                {
                    Hwnd = IntPtr.Zero,
                    Title = $"Monitor {i + 1} ({s.Bounds.Width}x{s.Bounds.Height}){(s.Primary ? " [Primary]" : "")}",
                    ProcessName = "Desktop",
                    Bounds = s.Bounds,
                    IsScreen = true,
                    ScreenIndex = i
                });
            }

            // 2. Enumerate visible top-level application windows
            IntPtr shellWindow = GetShellWindow();
            int currentPid = Environment.ProcessId;

            EnumWindows((hWnd, lParam) =>
            {
                if (hWnd == shellWindow) return true;
                if (!IsWindowVisible(hWnd)) return true;

                int length = GetWindowTextLength(hWnd);
                if (length == 0) return true;

                var sb = new StringBuilder(length + 1);
                GetWindowText(hWnd, sb, sb.Capacity);
                string title = sb.ToString().Trim();

                if (string.IsNullOrWhiteSpace(title)) return true;

                // Skip known background/system surfaces
                if (title == "Program Manager" || title == "Windows Input Experience" || title == "Settings")
                    return true;

                // Check styles: skip tool windows unless explicitly marked as app windows
                long exStyle = GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64();
                if ((exStyle & WS_EX_TOOLWINDOW) != 0 && (exStyle & WS_EX_APPWINDOW) == 0)
                    return true;

                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid == currentPid) return true; // Skip our own wallpaper process windows

                string procName = "";
                try
                {
                    using var proc = Process.GetProcessById((int)pid);
                    procName = proc.ProcessName;
                }
                catch { }

                // Get accurate window bounds via DWM or fallback
                bool isMin = IsIconic(hWnd);
                Rectangle bounds;
                if (isMin)
                {
                    bounds = new Rectangle(0, 0, 1280, 720);
                }
                else
                {
                    if (DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT dwmRect, Marshal.SizeOf<RECT>()) == 0)
                    {
                        bounds = dwmRect.ToRectangle();
                    }
                    else
                    {
                        GetWindowRect(hWnd, out RECT winRect);
                        bounds = winRect.ToRectangle();
                    }
                }

                if (!isMin && (bounds.Width < 50 || bounds.Height < 50)) return true;

                results.Add(new WindowInfo
                {
                    Hwnd = hWnd,
                    Title = title,
                    ProcessName = procName,
                    Bounds = bounds,
                    IsScreen = false,
                    ScreenIndex = -1
                });

                return true;
            }, IntPtr.Zero);

            return results;
        }

        /// <summary>
        /// Captures a single snapshot of the target window or screen.
        /// </summary>
        public static Bitmap? CaptureSnapshot(WindowInfo info, int maxDimension = 960)
        {
            try
            {
                if (info.IsScreen)
                {
                    var screens = Screen.AllScreens;
                    Rectangle srcRect;
                    if (info.ScreenIndex >= 0 && info.ScreenIndex < screens.Length)
                    {
                        srcRect = screens[info.ScreenIndex].Bounds;
                    }
                    else
                    {
                        srcRect = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
                    }

                    return CaptureScreenRegion(srcRect, maxDimension);
                }

                if (info.Hwnd == IntPtr.Zero || !IsWindowVisible(info.Hwnd))
                {
                    return null;
                }

                // If window is minimized, we cannot capture DWM content reliably
                if (IsIconic(info.Hwnd))
                {
                    return CreatePlaceholderBitmap("Window Minimized", info.Title, maxDimension);
                }

                Rectangle bounds;
                if (DwmGetWindowAttribute(info.Hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT dwmRect, Marshal.SizeOf<RECT>()) == 0)
                {
                    bounds = dwmRect.ToRectangle();
                }
                else
                {
                    GetWindowRect(info.Hwnd, out RECT winRect);
                    bounds = winRect.ToRectangle();
                }

                if (bounds.Width <= 0 || bounds.Height <= 0)
                {
                    return null;
                }

                // Method 1: PrintWindow with PW_RENDERFULLCONTENT
                var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
                bool success = false;
                using (var g = Graphics.FromImage(bmp))
                {
                    IntPtr hdc = g.GetHdc();
                    try
                    {
                        success = PrintWindow(info.Hwnd, hdc, PW_RENDERFULLCONTENT);
                    }
                    catch { }
                    finally
                    {
                        g.ReleaseHdc(hdc);
                    }
                }

                // Method 2: If PrintWindow fails or returns black, fallback to screen region copy
                if (!success || IsBitmapEmpty(bmp))
                {
                    bmp.Dispose();
                    return CaptureScreenRegion(bounds, maxDimension);
                }

                if (bounds.Width > maxDimension || bounds.Height > maxDimension)
                {
                    var scaled = ScaleBitmap(bmp, maxDimension);
                    bmp.Dispose();
                    return scaled;
                }

                return bmp;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"CaptureSnapshot error: {ex.Message}");
                return null;
            }
        }

        private static Bitmap CaptureScreenRegion(Rectangle bounds, int maxDimension)
        {
            var rawBmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(rawBmp))
            {
                g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
            }

            if (bounds.Width > maxDimension || bounds.Height > maxDimension)
            {
                var scaled = ScaleBitmap(rawBmp, maxDimension);
                rawBmp.Dispose();
                return scaled;
            }

            return rawBmp;
        }

        private static Bitmap ScaleBitmap(Bitmap original, int maxDimension)
        {
            float scale = Math.Min((float)maxDimension / original.Width, (float)maxDimension / original.Height);
            int newWidth = Math.Max(1, (int)(original.Width * scale));
            int newHeight = Math.Max(1, (int)(original.Height * scale));

            var scaled = new Bitmap(newWidth, newHeight, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(scaled);
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(original, 0, 0, newWidth, newHeight);
            return scaled;
        }

        private static bool IsBitmapEmpty(Bitmap bmp)
        {
            try
            {
                Color c1 = bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);
                Color c2 = bmp.GetPixel(bmp.Width / 4, bmp.Height / 4);
                Color c3 = bmp.GetPixel(bmp.Width * 3 / 4, bmp.Height * 3 / 4);

                return c1.A == 0 && c2.A == 0 && c3.A == 0;
            }
            catch
            {
                return false;
            }
        }

        private static Bitmap CreatePlaceholderBitmap(string title, string subtitle, int dimension)
        {
            int w = Math.Min(dimension, 640);
            int h = Math.Min(dimension, 360);
            var bmp = new Bitmap(w, h);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.FromArgb(14, 20, 36));
            using var brush = new SolidBrush(Color.FromArgb(0, 240, 255));
            using var fontTitle = new Font("Segoe UI", 16, FontStyle.Bold);
            using var fontSub = new Font("Segoe UI", 11, FontStyle.Regular);
            using var subBrush = new SolidBrush(Color.FromArgb(148, 163, 184));

            g.DrawString(title, fontTitle, brush, new PointF(30, h / 2 - 30));
            g.DrawString(subtitle, fontSub, subBrush, new PointF(30, h / 2 + 5));
            return bmp;
        }

        /// <summary>
        /// Starts continuous live window or screen capture.
        /// </summary>
        public void StartCapture(WindowInfo source, int fps = 30)
        {
            lock (_stateLock)
            {
                StopCapture();

                _currentSource = source;
                _targetFps = Math.Clamp(fps, 10, 60);
                _captureCts = new CancellationTokenSource();
                _isCapturing = true;

                var token = _captureCts.Token;
                _captureTask = Task.Run(() => CaptureLoopAsync(source, _targetFps, token), token);

                AppLogger.Log($"WindowCaptureService started for '{source.Title}' at {_targetFps} FPS.");
                CaptureStateChanged?.Invoke(true, source);
            }
        }

        public void StopCapture()
        {
            lock (_stateLock)
            {
                if (!_isCapturing) return;

                _isCapturing = false;
                _captureCts?.Cancel();
                _captureCts?.Dispose();
                _captureCts = null;

                var priorSource = _currentSource;
                _currentSource = null;
                _latestJpegFrame = null;

                AppLogger.Log("WindowCaptureService stopped.");
                CaptureStateChanged?.Invoke(false, priorSource);
            }
        }

        private async Task CaptureLoopAsync(WindowInfo source, int fps, CancellationToken token)
        {
            int intervalMs = Math.Max(16, 1000 / fps);
            var sw = Stopwatch.StartNew();

            while (!token.IsCancellationRequested)
            {
                long startTicks = sw.ElapsedMilliseconds;

                try
                {
                    using var bmp = CaptureSnapshot(source, 800);
                    if (bmp != null && JpegCodec != null)
                    {
                        using var ms = new MemoryStream();
                        bmp.Save(ms, JpegCodec, JpegParams);
                        byte[] jpegBytes = ms.ToArray();
                        _latestJpegFrame = jpegBytes;

                        FrameCaptured?.Invoke(jpegBytes);
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"CaptureLoop frame error: {ex.Message}");
                }

                long elapsed = sw.ElapsedMilliseconds - startTicks;
                int delay = (int)(intervalMs - elapsed);
                if (delay > 2)
                {
                    try
                    {
                        await Task.Delay(delay, token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
                else
                {
                    await Task.Yield();
                }
            }
        }

        public void Dispose()
        {
            StopCapture();
        }
    }
}
