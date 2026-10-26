using System.Text.Json;
using MiMi.AgentCli;
using MiMi.Agents;
using Xunit;

namespace MiMi.Tests;

/// <summary>
/// Tests for the Claude Code PermissionRequest hook adapter: stdin parsing, tool
/// mapping, decision JSON shape (must NEVER contain updatedInput/updatedPermissions),
/// and installer preservation semantics.
/// </summary>
public class ClaudeHookTests
{
    private const string BashRequest = """
    {
      "session_id": "cc-sess-1",
      "transcript_path": null,
      "cwd": "C:\\code\\MyApplication",
      "permission_mode": "default",
      "hook_event_name": "PermissionRequest",
      "tool_name": "Bash",
      "tool_input": { "command": "npm run build", "description": "build it" }
    }
    """;

    [Fact]
    public void ParseStdin_Bash_MapsCommandAndProject()
    {
        var parsed = ClaudeHook.ParseStdin(BashRequest);
        var ev = parsed.Event;

        Assert.Equal("claude-code", ev.Provider);
        Assert.Equal("cc-sess-1", ev.SessionId);
        Assert.Equal("MyApplication", ev.Project);
        Assert.Equal(AgentEventTypes.ApprovalRequested, ev.Type);
        Assert.Equal("Execute Command", ev.Metadata["action_kind"]);
        Assert.Equal("npm run build", ev.Metadata["command"]);
        Assert.Equal("Bash", ev.Metadata["tool"]);
        Assert.StartsWith("cc-", ev.ApprovalRequestId);
    }

    [Fact]
    public void ParseStdin_EditTool_MapsFilePath()
    {
        var parsed = ClaudeHook.ParseStdin("""
        { "hook_event_name": "PermissionRequest", "session_id": "s", "cwd": "/tmp/proj",
          "tool_name": "Edit", "tool_input": { "file_path": "src/app.ts", "old_string": "a", "new_string": "b" } }
        """);
        Assert.Equal("Edit File", parsed.Event.Metadata["action_kind"]);
        Assert.Equal("src/app.ts", parsed.Event.Metadata["command"]);
    }

    [Fact]
    public void ParseStdin_UnknownTool_UsesJsonExcerpt()
    {
        var parsed = ClaudeHook.ParseStdin("""
        { "hook_event_name": "PermissionRequest", "session_id": "s", "cwd": "/p",
          "tool_name": "SomeMcpTool", "tool_input": { "query": "hello" } }
        """);
        Assert.Equal("Use SomeMcpTool", parsed.Event.Metadata["action_kind"]);
        Assert.Contains("hello", parsed.Event.Metadata["command"]);
    }

    [Fact]
    public void ParseStdin_WrongEvent_Rejected()
    {
        Assert.ThrowsAny<Exception>(() => ClaudeHook.ParseStdin(
            """{ "hook_event_name": "PreToolUse", "tool_name": "Bash", "tool_input": {} }"""));
    }

    [Fact]
    public void ParseStdin_FutureInputFields_Tolerated()
    {
        // Newer Claude Code builds add fields (turn_id, agent_id, permission_suggestions);
        // the parser must ignore unknown members, not throw.
        var parsed = ClaudeHook.ParseStdin("""
        { "hook_event_name": "PermissionRequest", "session_id": "s", "cwd": "/p",
          "turn_id": "t1", "agent_id": "a1", "permission_suggestions": [],
          "tool_name": "Bash", "tool_input": { "command": "ls" } }
        """);
        Assert.Equal("ls", parsed.Event.Metadata["command"]);
    }

    [Fact]
    public void WriteDecision_AllowAndDeny_ExactShape_NoUpdatedInput()
    {
        foreach (var (decision, behavior) in new[] { (ApprovalDecision.Approved, "allow"), (ApprovalDecision.Denied, "deny") })
        {
            using var sw = new StringWriter();
            ClaudeHook.WriteDecision(sw, decision, "reason text");
            var json = sw.ToString();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.True(root.TryGetProperty("hookSpecificOutput", out var hso));
            Assert.Equal("PermissionRequest", hso.GetProperty("hookEventName").GetString());
            var d = hso.GetProperty("decision");
            Assert.Equal(behavior, d.GetProperty("behavior").GetString());
            Assert.Equal("reason text", d.GetProperty("message").GetString());
            // Security: these fields fail closed in Claude Code and must never be emitted.
            Assert.False(d.TryGetProperty("updatedInput", out _));
            Assert.False(d.TryGetProperty("updatedPermissions", out _));
            Assert.False(d.TryGetProperty("interrupt", out _));
        }
    }
}

