using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MiMi.Agents;
using MiMi.Codex;
using MiMi.App.Interop;
using MiMi.Core;

namespace MiMi.App.UI;

public partial class CodexConversationWindow : Window
{
    private readonly ApprovalStore _approvals;
    private readonly AgentEventBus _bus;
    private readonly string _workspace;
    private readonly CodexAppServerClient _client;
    private readonly ConcurrentDictionary<string, string> _approvalIds = new();
    private readonly ConcurrentQueue<CodexTimelineItem> _pendingItems = new();
    private readonly ObservableCollection<CodexTranscriptRow> _rows = new();
    private readonly DispatcherTimer _transcriptTimer = new() { Interval = TimeSpan.FromMilliseconds(90) };
    private readonly DispatcherTimer _heartbeatTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private int _transcriptFlushQueued;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    private bool _wasBusy;
    private bool _closing;

    public CodexConversationWindow(ApprovalStore approvals, AgentEventBus bus, string workspace)
    {
        _approvals = approvals;
        _bus = bus;
        _workspace = workspace;
        InitializeComponent();
        MiMiIcon.Apply(this);
        TranscriptItems.ItemsSource = _rows;
        WorkspaceText.Text = workspace;
        WorkspaceText.ToolTip = workspace;
        _client = new CodexAppServerClient(workspace);
        _client.StateChanged += OnStateChanged;
        _client.ItemChanged += QueueItem;
        _client.ApprovalRequested += OnApprovalRequested;
        _client.ApprovalCancelled += OnApprovalCancelled;
        _approvals.Resolved += OnApprovalResolved;
        _transcriptTimer.Tick += (_, _) => FlushItems();
        _heartbeatTimer.Tick += (_, _) =>
        {
            if (!_client.State.Busy) return;
            _bus.Publish(AgentEventFactory.Create("codex-app", _client.State.ThreadId ?? "codex",
                AgentEventTypes.Heartbeat, metadata: new Dictionary<string, string>
                {
                    ["activity_type"] = AgentEventTypes.ToolStarted,
                }));
        };
        Closed += (_, _) => _ = EnsureDisposedAsync();
    }

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendPromptAsync();

