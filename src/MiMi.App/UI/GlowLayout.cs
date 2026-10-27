// Glow layout calculations (testable) + band sizing per island state
namespace MiMi.App.UI;

/// <summary>
/// Pure glow-geometry calculations. The glow is a FIXED-HEIGHT bottom band (DIPs),
/// never proportional to the panel — that was the root cause of the "brown oval"
/// across expanded cards. Idle pills get a thinner band (bottom 10–20% of height);
/// expanded panels get a restrained 18–22 DIP falloff regardless of their height.
/// Opacity is handled separately (state-driven), so geometry and intensity are
/// fully decoupled.
/// </summary>
public static class GlowLayout
{
    public const double IdleBandHeight = 8.0;
    public const double PanelBandHeight = 20.0;
    public const double IdleBandFraction = 0.20; // idle: bottom 10–20% of pill height

    /// <summary>Band height for a pill of the given height. Idle pills use the
    /// reference's thin band; expanded panels use the fixed panel band.</summary>
    public static double BandHeight(bool isIdle, double pillHeight)
    {
        if (isIdle)
        {
            var fractionBased = pillHeight * IdleBandFraction;
            return Math.Clamp(fractionBased, 5, IdleBandHeight);
        }
        return PanelBandHeight;
    }

    /// <summary>Bottom-anchored band never renders above the eyes' line: clamp so the
    /// band always stays in the lower portion of the pill.</summary>
    public static double ClampToBottomRegion(double bandHeight, double pillHeight) =>
        Math.Clamp(bandHeight, 3, pillHeight * 0.45);
}
