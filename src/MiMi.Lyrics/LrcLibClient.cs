using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace MiMi.Lyrics;

/// <summary>
/// Chooses the best LRCLIB record for a track: exact (case-insensitive, trimmed)
/// track+artist match with the closest duration first, then track-only match, then
/// any candidate with a plain-text match on normalized names. Never picks a record
/// whose track name is clearly a different song.
/// </summary>
public static class LyricsMatchPicker
{
    public static LyricsRecord? Pick(IReadOnlyList<LyricsRecord> candidates, LyricsQuery query)
    {
        if (candidates.Count == 0) return null;

        var track = Normalize(query.TrackName);
        var artist = Normalize(query.ArtistName);
        var duration = query.DurationSeconds;

        LyricsRecord? bestExact = null;
        var bestExactDelta = double.MaxValue;
        LyricsRecord? bestTrack = null;
        var bestTrackDelta = double.MaxValue;

        foreach (var c in candidates)
        {
            var trackMatches = Normalize(c.TrackName) == track;
            var artistMatches = artist.Length == 0 || Normalize(c.ArtistName).Contains(artist)
                || artist.Contains(Normalize(c.ArtistName));

            var delta = duration > 0 && c.Duration > 0 ? Math.Abs(c.Duration - duration) : double.MaxValue;

            if (trackMatches && artistMatches && delta < bestExactDelta)
            {
                bestExact = c;
                bestExactDelta = delta;
            }
            if (trackMatches && delta < bestTrackDelta)
            {
                bestTrack = c;
                bestTrackDelta = delta;
            }
        }

        // A duration mismatch larger than 15 s means "a different recording with the
        // same title" — never display lyrics from that. Without a usable duration
        // (live streams, unknown lengths) name matching alone decides.
        if (duration > 0)
        {
            if (bestExact is not null && bestExactDelta <= 15) return bestExact;
            if (bestTrack is not null && bestTrackDelta <= 15) return bestTrack;
            return null;
        }
        return bestExact ?? bestTrack;
    }

    internal static string Normalize(string? s) => (s ?? "").Trim().ToLowerInvariant();
}

/// <summary>One LRCLIB record (subset of fields MiMi uses).</summary>
public sealed record LyricsRecord(
    long Id, string TrackName, string ArtistName, string AlbumName,
    double Duration, bool Instrumental, string? PlainLyrics, string? SyncedLyrics);

/// <summary>
/// LRCLIB client per https://lrclib.net/docs: exact-match /api/get first, then
/// /api/search + <see cref="LyricsMatchPicker"/>. Identifies MiMi via User-Agent,
/// honors HTTP 429 with Retry-After (client-wide cooldown), supports cancellation,
/// and treats every non-200 as "no lyrics" — a lyrics outage must never disturb
/// playback.
/// </summary>
public sealed class LrcLibClient
{
    public const string UserAgent = "MiMi/4.0 (Windows Dynamic Island; local desktop app)";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);

    private readonly HttpClient _http;
    private DateTimeOffset _cooldownUntil;
    private readonly object _gate = new();

    public LrcLibClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    /// <summary>True while the client is backing off after HTTP 429.</summary>
    public bool IsCoolingDown => DateTimeOffset.UtcNow < _cooldownUntil;

    public async Task<LyricsResult?> GetAsync(LyricsQuery query, CancellationToken ct = default)
    {
        if (IsCoolingDown) return null;
        if (string.IsNullOrWhiteSpace(query.TrackName)) return null;

        // 1. Exact match when we have enough metadata.
        if (query.DurationSeconds > 0 && !string.IsNullOrWhiteSpace(query.ArtistName))
        {
            var url = $"https://lrclib.net/api/get?track_name={Uri.EscapeDataString(query.TrackName)}" +
                      $"&artist_name={Uri.EscapeDataString(query.ArtistName)}" +
                      (string.IsNullOrWhiteSpace(query.AlbumName)
                          ? ""
                          : $"&album_name={Uri.EscapeDataString(query.AlbumName)}") +
                      $"&duration={Math.Round(query.DurationSeconds)}";
            var exact = await FetchRecordAsync(url, ct);
            if (exact is not null) return ToResult(exact);
            ct.ThrowIfCancellationRequested();
        }

        // 2. Search fallback.
        var searchUrl = $"https://lrclib.net/api/search?track_name={Uri.EscapeDataString(query.TrackName)}" +
                        (string.IsNullOrWhiteSpace(query.ArtistName)
                            ? ""
                            : $"&artist_name={Uri.EscapeDataString(query.ArtistName)}");
        using var response = await SendAsync(searchUrl, ct);
        if (response is null || response.StatusCode != HttpStatusCode.OK) return null;

        List<LyricsRecord>? candidates;
        try
        {
            candidates = await response.Content.ReadFromJsonAsync<List<LyricsRecord>>(JsonOpts, ct);
        }
        catch (JsonException) { return null; }
        if (candidates is null || candidates.Count == 0) return null;

        var picked = LyricsMatchPicker.Pick(candidates, query);
        return picked is null ? null : ToResult(picked);
    }

    private async Task<LyricsRecord?> FetchRecordAsync(string url, CancellationToken ct)
    {
        using var response = await SendAsync(url, ct);
        if (response is null || response.StatusCode != HttpStatusCode.OK) return null;
        try
        {
            return await response.Content.ReadFromJsonAsync<LyricsRecord>(JsonOpts, ct);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Returns null for any non-200 (not-found, 429 after arming cooldown, 5xx),
    /// so callers treat every failure uniformly as "no lyrics available".</summary>
    private async Task<HttpResponseMessage?> SendAsync(string url, CancellationToken ct)
    {
        if (IsCoolingDown) return null;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(RequestTimeout);
            var response = await _http.GetAsync(url, timeoutCts.Token);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta
                    ?? TimeSpan.FromSeconds(60);
                lock (_gate) _cooldownUntil = DateTimeOffset.UtcNow + retryAfter;
                return null;
            }
            return response;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // our own timeout: treat as unavailable
        }
        catch (HttpRequestException)
        {
            return null; // offline / DNS / TLS: lyrics are optional, stay silent
        }
    }

    private static LyricsResult ToResult(LyricsRecord record) => new()
    {
        TrackName = record.TrackName,
        ArtistName = record.ArtistName,
        SyncedLines = LrcParser.Parse(record.SyncedLyrics),
        PlainLyrics = string.IsNullOrWhiteSpace(record.PlainLyrics) ? null : record.PlainLyrics,
        Instrumental = record.Instrumental,
        RecordId = record.Id,
    };

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };
}
