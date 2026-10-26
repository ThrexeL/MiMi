using System.IO;
using System.IO.Pipes;
using MiMi.Agents;
using MiMi.Core;

namespace MiMi.Bridge;

/// <summary>
/// Secure local event receiver over a Windows named pipe.
///
/// Security model:
///  - bound to the per-user pipe namespace; ACL allows only the current user + SYSTEM;
///  - first frame must be "hello" carrying the shared token from %LOCALAPPDATA%\MiMi\bridge-token
///    (token file readable only by the same user);
///  - payloads are size-limited (64 KiB), JSON-validated, and strictly display-only;
///  - MiMi never executes anything that arrives here.
///
/// Two-way path: when a UI approval is decided for a request that arrived from a connected
/// client, an "approval_resolved" frame is pushed back over that client's pipe. Providers
/// that cannot receive responses simply ignore it. MiMi reports delivery only after the
/// frame is written — and never claims an agent acted on it.
/// </summary>
public sealed class NamedPipeAgentBridge : IAsyncDisposable
{
    private readonly AgentEventBus _bus;
    private readonly ApprovalStore _approvals;
    private readonly DedupStore _dedup = new();
    private readonly string _token;
    private readonly string _pipeName;
    private readonly int _maxConcurrentClients;
    private readonly SemaphoreSlim _clientSlots;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    // Connected clients that may receive approval responses (client label → stream).
    private readonly Dictionary<string, Stream> _responseTargets = new();
    private readonly object _targetGate = new();

    public bool IsRunning { get; private set; }
    public string PipeName => _pipeName;

    /// <summary>Raised on the pipeline thread for every accepted (deduped, validated) event.</summary>
    public event Action<AgentEvent>? EventAccepted;

