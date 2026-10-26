using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MiMi.Agents;
using MiMi.Media;

namespace MiMi.App.UI;

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => execute();
}

/// <summary>Marker base so the window can pick a DataTemplate per content type.
/// Inherits change notification so mutable views (resting clock) can update bindings.</summary>
public abstract class IslandContent : ObservableObject
{
    public abstract double TargetWidth { get; }
    public abstract double TargetHeight { get; }
    public abstract double TargetCornerRadius { get; }
    public virtual bool Expandable => false;
    /// <summary>When true the pill hugs the measured content height instead of the design height.</summary>
    public virtual bool AutoHeight => false;
}

/// <summary>Class (not record) so the live clock can update without swapping content objects.</summary>
public sealed class RestingView : IslandContent
{
    private string _clock;
    public RestingView(string clock) { _clock = clock; }
    public string Clock { get => _clock; set => Set(ref _clock, value); }
    public bool ShowClock { get; init; } = true;
    public bool ShowMascot { get; init; } = true;
    public override double TargetWidth => 150;
    public override double TargetHeight => 36;
    public override double TargetCornerRadius => 18;
}

public sealed class MediaCompactView : IslandContent
{
    public MediaViewModel Media { get; }
    public MediaCompactView(MediaViewModel media) { Media = media; }
    public override double TargetWidth => 320;
    public override double TargetHeight => 44;
    public override double TargetCornerRadius => 22;
    public override bool Expandable => true;
}

public sealed class MediaExpandedView : IslandContent
{
    public MediaViewModel Media { get; }
    public MediaExpandedView(MediaViewModel media) { Media = media; }
    public override double TargetWidth => 430;
    public override double TargetHeight => 190;
    public override double TargetCornerRadius => 24;
    public override bool AutoHeight => true;
}

public sealed class AgentCompactView : IslandContent
{
    public AgentCardViewModel Agent { get; }
    public AgentCompactView(AgentCardViewModel agent) { Agent = agent; }
    public override double TargetWidth => 330;
    public override double TargetHeight => 44;
    public override double TargetCornerRadius => 22;
    public override bool Expandable => true;
}

public sealed class AgentExpandedView : IslandContent
{
    public AgentCardViewModel Agent { get; }
    public AgentExpandedView(AgentCardViewModel agent) { Agent = agent; }
    public override double TargetWidth => 420;
    public override double TargetHeight => 130;
    public override double TargetCornerRadius => 24;
    public override bool AutoHeight => true;
}

public sealed class ApprovalView : IslandContent
{
    public AgentCardViewModel Agent { get; }
    public ApprovalView(AgentCardViewModel agent) { Agent = agent; }
    public override double TargetWidth => 440;
    public override double TargetHeight => 210;
    public override double TargetCornerRadius => 24;
    public override bool AutoHeight => true;
}

public sealed class MediaViewModel : ObservableObject
{
    private string _title = "";
    private string _subtitle = "";
    private string _source = "";
    private ImageSource? _art;
    private bool _isPlaying;
    private string _elapsed = "0:00";
    private string _remaining = "0:00";
    private double _progress;

    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Subtitle { get => _subtitle; private set => Set(ref _subtitle, value); }
    public string Source { get => _source; private set => Set(ref _source, value); }
    public ImageSource? Art { get => _art; private set => Set(ref _art, value); }
    public bool IsPlaying { get => _isPlaying; private set => Set(ref _isPlaying, value); }
    public string Elapsed { get => _elapsed; private set => Set(ref _elapsed, value); }
    public string Remaining { get => _remaining; private set => Set(ref _remaining, value); }
    /// <summary>0..1; NaN when duration unknown (slider disabled via trigger).</summary>
    public double Progress { get => _progress; private set => Set(ref _progress, value); }

    private MediaSnapshot? _snapshot;
    private readonly object _gate = new();

    public MediaViewModel(
        Action? playPause = null, Action? next = null, Action? previous = null,
        Action<TimeSpan>? seek = null)
    {
        if (playPause is not null) PlayPauseCommand = new RelayCommand(playPause, () => _snapshot is not null);
        if (next is not null) NextCommand = new RelayCommand(next, () => _snapshot is not null);
        if (previous is not null) PreviousCommand = new RelayCommand(previous, () => _snapshot is not null);
        _seek = seek;
    }

    private readonly Action<TimeSpan>? _seek;

    public ICommand? PlayPauseCommand { get; }
    public ICommand? NextCommand { get; }
    public ICommand? PreviousCommand { get; }

    public void SeekToFraction(double fraction)
    {
        if (_snapshot is not { Duration.TotalSeconds: > 0 } snap) return;
        _seek?.Invoke(TimeSpan.FromSeconds(Math.Clamp(fraction, 0, 1) * snap.Duration.TotalSeconds));
    }

    public void Update(MediaSnapshot? snapshot)
    {
        _snapshot = snapshot;
        if (snapshot is null)
        {
            Title = "";
            Subtitle = "";
            Source = "";
            Art = null;
            IsPlaying = false;
            Progress = 0;
            Elapsed = "0:00";
            Remaining = "0:00";
            return;
        }

        Title = string.IsNullOrEmpty(snapshot.Title) ? "Unknown title" : snapshot.Title;
        Subtitle = string.IsNullOrEmpty(snapshot.Artist) ? string.IsNullOrEmpty(snapshot.Album) ? "" : snapshot.Album : snapshot.Artist;
        Source = FriendlyName(snapshot.SourceAppId);
        IsPlaying = snapshot.IsPlaying;
        if (snapshot.Artwork is { Length: > 0 })
        {
            try
            {
                var image = new BitmapImage();
                using var stream = new MemoryStream(snapshot.Artwork);
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 96; // thumbnail-sized decode keeps memory tiny
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                Art = image;
            }
            catch { Art = null; } // broken artwork must not crash the island
        }
        else Art = null;
        RefreshTimeline();
    }

