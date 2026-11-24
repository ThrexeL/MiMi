using System.Collections.Concurrent;

namespace MiMi.Agents;

/// <summary>A pending or resolved approval request originating from an agent.</summary>
public sealed class ApprovalRequest
{
    public required string ApprovalRequestId { get; init; }
    public required string Provider { get; init; }
    public required string SessionId { get; init; }
    public string Project { get; init; } = "";
    /// <summary>e.g. "Execute Command", "Edit File". Display-only.</summary>
    public string ActionKind { get; init; } = "";
    /// <summary>e.g. "npm run build". Display-only; MiMi never executes this.</summary>
    public string Command { get; init; } = "";
    public string Message { get; init; } = "";
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public TimeSpan TimeToLive { get; init; } = TimeSpan.FromMinutes(5);

    public ApprovalDecision? Decision { get; private set; }
    public string ResolvedBy { get; private set; } = "";
    public DateTimeOffset? ResolvedUtc { get; private set; }

    public bool IsExpired => Decision is null && DateTimeOffset.UtcNow - CreatedUtc >= TimeToLive;
    public bool IsPending => Decision is null && !IsExpired;

    /// <summary>
    /// Records a decision exactly once. Returns false when already resolved
    /// (double-clicks, duplicate events) or expired (acting on it would be stale).
    /// </summary>
    public bool TryResolve(ApprovalDecision decision, string resolvedBy)
    {
        if (decision == ApprovalDecision.Expired) return false; // expiry is system-driven only
        if (!IsPending) return false;
        Decision = decision;
        ResolvedBy = resolvedBy;
        ResolvedUtc = DateTimeOffset.UtcNow;
        return true;
    }

    /// <summary>System-side expiry. Returns true if this call expired the request.</summary>
    public bool TryExpire()
    {
        if (Decision is not null || !IsExpired) return false;
        Decision = ApprovalDecision.Expired;
        ResolvedUtc = DateTimeOffset.UtcNow;
        return true;
    }
}

/// <summary>
/// Thread-safe store of pending approvals. Guarantees:
///  - an approval stays visible until resolved or TTL-expired;
///  - expired approvals can never be approved/denied through the UI;
///  - each approval resolves at most once, so responses are never replayed to agents.
/// </summary>
public sealed class ApprovalStore : IDisposable
{
    public const int MaxPendingApprovals = 64;
    public const int MaxRetainedApprovals = 256;

    private readonly ConcurrentDictionary<string, ApprovalRequest> _requests = new();
    private readonly object _registerGate = new();
    private readonly Timer _sweeper;
    private readonly TimeSpan _defaultTtl;
    private readonly int _maxPendingApprovals;

    public event Action<ApprovalRequest>? Added;
    public event Action<ApprovalRequest>? Resolved;

