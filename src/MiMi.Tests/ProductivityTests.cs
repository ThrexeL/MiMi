using MiMi.Core;
using Xunit;

namespace MiMi.Tests;

public class PomodoroTimerTests
{
    [Fact]
    public void Starts_InFocusPhase()
    {
        var t = new PomodoroTimer();
        t.StartFocus();
        Assert.Equal(PomodoroPhase.Focus, t.Phase);
        Assert.True(t.IsRunning);
        Assert.InRange(t.Remaining(), TimeSpan.FromMinutes(24), TimeSpan.FromMinutes(25));
    }

    [Fact]
    public void Pause_Resumes_WithDeadlineAccuracy()
    {
        var t = new PomodoroTimer { FocusDuration = TimeSpan.FromMinutes(25) };
        t.StartFocus();
        t.Pause();
        Assert.True(t.IsPaused);
        Thread.Sleep(30); // paused time must not tick away
        t.Resume();
        var r = t.Remaining();
        Assert.InRange(r, TimeSpan.FromMinutes(24.9), TimeSpan.FromMinutes(25));
    }

    [Fact]
    public void PhaseCompletion_FiresOnce_ThenAutoAdvancesToBreak()
    {
        var t = new PomodoroTimer
        {
            FocusDuration = TimeSpan.FromMilliseconds(80),
            ShortBreakDuration = TimeSpan.FromMinutes(5),
        };
        var fired = new List<PomodoroPhase>();
        t.PhaseCompleted += p => fired.Add(p);

        t.StartFocus();
        Thread.Sleep(140);
        t.Tick();
        t.Tick(); // exactly once

        _ = Assert.Single(fired);
        Assert.Equal(PomodoroPhase.Focus, fired[0]);
        Assert.Equal(PomodoroPhase.ShortBreak, t.Phase);
    }

    [Fact]
    public void FourthCompletedFocus_LeadsToLongBreak()
    {
        var t = new PomodoroTimer
        {
            FocusDuration = TimeSpan.FromMilliseconds(40),
            ShortBreakDuration = TimeSpan.FromMilliseconds(20),
            LongBreakDuration = TimeSpan.FromMilliseconds(20),
        };
        t.PhaseCompleted += _ => { };
        for (var i = 0; i < 4; i++)
        {
            t.StartFocus();
            Thread.Sleep(70);
            t.Tick();
        }
        Assert.Equal(4, t.CompletedFocusSessions);
        // After the 4th focus, StartBreak chose the long break.
        Assert.Equal(PomodoroPhase.LongBreak, t.Phase);
    }

    [Fact]
    public void Reset_ClearsToIdle()
    {
        var t = new PomodoroTimer();
        t.StartFocus();
        t.Reset(clearSessions: true);
        Assert.Equal(PomodoroPhase.Idle, t.Phase);
        Assert.Equal(TimeSpan.Zero, t.Remaining());
        Assert.Equal(0, t.CompletedFocusSessions);
    }

    [Fact]
    public void Progress_ClampsToOne()
    {
        var t = new PomodoroTimer { FocusDuration = TimeSpan.FromMilliseconds(30) };
        t.StartFocus();
        Thread.Sleep(120);
        Assert.Equal(1.0, t.Progress());
    }
}

