using System.Text.Json;
using MiMi.AgentCli;
using MiMi.Agents;
using Xunit;

namespace MiMi.Tests;

public class ImpressiveBotLiveCountTests
{
    [Fact]
    public void ThinkingAndToolBots_StayCountedAcrossLongTurns_ThenSettleIndependently()
    {
        var clock = new Clock();
        using var manager = new AgentSessionManager(clock);
        string[] names = ["Patch", "Forge", "Sentry"];
        long sequence = 0;
        // The sender reports confirmed runtime activity, including quiet thinking, every 20 seconds.
        for (var tick = 0; tick < 31; tick++)
        {
            foreach (var bot in names) Send(bot, "working");
            manager.SweepStale(clock.GetUtcNow());
            Assert.Equal(3, manager.WorkingCount);
            Assert.Equal(3, Status().ActiveCount);
            Assert.Equal(3, manager.Active.Count);
            clock.Advance(TimeSpan.FromSeconds(20));
        }

        Send("Forge", "finished");
        Assert.Equal(2, manager.WorkingCount);
        Assert.Equal(2, Status().ActiveCount);
        Send("Forge", "working");
        Assert.Equal(3, manager.WorkingCount);
        Assert.Equal(3, Status().ActiveCount);

        Send("Patch", "waiting");
        Assert.Equal(3, manager.WorkingCount); // The compact count includes sessions waiting for the person.
        Assert.Equal(2, Status().ActiveCount);
        Assert.Equal(1, Status().WaitingCount);

        clock.Advance(AgentSessionManager.StaleAfter + TimeSpan.FromSeconds(1));
        manager.SweepStale(clock.GetUtcNow());
        Assert.Equal(0, manager.WorkingCount);
        Assert.Equal(0, Status().ActiveCount);
        Assert.Equal(0, Status().WaitingCount);
        Assert.Equal(ImpressiveBotLink.Stale, Status().Link);

        Send("Patch", "working");
        Send("Forge", "working");
        Send("Sentry", "working");
        Assert.Equal(3, manager.WorkingCount);
        Assert.Equal(3, Status().ActiveCount);
        Assert.All(manager.All, s => Assert.True(s.History.Count <= AgentSessionManager.TimelineLimit));

        ImpressiveBotStatus Status() => ImpressiveBotStatusReader.Build(manager.All, "fixture-cli", clock.GetUtcNow());
        void Send(string bot, string state)
        {
            var payload = JsonSerializer.Serialize(new
            {
                bot, state, session = "shared-room", sequence = ++sequence,
                timestamp = clock.GetUtcNow().ToString("O"),
            });
            var update = Assert.IsType<ImpressiveBotRelay.StatusUpdate>(ImpressiveBotRelay.Parse(payload));
            manager.Apply(ImpressiveBotRelay.ToEvent(update));
        }
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan interval) => _now += interval;
    }
}
