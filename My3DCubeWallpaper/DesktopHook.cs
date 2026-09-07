using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace My3DCubeWallpaper
{
    public static class DesktopHook
    {
        private const uint WM_SPAWN_WORKERW = 0x052C;
        private const int SWP_SHOWWINDOW = 0x0040;
        private const int SWP_NOACTIVATE = 0x0010;
        private const int SWP_ASYNCWINDOWPOS = 0x4000;

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

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        /// <summary>
        /// Locates the WorkerW window directly behind desktop icons and docks the wallpaper window onto it.
        /// </summary>
        public static bool AttachToDesktop(IntPtr formHandle)
        {
            try
            {
                // 1. Fetch Progman handle
                IntPtr progman = FindWindow("Progman", null);
                if (progman == IntPtr.Zero)
                {
                    return false;
                }

                // 2. Send 0x052C message to Progman to spawn a WorkerW window behind icons
                SendMessageTimeout(
                    progman,
                    WM_SPAWN_WORKERW,
                    new IntPtr(0xD),
                    new IntPtr(0x1),
                    0,
                    1000,
                    out _);

                // 3. Find the WorkerW window that sits behind SHELLDLL_DefView
                IntPtr workerW = IntPtr.Zero;

                EnumWindows((hWnd, lParam) =>
                {
                    IntPtr shellDll = FindWindowEx(hWnd, IntPtr.Zero, "SHELLDLL_DefView", null);
                    if (shellDll != IntPtr.Zero)
                    {
                        // The WorkerW behind the desktop icons is the next sibling window
                        workerW = FindWindowEx(IntPtr.Zero, hWnd, "WorkerW", null);
                    }
                    return true;
                }, IntPtr.Zero);

                // Fallback if WorkerW sibling wasn't found directly
                if (workerW == IntPtr.Zero)
                {
                    workerW = progman;
                }

                // 4. Modify form extended style so it won't show on Alt+Tab or take focus
                int exStyle = GetWindowLong(formHandle, GWL_EXSTYLE);
                SetWindowLong(formHandle, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

                // 5. Parent the form to WorkerW
                SetParent(formHandle, workerW);

                // 6. Cover the entire virtual screen bounds (supports multi-monitor setups)
                var virtualBounds = SystemInformation.VirtualScreen;
                SetWindowPos(
                    formHandle,
                    IntPtr.Zero,
                    virtualBounds.X,
                    virtualBounds.Y,
                    virtualBounds.Width,
                    virtualBounds.Height,
                    SWP_SHOWWINDOW | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error attaching to desktop: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Restores parent to desktop when exiting.
        /// </summary>
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
