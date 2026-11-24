using MiMi.Core;
using Windows.Storage.Streams;
using WindowsMediaControl = Windows.Media.Control;

namespace MiMi.Media;

/// <summary>
/// Real Windows media integration via Global System Media Transport Controls (GSMTC).
/// Event-driven: subscribes to session add/remove and per-session change events.
/// A 120 ms coalescing debounce absorbs update storms from chatty players.
/// API surface verified against the 10.0.26100 projections: GetSessions()/GetTimelineProperties()
/// are synchronous; Thumbnail is a property; sessions have no id — identity is by reference.
/// </summary>
public sealed class GsmtcMediaService : IMediaService
{
    private const int ArtworkMaxBytes = 3 * 1024 * 1024;

    private WindowsMediaControl.GlobalSystemMediaTransportControlsSessionManager? _manager;
    private readonly List<WindowsMediaControl.GlobalSystemMediaTransportControlsSession> _sessions = new();
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private MediaSnapshot? _current;
    private WindowsMediaControl.GlobalSystemMediaTransportControlsSession? _activeSession;
    private Timer? _debounce;
    private int _dirty;
    private int _snapshotGeneration;

    public event Action<MediaSnapshot?>? Changed;

    public MediaSnapshot? Current => _current;

    public async Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _manager = await WindowsMediaControl.GlobalSystemMediaTransportControlsSessionManager
            .RequestAsync().AsTask(_cts.Token);

