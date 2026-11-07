using MiMi.Agents;
using MiMi.AgentCli;
using MiMi.Bridge;
using System.IO;
using System.Text.Json;
using Xunit;

namespace MiMi.Tests;

public class ThursdayIntegrationTests
{
    [Theory]
    [InlineData(ApprovalDecision.Approved, "approve_task")]
    [InlineData(ApprovalDecision.Denied, "deny")]
    public async Task IslandDecision_RoundTripsOverAuthenticatedPipe(ApprovalDecision decision, string expected)
    {
        var bus = new AgentEventBus();
        using var approvals = new ApprovalStore();
        var token = new string('a', 64);
        await using var server = new NamedPipeAgentBridge(bus, approvals, token,
            pipeName: "mimi-thursday-test-" + Guid.NewGuid().ToString("N"));
        var received = new TaskCompletionSource<AgentEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        bus.Subscribe(ev => received.TrySetResult(ev));
        server.Start();
        var ev = AgentEventFactory.Create("thursday", "chat", AgentEventTypes.ApprovalRequested,
            approvalRequestId: "sealed-action", metadata: new Dictionary<string, string> { ["command"] = "printf exact" });
        var output = new StringWriter();
        var relay = ThursdayPermission.RunAsync(new StringReader(ev.ToJson()), output,
            () => BridgeClient.ConnectAsync(TimeSpan.FromSeconds(1), "test", server.PipeName, token));
        Assert.Equal("sealed-action", (await received.Task.WaitAsync(TimeSpan.FromSeconds(3))).ApprovalRequestId);
        Assert.True(approvals.TryDecide("sealed-action", decision, "user"));
        Assert.Equal(0, await relay.WaitAsync(TimeSpan.FromSeconds(3)));
        using var response = JsonDocument.Parse(output.ToString());
        Assert.Equal("sealed-action", response.RootElement.GetProperty("approval_id").GetString());
        Assert.Equal(expected, response.RootElement.GetProperty("decision").GetString());
    }

    [Fact]
    public void Permission_PreservesCompleteMultilineAction()
    {
        var command = "printf first\nprintf " + new string('x', 400);
        var ev = AgentEventFactory.Create("thursday", "s", AgentEventTypes.ApprovalRequested,
            approvalRequestId: "approval", metadata: new Dictionary<string, string> { ["command"] = command });
        Assert.Equal(command, ThursdayPermission.Parse(ev.ToJson())!.Metadata["command"]);
        Assert.Null(ThursdayPermission.Parse((ev with { Provider = "codex" }).ToJson()));
        Assert.Null(ThursdayPermission.Parse((ev with { Type = AgentEventTypes.Status }).ToJson()));
        Assert.Null(ThursdayPermission.Parse((ev with { Metadata = new Dictionary<string, string> { ["command"] = new string('x', 12001) } }).ToJson()));
    }

    [Theory]
    [InlineData(AgentEventTypes.TurnFinished, SessionActivity.TurnFinished)]
    [InlineData(AgentEventTypes.Interrupted, SessionActivity.Interrupted)]
    [InlineData(AgentEventTypes.Failed, SessionActivity.Error)]
    [InlineData(AgentEventTypes.WaitingForInput, SessionActivity.WaitingForInput)]
    public void ConfirmedTerminalAndWaitingStates_DoNotBecomeUnknown(string type, SessionActivity state)
    {
        using var manager = new AgentSessionManager();
        manager.Apply(AgentEventFactory.Create("thursday", "s", type));
        manager.SweepStale(DateTimeOffset.UtcNow.AddMinutes(3));
        Assert.False(manager.Get("thursday", "s")!.Stale);
        Assert.Equal(state, manager.Get("thursday", "s")!.State);
    }

    [Fact]
    public void Activity_OrdersApprovalsThenWorkThenRecent()
    {
        using var manager = new AgentSessionManager();
        manager.Apply(AgentEventFactory.Create("thursday", "finished", AgentEventTypes.TurnFinished));
        manager.Apply(AgentEventFactory.Create("codex", "working", AgentEventTypes.ToolStarted));
        manager.Apply(AgentEventFactory.Create("thursday", "approval", AgentEventTypes.ApprovalRequested, approvalRequestId: "a"));
        Assert.Equal(new[] { "approval", "working", "finished" }, manager.Active.Select(s => s.SessionId));
    }

    [Fact]
    public void Heartbeat_RestoresConfirmedActivityWithoutTimelineNoise()
    {
        using var manager = new AgentSessionManager();
        manager.Apply(AgentEventFactory.Create("thursday", "s", AgentEventTypes.ToolStarted));
        manager.SweepStale(DateTimeOffset.UtcNow.AddMinutes(3));
        var ev = AgentEventFactory.Create("thursday", "s", AgentEventTypes.Heartbeat,
            metadata: new Dictionary<string, string> { ["activity_type"] = AgentEventTypes.ToolStarted });
        manager.Apply(ev);
        var session = manager.Get("thursday", "s")!;
        Assert.Equal(SessionActivity.ExecutingTool, session.State);
        Assert.False(session.Stale);
        Assert.Single(session.History);
    }
}
