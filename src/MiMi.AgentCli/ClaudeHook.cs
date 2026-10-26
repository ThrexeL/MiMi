using System.IO;
using System.Text.Json;
using MiMi.Agents;

namespace MiMi.AgentCli;

/// <summary>
/// Maps a Claude Code PermissionRequest hook invocation onto a MiMi approval, and maps
/// MiMi's decision back onto Claude Code's PermissionRequest output schema.
///
/// Output contract (from Claude Code's own permission-request.command.output schema):
///   { "hookSpecificOutput": { "hookEventName": "PermissionRequest",
///     "decision": { "behavior": "allow"|"deny", "message": "..." } } }
/// Fields "updatedInput", "updatedPermissions" and "interrupt" FAIL CLOSED in Claude Code
/// and are therefore never emitted here. On any failure path (timeout, MiMi offline,
/// expired, cancelled) we DENY — a hook must never fail open.
/// </summary>
public static class ClaudeHook
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 12,
    };

    public sealed record ParsedRequest(
        AgentEvent Event, string SessionId, string ToolName, string DecisionSummary);

    /// <summary>Parses a provider hook's stdin JSON into a normalized approval event.</summary>
    public static ParsedRequest ParseStdin(string stdinJson, string provider = "claude-code")
    {
        using var doc = JsonDocument.Parse(stdinJson, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            MaxDepth = 12,
        });
        var root = doc.RootElement;

        var eventName = GetString(root, "hook_event_name");
        if (!string.Equals(eventName, "PermissionRequest", StringComparison.Ordinal))
            throw new InvalidDataException($"unsupported hook event '{eventName ?? "(null)"}' — this command handles PermissionRequest only");

        var toolName = GetString(root, "tool_name") ?? "unknown-tool";
        var sessionId = GetString(root, "session_id") ?? "";
        var cwd = GetString(root, "cwd") ?? "";
        var project = string.IsNullOrEmpty(cwd) ? "" : Path.GetFileName(cwd.TrimEnd(Path.DirectorySeparatorChar));

        var (actionKind, command) = DescribeToolCall(toolName, root.TryGetProperty("tool_input", out var ti) ? ti : default);

        var ev = AgentEventFactory.Create(
            provider: provider,
            sessionId: sessionId,
            type: AgentEventTypes.ApprovalRequested,
            title: "Approval Required",
            message: $"Claude Code wants to use {toolName}.",
            project: project,
            approvalRequestId: "cc-" + Guid.NewGuid().ToString("N"),
            metadata: new Dictionary<string, string>
            {
                ["action_kind"] = actionKind,
                ["command"] = command,
                ["tool"] = toolName,
            });

        return new ParsedRequest(ev, sessionId, toolName, command);
    }

    /// <summary>Human-meaningful action + command per known tool; JSON excerpt otherwise.</summary>
    private static (string ActionKind, string Command) DescribeToolCall(string toolName, JsonElement toolInput)
    {
        string? Get(string key) =>
            toolInput.ValueKind == JsonValueKind.Object
            && toolInput.TryGetProperty(key, out var v)
            && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;

        return toolName switch
        {
            "Bash" or "BashOutput" => ("Execute Command", Get("command") ?? "(no command shown)"),
            "apply_patch" => ("Apply Patch", Get("command") ?? "(no patch shown)"),
            "Edit" or "Write" or "MultiEdit" or "NotebookEdit" => ("Edit File", Get("file_path") ?? "(no path shown)"),
            "Read" => ("Read File", Get("file_path") ?? "(no path shown)"),
            "WebFetch" => ("Fetch URL", Get("url") ?? "(no url shown)"),
            "WebSearch" => ("Web Search", Get("query") ?? "(no query shown)"),
            "Task" or "Agent" => ("Launch Agent", Get("description") ?? Get("prompt") ?? "(no description)"),
            _ => ($"Use {toolName}", Excerpt(toolInput)),
        };
    }

    private static string Excerpt(JsonElement toolInput)
    {
        if (toolInput.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return "(no input)";
        try
        {
            var json = JsonSerializer.Serialize(toolInput);
            return json.Length <= 180 ? json : json[..177] + "...";
        }
        catch { return "(complex input)"; }
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Prints the Claude Code PermissionRequest decision for an allow.</summary>
    public static void WriteDecision(TextWriter stdout, ApprovalDecision decision, string reason)
    {
        var behavior = decision == ApprovalDecision.Approved ? "allow" : "deny";
        var payload = new
        {
            hookSpecificOutput = new
            {
                hookEventName = "PermissionRequest",
                decision = new
                {
                    behavior,
                    message = reason,
                },
            },
        };
        stdout.WriteLine(JsonSerializer.Serialize(payload));
    }
}
