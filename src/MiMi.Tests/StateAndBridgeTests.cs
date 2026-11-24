using System.IO;
using System.IO.Pipes;
using System.Text;
using MiMi.Agents;
using MiMi.Bridge;
using MiMi.Core;
using Xunit;

namespace MiMi.Tests;

public class IslandStateManagerTests
{
    [Fact]
    public void HigherPriorityAlert_Wins_ThenPinnedReturns()
    {
        using var manager = new IslandStateManager();
        object? effective = null;
        manager.EffectiveContentChanged += c => effective = c;

        manager.SetPinned(IslandPriority.Media, "MEDIA");
        Assert.Equal("MEDIA", effective);

        manager.Push(new IslandAlert { Id = "a1", Priority = IslandPriority.AgentCompletion, Content = "AGENT" });
        Assert.Equal("AGENT", effective);

        manager.Clear("a1");
        Assert.Equal("MEDIA", effective);
    }

    [Fact]
    public void Approval_ObeysMediaPinned()
    {
        using var manager = new IslandStateManager();
        object? effective = null;
        manager.EffectiveContentChanged += c => effective = c;

        manager.SetPinned(IslandPriority.Media, "MEDIA");
        manager.Push(new IslandAlert { Id = "ap", Priority = IslandPriority.Approval, Content = "APPROVAL" });
        Assert.Equal("APPROVAL", effective);
    }

    [Fact]
    public void UserPanel_OutranksCompletion_ButYieldsToFailure()
    {
        using var manager = new IslandStateManager();
        object? effective = null;
        manager.EffectiveContentChanged += c => effective = c;

        manager.SetPinned(IslandPriority.UserPanel, "PANEL");
        manager.Push(new IslandAlert { Id = "done", Priority = IslandPriority.AgentCompletion, Content = "DONE" });
        Assert.Equal("PANEL", effective);

        manager.Push(new IslandAlert { Id = "failed", Priority = IslandPriority.AgentFailure, Content = "FAILED" });
        Assert.Equal("FAILED", effective);
    }

    [Fact]
    public void DuplicatePush_DoesNotFireAgain()
    {
        using var manager = new IslandStateManager();
        var fired = 0;
        manager.EffectiveContentChanged += _ => fired++;
        manager.SetPinned(IslandPriority.Idle, "IDLE");
        var alert = new IslandAlert { Id = "x", Priority = IslandPriority.Approval, Content = "X" };
        Assert.True(manager.Push(alert));
        Assert.False(manager.Push(alert)); // dedup
        Assert.Equal(2, fired);            // idle → X only
    }

    [Fact]
    public void TransientAlert_Expires()
    {
        using var manager = new IslandStateManager();
        object? effective = null;
        manager.EffectiveContentChanged += c => effective = c;

        manager.SetPinned(IslandPriority.Media, "MEDIA");
        manager.Push(new IslandAlert
        {
            Id = "t",
            Priority = IslandPriority.AgentCompletion,
            Content = "TOAST",
            Duration = TimeSpan.FromMilliseconds(50),
        });
        Assert.Equal("TOAST", effective);
        Thread.Sleep(700); // sweeper runs every 500 ms
        Assert.Equal("MEDIA", effective);
    }
}

public class BridgeProtocolTests
{
    [Fact]
    public async Task FrameRoundTrip()
    {
        using var pipe = new AnonymousPipeServerStream(PipeDirection.Out);
        using var reader = new AnonymousPipeClientStream(PipeDirection.In, pipe.GetClientHandleAsString());
        var frame = new BridgeProtocol { Kind = "ack", Ok = true };
        await BridgeProtocol.WriteFrameAsync(pipe, frame, CancellationToken.None);

        var text = await BridgeProtocol.ReadFrameAsync(reader, CancellationToken.None);
        Assert.NotNull(text);
        var parsed = BridgeProtocol.Deserialize(text!);
        Assert.Equal("ack", parsed!.Kind);
        Assert.True(parsed.Ok);
    }

