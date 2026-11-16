using System.Threading.Channels;
using MiMi.Core;
using Xunit;

namespace MiMi.Tests;

public class BrightnessMonitorLifetimeTests
{
    [Fact]
    public void DisabledStartupDoesNotCreateMonitorAndRepeatedEnableIsIdempotent()
    {
        using var harness = new Harness();
        harness.Lifetime.SetEnabled(false);
        Assert.Empty(harness.Monitors);
        harness.Lifetime.SetEnabled(true);
        harness.Lifetime.SetEnabled(true);
        Assert.Single(harness.Monitors);
        Assert.Equal(0, harness.Monitors[0].DisposeCount);
    }

    [Fact]
    public async Task RapidToggleWaitsForTeardownAndRejectsOldCallbacks()
    {
        using var harness = new Harness();
        harness.Lifetime.SetEnabled(true);
        var old = harness.Monitors[0];
        var queued = old.Emit(40);
        harness.Lifetime.SetEnabled(false);
        Assert.Equal(1, old.DisposeCount);
        harness.Lifetime.SetEnabled(true);
        harness.Lifetime.SetEnabled(false);
        harness.Lifetime.SetEnabled(true);
        Assert.Single(harness.Monitors); // Dispose alone is not proof of native teardown.

        await harness.RunNextAsync();
        await queued;
        Assert.Empty(harness.Published);
        old.Stopped.SetResult();
        await harness.RunNextAsync();
        await harness.Lifetime.TransitionCompletion;
        Assert.Equal(2, harness.Monitors.Count);
        Assert.Equal(1, old.DisposeCount);

        var stale = old.Emit(60);
        await harness.RunNextAsync();
        await stale;
        Assert.Empty(harness.Published);
        var fresh = harness.Monitors[1].Emit(70);
        await harness.RunNextAsync();
        await fresh;
        Assert.Equal(new[] { 70 }, harness.Published);
    }

    [Fact]
    public async Task LatestDisabledPreferencePreventsRestartAfterTeardown()
    {
        using var harness = new Harness();
        harness.Lifetime.SetEnabled(true);
        harness.Lifetime.SetEnabled(false);
        harness.Lifetime.SetEnabled(true);
        harness.Lifetime.SetEnabled(false);
        harness.Monitors[0].Stopped.SetResult();
        await harness.RunNextAsync();
        await harness.Lifetime.TransitionCompletion;
        Assert.Single(harness.Monitors);
    }

    [Fact]
    public async Task DisposeWhileRestartIsPendingPreventsRestartAndCallbacks()
    {
        using var harness = new Harness();
        harness.Lifetime.SetEnabled(true);
        var old = harness.Monitors[0];
        harness.Lifetime.SetEnabled(false);
        harness.Lifetime.SetEnabled(true);
        harness.Lifetime.Dispose();
        harness.Lifetime.Dispose();
        harness.Lifetime.SetEnabled(true);
        var stale = old.Emit(80);
        await harness.RunNextAsync();
        await stale;
        old.Stopped.SetResult();
        await harness.RunNextAsync();
        await harness.Lifetime.TransitionCompletion;
        Assert.Single(harness.Monitors);
        Assert.Equal(1, old.DisposeCount);
        Assert.Empty(harness.Published);
    }

    [Fact]
    public async Task FailedTeardownDoesNotAllowOverlappingReplacement()
    {
        using var harness = new Harness();
        harness.Lifetime.SetEnabled(true);
        harness.Lifetime.SetEnabled(false);
        harness.Lifetime.SetEnabled(true);
        harness.Monitors[0].Stopped.SetException(new InvalidOperationException("Teardown failed"));
        await harness.Lifetime.TransitionCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        harness.Lifetime.SetEnabled(false);
        harness.Lifetime.SetEnabled(true);
        Assert.Single(harness.Monitors);
    }

    [Fact]
    public void LifecycleMutationsVerifyOwnerThreadAccess()
    {
        var allowAccess = true;
        using var lifetime = new BrightnessMonitorLifetime(
            _ => throw new InvalidOperationException("Should not create"),
            action => { action(); return Task.CompletedTask; },
            () => { if (!allowAccess) throw new InvalidOperationException("Wrong thread"); },
            _ => { });
        allowAccess = false;
        Assert.Throws<InvalidOperationException>(() => lifetime.SetEnabled(true));
        Assert.Throws<InvalidOperationException>(() => lifetime.Dispose());
        allowAccess = true;
    }

    private sealed class FakeMonitor(Func<int, Task> callback) : IBrightnessMonitor
    {
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Completion => Stopped.Task;
        public int DisposeCount { get; private set; }
        public Task Emit(int percent) => callback(percent);
        public void Dispose() => DisposeCount++;
    }

    private sealed class Harness : IDisposable
    {
        private readonly Channel<(Action Action, TaskCompletionSource Completion)> _ui =
            Channel.CreateUnbounded<(Action, TaskCompletionSource)>();
        public List<FakeMonitor> Monitors { get; } = [];
        public List<int> Published { get; } = [];
        public BrightnessMonitorLifetime Lifetime { get; }

        public Harness()
        {
            Lifetime = new BrightnessMonitorLifetime(callback =>
            {
                // Creation must not precede any previous native teardown.
                Assert.All(Monitors, monitor => Assert.True(monitor.Completion.IsCompletedSuccessfully));
                var monitor = new FakeMonitor(callback);
                Monitors.Add(monitor);
                return monitor;
            }, action =>
            {
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _ui.Writer.TryWrite((action, completion));
                return completion.Task;
            }, () => { }, Published.Add);
        }

        public async Task RunNextAsync()
        {
            var work = await _ui.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            try { work.Action(); work.Completion.SetResult(); }
            catch (Exception ex) { work.Completion.SetException(ex); throw; }
        }

        public void Dispose()
        {
            Lifetime.Dispose();
            // Finish fake teardown without requiring a live dispatcher after test exit.
            _ui.Writer.TryComplete();
            foreach (var monitor in Monitors) monitor.Stopped.TrySetCanceled();
        }
    }
}
