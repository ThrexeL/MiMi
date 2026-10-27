using MiMi.Agents;
using MiMi.Core;
using Xunit;

namespace MiMi.Tests;

public class AgentNotificationTests
{
    private DateTimeOffset _now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private AgentNotificationCoordinator Coordinator(bool hidden = false, Action<AppSettings>? tweak = null)
    {
        var settings = new AppSettings();
        tweak?.Invoke(settings);
        var c = new AgentNotificationCoordinator(() => _now);
        c.Configure(settings);
        c.SetHidden(hidden);
        return c;
    }

    private static AgentEvent Event(string type, string title = "t", string message = "m",
        string session = "s1", string project = "MiMi", string? approval = null,
        Dictionary<string, string>? metadata = null, string? eventId = null) =>
        AgentEventFactory.Create("claude-code", session, type, title: title, message: message,
            project: project, approvalRequestId: approval, metadata: metadata, eventId: eventId);

    [Fact]
    public void ToolAndStatusEvents_AreSilent()
    {
        var c = Coordinator();
        Assert.Empty(c.OnEvent(Event(AgentEventTypes.ToolStarted)));
        Assert.Empty(c.OnEvent(Event(AgentEventTypes.ToolFinished)));
        Assert.Empty(c.OnEvent(Event(AgentEventTypes.PromptSubmitted)));
        Assert.Empty(c.OnEvent(Event(AgentEventTypes.SessionEnded)));
    }

    [Fact]
    public void Started_ProducesShortIslandNotice()
    {
        var notices = Coordinator().OnEvent(Event(AgentEventTypes.Started));

        var n = Assert.Single(notices);
        Assert.Equal(AgentNoticeKind.Started, n.Kind);
        Assert.True(n.ShowOnIsland);
        Assert.False(n.WindowsEligible);
        Assert.Contains("Claude Code", n.Title);
        Assert.NotNull(n.Duration);
    }

    [Fact]
    public void Completion_IsNotifiedOncePerTurn()
    {
        var c = Coordinator();
        var ev = Event(AgentEventTypes.TurnFinished,
            metadata: new Dictionary<string, string> { ["turn_id"] = "turn-1" });

        Assert.Single(c.OnEvent(ev));
        // Same turn reported again is suppressed even with a fresh event id.
        var duplicate = Event(AgentEventTypes.Completed,
            metadata: new Dictionary<string, string> { ["turn_id"] = "turn-1" });
        Assert.Empty(c.OnEvent(duplicate));
    }

    [Fact]
    public void DuplicateEventId_IsNotifiedOnce()
    {
        var c = Coordinator();
        var ev = Event(AgentEventTypes.Failed, eventId: "same-event");

        Assert.Single(c.OnEvent(ev));
        Assert.Empty(c.OnEvent(ev));
    }

    [Fact]
    public void DisabledNotifications_SuppressEverything()
    {
        var c = Coordinator(tweak: s => s.AgentNotificationsEnabled = false);

        Assert.Empty(c.OnEvent(Event(AgentEventTypes.Started)));
        Assert.Empty(c.OnEvent(Event(AgentEventTypes.Failed)));
        Assert.Empty(c.NotifyApproval(new ApprovalRequest
        {
            ApprovalRequestId = "ap", Provider = "claude-code", SessionId = "s1",
        }));
        Assert.Null(c.NotifyAggregate(3, 1));
    }

    [Fact]
    public void CompletionPreference_DoesNotSilenceErrors()
    {
        var c = Coordinator(tweak: s => s.CompletionNotificationsEnabled = false);

        Assert.Empty(c.OnEvent(Event(AgentEventTypes.Completed)));
        var error = Assert.Single(c.OnEvent(Event(AgentEventTypes.Failed)));
        Assert.Equal(AgentNoticeKind.Error, error.Kind);
    }

