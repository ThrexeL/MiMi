using MiMi.Core;
using Xunit;

namespace MiMi.Tests;

/// <summary>
/// File shelf semantics: references only, validated paths, duplicate-safe, bounded,
/// persistent, and tolerant of missing originals (render-time check, not storage).
/// </summary>
public class FileShelfStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"shelf-{Guid.NewGuid():N}");
    private readonly string _storePath;

    public FileShelfStoreTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "design.png"), new string('x', 2500));
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "hello");
        _storePath = Path.Combine(_dir, "shelf.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void AddExisting_Files_WithSizes()
    {
        var store = new FileShelfStore(_storePath);
        var added = store.AddRange(new[] { Path.Combine(_dir, "design.png"), Path.Combine(_dir, "notes.txt") });
        Assert.Equal(2, added);
        Assert.Equal(2, store.Count);
        Assert.Equal(2500, store.Items[0].SizeBytes);
        Assert.False(store.Items[0].IsDirectory);
    }

    [Fact]
    public void NonExistent_Paths_Rejected()
    {
        var store = new FileShelfStore(_storePath);
        var added = store.AddRange(new[] { Path.Combine(_dir, "ghost.png"), "", "   " });
        Assert.Equal(0, added);
        Assert.Empty(store.Items);
    }

    [Fact]
    public void Duplicates_Rejected_CaseInsensitive()
    {
        var store = new FileShelfStore(_storePath);
        var file = Path.Combine(_dir, "notes.txt");
        store.AddRange(new[] { file });
        Assert.Equal(0, store.AddRange(new[] { file.ToUpperInvariant() }));
        Assert.Single(store.Items);
    }

    [Fact]
    public void Capacity_EvictsOldest()
    {
        var store = new FileShelfStore(_storePath);
        var paths = Enumerable.Range(0, 15)
            .Select(i => { var p = Path.Combine(_dir, $"f{i}.txt"); File.WriteAllText(p, "x"); return p; })
            .ToList();
        store.AddRange(paths);
        Assert.True(store.Count <= FileShelfStore.Capacity);
        Assert.Equal("f3.txt", Path.GetFileName(store.Items[0].Path)); // 15 - 12 = 3 oldest evicted
    }

    [Fact]
    public void Remove_DeletesReferenceNotOriginal()
    {
        var store = new FileShelfStore(_storePath);
        var file = Path.Combine(_dir, "notes.txt");
        store.AddRange(new[] { file });

        Assert.True(store.Remove(file));
        Assert.False(store.Remove(file)); // already gone
        Assert.True(File.Exists(file));   // original untouched
    }

    [Fact]
    public void Exists_Detects_MissingOriginals()
    {
        var store = new FileShelfStore(_storePath);
        var file = Path.Combine(_dir, "notes.txt");
        store.AddRange(new[] { file });
        File.Delete(file);
        Assert.False(store.Exists(store.Items[0]));
    }

    [Fact]
    public void Directories_AreShelved_WithoutSize()
    {
        var sub = Path.Combine(_dir, "Project files");
        Directory.CreateDirectory(sub);
        var store = new FileShelfStore(_storePath);
        store.AddRange(new[] { sub });
        Assert.True(store.Items[0].IsDirectory);
        Assert.Equal(0, store.Items[0].SizeBytes);
    }

    [Fact]
    public void Persistence_RoundTrips()
    {
        var store = new FileShelfStore(_storePath);
        store.AddRange(new[] { Path.Combine(_dir, "design.png") });
        store.Save();

        var loaded = new FileShelfStore(_storePath);
        Assert.Single(loaded.Items);
        Assert.Equal("design.png", Path.GetFileName(loaded.Items[0].Path));
    }

    [Fact]
    public void CorruptFile_StartsFresh()
    {
        File.WriteAllText(_storePath, "{ broken !!");
        var store = new FileShelfStore(_storePath);
        Assert.Empty(store.Items);
    }

    [Fact]
    public void Load_RejectsNullEntries_AndEnforcesCapacity()
    {
        var rows = Enumerable.Range(0, FileShelfStore.Capacity + 5)
            .Select(i => $"{{ \"path\": \"C:\\\\tmp\\\\f{i}.txt\" }}");
        File.WriteAllText(_storePath, "[null," + string.Join(',', rows) + "]");

        var store = new FileShelfStore(_storePath);
        Assert.Equal(FileShelfStore.Capacity, store.Count);
        Assert.All(store.Items, item => Assert.False(string.IsNullOrWhiteSpace(item.Path)));
    }

    [Fact]
    public void FormatSize_HumanReadable()
    {
        Assert.Equal("500 B", FileShelfStore.FormatSize(500));
        Assert.Equal("2.4 KB", FileShelfStore.FormatSize(2500));
        Assert.Equal("2.4 MB", FileShelfStore.FormatSize(2_500_000));
        Assert.Equal("1.1 GB", FileShelfStore.FormatSize(1_200_000_000));
        Assert.Equal("", FileShelfStore.FormatSize(0)); // directories
    }
}
