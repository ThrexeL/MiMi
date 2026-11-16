using System.Globalization;
using System.Text.Json;
using MiMi.Agents;

namespace MiMi.AgentCli;

/// <summary>
/// Status mirror for ImpressiveBot. ImpressiveBot runs its own bots and owns every
/// approval card; this command only lets MiMi *show* what is happening there.
///
/// Contract:
///  - input is one JSON object on stdin, describing one bot's current state;
///  - the provider key carries the bot's name ("impressivebot:Patch"), so the island
///    row says which teammate is working;
///  - MiMi is never asked to decide anything: no approval is created and no decision
///    is read back. Approvals stay in ImpressiveBot, where the card is authoritative.
///
/// By default failures exit 0 so a MiMi outage cannot disturb legacy callers.
/// Opt-in require-ack mode reports delivery failures so senders can retry.
/// </summary>
public static class ImpressiveBotRelay
{
    /// <summary>Longest bot name accepted; the provider field is capped at 40 chars by
    /// AgentEvent.Normalize, and the prefix costs 14.</summary>
    public const int MaxBotName = 24;

    private static readonly HashSet<string> AllowedStates = new(StringComparer.Ordinal)
    {
        "working", "waiting", "finished", "failed", "stopped",
    };

    /// <summary>One status update from ImpressiveBot, already validated.</summary>
    public sealed record StatusUpdate(
        string Provider,
        string SessionId,
        string Type,
        string Bot,
        string Message,
        string Project,
        IReadOnlyDictionary<string, string> Metadata,
        DateTimeOffset? TimestampUtc = null);

    /// <summary>
    /// Parses one status line. Returns null for anything malformed, unknown, or trying
    /// to smuggle an approval through this display-only channel.
    /// </summary>
    public static StatusUpdate? Parse(string json) => Parse(json, out _);

    public static StatusUpdate? Parse(string json, out string? rejection)
    {
        rejection = "invalid_payload";
        if (string.IsNullOrWhiteSpace(json) || json.Length > 32_000) return null;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return null; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            // ImpressiveBot always sends a bot name; a payload without one is malformed,
            // not an unnamed bot, so it is rejected rather than guessed at.
            var bot = Flatten(Str(root, "bot"), MaxBotName);
            if (bot.Length == 0)
            {
                rejection = "missing_bot";
                return null;
            }

            var state = Str(root, "state").ToLowerInvariant();
            if (!AllowedStates.Contains(state))
            {
                rejection = "invalid_state";
                return null;
            }

            var session = Flatten(Str(root, "session"), 80);
            if (session.Length == 0) session = bot;

            var message = Flatten(Str(root, "message"), 400);
            var project = Flatten(Str(root, "project"), 80);

            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["source"] = "impressivebot",
                ["bot"] = bot,
            };
            if (Str(root, "thread").Length > 0) metadata["thread"] = Flatten(Str(root, "thread"), 80);
            if (Str(root, "tool").Length > 0) metadata["tool"] = Flatten(Str(root, "tool"), 60);

            if (root.TryGetProperty("sequence", out var sequence))
            {
                if (sequence.ValueKind != JsonValueKind.Number || !sequence.TryGetInt64(out var value) || value < 0)
                {
                    rejection = "invalid_sequence";
                    return null;
                }
                metadata["sequence"] = value.ToString(CultureInfo.InvariantCulture);
            }

            DateTimeOffset? timestamp = null;
            if (root.TryGetProperty("timestamp", out var timestampValue))
            {
                if (timestampValue.ValueKind != JsonValueKind.String
                    || !(Str(root, "timestamp").EndsWith("Z", StringComparison.Ordinal)
                        || Str(root, "timestamp").EndsWith("+00:00", StringComparison.Ordinal))
                    || !timestampValue.TryGetDateTimeOffset(out var value) || value.Offset != TimeSpan.Zero)
                {
                    rejection = "invalid_timestamp";
                    return null;
                }
                timestamp = value;
                // Survives bridge normalization, which clamps future event timestamps.
                metadata["sender_timestamp"] = value.ToString("O", CultureInfo.InvariantCulture);
            }

            rejection = null;
            return new StatusUpdate(
                Provider: ProviderNames.ImpressiveBotProvider + ":" + bot,
                SessionId: session,
                Type: state switch
                {
                    "working" => AgentEventTypes.ToolStarted,
                    "waiting" => AgentEventTypes.WaitingForInput,
                    "finished" => AgentEventTypes.TurnFinished,
                    "failed" => AgentEventTypes.Failed,
                    _ => AgentEventTypes.Interrupted,
                },
                Bot: bot,
                Message: message,
                Project: project,
                Metadata: metadata,
                TimestampUtc: timestamp);
        }
    }

    /// <summary>Projects a status update onto the normalized event MiMi already understands.</summary>
    public static AgentEvent ToEvent(StatusUpdate update) =>
        AgentEventFactory.Create(
            update.Provider, update.SessionId, update.Type,
            title: update.Type switch
            {
                AgentEventTypes.ToolStarted => update.Bot + " is working",
                AgentEventTypes.WaitingForInput => update.Bot + " is waiting",
                AgentEventTypes.TurnFinished => update.Bot + " finished",
                AgentEventTypes.Failed => update.Bot + " hit an error",
                _ => update.Bot + " stopped",
            },
            message: update.Message,
            project: update.Project,
            metadata: update.Metadata) with { TimestampUtc = update.TimestampUtc ?? DateTimeOffset.UtcNow };

    public static async Task<int> RunAsync(TextReader input, Func<Task<BridgeClient>>? connect = null,
        bool requireAck = false, TextWriter? diagnostics = null)
    {
        var update = Parse(await input.ReadToEndAsync(), out var rejection);
        if (update is null)
        {
            (diagnostics ?? Console.Error).WriteLine($"MiMi: status rejected ({rejection})");
            return requireAck ? 1 : 0;
        }

        try
        {
            await using var bridge = await (connect?.Invoke()
                ?? BridgeClient.ConnectAsync(TimeSpan.FromMilliseconds(500), "impressivebot-status"));
            await bridge.SendEventAsync(ToEvent(update), TimeSpan.FromSeconds(1))
                .WaitAsync(TimeSpan.FromSeconds(2));
            return 0;
        }
        catch (Exception ex)
        {
            // Only opt-in callers use failure as a delivery/retry signal.
            (diagnostics ?? Console.Error).WriteLine($"MiMi: status not shown ({ex.GetType().Name})");
            return requireAck ? 1 : 0;
        }
    }

    private static string Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    /// <summary>Collapses whitespace and strips control characters so a name can never
    /// break a single-line label or the provider key.</summary>
    private static string Flatten(string text, int max)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        var space = false;
        foreach (var ch in text)
        {
            if (char.IsControl(ch) || char.IsWhiteSpace(ch)) { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; }
            if (sb.Length >= max) break;
            sb.Append(ch);
        }
        return sb.ToString().Trim();
    }
}