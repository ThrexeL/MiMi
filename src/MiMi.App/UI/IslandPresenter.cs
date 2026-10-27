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
    private TimeSpan _userIdle;

    public IslandContent? PinnedContent { get; private set; }

    public IslandPresenter(ConfigStore config, IslandStateManager state, ApprovalStore approvals,
        MediaViewModel mediaVm, Dispatcher dispatcher)
    {
        _config = config;
        _state = state;
        _approvals = approvals;
        _mediaVm = mediaVm;
        _dispatcher = dispatcher;

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
    }

    public AgentSessionManager Sessions => _sessions;

    /// <summary>Notification decisions that should become Windows toasts while hidden.
    /// The composition root owns the tray; the presenter never displays system UI itself.</summary>
    public event Action<AgentNotice>? WindowsNoticeRequested;

    public AgentNotificationCoordinator Notifications => _notifications;

    private RestingView MakeResting() => new(DateTime.Now.ToString("HH:mm"))
    {
        ShowClock = _config.Current.ClockEnabled,
        ShowMascot = _config.Current.MascotEnabled,
    };

    // ---- media ------------------------------------------------------------------

    public void OnMediaChanged(MediaSnapshot? snapshot)
    {
        // Reuse the compact view: MediaViewModel bindings update live, so recreating it
        // per snapshot would needlessly re-trigger the pill's fade animation.
        if (snapshot is null) _mediaCompact = null;
        else _mediaCompact ??= new MediaCompactView(_mediaVm);
        RefreshPinned();
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

        if (agentsActive && !mediaActive)
            PinnedContent = _agentCompact!;
        else if (mediaActive)
            PinnedContent = _mediaCompact!;
        else
            PinnedContent = _resting;

        _state.SetPinned(PinnedContent switch
        {
            AgentCompactView => IslandPriority.AgentCompletion,
            MediaCompactView => IslandPriority.Media,
            _ => IslandPriority.Idle,
        }, PinnedContent);

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

    /// <summary>Persistent mood from confirmed state only. User idleness is measured from
    /// system input activity, never from agent silence.</summary>
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
            _userIdle,
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

    /// <summary>Called about twice a minute by the shell; only crossing the sleep threshold
    /// triggers recomposition, so idle polling stays effectively free.</summary>
    public void UpdateUserIdle(TimeSpan idle)
    {
        var wasSleeping = _userIdle >= MascotMoodPolicy.DefaultSleepAfter;
        _userIdle = idle;
        var isSleeping = idle >= MascotMoodPolicy.DefaultSleepAfter;
        if (wasSleeping == isSleeping) return;
        _reaction = null;
        _reactionTimer.Stop();
        RefreshPinned();
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
