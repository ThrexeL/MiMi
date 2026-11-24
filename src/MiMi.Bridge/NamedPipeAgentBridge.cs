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

    // Approval responses are private to the connection that requested them. Labels
    // are display-only: concurrent hook processes often use the same label.
    private readonly Dictionary<string, ApprovalTarget> _approvalTargets = new();
    private readonly object _targetGate = new();

    private sealed record ApprovalTarget(Stream Stream, string Label, string Provider, string SessionId);

    private sealed class ClientRateLimiter
    {
        private const int Limit = 120;
        private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);
        private readonly Queue<long> _hits = new();
        private readonly object _gate = new();

        public bool TryAcquire()
        {
            var now = Environment.TickCount64;
            lock (_gate)
            {
                while (_hits.Count > 0 && now - _hits.Peek() >= Window.TotalMilliseconds)
                    _hits.Dequeue();
                if (_hits.Count >= Limit) return false;
                _hits.Enqueue(now);
                return true;
            }
        }
    }

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
            var slotAcquired = false;
            var slotTransferred = false;
            try
            {
                if (!await _clientSlots.WaitAsync(1000, ct)) continue;
                slotAcquired = true;
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
                slotTransferred = true;
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
                // Every acquired slot has exactly one owner. Failures before the
                // handler owns it must release it here or eight transient errors can
                // permanently disable the bridge.
                if (slotAcquired && !slotTransferred) _clientSlots.Release();
            }
        }
    }

    private async Task ServeClientAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        var clientLabel = "unknown";
        var rateLimiter = new ClientRateLimiter();
        // Approvals sent on this connection: if it drops before a decision is written,
        // nobody can receive the response any more, so they must not linger in the UI.
        var approvalsOnConnection = new HashSet<string>(StringComparer.Ordinal);
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
                if (clientLabel.Length > 80) clientLabel = clientLabel[..80];
                Logger.Info($"Bridge: client '{clientLabel}' connected.");
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

                        if (!rateLimiter.TryAcquire())
                        {
                            await BridgeProtocol.WriteFrameAsync(server,
                                new BridgeProtocol { Kind = "ack", Ok = false, Error = "event rate limit exceeded" }, ct);
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

                        // A resolution is an authorization decision, not display-only
                        // telemetry. Only the connection that created the approval may
                        // resolve it, and its provider/session identity must still match.
                        if (normalized.Type == AgentEventTypes.ApprovalResolved
                            && !IsApprovalOwner(server, normalized))
                        {
                            Logger.Warn($"Bridge: rejected unauthorized approval resolution from '{clientLabel}'.");
                            await BridgeProtocol.WriteFrameAsync(server,
                                new BridgeProtocol { Kind = "ack", Ok = false, Error = "approval owner mismatch" }, ct);
                            continue;
                        }

                        if (!_dedup.TryRecord(normalized.EventId))
                        {
                            await BridgeProtocol.WriteFrameAsync(server,
                                new BridgeProtocol { Kind = "ack", Ok = true, Error = "duplicate" }, ct);
                            continue;
                        }

                        // Register before publishing: subscribers must always be able to
                        // resolve the approval they are about to display. (Registering from
                        // both sides around Publish raced and could drop the first card.)
                        if (normalized.Type == AgentEventTypes.ApprovalRequested)
                        {
                            var id = normalized.ApprovalRequestId!;
                            var request = _approvals.Register(normalized);
                            if (request is null)
                            {
                                var duplicate = _approvals.Get(id) is not null;
                                if (!duplicate) _dedup.Forget(normalized.EventId);
                                await BridgeProtocol.WriteFrameAsync(server, new BridgeProtocol
                                {
                                    Kind = "ack",
                                    Ok = duplicate,
                                    Error = duplicate ? "duplicate" : "approval capacity exceeded",
                                }, ct);
                                continue;
                            }
                            lock (_targetGate)
                                _approvalTargets[id] = new ApprovalTarget(server, clientLabel,
                                    normalized.Provider, normalized.SessionId);
                            approvalsOnConnection.Add(id);
                        }
                        EventAccepted?.Invoke(normalized);
                        _bus.Publish(normalized);
                        await BridgeProtocol.WriteFrameAsync(server,
                            new BridgeProtocol { Kind = "ack", Ok = true }, ct);
                    }
                }
                finally
                {
                    lock (_targetGate)
                    {
                        foreach (var id in approvalsOnConnection)
                        {
                            if (_approvalTargets.TryGetValue(id, out var target)
                                && ReferenceEquals(target.Stream, server))
                                _approvalTargets.Remove(id);
                        }
                    }
                    Logger.Info($"Bridge: client '{clientLabel}' disconnected.");
                    // A dropped requester can never receive a decision. Dismiss its still-
                    // pending approvals so cards and session states do not go stale.
                    foreach (var id in approvalsOnConnection)
                    {
                        if (_approvals.TryDecide(id, ApprovalDecision.Dismissed, "provider-disconnected"))
                            Logger.Warn($"Approval '{id}' dismissed: requesting client '{clientLabel}' disconnected before a decision.");
                    }
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

    private bool IsApprovalOwner(Stream stream, AgentEvent ev)
    {
        lock (_targetGate)
        {
            return _approvalTargets.TryGetValue(ev.ApprovalRequestId!, out var target)
                   && ReferenceEquals(target.Stream, stream)
                   && string.Equals(target.Provider, ev.Provider, StringComparison.Ordinal)
                   && string.Equals(target.SessionId, ev.SessionId, StringComparison.Ordinal);
        }
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
        ApprovalTarget target = null!;
        lock (_targetGate)
        {
            if (!_approvalTargets.Remove(request.ApprovalRequestId, out var found)) return;
            target = found;
        }

        // Fire-and-forget push only to the requester. A dead client's pipe just fails
        // its next read; unrelated authenticated clients never see approval metadata.
        _ = Task.Run(async () =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await BridgeProtocol.WriteFrameAsync(target.Stream, frame, timeout.Token); }
            catch (Exception ex) { Logger.Warn($"Bridge: response push to '{target.Label}' failed ({ex.Message})"); }
        });
        Logger.Info($"Bridge: approval '{request.ApprovalRequestId}' resolved ({request.Decision}); response frame queued for '{target.Label}'.");
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
