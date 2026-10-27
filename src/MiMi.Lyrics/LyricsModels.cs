namespace MiMi.Lyrics;

/// <summary>One timed lyric line. <see cref="Time"/> is the moment the line becomes current.</summary>
public sealed record LrcLine(TimeSpan Time, string Text);

/// <summary>Identifies the track lyrics are requested for (GSMTC metadata).</summary>
public sealed record LyricsQuery(string TrackName, string ArtistName, string AlbumName, double DurationSeconds)
{
    public string CacheKey => FormattableString.Invariant(
        $"{(TrackName ?? "").ToLowerInvariant().Trim()}|{(ArtistName ?? "").ToLowerInvariant().Trim()}|{Math.Round(DurationSeconds)}");
}

/// <summary>Lyrics for one track: synchronized lines when available, plain text otherwise.</summary>
public sealed record LyricsResult
{
    public required string TrackName { get; init; }
    public required string ArtistName { get; init; }
    public IReadOnlyList<LrcLine>? SyncedLines { get; init; }
    public string? PlainLyrics { get; init; }
    public bool Instrumental { get; init; }
    public long RecordId { get; init; }

    public bool HasSynced => SyncedLines is { Count: > 0 };
}

/// <summary>Display state for the lyrics UI (explicit, no guessing).</summary>
public enum LyricsState
{
    Idle,         // nothing playing yet
    Loading,      // fetch in flight
    Synced,       // timed lines available
    Plain,        // only unsynchronized text
    Instrumental, // track confirmed instrumental
    Unavailable,  // nothing found for this track
}
