using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MiMi.Agents;
using MiMi.App.UI.Mascot;
using MiMi.Core;
using MiMi.Lyrics;
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
    private MascotExpression _expression = MascotExpression.Calm;
    public RestingView() { }
    /// <summary>Persistent mood (plus any brief reaction) for the resting face.</summary>
    public MascotExpression Expression { get => _expression; set => Set(ref _expression, value); }
    public bool ShowMascot { get; init; } = true;
    // Face-only idle: a slim pill whose width hugs the face with breathing room.
    public override double TargetWidth => 118;
    public override double TargetHeight => 38;
    public override double TargetCornerRadius => 19;
}

public sealed class MediaCompactView : IslandContent
{
    private int _agentBadgeCount;

    public MediaViewModel Media { get; }
    public MediaCompactView(MediaViewModel media) { Media = media; }
    public override double TargetWidth => 320;
    public override double TargetHeight => 44;
    public override double TargetCornerRadius => 22;
    public override bool Expandable => true;

    /// <summary>Agent sessions active while music plays: a tiny count on the pill's
    /// right edge keeps both worlds discoverable without displacing either.</summary>
    public int AgentBadgeCount
    {
        get => _agentBadgeCount;
        set
        {
            if (_agentBadgeCount == value) return;
            _agentBadgeCount = value;
            Raise(nameof(AgentBadgeCount));
            // The template binds the derived properties, so they must notify too.
            Raise(nameof(HasAgentBadge));
            Raise(nameof(AgentBadgeLabel));
        }
    }

    public bool HasAgentBadge => AgentBadgeCount > 0;
    public string AgentBadgeLabel => AgentBadgeCount.ToString();
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
    public AgentIndicatorViewModel Indicator { get; }
    public AgentCompactView(AgentIndicatorViewModel indicator) { Indicator = indicator; }
    public override double TargetWidth => Math.Clamp(88 + new FormattedText(
        Indicator.Line, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
        new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
        13, Brushes.White, 1).WidthIncludingTrailingWhitespace, 180, 380);
    public override double TargetHeight => 44;
    public override double TargetCornerRadius => 22;
    public override bool Expandable => true;
}

// ---- productivity utilities (Phase 4 / Milestone 4) ----------------------------

public sealed class PomodoroView : IslandContent
{
    public PomodoroViewModel Pomodoro { get; }
    public PomodoroView(PomodoroViewModel pomodoro) { Pomodoro = pomodoro; }
    public override double TargetWidth => 400;
    public override double TargetHeight => 74;
    public override double TargetCornerRadius => 22;
}

public sealed class WaterView : IslandContent
{
    public WaterViewModel Water { get; }
    public WaterView(WaterViewModel water) { Water = water; }
    public override double TargetWidth => 380;
    public override double TargetHeight => 74;
    public override double TargetCornerRadius => 22;
}

public sealed class TodoView : IslandContent
{
    public TodoViewModel Todo { get; }
    public TodoView(TodoViewModel todo) { Todo = todo; }
    public override double TargetWidth => 400;
    public override double TargetHeight => 74;
    public override double TargetCornerRadius => 22;
    public override bool AutoHeight => true;
}

/// <summary>Live Pomodoro bindings. Remaining/progress refresh on the 1 Hz tick.</summary>
public sealed class PomodoroViewModel : ObservableObject
{
    private string _phaseLabel = "Focus time";
    private string _remaining = "25:00";
    private string _subLabel = "Press play to begin";
    private double _progress;
    private bool _running;
    private bool _idle = true;
    private string _sessionsLabel = "0";

