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
    private const int MenuSettings = 100;
    private const int MenuSimulate = 101;
    private const int MenuAutoStart = 102;
    private const int MenuRestart = 103;
    private const int MenuExit = 104;

    private static readonly Dictionary<IntPtr, TrayIcon> Instances = new();
    private static readonly NativeMethods.WndProc WndProcDelegate = WndProcStatic;

    private IntPtr _hwnd;
    private IntPtr _icon;
    private bool _added;
    private bool _autoStartChecked;

    public event Action<TrayIcon, TrayMenuCommand>? CommandSelected;

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

    private void ShowMenu()
    {
        var menu = NativeMethods.CreatePopupMenu();
        NativeMethods.AppendMenuW(menu, 0, (IntPtr)MenuSettings, "Settings…");
        NativeMethods.AppendMenuW(menu, 0, (IntPtr)MenuSimulate, "Simulate agent event");
        NativeMethods.AppendMenuW(menu, 0x800, IntPtr.Zero, ""); // separator
        NativeMethods.AppendMenuW(menu, _autoStartChecked ? 8u : 0u, (IntPtr)MenuAutoStart, "Start with Windows"); // MF_CHECKED
        NativeMethods.AppendMenuW(menu, 0, (IntPtr)MenuRestart, "Restart");
        NativeMethods.AppendMenuW(menu, 0, (IntPtr)MenuExit, "Exit");

        NativeMethods.GetCursorPos(out var pt);
        NativeMethods.SetForegroundWindow(_hwnd);
        var cmd = NativeMethods.TrackPopupMenuEx(menu,
            NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_RIGHTBUTTON, pt.X, pt.Y, _hwnd, IntPtr.Zero);
        NativeMethods.DestroyMenu(menu);
        NativeMethods.PostMessageW(_hwnd, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);

        var command = cmd switch
        {
            MenuSettings => TrayMenuCommand.Settings,
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
        if (msg == NativeMethods.WM_APP_TRAY)
        {
            var mouseMsg = (uint)(lParam.ToInt64() & 0xFFFF);
            if (mouseMsg == 0x0205 && Instances.TryGetValue(hWnd, out var tray)) // WM_RBUTTONUP
                tray.ShowMenu();
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

public enum TrayMenuCommand { Settings, Simulate, ToggleAutoStart, Restart, Exit }
