namespace MiMi.Core;

public interface IBrightnessMonitor : IDisposable
{
    /// <summary>Completes only after the monitor's native subscription has been torn down.</summary>
    Task Completion { get; }
}

/// <summary>
/// UI-thread-owned brightness monitoring lifetime. Replacements wait for teardown;
/// callbacks are checked on the UI thread against their originating generation.
/// Dispatch and monitor creation are injected so tests need neither WMI nor WPF.
/// </summary>
public sealed class BrightnessMonitorLifetime : IDisposable
{
    private readonly Func<Func<int, Task>, IBrightnessMonitor> _create;
    private readonly Func<Action, Task> _dispatch;
    private readonly Action _verifyAccess;
    private readonly Action<int> _publish;
    private IBrightnessMonitor? _monitor;
    private object? _generation;
    private bool _enabled;
    private bool _stopping;
    private bool _disposed;

    public Task TransitionCompletion { get; private set; } = Task.CompletedTask;

    public BrightnessMonitorLifetime(Func<Func<int, Task>, IBrightnessMonitor> create,
        Func<Action, Task> dispatch, Action verifyAccess, Action<int> publish)
    {
        _create = create;
        _dispatch = dispatch;
        _verifyAccess = verifyAccess;
        _publish = publish;
    }

    public void SetEnabled(bool enabled)
    {
        _verifyAccess();
        if (_disposed || _enabled == enabled) return;
        _enabled = enabled;
        Reconcile();
    }

    private void Reconcile()
    {
        _verifyAccess();
        if (!_enabled || _disposed)
        {
            _generation = null;
            if (_monitor is not { } previous) return;
            _monitor = null;
            _stopping = true;
            previous.Dispose(); // Signals only; native cleanup belongs to the worker.
            TransitionCompletion = FinishStopAsync(previous.Completion);
        }
        else if (_monitor is null && !_stopping)
        {
            var generation = _generation = new object();
            _monitor = _create(percent => _dispatch(() =>
            {
                _verifyAccess();
                if (!_disposed && _enabled && ReferenceEquals(_generation, generation))
                    _publish(percent);
            }));
        }
    }

    private async Task FinishStopAsync(Task completion)
    {
        try { await completion.ConfigureAwait(false); }
        catch (Exception ex)
        {
            // A failed teardown cannot safely be followed by another subscription.
            Logger.Error("Brightness monitor teardown failed; restart suppressed", ex);
            return;
        }

        try
        {
            await _dispatch(() =>
            {
                _verifyAccess();
                _stopping = false;
                Reconcile(); // Uses the latest preference, not the one at stop time.
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* Dispatcher shutdown; no restart needed. */ }
    }

    public void Dispose()
    {
        _verifyAccess();
        if (_disposed) return;
        _disposed = true;
        _enabled = false;
        Reconcile();
    }
}
