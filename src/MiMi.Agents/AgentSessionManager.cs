using System.Collections.Concurrent;

namespace MiMi.Agents;

/// <summary>Coarse activity state of one agent session, derived only from confirmed
/// hook events. Hooks cannot reveal model thinking — silence never becomes "finished".</summary>
public enum SessionActivity
{
    Started,
    Working,          // prompt submitted / general activity
    ExecutingTool,
    AwaitingApproval,
    TurnFinished,
    Interrupted,
    Error,
    Ended,
    Unknown,          // stale: no confirmed events for a while
}

/// <summary>One short, confirmed history entry for a session. Never contains prompt
/// text, command text, credentials, or transcript content.</summary>
public sealed record AgentTimelineEntry(DateTimeOffset TimestampUtc, string Text, string Kind);

/// <summary>One tracked agent session. Identity is (Provider, SessionId) — the project
/// name is display metadata only, never a key, because several sessions can share one.</summary>
public sealed class AgentSession
{
    public required string Provider { get; init; }
    public required string SessionId { get; init; }
    public string Project { get; internal set; } = "";
    public SessionActivity State { get; internal set; } = SessionActivity.Started;
    public DateTimeOffset LastEventUtc { get; internal set; } = DateTimeOffset.UtcNow;
    public string LastToolName { get; internal set; } = "";
    public string LastSummary { get; internal set; } = "";
    public string TurnId { get; internal set; } = "";
    public string SubagentId { get; internal set; } = "";
    public string LastEventId { get; internal set; } = "";
    public long LastSequence { get; internal set; } = -1;
    public bool Ended { get; internal set; }
    public bool Stale { get; internal set; }
    public IReadOnlyList<string> PendingApprovalIds => _pendingApprovals;
    internal readonly List<string> _pendingApprovals = new();

    /// <summary>Bounded, newest-last timeline shown in the expanded activity panel.</summary>
    public IReadOnlyList<AgentTimelineEntry> History => _history;
    internal readonly List<AgentTimelineEntry> _history = new();
}

