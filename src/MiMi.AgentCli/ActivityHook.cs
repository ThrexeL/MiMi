using System.Text.Json;
using MiMi.Agents;

namespace MiMi.AgentCli;

/// <summary>
/// Maps non-approval lifecycle hook payloads (Claude Code and Codex share the same
/// schema family) onto normalized activity events. Activity hooks must be fast and
/// fail-safe: they never block the agent and never require user interaction.
///
/// Honesty rules: hooks report hook moments only. UserPromptSubmit means "received a
/// task" (shown as Working), Stop means "turn finished" (not session ended), and
/// silence means nothing — unknown status is displayed as unknown.
/// </summary>
public static class ActivityHook
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        MaxDepth = 12,
    };

    /// <summary>Lifecycle hook events MiMi accepts, per provider (research-verified).</summary>
    public static readonly IReadOnlySet<string> SupportedClaudeEvents =
        new HashSet<string>(ProviderHookCatalog.ClaudeActivityEvents, StringComparer.Ordinal);

    public static readonly IReadOnlySet<string> SupportedCodexEvents =
        new HashSet<string>(ProviderHookCatalog.CodexActivityEvents, StringComparer.Ordinal);

    public static bool IsSupported(string hookEventName, string provider) =>
        provider == "codex"
            ? SupportedCodexEvents.Contains(hookEventName)
            : SupportedClaudeEvents.Contains(hookEventName);

    /// <summary>Parses a lifecycle hook payload into a normalized activity event.
    /// Returns null for events MiMi does not track (caller exits 0, silently).</summary>
    public static AgentEvent? Parse(string stdinJson, string provider)
    {
        using var doc = JsonDocument.Parse(stdinJson, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            MaxDepth = 12,
        });
        var root = doc.RootElement;

        var eventName = GetString(root, "hook_event_name") ?? "";
        if (!IsSupported(eventName, provider)) return null;

        var sessionId = GetString(root, "session_id") ?? "";
        var cwd = GetString(root, "cwd") ?? "";
        var project = string.IsNullOrEmpty(cwd) ? "" : Path.GetFileName(cwd.TrimEnd(Path.DirectorySeparatorChar));
        var toolName = GetString(root, "tool_name") ?? "";
        var turnId = GetString(root, "turn_id") ?? "";
        var sequence = GetString(root, "sequence");

        var (type, summary) = eventName switch
        {
            "SessionStart" => (AgentEventTypes.Started, "Session started"),
            "UserPromptSubmit" => (AgentEventTypes.PromptSubmitted, Excerpt(FirstString(root, "prompt", "user_prompt", "input") ?? "New task received")),
            "PreToolUse" => (AgentEventTypes.ToolStarted, DescribeTool(toolName, root)),
            "PostToolUse" => (AgentEventTypes.ToolFinished, $"Finished {toolName}"),
            "PostToolUseFailure" => (AgentEventTypes.ToolFailed, $"{toolName} failed"),
            "Stop" => (AgentEventTypes.TurnFinished, "Turn finished"),
            "Interrupt" => (AgentEventTypes.Interrupted, "Turn interrupted"),
            "SessionEnd" => (AgentEventTypes.SessionEnded, "Session ended"),
            "SubagentStart" => (AgentEventTypes.SubagentStarted, $"Subagent started{SubSuffix(root)}"),
            "SubagentStop" => (AgentEventTypes.SubagentStopped, "Subagent finished"),
            _ => (AgentEventTypes.Status, "Agent activity"),
        };

        var metadata = new Dictionary<string, string>();
        if (toolName.Length > 0) metadata["tool"] = toolName;
        if (turnId.Length > 0) metadata["turn_id"] = turnId;
        if (sequence is { Length: > 0 }) metadata["sequence"] = sequence;
        if (eventName.Length > 0) metadata["hook_event"] = eventName;

        return AgentEventFactory.Create(provider, sessionId, type,
            title: DefaultTitle(eventName),
            message: summary,
            project: project,
            metadata: metadata);
    }

    private static string DefaultTitle(string hookEvent) => hookEvent switch
    {
        "SessionStart" => "Session started",
        "UserPromptSubmit" => "Working",
        "PreToolUse" => "Executing tool",
        "PostToolUse" => "Tool finished",
        "PostToolUseFailure" => "Tool failed",
        "Stop" => "Turn finished",
        "Interrupt" => "Interrupted",
        "SessionEnd" => "Session ended",
        "SubagentStart" => "Subagent started",
        "SubagentStop" => "Subagent finished",
        _ => "Agent activity",
    };

    private static string DescribeTool(string toolName, JsonElement root)
    {
        if (!root.TryGetProperty("tool_input", out var ti) || ti.ValueKind != JsonValueKind.Object)
            return $"Running {toolName}";

        string? Get(string key) =>
            ti.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        var detail = toolName switch
        {
            "Bash" => Get("command"),
            "Edit" or "Write" or "MultiEdit" or "NotebookEdit" => Get("file_path") is { } p ? $"editing {p}" : null,
            "WebFetch" => Get("url"),
            "WebSearch" => Get("query"),
            "apply_patch" => "applying patch",
            _ => null,
        };
        var baseText = $"Running {toolName}";
        return detail is null ? baseText : $"{baseText}: {Excerpt(detail)}";
    }

    private static string SubSuffix(JsonElement root) =>
        GetString(root, "agent_type") is { } t ? $" ({t})" : "";

    private static string Excerpt(string s)
    {
        s = s.ReplaceLineEndings(" ");
        return s.Length <= 120 ? s : s[..117] + "...";
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? FirstString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var v))
            {
                if (v.ValueKind == JsonValueKind.String) return v.GetString();
                if (v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0 && v[0].ValueKind == JsonValueKind.String)
                    return v[0].GetString();
            }
        }
        return null;
    }
}