    public NamedPipeAgentBridge(AgentEventBus bus, ApprovalStore approvals, string token,
        string? pipeName = null, int maxConcurrentClients = 8)
    {
        _bus = bus;
        _approvals = approvals;
        _token = token;
        _pipeName = pipeName ?? BridgeProtocol.PipeNameForUser();
        _maxConcurrentClients = maxConcurrentClients;
        _clientSlots = new SemaphoreSlim(maxConcurrentClients, maxConcurrentClients);

        // Forward UI decisions back to the origin client when one is attached.
        _approvals.Resolved += OnApprovalResolved;
    }

    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        IsRunning = true;
        Logger.Info($"Bridge: listening on pipe '{_pipeName}'.");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                if (!await _clientSlots.WaitAsync(1000, ct)) continue;
                server = System.IO.Pipes.NamedPipeServerStreamAcl.Create(
                    _pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, inBufferSize: 8192, outBufferSize: 8192,
                    pipeSecurity: BridgeProtocol.CreatePipeSecurity());
                await server.WaitForConnectionAsync(ct);
                // Capture into a local: the lambda must not observe the `server = null`
                // ownership transfer below (captured variables race with Task.Run).
                var connected = server;
                var _ = Task.Run(() => ServeClientAsync(connected, ct), ct);
                server = null; // ownership transferred
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Logger.Error("Bridge: accept loop error", ex);
                try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
            }
            finally
            {
                server?.Dispose();
            }
        }
    }

    private async Task ServeClientAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        var clientLabel = "unknown";
        try
        {
            // Plain await-using: server runs on a threadpool thread (no sync context), and
            // the ConfigureAwait(false) disposable variant NREs in DisposeAsync here.
            await using (server)
            {
                // 1. Handshake: hello + token, within 5 s.
                using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                handshakeCts.CancelAfter(TimeSpan.FromSeconds(5));
                var helloFrame = await BridgeProtocol.ReadFrameAsync(server, handshakeCts.Token);
                var hello = helloFrame is null ? null : BridgeProtocol.Deserialize(helloFrame);
                if (hello is null || !string.Equals(hello.Kind, "hello", StringComparison.Ordinal))
                {
                    await Reject(server, "expected hello frame", ct);
                    return;
                }
                if (!string.Equals(hello.Token, _token, StringComparison.Ordinal))
                {
                    Logger.Warn("Bridge: rejected client with invalid token.");
                    await Reject(server, "invalid token", ct);
                    return;
                }
                clientLabel = string.IsNullOrWhiteSpace(hello.Client) ? "client" : hello.Client!.Trim();
                Logger.Info($"Bridge: client '{clientLabel}' connected.");
                lock (_targetGate) _responseTargets[clientLabel] = server;
                try
                {
                    await BridgeProtocol.WriteFrameAsync(server, new BridgeProtocol { Kind = "ack", Ok = true }, ct);

                    // 2. Event frames until the client disconnects.
                    while (true)
                    {
                        var frame = await BridgeProtocol.ReadFrameAsync(server, ct);
                        if (frame is null) break; // clean disconnect
                        var msg = BridgeProtocol.Deserialize(frame);
                        if (msg is null || !string.Equals(msg.Kind, "event", StringComparison.Ordinal) || msg.Event is null)
                        {
                            await BridgeProtocol.WriteFrameAsync(server,
                                new BridgeProtocol { Kind = "ack", Ok = false, Error = "expected event frame" }, ct);
                            continue;
                        }

                        var normalized = AgentEvent.Normalize(msg.Event, out var error);
                        if (normalized is null)
                        {
                            Logger.Warn($"Bridge: rejected event from '{clientLabel}': {error}");
                            await BridgeProtocol.WriteFrameAsync(server,
                                new BridgeProtocol { Kind = "ack", Ok = false, Error = error }, ct);
                            continue;
                        }
                        if (!_dedup.TryRecord(normalized.EventId))
                        {
                            await BridgeProtocol.WriteFrameAsync(server,
                                new BridgeProtocol { Kind = "ack", Ok = true, Error = "duplicate" }, ct);
                            continue;
                        }

                        EventAccepted?.Invoke(normalized);
                        _bus.Publish(normalized);
                        // The bridge is the security boundary: approvals are registered here
                        // so UI decisions correlate even if no presenter is attached.
                        if (normalized.Type == AgentEventTypes.ApprovalRequested)
                            _approvals.Register(normalized);
                        await BridgeProtocol.WriteFrameAsync(server,
                            new BridgeProtocol { Kind = "ack", Ok = true }, ct);
                    }
                }
                finally
                {
                    lock (_targetGate) _responseTargets.Remove(clientLabel);
                    Logger.Info($"Bridge: client '{clientLabel}' disconnected.");
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (IOException) { /* client dropped mid-frame; normal */ }
        catch (Exception ex)
        {
            Logger.Error($"Bridge: client '{clientLabel}' handler failed", ex);
        }
        finally
        {
            _clientSlots.Release();
        }
    }

    private static async Task Reject(NamedPipeServerStream server, string error, CancellationToken ct)
    {
        try
        {
            await BridgeProtocol.WriteFrameAsync(server, new BridgeProtocol { Kind = "ack", Ok = false, Error = error }, ct);
        }
        catch { /* client may already be gone */ }
    }

    private void OnApprovalResolved(ApprovalRequest request)
    {
        if (request.Decision is null) return;
        var frame = new BridgeProtocol
        {
            Kind = "approval_resolved",
            ApprovalRequestId = request.ApprovalRequestId,
            Decision = request.Decision.Value.ToString().ToLowerInvariant(),
            By = string.IsNullOrEmpty(request.ResolvedBy) ? "system" : request.ResolvedBy,
            At = request.ResolvedUtc ?? DateTimeOffset.UtcNow,
        };
        Stream[] targets;
        lock (_targetGate) targets = _responseTargets.Values.ToArray();
        foreach (var target in targets)
        {
            // Fire-and-forget push; a dead client's pipe just fails its next read.
            _ = Task.Run(async () =>
            {
                try { await BridgeProtocol.WriteFrameAsync(target, frame, CancellationToken.None); }
                catch (Exception ex) { Logger.Warn($"Bridge: response push failed ({ex.Message})"); }
            });
        }
        Logger.Info($"Bridge: approval '{request.ApprovalRequestId}' resolved ({request.Decision}); response frame queued.");
    }

    public async ValueTask DisposeAsync()
    {
        _approvals.Resolved -= OnApprovalResolved;
        IsRunning = false;
        _cts?.Cancel();
        try { if (_acceptLoop is not null) await _acceptLoop; } catch { /* shutting down */ }
        _cts?.Dispose();
        _clientSlots.Dispose();
    }
}