    private async Task SendPromptAsync()
    {
        var prompt = PromptInput.Text.Trim();
        if (prompt.Length == 0 || _client.State.Busy) return;
        PromptInput.Text = "";
        SendButton.IsEnabled = false;
        try
        {
            var firstTurn = _client.State.ThreadId is null;
            QueueItem(new CodexTimelineItem("local:" + Guid.NewGuid().ToString("N"), "userMessage", "You",
                prompt, "complete", DateTimeOffset.UtcNow));
            var state = await _client.SendPromptAsync(prompt);
            if (firstTurn)
                _bus.Publish(AgentEventFactory.Create("codex-app", state.ThreadId ?? "codex", AgentEventTypes.Started,
                    title: "Codex session started", project: Path.GetFileName(_workspace)));
            _bus.Publish(AgentEventFactory.Create("codex-app", state.ThreadId ?? "codex",
                AgentEventTypes.PromptSubmitted, title: "Codex turn started", project: Path.GetFileName(_workspace)));
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            _bus.Publish(AgentEventFactory.Create("codex-app", _client.State.ThreadId ?? "codex",
                AgentEventTypes.Failed, title: "Codex could not start", message: ex.Message,
                project: Path.GetFileName(_workspace)));
        }
        finally
        {
            SendButton.IsEnabled = !_client.State.Busy;
        }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        try { await _client.InterruptAsync(); }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private void PromptInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift)) return;
        e.Handled = true;
        _ = SendPromptAsync();
    }

    private void OnStateChanged(CodexClientState state)
    {
        Dispatcher.BeginInvoke(() =>
        {
            StatusText.Text = state.LastError is { Length: > 0 } error
                ? $"{state.Status} · {error}"
                : $"{state.Status} · read-only sandbox · one-shot approvals";
            SendButton.IsEnabled = state.Connected && !state.Busy;
            StopButton.IsEnabled = state.Busy;
            if (state.Busy) _heartbeatTimer.Start(); else _heartbeatTimer.Stop();
            if (_wasBusy && !state.Busy)
            {
                var status = state.Status.ToLowerInvariant();
                var failed = state.LastError is not null || status.Contains("fail") || status.Contains("error");
                var interrupted = status.Contains("interrupt") || status.Contains("cancel");
                var disconnected = status == "disconnected";
                var type = failed ? AgentEventTypes.Failed
                    : interrupted ? AgentEventTypes.Interrupted
                    : disconnected ? AgentEventTypes.Cancelled
                    : AgentEventTypes.TurnFinished;
                var title = failed ? "Codex turn failed"
                    : interrupted ? "Codex turn interrupted"
                    : disconnected ? "Codex disconnected"
                    : "Codex turn finished";
                _bus.Publish(AgentEventFactory.Create("codex-app", state.ThreadId ?? "codex", type,
                    title: title, project: Path.GetFileName(_workspace)));
            }
            _wasBusy = state.Busy;
        });
    }

    private void QueueItem(CodexTimelineItem item)
    {
        _pendingItems.Enqueue(item);
        if (Interlocked.Exchange(ref _transcriptFlushQueued, 1) != 0) return;
        Dispatcher.BeginInvoke(() => _transcriptTimer.Start(), DispatcherPriority.Background);
    }

    private void FlushItems()
    {
        _transcriptTimer.Stop();
        var nearBottom = TranscriptScroll.VerticalOffset >= TranscriptScroll.ScrollableHeight - 24;
        var changed = new Dictionary<string, CodexTimelineItem>(StringComparer.Ordinal);
        while (_pendingItems.TryDequeue(out var item)) changed[item.Id] = item;
        Interlocked.Exchange(ref _transcriptFlushQueued, 0);
        if (!_pendingItems.IsEmpty && Interlocked.Exchange(ref _transcriptFlushQueued, 1) == 0)
            _transcriptTimer.Start();
        foreach (var item in changed.Values)
        {
            var row = new CodexTranscriptRow(item.Id, item.Title, item.Detail, item.Status);
            var index = _rows.ToList().FindIndex(r => r.Id == row.Id);
            if (index < 0) _rows.Add(row); else _rows[index] = row;
        }
        while (_rows.Count > 200) _rows.RemoveAt(0);
        if (nearBottom) TranscriptScroll.ScrollToEnd();
    }

    private void OnApprovalRequested(CodexApprovalRequest request)
    {
        var approvalId = "cx-" + Guid.NewGuid().ToString("N");
        var ev = AgentEventFactory.Create("codex-app", request.ThreadId, AgentEventTypes.ApprovalRequested,
            title: "Codex approval required", message: request.Reason,
            project: Path.GetFileName(_workspace), approvalRequestId: approvalId,
            metadata: new Dictionary<string, string>
            {
                ["command"] = request.DisplayCommand,
                ["action_kind"] = request.Kind,
                ["cwd"] = request.WorkingDirectory,
            });
        var registered = _approvals.Register(ev);
        if (registered is null)
        {
            _ = _client.RespondApprovalAsync(request.RpcId, false);
            return;
        }
        _approvalIds[request.RpcId] = approvalId;
        _bus.Publish(ev);
    }

    private void OnApprovalResolved(ApprovalRequest request)
    {
        foreach (var (rpcId, approvalId) in _approvalIds)
        {
            if (approvalId != request.ApprovalRequestId) continue;
            _approvalIds.TryRemove(rpcId, out _);
            _ = _client.RespondApprovalAsync(rpcId, request.Decision == ApprovalDecision.Approved);
            return;
        }
    }

    private void OnApprovalCancelled(CodexApprovalRequest request)
    {
        if (!_approvalIds.TryRemove(request.RpcId, out var approvalId)) return;
        _approvals.TryDecide(approvalId, ApprovalDecision.Dismissed, "codex-disconnected");
    }

    public Task EnsureDisposedAsync()
    {
        lock (_disposeGate)
            return _disposeTask ??= Task.Run(DisposeCoreAsync);
    }

    private async Task DisposeCoreAsync()
    {
        if (_closing) return;
        _closing = true;
        _transcriptTimer.Stop();
        _heartbeatTimer.Stop();
        _approvals.Resolved -= OnApprovalResolved;
        _client.StateChanged -= OnStateChanged;
        _client.ItemChanged -= QueueItem;
        _client.ApprovalRequested -= OnApprovalRequested;
        _client.ApprovalCancelled -= OnApprovalCancelled;
        var sessionId = _client.State.ThreadId ?? "codex";
        try { if (_client.State.Busy) await _client.InterruptAsync(); } catch { }
        await _client.DisposeAsync();
        foreach (var approvalId in _approvalIds.Values)
            _approvals.TryDecide(approvalId, ApprovalDecision.Dismissed, "codex-window-closed");
        _approvalIds.Clear();
        _bus.Publish(AgentEventFactory.Create("codex-app", sessionId,
            AgentEventTypes.SessionEnded, title: "Codex session ended", project: Path.GetFileName(_workspace)));
        await Task.CompletedTask;
    }
}
