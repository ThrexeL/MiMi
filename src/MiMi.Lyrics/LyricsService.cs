using System.Collections.Concurrent;

namespace MiMi.Lyrics;

/// <summary>
/// Bounded in-memory lyrics cache keyed by normalized track identity. In-memory only:
/// lyrics are public data but re-fetching after a restart is harmless and keeps the
/// disk write-free.
/// </summary>
public sealed class LyricsCache
{
    public const int Capacity = 64;
    private readonly ConcurrentDictionary<string, LyricsResult> _cache = new();
    private ConcurrentQueue<string> _order = new();

    public bool TryGet(LyricsQuery query, out LyricsResult? result)
    {
        if (_cache.TryGetValue(query.CacheKey, out var hit))
        {
            result = hit;
            // A cached miss is stored as a sentinel record with id -1 and no lyrics.
            return true;
        }
        result = null;
        return false;
    }

    public void Put(LyricsQuery query, LyricsResult? result)
    {
        var stored = result ?? new LyricsResult
        {
            TrackName = query.TrackName,
            ArtistName = query.ArtistName,
            SyncedLines = null,
            PlainLyrics = null,
            Instrumental = false,
            RecordId = -1,
        };

        if (!_cache.TryAdd(query.CacheKey, stored)) return;
        _order.Enqueue(query.CacheKey);
        while (_order.Count > Capacity && _order.TryDequeue(out var oldest))
            _cache.TryRemove(oldest, out _);
    }

    public void Clear() { _cache.Clear(); _order = new ConcurrentQueue<string>(); }
    public int Count => _cache.Count;
}

/// <summary>
/// Orchestrates lyrics for the currently playing track: caches, deduplicates,
/// cancels obsolete fetches when the track changes, and exposes the current line for
/// a playback position via the stateless LRC index lookup.
/// </summary>
public sealed class LyricsService : IDisposable
{
    private readonly LrcLibClient _client;
    private readonly LyricsCache _cache = new();
    private CancellationTokenSource? _fetchCts;

    private LyricsQuery _currentQuery = new("", "", "", 0);
    private LyricsResult? _current;
    private DateTimeOffset _lastFetchUtc;

    public LyricsService(LrcLibClient? client = null) => _client = client ?? new LrcLibClient();

    public LyricsState State { get; private set; } = LyricsState.Idle;
    public LyricsResult? Current => _current;
    public IReadOnlyList<LrcLine>? CurrentLines => _current?.SyncedLines;

    /// <summary>Raised on a worker thread whenever the lyrics state may have changed.</summary>
    public event Action? Changed;

    /// <summary>Called on every media snapshot. Same track → no-op (no refetch churn).</summary>
    public void OnTrackChanged(LyricsQuery query)
    {
        if (query.CacheKey == _currentQuery.CacheKey) return;
        if (string.IsNullOrWhiteSpace(query.TrackName))
        {
            _currentQuery = query;
            _current = null;
            State = LyricsState.Idle;
            Changed?.Invoke();
            return;
        }

        _currentQuery = query;
        _current = null;
        _fetchCts?.Cancel();
        _fetchCts = new CancellationTokenSource();

        if (_cache.TryGet(query, out var cached))
        {
            Apply(cached);
            return;
        }

        State = LyricsState.Loading;
        Changed?.Invoke();
        _ = FetchAsync(query, _fetchCts.Token);
    }

    private async Task FetchAsync(LyricsQuery query, CancellationToken ct)
    {
        try
        {
            // Light pacing: never more than one fetch per second, so rapid track skips
            // cannot hammer LRCLIB.
            var since = DateTimeOffset.UtcNow - _lastFetchUtc;
            if (since < TimeSpan.FromSeconds(1))
                await Task.Delay(TimeSpan.FromSeconds(1) - since, ct);

            _lastFetchUtc = DateTimeOffset.UtcNow;
            var result = await _client.GetAsync(query, ct);
            if (ct.IsCancellationRequested || query.CacheKey != _currentQuery.CacheKey) return;
            _cache.Put(query, result);
            if (query.CacheKey != _currentQuery.CacheKey) return;
            Apply(result);
        }
        catch (OperationCanceledException) { /* superseded by a newer track */ }
        catch (Exception)
        {
            if (query.CacheKey == _currentQuery.CacheKey && !_disposed)
            {
                _current = null;
                State = LyricsState.Unavailable;
                Changed?.Invoke();
            }
        }
    }

    private void Apply(LyricsResult? result)
    {
        _current = result;
        State = result is null ? LyricsState.Unavailable
            : result.Instrumental ? LyricsState.Instrumental
            : result.HasSynced ? LyricsState.Synced
            : string.IsNullOrWhiteSpace(result.PlainLyrics) ? LyricsState.Unavailable
            : LyricsState.Plain;
        Changed?.Invoke();
    }

    private bool _disposed;
    public void Dispose()
    {
        _disposed = true;
        _fetchCts?.Cancel();
        _fetchCts?.Dispose();
    }
}
