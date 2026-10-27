using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace MiMi.Agents;

/// <summary>Provider hook events MiMi installs. Shared by installers and diagnostics so the
/// expected set can never drift between the two.</summary>
public static class ProviderHookCatalog
{
    public static readonly string[] ClaudeActivityEvents =
    {
        "SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "PostToolUseFailure",
        "Stop", "SessionEnd", "SubagentStart", "SubagentStop",
    };

    public static readonly string[] CodexActivityEvents =
    {
        "SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse",
        "Stop", "Interrupt", "SessionEnd", "SubagentStart", "SubagentStop",
    };

    public static string[] ActivityEvents(string provider) => provider switch
    {
        "codex" => CodexActivityEvents,
        "claude-code" => ClaudeActivityEvents,
        _ => Array.Empty<string>(),
    };

    /// <summary>PermissionRequest plus every lifecycle hook MiMi expects.</summary>
    public static string[] ExpectedEvents(string provider) =>
        new[] { "PermissionRequest" }.Concat(ActivityEvents(provider)).ToArray();
}

public enum ProviderIntegrationState
{
    NotInstalled,
    HooksMissing,
    Incomplete,
    NeedsAttention,
    TrustNotVerified,
    Ready,
}

/// <summary>Verified integration facts for one provider. Nothing here is inferred from
/// credentials or conversation data: only executable presence, hook files, and hook
/// command paths are inspected.</summary>
public sealed record ProviderIntegrationStatus(
    string Provider,
    string DisplayName,
    ProviderIntegrationState State,
    string Summary,
    string Detail,
    string ConfigPath,
    string ExecutablePath,
    string Version,
    int InstalledHooks,
    int ExpectedHooks,
    bool RequiresTrust,
    string Instructions)
{
    public bool IsHealthy => State is ProviderIntegrationState.Ready
        or ProviderIntegrationState.TrustNotVerified;
}

/// <summary>
/// Read-only diagnostics for Claude Code and Codex hook integration. Codex hook trust
/// lives inside Codex and is not exposed to other processes, so a healthy Codex install
/// is reported as "Trust not verified" instead of guessing.
/// </summary>
public sealed class ProviderIntegrationInspector
{
    private readonly string _profile;
    private readonly Func<string, string?> _findExecutable;
    private readonly Func<string, string?> _probeVersion;

    public ProviderIntegrationInspector(
        string? userProfile = null,
        Func<string, string?>? findExecutable = null,
        Func<string, string?>? probeVersion = null)
    {
        _profile = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _findExecutable = findExecutable ?? FindOnPath;
        _probeVersion = probeVersion ?? ProbeVersion;
    }

    public string ClaudeSettingsPath => Path.Combine(_profile, ".claude", "settings.json");
    public string CodexHooksPath => Path.Combine(_profile, ".codex", "hooks.json");

    public IReadOnlyList<ProviderIntegrationStatus> InspectAll(bool includeVersions = false) =>
        new[] { InspectClaude(includeVersions), InspectCodex(includeVersions) };

    public ProviderIntegrationStatus InspectClaude(bool includeVersions = false) =>
        Inspect("claude-code", "Claude Code", "claude", ClaudeSettingsPath, includeVersions);

    public ProviderIntegrationStatus InspectCodex(bool includeVersions = false) =>
        Inspect("codex", "Codex", "codex", CodexHooksPath, includeVersions);

