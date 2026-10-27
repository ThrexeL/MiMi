using MiMi.AgentCli;
using MiMi.Agents;
using Xunit;

namespace MiMi.Tests;

/// <summary>
/// Phase 3.6: agent lifecycle tracking, multi-agent behavior, staleness, and the
/// activity-hook adapter. Sessions are keyed by (provider, session id) — never project.
/// </summary>
public class AgentSessionManagerTests : IDisposable
{
    private readonly AgentSessionManager _mgr = new();

    public void Dispose() => _mgr.Dispose();

    private static AgentEvent Ev(string provider, string session, string type,
        string? project = null, string? approvalId = null, string? tool = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        var md = metadata is null ? new Dictionary<string, string>() : new Dictionary<string, string>(metadata);
        if (tool is not null) md["tool"] = tool;
        return AgentEventFactory.Create(provider, session, type,
            project: project, approvalRequestId: approvalId, metadata: md);
    }

    [Fact]
    public void Lifecycle_FullTurn_EndsTerminal()
    {
        _mgr.Apply(Ev("claude-code", "s1", AgentEventTypes.Started, project: "App"));
        _mgr.Apply(Ev("claude-code", "s1", AgentEventTypes.PromptSubmitted));
        _mgr.Apply(Ev("claude-code", "s1", AgentEventTypes.ToolStarted, tool: "Bash"));
        Assert.Equal(SessionActivity.ExecutingTool, _mgr.Get("claude-code", "s1")!.State);

        _mgr.Apply(Ev("claude-code", "s1", AgentEventTypes.ToolFinished));
        Assert.Equal(SessionActivity.Working, _mgr.Get("claude-code", "s1")!.State);

        _mgr.Apply(Ev("claude-code", "s1", AgentEventTypes.TurnFinished));
        var s = _mgr.Get("claude-code", "s1")!;
        Assert.Equal(SessionActivity.TurnFinished, s.State);
        Assert.False(s.Ended); // a finished turn does NOT end the session

        _mgr.Apply(Ev("claude-code", "s1", AgentEventTypes.SessionEnded));
        s = _mgr.Get("claude-code", "s1")!;
        Assert.True(s.Ended);
        Assert.Equal(SessionActivity.Ended, s.State);
    }

    [Fact]
    public void PromptSubmitted_AndErrors_MapCorrectly()
    {
        _mgr.Apply(Ev("codex", "c1", AgentEventTypes.PromptSubmitted));
        Assert.Equal(SessionActivity.Working, _mgr.Get("codex", "c1")!.State);

        _mgr.Apply(Ev("codex", "c1", AgentEventTypes.ToolFailed));
        Assert.Equal(SessionActivity.Error, _mgr.Get("codex", "c1")!.State);

        _mgr.Apply(Ev("codex", "c1", AgentEventTypes.Interrupted));
        Assert.Equal(SessionActivity.Interrupted, _mgr.Get("codex", "c1")!.State);
    }

    [Fact]
    public void TwoSessions_SameProvider_TrackedIndependently()
    {
        _mgr.Apply(Ev("claude-code", "a", AgentEventTypes.ToolStarted, project: "App"));
        _mgr.Apply(Ev("claude-code", "b", AgentEventTypes.PromptSubmitted, project: "App"));

        Assert.Equal(SessionActivity.ExecutingTool, _mgr.Get("claude-code", "a")!.State);
        Assert.Equal(SessionActivity.Working, _mgr.Get("claude-code", "b")!.State);
        Assert.Equal(2, _mgr.All.Count);
    }

    [Fact]
    public void MixedProviders_TrackedIndependently()
    {
        _mgr.Apply(Ev("claude-code", "shared", AgentEventTypes.Started, project: "Web"));
        _mgr.Apply(Ev("codex", "shared", AgentEventTypes.Started, project: "Web"));
        Assert.Equal(2, _mgr.All.Count); // same id, different provider = two sessions
    }

    [Fact]
    public void ApprovalLifecycle_CorrelatesPendingIds()
    {
        _mgr.Apply(Ev("claude-code", "s1", AgentEventTypes.ApprovalRequested, approvalId: "ap-1"));
        var s = _mgr.Get("claude-code", "s1")!;
        Assert.Equal(SessionActivity.AwaitingApproval, s.State);
        Assert.Contains("ap-1", s.PendingApprovalIds);

        _mgr.Apply(Ev("claude-code", "s1", AgentEventTypes.ApprovalResolved, approvalId: "ap-1"));
        Assert.Empty(s.PendingApprovalIds);
        Assert.Equal(SessionActivity.Working, s.State);
    }

    [Fact]
    public void MarkApprovalResolved_UpdatesIndicatorState()
    {
        _mgr.Apply(Ev("codex", "x", AgentEventTypes.ApprovalRequested, approvalId: "ap-9"));
        _mgr.MarkApprovalResolved("codex", "x", "ap-9");
        Assert.Equal(SessionActivity.Working, _mgr.Get("codex", "x")!.State);
    }

