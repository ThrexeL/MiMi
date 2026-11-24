using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

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
    WaitingForInput,
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
    /// <summary>
    /// Five-minute safety lease for confirmed working or external waiting activity.
    /// Without fresh activity, the session becomes Unknown; silence never means finished.
    /// Pending local approvals remain governed by their own resolution/expiry flow.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);
    public const int EndedHistoryLimit = 20;
    public const int MaxTrackedSessions = 256;
    public const int TimelineLimit = 12;
    private static readonly TimeSpan ToolEntrySpacing = TimeSpan.FromSeconds(2);

    // ValueTuple's string equality is ordinal; delimiters in either field are data.
    private readonly ConcurrentDictionary<(string Provider, string SessionId), AgentSession> _sessions = new();
    private readonly object _sessionGate = new();
    private readonly TimeProvider _timeProvider;
    public static readonly TimeSpan SenderClockSkew = TimeSpan.FromSeconds(30);
    private readonly Timer _sweeper;
    private bool _disposed;

    /// <summary>Raised on a worker thread whenever any session's display state may have
    /// changed. Subscribers marshal to their own dispatcher.</summary>
    public event Action<AgentSession>? SessionChanged;

    public AgentSessionManager(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _sweeper = new Timer(_ => SweepStale(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    private static (string Provider, string SessionId) Key(string provider, string sessionId) => (provider, sessionId);

    /// <summary>Makes room under the hard session cap without dropping a session that
    /// owns a pending approval. Terminal/stale/oldest sessions are preferred.</summary>
    private bool TryEvictOldestUnprotected()
    {
        AgentSession? candidate = null;
        foreach (var session in _sessions.Values
                     .OrderBy(s => s.Ended ? 0 : s.Stale ? 1 : 2)
                     .ThenBy(s => s.LastEventUtc))
        {
            lock (session)
            {
                if (session._pendingApprovals.Count != 0) continue;
                candidate = session;
                break;
            }
        }
        return candidate is not null
               && _sessions.TryRemove(Key(candidate.Provider, candidate.SessionId), out _);
    }

    public AgentSession? Get(string provider, string sessionId) =>
        _sessions.TryGetValue(Key(provider, sessionId), out var s) ? s : null;

    public IReadOnlyList<AgentSession> All =>
        _sessions.Values.OrderBy(s => s.Provider).ThenBy(s => s.SessionId).ToList();

    /// <summary>Sessions worth showing in the compact indicator/panel: live ones, plus
    /// recently finished ones (turn finished / error / interrupted) until they age out.</summary>
    public IReadOnlyList<AgentSession> Active => _sessions.Values
        .Where(s => IsVisible(s, _timeProvider.GetUtcNow()))
        .OrderBy(s => s.State == SessionActivity.AwaitingApproval ? 0
            : s.State is SessionActivity.Started or SessionActivity.Working or SessionActivity.ExecutingTool or SessionActivity.WaitingForInput ? 1
            : s.State == SessionActivity.Unknown ? 3 : 2)
        .ThenByDescending(s => s.LastEventUtc).ToList();

    /// <summary>Finished sessions stay listed briefly; ended sessions are terminal and
    /// only remain while still "recent" (bounded history is pruned separately).</summary>
    public static readonly TimeSpan FinishedVisibleFor = TimeSpan.FromMinutes(2);

    public static bool IsVisible(AgentSession session, DateTimeOffset asOf)
    {
        if (session.State == SessionActivity.Ended) return false;
        if (session.State == SessionActivity.Unknown)
            return asOf - session.LastEventUtc <= StaleAfter + FinishedVisibleFor;
        // Finished / failed / interrupted sessions stay visible briefly, then age out.
        if (session.State is SessionActivity.TurnFinished or SessionActivity.Error or SessionActivity.Interrupted)
            return asOf - session.LastEventUtc <= FinishedVisibleFor;
        return true; // live session (stale sessions remain visible as "status unknown")
    }

    public int WorkingCount =>
        _sessions.Values.Count(s => !s.Ended && !s.Stale && s.State is SessionActivity.Started
            or SessionActivity.Working or SessionActivity.ExecutingTool or SessionActivity.AwaitingApproval or SessionActivity.WaitingForInput);

    public int PendingApprovalCount =>
        _sessions.Values.Sum(s => s._pendingApprovals.Count);

    /// <summary>Applies a normalized event to session state. Late events (older activity
    /// or after End) never resurrect or rewind a session.</summary>
    public AgentSession Apply(AgentEvent ev)
    {
        var now = _timeProvider.GetUtcNow();
        var key = Key(ev.Provider, ev.SessionId);
        var confirmedAt = now;
        // Explicit sender time is preserved in metadata across bridge normalization.
        // Legacy events retain receipt-time semantics; approval TTLs remain ApprovalStore-owned.
        if (ev.Type is not (AgentEventTypes.ApprovalRequested or AgentEventTypes.ApprovalResolved)
            && ev.Metadata.TryGetValue("sender_timestamp", out var senderTime))
        {
            if (!DateTimeOffset.TryParse(senderTime, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var timestamp)
                || timestamp.Offset != TimeSpan.Zero || timestamp > now + SenderClockSkew)
                return Reject("invalid_sender_timestamp");
            if (now - timestamp > StaleAfter)
                return Reject("expired_snapshot");
            confirmedAt = timestamp > now ? now : timestamp;
        }

        AgentSession session;
        lock (_sessionGate)
        {
            if (!_sessions.TryGetValue(key, out var existing)
                && _sessions.Count >= MaxTrackedSessions
                && !TryEvictOldestUnprotected())
                return Reject("session_capacity");
            session = existing ?? _sessions.GetOrAdd(key, _ => new AgentSession
            {
                Provider = ev.Provider,
                SessionId = ev.SessionId,
                State = SessionActivity.Unknown,
                Stale = true,
                LastEventUtc = DateTimeOffset.MinValue,
            });
        }

        lock (session)
        {
            // Ended is terminal, including for explicit activity heartbeats.
            if (session.Ended)
                return Reject("session_ended");

            // Sequence is monotonic across the session, never reset on turn changes.
            // Equal sequences (including conflicting replays) cannot freshen a session.
            long? sequence = null;
            if (ev.Metadata.TryGetValue("sequence", out var seqRaw))
            {
                if (!long.TryParse(seqRaw, NumberStyles.None, CultureInfo.InvariantCulture, out var seq) || seq < 0)
                    return Reject("invalid_sequence");
                if (seq <= session.LastSequence)
                    return Reject("non_increasing_sequence");
                sequence = seq;
            }
            // Sequence orders snapshots even across wall-clock adjustments; sender time
            // still controls freshness, and orders updates only when no sequence is supplied.
            if (sequence is null && ev.Metadata.ContainsKey("sender_timestamp") && confirmedAt < session.LastEventUtc
                && session.LastEventId.Length > 0)
                return Reject("older_snapshot");

            var activityType = ev.Type;
            if (ev.Type == AgentEventTypes.Heartbeat)
            {
                if (ev.Metadata.TryGetValue("activity_type", out var confirmedType))
                {
                    // A heartbeat may confirm activity, never manufacture approvals or end sessions.
                    if (confirmedType is not (AgentEventTypes.Started or AgentEventTypes.PromptSubmitted
                        or AgentEventTypes.Status or AgentEventTypes.ToolStarted or AgentEventTypes.ToolFinished
                        or AgentEventTypes.SubagentStarted or AgentEventTypes.SubagentStopped
                        or AgentEventTypes.WaitingForInput))
                        return Reject("invalid_heartbeat_activity");
                    activityType = confirmedType;
                }
                else if (session.Stale || session.State == SessionActivity.Unknown)
                    return Reject("unconfirmed_heartbeat");
            }
            if (sequence is { } acceptedSequence) session.LastSequence = acceptedSequence;

            session.Project = ev.Project.Length > 0 ? ev.Project : session.Project;
            session.LastEventUtc = confirmedAt;
            session.LastEventId = ev.EventId;
            session.LastSummary = ev.Message;
            session.TurnId = ev.Metadata.TryGetValue("turn_id", out var turn) ? turn : session.TurnId;
            session.SubagentId = ev.Metadata.TryGetValue("subagent_id", out var sub) ? sub : session.SubagentId;

            switch (activityType)
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
                    if (session.State is SessionActivity.Started or SessionActivity.Unknown or SessionActivity.ExecutingTool or SessionActivity.WaitingForInput)
                        session.State = SessionActivity.Working;
                    break;
                case AgentEventTypes.WaitingForInput:
                    session.State = session._pendingApprovals.Count > 0
                        ? SessionActivity.AwaitingApproval : SessionActivity.WaitingForInput;
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

        AgentSession Reject(string reason)
        {
            Trace.TraceWarning("MiMi: agent event rejected ({0})", reason);
            return Get(ev.Provider, ev.SessionId) ?? new AgentSession
            {
                Provider = ev.Provider, SessionId = ev.SessionId,
                State = SessionActivity.Unknown, Stale = true, LastEventUtc = DateTimeOffset.MinValue,
            };
        }
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
                session.LastEventUtc = _timeProvider.GetUtcNow();
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

    private void SweepStale() => SweepStale(_timeProvider.GetUtcNow());

    /// <summary>Time-injectable staleness sweep (testable without waiting).</summary>
    public void SweepStale(DateTimeOffset asOf)
    {
        if (_disposed) return;
        // Called from the background sweeper timer and from the UI's 1 Hz tick; the
        // dictionary is concurrent, but pruning must not interleave with itself.
        if (Interlocked.Exchange(ref _sweeping, 1) == 1) return;
        try { SweepStaleCore(asOf); }
        finally { Interlocked.Exchange(ref _sweeping, 0); }
    }

    private int _sweeping;

    private void SweepStaleCore(DateTimeOffset asOf)
    {
        var now = asOf;
        foreach (var session in _sessions.Values)
        {
            var changed = false;
            lock (session)
            {
                if (session.Ended || session._pendingApprovals.Count > 0) continue;
                // Silence expires the activity lease, not a locally owned approval.
                if (!session.Stale && now - session.LastEventUtc > StaleAfter
                    && session.State is SessionActivity.Started or SessionActivity.Working
                        or SessionActivity.ExecutingTool or SessionActivity.WaitingForInput)
                {
                    session.Stale = true;
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
        foreach (var s in _sessions.Values.Where(s => s.State != SessionActivity.AwaitingApproval
                     && s._pendingApprovals.Count == 0 && !s.Ended && now - s.LastEventUtc > TimeSpan.FromMinutes(15)))
            _sessions.TryRemove(Key(s.Provider, s.SessionId), out _);
    }

    public void Dispose()
    {
        _disposed = true;
        _sweeper.Dispose();
    }
}
