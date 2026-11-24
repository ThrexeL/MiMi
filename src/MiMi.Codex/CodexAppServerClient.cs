using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MiMi.Agents;
using MiMi.Core;

namespace MiMi.Codex;

/// <summary>
/// Opt-in Codex app-server client. Conversation content stays in memory and is never
/// logged. Every approval is one-request only; timeout, disconnect, and unknown
/// requests fail closed.
/// </summary>
public sealed class CodexAppServerClient : IAsyncDisposable
{
    private static readonly TimeSpan ApprovalTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly string? _executable;
    private readonly string _workspace;
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly ConcurrentDictionary<string, CodexTimelineItem> _items = new();
    private readonly ConcurrentDictionary<string, CodexApprovalRequest> _approvals = new();
    private readonly ConcurrentDictionary<string, JsonElement> _rpcIds = new();
    private readonly object _stateGate = new();

    private Process? _process;
    private CodexRpcConnection? _connection;
    private Task? _stderrDrain;
    private CancellationTokenSource? _connectionCts;
    private CodexClientState _state = CodexClientState.Disconnected;
    private string? _model;
    private int _sendInFlight;
    private int _disposed;

    public event Action<CodexClientState>? StateChanged;
    public event Action<CodexTimelineItem>? ItemChanged;
    public event Action<CodexApprovalRequest>? ApprovalRequested;
    public event Action<CodexApprovalRequest>? ApprovalCancelled;

    public CodexClientState State
    {
        get { lock (_stateGate) return _state; }
    }

    public IReadOnlyList<CodexTimelineItem> Items => _items.Values
        .OrderBy(i => i.UpdatedUtc).ToList();

