namespace MiMi.Core;

/// <summary>The user's explicit visibility choice for the island.</summary>
public enum VisibilityPreference
{
    Visible,
    HiddenByUser,
}

/// <summary>
/// Single source of truth for whether the island should be on screen:
///
///   EffectiveVisible = Preference == Visible && !FullscreenSuppressed && SystemAllowsOverlay
///
/// Every visibility influence (manual hotkey, fullscreen policy, future session
/// events) flows through here, so the systems can never fight each other. Display
/// content (music, agent cards, clock) is completely independent of this state —
/// media monitoring, the agent bridge, and approval expiry all continue while hidden.
///
/// Guarantees:
///  - a HiddenByUser preference survives Win+D, fullscreen exits, and shell restarts
///    until the user toggles it back (nothing else may clear it);
///  - a pending approval keeps expiring on schedule while hidden (fail-closed, and
///    never auto-approved just because the island is off screen).
/// </summary>
public sealed class VisibilityCoordinator
{
    public VisibilityPreference Preference { get; private set; } = VisibilityPreference.Visible;
    public bool FullscreenSuppressed { get; private set; }
    /// <summary>False only when the system session itself cannot show overlays
    /// (secure desktop / lock screen). MiMi never forces itself over those.</summary>
    public bool SystemAllowsOverlay { get; set; } = true;

    public bool EffectiveVisible => Preference == VisibilityPreference.Visible
        && !FullscreenSuppressed
        && SystemAllowsOverlay;

    /// <summary>Raised after any input change; subscribe to apply the new state.</summary>
    public event Action? Changed;

    public void SetPreference(VisibilityPreference preference)
    {
        if (Preference == preference) return;
        Preference = preference;
        Changed?.Invoke();
    }

    public void TogglePreference() =>
        SetPreference(Preference == VisibilityPreference.Visible
            ? VisibilityPreference.HiddenByUser
            : VisibilityPreference.Visible);

    public void SetFullscreenSuppressed(bool suppressed)
    {
        if (FullscreenSuppressed == suppressed) return;
        FullscreenSuppressed = suppressed;
        Changed?.Invoke();
    }
}
