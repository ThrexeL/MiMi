using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MiMi.Core;

/// <summary>User preferences. Persisted as JSON under %LOCALAPPDATA%\MiMi\settings.json.</summary>
public sealed class AppSettings
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("version")] public int Version { get; set; } = CurrentVersion;

    // Layout
    [JsonPropertyName("horizontalOffsetDip")] public double HorizontalOffsetDip { get; set; } = 0;
    [JsonPropertyName("topMarginDip")] public double TopMarginDip { get; set; } = 6;
    [JsonPropertyName("scale")] public double Scale { get; set; } = 1.0;

    // Behavior
    [JsonPropertyName("animationSpeed")] public double AnimationSpeed { get; set; } = 1.0;
    [JsonPropertyName("collapseDelayMs")] public int CollapseDelayMs { get; set; } = 900;
    [JsonPropertyName("expandOnHover")] public bool ExpandOnHover { get; set; } = true;

    // Modules
    [JsonPropertyName("mediaModuleEnabled")] public bool MediaModuleEnabled { get; set; } = true;
    [JsonPropertyName("agentsModuleEnabled")] public bool AgentsModuleEnabled { get; set; } = true;
    [JsonPropertyName("clockEnabled")] public bool ClockEnabled { get; set; } = true;
    [JsonPropertyName("mascotEnabled")] public bool MascotEnabled { get; set; } = true;

    // Bridge (Phase 2 foundation, shipped enabled but inert until a client connects)
    [JsonPropertyName("bridgeEnabled")] public bool BridgeEnabled { get; set; } = true;
    [JsonPropertyName("approvalTtlMinutes")] public int ApprovalTtlMinutes { get; set; } = 5;

    // Startup
    [JsonPropertyName("startWithWindows")] public bool StartWithWindows { get; set; } = false;

    public static AppSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new AppSettings();
            var json = File.ReadAllText(path);
            var s = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
            return s ?? new AppSettings();
        }
        catch (Exception)
        {
            // Corrupt settings must never prevent startup; defaults win.
            return new AppSettings();
        }
    }

    public void Save(string path)
    {
        Version = CurrentVersion;
        Clamp();
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // Atomic replace so a crash mid-write can't corrupt settings.
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions(JsonOpts) { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true);
    }

    public void Clamp()
    {
        Scale = Math.Clamp(Scale, 0.75, 1.5);
        AnimationSpeed = Math.Clamp(AnimationSpeed, 0.5, 2.5);
        TopMarginDip = Math.Clamp(TopMarginDip, 0, 200);
        HorizontalOffsetDip = Math.Clamp(HorizontalOffsetDip, -800, 800);
        CollapseDelayMs = Math.Clamp(CollapseDelayMs, 200, 5000);
        ApprovalTtlMinutes = Math.Clamp(ApprovalTtlMinutes, 1, 60);
        if (double.IsNaN(Scale) || double.IsInfinity(Scale)) Scale = 1.0;
        if (double.IsNaN(AnimationSpeed) || double.IsInfinity(AnimationSpeed)) AnimationSpeed = 1.0;
    }

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}
