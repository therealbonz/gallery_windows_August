using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace My3DCubeWallpaper
{
    public static class DesktopHook
    {
        private const uint WM_SPAWN_WORKERW = 0x052C;
        private const int SWP_SHOWWINDOW = 0x0040;
        private const int SWP_NOACTIVATE = 0x0010;
        private const int SWP_ASYNCWINDOWPOS = 0x4000;
        private const int SWP_NOMOVE = 0x0002;
        private const int SWP_NOSIZE = 0x0001;
        private static readonly IntPtr HWND_BOTTOM = new IntPtr(1);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string? className, string? windowTitle);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr hWnd,
            uint msg,
            IntPtr wParam,
            IntPtr lParam,
            uint fuFlags,
            uint uTimeout,
            out IntPtr lpdwResult);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        /// <summary>
        /// Locates the background WorkerW layer behind desktop icons and docks the wallpaper window underneath them.
        /// </summary>
        public static bool AttachToDesktop(IntPtr formHandle, Rectangle? customBounds = null)
        {
            try
            {
                // 1. Fetch Progman handle
                IntPtr progman = FindWindow("Progman", null);
                if (progman == IntPtr.Zero)
                {
                    progman = FindWindow("Progman", "Program Manager");
                }

                if (progman != IntPtr.Zero)
                {
                    // 2. Send 0x052C message to Progman to spawn the background WorkerW layer
                    SendMessageTimeout(
                        progman,
                        WM_SPAWN_WORKERW,
                        new IntPtr(0xD),
                        new IntPtr(0x1),
                        0,
                        1000,
                        out _);
                }

                // 3. Find the dedicated background WorkerW (the one without SHELLDLL_DefView)
                IntPtr wallpaperWorkerW = IntPtr.Zero;
                IntPtr shellWorkerW = IntPtr.Zero;
                IntPtr shellDefView = IntPtr.Zero;

                EnumWindows((hWnd, lParam) =>
                {
                    var sb = new StringBuilder(256);
                    GetClassName(hWnd, sb, sb.Capacity);
                    string cls = sb.ToString();

                    if (cls == "WorkerW")
                    {
                        IntPtr shell = FindWindowEx(hWnd, IntPtr.Zero, "SHELLDLL_DefView", null);
                        if (shell != IntPtr.Zero)
                        {
                            shellWorkerW = hWnd;
                            shellDefView = shell;
                            // Check next sibling WorkerW
                            IntPtr nextWorker = FindWindowEx(IntPtr.Zero, hWnd, "WorkerW", null);
                            if (nextWorker != IntPtr.Zero)
                            {
                                wallpaperWorkerW = nextWorker;
                            }
                        }
                        else
                        {
                            // A WorkerW window without SHELLDLL_DefView is the wallpaper layer
                            if (wallpaperWorkerW == IntPtr.Zero)
                            {
                                wallpaperWorkerW = hWnd;
                            }
                        }
                    }
                    else if (cls == "Progman")
                    {
                        IntPtr shell = FindWindowEx(hWnd, IntPtr.Zero, "SHELLDLL_DefView", null);
                        if (shell != IntPtr.Zero)
                        {
                            shellDefView = shell;
                            shellWorkerW = hWnd;
                        }
                    }

                    return true;
                }, IntPtr.Zero);

                // Target parent window: prefer wallpaperWorkerW, fallback to shellWorkerW or progman
                IntPtr targetParent = (wallpaperWorkerW != IntPtr.Zero) ? wallpaperWorkerW :
                                      (shellWorkerW != IntPtr.Zero) ? shellWorkerW : progman;

                if (targetParent == IntPtr.Zero)
                {
                    return false;
                }

                // 4. Modify form extended style so it won't show on Alt+Tab or take keyboard focus
                int exStyle = GetWindowLong(formHandle, GWL_EXSTYLE);
                SetWindowLong(formHandle, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

                // 5. Parent the form to the target background window
                SetParent(formHandle, targetParent);

                // 6. Cover screen bounds and place strictly at the BOTTOM of the Z-order behind icons
                var bounds = customBounds ?? SystemInformation.VirtualScreen;
                SetWindowPos(
                    formHandle,
                    HWND_BOTTOM,
                    bounds.X,
                    bounds.Y,
                    bounds.Width,
                    bounds.Height,
                    SWP_SHOWWINDOW | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);

                // 7. Ensure SHELLDLL_DefView (the desktop icons) stays above wallpaper
                if (shellDefView != IntPtr.Zero)
                {
                    SetWindowPos(shellDefView, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error attaching to desktop: {ex.Message}");
                return false;
            }
        }

        public static void DetachFromDesktop(IntPtr formHandle)
        {
            try
            {
                SetParent(formHandle, IntPtr.Zero);
            }
            catch
            {
                // Ignored during shutdown
            }
        }
    }
}
