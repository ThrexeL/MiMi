using MiMi.AgentCli;
using MiMi.Agents;
using Xunit;

namespace MiMi.Tests;

public class ActivityRelayTests
{
    [Fact]
    public void ThursdayTurn_UsesExistingSessionPipeline()
    {
        using var sessions = new AgentSessionManager();
        foreach (var (type, state) in new[] {
            (AgentEventTypes.PromptSubmitted, SessionActivity.Working),
            (AgentEventTypes.ToolStarted, SessionActivity.ExecutingTool),
            (AgentEventTypes.ToolFinished, SessionActivity.Working),
            (AgentEventTypes.TurnFinished, SessionActivity.TurnFinished) })
        {
            var ev = ActivityRelay.Parse(AgentEventFactory.Create("thursday", "session", type,
                message: "Reading files", metadata: new Dictionary<string, string> { ["tool"] = "read_file" }).ToJson());
            Assert.NotNull(ev);
            Assert.Equal(state, sessions.Apply(ev).State);
        }
        Assert.Equal("Thursday", ProviderNames.Display("thursday"));
        Assert.Equal("read_file", sessions.Get("thursday", "session")!.LastToolName);
        Assert.Equal(0, sessions.WorkingCount);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"provider\":\"thursday\",\"type\":\"agent.approval_requested\",\"approval_request_id\":\"a\"}")]
    [InlineData("{\"provider\":\"thursday\",\"type\":\"agent.approval_resolved\",\"approval_request_id\":\"a\"}")]
    public void Relay_RejectsMalformedOrApprovalEvents(string input) => Assert.Null(ActivityRelay.Parse(input));
}
