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

    public static event Action? Changed;

    public static void Apply(AppSettings settings)
    {
        FaceEnabled = settings.MascotEnabled;
        GlowEnabled = settings.GlowEnabled;
        GlowIntensity = Math.Clamp(settings.GlowIntensity, 0, 1);
        AnimationIntensity = Math.Clamp(settings.AnimationIntensity, 0.5, 2);
        Changed?.Invoke();
    }
}
