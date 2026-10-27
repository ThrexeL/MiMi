using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using MiMi.Agents;

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

    // Always quote: a MiMi install directory may contain spaces.
    private static string Quote(string path) => path.Length > 0 ? $"\"{path}\"" : "";

    public static string ClaudeHookCommand() => $"{Quote(ExePath())} claude-permission";
    public static string CodexHookCommand() => $"{Quote(ExePath())} codex-permission";
    // Tolerant ownership checks: match MiMi's command signature even if the executable
    // has moved, but never touch entries without it.
    private static bool IsMiMiClaudeHook(string? command) =>
        command is not null
        && command.Contains("MiMi.AgentCli", StringComparison.OrdinalIgnoreCase)
        && (command.Contains("claude-permission", StringComparison.OrdinalIgnoreCase)
            || command.Contains("activity --provider claude-code", StringComparison.OrdinalIgnoreCase));

    private static bool IsMiMiCodexHook(string? command) =>
        command is not null
        && command.Contains("MiMi.AgentCli", StringComparison.OrdinalIgnoreCase)
        && (command.Contains("codex-permission", StringComparison.OrdinalIgnoreCase)
            || command.Contains("activity --provider codex", StringComparison.OrdinalIgnoreCase));

    // Lifecycle hooks installed alongside PermissionRequest (research-verified event
    // names per provider). Activity hooks are fire-and-forget; no timeout pressure.
    private static readonly string[] ClaudeActivityEvents = ProviderHookCatalog.ClaudeActivityEvents;

    private static readonly string[] CodexActivityEvents = ProviderHookCatalog.CodexActivityEvents;

    public static string ClaudeActivityCommand() => $"{Quote(ExePath())} activity --provider claude-code";
    public static string CodexActivityCommand() => $"{Quote(ExePath())} activity --provider codex";

    public static string InstallClaude(int timeoutSeconds, string? settingsPath = null, string? executablePath = null)
    {
        var exe = executablePath ?? ExePath();
        if (exe is not { Length: > 0 } || !File.Exists(exe))
            return "REFUSED: MiMi.AgentCli executable path is unavailable; hooks were not modified.";
        var path = settingsPath ?? ClaudeSettingsPath;
        var permission = $"{Quote(exe)} claude-permission";
        var activity = $"{Quote(exe)} activity --provider claude-code";
        var result = InstallJsonHook(path, "PermissionRequest", permission, timeoutSeconds, IsMiMiClaudeHook);
        var details = new List<string> { result };
        foreach (var eventName in ClaudeActivityEvents)
            details.Add(InstallJsonHook(path, eventName, activity, 0, IsMiMiClaudeHook));
        return string.Join(Environment.NewLine, details);
    }

    public static string UninstallClaude(string? settingsPath = null)
    {
        var path = settingsPath ?? ClaudeSettingsPath;
        var details = new List<string> { UninstallJsonHook(path, "PermissionRequest", IsMiMiClaudeHook) };
        foreach (var eventName in ClaudeActivityEvents)
            details.Add(UninstallJsonHook(path, eventName, IsMiMiClaudeHook));
        return string.Join(Environment.NewLine, details);
    }

    public static string InstallCodex(int timeoutSeconds, string? hooksPath = null, string? executablePath = null)
    {
        var exe = executablePath ?? ExePath();
        if (exe is not { Length: > 0 } || !File.Exists(exe))
            return "REFUSED: MiMi.AgentCli executable path is unavailable; hooks were not modified.";
        var path = hooksPath ?? CodexHooksPath;
        var permission = $"{Quote(exe)} codex-permission";
        var activity = $"{Quote(exe)} activity --provider codex";
        var result = InstallJsonHook(path, "PermissionRequest", permission, timeoutSeconds, IsMiMiCodexHook);
        var details = new List<string> { result };
        foreach (var eventName in CodexActivityEvents)
            details.Add(InstallJsonHook(path, eventName, activity, 0, IsMiMiCodexHook));
        return string.Join(Environment.NewLine, details);
    }

    public static string UninstallCodex(string? hooksPath = null)
    {
        var path = hooksPath ?? CodexHooksPath;
        var details = new List<string> { UninstallJsonHook(path, "PermissionRequest", IsMiMiCodexHook) };
        foreach (var eventName in CodexActivityEvents)
            details.Add(UninstallJsonHook(path, eventName, IsMiMiCodexHook));
        return string.Join(Environment.NewLine, details);
    }

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
                return $"SKIP: {path} has a non-object 'hooks' key; refusing to modify it.";
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }

        if (hooks[eventName] is not JsonArray entries)
        {
            if (hooks.ContainsKey(eventName))
                return $"SKIP: 'hooks.{eventName}' is not an array in {path}.";
            entries = new JsonArray();
            hooks[eventName] = entries;
        }

        var changed = false;
        var found = false;
        var upgraded = false;

        // Exact entry already present: keep it, but refresh MiMi-owned timeout metadata.
        // An entry with the same MiMi signature but a different path is an outdated
        // install (the CLI moved): rewrite its command in place instead of duplicating it.
        foreach (var entry in entries.OfType<JsonObject>())
        {
            if (entry["hooks"] is not JsonArray inner) continue;
            foreach (var h in inner.OfType<JsonObject>())
            {
                if (h["type"]?.ToString() != "command") continue;
                var existing = h["command"]?.ToString();
                if (string.Equals(existing, command, StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                }
                else if (isOurs(existing))
                {
                    h["command"] = command;
                    found = true;
                    upgraded = true;
                    changed = true;
                }
                else
                {
                    continue;
                }

                if (timeoutSeconds > 0)
                {
                    if (h["timeout"]?.GetValue<int>() != timeoutSeconds)
                    {
                        h["timeout"] = timeoutSeconds;
                        changed = true;
                    }
                }
                else if (h.ContainsKey("timeout"))
                {
                    // Activity hooks must never gate the provider on MiMi.
                    h.Remove("timeout");
                    changed = true;
                }
            }
        }

        // Collapse duplicate MiMi-only groups (for example, one group per moved path).
        // Mixed groups that contain any non-MiMi hook are left untouched.
        if (CollapseDuplicateMiMiGroups(entries, isOurs)) changed = true;

        if (!found)
        {
            var hookEntry = new JsonObject
            {
                ["type"] = "command",
                ["command"] = command,
            };
            // timeout 0 = activity hooks use the provider default (they must never gate the
            // agent on MiMi); the synchronous approval hook keeps its explicit timeout.
            if (timeoutSeconds > 0) hookEntry["timeout"] = timeoutSeconds;
            // No matcher key = applies to ALL tools (documented behavior for both providers).
            entries.Add(new JsonObject { ["hooks"] = new JsonArray { hookEntry } });
            changed = true;
        }

        if (!changed)
            return $"ALREADY INSTALLED: {path} already contains the current MiMi {eventName} hook.";

        var backup = Backup(path);
        WriteJson(path, root);
        return upgraded
            ? $"UPDATED: MiMi {eventName} hook path refreshed in {path}. Backup: {backup}"
            : $"INSTALLED: {eventName} hook added to {path} (timeout {timeoutSeconds}s). Backup: {backup}";
    }

    /// <summary>Removes extra MiMi-only hook groups, keeping the first. Returns true when
    /// at least one group was removed.</summary>
    private static bool CollapseDuplicateMiMiGroups(JsonArray entries, Func<string?, bool> isOurs)
    {
        var seen = false;
        var removed = false;
        for (var i = 0; i < entries.Count;)
        {
            if (entries[i] is not JsonObject entry || entry["hooks"] is not JsonArray inner)
            {
                i++;
                continue;
            }
            var commands = inner.OfType<JsonObject>()
                .Where(h => h["type"]?.ToString() == "command")
                .Select(h => h["command"]?.ToString())
                .ToList();
            var allMine = commands.Count > 0 && commands.All(isOurs);
            if (!allMine)
            {
                i++;
                continue;
            }
            if (!seen)
            {
                seen = true;
                i++;
                continue;
            }
            entries.RemoveAt(i);
            removed = true;
        }
        return removed;
    }

    private static string UninstallJsonHook(string path, string eventName, Func<string?, bool> isOurs)
    {
        if (!File.Exists(path)) return "NOTHING TO DO: no hook configuration exists.";

        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        if (root?["hooks"] is not JsonObject hooks || hooks[eventName] is not JsonArray entries)
            return $"NOTHING TO DO: no {eventName} hooks configured.";

        var kept = new JsonArray();
        var changed = false;
        foreach (var entry in entries)
        {
            if (entry is not JsonObject group || group["hooks"] is not JsonArray inner)
            {
                kept.Add(entry?.DeepClone());
                continue;
            }

            var remaining = new JsonArray();
            foreach (var hook in inner)
            {
                var isMine = hook is JsonObject h
                    && h["type"]?.ToString() == "command"
                    && isOurs(h["command"]?.ToString());
                if (isMine) { changed = true; continue; }
                remaining.Add(hook?.DeepClone());
            }

            if (remaining.Count == inner.Count)
            {
                kept.Add(group.DeepClone());
            }
            else if (remaining.Count > 0)
            {
                // Mixed group: drop only MiMi's hook, preserve the user's hooks in place.
                var replacement = (JsonObject)group.DeepClone();
                replacement["hooks"] = remaining;
                kept.Add(replacement);
            }
        }

        if (!changed) return $"NOTHING TO DO: no MiMi {eventName} hook found.";

        var backup = Backup(path);
        if (kept.Count == 0) hooks.Remove(eventName);
        else hooks[eventName] = kept;

        WriteJson(path, root);
        return $"REMOVED: MiMi {eventName} hook from {path}. Backup: {backup}";
    }

    private static void WriteJson(string path, JsonNode root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true);
    }
}
