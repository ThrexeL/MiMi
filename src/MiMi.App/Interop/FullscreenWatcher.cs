using System.Runtime.InteropServices;
using System.Windows;
using MiMi.App.Interop;
using MiMi.Core;

namespace MiMi.App.Interop;

/// <summary>
/// Detects foreground fullscreen windows via EVENT_SYSTEM_FOREGROUND WinEvent hooks
/// (event-driven — no polling). Used to step the island aside during fullscreen media/games.
///
/// The Windows desktop (Progman/WorkerW), the taskbar, and other shell windows cover
/// the monitor exactly like a borderless game, so they are explicitly excluded: Win+D
/// and the taskbar's Show Desktop button make Progman the foreground window, and the
/// island must stay visible on the ordinary desktop. The exclusion is bypassed when the
/// user disables "Keep MiMi visible on desktop".
/// </summary>
public sealed class FullscreenWatcher : IDisposable
{
    private static readonly HashSet<string> ShellWindowClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",                 // desktop
        "WorkerW",                 // desktop wallpaper layer
        "Shell_TrayWnd",           // taskbar
        "Shell_SecondaryTrayWnd",  // secondary taskbars
        "XamlExplorerHostIslandWindow", // Win11 taskbar/shell host
    };

    private readonly Action<bool> _onChanged;
    private readonly Func<bool> _desktopPersistEnabled;
    private readonly IntPtr _hook;
    private readonly NativeMethods.WinEventProc _proc;
    private IntPtr _selfHwnd;
    private bool _isFullscreen;
    private bool _scheduled;

    public bool IsFullscreen => _isFullscreen;

    /// <summary>Raised (coalesced, on the dispatcher) whenever the foreground window
    /// changes — used by the island to re-assert topmost after shell z-order reshuffles.</summary>
    public event Action? ForegroundChanged;

    public FullscreenWatcher(Action<bool> onChanged, Func<bool>? desktopPersistEnabled = null)
    {
        _onChanged = onChanged;
        _desktopPersistEnabled = desktopPersistEnabled ?? (() => true);
        _proc = OnWinEvent;
        _hook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _proc, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);
        if (_hook == IntPtr.Zero) Logger.Warn("Fullscreen: WinEvent hook failed; fullscreen step-aside disabled.");
    }

    public void SetSelfWindow(IntPtr hwnd) => _selfHwnd = hwnd;

    /// <summary>Re-check on demand (e.g. after window reposition or display change).</summary>
    public void Refresh() => Evaluate();

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild,
        uint thread, uint time)
    {
        // Marshal onto the UI thread; coalesce bursts.
        if (_scheduled) return;
        _scheduled = true;
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _scheduled = false;
            ForegroundChanged?.Invoke();
            Evaluate();
        });
    }

    private void Evaluate()
    {
        var fullscreen = ComputeFullscreen();
        if (fullscreen == _isFullscreen) return;
        _isFullscreen = fullscreen;
        Logger.Info(fullscreen ? "Fullscreen detected — stepping aside." : "Fullscreen ended — returning.");
        _onChanged(fullscreen);
    }

    private bool ComputeFullscreen()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero || hwnd == _selfHwnd) return false;

        // The desktop and shell chrome are not fullscreen applications. MiMi stays
        // visible on the ordinary desktop; maximized (captioned) apps never match the
        // monitor bounds in the first place, so they are unaffected.
        if (_desktopPersistEnabled())
        {
            var className = GetWindowClass(hwnd);
            if (className is not null && ShellWindowClasses.Contains(className)) return false;
        }

        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return false;

        var monitor = NativeMethods.MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
        if (monitor == IntPtr.Zero) return false;
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfoW(monitor, ref info)) return false;

        // A window covering exactly the monitor bounds is fullscreen (taskbar-hidden).
        return rect.Left <= info.rcMonitor.Left && rect.Top <= info.rcMonitor.Top
            && rect.Right >= info.rcMonitor.Right && rect.Bottom >= info.rcMonitor.Bottom;
    }

    private static string? GetWindowClass(IntPtr hwnd)
    {
        var buffer = new System.Text.StringBuilder(256);
        var length = NativeMethods.GetClassNameW(hwnd, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString(0, length) : null;
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) NativeMethods.UnhookWinEvent(_hook);
    }
}

/// <summary>HKEY_CURRENT_USER Run-key autostart. No admin required.</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MiMi";

    public static bool IsEnabled()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static bool Set(bool enabled)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                var executable = Environment.ProcessPath
                    ?? throw new InvalidOperationException("MiMi executable path is unavailable.");
                key.SetValue(ValueName, $"\"{executable}\"", Microsoft.Win32.RegistryValueKind.String);
            }
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("AutoStart toggle failed", ex);
            return false;
        }
    }
}
