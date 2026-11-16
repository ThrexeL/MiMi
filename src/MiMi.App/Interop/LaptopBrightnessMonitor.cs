using System.Management;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading.Channels;
using MiMi.Core;

namespace MiMi.App.Interop;

/// <summary>
/// Observes one active WMI laptop backlight, never external-monitor DDC/CI or key presses.
/// WMI setup/teardown stays on a worker. A latest-value mailbox and awaited delivery
/// bound both native event bursts and dispatcher work; there is no polling or retry loop.
/// </summary>
public sealed class LaptopBrightnessMonitor : IBrightnessMonitor
{
    private readonly Channel<int> _readings = Channel.CreateBounded<int>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false,
    });
    private int _disposed;

    public Task Completion { get; }

    public LaptopBrightnessMonitor(Func<int, Task> onChanged)
    {
        Completion = Task.Run(() => RunAsync(onChanged));
    }

    private async Task RunAsync(Func<int, Task> onChanged)
    {
        ManagementEventWatcher? watcher = null;
        EventArrivedEventHandler? arrived = null;
        StoppedEventHandler? stopped = null;
        try
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            var scope = new ManagementScope(@"\\.\root\WMI");
            scope.Options.Timeout = TimeSpan.FromSeconds(5);
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT InstanceName, Active, CurrentBrightness FROM WmiMonitorBrightness"),
                new EnumerationOptions { Timeout = TimeSpan.FromSeconds(5), ReturnImmediately = false });
            using var results = searcher.Get();
            string? instance = null;
            var policy = new BrightnessHudPolicy();
            foreach (ManagementObject monitor in results)
            {
                using (monitor)
                {
                    if (monitor["Active"] is not true || monitor["InstanceName"] is not string name
                        || string.IsNullOrWhiteSpace(name) || monitor["CurrentBrightness"] is not byte percent
                        || percent > 100) continue;
                    instance = name;
                    policy.Evaluate(percent); // Startup is a baseline, not a brightness change.
                    break;
                }
            }

            if (instance is null)
            {
                Logger.Info("Brightness HUD unavailable: no active WMI laptop backlight.");
                return;
            }
            if (Volatile.Read(ref _disposed) != 0) return;

            watcher = new ManagementEventWatcher(scope,
                new WqlEventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
            arrived = (_, args) =>
            {
                using var reading = args.NewEvent;
                if (Volatile.Read(ref _disposed) != 0) return;
                try
                {
                    if (reading["InstanceName"] is string name
                        && string.Equals(name, instance, StringComparison.OrdinalIgnoreCase)
                        && reading["Brightness"] is byte percent && percent <= 100)
                        _readings.Writer.TryWrite(percent);
                }
                catch (Exception ex) when (IsUnavailable(ex))
                {
                    _readings.Writer.TryComplete(ex);
                }
            };
            stopped = (_, args) =>
            {
                // Provider removal, access loss or WMI shutdown: stop, don't retry forever.
                _readings.Writer.TryComplete();
            };
            watcher.EventArrived += arrived;
            watcher.Stopped += stopped;
            watcher.Start();
            Logger.Info("Brightness HUD monitoring an active WMI laptop backlight.");

            await foreach (var percent in _readings.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (Volatile.Read(ref _disposed) != 0) break;
                if (policy.Evaluate(percent))
                    await onChanged(percent).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (IsUnavailable(ex) || ex is OperationCanceledException)
        {
            Logger.Info($"Brightness HUD unavailable: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _readings.Writer.TryComplete();
            if (watcher is not null)
            {
                watcher.EventArrived -= arrived;
                watcher.Stopped -= stopped;
                try { watcher.Stop(); }
                catch (Exception ex) when (IsUnavailable(ex)) { }
                finally
                {
                    try { watcher.Dispose(); }
                    catch (Exception ex) when (IsUnavailable(ex)) { }
                }
            }
        }
    }

    private static bool IsUnavailable(Exception ex) =>
        ex is ManagementException or UnauthorizedAccessException or SecurityException
            or COMException or InvalidOperationException or NotSupportedException;

    /// <summary>Signals shutdown without waiting on WMI/COM from the UI thread.
    /// Completion includes worker-owned watcher teardown.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _readings.Writer.TryComplete();
    }
}