    [Fact]
    public async Task OversizedFrame_Throws()
    {
        using var pipe = new AnonymousPipeServerStream(PipeDirection.Out);
        using var reader = new AnonymousPipeClientStream(PipeDirection.In, pipe.GetClientHandleAsString());
        var header = BitConverter.GetBytes(BridgeProtocol.MaxFrameBytes + 1);
        await pipe.WriteAsync(header);
        await pipe.FlushAsync();

        await Assert.ThrowsAsync<IOException>(
            () => BridgeProtocol.ReadFrameAsync(reader, CancellationToken.None));
    }

    [Fact]
    public void EnsureToken_StableAndWellFormed()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mimi-tok-{Guid.NewGuid():N}");
        try
        {
            var t1 = BridgeProtocol.EnsureToken(dir);
            var t2 = BridgeProtocol.EnsureToken(dir);
            Assert.Equal(t1, t2);
            Assert.Equal(64, t1.Length);
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class NamedPipeAgentBridgeTests
{
    private static async Task<BridgeProtocol?> SendAndReceiveAsync(
        NamedPipeClientStream client, BridgeProtocol frame, CancellationToken ct)
    {
        await BridgeProtocol.WriteFrameAsync(client, frame, ct);
        var text = await BridgeProtocol.ReadFrameAsync(client, ct);
        return text is null ? null : BridgeProtocol.Deserialize(text);
    }

    [Fact]
    public async Task ClientSendsEvent_BridgePublishes_AckOk()
    {
        var bus = new AgentEventBus();
        using var approvals = new ApprovalStore();
        var token = "testtoken" + new string('0', 55);
        await using var bridge = new NamedPipeAgentBridge(bus, approvals, token, pipeName: $"mimi-test-{Guid.NewGuid():N}");
        bridge.Start();

        var received = new TaskCompletionSource<AgentEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        bus.Subscribe(ev => received.TrySetResult(ev));

        await using var client = new NamedPipeClientStream(".", bridge.PipeName, PipeDirection.InOut);
        await client.ConnectAsync(5000);

        var hello = await SendAndReceiveAsync(client, new BridgeProtocol { Kind = "hello", Token = token, Client = "xunit" }, CancellationToken.None);
        Assert.NotNull(hello);
        Assert.True(hello!.Ok);

        var ev = AgentEventFactory.Create("sim", "s1", AgentEventTypes.Started, project: "MiMi");
        var ack = await SendAndReceiveAsync(client, new BridgeProtocol { Kind = "event", Event = ev }, CancellationToken.None);
        Assert.NotNull(ack);
        Assert.True(ack!.Ok, ack.Error);

        var published = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("sim", published.Provider);
        Assert.Equal("MiMi", published.Project);
    }

    [Fact]
    public async Task InvalidToken_IsRejected()
    {
        var bus = new AgentEventBus();
        using var approvals = new ApprovalStore();
        await using var bridge = new NamedPipeAgentBridge(bus, approvals, "realtoken" + new string('1', 55), pipeName: $"mimi-test-{Guid.NewGuid():N}");
        bridge.Start();

        await using var client = new NamedPipeClientStream(".", bridge.PipeName, PipeDirection.InOut);
        await client.ConnectAsync(5000);

        var ack = await SendAndReceiveAsync(client, new BridgeProtocol { Kind = "hello", Token = "wrong" + new string('2', 54) }, CancellationToken.None);
        Assert.NotNull(ack);
        Assert.False(ack!.Ok);
        Assert.Contains("token", ack.Error);
    }

    [Fact]
    public async Task MalformedEvent_IsRejectedWithAck()
    {
        var bus = new AgentEventBus();
        using var approvals = new ApprovalStore();
        var token = "testtoken" + new string('3', 55);
        await using var bridge = new NamedPipeAgentBridge(bus, approvals, token, pipeName: $"mimi-test-{Guid.NewGuid():N}");
        bridge.Start();

        await using var client = new NamedPipeClientStream(".", bridge.PipeName, PipeDirection.InOut);
        await client.ConnectAsync(5000);
        await SendAndReceiveAsync(client, new BridgeProtocol { Kind = "hello", Token = token }, CancellationToken.None);

        var bad = AgentEventFactory.Create("sim", "s", "not.a.real.type"); // unknown type
        var ack = await SendAndReceiveAsync(client, new BridgeProtocol { Kind = "event", Event = bad }, CancellationToken.None);
        Assert.NotNull(ack);
        Assert.False(ack!.Ok);
    }

    [Fact]
    public async Task Approval_IsRegisteredBeforeSubscribersSeeIt()
    {
        var bus = new AgentEventBus();
        using var approvals = new ApprovalStore();
        var token = "testtoken" + new string('5', 55);
        await using var bridge = new NamedPipeAgentBridge(bus, approvals, token, pipeName: $"mimi-test-{Guid.NewGuid():N}");
        bridge.Start();

        var registeredWhenPublished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bus.Subscribe(ev =>
        {
            if (ev.Type == AgentEventTypes.ApprovalRequested)
                registeredWhenPublished.TrySetResult(approvals.Get(ev.ApprovalRequestId!) is { IsPending: true });
        });

        await using var client = new NamedPipeClientStream(".", bridge.PipeName, PipeDirection.InOut);
        await client.ConnectAsync(5000);
        await SendAndReceiveAsync(client, new BridgeProtocol { Kind = "hello", Token = token, Client = "xunit" }, CancellationToken.None);

        var approval = AgentEventFactory.Create("sim", "s1", AgentEventTypes.ApprovalRequested, approvalRequestId: "ap-order");
        var ack = await SendAndReceiveAsync(client, new BridgeProtocol { Kind = "event", Event = approval }, CancellationToken.None);
        Assert.True(ack!.Ok);

        Assert.True(await registeredWhenPublished.Task.WaitAsync(TimeSpan.FromSeconds(5)),
            "approvals must be registered before the bus publishes them so the UI can resolve the card");
    }

    [Fact]
    public async Task DisconnectingRequester_DismissesStillPendingApproval()
    {
        var bus = new AgentEventBus();
        using var approvals = new ApprovalStore();
        var token = "testtoken" + new string('6', 55);
        await using var bridge = new NamedPipeAgentBridge(bus, approvals, token, pipeName: $"mimi-test-{Guid.NewGuid():N}");
        bridge.Start();

        var client = new NamedPipeClientStream(".", bridge.PipeName, PipeDirection.InOut);
        await client.ConnectAsync(5000);
        await SendAndReceiveAsync(client, new BridgeProtocol { Kind = "hello", Token = token, Client = "xunit" }, CancellationToken.None);

        var approval = AgentEventFactory.Create("sim", "s1", AgentEventTypes.ApprovalRequested, approvalRequestId: "ap-drop");
        var ack = await SendAndReceiveAsync(client, new BridgeProtocol { Kind = "event", Event = approval }, CancellationToken.None);
        Assert.True(ack!.Ok);

        // The hook times out or crashes: no decision can ever be delivered.
        await client.DisposeAsync();

        for (var i = 0; i < 60 && approvals.Get("ap-drop")?.Decision is null; i++)
            await Task.Delay(50);

        Assert.Equal(ApprovalDecision.Dismissed, approvals.Get("ap-drop")?.Decision);
    }

    [Fact]
    public async Task CapacityRejectedApproval_CanBeRetriedWithSameEventId()
    {
        var bus = new AgentEventBus();
        using var approvals = new ApprovalStore(maxPendingApprovals: 1);
        Assert.NotNull(approvals.Register(AgentEventFactory.Create("sim", "s1",
            AgentEventTypes.ApprovalRequested, approvalRequestId: "existing")));
        var token = "testtoken" + new string('8', 55);
        await using var bridge = new NamedPipeAgentBridge(bus, approvals, token, pipeName: $"mimi-test-{Guid.NewGuid():N}");
        bridge.Start();

        await using var client = new NamedPipeClientStream(".", bridge.PipeName, PipeDirection.InOut);
        await client.ConnectAsync(5000);
        await SendAndReceiveAsync(client, new BridgeProtocol { Kind = "hello", Token = token }, CancellationToken.None);

        var approval = AgentEventFactory.Create("sim", "s1", AgentEventTypes.ApprovalRequested,
            eventId: "retryable", approvalRequestId: "retryable");
        var rejected = await SendAndReceiveAsync(client, new BridgeProtocol { Kind = "event", Event = approval }, CancellationToken.None);
        Assert.False(rejected!.Ok);
        Assert.Equal("approval capacity exceeded", rejected.Error);

        Assert.True(approvals.TryDecide("existing", ApprovalDecision.Denied, "user"));
        var accepted = await SendAndReceiveAsync(client, new BridgeProtocol { Kind = "event", Event = approval }, CancellationToken.None);
        Assert.True(accepted!.Ok, accepted.Error);
        Assert.True(approvals.Get("retryable")!.IsPending);
    }

    [Fact]
    public async Task ApprovalResolution_RequiresOwner_AndResponseIsNotBroadcast()
    {
        var bus = new AgentEventBus();
        using var approvals = new ApprovalStore();
        var token = "testtoken" + new string('7', 55);
        await using var bridge = new NamedPipeAgentBridge(bus, approvals, token, pipeName: $"mimi-test-{Guid.NewGuid():N}");
        bridge.Start();

        await using var owner = new NamedPipeClientStream(".", bridge.PipeName, PipeDirection.InOut);
        await using var other = new NamedPipeClientStream(".", bridge.PipeName, PipeDirection.InOut);
        await owner.ConnectAsync(5000);
        await other.ConnectAsync(5000);
        await SendAndReceiveAsync(owner, new BridgeProtocol { Kind = "hello", Token = token, Client = "owner" }, CancellationToken.None);
        await SendAndReceiveAsync(other, new BridgeProtocol { Kind = "hello", Token = token, Client = "other" }, CancellationToken.None);

        var approval = AgentEventFactory.Create("sim", "s1", AgentEventTypes.ApprovalRequested, approvalRequestId: "ap-private");
        var requestAck = await SendAndReceiveAsync(owner, new BridgeProtocol { Kind = "event", Event = approval }, CancellationToken.None);
        Assert.True(requestAck!.Ok, requestAck.Error);

        var forged = AgentEventFactory.Create("sim", "s1", AgentEventTypes.ApprovalResolved,
            approvalRequestId: "ap-private", metadata: new Dictionary<string, string> { ["decision"] = "approved" });
        var forgedAck = await SendAndReceiveAsync(other, new BridgeProtocol { Kind = "event", Event = forged }, CancellationToken.None);
        Assert.False(forgedAck!.Ok);
        Assert.Equal("approval owner mismatch", forgedAck.Error);
        Assert.True(approvals.Get("ap-private")!.IsPending);

        Assert.True(approvals.TryDecide("ap-private", ApprovalDecision.Approved, "user"));
        var response = BridgeProtocol.Deserialize((await BridgeProtocol.ReadFrameAsync(owner, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5)))!);
        Assert.Equal("approval_resolved", response!.Kind);
        Assert.Equal("ap-private", response.ApprovalRequestId);

        using var noFrame = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BridgeProtocol.ReadFrameAsync(other, noFrame.Token));
    }

