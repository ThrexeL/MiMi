using System.Windows;
using System.Windows.Threading;
using MiMi.Agents;
using MiMi.App.UI.Mascot;
using MiMi.Core;
using MiMi.Media;

namespace MiMi.App.UI;

/// <summary>
/// Composes what the island shows. Phase 3.6: full agent awareness via
/// <see cref="AgentSessionManager"/> — sessions are tracked per (provider, session id),
/// tool/status events update the indicator silently (no island churn), completion and
/// failure events produce short toasts, and approvals keep their sticky, navigable cards.
/// All methods run on the UI thread.
/// </summary>
public sealed class IslandPresenter
{
    private readonly ConfigStore _config;
    private readonly IslandStateManager _state;
    private readonly ApprovalStore _approvals;
    private readonly MediaViewModel _mediaVm;
    private readonly Dispatcher _dispatcher;
    private readonly AgentSessionManager _sessions = new();

    private readonly PomodoroTimer _pomodoro = new();
    private readonly WaterTracker _water;
    private readonly TodoStore _todos;
    private readonly FileShelfStore _shelf;
    private PomodoroViewModel? _pomodoroVm;
    private WaterViewModel? _waterVm;
    private VolumeViewModel? _volumeVm;
    private BrightnessViewModel? _brightnessVm;
    private TodoViewModel? _todoVm;
    private ClipboardViewModel _clipboardVm = null!;
    private WidgetsViewModel _widgetsVm = null!;
    private string? _selfCopiedText;
    private DateTime _selfCopiedUntilUtc;
    private PomodoroView? _pomodoroCachedView;
    private WaterView? _waterCachedView;
    private TodoView? _todoCachedView;
    private ShelfView? _shelfCachedView;
    private VolumeView? _volumeCachedView;
    private BrightnessView? _brightnessCachedView;
    private ClipboardView? _clipboardCachedView;
    private WidgetsView? _widgetsCachedView;
    private Func<WidgetSystemSnapshot>? _widgetSnapshot;
    private readonly UtilityPinState _utilityPins = new();
    private readonly DragRevertPolicy _shelfDrag = new();
    private bool _shelfDragAutoOpened;
    private readonly QuickAccessViewModel _quickAccessVm;
    private QuickAccessView? _quickAccessView;
    private readonly AgentNotificationCoordinator _notifications = new();

    private RestingView _resting;
    private MediaCompactView? _mediaCompact;
    private AgentCompactView? _agentCompact;
    private AgentIndicatorViewModel? _agentIndicatorVm;
    private readonly Dictionary<string, AgentCardViewModel> _approvalCards = new();

    // Pending-approval navigation: newest request becomes current; ‹ › cycle through.
    private readonly List<string> _approvalQueue = new();
    private int _approvalQueueIndex;

    private readonly DispatcherTimer _sessionSweeper;
    private int _sessionRefreshQueued;

    // Living personality: persistent mood + one brief one-shot reaction.
    private readonly DispatcherTimer _reactionTimer;
    private MascotMood? _reaction;
    private MascotMood _persistentMood = MascotMood.Calm;
    private MascotExpression _mascotExpression = MascotExpression.Calm;
    private DateTimeOffset _lastActivityUtc = DateTimeOffset.UtcNow;
    private readonly DispatcherTimer _quietTimer = new() { Interval = TimeSpan.FromSeconds(15) };

    public IslandContent? PinnedContent { get; private set; }

    public IslandPresenter(ConfigStore config, IslandStateManager state, ApprovalStore approvals,
        MediaViewModel mediaVm, Dispatcher dispatcher, WaterTracker water, TodoStore todos,
        FileShelfStore? shelf = null)
    {
        _shelf = shelf ?? new FileShelfStore();
        _config = config;
        _state = state;
        _approvals = approvals;
        _mediaVm = mediaVm;
        _dispatcher = dispatcher;
        _water = water;
        _todos = todos;
        // Initialize Quick Access ViewModel with proper callbacks for opening utilities and closing the hub.
        _quickAccessVm = new QuickAccessViewModel(
            openUtility: kind => _utilityPins.OpenUtilityFromHub(kind),
            back: () => _utilityPins.SetHub(false),
            openAi: () => AiProviderRequested?.Invoke(),
            openSettings: () => SettingsRequested?.Invoke()
        );
        _clipboardVm = new ClipboardViewModel(
            setCapture: enabled => _config.Update(s => s.ClipboardHistoryEnabled = enabled),
            captureNow: CaptureClipboardNow,
            copy: CopyClipboardText);
        _clipboardVm.SetCapturing(_config.Current.ClipboardHistoryEnabled);
        _quickAccessVm.ApplyHiddenPages(_config.Current.QuickAccessHiddenPages);
        _widgetsVm = new WidgetsViewModel();
        _utilityPins.Changed += () => _dispatcher.BeginInvoke(RefreshPinned);
        _pomodoro.PhaseCompleted += phase => _dispatcher.BeginInvoke(() =>
        {
            var label = phase == PomodoroPhase.Focus ? "Focus time complete" : "Break finished";
            PushUtilityNotice(label, phase == PomodoroPhase.Focus ? "Take a break" : "Back to focus");
        });

        _resting = MakeResting();
        _notifications.Configure(_config.Current);
        // Presenter mutates VM state and timers: always run on the UI thread.
        _config.Changed += _ => _dispatcher.BeginInvoke(() =>
        {
            _notifications.Configure(_config.Current);
            _resting = MakeResting();
            _clipboardVm.SetCapturing(_config.Current.ClipboardHistoryEnabled);
            _quickAccessVm.ApplyHiddenPages(_config.Current.QuickAccessHiddenPages);
            RefreshPinned();
        });

        // Session-manager changes arrive on worker threads; coalesce bursts into one
        // dispatcher refresh instead of posting work for every telemetry frame.
        _sessions.SessionChanged += _ => QueueSessionRefresh();

        // ApprovalStore.Resolved is wired once by the composition root (App), which also
        // logs decisions. Subscribing here as well would handle every resolution twice.

        // Low-frequency session sweep (staleness + bounded history) lives in the manager.
        _sessionSweeper = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _sessionSweeper.Tick += (_, _) => RefreshPinned();

        _reactionTimer = new DispatcherTimer();
        _reactionTimer.Tick += (_, _) =>
        {
            _reactionTimer.Stop();
            _reaction = null;
            RefreshPinned();
        };

        // Quiet is activity-based, not input-based: when nothing has needed the face for
        // a while, this tick swaps in the sleepy expression (music and work already
        // outrank sleep, so they need no special case here).
        _quietTimer.Tick += (_, _) =>
        {
            if (_persistentMood != MascotMood.Calm) return;
            if (QuietFor() < MascotMoodPolicy.DefaultSleepAfter) return;
            RefreshPinned();
        };
        _quietTimer.Start();
    }

