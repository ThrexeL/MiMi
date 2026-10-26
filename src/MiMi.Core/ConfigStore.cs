namespace MiMi.Core;

/// <summary>
/// Loads/saves <see cref="AppSettings"/> at the canonical user path and raises a change
/// notification whenever settings are swapped. Single-writer: the UI thread.
/// </summary>
public sealed class ConfigStore
{
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiMi");

    public static string DefaultPath => Path.Combine(DefaultDirectory, "settings.json");

    public AppSettings Current { get; private set; }

    private readonly string _path;
    private readonly object _gate = new();

    public event Action<AppSettings>? Changed;

    public ConfigStore(string? path = null)
    {
        _path = path ?? DefaultPath;
        Current = AppSettings.Load(_path);
    }

    public AppSettings Reload()
    {
        lock (_gate)
        {
            Current = AppSettings.Load(_path);
            Changed?.Invoke(Current);
            return Current;
        }
    }

    public void Update(Action<AppSettings> mutate, bool persist = true)
    {
        AppSettings snapshot;
        lock (_gate)
        {
            mutate(Current);
            Current.Clamp();
            if (persist) Current.Save(_path);
            snapshot = Current;
        }
        Changed?.Invoke(snapshot);
    }
}
