using MiMi.Core;

namespace MiMi.Agents;

/// <summary>Provider names shown in UI and notifications (one mapping for the whole app).</summary>
public static class ProviderNames
{
    public static string Display(string provider) => provider switch
    {
        "claude-code" => "Claude Code",
        "codex" => "Codex CLI",
        var p => string.IsNullOrEmpty(p) ? "Agent" : char.ToUpperInvariant(p[0]) + p[1..],
    };
}

public enum AgentNoticeKind
{
    Started,
    MultiAgent,
    TurnCompleted,
    Error,
    ApprovalRequired,
}

/// <summary>
/// A display-ready notification decision. It carries no command text, prompt content,
/// credentials, or transcript data — only provider/session metadata and a short summary.
/// Approval notices reference a request id; they can never carry (or perform) a decision.
/// </summary>
public sealed record AgentNotice(
    AgentNoticeKind Kind,
    string Key,
    IslandPriority Priority,
    string Title,
    string Body,
    string Provider,
    string SessionId,
    string Project,
    string EventType,
    string? ApprovalRequestId,
    TimeSpan? Duration,
    bool Sticky,
    bool ShowOnIsland,
    bool WindowsEligible,
    bool AllowSound)
{
    public bool IsApproval => Kind == AgentNoticeKind.ApprovalRequired;
}

/// <summary>
/// Central notification policy for agent activity. One place decides whether an event is
/// worth surfacing, how it is worded, how long it lives, and whether a Windows toast is
/// allowed while MiMi is hidden.
///
/// Rules:
///  - tool/status churn never notifies; only start, completion, failure, approvals, and
///    aggregate multi-agent changes do;
///  - duplicate event ids and repeated identical outcomes inside a short window are dropped;
///  - approvals are always shown as a request ("View request"), never as a decision;
///  - Windows toasts are opt-in, only while hidden, and never include commands/prompts;
///  - approval requests are sticky; everything else expires quickly.
/// </summary>
public sealed class AgentNotificationCoordinator
{
    private static readonly TimeSpan DedupeWindow = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan StaleKeyLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AggregateThrottle = TimeSpan.FromSeconds(5);
    private const int MaxTrackedKeys = 512;

    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<string, DateTimeOffset> _seenKeys = new(StringComparer.Ordinal);
    private AppSettings _settings = new();
    private bool _hidden;
    private DateTimeOffset _lastAggregateUtc = DateTimeOffset.MinValue;
    private string _lastAggregateText = "";