    [Fact]
    public async Task ApprovalDecision_IsPushedBackToClient()
    {
        var bus = new AgentEventBus();
        using var approvals = new ApprovalStore();
        var token = "testtoken" + new string('4', 55);
        await using var bridge = new NamedPipeAgentBridge(bus, approvals, token, pipeName: $"mimi-test-{Guid.NewGuid():N}");
        bridge.Start();

        await using var client = new NamedPipeClientStream(".", bridge.PipeName, PipeDirection.InOut);
        await client.ConnectAsync(5000);
        await SendAndReceiveAsync(client, new BridgeProtocol { Kind = "hello", Token = token, Client = "sim" }, CancellationToken.None);

        // Send an approval event.
        var approval = AgentEventFactory.Create("sim", "s1", AgentEventTypes.ApprovalRequested, approvalRequestId: "ap-test");
        var ack = await SendAndReceiveAsync(client, new BridgeProtocol { Kind = "event", Event = approval }, CancellationToken.None);
        Assert.True(ack!.Ok);

        // Resolve it locally (as the UI would).
        Assert.True(approvals.TryDecide("ap-test", ApprovalDecision.Approved, "user"));

        // The bridge pushes approval_resolved back over the same pipe.
        var text = await BridgeProtocol.ReadFrameAsync(client, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        var response = BridgeProtocol.Deserialize(text!);
        Assert.Equal("approval_resolved", response!.Kind);
        Assert.Equal("ap-test", response.ApprovalRequestId);
        Assert.Equal("approved", response.Decision);
    }
}
