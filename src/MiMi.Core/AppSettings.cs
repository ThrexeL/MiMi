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
    /// <summary>"small", "default", or "large" — island typography only.</summary>
    [JsonPropertyName("textSize")] public string TextSize { get; set; } = "default";

    // Behavior
    [JsonPropertyName("animationSpeed")] public double AnimationSpeed { get; set; } = 1.0;
    [JsonPropertyName("collapseDelayMs")] public int CollapseDelayMs { get; set; } = 900;
    [JsonPropertyName("expandOnHover")] public bool ExpandOnHover { get; set; } = true;

    // Modules
    [JsonPropertyName("mediaModuleEnabled")] public bool MediaModuleEnabled { get; set; } = true;
    [JsonPropertyName("agentsModuleEnabled")] public bool AgentsModuleEnabled { get; set; } = true;
    [JsonPropertyName("clockEnabled")] public bool ClockEnabled { get; set; } = true;
    [JsonPropertyName("mascotEnabled")] public bool MascotEnabled { get; set; } = true;
    [JsonPropertyName("mascotAnimationsEnabled")] public bool MascotAnimationsEnabled { get; set; } = true;
    [JsonPropertyName("mascotReactionsEnabled")] public bool MascotReactionsEnabled { get; set; } = true;
    [JsonPropertyName("mascotSleepyEnabled")] public bool MascotSleepyEnabled { get; set; } = true;
    [JsonPropertyName("glowEnabled")] public bool GlowEnabled { get; set; } = true;
    [JsonPropertyName("glowIntensity")] public double GlowIntensity { get; set; } = 0.6;
    [JsonPropertyName("animationIntensity")] public double AnimationIntensity { get; set; } = 1.0;

    // Notifications (Phase 3.7). Conservative defaults; Windows toasts are opt-in.
    [JsonPropertyName("agentNotificationsEnabled")] public bool AgentNotificationsEnabled { get; set; } = true;
    [JsonPropertyName("completionNotificationsEnabled")] public bool CompletionNotificationsEnabled { get; set; } = true;
    [JsonPropertyName("approvalNotificationsEnabled")] public bool ApprovalNotificationsEnabled { get; set; } = true;
    [JsonPropertyName("windowsNotificationsWhenHidden")] public bool WindowsNotificationsWhenHidden { get; set; } = false;
    [JsonPropertyName("notificationDurationMs")] public int NotificationDurationMs { get; set; } = 3000;
    [JsonPropertyName("notificationSoundEnabled")] public bool NotificationSoundEnabled { get; set; } = false;

    // Bridge (Phase 2 foundation, shipped enabled but inert until a client connects)
    [JsonPropertyName("bridgeEnabled")] public bool BridgeEnabled { get; set; } = true;
    [JsonPropertyName("approvalTtlMinutes")] public int ApprovalTtlMinutes { get; set; } = 5;

    // Startup
    [JsonPropertyName("startWithWindows")] public bool StartWithWindows { get; set; } = false;

    // Desktop behavior
    [JsonPropertyName("desktopPersistVisible")] public bool DesktopPersistVisible { get; set; } = true;
    [JsonPropertyName("fullscreenSuppress")] public bool FullscreenSuppress { get; set; } = true;

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
        GlowIntensity = Math.Clamp(GlowIntensity, 0, 1);
        AnimationIntensity = Math.Clamp(AnimationIntensity, 0.5, 2);
        if (double.IsNaN(GlowIntensity) || double.IsInfinity(GlowIntensity)) GlowIntensity = 0.6;
        if (double.IsNaN(AnimationIntensity) || double.IsInfinity(AnimationIntensity)) AnimationIntensity = 1.0;
        TopMarginDip = Math.Clamp(TopMarginDip, 0, 200);
        HorizontalOffsetDip = Math.Clamp(HorizontalOffsetDip, -800, 800);
        CollapseDelayMs = Math.Clamp(CollapseDelayMs, 200, 5000);
        ApprovalTtlMinutes = Math.Clamp(ApprovalTtlMinutes, 1, 60);
        NotificationDurationMs = Math.Clamp(NotificationDurationMs, 1500, 10000);
        if (double.IsNaN(Scale) || double.IsInfinity(Scale)) Scale = 1.0;
        if (double.IsNaN(AnimationSpeed) || double.IsInfinity(AnimationSpeed)) AnimationSpeed = 1.0;
        TextSize = TextSize?.Trim().ToLowerInvariant() switch
        {
            "small" => "small",
            "large" => "large",
            _ => "default",
        };
    }

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}
