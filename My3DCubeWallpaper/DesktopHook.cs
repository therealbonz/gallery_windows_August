using System;
using System.Drawing;
using System.IO;
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
        private static readonly IntPtr HWND_BOTTOM = new IntPtr(1);

        private const int GWL_EXSTYLE = -20;
        private const int GWL_STYLE = -16;
        private const int WS_CHILD = 0x40000000;
        private const int WS_CLIPSIBLINGS = 0x04000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const uint DESKTOP_ALL = 0x01FF;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenDesktop(string lpszDesktop, int dwFlags, bool fInherit, uint dwDesiredAccess);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetThreadDesktop(IntPtr hDesktop);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
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

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        /// <summary>
        /// Ensures the calling thread is attached to the interactive "default" desktop session.
        /// </summary>
        public static void EnsureDefaultDesktop()
        {
            try
            {
                IntPtr hDefault = OpenDesktop("default", 0, false, DESKTOP_ALL);
                if (hDefault != IntPtr.Zero)
                {
                    SetThreadDesktop(hDefault);
                }
            }
            catch { }
        }

        /// <summary>
        /// Locates the desktop wallpaper parent layer (behind icons) across all Windows 10/11 versions.
        /// </summary>
        public static IntPtr FindWallpaperParent()
        {
            EnsureDefaultDesktop();

            IntPtr progman = FindWindow("Progman", null);
            if (progman == IntPtr.Zero)
            {
                progman = FindWindow("Progman", "Program Manager");
            }

            if (progman != IntPtr.Zero)
            {
                // Send 0x052C message to Progman to spawn the background WorkerW layer
                SendMessageTimeout(progman, WM_SPAWN_WORKERW, new IntPtr(0xD), new IntPtr(0x1), 0, 1000, out _);
                SendMessageTimeout(progman, WM_SPAWN_WORKERW, IntPtr.Zero, IntPtr.Zero, 0, 1000, out _);
            }

            IntPtr targetParent = IntPtr.Zero;

            // Strategy 1: Child WorkerW directly inside Progman (behind SHELLDLL_DefView desktop icons)
            if (progman != IntPtr.Zero)
            {
                IntPtr childWorker = FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
                if (childWorker != IntPtr.Zero)
                {
                    return childWorker;
                }
            }

            // Strategy 2: Sibling WorkerW directly following top-level window hosting SHELLDLL_DefView
            EnumWindows((hWnd, lParam) =>
            {
                IntPtr shellDll = FindWindowEx(hWnd, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (shellDll != IntPtr.Zero)
                {
                    IntPtr sibling = FindWindowEx(IntPtr.Zero, hWnd, "WorkerW", null);
                    if (sibling != IntPtr.Zero)
                    {
                        targetParent = sibling;
                        return false;
                    }
                }
                return true;
            }, IntPtr.Zero);

            if (targetParent != IntPtr.Zero) return targetParent;

            // Strategy 3: Any top-level WorkerW that does not contain SHELLDLL_DefView
            EnumWindows((hWnd, lParam) =>
            {
                StringBuilder sb = new StringBuilder(256);
                GetClassName(hWnd, sb, 256);
                if (sb.ToString() == "WorkerW")
                {
                    IntPtr shellDll = FindWindowEx(hWnd, IntPtr.Zero, "SHELLDLL_DefView", null);
                    if (shellDll == IntPtr.Zero)
                    {
                        targetParent = hWnd;
                        return false;
                    }
                }
                return true;
            }, IntPtr.Zero);

            if (targetParent != IntPtr.Zero) return targetParent;

            // Strategy 4: Fallback to Progman
            return progman;
        }

        /// <summary>
        /// Docks the wallpaper form behind desktop icons, accurately mapping screen coordinates.
        /// </summary>
        public static bool AttachToDesktop(IntPtr formHandle, Rectangle? customBounds = null)
        {
            try
            {
                IntPtr targetParent = FindWallpaperParent();
                var screenBounds = customBounds ?? SystemInformation.VirtualScreen;

                // Map screen bounds to targetParent's client coordinates
                POINT pt = new POINT { X = screenBounds.X, Y = screenBounds.Y };
                if (targetParent != IntPtr.Zero)
                {
                    ScreenToClient(targetParent, ref pt);
                }

                // 1. Toolwindow and no-activate extended style
                int exStyle = GetWindowLong(formHandle, GWL_EXSTYLE);
                SetWindowLong(formHandle, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

                // 2. Set parent to target layer (behind icons)
                if (targetParent != IntPtr.Zero)
                {
                    SetParent(formHandle, targetParent);
                }

                // 3. Position window behind icons at calculated client bounds
                SetWindowPos(
                    formHandle,
                    HWND_BOTTOM,
                    pt.X,
                    pt.Y,
                    screenBounds.Width,
                    screenBounds.Height,
                    SWP_SHOWWINDOW | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);

                AppLogger.Log($"Attached Form 0x{formHandle:X} to Parent 0x{targetParent:X}. Screen: {screenBounds} -> Client: ({pt.X},{pt.Y})");
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"Attach error: {ex.Message}");
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
