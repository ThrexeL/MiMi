namespace MiMi.Core;

/// <summary>
/// Bounded set of recently seen event ids used to drop duplicate agent events
/// (providers may retry or replay). Fixed capacity, oldest entries evicted first.
/// </summary>
public sealed class DedupStore
{
    private readonly Dictionary<string, long> _seen = new(StringComparer.Ordinal);
    private readonly Queue<(string Id, long Version)> _order = new();
    private readonly object _gate = new();
    private readonly int _capacity;
    private long _version;

    public DedupStore(int capacity = 1024) => _capacity = Math.Max(1, capacity);

    /// <summary>Records the id. Returns true the first time it is seen, false afterwards.</summary>
    public bool TryRecord(string id)
    {
        if (string.IsNullOrEmpty(id)) return true; // nothing to dedup on
        lock (_gate)
        {
            if (_seen.ContainsKey(id)) return false;
            var version = ++_version;
            _seen[id] = version;
            _order.Enqueue((id, version));
            while (_order.Count > _capacity && _order.TryDequeue(out var old))
            {
                // A forgotten id may have been retried and recorded with a newer
                // version. Its stale queue entry must not evict the fresh record.
                if (_seen.TryGetValue(old.Id, out var current) && current == old.Version)
                    _seen.Remove(old.Id);
            }
            return true;
        }
    }

    public int Count
    {
        get { lock (_gate) return _seen.Count; }
    }

    /// <summary>Forgets an id when delivery was rejected before publication, so the
    /// same provider payload can be retried after a transient capacity failure.</summary>
    public void Forget(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        lock (_gate) _seen.Remove(id);
    }
}
