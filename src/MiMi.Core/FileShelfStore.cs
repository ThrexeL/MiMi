using System.Text.Json;

namespace MiMi.Core;

/// <summary>One shelf entry: a REFERENCE to an existing file or folder. MiMi never
/// copies, moves, or deletes the original.</summary>
public sealed class ShelfItem
{
    public string Path { get; init; } = "";
    public long SizeBytes { get; init; }
    public bool IsDirectory { get; init; }
    public DateTimeOffset AddedUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Temporary file shelf (Nusa-style): drag files in, retrieve them from anywhere.
/// Bounded (oldest evicted), duplicate-safe, persisted as JSON. Removing an entry
/// removes the reference only. Paths are validated at add time; existence is
/// re-checked at render time (moved/deleted originals render as missing).
/// </summary>
public sealed class FileShelfStore
{
    public const int Capacity = 12;
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiMi", "shelf.json");

    private readonly List<ShelfItem> _items = new();
    private readonly string _path;

    public FileShelfStore(string? path = null)
    {
        _path = path ?? DefaultPath;
        Load();
    }

    public IReadOnlyList<ShelfItem> Items => _items;
    public int Count => _items.Count;

    public bool Exists(ShelfItem item) =>
        File.Exists(item.Path) || Directory.Exists(item.Path);

    /// <summary>Adds existing files/folders. Returns the number actually added
    /// (non-existent paths and duplicates are rejected). Oldest entries evict
    /// beyond <see cref="Capacity"/>.</summary>
    public int AddRange(IEnumerable<string> paths)
    {
        var added = 0;
        foreach (var raw in paths)
        {
            var path = (raw ?? "").Trim();
            if (path.Length == 0 || path.Length > 500) continue;
            if (_items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)))
                continue;

            var isDir = Directory.Exists(path);
            var isFile = File.Exists(path);
            if (!isDir && !isFile) continue; // validate: only real files/folders are shelved

            var size = isFile ? new FileInfo(path).Length : 0;
            _items.Add(new ShelfItem { Path = path, SizeBytes = size, IsDirectory = isDir });
            added++;
        }

        while (_items.Count > Capacity)
            _items.RemoveAt(0);
        return added;
    }

    public bool Remove(string path) => _items.RemoveAll(i =>
        string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)) > 0;

    public void Clear() => _items.Clear();

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:0.#} GB",
        >= 1_048_576 => $"{bytes / 1_048_576.0:0.#} MB",
        >= 1024 => $"{bytes / 1024.0:0.#} KB",
        > 0 => $"{bytes} B",
        _ => "",
    };

    public void Save()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_items));
        File.Move(tmp, _path, overwrite: true);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var items = JsonSerializer.Deserialize<List<ShelfItem>>(File.ReadAllText(_path));
            if (items is not null) _items.AddRange(items);
        }
        catch (JsonException)
        {
            try { File.Copy(_path, _path + ".corrupt", overwrite: true); } catch { /* best effort */ }
        }
    }
}
