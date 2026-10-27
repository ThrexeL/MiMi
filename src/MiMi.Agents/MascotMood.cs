using MiMi.Core;

namespace MiMi.Agents;

/// <summary>MiMi's emotional state, derived from confirmed application events only.</summary>
public enum MascotMood
{
    /// <summary>MiMi has had nothing to reflect for a while: relaxed, half-lidded, dimmer glow.</summary>
    Sleepy,
    /// <summary>Nothing happening: round eyes, gentle blink, faint smile.</summary>
    Calm,
    /// <summary>A new agent session: attentive, briefly wider eyes.</summary>
    Curious,
    /// <summary>Agent working/tool running: attentive, still, no smile.</summary>
    Focused,
    /// <summary>Approval pending: wider eyes, brighter glow, no motion loop.</summary>
    Alert,
    /// <summary>One-shot celebration after a completed turn.</summary>
    Happy,
    /// <summary>Failure: faint concern, dimmer glow. Never dramatic.</summary>
    Concerned,
    /// <summary>Music playing with no agent work: quietly animated.</summary>
    Lively,
}

public enum MascotMoment
{
    None,
    AgentStarted,
    TurnCompleted,
}

/// <summary>Everything the mood policy needs. Kept inert and time-injected for tests.</summary>
public readonly record struct MascotMoodInput(
    bool ApprovalPending,
    bool ErrorActive,
    int WorkingAgents,
    bool MusicPlaying,
    TimeSpan QuietFor,
    bool SleepyEnabled,
    TimeSpan SleepAfter);

/// <summary>
/// Single source of truth for mascot expression priority:
///
///   approval &gt; error &gt; agent working &gt; completion reaction &gt; music &gt; quiet/sleep
///
/// Persistent mood is separate from brief reactions. Reactions never override approval or
/// error, never play while another agent is still working, and never resurrect sleep while
/// urgent work exists.
///
/// Sleep is about MiMi, not the desktop: it means no agents, approvals, notices, or media
/// have needed the face for <see cref="MascotMoodPolicy.DefaultSleepAfter"/>. System input
/// idleness is not used, because the display may already be off by then. Any new activity
/// wakes the face through the normal priority order.
/// </summary>
public static class MascotMoodPolicy
{
    /// <summary>Quiet time before dozing. Short enough to feel like personality, long
    /// enough that a finished notice does not immediately put MiMi to sleep.</summary>
    public static readonly TimeSpan DefaultSleepAfter = TimeSpan.FromMinutes(2);

    public static MascotMood Persistent(in MascotMoodInput input)
    {
        if (input.ApprovalPending) return MascotMood.Alert;
        if (input.ErrorActive) return MascotMood.Concerned;
        if (input.WorkingAgents > 0) return MascotMood.Focused;
        if (input.MusicPlaying) return MascotMood.Lively;
        if (input.SleepyEnabled && input.QuietFor >= input.SleepAfter) return MascotMood.Sleepy;
        return MascotMood.Calm;
    }

    /// <summary>Returns the transient mood for a moment, or null when the persistent mood
    /// already outranks it. A new session may briefly brighten even while work continues;
    /// a completion reaction never interrupts another agent that is still working, and
    /// neither reaction may override approval or error.</summary>
    public static MascotMood? Reaction(MascotMood persistent, MascotMoment moment) => moment switch
    {
        MascotMoment.AgentStarted when persistent is not (MascotMood.Alert or MascotMood.Concerned)
            => MascotMood.Curious,
        MascotMoment.TurnCompleted when persistent is MascotMood.Calm or MascotMood.Lively or MascotMood.Sleepy
            => MascotMood.Happy,
        _ => null,
    };

    /// <summary>A reaction must never outlive a newly arrived urgent state.</summary>
    public static bool ReactionAllowed(MascotMood persistent) =>
        persistent is MascotMood.Calm or MascotMood.Lively or MascotMood.Sleepy;
}
