using System.Windows;
using System.Windows.Threading;
using MiMi.Agents;
using MiMi.Core;
using MiMi.Media;

namespace MiMi.App.UI;

/// <summary>
/// Composes what the island shows: pins resting/media/agent-compact states and pushes
/// transient alerts and sticky approval cards through the IslandStateManager.
/// All methods run on the UI thread.
/// </summary>
public sealed class IslandPresenter
{
    private static readonly TimeSpan SessionActiveWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TransientStatusDuration = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan TransientOutcomeDuration = TimeSpan.FromSeconds(4.5);

    private readonly ConfigStore _config;
    private readonly IslandStateManager _state;
    private readonly ApprovalStore _approvals;
    private readonly MediaViewModel _mediaVm;
    private readonly Dispatcher _dispatcher;

    private RestingView _resting;
    private MediaCompactView? _mediaCompact;
    private AgentCompactView? _agentCompact;
    private readonly Dictionary<string, DateTimeOffset> _sessionActivity = new();
    private readonly Dictionary<string, AgentCardViewModel> _approvalCards = new();
    private readonly DispatcherTimer _sessionSweeper;

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
        // Presenter mutates VM state and timers: always run on the UI thread.
        _config.Changed += _ => _dispatcher.BeginInvoke(() => { _resting = MakeResting(); RefreshPinned(); });

