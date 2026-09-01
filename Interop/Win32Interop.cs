using System;
using System.Runtime.InteropServices;

namespace sumi.Interop
{
    /// <summary>
    /// Win32 P/Invoke 宣言、定数、構造体を集約した相互運用クラスです。
    /// </summary>
    public static class NativeMethods
    {
        #region Constants
        public static readonly IntPtr HWND_BROADCAST = (IntPtr)0xffff;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;

        public const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        public const uint MONITOR_DEFAULTTONULL = 0;

        public const int HOTKEY_ID_QUIT = 1001;
        public const int HOTKEY_ID_LAUNCH = 1002;

        public const uint WM_HOTKEY = 0x0312;
        public const uint WM_LBUTTONUP = 0x0202;
        public const uint WM_LBUTTONDBLCLK = 0x0203;
        public const uint WM_RBUTTONUP = 0x0205;
        public const uint WM_USER = 0x0400;
        public const uint WM_TRAYICON = WM_USER + 1;

        public const uint TRAY_ICON_ID = 1;
        public const int SUBCLASS_ID = 1;

        public const uint NIM_ADD = 0x00000000;
        public const uint NIM_MODIFY = 0x00000001;
        public const uint NIM_DELETE = 0x00000002;

        public const uint NIF_MESSAGE = 0x00000001;
        public const uint NIF_ICON = 0x00000002;
        public const uint NIF_TIP = 0x00000004;

        public const uint MF_STRING = 0x00000000;
        public const uint MF_SEPARATOR = 0x00000800;

        public const uint TPM_LEFTALIGN = 0x0000;
        public const uint TPM_BOTTOMALIGN = 0x0020;
        public const uint TPM_RIGHTBUTTON = 0x0002;
        public const uint TPM_RETURNCMD = 0x0100;

        public const uint IMAGE_ICON = 1;
        public const uint LR_LOADFROMFILE = 0x00000010;
        public const uint LR_DEFAULTSIZE = 0x00000040;

        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;
        public const uint MOD_NOREPEAT = 0x4000;
        #endregion

        #region Structs
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct NOTIFYICONDATA
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public uint uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }
        #endregion

        #region Delegates
        public delegate IntPtr SUBCLASSPROC(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData);
        #endregion

        #region User32
        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromRect(ref RECT lprc, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "AppendMenuW")]
        public static extern bool AppendMenu(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string lpNewItem);

        [DllImport("user32.dll")]
        public static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll")]
        public static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint RegisterWindowMessage(string lpString);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadImageW", SetLastError = true)]
        public static extern IntPtr LoadImage(IntPtr hInst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadIconW")]
        public static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        public static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        public static IntPtr SetWindowOwner(IntPtr childHwnd, IntPtr ownerHwnd)
        {
            const int GWL_HWNDPARENT = -8;
            if (IntPtr.Size == 8)
            {
                return SetWindowLongPtr(childHwnd, GWL_HWNDPARENT, ownerHwnd);
            }
            else
            {
                return new IntPtr(SetWindowLong32(childHwnd, GWL_HWNDPARENT, ownerHwnd.ToInt32()));
            }
        }
        #endregion

        #region ComCtl32
        [DllImport("comctl32.dll", CharSet = CharSet.Unicode)]
        public static extern bool SetWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, IntPtr uIdSubclass, IntPtr dwRefData);

        [DllImport("comctl32.dll", CharSet = CharSet.Unicode)]
        public static extern bool RemoveWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, IntPtr uIdSubclass);

        [DllImport("comctl32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);
        #endregion

        #region Shell32
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
        public static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);
        #endregion

        #region Shcore
        [DllImport("shcore.dll")]
        public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
        #endregion

        #region DwmApi
        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
        #endregion

        #region Kernel32
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string? lpModuleName);
        #endregion
    }
}