/// <summary>
/// Centralized multi-agent session tracking. Feeds the compact indicator and the
/// expanded activity panel; approvals remain owned by the ApprovalStore (this class
/// only mirrors pending ids for display correlation).
///
/// Reliability rules:
///  - events are correlated by (provider, session_id); duplicates are dropped upstream
///    by the bridge, and late events for a session that has Ended cannot resurrect it;
///  - event `sequence` (when provided) guards against out-of-order turns per session;
///  - sessions without confirmed events for <see cref="StaleAfter"/> flip to Unknown
///    ("status unknown") — never to "finished";
///  - history is bounded: ended sessions beyond <see cref="EndedHistoryLimit"/> are pruned.
/// </summary>
public sealed class AgentSessionManager : IDisposable
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(90);
    public const int EndedHistoryLimit = 20;
    public const int TimelineLimit = 12;
    private static readonly TimeSpan ToolEntrySpacing = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<string, AgentSession> _sessions = new();
    private readonly Timer _sweeper;
    private bool _disposed;

    /// <summary>Raised on a worker thread whenever any session's display state may have
    /// changed. Subscribers marshal to their own dispatcher.</summary>
    public event Action<AgentSession>? SessionChanged;

    public AgentSessionManager()
    {
        _sweeper = new Timer(_ => SweepStale(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    private static string Key(string provider, string sessionId) => $"{provider}|{sessionId}";

    public AgentSession? Get(string provider, string sessionId) =>
        _sessions.TryGetValue(Key(provider, sessionId), out var s) ? s : null;

    public IReadOnlyList<AgentSession> All =>
        _sessions.Values.OrderBy(s => s.Provider).ThenBy(s => s.SessionId).ToList();

    /// <summary>Sessions worth showing in the compact indicator/panel: live ones, plus
    /// recently finished ones (turn finished / error / interrupted) until they age out.</summary>
    public IReadOnlyList<AgentSession> Active => All.Where(s => IsVisible(s, DateTimeOffset.UtcNow)).ToList();

    /// <summary>Finished sessions stay listed briefly; ended sessions are terminal and
    /// only remain while still "recent" (bounded history is pruned separately).</summary>
    public static readonly TimeSpan FinishedVisibleFor = TimeSpan.FromMinutes(5);

    public static bool IsVisible(AgentSession session, DateTimeOffset asOf)
    {
        if (session.State == SessionActivity.Ended) return false;
        // Finished / failed / interrupted sessions stay visible briefly, then age out.
        if (session.State is SessionActivity.TurnFinished or SessionActivity.Error or SessionActivity.Interrupted)
            return asOf - session.LastEventUtc <= FinishedVisibleFor;
        return true; // live session (stale sessions remain visible as "status unknown")
    }

    public int WorkingCount =>
        _sessions.Values.Count(s => !s.Ended && s.State is SessionActivity.Started
            or SessionActivity.Working or SessionActivity.ExecutingTool or SessionActivity.AwaitingApproval);

    public int PendingApprovalCount =>
        _sessions.Values.Sum(s => s._pendingApprovals.Count);

    /// <summary>Applies a normalized event to session state. Late events (older activity
    /// or after End) never resurrect or rewind a session.</summary>
    public AgentSession Apply(AgentEvent ev)
    {
        var key = Key(ev.Provider, ev.SessionId);
        var session = _sessions.GetOrAdd(key, _ => new AgentSession
        {
            Provider = ev.Provider,
            SessionId = ev.SessionId,
        });

        lock (session)
        {
            // Out-of-order guard: a strictly older sequence for the same turn cannot
            // overwrite newer state. Events without sequence are always accepted.
            if (ev.Metadata.TryGetValue("sequence", out var seqRaw)
                && long.TryParse(seqRaw, out var seq) && seq >= 0)
            {
                if (session.LastSequence >= 0 && seq < session.LastSequence)
                    return session;
                session.LastSequence = seq;
            }

            // Late events after session end: keep the session Ended; record nothing else.
            if (session.Ended && ev.Type != AgentEventTypes.SessionEnded)
                return session;

            session.Project = ev.Project.Length > 0 ? ev.Project : session.Project;
            session.LastEventUtc = DateTimeOffset.UtcNow;
            session.LastEventId = ev.EventId;
            session.LastSummary = ev.Message;
            session.TurnId = ev.Metadata.TryGetValue("turn_id", out var turn) ? turn : session.TurnId;
            session.SubagentId = ev.Metadata.TryGetValue("subagent_id", out var sub) ? sub : session.SubagentId;

            switch (ev.Type)
            {
                case AgentEventTypes.Started:
                    session.State = SessionActivity.Started;
                    session.Ended = false;
                    break;
                case AgentEventTypes.PromptSubmitted:
                    session.State = SessionActivity.Working;
                    session.Ended = false;
                    break;
                case AgentEventTypes.Status:
                    if (session.State is SessionActivity.Started or SessionActivity.Unknown)
                        session.State = SessionActivity.Working;
                    break;
                case AgentEventTypes.ToolStarted or AgentEventTypes.SubagentStarted:
                    session.State = SessionActivity.ExecutingTool;
                    session.LastToolName = ev.Metadata.TryGetValue("tool", out var tool) ? tool : "";
                    break;
                case AgentEventTypes.ToolFinished:
                    session.State = SessionActivity.Working;
                    break;
                case AgentEventTypes.ApprovalRequested:
                    session.State = SessionActivity.AwaitingApproval;
                    if (ev.ApprovalRequestId is { } ap && !session._pendingApprovals.Contains(ap))
                        session._pendingApprovals.Add(ap);
                    break;
                case AgentEventTypes.ApprovalResolved:
                    if (ev.ApprovalRequestId is { } resolved)
                        session._pendingApprovals.Remove(resolved);
                    if (session._pendingApprovals.Count == 0 && session.State == SessionActivity.AwaitingApproval)
                        session.State = SessionActivity.Working;
                    break;
                case AgentEventTypes.Completed or AgentEventTypes.TurnFinished:
                    session.State = SessionActivity.TurnFinished;
                    break;
                case AgentEventTypes.Failed or AgentEventTypes.ToolFailed:
                    session.State = SessionActivity.Error;
                    break;
                case AgentEventTypes.Interrupted:
                    session.State = SessionActivity.Interrupted;
                    break;
                case AgentEventTypes.Cancelled:
                    session.State = SessionActivity.Interrupted;
                    break;
                case AgentEventTypes.SessionEnded:
                    session.State = SessionActivity.Ended;
                    session.Ended = true;
                    session._pendingApprovals.Clear();
                    break;
                case AgentEventTypes.SubagentStopped:
                    if (session.State == SessionActivity.ExecutingTool)
                        session.State = SessionActivity.Working;
                    break;
            }

            session.Stale = false;
            RecordHistory(session, ev);
        }

        SessionChanged?.Invoke(session);
        return session;
    }

    /// <summary>Mirrors an approval resolution that did not arrive as an agent event
    /// (the normal case: the user clicked in MiMi). Keeps session display in sync.</summary>
    public void MarkApprovalResolved(string provider, string sessionId, string approvalRequestId)
    {
        var session = Get(provider, sessionId);
        if (session is null) return;
        lock (session)
        {
            session._pendingApprovals.Remove(approvalRequestId);
            if (session._pendingApprovals.Count == 0 && session.State == SessionActivity.AwaitingApproval)
            {
                session.State = SessionActivity.Working;
                session.LastEventUtc = DateTimeOffset.UtcNow;
            }
        }
        SessionChanged?.Invoke(session);
    }

    /// <summary>Adds one short confirmed event. Consecutive duplicates are collapsed and
    /// rapid tool churn replaces the newest tool line instead of growing the list.</summary>
    private static void RecordHistory(AgentSession session, AgentEvent ev)
    {
        var text = Describe(ev);
        if (text is null) return;

        var now = session.LastEventUtc;
        if (session._history.Count > 0)
        {
            var last = session._history[^1];
            if (string.Equals(last.Text, text, StringComparison.Ordinal)) return;
            if (text.StartsWith("Running ", StringComparison.Ordinal)
                && last.Text.StartsWith("Running ", StringComparison.Ordinal)
                && now - last.TimestampUtc < ToolEntrySpacing)
            {
                session._history[^1] = new AgentTimelineEntry(last.TimestampUtc, text, "tool");
                return;
            }
        }

        session._history.Add(new AgentTimelineEntry(now, text, KindOf(ev)));
        if (session._history.Count > TimelineLimit)
            session._history.RemoveRange(0, session._history.Count - TimelineLimit);
    }

    /// <summary>Fixed, content-free wording. Provider messages, prompts, and commands are
    /// deliberately never copied into history.</summary>
    private static string? Describe(AgentEvent ev) => ev.Type switch
    {
        AgentEventTypes.Started => "Started",
        AgentEventTypes.PromptSubmitted => "Received a task",
        AgentEventTypes.ToolStarted => ToolText(ev),
        AgentEventTypes.ToolFinished => "Tool finished",
        AgentEventTypes.ToolFailed => "Tool failed",
        AgentEventTypes.ApprovalRequested => "Approval requested",
        AgentEventTypes.ApprovalResolved => "Approval resolved",
        AgentEventTypes.TurnFinished or AgentEventTypes.Completed => "Turn completed",
        AgentEventTypes.Failed => "Error",
        AgentEventTypes.Interrupted or AgentEventTypes.Cancelled => "Interrupted",
        AgentEventTypes.SessionEnded => "Session ended",
        AgentEventTypes.SubagentStarted => "Subagent started",
        AgentEventTypes.SubagentStopped => "Subagent stopped",
        _ => null, // Status and unknown types are noise, not history
    };

    private static string ToolText(AgentEvent ev)
    {
        if (!ev.Metadata.TryGetValue("tool", out var tool) || string.IsNullOrWhiteSpace(tool))
            return "Running tool";
        tool = tool.Trim();
        if (tool.Length > 32) tool = tool[..32] + "…";
        return "Running " + tool;
    }

    private static string KindOf(AgentEvent ev) => ev.Type switch
    {
        AgentEventTypes.ToolStarted or AgentEventTypes.ToolFinished or AgentEventTypes.ToolFailed => "tool",
        AgentEventTypes.ApprovalRequested or AgentEventTypes.ApprovalResolved => "approval",
        AgentEventTypes.Failed or AgentEventTypes.Interrupted or AgentEventTypes.Cancelled => "error",
        _ => "lifecycle",
    };

    private void SweepStale() => SweepStale(DateTimeOffset.UtcNow);

    /// <summary>Time-injectable staleness sweep (testable without waiting).</summary>
    public void SweepStale(DateTimeOffset asOf)
    {
        if (_disposed) return;
        var now = asOf;
        foreach (var session in _sessions.Values)
        {
            var changed = false;
            lock (session)
            {
                if (session.Ended) continue; // ended is terminal
                // Long silence while "working" becomes Unknown — never "finished".
                if (!session.Stale && now - session.LastEventUtc > StaleAfter
                    && session.State is not SessionActivity.AwaitingApproval)
                {
                    session.Stale = true;
                    if (session.State is SessionActivity.Started or SessionActivity.Working
                        or SessionActivity.ExecutingTool)
                        session.State = SessionActivity.Unknown;
                    changed = true;
                }
            }
            if (changed) SessionChanged?.Invoke(session);
        }

        // Bound ended history.
        var ended = _sessions.Values.Where(s => s.Ended)
            .OrderByDescending(s => s.LastEventUtc).Skip(EndedHistoryLimit).ToList();
        foreach (var s in ended)
            _sessions.TryRemove(Key(s.Provider, s.SessionId), out _);
    }

    public void Dispose()
    {
        _disposed = true;
        _sweeper.Dispose();
    }
}
