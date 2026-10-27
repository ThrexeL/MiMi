using MiMi.Core;

namespace MiMi.Agents;

/// <summary>MiMi's emotional state, derived from confirmed application events only.</summary>
public enum MascotMood
{
    /// <summary>Long genuine user inactivity: relaxed, half-lidded, dimmer glow.</summary>
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
    TimeSpan UserIdle,
    bool SleepyEnabled,
    TimeSpan SleepAfter);

/// <summary>
/// Single source of truth for mascot expression priority:
///
///   approval &gt; error &gt; agent working &gt; completion reaction &gt; music &gt; idle/sleep
///
/// Persistent mood is separate from brief reactions. Reactions never override approval or
/// error, never play while another agent is still working, and never resurrect idle while
/// urgent work exists. Sleep is user-idle based — agent silence is never treated as user
/// inactivity.
/// </summary>
public static class MascotMoodPolicy
{
    public static readonly TimeSpan DefaultSleepAfter = TimeSpan.FromMinutes(10);

    public static MascotMood Persistent(in MascotMoodInput input)
    {
        if (input.ApprovalPending) return MascotMood.Alert;
        if (input.ErrorActive) return MascotMood.Concerned;
        if (input.WorkingAgents > 0) return MascotMood.Focused;
        if (input.MusicPlaying) return MascotMood.Lively;
        if (input.SleepyEnabled && input.UserIdle >= input.SleepAfter) return MascotMood.Sleepy;
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