    public CodexAppServerClient(string? workspace = null, string? executable = null)
    {
        _workspace = Path.GetFullPath(workspace ?? Environment.CurrentDirectory);
        _executable = executable;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _lifecycleGate.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();
            if (_connection is not null) return;
            var executable = _executable ?? ProviderIntegrationInspector.FindOnPath("codex");
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
                throw new InvalidOperationException("Codex CLI was not found. Install Codex or set its executable path.");
            if (!executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Embedded Codex requires the native codex.exe; shell shims are not supported.");
            var version = await ProbeVersionAsync(executable);
            ThrowIfDisposed();
            if (_connection is not null) return;
            if (!version.StartsWith("codex-cli 0.160.", StringComparison.Ordinal))
                throw new InvalidOperationException($"Embedded Codex requires codex-cli 0.160.x; found '{version}'.");

            _connectionCts = new CancellationTokenSource();
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = _workspace,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("app-server");
            startInfo.ArgumentList.Add("--stdio");
            // Embedded approvals belong to MiMi. Running the user's global Codex hooks
            // here would create a second, competing approval path for the same command.
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("features.hooks=false");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("history.persistence=none");
            var process = Process.Start(startInfo)
                          ?? throw new InvalidOperationException("Codex app-server could not be started.");
            _process = process;
            var connection = new CodexRpcConnection(
                new StreamReader(process.StandardOutput.BaseStream, new UTF8Encoding(false)),
                new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true });
            _connection = connection;
            connection.RequestReceived += HandleServerRequestAsync;
            connection.NotificationReceived += HandleNotification;
            connection.Disconnected += ex => HandleDisconnected(ex, connection);
            connection.Start();
            var connectionCts = _connectionCts;
            _stderrDrain = Task.Run(() => DrainStandardErrorAsync(process.StandardError, connectionCts.Token));

            try
            {
                _ = await connection.RequestAsync("initialize", new
                {
                    clientInfo = new { name = "MiMi", title = "MiMi Dynamic Island", version = "1.0" },
                    capabilities = new { experimentalApi = false },
                }, TimeSpan.FromSeconds(10), ct);
                await connection.NotifyAsync("initialized", null, ct);
                var models = await connection.RequestAsync("model/list", new { }, TimeSpan.FromSeconds(10), ct);
                _model = SelectModel(models);
                UpdateState(_state with
                {
                    Connected = true,
                    Status = _state.Busy ? "Connecting" : "Ready",
                    Model = _model,
                    LastError = null,
                });
            }
            catch
            {
                await DisconnectCoreAsync();
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<CodexClientState> SendPromptAsync(string prompt, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("A prompt is required.", nameof(prompt));
        if (prompt.Length > 20_000) throw new ArgumentException("Prompt is too long.", nameof(prompt));
        ThrowIfDisposed();
        if (_state.Busy) throw new InvalidOperationException("Codex is still working on the previous turn.");
        if (Interlocked.CompareExchange(ref _sendInFlight, 1, 0) != 0)
            throw new InvalidOperationException("A Codex turn is already starting.");
        UpdateState(_state with { Busy = true, Status = "Connecting", LastError = null });
        var gateAcquired = false;
        try
        {
            await ConnectAsync(ct);
            await _turnGate.WaitAsync(ct);
            gateAcquired = true;
            var connection = _connection!;
            var threadId = _state.ThreadId;
            if (string.IsNullOrWhiteSpace(threadId))
            {
                var started = await connection.RequestAsync("thread/start", new
                {
                    cwd = _workspace,
                    model = _model,
                    approvalPolicy = "on-request",
                    approvalsReviewer = "user",
                    sandbox = "read-only",
                    ephemeral = true,
                }, RequestTimeout, ct);
                threadId = GetString(started, "thread", "id")
                           ?? throw new CodexRpcException(-32603, "Codex did not return a thread id.");
                UpdateState(_state with { ThreadId = threadId, Model = GetString(started, "model"), Status = "Starting" });
            }

            var turn = await connection.RequestAsync("turn/start", new
            {
                threadId,
                input = new[] { new { type = "text", text = prompt.Trim(), text_elements = Array.Empty<object>() } },
            }, RequestTimeout, ct);
            var turnId = GetString(turn, "turn", "id")
                         ?? throw new CodexRpcException(-32603, "Codex did not return a turn id.");
            UpdateState(_state with { TurnId = turnId, Status = "Working", LastError = null });
            return State;
        }
        catch (Exception ex)
        {
            await DisconnectCoreAsync();
            UpdateState(CodexClientState.Disconnected with { LastError = ex.Message });
            throw;
        }
        finally
        {
            if (gateAcquired) _turnGate.Release();
            Interlocked.Exchange(ref _sendInFlight, 0);
        }
    }

    public async Task InterruptAsync(CancellationToken ct = default)
    {
        var connection = _connection;
        var threadId = _state.ThreadId;
        var turnId = _state.TurnId;
        if (connection is null || string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(turnId)) return;
        await connection.RequestAsync("turn/interrupt", new { threadId, turnId }, RequestTimeout, ct);
        UpdateState(_state with { Status = "Interrupting" });
    }

    public async Task RespondApprovalAsync(string rpcId, bool approved, CancellationToken ct = default)
    {
        if (!_approvals.TryRemove(rpcId, out var request)) return;
        if (!_rpcIds.TryRemove(rpcId, out var rpcIdElement))
        {
            ApprovalCancelled?.Invoke(request);
            return;
        }
        var connection = _connection;
        if (connection is null)
        {
            ApprovalCancelled?.Invoke(request);
            return;
        }
        try
        {
            await connection.RespondAsync(rpcIdElement, new { decision = approved ? "accept" : "decline" }, ct);
            UpsertItem(new CodexTimelineItem("approval:" + rpcId, "approval", approved ? "Approved" : "Denied",
                request.DisplayCommand, "complete", DateTimeOffset.UtcNow));
        }
        catch
        {
            ApprovalCancelled?.Invoke(request);
        }
    }

    public async Task DisconnectAsync()
    {
        await _lifecycleGate.WaitAsync();
        try { await DisconnectCoreAsync(); }
        finally { _lifecycleGate.Release(); }
    }

    private async Task DisconnectCoreAsync()
    {
        _connectionCts?.Cancel();
        foreach (var request in _approvals.Values.ToArray())
        {
            _approvals.TryRemove(request.RpcId, out _);
            _rpcIds.TryRemove(request.RpcId, out _);
            ApprovalCancelled?.Invoke(request);
        }
        _rpcIds.Clear();
        var connection = _connection;
        _connection = null;
        if (connection is not null)
            await connection.DisposeAsync().ConfigureAwait(false);
        try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); }
        catch { /* best effort */ }
        _process?.Dispose();
        _process = null;
        _connectionCts?.Dispose();
        _connectionCts = null;
        _items.Clear();
        UpdateState(CodexClientState.Disconnected);
    }

