using MiMi.Agents;
using System.Text.Json;

namespace MiMi.AgentCli;

/// <summary>Bounded, display-only telemetry for local harnesses such as Thursday.
/// No approvals or decisions: permission remains with the originating harness.</summary>
public static class ActivityRelay
{
    public static AgentEvent? Parse(string line)
    {
        AgentEvent? ev;
        try { ev = JsonSerializer.Deserialize<AgentEvent>(line); }
        catch (JsonException) { return null; }
        if (ev is null || ev.Type is AgentEventTypes.ApprovalRequested or AgentEventTypes.ApprovalResolved)
            return null;
        return AgentEvent.Normalize(ev, out _);
    }

    public static async Task<int> RunAsync(TextReader input)
    {
        while (await input.ReadLineAsync() is { } line)
        {
            try
            {
                var ev = Parse(line);
                if (ev is null) continue;
                // Reconnect for each update, including after MiMi restarts. Bound the
                // entire exchange (writes included), not just the connect and ack.
                await SendAsync(ev);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"MiMi: relay skipped ({ex.GetType().Name})");
            }
        }
        return 0;
    }

    private static async Task SendAsync(AgentEvent ev)
    {
        await using var bridge = await BridgeClient.ConnectAsync(TimeSpan.FromMilliseconds(300), "thursday-activity");
        await bridge.SendEventAsync(ev, TimeSpan.FromSeconds(1)).WaitAsync(TimeSpan.FromSeconds(1));
    }
}