    public ApprovalStore(TimeSpan? defaultTtl = null, int maxPendingApprovals = MaxPendingApprovals)
    {
        if (maxPendingApprovals is <= 0 or > MaxRetainedApprovals)
            throw new ArgumentOutOfRangeException(nameof(maxPendingApprovals));
        _defaultTtl = defaultTtl ?? TimeSpan.FromMinutes(5);
        _maxPendingApprovals = maxPendingApprovals;
        _sweeper = new Timer(_ => Sweep(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public TimeSpan DefaultTtl => _defaultTtl;

    /// <summary>Registers a pending approval from an event. Returns null for duplicate
    /// ids or when the pending-approval cap has been reached.</summary>
    public ApprovalRequest? Register(AgentEvent ev, TimeSpan? ttl = null)
    {
        if (ev.ApprovalRequestId is not { Length: > 0 } id) return null;

        var request = new ApprovalRequest
        {
            ApprovalRequestId = id,
            Provider = ev.Provider,
            SessionId = ev.SessionId,
            Project = ev.Project,
            ActionKind = ev.Metadata.TryGetValue("action_kind", out var kind) ? kind : "",
            Command = ev.Metadata.TryGetValue("command", out var cmd) ? cmd : "",
            Message = ev.Message,
            // Sender clocks are advisory. TTL starts when MiMi receives the request,
            // never when a remote producer claims it was created.
            CreatedUtc = DateTimeOffset.UtcNow,
            TimeToLive = ttl ?? _defaultTtl,
        };

        lock (_registerGate)
        {
            TrimResolvedLocked(DateTimeOffset.UtcNow);
            if (_requests.ContainsKey(id)) return null; // duplicate request id
            if (_requests.Values.Count(r => r.IsPending) >= _maxPendingApprovals) return null;
            if (!_requests.TryAdd(id, request)) return null; // concurrent duplicate
        }
        Added?.Invoke(request);
        return request;
    }

    public ApprovalRequest? Get(string approvalRequestId) =>
        _requests.TryGetValue(approvalRequestId, out var r) ? r : null;

    public IReadOnlyList<ApprovalRequest> Pending =>
        _requests.Values.Where(r => r.IsPending).OrderBy(r => r.CreatedUtc).ToList();

    /// <summary>
    /// Attempts a UI decision. Returns false (without side effects) when the request is
    /// unknown, already resolved, or expired.
    /// </summary>
    public bool TryDecide(string approvalRequestId, ApprovalDecision decision, string resolvedBy)
    {
        if (!_requests.TryGetValue(approvalRequestId, out var request)) return false;
        if (!request.TryResolve(decision, resolvedBy)) return false;
        Resolved?.Invoke(request);
        return true;
    }

    private void Sweep()
    {
        foreach (var request in _requests.Values)
        {
            if (request.TryExpire()) Resolved?.Invoke(request);
        }
        // Keep resolved entries for a while (correlation/debugging), prune old ones.
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
        lock (_registerGate)
        {
            foreach (var (id, request) in _requests)
            {
                if (request.ResolvedUtc is { } at && at < cutoff) _requests.TryRemove(id, out _);
            }
            TrimResolvedLocked(DateTimeOffset.UtcNow);
        }
    }

    private void TrimResolvedLocked(DateTimeOffset now)
    {
        var cutoff = now - TimeSpan.FromHours(1);
        foreach (var (id, request) in _requests)
        {
            if (request.ResolvedUtc is { } at && at < cutoff) _requests.TryRemove(id, out _);
        }

        var excess = _requests.Count - MaxRetainedApprovals;
        if (excess <= 0) return;
        foreach (var (id, _) in _requests
                     .Where(pair => pair.Value.Decision is not null)
                     .OrderBy(pair => pair.Value.ResolvedUtc)
                     .Take(excess))
            _requests.TryRemove(id, out _);
    }

    public void Dispose() => _sweeper.Dispose();
}

/// <summary>
/// Minimal in-process event bus. Bridge, providers, and UI communicate through it;
/// nothing holds a reference to anything else's concrete type.
/// </summary>
public sealed class AgentEventBus
{
    private readonly List<Subscription> _subscriptions = new();
    private readonly object _gate = new();

    private sealed class Subscription(Action<AgentEvent> handler)
    {
        public readonly Action<AgentEvent> Handler = handler;
        public bool Active = true;
    }

    public event Action<AgentEvent>? EventReceived;

    public IDisposable Subscribe(Action<AgentEvent> handler)
    {
        var sub = new Subscription(handler);
        lock (_gate) _subscriptions.Add(sub);
        return new Unsubscriber(this, sub);
    }

    public void Publish(AgentEvent ev)
    {
        Subscription[] snapshot;
        lock (_gate) snapshot = _subscriptions.Where(s => s.Active).ToArray();
        foreach (var sub in snapshot)
        {
            try { sub.Handler(ev); }
            catch { /* one bad subscriber must not starve the pipeline */ }
        }
        try { EventReceived?.Invoke(ev); }
        catch { /* same */ }
    }

    private sealed class Unsubscriber(AgentEventBus bus, Subscription sub) : IDisposable
    {
        public void Dispose()
        {
            lock (bus._gate) sub.Active = false;
        }
    }
}
