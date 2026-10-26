using System.Text.Json;
using System.Text.Json.Serialization;
using MiMi.Core;

namespace MiMi.Agents;

/// <summary>Normalized agent lifecycle event types (spec §6).</summary>
public static class AgentEventTypes
{
    // Phase 1/2 contract (unchanged)
    public const string Started = "agent.started";
    public const string Status = "agent.status";
    public const string ToolStarted = "agent.tool_started";
    public const string ApprovalRequested = "agent.approval_requested";
    public const string ApprovalResolved = "agent.approval_resolved";
    public const string Completed = "agent.completed";
    public const string Failed = "agent.failed";
    public const string Cancelled = "agent.cancelled";

    // Phase 3.6 lifecycle events (additive; existing consumers ignore unknown types)
    public const string PromptSubmitted = "agent.prompt_submitted";
    public const string ToolFinished = "agent.tool_finished";
    public const string ToolFailed = "agent.tool_failed";
    public const string TurnFinished = "agent.turn_finished";
    public const string Interrupted = "agent.interrupted";
    public const string SessionEnded = "agent.session_ended";
    public const string SubagentStarted = "agent.subagent_started";
    public const string SubagentStopped = "agent.subagent_stopped";

    public static readonly IReadOnlySet<string> All = new HashSet<string>
    {
        Started, Status, ToolStarted, ApprovalRequested, ApprovalResolved, Completed, Failed, Cancelled,
        PromptSubmitted, ToolFinished, ToolFailed, TurnFinished, Interrupted, SessionEnded,
        SubagentStarted, SubagentStopped,
    };
}

public enum AgentPriority { Critical = 0, High = 1, Normal = 2, Low = 3 }

public enum ApprovalDecision { Approved, Denied, Expired, Dismissed }

