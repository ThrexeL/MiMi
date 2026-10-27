using MiMi.Agents;
using Xunit;

namespace MiMi.Tests;

public class AgentTimelineTests : IDisposable
{
    private readonly AgentSessionManager _sessions = new();

    public void Dispose() => _sessions.Dispose();

    private void Apply(string type, string session = "s1", string provider = "claude-code",
        string message = "", Dictionary<string, string>? metadata = null) =>
        _sessions.Apply(AgentEventFactory.Create(provider, session, type,
            title: "t", message: message, project: "MiMi", metadata: metadata));

    [Fact]
    public void RecordsConfirmedLifecycleAndToolEvents_InOrder()
    {
        Apply(AgentEventTypes.Started);
        Apply(AgentEventTypes.PromptSubmitted);
        Apply(AgentEventTypes.ToolStarted, metadata: new() { ["tool"] = "Bash" });
        Apply(AgentEventTypes.ToolFinished);
        Apply(AgentEventTypes.TurnFinished);

        var history = _sessions.Get("claude-code", "s1")!.History;
        Assert.Equal(new[]
        {
            "Started", "Received a task", "Running Bash", "Tool finished", "Turn completed",
        }, history.Select(h => h.Text));
    }

    [Fact]
    public void StatusAndUnknownEvents_AreNotHistoryNoise()
    {
        Apply(AgentEventTypes.Started);
        Apply(AgentEventTypes.Status, message: "just thinking");
        Apply("agent.unknown_future_event");

        Assert.Single(_sessions.Get("claude-code", "s1")!.History);
    }

    [Fact]
    public void ConsecutiveIdenticalEntries_AreCollapsed()
    {
        Apply(AgentEventTypes.ToolFinished);
        Apply(AgentEventTypes.ToolFinished);
        Apply(AgentEventTypes.ToolFinished);

        Assert.Single(_sessions.Get("claude-code", "s1")!.History);
    }

    [Fact]
    public void RapidToolChurn_ReplacesNewestToolLine()
    {
        Apply(AgentEventTypes.ToolStarted, metadata: new() { ["tool"] = "Read" });
        Apply(AgentEventTypes.ToolStarted, metadata: new() { ["tool"] = "Grep" });

        var history = _sessions.Get("claude-code", "s1")!.History;
        Assert.Single(history);
        Assert.Equal("Running Grep", history[0].Text);
    }

    [Fact]
    public void HistoryIsBounded()
    {
        for (var i = 0; i < 40; i++)
        {
            Apply(i % 2 == 0 ? AgentEventTypes.ToolFinished : AgentEventTypes.PromptSubmitted);
        }

        Assert.Equal(AgentSessionManager.TimelineLimit,
            _sessions.Get("claude-code", "s1")!.History.Count);
    }

    [Fact]
    public void HistoryNeverStoresPromptOrCommandText()
    {
        Apply(AgentEventTypes.Started, message: "PRIVATE PROMPT TEXT");
        Apply(AgentEventTypes.ApprovalRequested, message: "PRIVATE APPROVAL MESSAGE",
            metadata: new() { ["command"] = "curl SECRET_TOKEN", ["tool"] = "Bash" });

        var text = string.Join(" | ", _sessions.Get("claude-code", "s1")!.History.Select(h => h.Text));
        Assert.DoesNotContain("PRIVATE", text);
        Assert.DoesNotContain("SECRET_TOKEN", text);
        Assert.DoesNotContain("curl", text);
        Assert.Contains("Approval requested", text);
    }

    [Fact]
    public void SessionsKeepIndependentTimelines()
    {
        Apply(AgentEventTypes.Started, session: "one");
        Apply(AgentEventTypes.ToolStarted, session: "two", provider: "codex",
            metadata: new() { ["tool"] = "Bash" });

        Assert.Equal("Started", _sessions.Get("claude-code", "one")!.History.Single().Text);
        Assert.Equal("Running Bash", _sessions.Get("codex", "two")!.History.Single().Text);
    }

    [Fact]
    public void EndedSessionKeepsItsTimelineForTheVisibleGracePeriod()
    {
        Apply(AgentEventTypes.Started);
        Apply(AgentEventTypes.SessionEnded);

        var session = _sessions.Get("claude-code", "s1")!;
        Assert.True(session.Ended);
        Assert.Equal(new[] { "Started", "Session ended" }, session.History.Select(h => h.Text));
    }
}
