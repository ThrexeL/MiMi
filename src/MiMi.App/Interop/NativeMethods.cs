using System.Runtime.InteropServices;

namespace MiMi.App.Interop;

// Classic DllImport interop (not LibraryImport): the tray/window structs use ByValTStr
// strings which source-generated P/Invokes can't marshal, and the app is not AOT.
internal static class NativeMethods
{
    // ---- window styles / activation -----------------------------------------

    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_NOACTIVATE = 0x08000000;
    public const long WS_EX_TOOLWINDOW = 0x00000080;

    // ---- tray ------------------------------------------------------------------

    public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    public const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10;
    public const uint WM_APP_TRAY = 0x8000; // WM_APP

    // Balloon notification callbacks (delivered through uCallbackMessage).
    public const uint NIN_BALLOONSHOW = 0x0402, NIN_BALLOONHIDE = 0x0403;
    public const uint NIN_BALLOONTIMEOUT = 0x0404, NIN_BALLOONUSERCLICK = 0x0405;

    // NOTIFYICONINFO dwInfoFlags.
    public const uint NIIF_NONE = 0x0, NIIF_INFO = 0x1, NIIF_WARNING = 0x2, NIIF_ERROR = 0x3;
    public const uint NIIF_NOSOUND = 0x10;
    /// <summary>Honor Windows Do Not Disturb / Focus Assist instead of bypassing it.</summary>
    public const uint NIIF_RESPECT_QUIET_TIME = 0x80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATAW
    {
        public int cbSize;
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
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
    }

    [DllImport("shell32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    // ---- message-only window ----------------------------------------------------

    public const uint WS_POPUP = 0x80000000;
    // Signed: HWND_MESSAGE is (HWND)-3; an unsigned constant would widen to an invalid handle.
    public const nint HWND_MESSAGE = -3;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_NULL = 0x0000;

    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSW
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern ushort RegisterClassW(ref WNDCLASSW lpWndClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandleW(string? lpModuleName);

    // ---- context menu ------------------------------------------------------------

    public const uint TPM_RETURNCMD = 0x0100;
    public const uint TPM_RIGHTBUTTON = 0x0002;

    [DllImport("user32.dll", SetLastError = false)]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", SetLastError = false, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string lpNewItem);

    // Returns the selected command id when TPM_RETURNCMD is used (not a BOOL).
    [DllImport("user32.dll", SetLastError = false)]
    public static extern int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hWnd, IntPtr lptpm);

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    // ---- fullscreen detection ------------------------------------------------------

    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint WINEVENT_OUTOFCONTEXT = 0;

    public delegate void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hWnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll", SetLastError = false)]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventProc pfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll", SetLastError = false)]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = false)]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpInfo);

    public const uint WM_POWERBROADCAST = 0x0218;
    public const uint PBT_APMPOWERSTATUSCHANGE = 0x0010;

    // ---- hotkey + shell recovery + topmost ------------------------------------------------

    public const uint WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
    public const uint WM_SYSCOMMAND = 0x0112;
    public const uint WM_MOUSEACTIVATE = 0x0021;
    public const IntPtr MA_NOACTIVATE = (IntPtr)3;
    public const IntPtr SC_MINIMIZE = (IntPtr)0xF020;
    public const long WS_EX_TOPMOST = 0x00000008;
    public const long WS_EX_TRANSPARENT = 0x00000020;
    public static readonly IntPtr HwndTopmost = new(-1);
    public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010,
        SWP_NOOWNERZORDER = 0x0200;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AddClipboardFormatListener(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RemoveClipboardFormatListener(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = false, CharSet = CharSet.Unicode)]
    public static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow); // SW_SHOWNOACTIVATE = 4

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = false)]
    public static extern uint SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = false, CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessageW(string lpString);

    // ---- window long ------------------------------------------------------------------

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    // ---- GDI icon factory -------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    public struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("gdi32.dll", SetLastError = false)]
    public static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint nPlanes, uint nBitCount, byte[] lpBits);

    [DllImport("user32.dll", SetLastError = false)]
    public static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);

    [DllImport("gdi32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    // ---- console for --selftest ----------------------------------------------------------

    [DllImport("kernel32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachConsole(uint dwProcessId); // ATTACH_PARENT_PROCESS = 0xFFFFFFFF
}
