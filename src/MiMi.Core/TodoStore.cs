using System.Text.Json;

namespace MiMi.Core;

/// <summary>
/// Tiny local to-do list: create/complete/delete tasks with optional due times and
/// reminders. Persisted as JSON with corruption recovery (a broken file starts fresh
/// rather than blocking the app). Intentionally not a productivity suite.
/// </summary>
public sealed class TodoStore
{
    public sealed class TodoItem
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public string Title { get; set; } = "";
        public bool Done { get; set; }
        public DateTime? Due { get; set; }
        public bool ReminderFired { get; set; }
        public DateTime? SnoozedUntil { get; set; }
        public DateTime Created { get; init; } = DateTime.Now;
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiMi", "todos.json");

    private readonly List<TodoItem> _items = new();
    private readonly string _path;

    public TodoStore(string? path = null)
    {
        _path = path ?? DefaultPath;
        Load();
    }

    public IReadOnlyList<TodoItem> Items => _items;
    public IReadOnlyList<TodoItem> Open => _items.Where(t => !t.Done).ToList();
    public IReadOnlyList<TodoItem> Done => _items.Where(t => t.Done).ToList();
    public int OpenCount => _items.Count(t => !t.Done);

    public TodoItem Add(string title, DateTime? due = null)
    {
        title = (title ?? "").Trim();
        if (title.Length == 0) throw new ArgumentException("task title required", nameof(title));
        var item = new TodoItem { Title = title[..Math.Min(title.Length, 200)], Due = due };
        _items.Add(item);
        return item;
    }

    public bool Complete(Guid id)
    {
        var item = _items.FirstOrDefault(t => t.Id == id);
        if (item is null || item.Done) return false;
        item.Done = true;
        return true;
    }

    public bool Reopen(Guid id)
    {
        var item = _items.FirstOrDefault(t => t.Id == id);
        if (item is null || !item.Done) return false;
        item.Done = false;
        item.ReminderFired = false;
        return true;
    }

    public bool Delete(Guid id) => _items.RemoveAll(t => t.Id == id) > 0;

    public void ClearCompleted() => _items.RemoveAll(t => t.Done);

    /// <summary>Tasks due now (due time passed, not done, reminder not yet fired).</summary>
    public IReadOnlyList<TodoItem> DueNow(DateTime now) =>
        _items.Where(t => !t.Done && !t.ReminderFired && t.Due is { } d && d <= now
            && (t.SnoozedUntil is null || now >= t.SnoozedUntil)).ToList();

    public bool Snooze(Guid id, TimeSpan duration, DateTime now)
    {
        var item = _items.FirstOrDefault(t => t.Id == id);
        if (item is null || item.Done) return false;
        item.SnoozedUntil = now.Add(duration);
        item.ReminderFired = false;
        return true;
    }

    public void MarkReminded(Guid id)
    {
        var item = _items.FirstOrDefault(t => t.Id == id);
        if (item is not null) item.ReminderFired = true;
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var options = new JsonSerializerOptions { WriteIndented = true };
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_items, options));
        File.Move(tmp, _path, overwrite: true);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var items = JsonSerializer.Deserialize<List<TodoItem>>(File.ReadAllText(_path));
            if (items is not null)
            {
                foreach (var item in items.Where(i => i is not null))
                {
                    item.Title ??= "";
                    _items.Add(item);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or JsonException or InvalidOperationException or FormatException)
        {
            Logger.Warn($"To-do: could not load '{_path}' ({ex.GetType().Name}); starting with an empty list.");
            try { File.Copy(_path, _path + ".corrupt", overwrite: true); } catch { /* best effort */ }
        }
    }
}