public class InstallerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"mimi-inst-{Guid.NewGuid():N}");

    public InstallerTests() => Directory.CreateDirectory(_dir);

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Install_CreatesHookEntry()
    {
        var result = Installers.InstallClaude(260, SettingsPath);
        Assert.Contains("INSTALLED", result);
        var json = File.ReadAllText(SettingsPath);
        Assert.Contains("claude-permission", json);
        Assert.Contains("PermissionRequest", json);
    }

    [Fact]
    public void Install_IsIdempotent()
    {
        Installers.InstallClaude(260, SettingsPath);
        var second = Installers.InstallClaude(260, SettingsPath);
        Assert.Contains("ALREADY INSTALLED", second);
        var json = File.ReadAllText(SettingsPath);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(json, "claude-permission").Count);
    }

    [Fact]
    public void Install_PreservesExistingHooks()
    {
        File.WriteAllText(SettingsPath, """
        {
          "model": "opus",
          "hooks": {
            "PreToolUse": [ { "matcher": "Bash", "hooks": [ { "type": "command", "command": "my-guard.sh" } ] } ],
            "Stop": [ { "hooks": [ { "type": "command", "command": "on-stop.sh" } ] } ]
          }
        }
        """);
        Installers.InstallClaude(260, SettingsPath);
        var json = File.ReadAllText(SettingsPath);

        // Everything the user had is still there, untouched.
        Assert.Contains("my-guard.sh", json);
        Assert.Contains("on-stop.sh", json);
        Assert.Contains("\"model\"", json);
        Assert.Contains("claude-permission", json);

        using var doc = JsonDocument.Parse(json);
        var hooks = doc.RootElement.GetProperty("hooks");
        Assert.True(hooks.TryGetProperty("PreToolUse", out _));
        Assert.True(hooks.TryGetProperty("Stop", out _));
        Assert.True(hooks.TryGetProperty("PermissionRequest", out _));
    }

    [Fact]
    public void Install_NonObjectHooksKey_SkipsAndPreserves()
    {
        File.WriteAllText(SettingsPath, """{ "hooks": "not-an-object" }""");
        var result = Installers.InstallClaude(260, SettingsPath);
        Assert.Contains("SKIP", result);
        Assert.Equal("""{ "hooks": "not-an-object" }""", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Install_CorruptSettingsFile_ThrowsWithoutWriting()
    {
        File.WriteAllText(SettingsPath, "{ broken json");
        Assert.ThrowsAny<Exception>(() => Installers.InstallClaude(260, SettingsPath));
        Assert.Equal("{ broken json", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Uninstall_RemovesOnlyMiMiEntry()
    {
        File.WriteAllText(SettingsPath, """
        {
          "hooks": {
            "PermissionRequest": [
              { "hooks": [ { "type": "command", "command": "other-gate.py" } ] },
              { "hooks": [ { "type": "command", "command": "MiMi.AgentCli claude-permission", "timeout": 260 } ] }
            ]
          }
        }
        """);
        var result = Installers.UninstallClaude(SettingsPath);
        Assert.Contains("REMOVED", result);

        var json = File.ReadAllText(SettingsPath);
        Assert.Contains("other-gate.py", json);
        Assert.DoesNotContain("claude-permission", json);
    }

    [Fact]
    public void Uninstall_WithoutMiMiHook_DoesNothing()
    {
        File.WriteAllText(SettingsPath, """{ "hooks": { "PermissionRequest": [ { "hooks": [ { "type": "command", "command": "x.sh" } ] } ] } }""");
        var result = Installers.UninstallClaude(SettingsPath);
        Assert.Contains("NOTHING TO DO", result);
    }
}

public class CodexNotifyTests
{
    [Fact]
    public void Parse_TurnComplete_MapsToCompleted()
    {
        var ev = CodexNotify.Parse("""
        { "type": "agent-turn-complete", "turn-id": "turn-9",
          "last-assistant-message": "Refactored the module.", "cwd": "C:\\repo\\app" }
        """);
        Assert.NotNull(ev);
        Assert.Equal("codex", ev!.Provider);
        Assert.Equal(AgentEventTypes.Completed, ev.Type);
        Assert.Equal("turn-9", ev.SessionId);
        Assert.Equal("app", ev.Project);
        Assert.Equal("Refactored the module.", ev.Message);
    }

    [Fact]
    public void Parse_LongMessage_Truncated()
    {
        var longMessage = new string('x', 900);
        var ev = CodexNotify.Parse($$"""{ "type": "agent-turn-complete", "last-assistant-message": "{{longMessage}}" }""");
        Assert.True(ev!.Message.Length <= 200);
        Assert.EndsWith("...", ev.Message);
    }

    [Fact]
    public void Parse_UnknownType_MapsToStatus()
    {
        var ev = CodexNotify.Parse("""{ "type": "something-new" }""");
        Assert.Equal(AgentEventTypes.Status, ev!.Type);
        Assert.DoesNotContain("approval_requested", ev.Type);
    }
}

public class CodexHookTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"mimi-codex-{Guid.NewGuid():N}");

    public CodexHookTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void ParseStdin_CodexBash_MapsProviderAndCommand()
    {
        var parsed = ClaudeHook.ParseStdin("""
        { "hook_event_name": "PermissionRequest", "session_id": "thread-1", "cwd": "C:/repo/api",
          "turn_id": "7", "tool_name": "Bash",
          "tool_input": { "command": "cargo test", "description": "run tests" } }
        """, provider: "codex");
        Assert.Equal("codex", parsed.Event.Provider);
        Assert.Equal("Execute Command", parsed.Event.Metadata["action_kind"]);
        Assert.Equal("cargo test", parsed.Event.Metadata["command"]);
        Assert.Equal("api", parsed.Event.Project);
    }

    [Fact]
    public void ParseStdin_CodexPatch_MapsApplyPatch()
    {
        var parsed = ClaudeHook.ParseStdin("""
        { "hook_event_name": "PermissionRequest", "session_id": "t", "cwd": "/r",
          "tool_name": "apply_patch", "tool_input": { "command": "*** Begin Patch\n+line" } }
        """, provider: "codex");
        Assert.Equal("Apply Patch", parsed.Event.Metadata["action_kind"]);
        Assert.StartsWith("*** Begin Patch", parsed.Event.Metadata["command"]);
    }

    [Fact]
    public void InstallCodex_PreservesExistingAndIsIdempotent()
    {
        var hooksPath = Path.Combine(_dir, "hooks.json");
        File.WriteAllText(hooksPath, """
        { "description": "my hooks",
          "hooks": { "PreToolUse": [ { "matcher": "^Bash$", "hooks": [ { "type": "command", "command": "guard.py" } ] } ] } }
        """);
        var r1 = Installers.InstallCodex(260, hooksPath);
        Assert.Contains("INSTALLED", r1);
        var r2 = Installers.InstallCodex(260, hooksPath);
        Assert.Contains("ALREADY INSTALLED", r2);

        var json = File.ReadAllText(hooksPath);
        Assert.Contains("guard.py", json);            // user hooks preserved
        Assert.Contains("codex-permission", json);    // MiMi added
        Assert.Contains("PreToolUse", json);
    }

    [Fact]
    public void UninstallCodex_RemovesOnlyMiMiEntry()
    {
        var hooksPath = Path.Combine(_dir, "hooks.json");
        File.WriteAllText(hooksPath, """
        { "hooks": { "PermissionRequest": [
            { "matcher": "Bash", "hooks": [ { "type": "command", "command": "policy.py" } ] },
            { "hooks": [ { "type": "command", "command": "MiMi.AgentCli codex-permission", "timeout": 260 } ] } ] } }
        """);
        var result = Installers.UninstallCodex(hooksPath);
        Assert.Contains("REMOVED", result);
        var json = File.ReadAllText(hooksPath);
        Assert.Contains("policy.py", json);
        Assert.DoesNotContain("codex-permission", json);
    }
}
