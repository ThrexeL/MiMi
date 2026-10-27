using System.Collections.Concurrent;

namespace MiMi.Core;

/// <summary>Island display priority. Lower value = more urgent.</summary>
public enum IslandPriority
{
    Approval = 0,
    CriticalSystem = 1,
    AgentFailure = 2,
    AgentCompletion = 3,
    UserPanel = 3,        // explicitly opened utility panel (same rank as completions,
                          // but pinned content at this level only yields to approvals
                          // and failures)
    Utility = 4,          // running utilities (timer counting down)
    Media = 5,
    SystemInfo = 6,
    Idle = 7,
}

/// <summary>A transient piece of content the island should show for a limited time.</summary>
public sealed class IslandAlert
{
    public required string Id { get; init; }
    public required IslandPriority Priority { get; init; }
    public required object Content { get; init; }
    /// <summary>Null = sticky until explicitly cleared (e.g. a pending approval).</summary>
    public TimeSpan? Duration { get; init; }
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Central island state manager: decides what the island currently shows.
/// Rules implemented here (per spec §7):
///  - highest-priority active alert wins; approvals outrank media;
///  - duplicate alerts (same id) do not re-trigger or restart timers;
///  - when a transient alert expires the previous pinned state returns;
///  - media state changes never clear a pending approval.
/// The UI subscribes to <see cref="EffectiveContentChanged"/>.
/// </summary>
public sealed class IslandStateManager : IDisposable
{
    private readonly ConcurrentDictionary<string, IslandAlert> _active = new();
    private readonly object _gate = new();
    private readonly Timer _sweeper;
    private object? _pinnedContent;            // e.g. resting clock view-model
    private IslandPriority _pinnedPriority = IslandPriority.Idle;
    private object? _lastEffective;

    public event Action<object?>? EffectiveContentChanged;

    public IslandStateManager()
    {
        // Sweep expired transients twice a second; no-op when nothing is active.
        _sweeper = new Timer(_ => Sweep(), null, 500, 500);
    }

    public void SetPinned(IslandPriority priority, object content)
    {
        lock (_gate)
        {
            _pinnedPriority = priority;
            _pinnedContent = content;
        }
        Recompute();
    }

    /// <summary>Pushes an alert. Returns false when it was a duplicate already active.</summary>
    public bool Push(IslandAlert alert)
    {
        if (!_active.TryAdd(alert.Id, alert)) return false;
        Recompute();
        return true;
    }

    public void Clear(string alertId)
    {
        if (_active.TryRemove(alertId, out _)) Recompute();
    }

    public void ClearAll() { _active.Clear(); Recompute(); }

    /// <summary>True when an approval alert is currently the effective content.</summary>
    public bool IsShowingAlert(string alertId) => _active.ContainsKey(alertId) && ReferenceEquals(CurrentAlert?.Id, alertId);

    private void Sweep()
    {
        var now = DateTimeOffset.UtcNow;
        var changed = false;
        foreach (var (id, alert) in _active)
        {
            if (alert.Duration is { } d && now - alert.CreatedUtc >= d)
            {
                changed |= _active.TryRemove(id, out _);
            }
        }
        if (changed) Recompute();
    }

    private IslandAlert? CurrentAlert
    {
        get
        {
            IslandAlert? best = null;
            foreach (var a in _active.Values)
            {
                if (best is null || a.Priority < best.Priority) best = a;
            }
            return best;
        }
    }

    private void Recompute()
    {
        object? effective;
        lock (_gate)
        {
            var alert = CurrentAlert;
            // Alerts outrank the pinned state unless the pinned state is strictly higher priority.
            if (alert is not null && (int)alert.Priority <= (int)_pinnedPriority)
                effective = alert.Content;
            else
                effective = _pinnedContent;

            // Compare and record inside the lock so a sweeper-thread Recompute can never
            // publish a stale snapshot over a newer one that landed between the two steps.
            if (ReferenceEquals(effective, _lastEffective)) return;
            _lastEffective = effective;
        }
        EffectiveContentChanged?.Invoke(effective);
    }

    public void Dispose() => _sweeper.Dispose();
}
