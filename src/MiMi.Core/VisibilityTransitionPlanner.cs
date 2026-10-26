namespace MiMi.Core;

public enum FadeAction
{
    /// <summary>Desired state already matches reality — nothing to do.</summary>
    None,
    /// <summary>Window hidden → show at the current opacity, fade up to 1.</summary>
    ShowFadeIn,
    /// <summary>Window visible → fade down to 0, then hide on completion.</summary>
    HideFadeOut,
    /// <summary>No animation (reduced motion / system restriction): show instantly.</summary>
    ShowImmediate,
    /// <summary>No animation (reduced motion / system restriction): hide instantly.</summary>
    HideImmediate,
}

/// <summary>An immutable visibility-transition decision.</summary>
public sealed record FadePlan(FadeAction Action, double FromOpacity)
{
    public static readonly FadePlan None = new(FadeAction.None, 1);
}

/// <summary>
/// Pure decision logic for island visibility transitions. The coordinator says *what*
/// the desired visibility is; this planner says *how to get there* from the actual
/// window state without jumps: animations always start from the current rendered
/// opacity, so rapid toggles reverse smoothly mid-fade.
///
/// Rules:
///  - an in-flight transition is always replaced, even when shown/hidden already match
///    (the animation may be heading the wrong way);
///  - showing always fades in (polished entrance);
///  - hiding fades out only when the user asked for it (hotkey/tray);
///  - system restrictions (fullscreen policy, secure desktop) hide immediately —
///    a decorative animation must never delay a real visibility requirement;
///  - reduced motion collapses everything to immediate transitions.
/// </summary>
public static class VisibilityTransitionPlanner
{
    public static FadePlan Plan(bool currentlyShown, double currentOpacity, bool desiredVisible,
        bool hideWasRequestedByUser, bool reducedMotion, bool transitionInFlight)
    {
        var opacity = Math.Clamp(currentOpacity, 0, 1);

        if (reducedMotion)
            return desiredVisible
                ? (currentlyShown ? FadePlan.None : new FadePlan(FadeAction.ShowImmediate, 0))
                : (currentlyShown ? new FadePlan(FadeAction.HideImmediate, 0) : FadePlan.None);

        // A transition already in flight always needs a replacement (it may be heading
        // the wrong way — e.g. reversing a half-finished fade-out back to visible).
        var needsTransition = transitionInFlight || desiredVisible != currentlyShown;
        if (!needsTransition) return FadePlan.None;

        return desiredVisible
            ? new FadePlan(FadeAction.ShowFadeIn, opacity)
            : hideWasRequestedByUser
                ? new FadePlan(FadeAction.HideFadeOut, opacity)
                : new FadePlan(FadeAction.HideImmediate, 0);
    }
}

/// <summary>
/// Generation token for fade completion callbacks. Every new transition increments the
/// generation; a completion handler captured under an older generation is stale and must
/// do nothing — this is what makes rapid Ctrl+Alt+M toggles deterministic (a delayed
/// Hide() can never execute after a newer Show()).
/// </summary>
public sealed class FadeSequencer
{
    private int _generation;

    public int Begin() => ++_generation;

    public bool IsCurrent(int generation) => generation == _generation;
}
