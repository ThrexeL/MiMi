using System.Diagnostics;

namespace MiMi.Core;

public enum PomodoroPhase
{
    Idle,
    Focus,
    ShortBreak,
    LongBreak,
}

/// <summary>
/// Deadline-based Pomodoro engine. Timekeeping derives from a stopwatch + target
/// deadline rather than trusting timer callbacks, so the countdown stays correct
/// across panel collapses, hides, media changes, approvals, and system sleep
/// (sleep pauses the stopwatch, matching user expectation for a focus timer).
///
/// Phase flow: Focus → ShortBreak → Focus → ShortBreak → … → LongBreak every
/// <see cref="SessionsBeforeLongBreak"/> completed focus sessions.
/// Completion is raised exactly once per phase transition.
/// </summary>
public sealed class PomodoroTimer
{
    public static readonly TimeSpan DefaultFocus = TimeSpan.FromMinutes(25);
    public static readonly TimeSpan DefaultShortBreak = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DefaultLongBreak = TimeSpan.FromMinutes(15);

    private readonly Stopwatch _runningSince = new();
    private TimeSpan _phaseDuration;
    private PomodoroPhase _phase = PomodoroPhase.Idle;
    private int _completedFocusSessions;
    private bool _completionReported = true;

    public PomodoroPhase Phase => _phase;
    public int CompletedFocusSessions => _completedFocusSessions;
    public bool IsRunning => _phase != PomodoroPhase.Idle && _runningSince.IsRunning;
    public bool IsPaused => _phase != PomodoroPhase.Idle && !_runningSince.IsRunning;

    public TimeSpan FocusDuration { get; set; } = DefaultFocus;
    public TimeSpan ShortBreakDuration { get; set; } = DefaultShortBreak;
    public TimeSpan LongBreakDuration { get; set; } = DefaultLongBreak;
    public int SessionsBeforeLongBreak { get; set; } = 4;

    /// <summary>Raised exactly once when a phase reaches its deadline. Subscribers
    /// decide presentation (island notice, optional chime).</summary>
    public event Action<PomodoroPhase>? PhaseCompleted;

    public TimeSpan PhaseDurationFor(PomodoroPhase phase) => phase switch
    {
        PomodoroPhase.Focus => FocusDuration,
        PomodoroPhase.ShortBreak => ShortBreakDuration,
        PomodoroPhase.LongBreak => LongBreakDuration,
        _ => TimeSpan.Zero,
    };

    public void StartFocus() => Begin(PomodoroPhase.Focus);
    public void StartBreak()
        => Begin(_completedFocusSessions > 0 && _completedFocusSessions % SessionsBeforeLongBreak == 0
            ? PomodoroPhase.LongBreak
            : PomodoroPhase.ShortBreak);

    /// <summary>Pause: freeze the deadline by stopping the stopwatch.</summary>
    public void Pause()
    {
        if (_phase == PomodoroPhase.Idle || !_runningSince.IsRunning) return;
        _runningSince.Stop();
    }

    public void Resume()
    {
        if (_phase == PomodoroPhase.Idle || _runningSince.IsRunning) return;
        _runningSince.Start();
    }

    /// <summary>Reset to Idle. Session count is preserved unless <paramref name="clearSessions"/>.</summary>
    public void Reset(bool clearSessions = false)
    {
        _phase = PomodoroPhase.Idle;
        _runningSince.Reset();
        _completionReported = true;
        if (clearSessions) _completedFocusSessions = 0;
    }

    public void SkipToNextPhase()
    {
        if (_phase == PomodoroPhase.Idle) { StartFocus(); return; }
        if (_phase == PomodoroPhase.Focus) CompletePhase(PomodoroPhase.Focus);
        else StartFocus();
    }

    private void Begin(PomodoroPhase phase)
    {
        _phase = phase;
        _phaseDuration = PhaseDurationFor(phase);
        _completionReported = false;
        _runningSince.Restart();
    }

    private void CompletePhase(PomodoroPhase finished)
    {
        if (finished == PomodoroPhase.Focus) _completedFocusSessions++;
        StartBreak();
    }

    /// <summary>Remaining time in the current phase. Zero (clamped) when elapsed.</summary>
    public TimeSpan Remaining()
    {
        if (_phase == PomodoroPhase.Idle) return TimeSpan.Zero;
        var elapsed = _runningSince.Elapsed;
        var remaining = _phaseDuration - elapsed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>0..1 progress through the current phase.</summary>
    public double Progress()
    {
        if (_phase == PomodoroPhase.Idle || _phaseDuration <= TimeSpan.Zero) return 0;
        return Math.Clamp(_runningSince.Elapsed / _phaseDuration, 0, 1);
    }

    /// <summary>
    /// Poll (1 Hz from the UI tick). Fires <see cref="PhaseCompleted"/> once when the
    /// deadline passes and auto-advances into the next phase. Safe to call when idle.
    /// </summary>
    public void Tick()
    {
        if (_phase == PomodoroPhase.Idle || _completionReported) return;
        if (Remaining() > TimeSpan.Zero) return;

        _completionReported = true;
        var finished = _phase;
        PhaseCompleted?.Invoke(finished);
        // Auto-advance: after focus → break; after break → focus.
        if (finished == PomodoroPhase.Focus) CompletePhase(PomodoroPhase.Focus);
        else StartFocus();
    }

    /// <summary>Observable state snapshot for UI binding (no live objects).</summary>
    public (PomodoroPhase Phase, TimeSpan Remaining, double Progress, int Sessions, bool Running) Snapshot()
        => (_phase, Remaining(), Progress(), _completedFocusSessions, IsRunning);
}
