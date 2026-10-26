namespace MiMi.Media;

/// <summary>Immutable snapshot of the currently selected media session.</summary>
public sealed record MediaSnapshot
{
    public required string Title { get; init; }
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public string SourceAppId { get; init; } = "";
    public bool IsPlaying { get; init; }
    /// <summary>Playback position at <see cref="PositionUtc"/>.</summary>
    public TimeSpan Position { get; init; }
    public TimeSpan Duration { get; init; }
    public double Rate { get; init; } = 1.0;
    public DateTimeOffset PositionUtc { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>PNG/JPEG bytes of the thumbnail, size-limited. Null when unavailable.</summary>
    public byte[]? Artwork { get; init; }
    public string SessionId { get; init; } = "";

    /// <summary>Extrapolated position as of now (cheap; no API calls).</summary>
    public TimeSpan CurrentPosition() =>
        IsPlaying ? Position + (DateTimeOffset.UtcNow - PositionUtc) * (Rate <= 0 ? 1 : Rate) : Position;

    public string ElapsedLabel() => Format(CurrentPosition());
    public string RemainingLabel() => Format(Duration - CurrentPosition());

    public static string Format(TimeSpan t)
    {
        t = t < TimeSpan.Zero ? TimeSpan.Zero : t;
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }
}

/// <summary>Playback control commands routed to the active session.</summary>
public enum MediaCommand { PlayPause, Play, Pause, Next, Previous }

/// <summary>
/// Abstraction over the Windows media session backend (GSMTC), testable and replaceable.
/// </summary>
public interface IMediaService : IAsyncDisposable
{
    /// <summary>Raised whenever the effective snapshot changes (track, play state, session) or becomes null.</summary>
    event Action<MediaSnapshot?>? Changed;

    /// <summary>Current best snapshot, or null when no session exists. Cheap; no I/O.</summary>
    MediaSnapshot? Current { get; }

    Task StartAsync(CancellationToken ct = default);

    Task SendCommandAsync(MediaCommand command, CancellationToken ct = default);

    /// <summary>Seek if the active session supports it. No-op otherwise.</summary>
    Task SeekAsync(TimeSpan position, CancellationToken ct = default);
}
