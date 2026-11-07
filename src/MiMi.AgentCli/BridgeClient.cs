using System.IO;
using System.IO.Pipes;
using System.Text;
using MiMi.Agents;
using MiMi.Bridge;

namespace MiMi.AgentCli;

/// <summary>
/// A single-connection bridge client used by hook adapters: connect → hello →
/// send one approval event → wait (bounded) for the matching approval_resolved frame.
/// Frames addressed to other approval ids are ignored, so concurrent agent sessions
/// sharing this pipe never cross-talk.
/// </summary>
public sealed class BridgeClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly string _clientLabel;
    private readonly Queue<BridgeProtocol> _earlyDecisions = new();

    private BridgeClient(NamedPipeClientStream pipe, string clientLabel)
    {
        _pipe = pipe;
        _clientLabel = clientLabel;
    }

    public static string TokenPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiMi", "bridge-token");

    public static string PipeName => BridgeProtocol.PipeNameForUser();

    /// <summary>Connects and performs the token handshake. Throws with a clear message when MiMi is not running.</summary>
    public static async Task<BridgeClient> ConnectAsync(TimeSpan connectTimeout, string clientLabel,
        string? pipeName = null, string? token = null)
    {
        token ??= ReadToken();
        var pipe = new NamedPipeClientStream(".", pipeName ?? PipeName, PipeDirection.InOut);
        try
        {
            await pipe.ConnectAsync((int)connectTimeout.TotalMilliseconds);
            await WriteFrameAsync(pipe, new BridgeProtocol { Kind = "hello", Token = token, Client = clientLabel });

            using var handshakeTimeout = new CancellationTokenSource(connectTimeout);
            var ack = BridgeProtocol.Deserialize(await ReadFrameAsync(pipe, handshakeTimeout.Token) ?? "");
            if (ack is not { Ok: true })
                throw new IOException($"MiMi bridge rejected the handshake: {ack?.Error ?? "no ack"}");
            return new BridgeClient(pipe, clientLabel);
        }
        catch (Exception ex)
        {
            await pipe.DisposeAsync();
            if (ex is IOException or TimeoutException) throw;
            throw new IOException(
                $"Could not reach the MiMi bridge pipe '{PipeName}' ({ex.Message}). Is MiMi running with the agent bridge enabled?");
        }
    }

    private static string ReadToken()
    {
        if (!File.Exists(TokenPath))
            throw new IOException($"Bridge token not found at '{TokenPath}'. Is MiMi running with the agent bridge enabled?");
        var token = File.ReadAllText(TokenPath).Trim();
        if (token.Length != 64) throw new IOException("Bridge token file is malformed; delete it and restart MiMi.");
        return token;
    }

    /// <summary>Sends the event and consumes its ack.</summary>
    public async Task SendEventAsync(AgentEvent ev, TimeSpan ackTimeout)
    {
        await WriteFrameAsync(_pipe, new BridgeProtocol { Kind = "event", Event = ev });
        using var timeout = new CancellationTokenSource(ackTimeout);
        BridgeProtocol? ack;
        while (true)
        {
            ack = BridgeProtocol.Deserialize(await ReadFrameAsync(_pipe, timeout.Token) ?? "");
            // A fast island decision may arrive before the event acknowledgement.
            // Preserve it for WaitDecisionAsync instead of losing the user's click.
            if (ack is { Kind: "approval_resolved" }) { _earlyDecisions.Enqueue(ack); continue; }
            break;
        }
        if (ack is not { Ok: true })
            throw new IOException(ack is { Error: { Length: > 0 } err } && err == "duplicate"
                ? "duplicate event (already delivered)"
                : $"MiMi rejected the event: {ack?.Error ?? "no ack"}");
    }

    /// <summary>
    /// Reads frames until the decision for <paramref name="approvalId"/> arrives.
    /// Returns null on timeout. Frames for other approval ids are skipped.
    /// </summary>
    public async Task<ApprovalDecision?> WaitDecisionAsync(string approvalId, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            while (true)
            {
                var frame = _earlyDecisions.Count > 0 ? _earlyDecisions.Dequeue()
                    : BridgeProtocol.Deserialize(await ReadFrameAsync(_pipe, cts.Token) ?? "");
                if (frame is null) throw new IOException("MiMi closed the connection before a decision arrived.");
                if (!string.Equals(frame.Kind, "approval_resolved", StringComparison.Ordinal)) continue;
                if (!string.Equals(frame.ApprovalRequestId, approvalId, StringComparison.Ordinal)) continue;
                return frame.Decision?.ToLowerInvariant() switch
                {
                    "approved" => ApprovalDecision.Approved,
                    "denied" => ApprovalDecision.Denied,
                    "expired" => ApprovalDecision.Expired,
                    _ => null,
                };
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // our wait timeout, not an external cancel
        }
    }

    private static async Task WriteFrameAsync(Stream stream, BridgeProtocol frame)
    {
        var payload = Encoding.UTF8.GetBytes(frame.Serialize());
        if (payload.Length > BridgeProtocol.MaxFrameBytes) throw new IOException("event payload too large");
        var header = BitConverter.GetBytes(payload.Length);
        await stream.WriteAsync(header);
        await stream.WriteAsync(payload);
        await stream.FlushAsync();
    }

    private static async Task<string?> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        for (var read = 0; read < 4;)
        {
            var n = await stream.ReadAsync(header.AsMemory(read, 4 - read), ct);
            if (n == 0) return null;
            read += n;
        }
        var length = BitConverter.ToInt32(header, 0);
        if (length is < 0 or > BridgeProtocol.MaxFrameBytes) throw new IOException($"frame too large: {length}");
        var payload = new byte[length];
        for (var read = 0; read < length;)
        {
            var n = await stream.ReadAsync(payload.AsMemory(read, length - read), ct);
            if (n == 0) throw new IOException("EOF inside frame body");
            read += n;
        }
        return Encoding.UTF8.GetString(payload);
    }

    public async ValueTask DisposeAsync()
    {
        try { await _pipe.DisposeAsync(); } catch { /* best effort */ }
    }
}
