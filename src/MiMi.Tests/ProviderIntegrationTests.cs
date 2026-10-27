using System.Text.Json.Nodes;
using MiMi.AgentCli;
using MiMi.Agents;
using Xunit;

namespace MiMi.Tests;

public class ProviderIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"mimi-prov-{Guid.NewGuid():N}");
    private readonly string _exe;

    public ProviderIntegrationTests()
    {
        Directory.CreateDirectory(_dir);
        _exe = Path.Combine(_dir, "MiMi.AgentCli.exe");
        File.WriteAllText(_exe, "stub");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private ProviderIntegrationInspector Inspector(string? exe = null) =>
        new(_dir, _ => exe ?? _exe, _ => "test-version");

    private string HooksPath(string provider) => provider == "codex"
        ? Path.Combine(_dir, ".codex", "hooks.json")
        : Path.Combine(_dir, ".claude", "settings.json");

    private void WriteHooks(string provider, string executable, bool partial = false)
    {
        var events = ProviderHookCatalog.ExpectedEvents(provider).ToList();
        if (partial) events = events.Take(3).ToList();
        var marker = provider == "codex" ? "codex" : "claude-code";
        var hooks = new JsonObject();
        foreach (var ev in events)
        {
            var command = ev == "PermissionRequest"
                ? $"\"{executable}\" {marker}-permission"
                : $"\"{executable}\" activity --provider {marker}";
            hooks[ev] = new JsonArray(new JsonObject
            {
                ["hooks"] = new JsonArray(new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = command,
                }),
            });
        }
        var path = HooksPath(provider);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, new JsonObject { ["hooks"] = hooks }.ToJsonString());
    }

    [Fact]
    public void MissingCli_IsReportedAsNotInstalled()
    {
        var status = Inspector(exe: Path.Combine(_dir, "missing.exe")).InspectClaude();
        Assert.Equal(ProviderIntegrationState.NotInstalled, status.State);
        Assert.Equal(0, status.InstalledHooks);
    }

    [Fact]
    public void InstalledCliWithoutHooks_IsReportedAsHooksMissing()
    {
        var status = Inspector().InspectClaude();
        Assert.Equal(ProviderIntegrationState.HooksMissing, status.State);
        Assert.Contains("No MiMi hook file", status.Detail);
    }

    [Fact]
    public void CompleteClaudeHooks_AreReady()
    {
        WriteHooks("claude-code", _exe);
        var status = Inspector().InspectClaude();
        Assert.Equal(ProviderIntegrationState.Ready, status.State);
        Assert.Equal(status.ExpectedHooks, status.InstalledHooks);
        Assert.False(status.RequiresTrust);
    }

    [Fact]
    public void CompleteCodexHooks_ReportTrustUnverified_NotGuessed()
    {
        WriteHooks("codex", _exe);
        var status = Inspector().InspectCodex();
        Assert.Equal(ProviderIntegrationState.TrustNotVerified, status.State);
        Assert.True(status.RequiresTrust);
        Assert.Contains("/hooks", status.Instructions);
    }

    [Fact]
    public void PartialHooks_ReportMissingEvents()
    {
        WriteHooks("claude-code", _exe, partial: true);
        var status = Inspector().InspectClaude();
        Assert.Equal(ProviderIntegrationState.Incomplete, status.State);
        Assert.Equal(3, status.InstalledHooks);
        Assert.Contains("Missing:", status.Detail);
    }

    [Fact]
    public void StaleExecutablePath_NeedsAttention()
    {
        WriteHooks("claude-code", Path.Combine(_dir, "old", "MiMi.AgentCli.exe"));
        var status = Inspector().InspectClaude();
        Assert.Equal(ProviderIntegrationState.NeedsAttention, status.State);
        Assert.Contains("missing executable", status.Detail);
    }

    [Fact]
    public void OtherProvidersHooks_DoNotCountAsInstalled()
    {
        WriteHooks("claude-code", _exe);
        var codex = Inspector().InspectCodex();
        Assert.Equal(ProviderIntegrationState.HooksMissing, codex.State);
        Assert.Equal(0, codex.InstalledHooks);
    }

    [Fact]
    public void InvalidHookJson_NeedsAttention_WithoutCrashing()
    {
        var path = HooksPath("claude-code");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ broken json");
        var status = Inspector().InspectClaude();
        Assert.Equal(ProviderIntegrationState.NeedsAttention, status.State);
        Assert.Contains("could not be read", status.Detail);
    }

    [Fact]
    public void Version_IsReportedOnlyWhenProbed()
    {
        WriteHooks("claude-code", _exe);
        var inspector = Inspector();
        Assert.Equal("", inspector.InspectClaude(includeVersions: false).Version);
        Assert.Equal("test-version", inspector.InspectClaude(includeVersions: true).Version);
    }

    [Fact]
    public void ExecutableFromCommand_HandlesQuotesAndBareNames()
    {
        Assert.Equal(@"C:\Program Files\MiMi\MiMi.AgentCli.exe",
            ProviderIntegrationInspector.ExecutableFromCommand(@"""C:\Program Files\MiMi\MiMi.AgentCli.exe"" claude-permission"));
        Assert.Equal("MiMi.AgentCli.exe",
            ProviderIntegrationInspector.ExecutableFromCommand("MiMi.AgentCli.exe codex-permission"));
    }
}

public class InstallerUpgradeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"mimi-upg-{Guid.NewGuid():N}");
    private readonly string _exe;

    public InstallerUpgradeTests()
    {
        Directory.CreateDirectory(_dir);
        _exe = Path.Combine(_dir, "MiMi.AgentCli.exe");
        File.WriteAllText(_exe, "stub");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Reinstall_RefreshesMovedMiMiPath_WithoutAddingDuplicates()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """
        {
          "hooks": {
            "PermissionRequest": [
              { "hooks": [ { "type": "command", "command": "\"C:\\Old\\MiMi.AgentCli.exe\" claude-permission", "timeout": 10 } ] },
              { "hooks": [ { "type": "command", "command": "user-policy.py" } ] }
            ]
          }
        }
        """);

        var result = Installers.InstallClaude(260, path, _exe);

        Assert.Contains("UPDATED", result);
        var json = File.ReadAllText(path);
        Assert.DoesNotContain(@"C:\\Old\\MiMi.AgentCli.exe", json);
        Assert.Contains("user-policy.py", json);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(json,
            "MiMi\\.AgentCli\\.exe.*claude-permission").Count);
    }

    [Fact]
    public void Reinstall_DoesNotCreateBackupsWhenNothingChanges()
    {
        var path = Path.Combine(_dir, "settings.json");
        Installers.InstallClaude(260, path, _exe);
        var backups = Directory.GetFiles(_dir, "settings.json.mimi-backup-*").Length;
        Installers.InstallClaude(260, path, _exe);
        Assert.Equal(backups, Directory.GetFiles(_dir, "settings.json.mimi-backup-*").Length);
    }

    [Fact]
    public void Uninstall_MixedGroup_RemovesOnlyMiMiHook()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """
        {
          "hooks": {
            "Stop": [
              { "hooks": [
                { "type": "command", "command": "user-policy.py" },
                { "type": "command", "command": "\"C:\\Tools\\MiMi.AgentCli.exe\" activity --provider claude-code" }
              ] }
            ]
          }
        }
        """);

        var result = Installers.UninstallClaude(path);

        Assert.Contains("REMOVED", result);
        var json = File.ReadAllText(path);
        Assert.Contains("user-policy.py", json);
        Assert.DoesNotContain("MiMi.AgentCli", json);
    }

    [Fact]
    public void Reinstall_CollapsesDuplicateMiMiOnlyGroups()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """
        {
          "hooks": {
            "PreToolUse": [
              { "hooks": [ { "type": "command", "command": "\"C:\\A\\MiMi.AgentCli.exe\" activity --provider claude-code" } ] },
              { "hooks": [ { "type": "command", "command": "\"C:\\B\\MiMi.AgentCli.exe\" activity --provider claude-code" } ] }
            ]
          }
        }
        """);

        Installers.InstallClaude(260, path, _exe);

        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var preToolUse = doc.RootElement.GetProperty("hooks").GetProperty("PreToolUse");
        var groups = preToolUse.EnumerateArray().ToList();
        Assert.Single(groups);
        var commands = groups[0].GetProperty("hooks").EnumerateArray()
            .Select(h => h.GetProperty("command").GetString() ?? "")
            .ToList();
        Assert.Single(commands);
        Assert.Contains(_exe, commands[0]);

        // Every other activity event must still be installed exactly once.
        foreach (var eventName in ProviderHookCatalog.ClaudeActivityEvents.Where(e => e != "PreToolUse"))
        {
            var eventGroups = doc.RootElement.GetProperty("hooks").GetProperty(eventName)
                .EnumerateArray().ToList();
            Assert.Single(eventGroups);
        }
    }
}
