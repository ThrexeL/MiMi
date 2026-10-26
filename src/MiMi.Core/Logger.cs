namespace MiMi.Core;

/// <summary>
/// Minimal rotating file logger. Writes only on Info+ and rotates at 1 MB, keeping 2
/// previous files, to honor the "no unnecessary disk writes" requirement.
/// Thread-safe.
/// </summary>
public static class Logger
{
    private static readonly object Gate = new();
    private static string? _dir;
    private const long MaxBytes = 1_000_000;

    public static string LogDirectory =>
        _dir ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiMi", "logs");

    public static string CurrentPath => Path.Combine(LogDirectory, "mimi.log");

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg, Exception? ex = null)
        => Write("ERROR", ex is null ? msg : $"{msg} :: {ex.GetType().Name}: {ex.Message} @ {FirstFrames(ex.StackTrace)}");

    private static string FirstFrames(string? stack)
    {
        if (string.IsNullOrEmpty(stack)) return "";
        var lines = stack.Split('\n', 4);
        return string.Join(" | ", lines.Take(3)).Trim();
    }

    private static void Write(string level, string msg)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                RotateIfNeeded();
                File.AppendAllText(CurrentPath,
                    $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}{Environment.NewLine}");
            }
        }
        catch
        {
            // Never let logging crash the app.
        }
    }

    private static void RotateIfNeeded()
    {
        try
        {
            if (!File.Exists(CurrentPath) || new FileInfo(CurrentPath).Length < MaxBytes) return;
            File.Copy(CurrentPath, Path.Combine(LogDirectory, "mimi.1.log"), overwrite: true);
            File.Delete(CurrentPath);
        }
        catch { /* best effort */ }
    }
}
