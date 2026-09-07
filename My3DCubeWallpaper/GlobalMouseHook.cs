using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace My3DCubeWallpaper
{
    public class GlobalMouseHook : IDisposable
    {
        private const int WH_MOUSE_LL = 14;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_MOUSEMOVE = 0x0200;

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
        private readonly LowLevelMouseProc _proc;
        private IntPtr _hookId = IntPtr.Zero;

        private bool _isDragging = false;
        private Point _lastPoint = Point.Empty;

        public event Action<Point, int, int>? DragRotate;
        public event Action<Point>? MouseHover;
        public event Action<Point>? DesktopClick;
        private long _lastHoverTick = 0;

        public GlobalMouseHook()
        {
            _proc = HookCallback;
            _hookId = SetHook(_proc);
        }

        private IntPtr SetHook(LowLevelMouseProc proc)
        {
            using var curProcess = Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule;
            return SetWindowsHookEx(WH_MOUSE_LL, proc, GetModuleHandle(curModule?.ModuleName), 0);
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var pt = new Point(hookStruct.pt.x, hookStruct.pt.y);
                int msg = wParam.ToInt32();

                if (msg == WM_LBUTTONDOWN)
                {
                    if (IsDesktopWindowAt(pt))
                    {
                        _isDragging = true;
                        _lastPoint = pt;
                        DesktopClick?.Invoke(pt);
                    }
                }
                else if (msg == WM_MOUSEMOVE)
                {
                    if (_isDragging)
                    {
                        int deltaX = pt.X - _lastPoint.X;
                        int deltaY = pt.Y - _lastPoint.Y;

                        if (deltaX != 0 || deltaY != 0)
                        {
                            DragRotate?.Invoke(pt, deltaX, deltaY);
                            _lastPoint = pt;
                        }
                    }
                    else
                    {
                        long now = Environment.TickCount64;
                        if (now - _lastHoverTick >= 25)
                        {
                            _lastHoverTick = now;
                            if (IsDesktopWindowAt(pt))
                            {
                                MouseHover?.Invoke(pt);
                            }
                        }
                    }
                }
                else if (msg == WM_LBUTTONUP)
                {
                    _isDragging = false;
                }
            }

            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        private static bool IsDesktopWindowAt(Point pt)
        {
            try
            {
                IntPtr hWnd = WindowFromPoint(new POINT { x = pt.X, y = pt.Y });
                if (hWnd == IntPtr.Zero) return false;

                var sb = new StringBuilder(256);
                GetClassName(hWnd, sb, sb.Capacity);
                string className = sb.ToString();

                // Desktop window classes
                return className == "WorkerW" ||
                       className == "Progman" ||
                       className == "SHELLDLL_DefView" ||
                       className == "SysListView32";
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
            GC.SuppressFinalize(this);
        }

        #region Win32 P/Invoke
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT point);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        #endregion
    }
}