    [Fact]
    public void Staleness_FlipsToUnknown_NeverToFinished()
    {
        _mgr.Apply(Ev("claude-code", "stale", AgentEventTypes.ToolStarted));

        // Simulate 3 minutes of silence.
        _mgr.SweepStale(DateTimeOffset.UtcNow.AddMinutes(3));
        var s = _mgr.Get("claude-code", "stale")!;
        Assert.True(s.Stale);
        Assert.Equal(SessionActivity.Unknown, s.State);

        // A fresh event recovers the session.
        _mgr.Apply(Ev("claude-code", "stale", AgentEventTypes.ToolStarted));
        Assert.False(_mgr.Get("claude-code", "stale")!.Stale);
    }

    [Fact]
    public void AwaitingApproval_DoesNotGoStale()
    {
        _mgr.Apply(Ev("claude-code", "wait", AgentEventTypes.ApprovalRequested, approvalId: "ap"));
        _mgr.SweepStale(DateTimeOffset.UtcNow.AddMinutes(30));
        var s = _mgr.Get("claude-code", "wait")!;
        Assert.False(s.Stale); // approvals keep waiting per their own TTL
        Assert.Equal(SessionActivity.AwaitingApproval, s.State);
    }

    [Fact]
    public void EndedSession_IgnoresLateEvents()
    {
        _mgr.Apply(Ev("claude-code", "done", AgentEventTypes.SessionEnded));
        _mgr.Apply(Ev("claude-code", "done", AgentEventTypes.ToolStarted)); // late/duplicate replay
        var s = _mgr.Get("claude-code", "done")!;
        Assert.True(s.Ended);
        Assert.Equal(SessionActivity.Ended, s.State);
    }

    [Fact]
    public void OutOfOrderSequence_CannotRewindState()
    {
        var md1 = new Dictionary<string, string> { ["sequence"] = "5" };
        var md2 = new Dictionary<string, string> { ["sequence"] = "2" };
        _mgr.Apply(Ev("codex", "o1", AgentEventTypes.ToolStarted, metadata: md1));
        _mgr.Apply(Ev("codex", "o1", AgentEventTypes.ToolFinished, metadata: md2)); // older, must be ignored
        Assert.Equal(SessionActivity.ExecutingTool, _mgr.Get("codex", "o1")!.State);
    }

    [Fact]
    public void EndedHistory_IsBounded()
    {
        for (var i = 0; i < 25; i++)
            _mgr.Apply(Ev("codex", $"end-{i}", AgentEventTypes.SessionEnded));
        _mgr.SweepStale(DateTimeOffset.UtcNow);
        Assert.True(_mgr.All.Count(s => s.Ended) <= AgentSessionManager.EndedHistoryLimit + 5,
            "ended history should be pruned, not grow unbounded");
    }

    [Fact]
    public void WorkingCount_IgnoresEndedAndStale()
    {
        _mgr.Apply(Ev("codex", "w1", AgentEventTypes.ToolStarted));
        _mgr.Apply(Ev("claude-code", "w2", AgentEventTypes.SessionEnded));
        Assert.Equal(1, _mgr.WorkingCount);
    }
}

public class ActivityHookTests
{
    [Fact]
    public void ClaudePreToolUse_MapsToToolStarted()
    {
        var ev = ActivityHook.Parse("""
        { "hook_event_name": "PreToolUse", "session_id": "s1", "cwd": "C:/repos/App",
          "tool_name": "Bash", "tool_input": { "command": "dotnet test" } }
        """, provider: "claude-code");
        Assert.NotNull(ev);
        Assert.Equal(AgentEventTypes.ToolStarted, ev!.Type);
        Assert.Equal("App", ev.Project);
        Assert.Equal("Bash", ev.Metadata["tool"]);
        Assert.Contains("dotnet test", ev.Message);
    }

    [Fact]
    public void ClaudeUserPromptSubmit_MapsToWorking_WithExcerpt()
    {
        var longPrompt = new string('x', 400);
        var ev = ActivityHook.Parse($$"""
        { "hook_event_name": "UserPromptSubmit", "session_id": "s2", "cwd": "/w", "prompt": "{{longPrompt}}" }
        """, provider: "claude-code");
        Assert.NotNull(ev);
        Assert.Equal(AgentEventTypes.PromptSubmitted, ev!.Type);
        Assert.True(ev.Message.Length <= 120);
        Assert.EndsWith("...", ev.Message);
    }

    [Fact]
    public void ClaudeStop_MapsToTurnFinished_NotSessionEnded()
    {
        var ev = ActivityHook.Parse("""
        { "hook_event_name": "Stop", "session_id": "s3", "cwd": "/w" }
        """, provider: "claude-code");
        Assert.Equal(AgentEventTypes.TurnFinished, ev!.Type);
    }