    private ProviderIntegrationStatus Inspect(string provider, string displayName, string executable,
        string hooksPath, bool includeVersion)
    {
        var exe = _findExecutable(executable);
        if (exe is { Length: > 0 } && !File.Exists(exe)) exe = null;
        var version = includeVersion && exe is not null ? _probeVersion(exe) ?? "" : "";
        var expected = ProviderHookCatalog.ExpectedEvents(provider);
        var requiresTrust = provider == "codex";
        var instructions = provider == "codex"
            ? "Install: MiMi.AgentCli.exe install-codex. Then open /hooks in Codex, review and trust the MiMi entries, and start a new Codex session."
            : "Install: MiMi.AgentCli.exe install-claude. Start (or restart) a Claude Code session afterwards.";

        if (exe is null)
        {
            var leftover = File.Exists(hooksPath) ? " Hook configuration exists but the CLI is absent." : "";
            return new(provider, displayName, ProviderIntegrationState.NotInstalled,
                "Not installed", $"{displayName} CLI was not found on PATH.{leftover}",
                hooksPath, "", version, 0, expected.Length, requiresTrust, instructions);
        }

        if (!File.Exists(hooksPath))
        {
            return new(provider, displayName, ProviderIntegrationState.HooksMissing,
                "Hooks not installed", $"Found {exe}. No MiMi hook file exists.",
                hooksPath, exe, version, 0, expected.Length, requiresTrust, instructions);
        }

        HookScan scan;
        try
        {
            scan = ScanHooks(hooksPath, provider);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new(provider, displayName, ProviderIntegrationState.NeedsAttention,
                "Needs attention", $"Hook file could not be read as JSON: {ex.Message}",
                hooksPath, exe, version, 0, expected.Length, requiresTrust,
                "Fix the hook file or run MiMi.AgentCli.exe uninstall-"
                + (provider == "codex" ? "codex" : "claude") + " to remove only MiMi entries.");
        }

        var installed = expected.Count(scan.Events.Contains);
        var missing = expected.Where(e => !scan.Events.Contains(e)).ToArray();
        var stale = scan.ExecutablePaths.FirstOrDefault(p => !File.Exists(p));

        if (stale is not null)
        {
            return new(provider, displayName, ProviderIntegrationState.NeedsAttention,
                "Needs attention", $"A MiMi hook points to a missing executable: {stale}",
                hooksPath, exe, version, installed, expected.Length, requiresTrust,
                "Re-run the installer from the current MiMi.AgentCli.exe so hook paths are refreshed.");
        }

        if (installed == 0)
        {
            return new(provider, displayName, ProviderIntegrationState.HooksMissing,
                "Hooks not installed", $"Found {exe}; the hook file contains no MiMi entries.",
                hooksPath, exe, version, 0, expected.Length, requiresTrust, instructions);
        }

        if (missing.Length > 0)
        {
            return new(provider, displayName, ProviderIntegrationState.Incomplete,
                "Hooks incomplete", $"{installed} of {expected.Length} hooks present. Missing: {string.Join(", ", missing)}.",
                hooksPath, exe, version, installed, expected.Length, requiresTrust, instructions);
        }

        if (requiresTrust)
        {
            return new(provider, displayName, ProviderIntegrationState.TrustNotVerified,
                "Installed — trust not verified", "All hooks are present. Codex hides its hook-trust state; confirm in /hooks.",
                hooksPath, exe, version, installed, expected.Length, requiresTrust, instructions);
        }

        return new(provider, displayName, ProviderIntegrationState.Ready,
            "Installed", "All expected MiMi hooks are present.",
            hooksPath, exe, version, installed, expected.Length, requiresTrust, instructions);
    }

    private static HookScan ScanHooks(string path, string provider)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
            MaxDepth = 16,
        });

        var events = new HashSet<string>(StringComparer.Ordinal);
        var executables = new List<string>();
        var marker = provider == "codex" ? "codex" : "claude-code";

        if (!doc.RootElement.TryGetProperty("hooks", out var hooks) || hooks.ValueKind != JsonValueKind.Object)
            return new HookScan(events, executables);

        foreach (var eventProperty in hooks.EnumerateObject())
        {
            if (eventProperty.Value.ValueKind != JsonValueKind.Array) continue;
            foreach (var group in eventProperty.Value.EnumerateArray())
            {
                if (group.ValueKind != JsonValueKind.Object) continue;
                if (!group.TryGetProperty("hooks", out var inner) || inner.ValueKind != JsonValueKind.Array) continue;
                foreach (var hook in inner.EnumerateArray())
                {
                    if (hook.ValueKind != JsonValueKind.Object) continue;
                    if (hook.TryGetProperty("type", out var type) && type.GetString() != "command") continue;
                    if (!hook.TryGetProperty("command", out var commandElement)) continue;
                    var command = commandElement.GetString() ?? "";
                    if (!IsMiMiCommand(command, marker)) continue;
                    events.Add(eventProperty.Name);
                    if (ExecutableFromCommand(command) is { Length: > 0 } executablePath)
                        executables.Add(executablePath);
                }
            }
        }

        return new HookScan(events, executables);
    }

    /// <summary>True only for MiMi's signed command for this provider. Each provider marker
    /// is checked so a Claude hook never counts as Codex integration (or vice versa).</summary>
    public static bool IsMiMiCommand(string command, string providerMarker) =>
        command.Contains("MiMi.AgentCli", StringComparison.OrdinalIgnoreCase)
        && (command.Contains($"{providerMarker}-permission", StringComparison.OrdinalIgnoreCase)
            || command.Contains($"activity --provider {providerMarker}", StringComparison.OrdinalIgnoreCase));

    /// <summary>Extracts the executable from a hook command, handling quoted paths.</summary>
    public static string? ExecutableFromCommand(string command)
    {
        command = command.Trim();
        if (command.Length == 0) return null;
        if (command[0] == '"')
        {
            var end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }
        var exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe >= 0) return command[..(exe + 4)];
        var space = command.IndexOf(' ');
        return space > 0 ? command[..space] : command;
    }

    public static string? FindOnPath(string executable)
    {
        var names = OperatingSystem.IsWindows()
            ? new[] { executable + ".exe", executable + ".cmd", executable + ".bat", executable }
            : new[] { executable };
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var name in names)
            {
                try
                {
                    var candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { /* malformed PATH entry */ }
            }
        }
        return null;
    }

    private static string? ProbeVersion(string executable)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var process = Process.Start(info);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd().Trim();
            if (!process.WaitForExit(1800))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            return output.Length > 0 ? output.Split('\n')[0].Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private sealed record HookScan(HashSet<string> Events, List<string> ExecutablePaths);
}
