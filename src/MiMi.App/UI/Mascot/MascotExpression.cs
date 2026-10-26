namespace MiMi.App.UI.Mascot;

/// <summary>
/// MiMi's facial expression, derived from the island's current state.
/// Deliberately coarse: five expressions, subtle deltas between them.
/// </summary>
public enum MascotExpression
{
    /// <summary>Idle: relaxed, slow blinking, gentle glow. The signature look.</summary>
    Calm,
    /// <summary>Music playing: softly animated, glow breathes with a livelier rhythm.</summary>
    Lively,
    /// <summary>Approval required: eyes open wider, glow brighter, mouth hidden.</summary>
    Alert,
    /// <summary>Agent working: slightly narrowed eyes, steady dim glow, no mouth.</summary>
    Focused,
    /// <summary>Failure: faint inward tilt of the eyes, dimmed glow. Still composed.</summary>
    Concerned,
}
