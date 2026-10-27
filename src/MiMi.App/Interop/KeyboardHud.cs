using System.Runtime.InteropServices;

namespace MiMi.App.Interop;

/// <summary>
/// Keyboard HUD state via the EXISTING 1 Hz island tick: Caps Lock state and the
/// current input language. No keyboard hooks, no keystroke content — only the lock
/// toggle state and the locale identifier of the focused thread's layout.
/// </summary>
public static class KeyboardHud
{
    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetLocaleInfoW(IntPtr locale, uint lcType,
        System.Text.StringBuilder data, int size);

    private const int VK_CAPITAL = 0x14;
    private const uint LOCALE_SENGLANGUAGE = 0x00001001;
    private const uint LOCALE_SENGCOUNTRY = 0x00001002;

    public static bool CapsLockOn => (GetKeyState(VK_CAPITAL) & 1) != 0;

    /// <summary>e.g. "ENG · UNITED STATES" from the current keyboard layout.</summary>
    public static string CurrentLayoutLabel()
    {
        try
        {
            var layout = GetKeyboardLayout(0);
            var langId = (ushort)(layout.ToInt64() & 0xFFFF);
            var language = GetLocaleInfo(langId, LOCALE_SENGLANGUAGE);
            var country = GetLocaleInfo(langId, LOCALE_SENGCOUNTRY);
            if (string.IsNullOrEmpty(language)) return "";
            return country.Length > 0 ? $"{language.ToUpperInvariant()} · {country.ToUpperInvariant()}" : language.ToUpperInvariant();
        }
        catch { return ""; }
    }

    private static string GetLocaleInfo(IntPtr locale, uint lcType)
    {
        var sb = new System.Text.StringBuilder(128);
        var n = GetLocaleInfoW(locale, lcType, sb, sb.Capacity);
        return n > 0 ? sb.ToString(0, n - 1) : "";
    }
}

/// <summary>Pure change-detector for the keyboard HUD (testable).</summary>
public sealed class KeyboardHudPolicy
{
    public enum Kind { None, CapsOn, CapsOff, LayoutChanged }

    private bool? _lastCaps;
    private string _lastLayout = "";

    public (Kind Kind, string Layout) Evaluate(bool capsOn, string layout)
    {
        Kind kind = Kind.None;
        if (_lastCaps is { } lastCaps && lastCaps != capsOn)
            kind = capsOn ? Kind.CapsOn : Kind.CapsOff;
        _lastCaps = capsOn;

        if (_lastLayout.Length > 0 && layout != _lastLayout && kind == Kind.None)
            kind = Kind.LayoutChanged;
        _lastLayout = layout;
        return (kind, layout);
    }
}
