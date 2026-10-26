using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MiMi.AgentCli;

/// <summary>
/// Installs/uninstalls provider hook configuration with strict preservation rules:
///  - a timestamped backup is written before every modification;
///  - existing hook entries are never rewritten — MiMi's entry is added only when an
///    equivalent one (same command) is not already present;
///  - uninstall removes only entries whose commands carry MiMi's signature.
/// Claude Code (~/.claude/settings.json) and Codex (~/.codex/hooks.json) share the same
/// hooks schema family, so one generic JSON installer serves both.
/// </summary>
public static class Installers
{
    public static string ClaudeSettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    public static string CodexHooksPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "hooks.json");

    public static string CodexConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");

    private static string ExePath() => Environment.ProcessPath ?? "";

    public static string ClaudeHookCommand() => $"{ExePath()} claude-permission";
    public static string CodexHookCommand() => $"{ExePath()} codex-permission";

    // Tolerant ownership checks: match MiMi's command signature even if the executable
    // has moved, but never touch entries without it.
    private static bool IsMiMiClaudeHook(string? command) =>
        command is not null
        && command.Contains("MiMi.AgentCli", StringComparison.OrdinalIgnoreCase)
        && command.Contains("claude-permission", StringComparison.OrdinalIgnoreCase);

    private static bool IsMiMiCodexHook(string? command) =>
        command is not null
        && command.Contains("MiMi.AgentCli", StringComparison.OrdinalIgnoreCase)
        && command.Contains("codex-permission", StringComparison.OrdinalIgnoreCase);

    public static string InstallClaude(int timeoutSeconds, string? settingsPath = null)
        => InstallJsonHook(settingsPath ?? ClaudeSettingsPath, "PermissionRequest",
            ClaudeHookCommand(), timeoutSeconds, IsMiMiClaudeHook);

    public static string UninstallClaude(string? settingsPath = null)
        => UninstallJsonHook(settingsPath ?? ClaudeSettingsPath, "PermissionRequest", IsMiMiClaudeHook);

    public static string InstallCodex(int timeoutSeconds, string? hooksPath = null)
        => InstallJsonHook(hooksPath ?? CodexHooksPath, "PermissionRequest",
            CodexHookCommand(), timeoutSeconds, IsMiMiCodexHook);

    public static string UninstallCodex(string? hooksPath = null)
        => UninstallJsonHook(hooksPath ?? CodexHooksPath, "PermissionRequest", IsMiMiCodexHook);

    // ---- generic JSON hooks installer ---------------------------------------------

    private static string Backup(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Ticks suffix keeps rapid successive backups from colliding on the same filename.
        var backup = path + $".mimi-backup-{DateTime.Now:yyyyMMdd-HHmmss-fffffff}";
        if (File.Exists(path)) File.Copy(path, backup);
        return backup;
    }

    private static string InstallJsonHook(string path, string eventName, string command, int timeoutSeconds,
        Func<string?, bool> isOurs)
    {
        var backup = Backup(path);

        JsonObject root;
        if (File.Exists(path))
        {
            root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new InvalidDataException($"{path} is not a JSON object — leaving it untouched.");
        }
        else
        {
            root = new JsonObject();
        }

        if (root["hooks"] is not JsonObject hooks)
        {
            // "hooks" exists but is not an object (or missing): only replace when absent.
            if (root.ContainsKey("hooks"))
                return $"SKIP: {path} has a non-object 'hooks' key; refusing to modify it. See backup {backup}";
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }

        if (hooks[eventName] is not JsonArray entries)
        {
            if (hooks.ContainsKey(eventName))
                return $"SKIP: 'hooks.{eventName}' is not an array in {path}. See backup {backup}";
            entries = new JsonArray();
            hooks[eventName] = entries;
        }

        // Skip when an equivalent entry already exists (idempotent install).
        foreach (var entry in entries.OfType<JsonObject>())
        {
            if (entry["hooks"] is not JsonArray inner) continue;
            foreach (var h in inner.OfType<JsonObject>())
            {
                if (h["type"]?.ToString() == "command" &&
                    string.Equals(h["command"]?.ToString(), command, StringComparison.OrdinalIgnoreCase))
                    return $"ALREADY INSTALLED: {path} already contains the MiMi {eventName} hook.";
            }
        }

        var hookEntry = new JsonObject
        {
            ["type"] = "command",
            ["command"] = command,
            ["timeout"] = timeoutSeconds,
        };
        // No matcher key = applies to ALL tools (documented behavior for both providers).
        entries.Add(new JsonObject { ["hooks"] = new JsonArray { hookEntry } });

        WriteJson(path, root);
        return $"INSTALLED: {eventName} hook added to {path} (timeout {timeoutSeconds}s). Backup: {backup}";
    }

    private static string UninstallJsonHook(string path, string eventName, Func<string?, bool> isOurs)
    {
        if (!File.Exists(path)) return "NOTHING TO DO: no hook configuration exists.";
        var backup = Backup(path);

        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        if (root?["hooks"] is not JsonObject hooks || hooks[eventName] is not JsonArray entries)
            return $"NOTHING TO DO: no {eventName} hooks configured.";

        var before = entries.Count;
        var kept = new JsonArray();
        foreach (var entry in entries.OfType<JsonObject>())
        {
            var inner = entry["hooks"] as JsonArray;
            // Drop the group only when every command inside is MiMi's.
            var mine = inner?.OfType<JsonObject>()
                .Where(h => h["type"]?.ToString() == "command")
                .ToList() ?? [];
            var allMine = mine.Count > 0 && mine.All(h => isOurs(h["command"]?.ToString()));
            if (!allMine) kept.Add(entry.DeepClone());
        }
        if (kept.Count == before) return $"NOTHING TO DO: no MiMi {eventName} hook found.";

        if (kept.Count == 0) hooks.Remove(eventName);
        else hooks[eventName] = kept;

        WriteJson(path, root);
        return $"REMOVED: MiMi {eventName} hook from {path}. Backup: {backup}";
    }

    private static void WriteJson(string path, JsonNode root)
    {
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