    private static string? SelectModel(JsonElement response)
    {
        if (!response.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return null;
        var models = data.EnumerateArray().Where(m => m.ValueKind == JsonValueKind.Object).ToList();
        JsonElement? selected = models
            .Where(m => m.TryGetProperty("isDefault", out var isDefault)
                        && isDefault.ValueKind == JsonValueKind.True
                        && (!m.TryGetProperty("hidden", out var hidden) || hidden.ValueKind != JsonValueKind.True))
            .Select(m => (JsonElement?)m).FirstOrDefault();
        selected ??= models
            .Where(m => !m.TryGetProperty("hidden", out var hidden) || hidden.ValueKind != JsonValueKind.True)
            .Select(m => (JsonElement?)m).FirstOrDefault();
        selected ??= models.Select(m => (JsonElement?)m).FirstOrDefault();
        return selected is { } model ? GetString(model, "model") ?? GetString(model, "id") : null;
    }

    private static async Task<string> ProbeVersionAsync(string executable)
    {
        var info = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add("--version");
        using var process = Process.Start(info)
                           ?? throw new InvalidOperationException("Codex version probe failed.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new InvalidOperationException("Codex version probe timed out.");
        }
    }

    private async Task HandleServerRequestAsync(JsonElement request, CancellationToken ct)
    {
        if (!request.TryGetProperty("id", out var idElement)
            || !request.TryGetProperty("method", out var methodElement)
            || !request.TryGetProperty("params", out var parameters))
        {
            try
            {
                if (request.TryGetProperty("id", out var badId))
                    await _connection!.RespondErrorAsync(badId, -32600, "Invalid Codex request.", ct);
            }
            finally
            {
                await DisconnectAsync();
            }
            return;
        }

        var rpcId = idElement.ToString();
        var method = methodElement.GetString() ?? "";
        CodexApprovalRequest? approval = method switch
        {
            "item/commandExecution/requestApproval" => ParseCommandApproval(rpcId, method, parameters),
            _ => null,
        };
        if (approval is null)
        {
            if (method == "item/fileChange/requestApproval")
            {
                // The stable file-change approval payload does not include the affected
                // paths/diff. Decline rather than ask for an uninformed grant.
                try
                {
                    await _connection!.RespondAsync(idElement, new { decision = "decline" }, ct);
                }
                finally
                {
                    await DisconnectAsync();
                }
                return;
            }
            try
            {
                await _connection!.RespondErrorAsync(idElement, -32601,
                    "MiMi only supports one-request command approvals.", ct);
            }
            finally
            {
                await DisconnectAsync();
            }
            return;
        }

        if (approval.ThreadId.Length == 0 || approval.TurnId.Length == 0
            || approval.ItemId.Length == 0 || approval.DisplayCommand.Length == 0)
        {
            try
            {
                await _connection!.RespondAsync(idElement, new { decision = "decline" }, ct);
            }
            finally
            {
                await DisconnectAsync();
            }
            return;
        }

        _rpcIds[rpcId] = idElement.Clone();
        _approvals[rpcId] = approval;
        ApprovalRequested?.Invoke(approval);
        _ = ExpireApprovalAsync(approval, _connectionCts?.Token ?? CancellationToken.None);
    }

    private async Task ExpireApprovalAsync(CodexApprovalRequest approval, CancellationToken ct)
    {
        try
        {
            await Task.Delay(ApprovalTimeout, ct);
            await RespondApprovalAsync(approval.RpcId, false);
        }
        catch (OperationCanceledException) { }
    }

    private static CodexApprovalRequest ParseCommandApproval(string rpcId, string method, JsonElement p) => new(
        rpcId, method,
        GetString(p, "threadId") ?? "",
        GetString(p, "turnId") ?? "",
        GetString(p, "itemId") ?? "",
        GetString(p, "kind") ?? "command",
        GetString(p, "command") ?? "",
        GetString(p, "cwd") ?? "",
        GetString(p, "reason") ?? "");

    private static CodexApprovalRequest ParseFileApproval(string rpcId, string method, JsonElement p) => new(
        rpcId, method,
        GetString(p, "threadId") ?? "",
        GetString(p, "turnId") ?? "",
        GetString(p, "itemId") ?? "",
        "file-change",
        "Apply file changes",
        GetString(p, "grantRoot") ?? "",
        GetString(p, "reason") ?? "");

    private void HandleNotification(JsonElement notification)
    {
        if (!notification.TryGetProperty("method", out var methodElement)
            || !notification.TryGetProperty("params", out var p))
            return;
        var method = methodElement.GetString() ?? "";
        switch (method)
        {
            case "turn/started":
                UpdateState(_state with
                {
                    Busy = true,
                    TurnId = GetString(p, "turn", "id"),
                    Status = "Working",
                    LastError = null,
                });
                break;
            case "item/started":
            case "item/completed":
                var item = ParseItem(p);
                if (item.Kind != "userMessage") UpsertItem(item);
                break;
            case "item/agentMessage/delta":
                AppendAgentDelta(p);
                break;
            case "turn/completed":
                var status = GetString(p, "turn", "status") ?? "completed";
                var error = GetString(p, "turn", "error", "message");
                UpdateState(_state with { Busy = false, Status = status, LastError = error });
                break;
            case "error":
                UpdateState(_state with { Busy = false, Status = "Error", LastError = GetString(p, "message") });
                break;
            case "thread/status/changed":
                UpdateState(_state with { Status = GetString(p, "status") ?? _state.Status });
                break;
        }
    }

    private CodexTimelineItem ParseItem(JsonElement p)
    {
        var item = p.TryGetProperty("item", out var value) ? value : default;
        var id = GetString(item, "id") ?? Guid.NewGuid().ToString("N");
        var type = GetString(item, "type") ?? "item";
        var now = DateTimeOffset.UtcNow;
        return type switch
        {
            "userMessage" => new(id, type, "You", ExtractUserText(item), "complete", now),
            "agentMessage" => new(id, type, "Codex", GetString(item, "text") ?? "", "complete", now),
            "reasoning" => new(id, type, "Thinking", string.Join(" • ", GetStringArray(item, "summary")), "complete", now),
            "plan" => new(id, type, "Plan", GetString(item, "text") ?? "", "complete", now),
            "commandExecution" => new(id, type, "Command", GetString(item, "command") ?? "",
                GetString(item, "status") ?? "running", now),
            "fileChange" => new(id, type, "File changes", ExtractFilePaths(item),
                GetString(item, "status") ?? "running", now),
            "mcpToolCall" => new(id, type, "MCP · " + (GetString(item, "tool") ?? "tool"),
                GetString(item, "server") ?? "", GetString(item, "status") ?? "running", now),
            "contextCompaction" => new(id, type, "Context compacted", "Conversation context was compacted.", "complete", now),
            _ => new(id, type, type, "", "complete", now),
        };
    }

    private void AppendAgentDelta(JsonElement p)
    {
        var id = GetString(p, "itemId") ?? "";
        var delta = GetString(p, "delta") ?? "";
        if (id.Length == 0 || delta.Length == 0) return;
        var existing = _items.GetOrAdd(id, _ => new CodexTimelineItem(id, "agentMessage", "Codex", "", "streaming", DateTimeOffset.UtcNow));
        var detail = existing.Detail + delta;
        if (detail.Length > 30_000) detail = detail[^30_000..];
        UpsertItem(existing with { Detail = detail, Status = "streaming", UpdatedUtc = DateTimeOffset.UtcNow });
    }

    private void UpsertItem(CodexTimelineItem item)
    {
        _items[item.Id] = item;
        var excess = _items.Count - 200;
        if (excess > 0)
        {
            foreach (var (id, _) in _items
                         .Where(pair => pair.Value.Status is not "streaming" and not "running")
                         .OrderBy(pair => pair.Value.UpdatedUtc)
                         .Take(excess))
                _items.TryRemove(id, out _);
        }
        ItemChanged?.Invoke(item);
    }

    private void HandleDisconnected(Exception? error, CodexRpcConnection source)
    {
        foreach (var request in _approvals.Values.ToArray())
        {
            _approvals.TryRemove(request.RpcId, out _);
            _rpcIds.TryRemove(request.RpcId, out _);
            ApprovalCancelled?.Invoke(request);
        }
        _rpcIds.Clear();
        UpdateState(_state with
        {
            Connected = false,
            Busy = false,
            Status = "Disconnected",
            LastError = error?.Message,
        });
        _ = Task.Run(async () =>
        {
            try
            {
                await _lifecycleGate.WaitAsync();
                try
                {
                    if (!ReferenceEquals(_connection, source)) return;
                    await DisconnectCoreAsync().ConfigureAwait(false);
                    UpdateState(CodexClientState.Disconnected with { LastError = error?.Message });
                }
                finally
                {
                    _lifecycleGate.Release();
                }
            }
            catch (ObjectDisposedException) { }
        });
    }

    private void UpdateState(CodexClientState state)
    {
        lock (_stateGate) _state = state;
        StateChanged?.Invoke(state);
    }

    private async Task DrainStandardErrorAsync(StreamReader reader, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;
                if (line.Length > 4096) line = line[..4096];
                if (line.Contains("error", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("failed", StringComparison.OrdinalIgnoreCase))
                    Logger.Warn("Codex app-server reported an error.");
            }
        }
        catch (OperationCanceledException) { }
        catch { /* stderr is diagnostic-only and never surfaced verbatim */ }
    }

    private static string ExtractUserText(JsonElement item)
    {
        if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return "";
        var parts = new List<string>();
        foreach (var part in content.EnumerateArray())
        {
            if (GetString(part, "type") == "text" && GetString(part, "text") is { Length: > 0 } text)
                parts.Add(text);
        }
        return string.Join("\n", parts);
    }

    private static string ExtractFilePaths(JsonElement item)
    {
        if (!item.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
            return "";
        return string.Join("\n", changes.EnumerateArray()
            .Select(change => GetString(change, "path") ?? "")
            .Where(path => path.Length > 0));
    }

    private static string[] GetStringArray(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String)
                .Select(v => v.GetString() ?? "").Where(v => v.Length > 0).ToArray()
            : Array.Empty<string>();

    private static string? GetString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var name in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current)) return null;
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(CodexAppServerClient));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await DisconnectAsync().ConfigureAwait(false);
        _turnGate.Dispose();
        _lifecycleGate.Dispose();
        await Task.CompletedTask;
    }
}
