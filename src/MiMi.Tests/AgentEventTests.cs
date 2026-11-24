using MiMi.Agents;
using MiMi.Core;
using Xunit;

namespace MiMi.Tests;

public class AgentEventTests
{
    private static AgentEvent Raw(string type = AgentEventTypes.Status, string provider = "claude-code") =>
        AgentEventFactory.Create(provider, "sess-1", type);

    [Fact]
    public void Normalize_ValidEvent_Passes()
    {
        var ev = AgentEvent.Normalize(Raw(), out var error);
        Assert.Null(error);
        Assert.NotNull(ev);
        Assert.Equal("claude-code", ev!.Provider);
        Assert.Equal("Agent status", ev.Title);
    }

    [Fact]
    public void Normalize_MissingProvider_Rejected()
    {
        var raw = AgentEventFactory.Create("", "s", AgentEventTypes.Status);
        var ev = AgentEvent.Normalize(raw, out var error);
        Assert.Null(ev);
        Assert.NotNull(error);
    }

    [Fact]
    public void Normalize_UnknownType_Rejected()
    {
        var raw = AgentEventFactory.Create("codex", "s", "agent.hax");
        var ev = AgentEvent.Normalize(raw, out var error);
        Assert.Null(ev);
    }

    [Fact]
    public void Normalize_ApprovalWithoutId_Rejected()
    {
        var raw = AgentEventFactory.Create("codex", "s", AgentEventTypes.ApprovalRequested);
        var ev = AgentEvent.Normalize(raw, out var error);
        Assert.Null(ev);
        Assert.Contains("approval_request_id", error);
    }

    [Fact]
    public void Normalize_TrimsLongFields()
    {
        var raw = AgentEventFactory.Create("codex", "s", AgentEventTypes.Status,
            title: new string('x', 500), message: new string('y', 5000));
        var ev = AgentEvent.Normalize(raw, out _);
        Assert.NotNull(ev);
        Assert.True(ev!.Title.Length <= 120);
        Assert.True(ev.Message.Length <= 500);
    }

    [Fact]
    public void Normalize_StripsControlCharacters()
    {
        var raw = new AgentEvent
        {
            Provider = "cod\u0001ex\u0002", Type = AgentEventTypes.Status,
            Message = "line\u0007break",
        };
        var ev = AgentEvent.Normalize(raw, out _);
        Assert.NotNull(ev);
        Assert.DoesNotContain('\u0001', ev!.Provider);
        Assert.DoesNotContain('\u0007', ev!.Message);
    }

    [Fact]
    public void Normalize_MissingEventId_GeneratesUniqueIds()
    {
        var a = AgentEvent.Normalize(new AgentEvent { Provider = "codex", Type = AgentEventTypes.Status }, out _);
        var b = AgentEvent.Normalize(new AgentEvent { Provider = "codex", Type = AgentEventTypes.Status }, out _);
        Assert.False(string.IsNullOrEmpty(a!.EventId));
        Assert.NotEqual(a.EventId, b!.EventId);
    }