    [Fact]
    public void ClaudeSessionEnd_AndFailure_MapCorrectly()
    {
        var end = ActivityHook.Parse("""{ "hook_event_name": "SessionEnd", "session_id": "s", "cwd": "/w" }""", "claude-code");
        var fail = ActivityHook.Parse("""{ "hook_event_name": "PostToolUseFailure", "session_id": "s", "cwd": "/w", "tool_name": "Bash" }""", "claude-code");
        Assert.Equal(AgentEventTypes.SessionEnded, end!.Type);
        Assert.Equal(AgentEventTypes.ToolFailed, fail!.Type);
    }

    [Fact]
    public void CodexInterrupt_MapsToInterrupted()
    {
        var ev = ActivityHook.Parse("""
        { "hook_event_name": "Interrupt", "session_id": "c1", "cwd": "/p" }
        """, provider: "codex");
        Assert.Equal(AgentEventTypes.Interrupted, ev!.Type);
        Assert.Equal("codex", ev.Provider);
    }

    [Fact]
    public void PermissionRequest_IsNotAnActivityEvent()
    {
        // Approval flow owns this event; the activity path must not double-report it.
        Assert.Null(ActivityHook.Parse("""{ "hook_event_name": "PermissionRequest", "session_id": "s", "cwd": "/w", "tool_name": "Bash", "tool_input": {} }""", "claude-code"));
    }

    [Fact]
    public void UnknownEvent_ReturnsNull()
    {
        Assert.Null(ActivityHook.Parse("""{ "hook_event_name": "PreCompact", "session_id": "s", "cwd": "/w" }""", "claude-code"));
    }

    [Fact]
    public void TurnId_AndSequence_CarryThrough()
    {
        var ev = ActivityHook.Parse("""
        { "hook_event_name": "PreToolUse", "session_id": "s", "cwd": "/w",
          "turn_id": "t9", "sequence": "12", "tool_name": "Bash", "tool_input": { "command": "ls" } }
        """, provider: "claude-code");
        Assert.Equal("t9", ev!.Metadata["turn_id"]);
        Assert.Equal("12", ev.Metadata["sequence"]);
    }
}

public class ActivityInstallerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"mimi-act-{Guid.NewGuid():N}");

    public ActivityInstallerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void InstallClaude_AddsPermissionAndAllActivityHooks()
    {
        var path = Path.Combine(_dir, "settings.json");
        Installers.InstallClaude(260, path);
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var events = doc.RootElement.GetProperty("hooks").EnumerateObject().Select(p => p.Name).ToHashSet();

        Assert.Contains("PermissionRequest", events);
        foreach (var expected in new[] { "SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "PostToolUseFailure", "Stop", "SessionEnd" })
            Assert.Contains(expected, events);
    }

    [Fact]
    public void InstallClaude_ActivityHooks_HaveNoTimeoutGate()
    {
        var path = Path.Combine(_dir, "settings.json");
        Installers.InstallClaude(260, path);
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var hook = doc.RootElement.GetProperty("hooks").GetProperty("PreToolUse")[0].GetProperty("hooks")[0];
        Assert.False(hook.TryGetProperty("timeout", out _), "activity hooks must use the provider default timeout");
    }

    [Fact]
    public void InstallClaude_IsIdempotent_AcrossActivityHooks()
    {
        var path = Path.Combine(_dir, "settings.json");
        Installers.InstallClaude(260, path);
        var first = File.ReadAllText(path);
        Installers.InstallClaude(260, path); // second pass must add nothing
        var second = File.ReadAllText(path);

        var count = (string json) => System.Text.RegularExpressions.Regex.Matches(json, "activity --provider claude-code").Count;
        Assert.Equal(count(first), count(second)); // no duplicates added
        Assert.Equal(9, count(second));            // one per lifecycle event
    }

    [Fact]
    public void InstallCodex_AddsInterruptAndLifecycleHooks()
    {
        var path = Path.Combine(_dir, "hooks.json");
        Installers.InstallCodex(260, path);
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var events = doc.RootElement.GetProperty("hooks").EnumerateObject().Select(p => p.Name).ToHashSet();
        Assert.Contains("PermissionRequest", events);
        Assert.Contains("Interrupt", events);
        Assert.Contains("SessionStart", events);
        Assert.Contains("SessionEnd", events);
    }

    [Fact]
    public void Uninstall_RemovesEveryMiMiHook_ButKeepsUserHooks()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """
        { "hooks": { "Stop": [ { "hooks": [ { "type": "command", "command": "user-script.sh" } ] } ] } }
        """);
        Installers.InstallClaude(260, path);
        Installers.UninstallClaude(path);

        var json = File.ReadAllText(path);
        Assert.Contains("user-script.sh", json);
        Assert.DoesNotContain("MiMi.AgentCli", json);
    }
}
