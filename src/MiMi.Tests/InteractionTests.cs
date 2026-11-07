using MiMi.Core;
using Xunit;

namespace MiMi.Tests;

/// <summary>
/// Context-aware island interactions (Phase 4.1): utility pin resolution
/// (hub > user pins > running timer) and the drag-away revert policy.
/// </summary>
public class UtilityPinStateTests
{
    [Fact]
    public void EmptyState_ResolvesToNull()
    {
        var state = new UtilityPinState();
        Assert.Null(state.ActiveUtility);
    }

    [Fact]
    public void HubOpen_WinsOverPinnedUtilities()
    {
        var state = new UtilityPinState();
        state.SetPin(UtilityPinState.Pomodoro, true);
        state.SetHub(true);
        Assert.Equal(UtilityPinState.Hub, state.ActiveUtility);

        state.SetHub(false);
        Assert.Equal(UtilityPinState.Pomodoro, state.ActiveUtility);
    }

    [Fact]
    public void PinOrder_PomodoroBeforeWaterBeforeTodoBeforeShelf()
    {
        var state = new UtilityPinState();
        state.SetPin(UtilityPinState.Shelf, true);
        state.SetPin(UtilityPinState.Todo, true);
        state.SetPin(UtilityPinState.Water, true);
        state.SetPin(UtilityPinState.Pomodoro, true);
        Assert.Equal(UtilityPinState.Pomodoro, state.ActiveUtility);

        state.SetPin(UtilityPinState.Pomodoro, false);
        Assert.Equal(UtilityPinState.Water, state.ActiveUtility);
        state.SetPin(UtilityPinState.Water, false);
        Assert.Equal(UtilityPinState.Todo, state.ActiveUtility);
        state.SetPin(UtilityPinState.Todo, false);
        Assert.Equal(UtilityPinState.Shelf, state.ActiveUtility);
    }

    [Fact]
    public void Toggle_IsIdempotentSemantics()
    {
        var state = new UtilityPinState();
        state.TogglePin(UtilityPinState.Water);
        Assert.True(state.IsPinned(UtilityPinState.Water));
        state.TogglePin(UtilityPinState.Water);
        Assert.False(state.IsPinned(UtilityPinState.Water));
    }

    [Fact]
    public void ClearUserPins_ClosesHubToo()
    {
        var state = new UtilityPinState();
        state.SetPin(UtilityPinState.Shelf, true);
        state.SetHub(true);
        state.ClearUserPins();
        Assert.Null(state.ActiveUtility);
        Assert.False(state.IsHubOpen);
    }

    [Fact]
    public void Changed_FiresOnlyOnTransitions()
    {
        var state = new UtilityPinState();
        var fired = 0;
        state.Changed += () => fired++;
        state.SetPin(UtilityPinState.Water, true);
        state.SetPin(UtilityPinState.Water, true); // no-op
        state.SetHub(true);
        state.SetHub(true); // no-op
        Assert.Equal(2, fired);
    }

    [Fact]
    public void OpenUtilityFromHub_TransitionsAtomicallyInSingleNotification()
    {
        var state = new UtilityPinState();
        state.SetHub(true);
        var notifications = 0;
        state.Changed += () => notifications++;

        state.OpenUtilityFromHub(UtilityPinState.Pomodoro);
        Assert.False(state.IsHubOpen);
        Assert.True(state.IsPinned(UtilityPinState.Pomodoro));
        Assert.Equal(UtilityPinState.Pomodoro, state.ActiveUtility);
        Assert.Equal(1, notifications);
    }
}

public class DragRevertPolicyTests
{
    [Fact]
    public void Enter_KeepsShelfActive_NoRevert()
    {
        var policy = new DragRevertPolicy();
        policy.Enter(DateTimeOffset.UtcNow);
        Assert.True(policy.DragActive);
        Assert.False(policy.ShouldRevert(DateTimeOffset.UtcNow.AddSeconds(1)));
    }

    [Fact]
    public void Leave_ThenGracePeriod_RetURNS_Revert()
    {
        var policy = new DragRevertPolicy();
        var t0 = DateTimeOffset.UtcNow;
        policy.Enter(t0);
        policy.Leave(t0);
        Assert.False(policy.ShouldRevert(t0.AddSeconds(1)));      // grace running
        Assert.True(policy.ShouldRevert(t0.AddSeconds(2)));       // 1.5 s grace elapsed
    }

    [Fact]
    public void ReEnter_CancelsPendingRevert()
    {
        var policy = new DragRevertPolicy();
        var t0 = DateTimeOffset.UtcNow;
        policy.Enter(t0);
        policy.Leave(t0);
        policy.Enter(t0.AddSeconds(1)); // came back before the grace elapsed
        Assert.False(policy.ShouldRevert(t0.AddSeconds(2)));
        Assert.True(policy.DragActive);
    }

    [Fact]
    public void Drop_EndsSession_NoRevert()
    {
        var policy = new DragRevertPolicy();
        var t0 = DateTimeOffset.UtcNow;
        policy.Enter(t0);
        policy.Dropped();
        Assert.False(policy.ShouldRevert(t0.AddSeconds(5)));
        Assert.False(policy.DragActive);
    }

    [Fact]
    public void Leave_WithoutEnter_IsNoOp()
    {
        var policy = new DragRevertPolicy();
        policy.Leave(DateTimeOffset.UtcNow);
        Assert.False(policy.ShouldRevert(DateTimeOffset.UtcNow.AddSeconds(10)));
    }
}
