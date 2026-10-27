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
    private TodoViewModel? _todoVm;
    private readonly HashSet<string> _utilityPins = new(StringComparer.Ordinal);
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
            RefreshPinned();
        });

        // Session-manager changes arrive on worker threads; refresh silently on the UI.
        _sessions.SessionChanged += _ => _dispatcher.BeginInvoke(RefreshPinned);

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

    public AgentSessionManager Sessions => _sessions;
    public IReadOnlyCollection<string> UtilityPins => _utilityPins;

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
            RefreshPinned();
        }
    }

    public void RemoveFromShelf(string path)
    {
        _shelf.Remove(path);
        _shelf.Save();
        RefreshPinned();
    }

    public ShelfView BuildShelfView()
    {
        var rows = _shelf.Items.Select(i => new ShelfRowViewModel(
            i.Path,
            FileShelfStore.FormatSize(i.SizeBytes),
            !_shelf.Exists(i),
            path => RemoveFromShelf(path))).ToList();
        return new ShelfView(rows, $"{_shelf.Count} item{(_shelf.Count == 1 ? "" : "s")}");
    }

    /// <summary>Notification decisions that should become Windows toasts while hidden.
    /// The composition root owns the tray; the presenter never displays system UI itself.</summary>
    public event Action<AgentNotice>? WindowsNoticeRequested;

    public AgentNotificationCoordinator Notifications => _notifications;

    private RestingView MakeResting() => new()
    {
        ShowMascot = _config.Current.MascotEnabled,
    };

    // ---- productivity utilities (Phase 4) ---------------------------------------

    /// <summary>Tray toggle: opens/closes a utility panel. User-opened panels pin at
    /// UserPanel priority — only approval alerts (and failures) displace them.</summary>
    public void ToggleUtility(string kind)
    {
        if (!_utilityPins.Remove(kind)) _utilityPins.Add(kind);
        RefreshPinned();
    }

    /// <summary>1 Hz heartbeat from the island window: drives the timer, reminders,
    /// and live utility VMs. Deliberately cheap when nothing is pinned.</summary>
    public void OnSecondTick()
    {
        _pomodoro.Tick();
        RefreshPinned(); // timer countdown + phase changes repaint via the pinned VM

        var now = DateTimeOffset.Now;
        if (_water.ShouldRemindNow(now))
        {
            _water.MarkReminded(now);
            PushUtilityNotice("Time to drink water", $"{_water.RemainingMl} ml to go");
        }

        foreach (var due in _todos.DueNow(now.LocalDateTime))
        {
            _todos.MarkReminded(due.Id);
            PushUtilityNotice("Task due", due.Title);
        }
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

        // To-do (max 4 open rows)
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
            _mediaCompact.AgentBadgeCount = agentsActive ? workingCount : 0;

        // Utility pinning (Phase 4). User-opened panels pin at UserPanel priority —
        // only approval alerts displace them. A merely RUNNING focus timer pins at
        // Utility, below agent status, so agents never lose their pill to the timer.
        UpdateUtilityVms();
        var pomodoroVisible = _pomodoro.Phase != PomodoroPhase.Idle;
        IslandContent? utilityView =
            _utilityPins.Contains("pomodoro") ? new PomodoroView(_pomodoroVm!)
            : _utilityPins.Contains("water") ? new WaterView(_waterVm!)
            : _utilityPins.Contains("todo") ? new TodoView(_todoVm!)
            : _utilityPins.Contains("shelf") ? BuildShelfView()
            : _shelf.Count > 0 ? BuildShelfView()
            : pomodoroVisible ? new PomodoroView(_pomodoroVm!)
            : null;
        var utilityPriority = _utilityPins.Count > 0 ? IslandPriority.UserPanel : IslandPriority.Utility;

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
            or SessionActivity.ExecutingTool or SessionActivity.AwaitingApproval;

    private void UpdateAgentIndicator(IReadOnlyList<AgentSession> sessions, int workingCount)
    {
        _agentIndicatorVm ??= new AgentIndicatorViewModel();
        if (workingCount > 1)
        {
            var pending = sessions.Sum(s => s.PendingApprovalIds.Count);
            _agentIndicatorVm.Update($"{workingCount} agents active", _mascotExpression, pending);
        }
        else
        {
            var s = sessions.First(IsWorking);
            var provider = AgentCardViewModel.DisplayProvider(s.Provider);
            _agentIndicatorVm.Update($"{provider} · {StatusLabel(s)}", _mascotExpression, s.PendingApprovalIds.Count);
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
        SessionActivity.Working => "Working",
        SessionActivity.ExecutingTool => "Executing tool",
        SessionActivity.AwaitingApproval => "Waiting for approval",
        SessionActivity.TurnFinished => "Turn finished",
        SessionActivity.Interrupted => "Interrupted",
        SessionActivity.Error => "Error",
        SessionActivity.Ended => "Ended",
        _ => "Status unknown",
    };

    internal static string StateColor(AgentSession s) => s.Stale ? "#9A9AA0" : s.State switch
    {
        SessionActivity.AwaitingApproval => "#FFB454",
        SessionActivity.Error => "#FF6B6B",
        SessionActivity.TurnFinished => "#5DD97C",
        SessionActivity.Ended => "#9A9AA0",
        _ => "#8AB4FF",
    };

    /// <summary>Rows for the expanded activity panel, built fresh on each hover.</summary>
    public AgentActivityPanelView BuildAgentPanel()
    {
        var rows = _sessions.Active.Select(s => new AgentSessionRow
        {
            Provider = AgentCardViewModel.DisplayProvider(s.Provider),
            Project = string.IsNullOrEmpty(s.Project) ? "—" : s.Project,
            StatusLabel = StatusLabel(s),
            LastAction = string.IsNullOrEmpty(s.LastSummary) ? StatusLabel(s) : s.LastSummary,
            AgeLabel = AgeLabel(s.LastEventUtc),
            PendingApprovals = s.PendingApprovalIds.Count,
            StateBrush = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(StateColor(s))),
            Timeline = s.History.TakeLast(3).Select(h => new AgentTimelineItem
            {
                TimeLabel = h.TimestampUtc.ToLocalTime().ToString("HH:mm"),
                Text = h.Text,
                Kind = h.Kind,
            }).ToList(),
        }).ToList();
        return new AgentActivityPanelView(rows);
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

        if (_approvalQueue.Remove(request.ApprovalRequestId) && _approvalQueue.Count > 0)
        {
            _approvalQueueIndex = Math.Clamp(_approvalQueueIndex, 0, _approvalQueue.Count - 1);
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
