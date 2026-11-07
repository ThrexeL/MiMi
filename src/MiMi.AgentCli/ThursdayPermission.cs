using System.Text.Json;
using MiMi.Agents;

namespace MiMi.AgentCli;

/// <summary>One exact Thursday action, kept connected until the island decision.</summary>
public static class ThursdayPermission
{
    public static AgentEvent? Parse(string json)
    {
        AgentEvent? raw;
        try { raw = JsonSerializer.Deserialize<AgentEvent>(json); }
        catch (JsonException) { return null; }
        if (raw?.Metadata is null || !raw.Metadata.TryGetValue("command", out var original) || original.Length > 12000)
            return null;
        var ev = AgentEvent.Normalize(raw, out _);
        return ev is { Provider: "thursday", Type: AgentEventTypes.ApprovalRequested }
            && ev.Metadata.TryGetValue("command", out var command) && command.Length <= 12000
            && command == original
            ? ev : null;
    }

    public static async Task<int> RunAsync(TextReader input, TextWriter output, Func<Task<BridgeClient>>? connect = null)
    {
        var ev = Parse(await input.ReadToEndAsync());
        if (ev is null) return 2;
        try
        {
            await using var bridge = await (connect?.Invoke()
                ?? BridgeClient.ConnectAsync(TimeSpan.FromSeconds(1), "thursday-permission"));
            await bridge.SendEventAsync(ev, TimeSpan.FromSeconds(2)).WaitAsync(TimeSpan.FromSeconds(2));
            var decision = await bridge.WaitDecisionAsync(ev.ApprovalRequestId!, TimeSpan.FromMinutes(10), default);
            await output.WriteLineAsync(JsonSerializer.Serialize(new {
                approval_id = ev.ApprovalRequestId,
                decision = decision switch {
                    ApprovalDecision.Approved => "approve_task",
                    ApprovalDecision.Denied => "deny",
                    _ => "unavailable"
                }
            }));
            return 0;
        }
        catch (Exception)
        {
            // No island decision is not a denial or a grant. Thursday's original
            // card stays available, and its sealed action remains unconsumed.
            return 1;
        }
    }
}