public class WaterTrackerTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"water-{Guid.NewGuid():N}.json");

    [Fact]
    public void Add_Accumulates_AndComputesRemaining()
    {
        var t = new WaterTracker { DailyGoalMl = 1800 };
        // Explicit timestamps: immune to the test crossing local midnight.
        var at = DateTimeOffset.Now.Date.AddHours(10);
        t.Add(250, at);
        t.Add(250, at.AddMinutes(5));
        Assert.Equal(500, t.TodayMl);
        Assert.Equal(1300, t.RemainingMl);
        Assert.False(t.GoalReached);
    }

    [Fact]
    public void GoalReached_AtOrAboveGoal()
    {
        var t = new WaterTracker { DailyGoalMl = 500 };
        t.Add(500, DateTimeOffset.Now.Date.AddHours(10));
        Assert.True(t.GoalReached);
        Assert.Equal(0, t.RemainingMl);
    }

    [Fact]
    public void RollbackIfNewDay_ClearsTotals()
    {
        var t = new WaterTracker();
        t.Add(1000, DateTimeOffset.Now);
        t.RollbackIfNewDay(DateTime.Today.AddDays(1));
        Assert.Equal(0, t.TodayMl);
        Assert.Empty(t.TodayEntries);
    }

    [Fact]
    public void Persistence_RoundTrips_TodayTotals()
    {
        var path = TempPath();
        try
        {
            var t = new WaterTracker();
            t.Add(750);
            t.Save(path);
            var loaded = WaterTracker.Load(path);
            Assert.Equal(750, loaded.TodayMl);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Persistence_YesterdaysFile_RollsOverToZero()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, $$"""
            { "day": "{{DateTime.Today.AddDays(-1):yyyy-MM-dd}}", "ml": 1500, "entries": [] }
            """);
            var loaded = WaterTracker.Load(path);
            Assert.Equal(0, loaded.TodayMl);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_ValidJsonWithInvalidSchema_StartsFresh()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, "{\"day\":42,\"ml\":\"bad\",\"entries\":[null]}");
            var loaded = WaterTracker.Load(path);
            Assert.Equal(0, loaded.TodayMl);
            Assert.Empty(loaded.TodayEntries);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Reminders_RespectInterval_Snooze_AndQuietHours()
    {
        var t = new WaterTracker { ReminderIntervalMinutes = 60 };
        var morning = DateTimeOffset.Now.Date.AddHours(10);
        Assert.True(t.ShouldRemindNow(morning));                 // never reminded yet
        t.MarkReminded(morning);
        Assert.False(t.ShouldRemindNow(morning.AddMinutes(30))); // interval not elapsed
        t.Snooze(TimeSpan.FromHours(2), morning);
        Assert.False(t.ShouldRemindNow(morning.AddMinutes(61))); // snoozed
        Assert.True(t.ShouldRemindNow(morning.AddHours(3)));

        // Quiet hours: after 22:00 local, no reminders.
        var night = DateTimeOffset.Now.Date.AddHours(23);
        var t2 = new WaterTracker { ReminderIntervalMinutes = 30 };
        Assert.False(t2.ShouldRemindNow(night));
    }

    [Fact]
    public void Reminders_Disabled_WhenIntervalZero()
    {
        var t = new WaterTracker { ReminderIntervalMinutes = 0 };
        Assert.False(t.ShouldRemindNow(DateTimeOffset.Now.Date.AddHours(10)));
    }
}

public class TodoStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"todo-{Guid.NewGuid():N}.json");

    public void Dispose() { try { File.Delete(_path); } catch { } }

    [Fact]
    public void Add_Complete_Delete_Lifecycle()
    {
        var store = new TodoStore(_path);
        var task = store.Add("Review the MiMi UI");
        Assert.Equal(1, store.OpenCount);

        Assert.True(store.Complete(task.Id));
        Assert.Equal(0, store.OpenCount);
        Assert.Single(store.Done);

        Assert.False(store.Complete(task.Id)); // double complete rejected
        Assert.True(store.Reopen(task.Id));
        Assert.Equal(1, store.OpenCount);

        Assert.True(store.Delete(task.Id));
        Assert.Equal(0, store.OpenCount);
    }

    [Fact]
    public void EmptyTitle_Rejected()
    {
        var store = new TodoStore(_path);
        Assert.Throws<ArgumentException>(() => store.Add("   "));
    }

    [Fact]
    public void Persistence_RoundTrips()
    {
        var store = new TodoStore(_path);
        var a = store.Add("task one");
        store.Add("task two", due: DateTime.Now.AddHours(2));
        store.Complete(a.Id);
        store.Save();

        var loaded = new TodoStore(_path);
        Assert.Equal(2, loaded.Items.Count);
        Assert.Single(loaded.Done);
        Assert.Equal("task two", loaded.Open[0].Title);
        Assert.NotNull(loaded.Open[0].Due);
    }

    [Fact]
    public void CorruptFile_StartsFresh()
    {
        File.WriteAllText(_path, "{ broken json !!");
        var store = new TodoStore(_path);
        Assert.Empty(store.Items);
        store.Add("still works");
        store.Save();
        Assert.True(File.Exists(_path));
    }

    [Fact]
    public void Load_ValidJsonWithInvalidSchema_StartsFresh()
    {
        File.WriteAllText(_path, "[null,{\"title\":null},{}]");
        var store = new TodoStore(_path);
        Assert.Equal(2, store.Items.Count);
        Assert.All(store.Items, item => Assert.NotNull(item.Title));
    }

    [Fact]
    public void DueNow_RespectsReminderFired()
    {
        var store = new TodoStore(_path);
        var task = store.Add("take a break", due: DateTime.Now.AddMinutes(-5));
        Assert.Single(store.DueNow(DateTime.Now));
        store.MarkReminded(task.Id);
        Assert.Empty(store.DueNow(DateTime.Now));
    }
}
