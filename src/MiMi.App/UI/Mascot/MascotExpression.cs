namespace MiMi.App.UI.Mascot;

/// <summary>
/// MiMi's facial expression, derived from the island's current state.
/// Deliberately coarse: subtle deltas between them, never cartoonish.
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
    /// <summary>Agent started: eyes a touch wider, attentive. Settles into Focused.</summary>
    Curious,
    /// <summary>Turn completed: brief happy squint and a small smile, then back.</summary>
    Happy,
    /// <summary>Long user inactivity: gently half-lidded, dimmer glow, still adorable.</summary>
    Sleepy,
}