        _manager.SessionsChanged += OnSessionsChanged;
        RefreshSessions();
        Logger.Info($"Media: GSMTC started, {_sessions.Count} session(s).");
    }

    // ---- session management -------------------------------------------------

    private void OnSessionsChanged(WindowsMediaControl.GlobalSystemMediaTransportControlsSessionManager sender, object args)
        => _ = RefreshSessionsSafeAsync();

    private async Task RefreshSessionsSafeAsync()
    {
        try { await Task.Run(RefreshSessions, _cts?.Token ?? CancellationToken.None); }
        catch (Exception ex) { Logger.Error("Media: session refresh failed", ex); }
    }

    private void RefreshSessions()
    {
        if (_manager is null) return;
        var fresh = _manager.GetSessions();

        lock (_gate)
        {
            // Identity by reference: subscribe to new sessions, detach removed ones.
            foreach (var session in fresh)
            {
                if (_sessions.Contains(session)) continue;
                _sessions.Add(session);
                session.PlaybackInfoChanged += OnSessionChanged;
                session.MediaPropertiesChanged += OnSessionChanged;
                session.TimelinePropertiesChanged += OnSessionChanged;
            }
            foreach (var session in _sessions.Where(s => !fresh.Contains(s)).ToList())
            {
                session.PlaybackInfoChanged -= OnSessionChanged;
                session.MediaPropertiesChanged -= OnSessionChanged;
                session.TimelinePropertiesChanged -= OnSessionChanged;
                _sessions.Remove(session);
                if (ReferenceEquals(_activeSession, session)) _activeSession = null;
            }
        }
        MarkDirty();
    }

    private void OnSessionChanged(WindowsMediaControl.GlobalSystemMediaTransportControlsSession sender, object args)
        => MarkDirty();

    /// <summary>Coalesces change notifications; the snapshot is rebuilt at most every 120 ms.</summary>
    private void MarkDirty()
    {
        var first = Interlocked.Exchange(ref _dirty, 1) == 0;
        _debounce ??= new Timer(_ =>
        {
            Interlocked.Exchange(ref _dirty, 0);
            _ = RefreshSnapshotSafeAsync();
        });
        _debounce.Change(120, Timeout.Infinite);
    }

    private async Task RefreshSnapshotSafeAsync()
    {
        try { await RefreshSnapshotAsync(); }
        catch (Exception ex) { Logger.Error("Media: snapshot refresh failed", ex); }
    }

    private async Task RefreshSnapshotAsync()
    {
        var generation = Interlocked.Increment(ref _snapshotGeneration);
        WindowsMediaControl.GlobalSystemMediaTransportControlsSession? session;
        lock (_gate)
        {
            session = _sessions.Count == 0 ? null : PickActiveSessionLocked();
        }
        if (session is null)
        {
            if (generation != Volatile.Read(ref _snapshotGeneration)) return;
            _activeSession = null;
            Publish(null);
            return;
        }

        var snapshot = await BuildSnapshotAsync(session);
        // A slower older refresh must never overwrite a newer track/artwork snapshot.
        if (generation != Volatile.Read(ref _snapshotGeneration)) return;
        _activeSession = session;
        Publish(snapshot);
    }

    private WindowsMediaControl.GlobalSystemMediaTransportControlsSession? PickActiveSessionLocked()
    {
        // Prefer a playing session; otherwise fall back to any session (last wins when
        // players register multiple). Multiple simultaneous sessions are supported by
        // keeping all of them alive; the UI shows the active one.
        return _sessions.FirstOrDefault(s => s.GetPlaybackInfo().PlaybackStatus
                == WindowsMediaControl.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            ?? _sessions.LastOrDefault();
    }

    private async Task<MediaSnapshot> BuildSnapshotAsync(WindowsMediaControl.GlobalSystemMediaTransportControlsSession session)
    {
        var playback = session.GetPlaybackInfo();
        var timeline = session.GetTimelineProperties();
        var props = await session.TryGetMediaPropertiesAsync().AsTask();

        byte[]? artwork = null;
        try { artwork = await ReadThumbnailAsync(props); }
        catch (Exception ex) { Logger.Warn($"Media: artwork unavailable ({ex.Message})"); }

        var isPlaying = playback.PlaybackStatus
            == WindowsMediaControl.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

        var duration = timeline.EndTime > TimeSpan.Zero ? timeline.EndTime : TimeSpan.Zero;

        return new MediaSnapshot
        {
            Title = props.Title ?? "",
            Artist = props.Artist ?? "",
            Album = props.AlbumTitle ?? "",
            SourceAppId = session.SourceAppUserModelId ?? "",
            IsPlaying = isPlaying,
            Position = timeline.Position,
            Duration = duration,
            Rate = playback.PlaybackRate is { } rate && rate > 0 ? rate : 1.0,
            PositionUtc = DateTimeOffset.UtcNow,
            Artwork = artwork,
            SessionId = session.SourceAppUserModelId ?? "",
        };
    }

    private static async Task<byte[]?> ReadThumbnailAsync(WindowsMediaControl.GlobalSystemMediaTransportControlsSessionMediaProperties props)
    {
        var reference = props.Thumbnail;
        if (reference is null) return null;

        using var stream = await reference.OpenReadAsync().AsTask();
        if (stream.Size == 0 || stream.Size > ArtworkMaxBytes) return null;

        using var reader = new DataReader(stream);
        await reader.LoadAsync((uint)stream.Size).AsTask();
        var bytes = new byte[stream.Size];
        reader.ReadBytes(bytes);
        return bytes.Length == 0 ? null : bytes;
    }

    private void Publish(MediaSnapshot? snapshot)
    {
        _current = snapshot;
        try { Changed?.Invoke(snapshot); }
        catch (Exception ex) { Logger.Error("Media: Changed handler threw", ex); }
    }

    // ---- controls -------------------------------------------------------------

    public async Task SendCommandAsync(MediaCommand command, CancellationToken ct = default)
    {
        var session = GetActiveSession();
        if (session is null) return;
        try
        {
            switch (command)
            {
                case MediaCommand.PlayPause: await session.TryTogglePlayPauseAsync().AsTask(ct); break;
                case MediaCommand.Play: await session.TryPlayAsync().AsTask(ct); break;
                case MediaCommand.Pause: await session.TryPauseAsync().AsTask(ct); break;
                case MediaCommand.Next: await session.TrySkipNextAsync().AsTask(ct); break;
                case MediaCommand.Previous: await session.TrySkipPreviousAsync().AsTask(ct); break;
            }
        }
        catch (Exception ex) { Logger.Warn($"Media: command {command} failed ({ex.Message})"); }
    }

    public async Task SeekAsync(TimeSpan position, CancellationToken ct = default)
    {
        var session = GetActiveSession();
        if (session is null) return;
        try
        {
            await session.TryChangePlaybackPositionAsync(position.Ticks).AsTask(ct);
        }
        catch (Exception ex) { Logger.Warn($"Media: seek failed ({ex.Message})"); }
    }

    private WindowsMediaControl.GlobalSystemMediaTransportControlsSession? GetActiveSession()
    {
        lock (_gate)
        {
            if (_activeSession is not null && _sessions.Contains(_activeSession)) return _activeSession;
            return PickActiveSessionLocked();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _snapshotGeneration); // invalidate any in-flight publish
        try
        {
            _cts?.Cancel();
            _debounce?.Dispose();
            if (_manager is not null) _manager.SessionsChanged -= OnSessionsChanged;
            lock (_gate)
            {
                foreach (var s in _sessions)
                {
                    s.PlaybackInfoChanged -= OnSessionChanged;
                    s.MediaPropertiesChanged -= OnSessionChanged;
                    s.TimelinePropertiesChanged -= OnSessionChanged;
                }
                _sessions.Clear();
            }
        }
        catch { /* disposing: best effort */ }
        await Task.CompletedTask;
    }
}
