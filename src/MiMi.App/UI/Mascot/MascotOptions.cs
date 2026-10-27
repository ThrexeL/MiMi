using MiMi.Core;

namespace MiMi.App.UI.Mascot;

/// <summary>
/// Process-wide mascot appearance options, mirrored from the user's settings.
/// Controls read these at load and on <see cref="Changed"/>; App keeps it in sync
/// with the ConfigStore so templates (which have no config DataContext) stay simple.
/// </summary>
public static class MascotOptions
{
    public static bool FaceEnabled { get; private set; } = true;
    public static bool GlowEnabled { get; private set; } = true;
    /// <summary>0..1 — scales the gold glow's peak opacity.</summary>
    public static double GlowIntensity { get; private set; } = 0.6;
    /// <summary>0.5..2 — scales blink frequency, dart frequency, and glow pulse depth.</summary>
    public static double AnimationIntensity { get; private set; } = 1.0;

    /// <summary>Blink, glance, and float. Off = a still (but still expressive) face.</summary>
    public static bool AnimationsEnabled { get; private set; } = true;
    /// <summary>Brief start/completion reactions. Off keeps only persistent expressions.</summary>
    public static bool ReactionsEnabled { get; private set; } = true;
    /// <summary>After two quiet minutes with nothing to reflect, MiMi may doze.</summary>
    public static bool SleepyEnabled { get; private set; } = true;

    /// <summary>Windows animation preference plus MiMi's own setting. When true, expressions
    /// snap instead of tween and no looping animation runs.</summary>
    public static bool ReducedMotion { get; private set; }

    public static event Action? Changed;

    public static void Apply(AppSettings settings)
    {
        FaceEnabled = settings.MascotEnabled;
        GlowEnabled = settings.GlowEnabled;
        GlowIntensity = Math.Clamp(settings.GlowIntensity, 0, 1);
        AnimationIntensity = Math.Clamp(settings.AnimationIntensity, 0.5, 2);
        AnimationsEnabled = settings.MascotAnimationsEnabled;
        ReactionsEnabled = settings.MascotReactionsEnabled;
        SleepyEnabled = settings.MascotSleepyEnabled;
        ReducedMotion = !AnimationsEnabled || !System.Windows.SystemParameters.ClientAreaAnimation;
        Changed?.Invoke();
    }

    /// <summary>System animation settings can change at runtime (accessibility / power).
    /// Only notifies when the effective value actually changed.</summary>
    public static void RefreshReducedMotion()
    {
        var reduced = !AnimationsEnabled || !System.Windows.SystemParameters.ClientAreaAnimation;
        if (reduced == ReducedMotion) return;
        ReducedMotion = reduced;
        Changed?.Invoke();
    }
}
