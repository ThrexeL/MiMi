using MiMi.Agents;
using Xunit;

namespace MiMi.Tests;

public class ImpressiveBotStatusTests
{
    private const string Cli = @"C:\x\MiMi.AgentCli.exe";

    /// <summary>Builds a session through the real event path, exactly as the relay would.</summary>
    private static AgentSession Session(AgentSessionManager manager, string provider, string sessionId,
        string type, string message = "", string project = "")
    {
        manager.Apply(AgentEventFactory.Create(provider, sessionId, type, message: message, project: project));
        return manager.Get(provider, sessionId)!;
    }

    private static ImpressiveBotStatus Build(AgentSessionManager manager, string? cli = Cli) =>
        ImpressiveBotStatusReader.Build(manager.All, cli);

    [Fact]
    public void Build_ReportsNotInstalledWhenTheCliIsMissing()
    {
        using var manager = new AgentSessionManager();
        var status = Build(manager, cli: null);
        Assert.Equal(ImpressiveBotLink.NotInstalled, status.Link);
        Assert.Equal("not installed", status.StateLabel);
        Assert.Contains("MIMI_AGENT_CLI", status.Detail);
        Assert.False(status.IsHealthy);
    }

    [Fact]
    public void Build_ReportsReadyButNotLiveBeforeAnyStatusArrives()
    {
        using var manager = new AgentSessionManager();
        var status = Build(manager);
        Assert.Equal(ImpressiveBotLink.Ready, status.Link);
        Assert.Equal("no status yet", status.StateLabel);
        // The CLI existing must never be presented as "ImpressiveBot is running".
        Assert.False(status.IsHealthy);
        Assert.DoesNotContain("working", status.Detail);
    }

    [Fact]
    public void Build_CountsOnlyImpressiveBotSessions()
    {
        using var manager = new AgentSessionManager();
        Session(manager, "impressivebot:Patch", "t1", AgentEventTypes.ToolStarted, "Reviewing MiMi");
        Session(manager, "impressivebot:Echo", "t2", AgentEventTypes.WaitingForInput);
        Session(manager, "claude-code", "c1", AgentEventTypes.ToolStarted);
        Session(manager, "thursday", "x", AgentEventTypes.ToolStarted);

        var status = Build(manager);
        Assert.Equal(ImpressiveBotLink.Live, status.Link);
        Assert.Equal(2, status.Bots.Count);
        Assert.Equal(1, status.ActiveCount);
        Assert.Equal(1, status.WaitingCount);
        Assert.Equal("waiting for you", status.StateLabel);
        Assert.False(status.IsHealthy);
        Assert.Contains("Echo", status.Line);
    }

    [Fact]
    public void Build_IsHealthyOnlyWhenWorkingAndNothingNeedsThePerson()
    {
        using var manager = new AgentSessionManager();
        Session(manager, "impressivebot:Patch", "t1", AgentEventTypes.ToolStarted, "Fixing a bug");

        var status = Build(manager);
        Assert.True(status.IsHealthy);
        Assert.Equal("working", status.StateLabel);
        Assert.Equal("ImpressiveBot \u00b7 working \u00b7 Patch", status.Line);
    }

    [Fact]
    public void Build_AnIdleBotIsStillLive()
    {
        using var manager = new AgentSessionManager();
        Session(manager, "impressivebot:Momo", "t1", AgentEventTypes.TurnFinished);

        var status = Build(manager);
        Assert.Equal(ImpressiveBotLink.Live, status.Link);
        Assert.Equal("idle", status.StateLabel);
        Assert.True(status.IsHealthy);
        Assert.Equal(0, status.ActiveCount);
    }

    [Fact]
    public void Build_AFailureIsSurfacedAsNeedingAttention()
    {
        using var manager = new AgentSessionManager();
        Session(manager, "impressivebot:Vera", "t1", AgentEventTypes.Failed);

        var status = Build(manager);
        Assert.Equal(1, status.FailedCount);
        Assert.Equal("needs attention", status.StateLabel);
        Assert.False(status.IsHealthy);
        Assert.Contains("1 failed", status.Detail);
    }

    [Fact]
    public void Build_StaleSessionsAreNotCountedAsWorking()
    {
        using var manager = new AgentSessionManager();
        Session(manager, "impressivebot:Turnip", "t1", AgentEventTypes.ToolStarted, "Long job");
        manager.SweepStale(DateTimeOffset.UtcNow + AgentSessionManager.StaleAfter + TimeSpan.FromSeconds(10));

        var status = Build(manager);
        Assert.Equal(0, status.ActiveCount);
        Assert.Equal("idle", status.StateLabel);
    }

    [Fact]
    public void Build_NamesAreBoundedOnTheLine()
    {
        using var manager = new AgentSessionManager();
        Session(manager, "impressivebot:Patch", "t1", AgentEventTypes.ToolStarted);
        Session(manager, "impressivebot:Echo", "t2", AgentEventTypes.ToolStarted);
        Session(manager, "impressivebot:Cove", "t3", AgentEventTypes.ToolStarted);

        var status = Build(manager);
        Assert.Equal("working", status.StateLabel);
        Assert.Contains("+2", status.Line);
    }

    [Fact]
    public void Build_AnUnnamedSessionIsShownAsImpressiveBot()
    {
        using var manager = new AgentSessionManager();
        Session(manager, "impressivebot", "t1", AgentEventTypes.ToolStarted);
        Assert.Equal("ImpressiveBot", Build(manager).Bots[0].Bot);
    }

    [Fact]
    public void Build_ShowsTheProjectWhenOneIsReported()
    {
        using var manager = new AgentSessionManager();
        Session(manager, "impressivebot:Patch", "t1", AgentEventTypes.ToolStarted, "Editing", "MiMi");
        Assert.Equal("MiMi", Build(manager).Bots[0].Project);
    }

    [Fact]
    public void ResolveCli_PrefersTheOverrideAndFallsBackToLocalAppData()
    {
        var exists = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\custom\cli.exe" };
        bool Check(string path) => exists.Contains(path);

        Assert.Equal(@"C:\custom\cli.exe",
            ImpressiveBotStatusReader.ResolveCli(@"C:\custom\cli.exe", @"C:\Users\u\AppData\Local", Check));
        Assert.Null(ImpressiveBotStatusReader.ResolveCli(@"C:\missing\cli.exe", @"C:\Users\u\AppData\Local", Check));
        exists.Add(@"C:\Users\u\AppData\Local\MiMi\MiMi.AgentCli.exe");
        Assert.Equal(@"C:\Users\u\AppData\Local\MiMi\MiMi.AgentCli.exe",
            ImpressiveBotStatusReader.ResolveCli("", @"C:\Users\u\AppData\Local", Check));
    }

    [Fact]
    public void Ago_IsShortAndHuman()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal("5s ago", ImpressiveBotStatus.Ago(now.AddSeconds(-5), now));
        Assert.Equal("3m ago", ImpressiveBotStatus.Ago(now.AddMinutes(-3), now));
        Assert.Equal("2h ago", ImpressiveBotStatus.Ago(now.AddHours(-2), now));
    }

    [Fact]
    public void ApprovalNote_KeepsDecisionsInImpressiveBot()
    {
        Assert.Contains("ImpressiveBot", ImpressiveBotStatus.ApprovalNote);
    }
}