/// <summary>
/// Provider-independent normalized agent event. Every provider adapter (Codex, Claude Code,
/// custom harnesses) projects into this shape; UI components never see provider specifics.
/// </summary>
public sealed record AgentEvent
{
    [JsonPropertyName("event_id")] public string EventId { get; init; } = "";
    [JsonPropertyName("provider")] public string Provider { get; init; } = "";
    [JsonPropertyName("session_id")] public string SessionId { get; init; } = "";
    [JsonPropertyName("project")] public string Project { get; init; } = "";
    [JsonPropertyName("type")] public string Type { get; init; } = "";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("message")] public string Message { get; init; } = "";
    [JsonPropertyName("timestamp")] public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    // String-or-number enum so external providers can send "Critical" or 0.
    [JsonPropertyName("priority")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AgentPriority Priority { get; init; } = AgentPriority.Normal;
    [JsonPropertyName("approval_request_id")] public string? ApprovalRequestId { get; init; }
    [JsonPropertyName("metadata")] public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>();

    /// <summary>
    /// Fills defaults, clamps field sizes, and validates structure.
    /// Returns null with a reason when the event cannot be accepted.
    /// This is the security boundary for anything arriving from the bridge: payloads are
    /// display-only and no field is ever executed.
    /// </summary>
    public static AgentEvent? Normalize(AgentEvent raw, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(raw.Type) || !AgentEventTypes.All.Contains(raw.Type))
        {
            error = $"unknown or missing event type '{raw.Type}'";
            return null;
        }
        if (string.IsNullOrWhiteSpace(raw.Provider))
        {
            error = "missing provider";
            return null;
        }

        var provider = Sanitize(raw.Provider, 40);
        if (provider is null) { error = "provider contains control characters"; return null; }

        var title = Sanitize(raw.Title, 120) ?? "";
        var message = Sanitize(raw.Message, 500) ?? "";
        var project = Sanitize(raw.Project, 80) ?? "";
        var sessionId = Sanitize(raw.SessionId, 80) ?? "";
        var eventId = Sanitize(raw.EventId, 80) ?? Guid.NewGuid().ToString("N");

        // Trust our own clock for ordering/sweeps; client timestamps are advisory only.
        var timestamp = raw.TimestampUtc is { } ts && ts <= DateTimeOffset.UtcNow
            ? ts : DateTimeOffset.UtcNow;

        var metadata = new Dictionary<string, string>();
        if (raw.Metadata is not null)
        {
            foreach (var (k, v) in raw.Metadata.Take(16))
            {
                var key = Sanitize(k, 40);
                var val = Sanitize(v, 200);
                if (key is not null && val is not null) metadata[key] = val;
            }
        }

        // Approval events must carry an approval id so responses can be correlated.
        string? approvalId = raw.ApprovalRequestId is { Length: > 0 } a ? Sanitize(a, 80) : null;
        if (raw.Type is AgentEventTypes.ApprovalRequested or AgentEventTypes.ApprovalResolved && approvalId is null)
        {
            error = $"{raw.Type} requires approval_request_id";
            return null;
        }

        return new AgentEvent
        {
            EventId = eventId,
            Provider = provider,
            SessionId = sessionId,
            Project = project,
            Type = raw.Type,
            Title = string.IsNullOrWhiteSpace(title) ? DefaultTitle(raw.Type) : title,
            Message = message,
            TimestampUtc = timestamp,
            Priority = raw.Priority,
            ApprovalRequestId = approvalId,
            Metadata = metadata,
        };
    }

    private static string DefaultTitle(string type) => type switch
    {
        AgentEventTypes.Started => "Agent started",
        AgentEventTypes.Status => "Agent status",
        AgentEventTypes.ToolStarted => "Running tool",
        AgentEventTypes.ApprovalRequested => "Approval required",
        AgentEventTypes.ApprovalResolved => "Approval resolved",
        AgentEventTypes.Completed => "Agent completed",
        AgentEventTypes.Failed => "Agent failed",
        AgentEventTypes.Cancelled => "Agent cancelled",
        AgentEventTypes.PromptSubmitted => "New task",
        AgentEventTypes.ToolFinished => "Tool finished",
        AgentEventTypes.ToolFailed => "Tool failed",
        AgentEventTypes.TurnFinished => "Turn finished",
        AgentEventTypes.Interrupted => "Interrupted",
        AgentEventTypes.SessionEnded => "Session ended",
        AgentEventTypes.SubagentStarted => "Subagent started",
        AgentEventTypes.SubagentStopped => "Subagent stopped",
        _ => "Agent event",
    };

    /// <summary>Trims length and strips control characters (no newlines into single-line labels).</summary>
    private static string? Sanitize(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return s?.Length == 0 ? "" : null;
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (!char.IsControl(ch)) sb.Append(ch);
            if (sb.Length >= max) break;
        }
        return sb.ToString();
    }

    public static AgentEvent? TryParse(string json, out string? error)
    {
        error = null;
        try
        {
            var raw = JsonSerializer.Deserialize<AgentEvent>(json, JsonOpts);
            if (raw is null) { error = "empty payload"; return null; }
            return Normalize(raw, out error);
        }
        catch (JsonException ex)
        {
            error = $"invalid JSON: {ex.Message}";
            return null;
        }
    }

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 8,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    /// <summary>Maps an agent priority onto island display priority. Lifecycle events
    /// (Phase 3.6) are silent by default — they update the session indicator only.</summary>
    public IslandPriority ToIslandPriority() => Type switch
    {
        AgentEventTypes.ApprovalRequested => IslandPriority.Approval,
        AgentEventTypes.Failed or AgentEventTypes.ToolFailed => IslandPriority.AgentFailure,
        AgentEventTypes.Completed or AgentEventTypes.Cancelled or AgentEventTypes.TurnFinished
            or AgentEventTypes.SessionEnded => IslandPriority.AgentCompletion,
        _ => Priority == AgentPriority.Critical ? IslandPriority.CriticalSystem : IslandPriority.AgentCompletion,
    };
}

/// <summary>Convenience factory for providers/simulators.</summary>
public static class AgentEventFactory
{
    public static AgentEvent Create(
        string provider, string sessionId, string type,
        string? title = null, string? message = null, string? project = null,
        AgentPriority priority = AgentPriority.Normal, string? approvalRequestId = null,
        IReadOnlyDictionary<string, string>? metadata = null)
        => new()
        {
            EventId = Guid.NewGuid().ToString("N"),
            Provider = provider,
            SessionId = sessionId,
            Type = type,
            Title = title ?? "",
            Message = message ?? "",
            Project = project ?? "",
            Priority = priority,
            ApprovalRequestId = approvalRequestId,
            Metadata = metadata ?? new Dictionary<string, string>(),
        };
}