        // Drop agent sessions that went quiet; keeps the compact agent pill from sticking forever.
        _sessionSweeper = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _sessionSweeper.Tick += (_, _) =>
        {
            var now = DateTimeOffset.UtcNow;
            var expired = _sessionActivity.Where(kv => now - kv.Value > SessionActiveWindow)
                .Select(kv => kv.Key).ToList();
            if (expired.Count == 0) return;
            foreach (var id in expired) _sessionActivity.Remove(id);
            RefreshPinned();
        };
    }

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
        var agentActive = _config.Current.AgentsModuleEnabled && _sessionActivity.Count > 0;
        _agentCompact = agentActive && _agentCompact is not null
            ? _agentCompact
            : agentActive && _currentAgentCard is not null
                ? new AgentCompactView(_currentAgentCard)
                : null;

        var mediaActive = _config.Current.MediaModuleEnabled && _mediaCompact is not null;
        if (!mediaActive) _mediaCompact = null;

        if (_agentCompact is not null) PinnedContent = _agentCompact;
        else if (_mediaCompact is not null) PinnedContent = _mediaCompact;
        else PinnedContent = _resting;

        // If a hover-expanded view of the old pinned content is on screen, refresh it too.
        _state.SetPinned(PinnedContent switch
        {
            AgentCompactView => IslandPriority.AgentCompletion,
            MediaCompactView => IslandPriority.Media,
            _ => IslandPriority.Idle,
        }, PinnedContent);

        if (_sessionActivity.Count > 0) _sessionSweeper.Start();
        else _sessionSweeper.Stop();
    }

    private AgentCardViewModel? _currentAgentCard;

    // ---- agent events ---------------------------------------------------------------

    public void OnAgentEvent(AgentEvent ev)
    {
        switch (ev.Type)
        {
            case AgentEventTypes.ApprovalRequested:
                MarkSessionActive(ev);
                // Bridge-arrived approvals are already registered; simulator-path ones aren't.
                var request = _approvals.Get(ev.ApprovalRequestId!) ?? _approvals.Register(ev);
                if (request is null || _approvalCards.ContainsKey(ev.ApprovalRequestId!)) return;
                var card = new AgentCardViewModel
                {
                    ApprovalId = ev.ApprovalRequestId!,
                    Decide = decision => _approvals.TryDecide(ev.ApprovalRequestId!, decision, "user"),
                    OpenInAgent = () => OpenInAgent(ev),
                };
                card.Update(ev);
                _approvalCards[ev.ApprovalRequestId!] = card;
                _state.Push(new IslandAlert
                {
                    Id = "approval:" + ev.ApprovalRequestId,
                    Priority = IslandPriority.Approval,
                    Content = new ApprovalView(card),
                    // Sticky: stays until resolved/expired — a song change never displaces it.
                });
                break;

            case AgentEventTypes.ApprovalResolved:
                if (ev.ApprovalRequestId is { } resolvedId)
                {
                    var decision = ev.Metadata.TryGetValue("decision", out var d) &&
                                   Enum.TryParse<ApprovalDecision>(d, ignoreCase: true, out var parsed)
                        ? parsed : ApprovalDecision.Dismissed;
                    _approvals.TryDecide(resolvedId, decision, "provider");
                }
                break;

            case AgentEventTypes.Failed:
            case AgentEventTypes.Completed:
            case AgentEventTypes.Cancelled:
                MarkSessionInactive(ev);
                PushTransient(ev, TransientOutcomeDuration, expanded: true);
                break;

            case AgentEventTypes.Started:
            case AgentEventTypes.Status:
            case AgentEventTypes.ToolStarted:
                MarkSessionActive(ev);
                PushTransient(ev, TransientStatusDuration, expanded: false);
                break;
        }

        RefreshPinned();
    }

    private void PushTransient(AgentEvent ev, TimeSpan duration, bool expanded)
    {
        var card = new AgentCardViewModel
        {
            ApprovalId = "",
            Decide = _ => { },
            OpenInAgent = () => OpenInAgent(ev),
        };
        card.Update(ev);
        _state.Push(new IslandAlert
        {
            Id = ev.EventId,
            Priority = ev.ToIslandPriority(),
            Content = expanded ? new AgentExpandedView(card) : new AgentCompactView(card),
            Duration = duration,
        });
    }

    private void MarkSessionActive(AgentEvent ev)
    {
        if (string.IsNullOrEmpty(ev.SessionId)) return;
        _sessionActivity[ev.SessionId] = DateTimeOffset.UtcNow;
        if (_currentAgentCard is null || _currentAgentCard.SessionId != ev.SessionId)
        {
            var card = new AgentCardViewModel { ApprovalId = "", Decide = _ => { }, OpenInAgent = () => OpenInAgent(ev) };
            card.Update(ev);
            _currentAgentCard = card;
        }
        else
        {
            _currentAgentCard.Update(ev);
        }
    }

    private void MarkSessionInactive(AgentEvent ev)
    {
        if (!string.IsNullOrEmpty(ev.SessionId)) _sessionActivity.Remove(ev.SessionId);
    }

    public void OnApprovalResolved(ApprovalRequest request)
    {
        _approvalCards.Remove(request.ApprovalRequestId);
        _state.Clear("approval:" + request.ApprovalRequestId);

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
    }

    /// <summary>
    /// Phase-1 fallback for "Open in Agent": copies the approval details to the clipboard.
    /// Phase 3 provider adapters will route this to the originating terminal/session.
    /// </summary>
    private void OpenInAgent(AgentEvent ev)
    {
        try
        {
            var text = $"[{AgentCardViewModel.DisplayProvider(ev.Provider)}] session {ev.SessionId}" +
                       (ev.ApprovalRequestId is { Length: > 0 } ap ? $" approval {ap}" : "") +
                       (ev.Project.Length > 0 ? $" project {ev.Project}" : "");
            Clipboard.SetText(text);
            Logger.Info($"Open in Agent: copied '{text}'. (Direct agent focus arrives with Phase 3 adapters.)");
        }
        catch (Exception ex) { Logger.Warn($"Open in Agent failed: {ex.Message}"); }
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
            Publish(AgentEventFactory.Create(provider, session, AgentEventTypes.ToolStarted,
                title: "Running tool", message: "Building the solution before continuing.",
                project: project,
                metadata: new Dictionary<string, string> { ["tool"] = "Bash: dotnet build" }));

            await Task.Delay(1600);
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