    private void QueueSessionRefresh()
    {
        if (_dispatcher.CheckAccess())
        {
            RefreshPinned();
            return;
        }
        if (Interlocked.Exchange(ref _sessionRefreshQueued, 1) == 1) return;
        _dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _sessionRefreshQueued, 0);
            RefreshPinned();
        }, DispatcherPriority.Background);
    }

    public AgentSessionManager Sessions => _sessions;

    private bool _proximityAwake;

    /// <summary>True while the cursor is near a sleepy MiMi (window proximity sampler).
    /// Waking shows the awake face; leaving re-dozes after the policy's cooldown.</summary>
    public void SetProximityAwake(bool awake)
    {
        if (_proximityAwake == awake) return;
        _proximityAwake = awake;
        RefreshPinned();
    }

    /// <summary>Whether the current resting face is the sleepy expression (the window
    /// runs its proximity sampler only in that state).</summary>
    public bool IsMascotSleepy => _mascotExpression == MascotExpression.Sleepy;
    public FileShelfStore Shelf => _shelf;

    /// <summary>Files dropped onto the open shelf panel.</summary>
    public void AddToShelf(IEnumerable<string> paths)
    {
        if (_shelf.AddRange(paths) > 0)
        {
            _shelf.Save();
            _shelfCachedView = null; // rows changed: rebuild on next refresh
            RefreshPinned();
        }
    }

    public void RemoveFromShelf(string path)
    {
        _shelf.Remove(path);
        _shelf.Save();
        _shelfCachedView = null;
        RefreshPinned();
    }

    public ShelfView BuildShelfView()
    {
        var rows = _shelf.Items.Select(i => new ShelfRowViewModel(
            i.Path,
            FileShelfStore.FormatSize(i.SizeBytes),
            !_shelf.Exists(i),
            path => RemoveFromShelf(path),
            i.IsDirectory)).ToList();
        return new ShelfView(rows, $"{_shelf.Count} item{(_shelf.Count == 1 ? "" : "s")}");
    }

    /// <summary>Notification decisions that should become Windows toasts while hidden.
    /// The composition root owns the tray; the presenter never displays system UI itself.</summary>
    public event Action<AgentNotice>? WindowsNoticeRequested;
    public event Action? TaskEntryRequested;
    public event Action? AiProviderRequested;
    public event Action? SettingsRequested;

    public AgentNotificationCoordinator Notifications => _notifications;

    private RestingView MakeResting() => new()
    {
        ShowMascot = _config.Current.MascotEnabled,
    };

    // ---- productivity utilities (Phase 4) ---------------------------------------

    public void ConfigureVolume(Func<(float Level, bool Muted, bool IsAvailable)> snapshot,
        Func<float, bool> setVolume, Action toggleMute)
    {
        _volumeVm = new VolumeViewModel(snapshot, setVolume, toggleMute);
        RefreshPinned();
    }

    public void ConfigureBrightness(Func<Task<Interop.LaptopBrightnessControl.Snapshot>> read,
        Func<string, int, Task<bool>> set)
    {
        _brightnessVm = new BrightnessViewModel(read, set);
        _ = _brightnessVm.RefreshAsync();
        RefreshPinned();
    }

    public void ConfigureWidgets(Func<WidgetSystemSnapshot> snapshot)
    {
        _widgetSnapshot = snapshot;
        RefreshPinned();
    }

    /// <summary>Adds copied text to the bounded session-only history without logging it.</summary>
    public void CaptureClipboard(string? text)
    {
        if (DateTime.UtcNow < _selfCopiedUntilUtc
            && string.Equals(text, _selfCopiedText, StringComparison.Ordinal)) return;
        if (_clipboardVm.Add(text))
        {
            _quickAccessVm.UpdateClipboardBadge(_clipboardVm.Count);
            if (_utilityPins.IsPinned(UtilityPinState.Clipboard)) RefreshPinned();
        }
    }

    public void CaptureClipboardNow()
    {
        try
        {
            if (System.Windows.Clipboard.ContainsText())
                CaptureClipboard(System.Windows.Clipboard.GetText());
        }
        catch (Exception ex)
        {
            // Another app can temporarily own the clipboard. Never log clipboard contents.
            Logger.Info($"Clipboard read unavailable: {ex.GetType().Name}.");
        }
    }

    private void CopyClipboardText(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
            _selfCopiedText = text;
            _selfCopiedUntilUtc = DateTime.UtcNow.AddSeconds(2);
        }
        catch (Exception ex) { Logger.Info($"Clipboard write unavailable: {ex.GetType().Name}."); }
    }

    /// <summary>Hover preview uses the same live ViewModel as the pinned hub, but does
    /// not change pin state. Moving the pointer away collapses back to the face.</summary>
    public QuickAccessView BuildQuickAccessView()
    {
        _quickAccessVm.UpdateClock(DateTime.Now);
        _quickAccessVm.UpdateTimerBadge(_pomodoro.IsRunning, _pomodoro.CompletedFocusSessions);
        _quickAccessVm.UpdateShelfBadge(_shelf.Count);
        _quickAccessVm.UpdateClipboardBadge(_clipboardVm.Count);
        return _quickAccessView ??= new QuickAccessView(_quickAccessVm);
    }

    public void PromptAddTask() => TaskEntryRequested?.Invoke();

    public bool AddTask(string title) => AddTask(title, null);

    public bool AddTask(string title, DateTime? due)
    {
        try
        {
            _todos.Add(title, due);
            _todos.Save();
            RefreshPinned();
            return true;
        }
        catch (ArgumentException) { return false; }
        catch (Exception ex)
        {
            Logger.Error("Could not add task", ex);
            return false;
        }
    }

    /// <summary>Tray toggle: opens/closes a utility panel. User-opened panels pin at
    /// UserPanel priority — only approval alerts (and failures) displace them.</summary>
    public void ToggleUtility(string kind) => _utilityPins.TogglePin(kind);

    /// <summary>Pins a utility panel open without closing it (drag-toward-MiMi).</summary>
    public void SetUtilityPin(string kind, bool pinned) => _utilityPins.SetPin(kind, pinned);

    public IReadOnlyCollection<string> UtilityPins =>
        _utilityPins.ActiveUtility is { } active ? new[] { active } : Array.Empty<string>();

    /// <summary>Click the idle face (or the music panel's utilities glyph): the Quick
    /// Access hub opens and stays until the user picks a utility or goes back.</summary>
    public void OpenQuickAccess() => _utilityPins.SetHub(true);

    public void CloseUtilityPanel()
    {
        _utilityPins.ClearUserPins();
        _quickAccessView = null;
        RefreshPinned();
    }

    // ---- file-shelf drag session (drag toward MiMi) ------------------------------

    /// <summary>An Explorer drag entered the island: reveal the shelf as the drop
    /// target at UserPanel priority (approvals still preempt).</summary>
    public void ShelfDragEnter()
    {
        _shelfDrag.Enter(DateTimeOffset.UtcNow);
        if (!_utilityPins.IsPinned(UtilityPinState.Shelf))
        {
            _shelfDragAutoOpened = true;
            _utilityPins.SetPin(UtilityPinState.Shelf, true);
        }
        RefreshPinned();
    }

    /// <summary>The drag left without dropping: revert after the grace period.</summary>
    public void ShelfDragLeave() => _shelfDrag.Leave(DateTimeOffset.UtcNow);

    /// <summary>Closes a shelf that was opened only as a drag preview. A shelf the
    /// user had already pinned remains open.</summary>
    public void EndShelfDragPreview()
    {
        _shelfDrag.Dropped();
        if (_shelfDragAutoOpened)
        {
            _shelfDragAutoOpened = false;
            _utilityPins.SetPin(UtilityPinState.Shelf, false);
        }
        RefreshPinned();
    }

    /// <summary>The drop landed: shelve the files and keep the shelf open briefly.</summary>
    public void ShelfDrop(IEnumerable<string> paths)
    {
        _shelfDrag.Dropped();
        _shelfDragAutoOpened = false; // a completed drop intentionally leaves the shelf open
        AddToShelf(paths);
        _dispatcher.BeginInvoke(ShelfDragRevert, DateTimeOffset.Now + TimeSpan.FromSeconds(4));
    }

    private void ShelfDragRevert()
    {
        if (_shelfDrag.DragActive) return;              // a new drag is in flight
        if (_utilityPins.IsPinned(UtilityPinState.Shelf)) return; // user pinned it
        _utilityPins.ClearUserPins();
        RefreshPinned();
    }

    /// <summary>1 Hz heartbeat from the island window: drives the timer, reminders,
    /// and live utility VMs. Deliberately cheap: when no utility is pinned or running
    /// (the common idle case) it only checks reminder conditions and skips the
    /// full pin recomputation entirely.</summary>
    public void OnSecondTick()
    {
        _pomodoro.Tick();

        // ImpressiveBot's line is live data, not a snapshot: a bot that starts, waits or
        // finishes between panel rebuilds must show up within a second. This keeps the
        // session table honest (a silent bot ages out instead of claiming to work) and
        // asks the open panel to re-read it.
        _sessions.SweepStale(DateTimeOffset.UtcNow);
        ImpressiveBotStatusChanged?.Invoke();

        var now = DateTimeOffset.Now;
        var utilityActive = _utilityPins.ActiveUtility is not null || _pomodoro.Phase != PomodoroPhase.Idle;
        if (!utilityActive)
        {
            // Reminder checks only (allocation-free when nothing is due).
            if (_water.ShouldRemindNow(now))
            {
                _water.MarkReminded(now);
                PushWaterReminder();
            }
            foreach (var due in _todos.DueNow(now.LocalDateTime))
            {
                _todos.MarkReminded(due.Id);
                PushTodoDue(due.Title, due.Id);
            }
            return;
        }

        RefreshPinned(); // timer countdown + phase changes repaint via the pinned VM

        var now2 = DateTimeOffset.Now;
        if (_water.ShouldRemindNow(now2))
        {
            _water.MarkReminded(now2);
            PushWaterReminder();
        }
        foreach (var due in _todos.DueNow(now2.LocalDateTime))
        {
            _todos.MarkReminded(due.Id);
            PushTodoDue(due.Title, due.Id);
        }
    }

    /// <summary>System HUD: same-title HUDs replace the running alert (rapid volume
    /// keypresses update one toast instead of stacking), auto-expiring shortly after.</summary>
    public void PushHud(string iconGlyph, string title, string subLabel, double? progress = null,
        TimeSpan? duration = null, string? hudId = null)
    {
        var id = "hud:" + (hudId ?? title);
        _state.Clear(id);
        _state.Push(new IslandAlert
        {
            Id = id,
            Priority = IslandPriority.AgentCompletion,
            Content = new HudView(iconGlyph, title, subLabel, progress),
            Duration = duration ?? TimeSpan.FromSeconds(2),
        });
    }

    /// <summary>Hydration reminder with quick-add and snooze actions.</summary>
    private void PushWaterReminder()
    {
        UpdateUtilityVms();
        var view = new WaterReminderView(_waterVm!, snooze: () =>
        {
            _water.Snooze(TimeSpan.FromMinutes(10), DateTimeOffset.Now);
            _state.Clear("hud:water-reminder");
        });
        _state.Push(new IslandAlert
        {
            Id = "hud:water-reminder",
            Priority = IslandPriority.Utility,
            Content = view,
            Duration = TimeSpan.FromSeconds(30),
        });
    }

    /// <summary>To-do due reminder with a mark-done action and a list shortcut.</summary>
    private void PushTodoDue(string title, Guid id)
    {
        _state.Push(new IslandAlert
        {
            Id = "hud:todo-due:" + id,
            Priority = IslandPriority.Utility,
            Content = new TodoDueView(title,
                complete: () =>
                {
                    _todos.Complete(id);
                    _todos.Save();
                    _state.Clear("hud:todo-due:" + id);
                },
                openList: () =>
                {
                    SetUtilityPin(UtilityPinState.Todo, true);
                    _state.Clear("hud:todo-due:" + id);
                },
                snooze: () =>
                {
                    _todos.Snooze(id, TimeSpan.FromMinutes(10), DateTime.Now);
                    _todos.Save();
                    _state.Clear("hud:todo-due:" + id);
                }),
            Duration = TimeSpan.FromSeconds(30),
        });
    }


    private void PushUtilityNotice(string title, string message)
    {
        var card = new AgentCardViewModel { ApprovalId = "", Decide = _ => { }, OpenInAgent = () => { } };
        card.Update(AgentEventFactory.Create("mimi", "utility", AgentEventTypes.Status,
            title: title, message: message));
        _state.Push(new IslandAlert
        {
            Id = "util:" + title + ":" + Guid.NewGuid().ToString("N")[..6],
            Priority = IslandPriority.AgentCompletion,
            Content = new AgentExpandedView(card),
            Duration = TimeSpan.FromSeconds(3),
        });
    }

    private void UpdateUtilityVms()
    {
        var todoPinned = _utilityPins.IsPinned(UtilityPinState.Todo);
        var widgetsPinned = _utilityPins.IsPinned(UtilityPinState.Widgets);
        // Pomodoro
        _pomodoroVm ??= new PomodoroViewModel(
            playPause: () =>
            {
                if (_pomodoro.Phase == PomodoroPhase.Idle) _pomodoro.StartFocus();
                else if (_pomodoro.IsRunning) _pomodoro.Pause();
                else _pomodoro.Resume();
                RefreshPinned();
            },
            stop: () =>
            {
                _pomodoro.Reset();
                RefreshPinned();
            });
        var (phase, remaining, progress, sessions, running) = _pomodoro.Snapshot();
        var phaseLabel = phase switch
        {
            PomodoroPhase.Focus => "Focus time",
            PomodoroPhase.ShortBreak => "Short break",
            PomodoroPhase.LongBreak => "Long break",
            _ => "Focus time",
        };
        _pomodoroVm.Update(phaseLabel,
            phase == PomodoroPhase.Idle
                ? _pomodoro.FocusDuration.ToString(@"mm\:ss")
                : remaining.ToString(@"mm\:ss"),
            running ? "Stay on it" : phase == PomodoroPhase.Idle ? "Press play to begin" : "Paused",
            progress, running, phase == PomodoroPhase.Idle, sessions);

        // Water
        _waterVm ??= new WaterViewModel(() =>
        {
            _water.Add(_water.QuickAddMl);
            _water.Save(WaterTracker.DefaultPath);
            RefreshPinned();
        });
        _waterVm.Update(
            $"{_water.TodayMl / 1000.0:0.0} / {_water.DailyGoalMl / 1000.0:0.0} L",
            _water.GoalFraction,
            _water.GoalReached ? "Goal reached" : $"{_water.RemainingMl} ml to go");

        _volumeVm?.Refresh();

        if (widgetsPinned)
        {
            _widgetsVm ??= new WidgetsViewModel();
            WidgetSystemSnapshot? snapshot = null;
            try { snapshot = _widgetSnapshot?.Invoke(); }
            catch (Exception ex) { Logger.Info($"Widgets snapshot unavailable: {ex.GetType().Name}: {ex.Message}"); }
            _widgetsVm.Update(DateTime.Now, _todos.Open, _pomodoro.CompletedFocusSessions,
                _pomodoro.IsRunning, _water.TodayMl, _water.DailyGoalMl, snapshot);
        }

        // To-do (max 4 open rows) — rebuilt only while the panel is open.
        if (!todoPinned) return;
        _todoVm ??= new TodoViewModel(
            add: title =>
            {
                try { _todos.Add(title); _todos.Save(); return true; }
                catch (ArgumentException) { return false; }
            },
            refresh: () => RefreshPinned());
        var open = _todos.Open;
        _todoVm.HeaderCount = $"{open.Count} of {open.Count + _todos.Done.Count}";
        _todoVm.Rows = open.Take(4).Select(t => new TodoRowViewModel(
            t.Id, t.Title,
            t.Due is { } d ? d.ToString("HH:mm") : "",
            id => { _todos.Complete(id); _todos.Save(); RefreshPinned(); })).ToList();
    }

    // ---- media ------------------------------------------------------------------

    public void OnMediaChanged(MediaSnapshot? snapshot)
    {
        TouchActivity();
        // Reuse the compact view: MediaViewModel bindings update live, so recreating it
        // per snapshot would needlessly re-trigger the pill's fade animation.
        if (snapshot is null) _mediaCompact = null;
        else _mediaCompact ??= new MediaCompactView(_mediaVm);
        RefreshPinned();
    }

    /// <summary>Marks "MiMi has something to reflect". Sleep is measured from this, never
    /// from desktop input (the display may already be off).</summary>
    private void TouchActivity() => _lastActivityUtc = DateTimeOffset.UtcNow;

    private TimeSpan QuietFor()
    {
        var quiet = DateTimeOffset.UtcNow - _lastActivityUtc;
        return quiet < TimeSpan.Zero ? TimeSpan.Zero : quiet;
    }

    // ---- pinned composition ------------------------------------------------------

    public void RefreshPinned()
    {
        var agentsEnabled = _config.Current.AgentsModuleEnabled;
        var activeSessions = agentsEnabled ? _sessions.Active : Array.Empty<AgentSession>();
        var workingCount = activeSessions.Count(IsWorking);

        var mediaActive = _config.Current.MediaModuleEnabled && _mediaCompact is not null;
        if (!mediaActive) _mediaCompact = null;

        // Mood is computed before view composition so the face and indicator agree.
        _persistentMood = ComputePersistentMood(workingCount, activeSessions, mediaActive);
        if (_reaction is { } reaction && !MascotMoodPolicy.ReactionAllowed(_persistentMood))
        {
            _reaction = null; // an urgent state arrived; never let a stale one-shot win
            _reactionTimer.Stop();
        }
        // Mouse-proximity wake: a sleepy MiMi shows its awake face while the cursor
        // is near (Phase 4). ComputePersistentMood stays authoritative; proximity only
        // overrides the Sleepy result, and only when the doze feature is enabled.
        if (_persistentMood == MascotMood.Sleepy && _proximityAwake && _config.Current.MascotSleepyEnabled)
            _persistentMood = MascotMood.Calm;

        _mascotExpression = ToExpression(_reaction ?? _persistentMood);
        _resting.Expression = _mascotExpression;
        _resting.ShelfCount = _shelf.Count;
        _quickAccessVm.UpdateClock(DateTime.Now);
        _quickAccessVm.UpdateTimerBadge(_pomodoro.IsRunning, _pomodoro.CompletedFocusSessions);
        _quickAccessVm.UpdateShelfBadge(_shelf.Count);
        _quickAccessVm.UpdateClipboardBadge(_clipboardVm.Count);

        // Coexistence rule: with music playing, the media pill stays and carries a tiny
        // agent-count badge; without music, the agent indicator gets the pill.
        var agentsActive = agentsEnabled && workingCount > 0;
        if (agentsActive)
        {
            UpdateAgentIndicator(activeSessions, workingCount);
            _agentCompact ??= new AgentCompactView(_agentIndicatorVm!);
        }
        else
        {
            _agentCompact = null;
        }

        if (_mediaCompact is not null)
        {
            _mediaCompact.AgentBadgeCount = agentsActive ? workingCount : 0;
            _mediaCompact.ShelfCount = _shelf.Count;
        }

        // Utility pinning (Phase 4). User-opened panels pin at UserPanel priority —
        // only approval alerts displace them. A merely RUNNING focus timer pins at
        // Utility, below agent status, so agents never lose their pill to the timer.
        UpdateUtilityVms();
        var pomodoroVisible = _pomodoro.Phase != PomodoroPhase.Idle;
        // Cached wrappers: the VMs update in place, so re-wrapping per refresh would
        // force a full content swap (and a render) every tick.
        var activeUtility = _utilityPins.ActiveUtility
            ?? (pomodoroVisible && !_shelfDrag.DragActive ? UtilityPinState.Pomodoro : null);
        
        // FIX: Clear hub cache when switching from Hub to utility to prevent text jumbling
        if (_quickAccessView != null && activeUtility != UtilityPinState.Hub)
        {
            _quickAccessView = null;
        }
        
        IslandContent? utilityView = activeUtility switch
        {
            UtilityPinState.Hub => BuildQuickAccessView(),
            UtilityPinState.Pomodoro => _pomodoroCachedView ??= new PomodoroView(_pomodoroVm!),
            UtilityPinState.Water => _waterCachedView ??= new WaterView(_waterVm!),
            UtilityPinState.Todo => _todoCachedView ??= new TodoView(_todoVm!),
            UtilityPinState.Shelf => _shelfCachedView ??= BuildShelfView(),
            UtilityPinState.Volume => _volumeCachedView ??= _volumeVm is null ? null : new VolumeView(_volumeVm),
            UtilityPinState.Brightness => _brightnessCachedView ??= _brightnessVm is null ? null : new BrightnessView(_brightnessVm),
            UtilityPinState.Clipboard => _clipboardCachedView ??= new ClipboardView(_clipboardVm),
            UtilityPinState.Widgets => _widgetsCachedView ??= new WidgetsView(_widgetsVm),
            _ => null,
        };
        var utilityPriority = activeUtility is not null ? IslandPriority.UserPanel : IslandPriority.Utility;
        if (_shelfDrag.DragActive) utilityPriority = IslandPriority.UserPanel; // drag target stays up

        if (utilityView is not null && (utilityPriority == IslandPriority.UserPanel || !agentsActive))
        {
            PinnedContent = utilityView;
            _state.SetPinned(utilityPriority, PinnedContent);
        }
        else if (agentsActive && !mediaActive)
        {
            PinnedContent = _agentCompact!;
            _state.SetPinned(IslandPriority.AgentCompletion, PinnedContent);
        }
        else if (mediaActive)
        {
            PinnedContent = _mediaCompact!;
            _state.SetPinned(IslandPriority.Media, PinnedContent);
        }
        else
        {
            PinnedContent = utilityView ?? _resting;
            _state.SetPinned(utilityView is not null ? utilityPriority : IslandPriority.Idle, PinnedContent);
        }

        if (_sessions.All.Count > 0) _sessionSweeper.Start(); else _sessionSweeper.Stop();

        // Aggregate multi-agent summary: throttled and only when the wording changes.
        var aggregate = _notifications.NotifyAggregate(workingCount, _sessions.PendingApprovalCount);
        if (aggregate is not null) PushNotice(aggregate);
    }

    /// <summary>The one definition of "actively working" used by both the count and the
    /// single-session label, so the two can never disagree.</summary>
    private static bool IsWorking(AgentSession s) =>
        !s.Ended && !s.Stale
        && s.State is SessionActivity.Started or SessionActivity.Working
            or SessionActivity.ExecutingTool or SessionActivity.AwaitingApproval or SessionActivity.WaitingForInput;

    private void UpdateAgentIndicator(IReadOnlyList<AgentSession> sessions, int workingCount)
    {
        _agentIndicatorVm ??= new AgentIndicatorViewModel();
        var active = sessions.Where(IsWorking).ToArray();
        var expression = _mascotExpression == MascotExpression.Alert
            ? MascotExpression.Alert
            : active.Any(s => s.State == SessionActivity.ExecutingTool)
                ? MascotExpression.Lively
                : active.Any(s => s.State is SessionActivity.Started or SessionActivity.WaitingForInput)
                    ? MascotExpression.Curious
                    : _mascotExpression;
        if (workingCount > 1)
        {
            var pending = sessions.Sum(s => s.PendingApprovalIds.Count);
            _agentIndicatorVm.Update($"{workingCount} agents active", expression, pending);
        }
        else
        {
            var s = active[0];
            var provider = AgentCardViewModel.DisplayProvider(s.Provider);
            _agentIndicatorVm.Update($"{provider} \u00B7 {StatusLabel(s)}", expression, s.PendingApprovalIds.Count);
        }
    }

    /// <summary>Persistent mood from confirmed state only. Sleep follows quiet time —
    /// time since MiMi last had an agent event, approval, notice, or media change — never
    /// desktop input idleness.</summary>
    private MascotMood ComputePersistentMood(int workingCount, IReadOnlyList<AgentSession> sessions, bool mediaActive)
    {
        var approvalPending = _approvals.Pending.Count > 0;
        var errorActive = sessions.Any(s => !s.Stale && s.State is SessionActivity.Error);
        var musicPlaying = mediaActive && _mediaVm.IsPlaying;
        return MascotMoodPolicy.Persistent(new MascotMoodInput(
            approvalPending,
            errorActive,
            workingCount,
            musicPlaying,
            QuietFor(),
            _config.Current.MascotSleepyEnabled,
            MascotMoodPolicy.DefaultSleepAfter));
    }

    private static MascotExpression ToExpression(MascotMood mood) => mood switch
    {
        MascotMood.Sleepy => MascotExpression.Sleepy,
        MascotMood.Curious => MascotExpression.Curious,
        MascotMood.Focused => MascotExpression.Focused,
        MascotMood.Alert => MascotExpression.Alert,
        MascotMood.Happy => MascotExpression.Happy,
        MascotMood.Concerned => MascotExpression.Concerned,
        MascotMood.Lively => MascotExpression.Lively,
        _ => MascotExpression.Calm,
    };

    /// <summary>Brief one-shot reaction, always subordinate to approval/error/working.</summary>
    private void React(MascotMoment moment)
    {
        if (!_config.Current.MascotReactionsEnabled || !MascotOptions.ReactionsEnabled) return;

        // Recompute from the just-applied event so a completion sees "not working".
        var active = _config.Current.AgentsModuleEnabled ? _sessions.Active : Array.Empty<AgentSession>();
        var workingCount = active.Count(IsWorking);
        var mediaActive = _config.Current.MediaModuleEnabled && _mediaCompact is not null;
        _persistentMood = ComputePersistentMood(workingCount, active, mediaActive);

        if (MascotMoodPolicy.Reaction(_persistentMood, moment) is not { } reaction) return;

        _reaction = reaction;
        _reactionTimer.Stop();
        _reactionTimer.Interval = TimeSpan.FromMilliseconds(
            reaction == MascotMood.Happy ? 1000 : 1200);
        _reactionTimer.Start();
    }

    /// <summary>Called after the user acts on MiMi from a system surface (tray/notification).
    /// Counts as activity so a dozing face wakes before the request is shown.</summary>
    public void NoteUserActivity()
    {
        TouchActivity();
        if (_persistentMood == MascotMood.Sleepy) RefreshPinned();
    }

    internal static string StatusLabel(AgentSession s) => s.Stale ? "Status unknown" : s.State switch
    {
        SessionActivity.Started => "Started",
        SessionActivity.Working => ThursdayProgress(s, "Working"),
        SessionActivity.ExecutingTool => ThursdayProgress(s, "Executing tool"),
        SessionActivity.AwaitingApproval => "Waiting for approval",
        SessionActivity.WaitingForInput => "Waiting for input",
        SessionActivity.TurnFinished => "Turn finished",
        SessionActivity.Interrupted => "Interrupted",
        SessionActivity.Error => "Error",
        SessionActivity.Ended => "Ended",
        _ => "Status unknown",
    };

    /// <summary>Thursday and ImpressiveBot report a live one-line summary; show it in place
    /// of the generic state word so the island says what the bot is actually doing.</summary>
    private static string ThursdayProgress(AgentSession s, string fallback) =>
        (s.Provider == "thursday" || ProviderNames.IsImpressiveBot(s.Provider))
        && !string.IsNullOrWhiteSpace(s.LastSummary)
            ? (s.LastSummary.Length > 48 ? s.LastSummary[..47] + "\u2026" : s.LastSummary)
            : fallback;

    internal static string StateColor(AgentSession s) => s.Stale ? "#9A9AA0" : s.State switch
    {
        SessionActivity.AwaitingApproval or SessionActivity.WaitingForInput => "#FFB454",
        SessionActivity.Error => "#FF6B6B",
        SessionActivity.TurnFinished => "#5DD97C",
        SessionActivity.Ended => "#9A9AA0",
        _ => "#FFD79A",
    };

    /// <summary>Resolved once and re-checked only when it was missing, so installing the CLI
    /// later is picked up without a restart.</summary>
    private string? _impressiveBotCli;
    private bool _impressiveBotCliChecked;

    /// <summary>Raised once a second while the island runs, so an open activity panel can
    /// re-read the ImpressiveBot line without a full content rebuild.</summary>
    public event Action? ImpressiveBotStatusChanged;

    /// <summary>What MiMi can truthfully say about ImpressiveBot right now, from live sessions.</summary>
    public ImpressiveBotStatus ImpressiveBotStatus()
    {
        if (!_impressiveBotCliChecked || _impressiveBotCli is null)
        {
            _impressiveBotCli = ImpressiveBotStatusReader.ResolveCli();
            _impressiveBotCliChecked = true;
        }
        return ImpressiveBotStatusReader.Build(_sessions.All, _impressiveBotCli);
    }
    /// <summary>Rows for the expanded activity panel, built fresh on each hover.</summary>
    public AgentActivityPanelView BuildAgentPanel()
    {
        var recent = _sessions.Active;
        var live = recent.Where(s => s.State is SessionActivity.Started or SessionActivity.Working
            or SessionActivity.ExecutingTool or SessionActivity.AwaitingApproval or SessionActivity.WaitingForInput).ToList();
        var visible = live.Concat(recent.Except(live).Take(3));
        var previousSection = "";
        var rows = visible.Select(s =>
        {
            var section = s.State == SessionActivity.AwaitingApproval ? "NEEDS APPROVAL"
                : live.Contains(s) ? "ACTIVE" : "RECENT";
            var heading = section != previousSection ? section : "";
            previousSection = section;
            var provider = ProviderNames.IsImpressiveBot(s.Provider)
                ? (ProviderNames.BotName(s.Provider) is { Length: > 0 } bot
                    ? "ImpressiveBot · " + bot
                    : "ImpressiveBot")
                : AgentCardViewModel.DisplayProvider(s.Provider);
            var sessionId = s.SessionId ?? "";
            return new AgentSessionRow
            {
                Section = heading,
                Provider = provider,
                SessionId = sessionId,
                CopySessionCommand = new RelayCommand(() => CopyClipboardText(
                    string.IsNullOrWhiteSpace(sessionId) ? provider : $"{provider}\nSession: {sessionId}")),
                Project = string.IsNullOrEmpty(s.Project) ? "\u2014" : s.Project,
            StatusLabel = StatusLabel(s),
            LastAction = string.IsNullOrEmpty(s.LastSummary) ? StatusLabel(s) : s.LastSummary,
            AgeLabel = AgeLabel(s.LastEventUtc),
            PendingApprovals = s.PendingApprovalIds.Count,
            StateBrush = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(StateColor(s))),
            Timeline = s.History.TakeLast(live.Contains(s) ? 2 : 1).Select(h => new AgentTimelineItem
            {
                TimeLabel = h.TimestampUtc.ToLocalTime().ToString("HH:mm"),
                Text = h.Text,
                Kind = h.Kind,
            }).ToList(),
        }; }).ToList();
        return new AgentActivityPanelView(rows, ImpressiveBotStatus());
    }

    private static string AgeLabel(DateTimeOffset utc)
    {
        var age = DateTimeOffset.UtcNow - utc;
        if (age < TimeSpan.FromSeconds(60)) return $"{(int)age.TotalSeconds}s ago";
        if (age < TimeSpan.FromSeconds(3600)) return $"{(int)age.TotalMinutes}m ago";
        return $"{(int)age.TotalHours}h ago";
    }

    // ---- agent events ---------------------------------------------------------------

    public void OnAgentEvent(AgentEvent ev)
    {
        TouchActivity();
        // Every event updates session tracking first — display flows from state.
        if (!string.IsNullOrEmpty(ev.SessionId)) _sessions.Apply(ev);

        switch (ev.Type)
        {
            case AgentEventTypes.ApprovalRequested:
            {
                var request = _approvals.Get(ev.ApprovalRequestId!)
                    ?? _approvals.Register(ev)
                    // Register can race another registration path; re-read before giving up.
                    ?? _approvals.Get(ev.ApprovalRequestId!);
                if (request is not { IsPending: true })
                {
                    Logger.Warn($"Approval '{ev.ApprovalRequestId}' ignored (unknown, resolved, or expired).");
                    // Session tracking already saw the event; a rejected request must not
                    // leave the indicator stuck on "Waiting for approval".
                    if (ev.ApprovalRequestId is { } ignoredId)
                        _sessions.MarkApprovalResolved(ev.Provider, ev.SessionId, ignoredId);
                    return;
                }
                Logger.Info($"Approval requested: id='{ev.ApprovalRequestId}' provider='{ev.Provider}' session='{ev.SessionId}' project='{ev.Project}'.");
                EnqueueApproval(ev.ApprovalRequestId!, isNavigating: false);
                foreach (var notice in _notifications.NotifyApproval(request))
                    if (notice.WindowsEligible) WindowsNoticeRequested?.Invoke(notice);
                break;
            }

            case AgentEventTypes.ApprovalResolved:
                if (ev.ApprovalRequestId is { } resolvedId)
                {
                    var decision = ev.Metadata.TryGetValue("decision", out var d) &&
                                   Enum.TryParse<ApprovalDecision>(d, ignoreCase: true, out var parsed)
                        ? parsed : ApprovalDecision.Dismissed;
                    _approvals.TryDecide(resolvedId, decision, "provider");
                }
                break;

            default:
            {
                // Living personality reacts to confirmed moments only; tool/status churn
                // leaves the face in its current focused mood.
                if (ev.Type == AgentEventTypes.Started) React(MascotMoment.AgentStarted);
                else if (ev.Type is AgentEventTypes.Completed or AgentEventTypes.TurnFinished)
                    React(MascotMoment.TurnCompleted);

                // Central policy decides what deserves a notice. Tool/status churn stays
                // silent; start/completion/failure produce island notices via the state
                // manager, so approvals always outrank them.
                foreach (var notice in _notifications.OnEvent(ev))
                {
                    if (notice.ShowOnIsland) PushNotice(notice);
                    if (notice.WindowsEligible) WindowsNoticeRequested?.Invoke(notice);
                }
                break;
            }
        }

        RefreshPinned();
    }

    private void PushNotice(AgentNotice notice)
    {
        // Every island-eligible notice carries an explicit duration; sticky approval
        // cards are owned by the approval queue instead.
        if (notice.Duration is not { } duration) return;

        var ev = AgentEventFactory.Create(notice.Provider, notice.SessionId, notice.EventType,
            title: notice.Title,
            message: notice.Body,
            project: notice.Project,
            approvalRequestId: notice.ApprovalRequestId);
        var card = new AgentCardViewModel
        {
            ApprovalId = "",
            Decide = _ => { },
            OpenInAgent = () => OpenInAgent(ev),
        };
        card.Update(ev);
        _state.Push(new IslandAlert
        {
            Id = "notice:" + notice.Key,
            Priority = notice.Priority,
            Content = new AgentExpandedView(card),
            Duration = duration,
        });
    }

    private void OpenInAgent(AgentEvent ev)
    {
        try
        {
            var text = $"[{AgentCardViewModel.DisplayProvider(ev.Provider)}] session {ev.SessionId}" +
                       (ev.ApprovalRequestId is { Length: > 0 } ap ? $" approval {ap}" : "") +
                       (ev.Project.Length > 0 ? $" project {ev.Project}" : "");
            Clipboard.SetText(text);
            Logger.Info($"Open in Agent: copied '{text}'.");
        }
        catch (Exception ex) { Logger.Warn($"Open in Agent failed: {ex.Message}"); }
    }

    // ---- approvals: queue + navigation -------------------------------------------------

    /// <summary>Shows the newest (or navigated) pending approval; earlier ones stay
    /// queued and reachable via the card's ‹ › controls.</summary>
    private void EnqueueApproval(string approvalId, bool isNavigating)
    {
        if (!_approvalQueue.Contains(approvalId)) _approvalQueue.Add(approvalId);
        if (!isNavigating) _approvalQueueIndex = _approvalQueue.Count - 1; // newest wins the view

        var request = _approvals.Get(approvalId);
        if (request is not { IsPending: true }) return;

        var card = new AgentCardViewModel(
            navigateApprovals: delta => _dispatcher.BeginInvoke(() => NavigateApprovals(delta)))
        {
            ApprovalId = approvalId,
            Decide = decision => _approvals.TryDecide(approvalId, decision, "user"),
            OpenInAgent = () =>
            {
                try
                {
                    var text = $"[{AgentCardViewModel.DisplayProvider(request.Provider)}] session {request.SessionId}" +
                               $" approval {approvalId}" +
                               (request.Project.Length > 0 ? $" project {request.Project}" : "");
                    Clipboard.SetText(text);
                    Logger.Info($"Open in Agent: copied '{text}'.");
                }
                catch (Exception ex) { Logger.Warn($"Open in Agent failed: {ex.Message}"); }
            },
        };
        card.Update(AgentEventFactory.Create(request.Provider, request.SessionId,
            AgentEventTypes.ApprovalRequested,
            title: "Approval Required",
            message: request.Message,
            project: request.Project,
            approvalRequestId: request.ApprovalRequestId,
            metadata: new Dictionary<string, string>
            {
                ["action_kind"] = request.ActionKind,
                ["command"] = request.Command,
            }));
        card.SetApprovalPosition(_approvalQueueIndex, _approvalQueue.Count);
        _approvalCards[approvalId] = card;

        // Only the current approval holds an alert; the rest live in the queue.
        foreach (var other in _approvalQueue.Where(id => id != approvalId).ToList())
            _state.Clear("approval:" + other);

        _state.Push(new IslandAlert
        {
            Id = "approval:" + approvalId,
            Priority = IslandPriority.Approval,
            Content = new ApprovalView(card),
            // Sticky: stays until resolved/expired — a song change never displaces it.
        });
        Logger.Info($"Approval card '{approvalId}' shown ({_approvalQueueIndex + 1} of {_approvalQueue.Count}).");
    }

    private void NavigateApprovals(int delta)
    {
        if (_approvalQueue.Count == 0) return;
        _approvalQueueIndex = ((_approvalQueueIndex + delta) % _approvalQueue.Count + _approvalQueue.Count) % _approvalQueue.Count;
        EnqueueApproval(_approvalQueue[_approvalQueueIndex], isNavigating: true);
    }

    /// <summary>Brings a specific pending approval into view (used by notification
    /// activation). Expired or unknown requests are ignored safely.</summary>
    public bool ShowApproval(string approvalId)
    {
        TouchActivity();
        var request = _approvals.Get(approvalId);
        if (request is not { IsPending: true })
        {
            Logger.Info($"Notification activation ignored: approval '{approvalId}' is no longer pending.");
            return false;
        }
        if (!_approvalQueue.Contains(approvalId)) _approvalQueue.Add(approvalId);
        _approvalQueueIndex = Math.Max(0, _approvalQueue.IndexOf(approvalId));
        EnqueueApproval(approvalId, isNavigating: true);
        return true;
    }

    public void OnApprovalResolved(ApprovalRequest request)
    {
        TouchActivity();
        Logger.Info($"Approval resolved in UI: id='{request.ApprovalRequestId}' decision={request.Decision} by='{request.ResolvedBy}'.");
        _approvalCards.Remove(request.ApprovalRequestId);
        _state.Clear("approval:" + request.ApprovalRequestId);

        // Session indicator follows the decision (UI decision or provider-side).
        _sessions.MarkApprovalResolved(request.Provider, request.SessionId, request.ApprovalRequestId);

        var selectedId = _approvalQueue.ElementAtOrDefault(_approvalQueueIndex);
        var removedIndex = _approvalQueue.IndexOf(request.ApprovalRequestId);
        if (_approvalQueue.Remove(request.ApprovalRequestId) && _approvalQueue.Count > 0)
        {
            if (string.Equals(selectedId, request.ApprovalRequestId, StringComparison.Ordinal))
                _approvalQueueIndex = Math.Clamp(removedIndex, 0, _approvalQueue.Count - 1);
            else
                _approvalQueueIndex = Math.Clamp(
                    _approvalQueueIndex - (removedIndex >= 0 && removedIndex < _approvalQueueIndex ? 1 : 0),
                    0, _approvalQueue.Count - 1);
            EnqueueApproval(_approvalQueue[_approvalQueueIndex], isNavigating: true);
        }

        // Brief confirmation toast so the user sees the decision registered.
        var decisionLabel = request.Decision switch
        {
            ApprovalDecision.Approved => "Approved",
            ApprovalDecision.Denied => "Denied",
            ApprovalDecision.Expired => "Expired",
            _ => "Resolved",
        };
        var toast = new AgentCardViewModel
        {
            ApprovalId = "", Decide = _ => { }, OpenInAgent = () => { },
        };
        toast.Update(AgentEventFactory.Create(request.Provider, request.SessionId, AgentEventTypes.Status,
            title: $"Decision sent: {decisionLabel}",
            message: request.Project.Length > 0 ? $"Project: {request.Project}" : ""));
        _state.Push(new IslandAlert
        {
            Id = "resolved:" + request.ApprovalRequestId,
            Priority = IslandPriority.AgentCompletion,
            Content = new AgentExpandedView(toast),
            Duration = TimeSpan.FromSeconds(1.6),
        });
        RefreshPinned();
    }

    // ---- simulation (tray menu; exercises the real pipeline, not a mock UI) ----------

    public void SimulateAgentSequence()
    {
        const string provider = "claude-code";
        const string session = "sim-session";
        const string project = "MiMi";

        void Publish(AgentEvent ev) => _dispatcher.BeginInvoke(() => OnAgentEvent(ev));

        Publish(AgentEventFactory.Create(provider, session, AgentEventTypes.Started,
            title: "Session started", message: "Working on src/MiMi.App", project: project));

        _ = Task.Run(async () =>
        {
            await Task.Delay(800);
            Publish(AgentEventFactory.Create(provider, session, AgentEventTypes.PromptSubmitted,
                title: "Working", message: "Received a new task.", project: project));

            await Task.Delay(1200);
            Publish(AgentEventFactory.Create(provider, session, AgentEventTypes.ToolStarted,
                title: "Executing tool", message: "Building the solution before continuing.",
                project: project, metadata: new Dictionary<string, string> { ["tool"] = "Bash: dotnet build" }));

            await Task.Delay(1800);
            Publish(AgentEventFactory.Create(provider, session, AgentEventTypes.TurnFinished,
                title: "Turn finished", message: "Build completed.", project: project));

            await Task.Delay(1500);
            Publish(AgentEventFactory.Create(provider, session, AgentEventTypes.ApprovalRequested,
                title: "Approval Required",
                message: "Wants to run a terminal command.",
                project: project,
                approvalRequestId: "sim-" + Guid.NewGuid().ToString("N")[..8],
                metadata: new Dictionary<string, string>
                {
                    ["action_kind"] = "Execute Command",
                    ["command"] = "npm run build",
                }));
        });
    }
}
