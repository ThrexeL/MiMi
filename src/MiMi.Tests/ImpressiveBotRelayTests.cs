using MiMi.Agents;
using MiMi.AgentCli;
using MiMi.Bridge;
using System.Text.Json;
using Xunit;

namespace MiMi.Tests;

public class ImpressiveBotRelayTests
{
    private static string Status(string json) => json;

    [Fact]
    public void Parse_CarriesTheBotNameInTheProviderKey()
    {
        var update = ImpressiveBotRelay.Parse(Status("""
            {"bot":"Patch","state":"working","session":"thread-1","message":"Reviewing MiMi"}
            """));
        Assert.NotNull(update);
        Assert.Equal("impressivebot:Patch", update!.Provider);
        Assert.Equal("thread-1", update.SessionId);
        Assert.Equal(AgentEventTypes.ToolStarted, update.Type);
    }

    [Theory]
    [InlineData("working", AgentEventTypes.ToolStarted)]
    [InlineData("waiting", AgentEventTypes.WaitingForInput)]
    [InlineData("finished", AgentEventTypes.TurnFinished)]
    [InlineData("failed", AgentEventTypes.Failed)]
    [InlineData("stopped", AgentEventTypes.Interrupted)]
    public void Parse_MapsEveryStateOntoAnExistingEventType(string state, string expected)
    {
        var update = ImpressiveBotRelay.Parse($$"""{"bot":"Echo","state":"{{state}}"}""");
        Assert.Equal(expected, update!.Type);
    }

    [Fact]
    public void Parse_RejectsMalformedAndUnknownInput()
    {
        Assert.Null(ImpressiveBotRelay.Parse(""));
        Assert.Null(ImpressiveBotRelay.Parse("not json"));
        Assert.Null(ImpressiveBotRelay.Parse("[]"));
        Assert.Null(ImpressiveBotRelay.Parse("""{"bot":"Patch","state":"exploded"}"""));
        Assert.Null(ImpressiveBotRelay.Parse("""{"state":"working"}"""));
        // An approval can never travel through the display-only channel.
        Assert.Null(ImpressiveBotRelay.Parse("""{"bot":"Patch","state":"approval_requested"}"""));
    }

    [Fact]
    public void Parse_FlattensNamesSoALabelCannotBreak()
    {
        var update = ImpressiveBotRelay.Parse("""{"bot":"Pa\ttch\nTeam","state":"working"}""");
        Assert.Equal("impressivebot:Pa tch Team", update!.Provider);
        Assert.DoesNotContain('\n', update.Provider);
    }

    [Fact]
    public void Parse_ClampsALongBotNameToFitTheProviderField()
    {
        var update = ImpressiveBotRelay.Parse(
            $$"""{"bot":"{{new string('x', 200)}}","state":"working"}""");
        // AgentEvent.Normalize caps provider at 40 characters.
        var ev = AgentEvent.Normalize(ImpressiveBotRelay.ToEvent(update!), out var error);
        Assert.Null(error);
        Assert.NotNull(ev);
        Assert.True(ev!.Provider.Length <= 40);
    }

    [Fact]
    public void Parse_DefaultsAnUnnamedSessionToTheBot()
    {
        var update = ImpressiveBotRelay.Parse("""{"bot":"Momo","state":"working"}""");
        Assert.Equal("Momo", update!.SessionId);
    }

    [Fact]
    public void Display_ShowsTheBotNameNotTheProviderKey()
    {
        Assert.Equal("Patch", ProviderNames.Display("impressivebot:Patch"));
        Assert.Equal("ImpressiveBot", ProviderNames.Display("impressivebot"));
        Assert.Equal("ImpressiveBot", ProviderNames.Display("impressivebot:"));
        Assert.True(ProviderNames.IsImpressiveBot("impressivebot:Patch"));
        Assert.True(ProviderNames.IsImpressiveBot("impressivebot"));
        Assert.False(ProviderNames.IsImpressiveBot("codex"));
        Assert.Equal("Patch", ProviderNames.BotName("impressivebot:Patch"));
        Assert.Equal("", ProviderNames.BotName("thursday"));
    }

    [Fact]
    public void ToEvent_TitlesNameTheBot()
    {
        var update = ImpressiveBotRelay.Parse("""{"bot":"Vera","state":"finished"}""")!;
        var ev = ImpressiveBotRelay.ToEvent(update);
        Assert.Equal("Vera finished", ev.Title);
    }

    [Fact]
    public async Task RunAsync_SendsOneEventOverTheAuthenticatedPipe()
    {
        var bus = new AgentEventBus();
        using var approvals = new ApprovalStore();
        var token = new string('b', 64);
        await using var server = new NamedPipeAgentBridge(bus, approvals, token,
            pipeName: "mimi-impressivebot-test-" + Guid.NewGuid().ToString("N"));
        var received = new TaskCompletionSource<AgentEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        bus.Subscribe(ev => received.TrySetResult(ev));
        server.Start();

        var input = new StringReader("""{"bot":"Patch","state":"working","session":"t1","message":"Reviewing"}""");
        var exit = await ImpressiveBotRelay.RunAsync(input,
            () => BridgeClient.ConnectAsync(TimeSpan.FromSeconds(1), "test", server.PipeName, token));

        Assert.Equal(0, exit);
        var ev = await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("impressivebot:Patch", ev.Provider);
        Assert.Equal(AgentEventTypes.ToolStarted, ev.Type);
        Assert.Equal("t1", ev.SessionId);
        // Display-only: no approval may be created by a status update.
        Assert.Empty(approvals.Pending);
    }

    [Fact]
    public async Task RunAsync_StaysSilentWhenMiMiIsNotReachable()
    {
        // No server: the relay must still exit 0 so ImpressiveBot is never disturbed.
        var exit = await ImpressiveBotRelay.RunAsync(
            new StringReader("""{"bot":"Patch","state":"working"}"""),
            () => BridgeClient.ConnectAsync(TimeSpan.FromMilliseconds(200), "test",
                "mimi-absent-" + Guid.NewGuid().ToString("N"), new string('c', 64)));
        Assert.Equal(0, exit);
    }

    [Fact]
    public void Session_AppearsAsAwaitingApprovalOnlyThroughRealApprovals()
    {
        using var manager = new AgentSessionManager();
        var update = ImpressiveBotRelay.Parse("""{"bot":"Patch","state":"waiting","session":"t1"}""")!;
        manager.Apply(ImpressiveBotRelay.ToEvent(update));
        var session = manager.Get("impressivebot:Patch", "t1")!;
        // "waiting" is the bot asking for input, not a MiMi approval card.
        Assert.Equal(SessionActivity.WaitingForInput, session.State);
        Assert.Empty(session.PendingApprovalIds);
    }
}