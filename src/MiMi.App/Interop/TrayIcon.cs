using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MiMi.Core;

namespace MiMi.App.Interop;

/// <summary>
/// Hand-rolled system tray icon (Shell_NotifyIconW) — no UI framework dependency.
/// Owns a message-only window for callbacks and a Win32 popup menu shown with the
/// SetForegroundWindow + TPM_RETURNCMD pattern so it dismisses correctly.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int MenuToggleVisibility = 100;
    private const int MenuPomodoro = 106;
    private const int MenuWater = 107;
    private const int MenuTodo = 108;
    private const int MenuShelf = 109;
    private const int MenuCodex = 110;
    private const int MenuSettings = 101;
    private const int MenuSimulate = 102;
    private const int MenuAutoStart = 103;
    private const int MenuRestart = 104;
    private const int MenuExit = 105;

    private static readonly Dictionary<IntPtr, TrayIcon> Instances = new();
    private static readonly NativeMethods.WndProc WndProcDelegate = WndProcStatic;

    private IntPtr _hwnd;
    private IntPtr _icon;
    private bool _added;
    private bool _autoStartChecked;
    private bool _islandVisible = true;
    private readonly HashSet<string> _utilityPins = new(StringComparer.Ordinal);
    private bool _shelfHasItems;
    private bool _shelfPinned;
    private uint _taskbarCreatedMsg;
    private Action? _balloonClick;

    public event Action<TrayIcon, TrayMenuCommand>? CommandSelected;

    /// <summary>Reflects the island's manual visibility preference in the menu label.</summary>
    public void SetVisibilityIndicator(bool islandVisible)
    {
        _islandVisible = islandVisible;
    }

    /// <summary>Raised when the menu opens so callers can refresh pin checkmarks.</summary>
    public event Action? UtilityIndicatorsRequested;

    /// <summary>Checkmarks the pinned utility panels (tray toggles open/close them).</summary>
    public void SetUtilityIndicators(bool pomodoro, bool water, bool todo)
    {
        _utilityPins.Clear();
        if (pomodoro) _utilityPins.Add("pomodoro");
        if (water) _utilityPins.Add("water");
        if (todo) _utilityPins.Add("todo");
    }

    /// <summary>Shelf pill auto-shows when items are on it; the tray item pins it open.</summary>
    public void SetShelfIndicator(bool hasItems, bool pinned)
    {
        _shelfHasItems = hasItems;
        _shelfPinned = pinned;
    }

    public void Initialize()
    {
        var wndClass = new NativeMethods.WNDCLASSW
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProcDelegate),
            lpszClassName = "MiMiTrayWnd",
            hInstance = NativeMethods.GetModuleHandleW(null),
        };
        NativeMethods.RegisterClassW(ref wndClass);

        _hwnd = NativeMethods.CreateWindowExW(0, "MiMiTrayWnd", "", NativeMethods.WS_POPUP,
            0, 0, 0, 0, (IntPtr)NativeMethods.HWND_MESSAGE, IntPtr.Zero, wndClass.hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("Could not create tray message window.");
        Instances[_hwnd] = this;

        _icon = CreateMascotIcon();
        AddOrUpdate(NativeMethods.NIM_ADD);
        _added = true;

        // Explorer restarts rebuild the taskbar and drop tray icons; Windows broadcasts
        // this registered message — re-add ours so the tray entry survives.
        _taskbarCreatedMsg = NativeMethods.RegisterWindowMessageW("TaskbarCreated");
    }

    /// <summary>Original MiMi identity: dark rounded square with two eyes, drawn in code.</summary>
    private static IntPtr CreateMascotIcon()
    {
        const int size = 32;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var body = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1C));
            dc.DrawRoundedRectangle(body, null, new Rect(2, 4, 28, 24), 9, 9);
            var eye = new SolidColorBrush(Colors.White);
            dc.DrawEllipse(eye, null, new Point(11, 16), 2.8, 2.8);
            dc.DrawEllipse(eye, null, new Point(21, 16), 2.8, 2.8);
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        // RenderTargetBitmap → HICON via GDI (no System.Drawing dependency).
        var stride = size * 4;
        var pixels = new byte[stride * size];
        bitmap.CopyPixels(pixels, stride, 0);
        var hColor = NativeMethods.CreateBitmap(size, size, 1, 32, pixels);
        var hMask = NativeMethods.CreateBitmap(size, size, 1, 1, new byte[(size / 8) * size]);
        try
        {
            var info = new NativeMethods.ICONINFO { fIcon = true, hbmMask = hMask, hbmColor = hColor };
            return NativeMethods.CreateIconIndirect(ref info);
        }
        finally
        {
            NativeMethods.DeleteObject(hColor);
            NativeMethods.DeleteObject(hMask);
        }
    }

    private void AddOrUpdate(uint message)
    {
        var data = new NativeMethods.NOTIFYICONDATAW
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP,
            uCallbackMessage = NativeMethods.WM_APP_TRAY,
            hIcon = _icon,
            szTip = "MiMi",
        };
        NativeMethods.Shell_NotifyIconW(message, ref data);
    }

    public void SetAutoStartIndicator(bool enabled) => _autoStartChecked = enabled;

    /// <summary>
    /// Shows a standard Shell balloon notification through the tray icon. This uses the
    /// long-supported Shell_NotifyIconW path (no Windows App SDK, package identity, or
    /// registration), respects Focus Assist, and never carries command text. Clicking is
    /// opt-in via <paramref name="onClick"/>; the callback runs on the tray window thread
    /// (the UI thread) and should only open/refocus MiMi — never approve anything.
    /// </summary>
    public void ShowBalloon(string title, string body, TrayNotificationKind kind,
        bool playSound, Action? onClick = null)
    {
        if (!_added || _hwnd == IntPtr.Zero) return;

        _balloonClick = onClick;
        var data = new NativeMethods.NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP | NativeMethods.NIF_INFO,
            uCallbackMessage = NativeMethods.WM_APP_TRAY,
            hIcon = _icon,
            szTip = "MiMi",
            szInfo = Truncate(body, 255),
            szInfoTitle = Truncate(title, 63),
            dwInfoFlags = BaseInfoFlags(kind) | NativeMethods.NIIF_RESPECT_QUIET_TIME
                           | (playSound ? 0 : NativeMethods.NIIF_NOSOUND),
        };
        if (!NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_MODIFY, ref data))
        {
            Logger.Warn("Balloon notification could not be displayed (tray icon not ready).");
            _balloonClick = null;
        }
    }

    private static uint BaseInfoFlags(TrayNotificationKind kind) => kind switch
    {
        TrayNotificationKind.Warning => NativeMethods.NIIF_WARNING,
        TrayNotificationKind.Error => NativeMethods.NIIF_ERROR,
        _ => NativeMethods.NIIF_INFO,
    };

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";

    private void ShowMenu()
    {
        UtilityIndicatorsRequested?.Invoke(); // refresh checkmarks at open time
        var menu = NativeMethods.CreatePopupMenu();
        NativeMethods.AppendMenuW(menu, 0, (IntPtr)MenuToggleVisibility,
            _islandVisible ? "Hide MiMi	Ctrl+Alt+M" : "Show MiMi	Ctrl+Alt+M");
        NativeMethods.AppendMenuW(menu, 0x800, IntPtr.Zero, ""); // separator
        NativeMethods.AppendMenuW(menu, _utilityPins.Contains("pomodoro") ? 8u : 0u, (IntPtr)MenuPomodoro, "Focus timer");
        NativeMethods.AppendMenuW(menu, _utilityPins.Contains("water") ? 8u : 0u, (IntPtr)MenuWater, "Water tracker");
        NativeMethods.AppendMenuW(menu, _utilityPins.Contains("todo") ? 8u : 0u, (IntPtr)MenuTodo, "To-do list");
        NativeMethods.AppendMenuW(menu, _shelfPinned ? 8u : 0u,
            (IntPtr)MenuShelf, "File shelf");
        NativeMethods.AppendMenuW(menu, 0x800, IntPtr.Zero, ""); // separator
        NativeMethods.AppendMenuW(menu, 0, (IntPtr)MenuSettings, "Settings…");
        NativeMethods.AppendMenuW(menu, 0, (IntPtr)MenuCodex, "Codex conversation…");
        NativeMethods.AppendMenuW(menu, 0, (IntPtr)MenuSimulate, "Simulate agent event");
        NativeMethods.AppendMenuW(menu, 0x800, IntPtr.Zero, ""); // separator
        NativeMethods.AppendMenuW(menu, _autoStartChecked ? 8u : 0u, (IntPtr)MenuAutoStart, "Start with Windows"); // MF_CHECKED
        NativeMethods.AppendMenuW(menu, 0, (IntPtr)MenuRestart, "Restart MiMi");
        NativeMethods.AppendMenuW(menu, 0, (IntPtr)MenuExit, "Exit MiMi");

        NativeMethods.GetCursorPos(out var pt);
        NativeMethods.SetForegroundWindow(_hwnd);
        var cmd = NativeMethods.TrackPopupMenuEx(menu,
            NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_RIGHTBUTTON, pt.X, pt.Y, _hwnd, IntPtr.Zero);
        NativeMethods.DestroyMenu(menu);
        NativeMethods.PostMessageW(_hwnd, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);

        var command = cmd switch
        {
            MenuPomodoro => TrayMenuCommand.TogglePomodoro,
            MenuWater => TrayMenuCommand.ToggleWater,
            MenuTodo => TrayMenuCommand.ToggleTodo,
            MenuShelf => TrayMenuCommand.ToggleShelf,
            MenuToggleVisibility => TrayMenuCommand.ToggleVisibility,
            MenuSettings => TrayMenuCommand.Settings,
            MenuCodex => TrayMenuCommand.CodexConversation,
            MenuSimulate => TrayMenuCommand.Simulate,
            MenuAutoStart => TrayMenuCommand.ToggleAutoStart,
            MenuRestart => TrayMenuCommand.Restart,
            MenuExit => TrayMenuCommand.Exit,
            _ => (TrayMenuCommand?)null,
        };
        if (command is not null) CommandSelected?.Invoke(this, command.Value);
    }

    public void Dispose()
    {
        _balloonClick = null;
        if (_added)
        {
            var data = new NativeMethods.NOTIFYICONDATAW
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
                hWnd = _hwnd,
                uID = 1,
            };
            NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_DELETE, ref data);
        }
        if (_hwnd != IntPtr.Zero) Instances.Remove(_hwnd);
        if (_icon != IntPtr.Zero) NativeMethods.DestroyIcon(_icon);
        if (_hwnd != IntPtr.Zero) NativeMethods.DestroyWindow(_hwnd);
    }

    private static IntPtr WndProcStatic(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (Instances.TryGetValue(hWnd, out var owner))
        {
            if (owner._taskbarCreatedMsg != 0 && msg == owner._taskbarCreatedMsg)
            {
                if (owner._added) owner.AddOrUpdate(NativeMethods.NIM_ADD); // re-add after Explorer restart
                return IntPtr.Zero;
            }
        }

        if (msg == NativeMethods.WM_APP_TRAY)
        {
            var mouseMsg = (uint)(lParam.ToInt64() & 0xFFFF);
            if (Instances.TryGetValue(hWnd, out var tray))
            {
                if (mouseMsg == 0x0205) // WM_RBUTTONUP
                    tray.ShowMenu();
                else if (mouseMsg == NativeMethods.NIN_BALLOONUSERCLICK)
                {
                    // Explicit user action only. Never auto-invoked.
                    var click = tray._balloonClick;
                    tray._balloonClick = null;
                    try { click?.Invoke(); }
                    catch (Exception ex) { Logger.Warn($"Balloon click handler failed: {ex.Message}"); }
                }
                else if (mouseMsg is NativeMethods.NIN_BALLOONTIMEOUT or NativeMethods.NIN_BALLOONHIDE)
                {
                    tray._balloonClick = null; // stale activation must not fire later
                }
            }
            return IntPtr.Zero;
        }
        if (msg == NativeMethods.WM_CLOSE)
        {
            NativeMethods.DestroyWindow(hWnd);
            return IntPtr.Zero;
        }
        return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }
}

public enum TrayMenuCommand { ToggleVisibility, TogglePomodoro, ToggleWater, ToggleTodo, ToggleShelf, Settings, CodexConversation, Simulate, ToggleAutoStart, Restart, Exit }

/// <summary>Balloon severity: maps to the Shell info icon (not app behavior).</summary>
public enum TrayNotificationKind { Info, Warning, Error }
