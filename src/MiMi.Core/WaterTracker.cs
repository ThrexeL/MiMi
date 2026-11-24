using System.Text.Json;

namespace MiMi.Core;

/// <summary>
/// Daily hydration tracking. Totals reset by local date; entries are a bounded local
/// history (today + a few days back). Persisted as JSON under the MiMi data directory.
/// Reminder scheduling (interval + working hours + snooze) is computed on demand —
/// no timers beyond the app's existing 1 Hz tick.
/// </summary>
public sealed class WaterTracker
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiMi", "water.json");

    public int DailyGoalMl { get; set; } = 1800;
    public int QuickAddMl { get; set; } = 250;
    /// <summary>Reminder interval in minutes; 0 disables reminders.</summary>
    public int ReminderIntervalMinutes { get; set; } = 0;
    public TimeSpan QuietBefore => new(22, 0, 0);
    public TimeSpan ActiveAfter => new(8, 0, 0);

    private DateTime _day = DateTime.Today;
    private int _todayMl;
    private readonly List<(DateTime Time, int Ml)> _todayEntries = new();
    private DateTimeOffset _lastReminderUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _snoozedUntil = DateTimeOffset.MinValue;

    public int TodayMl => _todayMl;
    public IReadOnlyList<(DateTime Time, int Ml)> TodayEntries => _todayEntries;
    public int RemainingMl => Math.Max(0, DailyGoalMl - _todayMl);
    public double GoalFraction => DailyGoalMl <= 0 ? 0 : Math.Clamp((double)_todayMl / DailyGoalMl, 0, 1);
    public bool GoalReached => _todayMl >= DailyGoalMl;

    public WaterTracker() { }

    public static WaterTracker Load(string path)
    {
        var tracker = new WaterTracker();
        try
        {
            if (!File.Exists(path)) return tracker;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("day", out var dayEl)
                || dayEl.ValueKind != JsonValueKind.String
                || !DateTime.TryParse(dayEl.GetString(), out var day))
                return tracker;

            tracker._day = day.Date;
            if (root.TryGetProperty("ml", out var ml)
                && ml.ValueKind == JsonValueKind.Number && ml.TryGetInt32(out var total) && total >= 0)
                tracker._todayMl = total;
            if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in entries.EnumerateArray().Take(40))
                {
                    if (e.ValueKind != JsonValueKind.Object
                        || !e.TryGetProperty("t", out var t) || t.ValueKind != JsonValueKind.String
                        || !DateTime.TryParse(t.GetString(), out var when)
                        || !e.TryGetProperty("ml", out var m) || m.ValueKind != JsonValueKind.Number
                        || !m.TryGetInt32(out var amount) || amount <= 0)
                        continue;
                    tracker._todayEntries.Add((when, amount));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or JsonException or InvalidOperationException or FormatException)
        {
            Logger.Warn($"Water: could not load '{path}' ({ex.GetType().Name}); starting with an empty tracker.");
        }
        tracker.RollbackIfNewDay(DateTime.Today);
        return tracker;
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var payload = new
        {
            day = _day.ToString("yyyy-MM-dd"),
            ml = _todayMl,
            entries = _todayEntries.Select(e => new { t = e.Time.ToString("O"), ml = e.Ml }),
        };
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(payload));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Add water; rolls the day over first if the local date changed.</summary>
    public void Add(int ml, DateTimeOffset? now = null)
    {
        if (ml <= 0) return;
        RollbackIfNewDay((now ?? DateTimeOffset.Now).LocalDateTime.Date);
        _todayMl += ml;
        _todayEntries.Add(((now ?? DateTimeOffset.Now).LocalDateTime, ml));
        if (_todayEntries.Count > 40) _todayEntries.RemoveAt(0); // bounded
    }

    /// <summary> Rolls totals back to zero when the local date has changed. </summary>
    public void RollbackIfNewDay(DateTime today)
    {
        if (_day == today) return;
        _day = today;
        _todayMl = 0;
        _todayEntries.Clear();
    }

    /// <summary>True when a hydration reminder should show right now.</summary>
    public bool ShouldRemindNow(DateTimeOffset now, bool goalNotificationsWanted = true)
    {
        if (ReminderIntervalMinutes <= 0) return false;
        if (GoalReached && !goalNotificationsWanted) return false;
        var localTime = now.LocalDateTime.TimeOfDay;
        if (localTime < ActiveAfter || localTime >= QuietBefore) return false;
        if (now < _snoozedUntil) return false;
        if (TodayMl >= DailyGoalMl) return false;
        return now - _lastReminderUtc >= TimeSpan.FromMinutes(ReminderIntervalMinutes);
    }

    public void MarkReminded(DateTimeOffset now) => _lastReminderUtc = now;

    public void Snooze(TimeSpan duration, DateTimeOffset now) => _snoozedUntil = now + duration;
}
