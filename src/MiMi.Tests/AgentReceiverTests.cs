
using System.Text.Json;
using MiMi.AgentCli;
using MiMi.Agents;
using Xunit;

namespace MiMi.Tests;

public class AgentReceiverTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2020, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static AgentEvent Snapshot(Clock clock, long sequence, string state = "working", string bot = "Patch",
        DateTimeOffset? timestamp = null) => ImpressiveBotRelay.ToEvent(ImpressiveBotRelay.Parse(JsonSerializer.Serialize(new
        {
            bot, session = "shared", state, sequence, timestamp = (timestamp ?? clock.Now).ToString("O"),
        }))!);

    [Fact]
    public void Relay_PreservesNumericSequenceAndUtcTimestampThroughNormalization()
    {
        var clock = new Clock();
        var ev = AgentEvent.Normalize(Snapshot(clock, 42), out var error)!;
        Assert.Null(error);
        Assert.Equal(clock.Now, Snapshot(clock, 42).TimestampUtc);
        Assert.Equal("42", ev.Metadata["sequence"]);
        Assert.Equal(clock.Now, ev.TimestampUtc);
        Assert.Equal(clock.Now.ToString("O"), ev.Metadata["sender_timestamp"]);
    }

    [Theory]
    [InlineData("\"sequence\":-1")]
    [InlineData("\"sequence\":1.5")]
    [InlineData("\"sequence\":\"1\"")]
    [InlineData("\"sequence\":9223372036854775808")]
    [InlineData("\"sequence\":null")]
    [InlineData("\"timestamp\":\"bad\"")]
    [InlineData("\"timestamp\":123")]
    [InlineData("\"timestamp\":\"2026-11-16T12:00:00\"")]
    [InlineData("\"timestamp\":\"2026-11-16T12:00:00+01:00\"")]
    public async Task Relay_InvalidOrderingFieldsFailClosedWithoutPrivateDiagnostics(string field)
    {
        var json = "{\"bot\":\"PRIVATE_BOT\",\"message\":\"PRIVATE_MESSAGE\",\"state\":\"working\"," + field + "}";
        Assert.Null(ImpressiveBotRelay.Parse(json));
        var output = new StringWriter();
        var connected = false;
        var exit = await ImpressiveBotRelay.RunAsync(new StringReader(json), () =>
        {
            connected = true;
            throw new InvalidOperationException();
        }, diagnostics: output);
        Assert.Equal(0, exit);
        Assert.False(connected);
        Assert.Contains("status rejected (invalid_", output.ToString());
        Assert.DoesNotContain("PRIVATE", output.ToString());
    }

    [Fact]
    public void ThreeBots_StayActiveWithTwentySecondSnapshotsBeyondStaleWindow()
    {
        var clock = new Clock();
        using var manager = new AgentSessionManager(clock);
        for (var sequence = 0; sequence < 40; sequence++)
        {
            foreach (var bot in new[] { "Patch", "Forge", "Sentry" })
                manager.Apply(Snapshot(clock, sequence, bot: bot));
            manager.SweepStale(clock.Now);
            Assert.Equal(3, manager.WorkingCount);
            clock.Now += TimeSpan.FromSeconds(20);
        }
        Assert.Equal(3, manager.All.Count);
        manager.Apply(Snapshot(clock, 40, "waiting", "Patch"));
        Assert.Equal(SessionActivity.WaitingForInput, manager.Get("impressivebot:Patch", "shared")!.State);
        Assert.Empty(manager.Get("impressivebot:Patch", "shared")!.PendingApprovalIds);
    }

    [Fact]
    public void EqualAndOlderSequencesCannotOverwriteOrFreshenAcrossTurns()
    {
        var clock = new Clock();
        using var manager = new AgentSessionManager(clock);
        var session = manager.Apply(Snapshot(clock, 100));
        var id = session.LastEventId;
        var at = session.LastEventUtc;
        var history = session.History.ToArray();
        clock.Now += TimeSpan.FromSeconds(20);
        foreach (var sequence in new long[] { 99, 100 })
            manager.Apply(Snapshot(clock, sequence, "finished") with
            { Metadata = new Dictionary<string, string>(Snapshot(clock, sequence).Metadata) { ["turn_id"] = "new-turn" } });
        Assert.Equal(SessionActivity.ExecutingTool, session.State);
        Assert.Equal(at, session.LastEventUtc);
        Assert.Equal(id, session.LastEventId);
        Assert.Equal(history, session.History);
        manager.Apply(Snapshot(clock, 101, "finished"));
        manager.Apply(Snapshot(clock, 0));
        Assert.Equal(SessionActivity.TurnFinished, session.State);
        manager.Apply(Snapshot(clock, 102));
        Assert.Equal(SessionActivity.ExecutingTool, session.State);
    }

    [Fact]
    public void IncreasingSequenceAcceptsCompletionAcrossSmallClockRollback()
    {
        var clock = new Clock();
        using var manager = new AgentSessionManager(clock);
        var session = manager.Apply(Snapshot(clock, 10));
        var previousTime = session.LastEventUtc;
        clock.Now -= TimeSpan.FromSeconds(5);
        var completion = Snapshot(clock, 11, "finished");
        manager.Apply(completion);
        Assert.Equal(SessionActivity.TurnFinished, session.State);
        Assert.Equal(11, session.LastSequence);
        Assert.Equal(completion.EventId, session.LastEventId);
        Assert.Equal(clock.Now, session.LastEventUtc);
        Assert.True(session.LastEventUtc < previousTime);
        Assert.False(session.Stale);
        Assert.Equal(0, manager.WorkingCount);
    }

    [Theory]
    [InlineData(9, -5)]
    [InlineData(9, 5)]
    [InlineData(10, -5)]
    [InlineData(10, 5)]
    public void NonIncreasingSequenceCannotOverwriteRegardlessOfTimestamp(long sequence, int seconds)
    {
        var clock = new Clock();
        using var manager = new AgentSessionManager(clock);
        var session = manager.Apply(Snapshot(clock, 10, "finished"));
        var at = session.LastEventUtc;
        var id = session.LastEventId;
        var history = session.History.ToArray();
        manager.Apply(Snapshot(clock, sequence, timestamp: clock.Now + TimeSpan.FromSeconds(seconds)));
        Assert.Equal(SessionActivity.TurnFinished, session.State);
        Assert.Equal(10, session.LastSequence);
        Assert.Equal(at, session.LastEventUtc);
        Assert.Equal(id, session.LastEventId);
        Assert.Equal(history, session.History);
    }

    [Theory]
    [InlineData("working")]
    [InlineData("waiting")]
    [InlineData("finished")]
    [InlineData("failed")]
    [InlineData("stopped")]
    public void ExpiredSnapshotCannotFreshenOrConsumeSequenceOrCreateActiveSession(string state)
    {
        var clock = new Clock();
        using var manager = new AgentSessionManager(clock);
        var session = manager.Apply(Snapshot(clock, 1));
        var at = session.LastEventUtc;
        clock.Now += AgentSessionManager.StaleAfter + TimeSpan.FromSeconds(1);
        manager.SweepStale(clock.Now);
        manager.Apply(Snapshot(clock, 100, state, timestamp: at));
        Assert.True(session.Stale);
        Assert.Equal(at, session.LastEventUtc);
        Assert.Equal(1, session.LastSequence);
        manager.Apply(Snapshot(clock, 100, state, bot: "Forge", timestamp: at));
        Assert.Null(manager.Get("impressivebot:Forge", "shared"));
        manager.Apply(Snapshot(clock, 2));
        Assert.False(session.Stale);
        Assert.Equal(1, manager.WorkingCount);
    }

    [Fact]
    public void CredibleSenderTimeControlsExpiryRatherThanReceiptTime()
    {
        var clock = new Clock();
        using var manager = new AgentSessionManager(clock);
        var sent = clock.Now - TimeSpan.FromMinutes(4);
        var session = manager.Apply(Snapshot(clock, 1, timestamp: sent));
        Assert.Equal(sent, session.LastEventUtc);
        clock.Now = sent + AgentSessionManager.StaleAfter + TimeSpan.FromTicks(1);
        manager.SweepStale(clock.Now);
        Assert.True(session.Stale);
    }

    [Fact]
    public void FutureSkewIsBoundedAndOlderUnsequencedSnapshotsCannotRewind()
    {
        var clock = new Clock();
        using var manager = new AgentSessionManager(clock);
        var session = manager.Apply(Snapshot(clock, 1, timestamp: clock.Now + AgentSessionManager.SenderClockSkew));
        Assert.Equal(clock.Now, session.LastEventUtc);
        manager.Apply(Snapshot(clock, 2, "finished", timestamp: clock.Now + AgentSessionManager.SenderClockSkew + TimeSpan.FromTicks(1)));
        Assert.Equal(1, session.LastSequence);
        var older = Snapshot(clock, 2, "finished", timestamp: clock.Now - TimeSpan.FromSeconds(1));
        manager.Apply(older with { Metadata = new Dictionary<string, string> { ["sender_timestamp"] = older.Metadata["sender_timestamp"] } });
        Assert.Equal(SessionActivity.ExecutingTool, session.State);
    }

    [Fact]
    public void NormalizedFutureTimestampRetainsSenderTimeForSkewValidation()
    {
        var clock = new Clock { Now = DateTimeOffset.UtcNow + TimeSpan.FromDays(1) };
        var ev = AgentEvent.Normalize(Snapshot(clock, 1), out _)!;
        Assert.True(ev.TimestampUtc < clock.Now);
        using var manager = new AgentSessionManager();
        manager.Apply(ev);
        Assert.Empty(manager.All);
    }

    [Fact]
    public void RejectedSnapshotsDoNotNotifyOrOverwriteDisplayMetadata()
    {
        var clock = new Clock();
        using var manager = new AgentSessionManager(clock);
        var changes = 0;
        manager.SessionChanged += _ => changes++;
        var session = manager.Apply(Snapshot(clock, 1));
        clock.Now += TimeSpan.FromSeconds(20);
        manager.Apply(Snapshot(clock, 1, "failed") with { Project = "private", Message = "private" });
        Assert.Equal(1, changes);
        Assert.Empty(session.Project);
        Assert.Empty(session.LastSummary);
    }

    [Fact]
    public void LegacyPayloadWithoutTimestampOrSequenceRemainsSupported()
    {
        var clock = new Clock();
        using var manager = new AgentSessionManager(clock);
        var ev = ImpressiveBotRelay.ToEvent(ImpressiveBotRelay.Parse("{\"bot\":\"Patch\",\"state\":\"working\"}")!);
        Assert.Equal(clock.Now, manager.Apply(ev).LastEventUtc);
        clock.Now += TimeSpan.FromSeconds(20);
        Assert.Equal(clock.Now, manager.Apply(ev).LastEventUtc);
    }

    [Theory]
    [InlineData(AgentEventTypes.Started)]
    [InlineData(AgentEventTypes.PromptSubmitted)]
    [InlineData(AgentEventTypes.ToolStarted)]
    [InlineData(AgentEventTypes.WaitingForInput)]
    public void SilentActivityExpiresThenUnknownRemainsVisibleForGraceWindow(string type)
    {
        var clock = new Clock();
        using var manager = new AgentSessionManager(clock);
        var session = manager.Apply(AgentEventFactory.Create("p", "s", type));
        var at = session.LastEventUtc;
        clock.Now = at + AgentSessionManager.StaleAfter;
        manager.SweepStale(clock.Now);
        Assert.False(session.Stale);
        Assert.Equal(1, manager.WorkingCount);

        clock.Now += TimeSpan.FromTicks(1);
        manager.SweepStale(clock.Now);
        Assert.True(session.Stale);
        Assert.Equal(SessionActivity.Unknown, session.State);
        Assert.Equal(at, session.LastEventUtc);
        Assert.Equal(0, manager.WorkingCount);
        Assert.Contains(session, manager.Active);

        clock.Now = at + AgentSessionManager.StaleAfter + AgentSessionManager.FinishedVisibleFor;
        Assert.Contains(session, manager.Active);
        clock.Now += TimeSpan.FromTicks(1);
        Assert.DoesNotContain(session, manager.Active);
        Assert.Same(session, manager.Get("p", "s"));
    }

    [Fact]
    public void ExternalWaitingCanRecoverWithFreshSnapshotAfterExpiry()
    {
        var clock = new Clock();
        using var manager = new AgentSessionManager(clock);
        var session = manager.Apply(Snapshot(clock, 1, "waiting"));
        clock.Now += AgentSessionManager.StaleAfter + TimeSpan.FromSeconds(1);
        manager.SweepStale(clock.Now);
        Assert.True(session.Stale);
        manager.Apply(Snapshot(clock, 2, "waiting"));
        Assert.False(session.Stale);
        Assert.Equal(SessionActivity.WaitingForInput, session.State);
        Assert.Equal(1, manager.WorkingCount);
        Assert.Empty(session.PendingApprovalIds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingLocalApprovalSurvivesSilenceAndPruning(bool activityAfterApproval)
    {
        var clock = new Clock();
        using var manager = new AgentSessionManager(clock);
        var session = manager.Apply(AgentEventFactory.Create("p", "s", AgentEventTypes.ApprovalRequested, approvalRequestId: "a"));
        if (activityAfterApproval)
            manager.Apply(AgentEventFactory.Create("p", "s", AgentEventTypes.ToolStarted));
        var state = session.State;
        clock.Now += TimeSpan.FromMinutes(30);
        manager.SweepStale(clock.Now);
        Assert.Same(session, manager.Get("p", "s"));
        Assert.False(session.Stale);
        Assert.Equal(state, session.State);
        Assert.Single(session.PendingApprovalIds);
        Assert.Equal(1, manager.WorkingCount);
    }

    [Fact]
    public void ApprovalResolutionUsesInjectedClockAndStartsNewActivityLease()
    {
        var clock = new Clock();
        using var manager = new AgentSessionManager(clock);
        var session = manager.Apply(AgentEventFactory.Create("p", "s", AgentEventTypes.ApprovalRequested, approvalRequestId: "a"));
        clock.Now += TimeSpan.FromMinutes(30);
        manager.MarkApprovalResolved("p", "s", "a");
        Assert.Equal(clock.Now, session.LastEventUtc);
        Assert.Equal(SessionActivity.Working, session.State);
        Assert.Empty(session.PendingApprovalIds);
        clock.Now += AgentSessionManager.StaleAfter + TimeSpan.FromTicks(1);
        manager.SweepStale(clock.Now);
        Assert.True(session.Stale);
    }

    [Fact]
    public void ActiveFinishedVisibilityUsesInjectedClock()
    {
        var clock = new Clock();
        using var manager = new AgentSessionManager(clock);
        var session = manager.Apply(AgentEventFactory.Create("p", "s", AgentEventTypes.TurnFinished));
        Assert.Contains(session, manager.Active);
        clock.Now += AgentSessionManager.FinishedVisibleFor;
        Assert.Contains(session, manager.Active);
        clock.Now += TimeSpan.FromTicks(1);
        Assert.DoesNotContain(session, manager.Active);
    }

    [Theory]
    [InlineData(AgentEventTypes.Started)]
    [InlineData(AgentEventTypes.PromptSubmitted)]
    [InlineData(AgentEventTypes.ToolStarted)]
    [InlineData(AgentEventTypes.WaitingForInput)]
    [InlineData(AgentEventTypes.ApprovalRequested)]
    public void WorkingCountExplicitlyExcludesStaleActivity(string type)
    {
        using var manager = new AgentSessionManager(new Clock());
        var session = manager.Apply(AgentEventFactory.Create("p", "s", type, approvalRequestId: "a"));
        Assert.Equal(1, manager.WorkingCount);
        // Exercise the count's own guard independently of the sweep's Unknown transition.
        typeof(AgentSession).GetProperty(nameof(AgentSession.Stale))!.SetValue(session, true);
        Assert.Equal(0, manager.WorkingCount);
    }

    [Fact]
    public void TupleIdentityIsOrdinalAndDelimiterSafe()
    {
        using var manager = new AgentSessionManager();
        foreach (var (provider, session) in new[] { ("impressivebot:Patch", "x|y"), ("impressivebot:Patch|x", "y"), ("impressivebot:patch", "x|y") })
            manager.Apply(AgentEventFactory.Create(provider, session, AgentEventTypes.ToolStarted));
        Assert.Equal(3, manager.All.Count);
        Assert.NotSame(manager.Get("impressivebot:Patch", "x|y"), manager.Get("impressivebot:Patch|x", "y"));
    }

    [Fact]
    public void BareHeartbeatCannotRestoreUnknownButExplicitActivityCanWithoutHistoryNoise()
    {
        var clock = new Clock();
        using var manager = new AgentSessionManager(clock);
        var session = manager.Apply(Snapshot(clock, 1));
        var at = session.LastEventUtc;
        clock.Now += AgentSessionManager.StaleAfter + TimeSpan.FromSeconds(1);
        manager.SweepStale(clock.Now);
        var heartbeat = AgentEventFactory.Create(session.Provider, session.SessionId, AgentEventTypes.Heartbeat);
        manager.Apply(heartbeat);
        Assert.True(session.Stale);
        Assert.Equal(SessionActivity.Unknown, session.State);
        Assert.Equal(at, session.LastEventUtc);
        manager.Apply(heartbeat with { Metadata = new Dictionary<string, string> { ["activity_type"] = AgentEventTypes.PromptSubmitted } });
        Assert.False(session.Stale);
        Assert.Equal(SessionActivity.Working, session.State);
        Assert.Single(session.History);
    }

    [Fact]
    public void EndedAndApprovalProtectionsSurviveHeartbeats()
    {
        using var manager = new AgentSessionManager();
        var session = manager.Apply(AgentEventFactory.Create("p", "s", AgentEventTypes.ApprovalRequested, approvalRequestId: "a"));
        manager.Apply(AgentEventFactory.Create("p", "s", AgentEventTypes.Heartbeat,
            metadata: new Dictionary<string, string> { ["activity_type"] = AgentEventTypes.ApprovalResolved }));
        Assert.Equal(SessionActivity.AwaitingApproval, session.State);
        Assert.Single(session.PendingApprovalIds);
        manager.Apply(AgentEventFactory.Create("p", "s", AgentEventTypes.SessionEnded));
        var at = session.LastEventUtc;
        manager.Apply(AgentEventFactory.Create("p", "s", AgentEventTypes.Heartbeat,
            metadata: new Dictionary<string, string> { ["activity_type"] = AgentEventTypes.ToolStarted, ["sequence"] = "100" }));
        Assert.True(session.Ended);
        Assert.Equal(SessionActivity.Ended, session.State);
        Assert.Equal(at, session.LastEventUtc);
        Assert.Equal(-1, session.LastSequence);
    }

    [Theory]
    [InlineData(AgentEventTypes.TurnFinished, SessionActivity.TurnFinished)]
    [InlineData(AgentEventTypes.Failed, SessionActivity.Error)]
    [InlineData(AgentEventTypes.Interrupted, SessionActivity.Interrupted)]
    public void GenericStatusDoesNotReactivateInactiveStates(string type, SessionActivity expected)
    {
        using var manager = new AgentSessionManager();
        var session = manager.Apply(AgentEventFactory.Create("p", "s", type));
        manager.Apply(AgentEventFactory.Create("p", "s", AgentEventTypes.Status));
        Assert.Equal(expected, session.State);
        Assert.Equal(0, manager.WorkingCount);
    }
}