    /// <summary>Cheap extrapolated tick (1 Hz while expanded/playing).</summary>
    public void RefreshTimeline()
    {
        if (_snapshot is null) return;
        var duration = _snapshot.Duration;
        var position = _snapshot.CurrentPosition();
        if (duration > TimeSpan.Zero)
        {
            Elapsed = MediaSnapshot.Format(position);
            Remaining = "-" + MediaSnapshot.Format(duration - position);
            Progress = Math.Clamp(position / duration, 0, 1);
        }
        else
        {
            Elapsed = MediaSnapshot.Format(position);
            Remaining = "";
            Progress = double.NaN;
        }
    }

    public static string FriendlyName(string appId) => appId switch
    {
        var s when s.StartsWith("Spotify", StringComparison.OrdinalIgnoreCase) => "Spotify",
        var s when s.Contains("Chrome", StringComparison.OrdinalIgnoreCase) => "Chrome",
        var s when s.Contains("Edge", StringComparison.OrdinalIgnoreCase) => "Edge",
        var s when s.Contains("Firefox", StringComparison.OrdinalIgnoreCase) => "Firefox",
        var s when s.StartsWith("Microsoft.MediaPlayer", StringComparison.OrdinalIgnoreCase) => "Media Player",
        var s when s.Contains("AppleMusic", StringComparison.OrdinalIgnoreCase) => "Apple Music",
        var s when s.Contains("foobar", StringComparison.OrdinalIgnoreCase) => "foobar2000",
        "" => "",
        _ => appId,
    };
}

/// <summary>Compact or expanded agent activity card; doubles as the approval card.</summary>
public sealed class AgentCardViewModel : ObservableObject
{
    private string _provider = "";
    private string _title = "";
    private string _message = "";
    private string _project = "";
    private string _sessionId = "";
    private string _detail = "";
    private string _command = "";
    private string _actionKind = "";
    private string _accent = "#FFB454"; // warm amber for attention states
    private string _elapsed = "";
    private string _timeLeft = "";

    public string Provider { get => _provider; private set => Set(ref _provider, value); }
    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Message { get => _message; private set => Set(ref _message, value); }
    public string Project { get => _project; private set => Set(ref _project, value); }
    public string SessionId { get => _sessionId; private set => Set(ref _sessionId, value); }
    public string Detail { get => _detail; private set => Set(ref _detail, value); }
    public string Command { get => _command; private set => Set(ref _command, value); }
    public string ActionKind { get => _actionKind; private set => Set(ref _actionKind, value); }
    public string Accent { get => _accent; private set => Set(ref _accent, value); }
    public string Elapsed { get => _elapsed; private set => Set(ref _elapsed, value); }
    public string TimeLeft { get => _timeLeft; private set => Set(ref _timeLeft, value); }

    public required string ApprovalId { get; init; }
    public Action<ApprovalDecision>? Decide { get; init; }
    public Action? OpenInAgent { get; init; }

    public ICommand ApproveCommand { get; }
    public ICommand DenyCommand { get; }
    public ICommand OpenCommand { get; }

    public AgentCardViewModel()
    {
        ApproveCommand = new RelayCommand(() => Decide?.Invoke(ApprovalDecision.Approved));
        DenyCommand = new RelayCommand(() => Decide?.Invoke(ApprovalDecision.Denied));
        OpenCommand = new RelayCommand(() => OpenInAgent?.Invoke());
    }

    public void Update(AgentEvent ev)
    {
        Provider = DisplayProvider(ev.Provider);
        Title = ev.Title;
        Message = ev.Message;
        Project = ev.Project;
        SessionId = ev.SessionId;
        Accent = ev.Type switch
        {
            AgentEventTypes.ApprovalRequested => "#FFB454",
            AgentEventTypes.Failed => "#FF6B6B",
            AgentEventTypes.Completed => "#5DD97C",
            AgentEventTypes.Cancelled => "#9A9AA0",
            _ => "#8AB4FF",
        };
        ev.Metadata.TryGetValue("action_kind", out var kind);
        ActionKind = kind ?? "";
        ev.Metadata.TryGetValue("command", out var cmd);
        Command = cmd ?? "";
        var detail = ev.Metadata.TryGetValue("tool", out var tool) ? tool : "";
        Detail = detail;
        Elapsed = "just now";
    }

    public void TickTimeLeft(TimeSpan remaining) => TimeLeft = remaining > TimeSpan.Zero
        ? $"{(int)remaining.TotalMinutes}:{remaining.Seconds:00} left"
        : "";

    public static string DisplayProvider(string provider) => provider switch
    {
        "claude-code" => "Claude Code",
        "codex" => "Codex CLI",
        var p => string.IsNullOrEmpty(p) ? "Agent" : char.ToUpperInvariant(p[0]) + p[1..],
    };

    // ---- computed display strings (empty-safe for missing metadata) ----

    public string ProjectLabel => string.IsNullOrEmpty(Project) ? "" : $"Project:  {Project}";
    public string SessionLabel => string.IsNullOrEmpty(SessionId) ? "" : $"session {SessionId}";
    public string ActionLabel => ActionKind switch
    {
        "" => "",
        var k => $"Action:  {k}",
    };
}

/// <summary>Collapses a row when its bound string is null/empty.</summary>
public sealed class StringToVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
