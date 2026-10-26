using System.Collections.Concurrent;

namespace MiMi.Core;

/// <summary>
/// Bounded set of recently seen event ids used to drop duplicate agent events
/// (providers may retry or replay). Fixed capacity, oldest entries evicted first.
/// </summary>
public sealed class DedupStore
{
    private readonly ConcurrentDictionary<string, byte> _seen = new();
    private ConcurrentQueue<string> _order = new();
    private readonly int _capacity;

    public DedupStore(int capacity = 1024) => _capacity = capacity;

    /// <summary>Records the id. Returns true the first time it is seen, false afterwards.</summary>
    public bool TryRecord(string id)
    {
        if (string.IsNullOrEmpty(id)) return true; // nothing to dedup on
        if (!_seen.TryAdd(id, 0)) return false;

        _order.Enqueue(id);
        while (_order.Count > _capacity && _order.TryDequeue(out var old))
            _seen.TryRemove(old, out _);
        return true;
    }

    public int Count => _seen.Count;
}