    public string PhaseLabel { get => _phaseLabel; private set => Set(ref _phaseLabel, value); }
    public string Remaining { get => _remaining; private set => Set(ref _remaining, value); }
    public string SubLabel { get => _subLabel; private set => Set(ref _subLabel, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public bool Running { get => _running; private set => Set(ref _running, value); }
    public bool Idle { get => _idle; private set => Set(ref _idle, value); }
    /// <summary>Play glyph when idle/paused, pause glyph when running.</summary>
    public string RunningGlyph => Running ? "\uE769" : "\uE768";
    public string SessionsLabel { get => _sessionsLabel; private set => Set(ref _sessionsLabel, value); }

    public ICommand PlayPauseCommand { get; }
    public ICommand StopCommand { get; }
    private readonly Action _playPause;
    private readonly Action _stop;

    public PomodoroViewModel(Action playPause, Action stop)
    {
        _playPause = playPause;
        _stop = stop;
        PlayPauseCommand = new RelayCommand(() => _playPause());
        StopCommand = new RelayCommand(() => _stop());
    }

    public void Update(string phaseLabel, string remaining, string subLabel, double progress,
        bool running, bool idle, int sessions)
    {
        PhaseLabel = phaseLabel;
        Remaining = remaining;
        SubLabel = subLabel;
        Progress = progress;
        Running = running;
        Idle = idle;
        SessionsLabel = sessions.ToString();
    }
}

/// <summary>Hydration bindings: today total, goal fraction, quick-add.</summary>
public sealed class WaterViewModel : ObservableObject
{
    private string _totalLabel = "0.0 / 1.8 L";
    private double _progress;
    private string _subLabel = "Stay hydrated";

    public string TotalLabel { get => _totalLabel; private set => Set(ref _totalLabel, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public string SubLabel { get => _subLabel; private set => Set(ref _subLabel, value); }
    public string QuickAddLabel => $"+{QuickAddMl} ml";

    public int QuickAddMl { get; set; } = 250;

    public ICommand QuickAddCommand { get; }
    private readonly Action _quickAdd;

    public WaterViewModel(Action quickAdd)
    {
        _quickAdd = quickAdd;
        QuickAddCommand = new RelayCommand(() => _quickAdd());
    }

    public void Update(string totalLabel, double progress, string subLabel)
    {
        TotalLabel = totalLabel;
        Progress = progress;
        SubLabel = subLabel;
    }
}

/// <summary>To-do bindings: open tasks (max 4 rows), add box, complete buttons.</summary>
public sealed class TodoViewModel : ObservableObject
{
    private string _headerCount = "";
    private string _newTaskTitle = "";
    private IReadOnlyList<TodoRowViewModel> _rows = Array.Empty<TodoRowViewModel>();

    public string HeaderCount { get => _headerCount; set => Set(ref _headerCount, value); }
    public IReadOnlyList<TodoRowViewModel> Rows { get => _rows; set => Set(ref _rows, value); }
    public bool HasNoRows => _rows.Count == 0;
    public string NewTaskTitle { get => _newTaskTitle; set => Set(ref _newTaskTitle, value); }

    public ICommand AddCommand { get; }
    private readonly Func<string, bool> _add;
    private readonly Action _refresh;

    public TodoViewModel(Func<string, bool> add, Action refresh)
    {
        _add = add;
        _refresh = refresh;
        AddCommand = new RelayCommand(() =>
        {
            if (_add(NewTaskTitle)) NewTaskTitle = "";
            _refresh();
        });
    }

    public sealed class TodoRow
    {
        public required Guid Id { get; init; }
        public required string Title { get; init; }
        public required string DueLabel { get; init; }
    }

    public ICommand? CompleteCommandFor(Guid id) => null; // rows complete via row VMs
}

/// <summary>Checkbox row VM: completes its task through the presenter callback.</summary>
public sealed class TodoRowViewModel : ObservableObject
{
    public Guid Id { get; }
    public string Title { get; }
    public string DueLabel { get; }

    public ICommand CompleteCommand { get; }

    public TodoRowViewModel(Guid id, string title, string dueLabel, Action<Guid> complete)
    {
        Id = id;
        Title = title;
        DueLabel = dueLabel;
        CompleteCommand = new RelayCommand(() => complete(id));
    }
}

// ---- Quick Access hub (Phase 4.1) ------------------------------------------------

public sealed record QuickAccessButton(string Kind, string Glyph, string Label, bool Accent);

/// <summary>Quick Access hub bindings: four utility shortcuts + back affordance.</summary>
public sealed class QuickAccessViewModel : ObservableObject
{
    private IReadOnlyList<QuickAccessButton> _buttons = Array.Empty<QuickAccessButton>();
    private string _timerBadge = "";

    public IReadOnlyList<QuickAccessButton> Buttons { get => _buttons; private set => Set(ref _buttons, value); }
    /// <summary>Shown on the Focus tile while a session runs (context retention cue).</summary>
    public string TimerBadge { get => _timerBadge; private set => Set(ref _timerBadge, value); }

    public ICommand OpenPomodoroCommand { get; }
    public ICommand OpenWaterCommand { get; }
    public ICommand OpenTodoCommand { get; }
    public ICommand OpenShelfCommand { get; }
    public ICommand BackCommand { get; }

    private readonly Action<string> _openUtility;
    private readonly Action _back;

    public QuickAccessViewModel(Action<string> openUtility, Action back)
    {
        _openUtility = openUtility;
        _back = back;
        OpenPomodoroCommand = new RelayCommand(() => _openUtility(UtilityPinState.Pomodoro));
        OpenWaterCommand = new RelayCommand(() => _openUtility(UtilityPinState.Water));
        OpenTodoCommand = new RelayCommand(() => _openUtility(UtilityPinState.Todo));
        OpenShelfCommand = new RelayCommand(() => _openUtility(UtilityPinState.Shelf));
        BackCommand = new RelayCommand(_back);
    }

    public void UpdateTimerBadge(bool running, int sessions)
    {
        TimerBadge = running ? "running" : sessions > 0 ? $"{sessions} done" : "";
    }
}

/// <summary>Hydration reminder with quick-add and snooze actions (Phase 4.1).</summary>
public sealed class WaterReminderView : IslandContent
{
    public WaterViewModel Water { get; }
    public ICommand SnoozeCommand { get; }

    public WaterReminderView(WaterViewModel water, Action snooze)
    {
        Water = water;
        SnoozeCommand = new RelayCommand(snooze);
    }

    public override double TargetWidth => 380;
    public override double TargetHeight => 74;
    public override double TargetCornerRadius => 22;
}

/// <summary>To-do due reminder with a complete action (Phase 4.1).</summary>
public sealed class TodoDueView : IslandContent
{
    public string TaskTitle { get; }
    public ICommand CompleteCommand { get; }
    public ICommand OpenListCommand { get; }

    public TodoDueView(string title, Action complete, Action openList)
    {
        TaskTitle = title;
        CompleteCommand = new RelayCommand(complete);
        OpenListCommand = new RelayCommand(openList);
    }

    public override double TargetWidth => 400;
    public override double TargetHeight => 64;
    public override double TargetCornerRadius => 20;
}

// ---- file shelf (Phase 4 / Milestone 5) -----------------------------------------

/// <summary>One draggable shelf row. Path feeds the drag-out DataObject.</summary>
public sealed class ShelfRowViewModel : ObservableObject
{
    public string Path { get; }
    public string Name { get; }
    public string SizeLabel { get; }
    public bool Missing { get; }
    public string MissingLabel { get; }

    public ICommand RemoveCommand { get; }

    public ShelfRowViewModel(string path, string sizeLabel, bool missing,
        Action<string> remove)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        SizeLabel = missing ? "missing" : sizeLabel;
        Missing = missing;
        MissingLabel = missing ? " (missing)" : "";
        RemoveCommand = new RelayCommand(() => remove(path));
    }
}

/// <summary>File shelf panel: header + rows. Auto-height; drag-out handled by the
/// window (DoDragDrop with a FileDrop DataObject).</summary>
public sealed class ShelfView : IslandContent
{
    public IReadOnlyList<ShelfRowViewModel> Rows { get; }
    public string HeaderCount { get; }

    public ShelfView(IReadOnlyList<ShelfRowViewModel> rows, string headerCount)
    {
        Rows = rows;
        HeaderCount = headerCount;
    }

    public override double TargetWidth => 400;
    public override double TargetCornerRadius => 22;
    public override double TargetHeight => 74;
    public override bool AutoHeight => true;
}

// ---- system HUDs (Phase 4 / Milestone 6) ----------------------------------------

/// <summary>Brief system status overlay: icon + title + sub + optional progress.
/// Rendered for ~2 s via the island's transient alert path.</summary>
public sealed class HudView : IslandContent
{
    public string IconGlyph { get; }
    public string Title { get; }
    public string SubLabel { get; }
    public double? Progress { get; }

    public HudView(string iconGlyph, string title, string subLabel, double? progress)
    {
        IconGlyph = iconGlyph;
        Title = title;
        SubLabel = subLabel;
        Progress = progress;
    }

    public override double TargetWidth => 360;
    public override double TargetHeight => 68;
    public override double TargetCornerRadius => 20;
}

/// <summary>Quick Access hub: the island-native utility launcher (Phase 4.1).</summary>
public sealed class QuickAccessView : IslandContent
{
    public QuickAccessViewModel QuickAccess { get; }
    public QuickAccessView(QuickAccessViewModel vm) { QuickAccess = vm; }
    public override double TargetWidth => 430;
    public override double TargetHeight => 92;
    public override double TargetCornerRadius => 22;
    public override bool AutoHeight => true;
}

/// <summary>Expanded multi-agent activity panel: one row per tracked session.</summary>
public sealed class AgentActivityPanelView : IslandContent
{
    private IReadOnlyList<AgentSessionRow> _rows = Array.Empty<AgentSessionRow>();
    private string _impressiveBotLine = "";
    private string _impressiveBotDetail = "";
    private string _impressiveBotColor = "#9A9AA0";
    public IReadOnlyList<AgentSessionRow> Rows => _rows;

    /// <summary>Live ImpressiveBot line, for example "ImpressiveBot - working - Patch"; empty hides it.</summary>
    public string ImpressiveBotLine => _impressiveBotLine;
    public string ImpressiveBotDetail => _impressiveBotDetail;
    public string ImpressiveBotColor => _impressiveBotColor;
    public bool HasImpressiveBot => _impressiveBotLine.Length > 0;
    public string Summary => Rows.Count == 0 ? "No recent activity" : $"{Rows.Count} recent sessions · approvals first";

    /// <summary>The status snapshot this panel was built with, so the 1 Hz tick can
    /// refresh the line in place instead of rebuilding the whole panel.</summary>
    public ImpressiveBotStatus? ImpressiveBot { get; private set; }

    public AgentActivityPanelView(IReadOnlyList<AgentSessionRow> rows,
        ImpressiveBotStatus? impressiveBot = null)
    {
        _rows = rows;
        SetImpressiveBot(impressiveBot);
    }

    public void Update(IReadOnlyList<AgentSessionRow> rows) => Update(rows, null);

    public void Update(IReadOnlyList<AgentSessionRow> rows, ImpressiveBotStatus? impressiveBot)
    {
        _rows = rows;
        SetImpressiveBot(impressiveBot);
        Raise(nameof(Rows));
        Raise(nameof(Summary));
    }

    /// <summary>Refreshes only the ImpressiveBot line: the 1 Hz tick uses this cheap path.</summary>
    public void SetImpressiveBot(ImpressiveBotStatus? status)
    {
        var line = status?.Line ?? "";
        var detail = status?.Detail ?? "";
        var color = status is null ? "#9A9AA0"
            : status.FailedCount > 0 ? "#FF6B6B"
            : status.WaitingCount > 0 ? "#FFB454"
            : status.IsHealthy ? "#5DD97C"
            : "#9A9AA0";
        ImpressiveBot = status;
        ImpressiveBot = status;
        if (line == _impressiveBotLine && detail == _impressiveBotDetail && color == _impressiveBotColor) return;
        _impressiveBotLine = line;
        _impressiveBotDetail = detail;
        _impressiveBotColor = color;
        Raise(nameof(ImpressiveBotLine));
        Raise(nameof(ImpressiveBotDetail));
        Raise(nameof(ImpressiveBotColor));
        Raise(nameof(HasImpressiveBot));
    }
    public override double TargetWidth => 450;
    public override double TargetHeight => PanelHeight;
    public override double TargetCornerRadius => 24;

    /// <summary>
    /// The panel keeps ONE height while it is open. It used to hug its content, so a
    /// session that finished or aged out shrank the pill under the cursor, the pointer
    /// left the pill, and the panel collapsed mid-read. A fixed height (the list scrolls
    /// inside it) keeps the panel stable for as long as it is shown.
    /// </summary>
    public const double PanelHeight = 300;
    public override bool AutoHeight => false;
}

/// <summary>One session row in the activity panel. Snapshot data at build time.</summary>
public sealed class AgentSessionRow
{
    public required string Provider { get; init; }
    public required string Project { get; init; }
    public required string StatusLabel { get; init; }
    public required string LastAction { get; init; }
    public required string AgeLabel { get; init; }
    public required int PendingApprovals { get; init; }
    public bool HasDetail => !string.IsNullOrEmpty(LastAction) && LastAction != StatusLabel;
    public string Section { get; init; } = "";
    public required Brush StateBrush { get; init; }
    /// <summary>Newest-last confirmed events (bounded, max three shown).</summary>
    public required IReadOnlyList<AgentTimelineItem> Timeline { get; init; }
}

/// <summary>One short timeline line: "14:32 · Started".</summary>
public sealed class AgentTimelineItem
{
    public required string TimeLabel { get; init; }
    public required string Text { get; init; }
    public required string Kind { get; init; }
}

/// <summary>Running -> pause glyph; idle/paused -> play glyph.</summary>
public sealed class RunningToGlyphConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is true ? "\uE769" : "\uE768";

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Shows a progress bar only when a progress value exists (null hides it).</summary>
public sealed class NullToVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Shows a pending-approval marker only when the count is above zero.</summary>
public sealed class PendingCountToVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is int i && i > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Live compact indicator for agent activity: one line ("Codex · Working" or
/// "3 agents active"). The presenter updates it in place — status changes never
/// re-trigger island animations.
/// </summary>
public sealed class AgentIndicatorViewModel : ObservableObject
{
    private string _line = "";
    private string _pendingLabel = "";
    private MascotExpression _expression = MascotExpression.Focused;

    public string Line { get => _line; private set => Set(ref _line, value); }
    public string PendingLabel { get => _pendingLabel; private set => Set(ref _pendingLabel, value); }
    public MascotExpression Expression { get => _expression; private set => Set(ref _expression, value); }

    public void Update(string line, MascotExpression expression, int pendingApprovals)
    {
        Line = line;
        Expression = expression;
        PendingLabel = pendingApprovals > 0 ? $"{pendingApprovals} approval{(pendingApprovals == 1 ? "" : "s")} pending" : "";
    }
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
        UpdateLyricWindow();
    }

    // ---- synchronized lyrics (Phase 4 / Milestone 2) ----------------------------

    private LyricsState _lyricsState = LyricsState.Idle;
    private IReadOnlyList<LrcLine> _lyricLines = Array.Empty<LrcLine>();
    private bool _lyricsVisible;
    private int _lyricOffsetMs;
    private int _lyricIndex = -1;

    private string _lyricPrev = "";
    private string _lyricCurrent = "";
    private string _lyricNext = "";
    private bool _hasLyricCurrent;

    /// <summary>Shows lyrics or their availability status while lyrics are enabled.</summary>
    public bool ShowLyricsBlock
    {
        get => _lyricsVisible;
        private set
        {
            if (_lyricsVisible == value) return;
            Set(ref _lyricsVisible, value);
            Raise(nameof(LyricsBlockVisible));
            Raise(nameof(LyricsBlockHidden));
        }
    }
    public bool LyricsBlockVisible => ShowLyricsBlock;
    public bool LyricsBlockHidden => !ShowLyricsBlock;
    /// <summary>Current line color: gold when a real line is active, dim for ♪ gaps.</summary>
    public System.Windows.Media.Color LyricCurrentColor =>
        LyricCurrentIsLine
            ? (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FFF5B84B")
            : System.Windows.Media.Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF);
    public string LyricPrev { get => _lyricPrev; private set => Set(ref _lyricPrev, value); }
    public string LyricCurrent { get => _lyricCurrent; private set => Set(ref _lyricCurrent, value); }
    public string LyricNext { get => _lyricNext; private set => Set(ref _lyricNext, value); }
    public bool LyricCurrentIsLine
    {
        get => _hasLyricCurrent;
        private set
        {
            if (_hasLyricCurrent == value) return;
            Set(ref _hasLyricCurrent, value);
            Raise(nameof(LyricCurrentColor));
        }
    }

    private bool _userWantsLyrics = true;

    /// <summary>User toggle (lyrics glyph on the expanded panel).</summary>
    public void ToggleLyrics()
    {
        _userWantsLyrics = !_userWantsLyrics;
        UpdateLyricVisibility();
    }

    private bool LyricsWanted => _userWantsLyrics && _lyricsEnabledBySettings;

    private bool _lyricsEnabledBySettings = true;

    public void ApplyLyricsSettings(int offsetMs, bool enabled)
    {
        if (_lyricOffsetMs == offsetMs && _lyricsEnabledBySettings == enabled) return;
        _lyricOffsetMs = offsetMs;
        _lyricsEnabledBySettings = enabled;
        _lyricIndex = -1;
        UpdateLyricVisibility();
        UpdateLyricWindow();
    }

    public void UpdateLyrics(LyricsState state, LyricsResult? result, int offsetMs, bool enabled)
    {
        _lyricsState = state;
        _lyricsEnabledBySettings = enabled;
        _lyricOffsetMs = offsetMs;
        _lyricLines = state == LyricsState.Synced && result is not null
            ? result.SyncedLines ?? Array.Empty<LrcLine>()
            : Array.Empty<LrcLine>();
        _lyricIndex = -1;
        LyricPrev = LyricNext = "";
        LyricCurrentIsLine = false;
        LyricCurrent = state switch
        {
            LyricsState.Loading => "Loading lyrics...",
            LyricsState.Instrumental => "Instrumental - no lyrics",
            LyricsState.Unavailable => "No lyrics available",
            LyricsState.Plain or LyricsState.Synced => "No lyrics available",
            _ => "",
        };

        // Plain lyrics without timestamps: show the first few lines as a static block.
        if (state == LyricsState.Plain && result is not null)
        {
            var plainLines = (result.PlainLyrics ?? "")
                .Replace("\r", "").Split("\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            LyricPrev = "";
            LyricCurrent = plainLines.Length > 0 ? plainLines[0] : "No lyrics available";
            LyricNext = plainLines.Length > 1 ? plainLines[1] : "";
            LyricCurrentIsLine = plainLines.Length > 0;
            UpdateLyricVisibility();
            return;
        }

        UpdateLyricVisibility();
        UpdateLyricWindow();
    }

    private void UpdateLyricVisibility() => ShowLyricsBlock = LyricsWanted && _lyricsState != LyricsState.Idle;

    /// <summary>Recomputes the 3-line window (previous / current / next) from the
    /// playback position. Called by the 1 Hz timeline tick — cheap by design.</summary>
    public void UpdateLyricWindow()
    {
        if (_lyricLines.Count == 0) return;
        if (_snapshot is null) return;

        var position = _snapshot.CurrentPosition().Add(TimeSpan.FromMilliseconds(_lyricOffsetMs));
        var index = LrcParser.CurrentLineIndex(_lyricLines, position);
        if (index == _lyricIndex) return;
        _lyricIndex = index;

        LyricPrev = index > 0 ? _lyricLines[index - 1].Text : "";
        var current = index >= 0 ? _lyricLines[index].Text : "";
        // Instrumental gaps render as a gentle note instead of an empty block.
        LyricCurrent = current.Length == 0 ? "\u266A" : current;
        LyricCurrentIsLine = current.Length > 0;
        LyricNext = index + 1 < _lyricLines.Count ? _lyricLines[index + 1].Text : "";
        UpdateLyricVisibility();
    }

    public void ClearLyrics()
    {
        _lyricsState = LyricsState.Idle;
        _lyricLines = Array.Empty<LrcLine>();
        _lyricIndex = -1;
        LyricCurrentIsLine = false;
        UpdateLyricVisibility();
        LyricPrev = LyricCurrent = LyricNext = "";
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

    private int _approvalIndex;
    private int _approvalTotal;

    /// <summary>"1 of 3" when several approvals are pending simultaneously.</summary>
    public string ApprovalPosition
    {
        get => _approvalTotal > 1 ? $"{_approvalIndex + 1} of {_approvalTotal}" : "";
        private set { }
    }

    public bool HasMultipleApprovals => _approvalTotal > 1;

    public ICommand NextApprovalCommand { get; }
    public ICommand PrevApprovalCommand { get; }

    /// <summary>Facial expression the mascot shows for this card's state.</summary>
    public MascotExpression Expression
    {
        get => _expression;
        private set => Set(ref _expression, value);
    }
    private MascotExpression _expression = MascotExpression.Calm;

    public void SetApprovalPosition(int index, int total)
    {
        _approvalIndex = index;
        _approvalTotal = total;
        // Bindings target the computed properties, not the backing fields.
        Raise(nameof(ApprovalPosition));
        Raise(nameof(HasMultipleApprovals));
    }

    public required string ApprovalId { get; init; }
    public Action<ApprovalDecision>? Decide { get; init; }
    public Action? OpenInAgent { get; init; }

    public ICommand ApproveCommand { get; }
    public string ApproveLabel => Provider == "Thursday" ? "Allow for this task" : "Approve";
    public ICommand DenyCommand { get; }
    public ICommand OpenCommand { get; }

    public AgentCardViewModel(Action<int>? navigateApprovals = null)
    {
        ApproveCommand = new RelayCommand(() => Decide?.Invoke(ApprovalDecision.Approved));
        DenyCommand = new RelayCommand(() => Decide?.Invoke(ApprovalDecision.Denied));
        OpenCommand = new RelayCommand(() => OpenInAgent?.Invoke());
        if (navigateApprovals is not null)
        {
            NextApprovalCommand = new RelayCommand(() => navigateApprovals(+1));
            PrevApprovalCommand = new RelayCommand(() => navigateApprovals(-1));
        }
        else
        {
            NextApprovalCommand = new RelayCommand(() => { });
            PrevApprovalCommand = new RelayCommand(() => { });
        }
    }

    public void Update(AgentEvent ev)
    {
        Provider = DisplayProvider(ev.Provider);
        Raise(nameof(ApproveLabel));
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
        Expression = ev.Type switch
        {
            AgentEventTypes.ApprovalRequested => MascotExpression.Alert,
            AgentEventTypes.Failed or AgentEventTypes.ToolFailed => MascotExpression.Concerned,
            AgentEventTypes.Completed or AgentEventTypes.TurnFinished => MascotExpression.Happy,
            AgentEventTypes.Started => MascotExpression.Curious,
            AgentEventTypes.Interrupted or AgentEventTypes.Cancelled or AgentEventTypes.SessionEnded
                => MascotExpression.Calm,
            _ => MascotExpression.Focused,
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

    public static string DisplayProvider(string provider) => ProviderNames.Display(provider);

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
        Raise(name);
    }

    protected void Raise(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
