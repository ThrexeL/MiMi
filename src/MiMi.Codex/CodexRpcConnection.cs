using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using MiMi.Core;

namespace MiMi.Codex;

/// <summary>Minimal JSON-RPC connection for Codex's line-oriented stdio transport.</summary>
public sealed class CodexRpcConnection : IAsyncDisposable
{
    public const int MaxLineCharacters = 1024 * 1024;

    private readonly TextReader _reader;
    private readonly TextWriter _writer;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _readLoop;
    private long _nextRequestId;
    private int _disconnectNotified;

    public event Func<JsonElement, CancellationToken, Task>? RequestReceived;
    public event Action<JsonElement>? NotificationReceived;
    public event Action<Exception?>? Disconnected;

    public CodexRpcConnection(TextReader reader, TextWriter writer)
    {
        _reader = reader;
        _writer = writer;
    }

    public void Start()
    {
        if (_readLoop is not null) return;
        _readLoop = Task.Run(ReadLoopAsync);
    }

    public async Task<JsonElement> RequestAsync(string method, object? parameters,
        TimeSpan timeout, CancellationToken ct = default)
    {
        var id = (Interlocked.Increment(ref _nextRequestId)).ToString(CultureInfo.InvariantCulture);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            await WriteAsync(new Dictionary<string, object?>
            {
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters,
            }, ct);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            linked.CancelAfter(timeout);
            var response = await completion.Task.WaitAsync(linked.Token);
            if (response.ValueKind != JsonValueKind.Object) return JsonDocument.Parse("null").RootElement.Clone();
            if (response.TryGetProperty("error", out var error))
                throw new CodexRpcException(error);
            return response.TryGetProperty("result", out var result)
                ? result.Clone()
                : JsonDocument.Parse("null").RootElement.Clone();
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public Task NotifyAsync(string method, object? parameters = null, CancellationToken ct = default)
    {
        var message = new Dictionary<string, object?> { ["method"] = method };
        if (parameters is not null) message["params"] = parameters;
        return WriteAsync(message, ct);
    }

    public Task RespondAsync(JsonElement id, object result, CancellationToken ct = default) =>
        WriteAsync(new Dictionary<string, object?> { ["id"] = id, ["result"] = result }, ct);

    public Task RespondErrorAsync(JsonElement id, int code, string message, CancellationToken ct = default) =>
        WriteAsync(new Dictionary<string, object?>
        {
            ["id"] = id,
            ["error"] = new { code, message },
        }, ct);

    private async Task WriteAsync(object message, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(message);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await _writeGate.WaitAsync(timeout.Token);
        try
        {
            await _writer.WriteLineAsync(json.AsMemory(), timeout.Token);
            await _writer.FlushAsync(timeout.Token);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        Exception? failure = null;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var line = await _reader.ReadLineAsync(_cts.Token);
                if (line is null) break;
                if (line.Length > MaxLineCharacters)
                    throw new CodexRpcException(-32600, "Codex app-server message exceeded MiMi's size limit.");
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("id", out var idElement))
                {
                    var id = idElement.ToString();
                    if (root.TryGetProperty("method", out _))
                    {
                        // Server-initiated requests must not block the notification stream
                        // while an approval card waits for the user.
                        _ = HandleRequestAsync(root.Clone(), _cts.Token);
                    }
                    else if (_pending.TryGetValue(id, out var completion))
                    {
                        completion.TrySetResult(root.Clone());
                    }
                    continue;
                }
                if (root.TryGetProperty("method", out _))
                    NotificationReceived?.Invoke(root.Clone());
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            foreach (var completion in _pending.Values)
                completion.TrySetException(failure ?? new CodexRpcException(-32000, "Codex app-server disconnected."));
            _pending.Clear();
            if (Interlocked.Exchange(ref _disconnectNotified, 1) == 0)
                Disconnected?.Invoke(failure);
        }
    }

    private async Task HandleRequestAsync(JsonElement request, CancellationToken ct)
    {
        try
        {
            if (RequestReceived is null)
            {
                if (request.TryGetProperty("id", out var id))
                    await RespondErrorAsync(id, -32601, "MiMi does not support this Codex request.", ct);
                return;
            }
            await RequestReceived(request, ct);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Codex: server request failed ({ex.GetType().Name}).");
            try
            {
                if (request.TryGetProperty("id", out var id))
                    await RespondErrorAsync(id, -32603, "MiMi could not handle this Codex request.", ct);
            }
            catch { /* connection may already be gone */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        foreach (var completion in _pending.Values)
            completion.TrySetCanceled(_cts.Token);
        _pending.Clear();
        if (_readLoop is not null)
        {
            try { await _readLoop.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch { /* shutting down */ }
        }
        _cts.Dispose();
        _writeGate.Dispose();
        await Task.CompletedTask;
    }
}

public sealed class CodexRpcException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;

    public CodexRpcException(JsonElement error)
        : this(error.TryGetProperty("code", out var code) ? code.GetInt32() : -32603,
            error.TryGetProperty("message", out var message) ? message.GetString() ?? "Codex request failed." : "Codex request failed.")
    {
    }
}