    public AgentNotificationCoordinator(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public bool Hidden => _hidden;
    public AppSettings Settings => _settings;

    public void Configure(AppSettings settings) => _settings = settings;

    /// <summary>Set only by the explicit Ctrl+Alt+M / tray visibility preference — never by
    /// a notification or by fullscreen suppression.</summary>
    public void SetHidden(bool hidden)
    {
        _hidden = hidden;
        if (!hidden) ResetAggregate();
    }

    public TimeSpan IslandDuration => TimeSpan.FromMilliseconds(
        Math.Clamp(_settings.NotificationDurationMs, 1500, 10000));

    /// <summary>Decides which notices (if any) an agent event should produce.</summary>
    public IReadOnlyList<AgentNotice> OnEvent(AgentEvent ev)
    {
        var notices = new List<AgentNotice>();
        if (!_settings.AgentNotificationsEnabled) return notices;

        var now = _clock();
        Prune(now);
        if (!Track($"event:{ev.EventId}", now)) return notices;

        var provider = ProviderNames.Display(ev.Provider);
        var project = ev.Project;
        var hasProject = !string.IsNullOrWhiteSpace(project);

        switch (ev.Type)
        {
            case AgentEventTypes.Started:
            {
                if (Track($"start:{ev.Provider}:{ev.SessionId}", now))
                {
                    notices.Add(new AgentNotice(
                        AgentNoticeKind.Started, $"start:{ev.Provider}:{ev.SessionId}",
                        IslandPriority.AgentCompletion,
                        $"{provider} started",
                        hasProject ? $"Working in {project}" : "New session started.",
                        ev.Provider, ev.SessionId, project, ev.Type, null,
                        IslandDuration, false, ShowOnIsland: true,
                        WindowsEligible: false, AllowSound: false));
                }
                break;
            }

            case AgentEventTypes.Completed:
            case AgentEventTypes.TurnFinished:
            {
                if (!_settings.CompletionNotificationsEnabled) break;
                if (!Track($"done:{ev.Provider}:{ev.SessionId}:{TurnKey(ev)}", now)) break;
                notices.Add(new AgentNotice(
                    AgentNoticeKind.TurnCompleted, $"done:{ev.EventId}",
                    IslandPriority.AgentCompletion,
                    $"{provider} finished its turn",
                    hasProject ? project : "Ready for the next task.",
                    ev.Provider, ev.SessionId, project, ev.Type, null,
                    IslandDuration, false, ShowOnIsland: true,
                    WindowsEligible: _hidden && _settings.WindowsNotificationsWhenHidden,
                    AllowSound: _settings.NotificationSoundEnabled));
                break;
            }

            case AgentEventTypes.Failed:
            case AgentEventTypes.ToolFailed:
            case AgentEventTypes.Interrupted:
            case AgentEventTypes.Cancelled:
            {
                var state = ev.Type == AgentEventTypes.Cancelled || ev.Type == AgentEventTypes.Interrupted
                    ? "was interrupted"
                    : "encountered an error";
                notices.Add(new AgentNotice(
                    AgentNoticeKind.Error, $"error:{ev.EventId}",
                    IslandPriority.AgentFailure,
                    $"{provider} {state}",
                    hasProject ? project : "Open MiMi for details.",
                    ev.Provider, ev.SessionId, project, ev.Type, null,
                    TimeSpan.FromMilliseconds(Math.Max(4000, IslandDuration.TotalMilliseconds)),
                    false, ShowOnIsland: true,
                    WindowsEligible: _hidden && _settings.WindowsNotificationsWhenHidden,
                    AllowSound: _settings.NotificationSoundEnabled));
                break;
            }
        }

        return notices;
    }

    /// <summary>Approval notification. The island card itself is owned by the presenter and
    /// remains reachable regardless of notification settings; this only decides whether an
    /// extra Windows toast is allowed while hidden.</summary>
    public IReadOnlyList<AgentNotice> NotifyApproval(ApprovalRequest request)
    {
        if (!_settings.AgentNotificationsEnabled || !_settings.ApprovalNotificationsEnabled)
            return Array.Empty<AgentNotice>();

        var now = _clock();
        Prune(now);
        if (!Track($"approval:{request.ApprovalRequestId}", now)) return Array.Empty<AgentNotice>();

        var provider = ProviderNames.Display(request.Provider);
        var action = string.IsNullOrWhiteSpace(request.ActionKind) ? "a request" : request.ActionKind;
        return new[]
        {
            new AgentNotice(
                AgentNoticeKind.ApprovalRequired, $"approval:{request.ApprovalRequestId}",
                IslandPriority.Approval,
                $"{provider} needs your approval",
                $"Review {action} in MiMi. Nothing is approved automatically.",
                request.Provider, request.SessionId, request.Project,
                AgentEventTypes.ApprovalRequested, request.ApprovalRequestId,
                null, Sticky: true, ShowOnIsland: false,
                WindowsEligible: _hidden && _settings.WindowsNotificationsWhenHidden,
                AllowSound: _settings.NotificationSoundEnabled),
        };
    }

    /// <summary>
    /// Aggregate multi-agent summary. Emitted only when the wording changes and at most once
    /// per few seconds, so several busy sessions cannot churn the island.
    /// </summary>
    public AgentNotice? NotifyAggregate(int workingCount, int pendingApprovals)
    {
        if (!_settings.AgentNotificationsEnabled || workingCount < 2) return null;

        var text = $"{workingCount} agents working"
                   + (pendingApprovals > 0 ? $" · {pendingApprovals} awaiting approval" : "");
        if (string.Equals(text, _lastAggregateText, StringComparison.Ordinal)) return null;

        var now = _clock();
        if (now - _lastAggregateUtc < AggregateThrottle) return null;

        _lastAggregateUtc = now;
        _lastAggregateText = text;
        return new AgentNotice(
            AgentNoticeKind.MultiAgent, $"multi:{text}",
            IslandPriority.AgentCompletion,
            text,
            "Multiple sessions are active.",
            "", "", "", AgentEventTypes.Status, null,
            IslandDuration, false, ShowOnIsland: true,
            WindowsEligible: false, AllowSound: false);
    }

    private void ResetAggregate()
    {
        _lastAggregateUtc = DateTimeOffset.MinValue;
        _lastAggregateText = "";
    }

    /// <summary>Completion dedupe key: prefer the turn id so two turns are both announced,
    /// while a duplicated Stop/Completed event for the same turn is suppressed.</summary>
    private static string TurnKey(AgentEvent ev) =>
        ev.Metadata.TryGetValue("turn_id", out var turn) && turn.Length > 0
            ? turn
            : ev.EventId;

    /// <summary>Returns false when this key was seen inside the dedupe window.</summary>
    private bool Track(string key, DateTimeOffset now)
    {
        if (_seenKeys.TryGetValue(key, out var seen) && now - seen < DedupeWindow) return false;
        _seenKeys[key] = now;
        return true;
    }

    private void Prune(DateTimeOffset now)
    {
        if (_seenKeys.Count <= MaxTrackedKeys) return;
        foreach (var (key, seen) in _seenKeys.ToList())
        {
            if (now - seen > StaleKeyLifetime) _seenKeys.Remove(key);
        }
        // Hard cap: drop the oldest entries if a burst still exceeds the limit.
        if (_seenKeys.Count > MaxTrackedKeys)
        {
            foreach (var key in _seenKeys.OrderBy(p => p.Value)
                         .Take(_seenKeys.Count - MaxTrackedKeys).Select(p => p.Key).ToList())
                _seenKeys.Remove(key);
        }
    }
}