    [Fact]
    public void WindowsToast_RequiresHiddenAndOptIn()
    {
        var visible = Coordinator(hidden: false, tweak: s => s.WindowsNotificationsWhenHidden = true);
        var visibleNotice = Assert.Single(
            visible.OnEvent(Event(AgentEventTypes.Completed)));
        Assert.False(visibleNotice.WindowsEligible);

        var optedOut = Coordinator(hidden: true);
        var optedOutNotice = Assert.Single(
            optedOut.OnEvent(Event(AgentEventTypes.Completed)));
        Assert.False(optedOutNotice.WindowsEligible);

        var hidden = Coordinator(hidden: true, tweak: s => s.WindowsNotificationsWhenHidden = true);
        var hiddenNotice = Assert.Single(
            hidden.OnEvent(Event(AgentEventTypes.Completed)));
        Assert.True(hiddenNotice.WindowsEligible);
    }

    [Fact]
    public void ApprovalToast_ContainsNoCommandOrPromptText()
    {
        var c = Coordinator(hidden: true, tweak: s => s.WindowsNotificationsWhenHidden = true);
        var request = new ApprovalRequest
        {
            ApprovalRequestId = "ap-9",
            Provider = "claude-code",
            SessionId = "s1",
            Project = "SecretProject",
            ActionKind = "Execute Command",
            Command = "curl https://example.test/?token=SUPER_SECRET",
            Message = "Private prompt content",
        };

        var notice = Assert.Single(c.NotifyApproval(request));
        Assert.True(notice.WindowsEligible);
        Assert.True(notice.IsApproval);
        Assert.Equal("ap-9", notice.ApprovalRequestId);
        Assert.DoesNotContain("SUPER_SECRET", notice.Title + notice.Body);
        Assert.DoesNotContain("curl", notice.Title + notice.Body);
        Assert.DoesNotContain("Private prompt", notice.Title + notice.Body);
        Assert.DoesNotContain("SecretProject", notice.Title + notice.Body);
        // An approval notice carries no decision and no approve/deny command surface.
        Assert.Equal(IslandPriority.Approval, notice.Priority);
        Assert.True(notice.Sticky);
        Assert.False(notice.ShowOnIsland);
    }

    [Fact]
    public void ApprovalPreference_OnlyAffectsTheToastNotTheRequestItself()
    {
        var c = Coordinator(hidden: true, tweak: s =>
        {
            s.WindowsNotificationsWhenHidden = true;
            s.ApprovalNotificationsEnabled = false;
        });
        Assert.Empty(c.NotifyApproval(new ApprovalRequest
        {
            ApprovalRequestId = "ap", Provider = "codex", SessionId = "s1",
        }));
    }

    [Fact]
    public void Aggregate_OnlyForMultipleAgents_AndThrottled()
    {
        var c = Coordinator();

        Assert.Null(c.NotifyAggregate(1, 0));
        var first = c.NotifyAggregate(2, 0);
        Assert.NotNull(first);
        Assert.Contains("2 agents working", first!.Title);

        // Same wording inside the throttle window is suppressed.
        Assert.Null(c.NotifyAggregate(2, 0));

        _now += TimeSpan.FromSeconds(6);
        var changed = c.NotifyAggregate(3, 1);
        Assert.NotNull(changed);
        Assert.Contains("3 agents working", changed!.Title);
        Assert.Contains("1 awaiting approval", changed.Title);
    }

    [Fact]
    public void UnhidingResetsAggregateSoNextSummaryCanShow()
    {
        var c = Coordinator(hidden: true, tweak: s => s.WindowsNotificationsWhenHidden = true);
        Assert.NotNull(c.NotifyAggregate(2, 0));
        c.SetHidden(false);
        Assert.NotNull(c.NotifyAggregate(2, 0));
    }

    [Fact]
    public void NotificationDurationComesFromSettings_AndIsClamped()
    {
        var c = Coordinator(tweak: s => s.NotificationDurationMs = 5000);
        Assert.Equal(TimeSpan.FromSeconds(5), c.IslandDuration);

        c.Configure(new AppSettings { NotificationDurationMs = 999999 });
        Assert.Equal(TimeSpan.FromSeconds(10), c.IslandDuration);
    }
}
