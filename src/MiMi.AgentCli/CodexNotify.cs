using System.Text.Json;
using MiMi.Agents;

namespace MiMi.AgentCli;

/// <summary>
/// Codex CLI notification adapter (notification-only mode).
///
/// Codex's documented integration surface for external programs is the `notify` config
/// entry: Codex invokes the configured command with a JSON document as the last argv
/// element (e.g. agent-turn-complete). This is ONE-WAY: Codex does not route interactive
/// approval requests through it, so this adapter never emits approval events and never
/// shows Approve/Deny in MiMi — only activity notifications. See docs/codex-integrations.md
/// for the assessment of Codex app-server approvals (future work).
/// </summary>
public static class CodexNotify
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        MaxDepth = 12,
    };

    /// <summary>Maps a notify JSON payload onto a normalized (non-approval) event. Null when uninteresting.</summary>
    public static AgentEvent? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            MaxDepth = 12,
        });
        var root = doc.RootElement;

        var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() ?? ""
            : "";

        var sessionId = root.TryGetProperty("turn-id", out var tid) && tid.ValueKind == JsonValueKind.String
            ? tid.GetString() ?? ""
            : root.TryGetProperty("thread-id", out var thid) && thid.ValueKind == JsonValueKind.String
                ? thid.GetString() ?? ""
                : "";

        var message = FirstString(root, "last-assistant-message", "last-assistant-message-content") ?? "";
        var cwd = FirstString(root, "cwd") ?? "";
        var project = string.IsNullOrEmpty(cwd) ? "" : Path.GetFileName(cwd.TrimEnd(Path.DirectorySeparatorChar));

        // Redact: notify payloads may contain transcript text; keep only a short excerpt.
        if (message.Length > 200) message = message[..197] + "...";

        return type switch
        {
            "agent-turn-complete" => AgentEventFactory.Create("codex", sessionId,
                AgentEventTypes.Completed,
                title: "Codex turn complete",
                message: message,
                project: project,
                metadata: new Dictionary<string, string> { ["source"] = "codex notify" }),
            _ => AgentEventFactory.Create("codex", sessionId,
                AgentEventTypes.Status,
                title: $"Codex: {type}",
                message: message,
                project: project,
                metadata: new Dictionary<string, string> { ["source"] = "codex notify", ["notify_type"] = type }),
        };
    }

    private static string? FirstString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
            if (root.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array
                && arr.GetArrayLength() > 0 && arr[0].ValueKind == JsonValueKind.String)
                return arr[0].GetString();
        }
        return null;
    }
}
