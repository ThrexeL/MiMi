namespace MiMi.Core;

/// <summary>
/// Drag-away revert policy for the file-shelf drag session: when an Explorer drag
/// leaves the island without dropping, the shelf view stays for a short grace
/// period and then the previous island state returns. A new drag cancels the
/// revert. Pure and testable.
/// </summary>
public sealed class DragRevertPolicy
{
    public TimeSpan GracePeriod { get; set; } = TimeSpan.FromSeconds(1.5);

    private DateTimeOffset? _leaveAt;
    private bool _active;

    public bool DragActive => _active;

    /// <summary>Drag entered (or re-entered): the shelf stays, any pending revert cancels.</summary>
    public void Enter(DateTimeOffset now)
    {
        _active = true;
        _leaveAt = null;
    }

    /// <summary>Drag left without dropping: schedule the revert.</summary>
    public void Leave(DateTimeOffset now)
    {
        if (!_active) return;
        _leaveAt = now;
    }

    /// <summary>A drop landed: the drag session ends, the shelf stays open (caller
    /// decides how long).</summary>
    public void Dropped() => _active = false;

    /// <summary>True when the grace period has elapsed and the shelf should return
    /// to the previous state. Call on the regular tick.</summary>
    public bool ShouldRevert(DateTimeOffset now)
    {
        if (_leaveAt is { } left && now - left >= GracePeriod)
        {
            _active = false;
            _leaveAt = null;
            return true;
        }
        return false;
    }
}