    [Fact]
    public void Normalize_ControlOnlyProvider_Rejected()
    {
        var raw = new AgentEvent { Provider = "\u0001\u0002", Type = AgentEventTypes.Status };
        Assert.Null(AgentEvent.Normalize(raw, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void TryParse_ValidJson_ReturnsEvent()
    {
        var json = """
        { "event_id": "abc", "provider": "codex", "session_id": "s1", "project": "P",
          "type": "agent.approval_requested", "title": "Approval Required",
          "message": "wants to run a command", "approval_request_id": "ap1",
          "metadata": { "command": "npm run build", "action_kind": "Execute Command" } }
        """;
        var ev = AgentEvent.TryParse(json, out var error);
        Assert.Null(error);
        Assert.NotNull(ev);
        Assert.Equal("npm run build", ev!.Metadata["command"]);
        Assert.Equal(IslandPriority.Approval, ev.ToIslandPriority());
    }

    [Fact]
    public void TryParse_MalformedJson_ReturnsNull()
    {
        Assert.Null(AgentEvent.TryParse("{ oops", out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void IslandPriority_MapsSecurityRules()
    {
        var approval = AgentEventFactory.Create("p", "s", AgentEventTypes.ApprovalRequested, approvalRequestId: "a");
        var failed = AgentEventFactory.Create("p", "s", AgentEventTypes.Failed);
        var media = AgentEventFactory.Create("p", "s", AgentEventTypes.Status);
        Assert.Equal(IslandPriority.Approval, approval.ToIslandPriority());
        Assert.Equal(IslandPriority.AgentFailure, failed.ToIslandPriority());
        Assert.Equal(IslandPriority.AgentCompletion, media.ToIslandPriority());
    }
}

public class ApprovalStoreTests
{
    private static AgentEvent ApprovalEvent(string id = "ap1", string provider = "claude-code") =>
        AgentEventFactory.Create(provider, "sess-1", AgentEventTypes.ApprovalRequested,
            approvalRequestId: id,
            metadata: new Dictionary<string, string> { ["command"] = "npm run build", ["action_kind"] = "Execute Command" });

    [Fact]
    public void Register_ThenDecide_SucceedsOnce()
    {
        using var store = new ApprovalStore();
        var request = store.Register(ApprovalEvent());
        Assert.NotNull(request);
        Assert.True(request!.IsPending);

        Assert.True(store.TryDecide("ap1", ApprovalDecision.Approved, "user"));
        Assert.False(store.TryDecide("ap1", ApprovalDecision.Denied, "user")); // double-resolve blocked
        Assert.Equal(ApprovalDecision.Approved, store.Get("ap1")!.Decision);
    }

    [Fact]
    public void ExpiredApproval_CannotBeDecided()
    {
        using var store = new ApprovalStore();
        var request = store.Register(ApprovalEvent(), ttl: TimeSpan.FromMilliseconds(-1)); // already past
        Assert.NotNull(request);
        Assert.True(request!.IsExpired);
        Assert.False(store.TryDecide("ap1", ApprovalDecision.Approved, "user"));
        Assert.Null(store.Get("ap1")!.Decision); // remains unresolved, decision rejected
    }

    [Fact]
    public void Sweeper_ExpiresStaleRequests()
    {
        using var store = new ApprovalStore();
        var fired = new List<ApprovalRequest>();
        store.Resolved += r => fired.Add(r);
        store.Register(ApprovalEvent("exp1"), ttl: TimeSpan.FromMilliseconds(-1));

        // Sweep runs on a 5 s timer; force-check expiry semantics directly:
        var request = store.Get("exp1")!;
        Assert.True(request.TryExpire());
        Assert.Equal(ApprovalDecision.Expired, request.Decision);
        Assert.False(request.TryExpire()); // second expire is a no-op
    }

    [Fact]
    public void Register_DuplicateId_ReturnsNull()
    {
        using var store = new ApprovalStore();
        Assert.NotNull(store.Register(ApprovalEvent("dup")));
        Assert.Null(store.Register(ApprovalEvent("dup")));
    }

    [Fact]
    public void Register_BackdatedSenderTimestamp_DoesNotShortenApprovalTtl()
    {
        using var store = new ApprovalStore(TimeSpan.FromMinutes(5));
        var before = DateTimeOffset.UtcNow;
        var request = store.Register(ApprovalEvent() with { TimestampUtc = before.AddHours(-1) })!;
        var after = DateTimeOffset.UtcNow;

        Assert.InRange(request.CreatedUtc, before, after);
        Assert.True(request.IsPending);
        Assert.InRange(request.TimeToLive - (DateTimeOffset.UtcNow - request.CreatedUtc),
            TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void Register_EnforcesPendingCap_AndAllowsProgressAfterResolution()
    {
        using var store = new ApprovalStore(maxPendingApprovals: 2);
        Assert.NotNull(store.Register(ApprovalEvent("a")));
        Assert.NotNull(store.Register(ApprovalEvent("b")));
        Assert.Null(store.Register(ApprovalEvent("c")));

        Assert.True(store.TryDecide("a", ApprovalDecision.Denied, "user"));
        Assert.NotNull(store.Register(ApprovalEvent("c")));
        Assert.Equal(2, store.Pending.Count);
    }

    [Fact]
    public void Pending_OrdersByCreation()
    {
        using var store = new ApprovalStore();
        var first = store.Register(ApprovalEvent("p1"));
        Thread.Sleep(5);
        var second = store.Register(ApprovalEvent("p2"));
        Assert.Equal("p1", store.Pending[0].ApprovalRequestId);
        Assert.Equal(2, store.Pending.Count);
    }
}
