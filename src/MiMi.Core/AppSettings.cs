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
    /// <summary>Maximum hover-panel lifetime without pointer movement. 0 disables the cap.</summary>
    [JsonPropertyName("panelIdleTimeoutSeconds")] public int PanelIdleTimeoutSeconds { get; set; } = 30;

    // Modules
    [JsonPropertyName("mediaModuleEnabled")] public bool MediaModuleEnabled { get; set; } = true;
    [JsonPropertyName("agentsModuleEnabled")] public bool AgentsModuleEnabled { get; set; } = true;
    [JsonPropertyName("mascotEnabled")] public bool MascotEnabled { get; set; } = true;
    [JsonPropertyName("mascotAnimationsEnabled")] public bool MascotAnimationsEnabled { get; set; } = true;
    [JsonPropertyName("mascotReactionsEnabled")] public bool MascotReactionsEnabled { get; set; } = true;
    // Quiet-doze is opt-in: the brief rejects a two-minute doze as the default.
    [JsonPropertyName("mascotSleepyEnabled")] public bool MascotSleepyEnabled { get; set; } = false;
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

    // Media / lyrics
    [JsonPropertyName("lyricsEnabled")] public bool LyricsEnabled { get; set; } = true;
    [JsonPropertyName("lyricsOffsetMs")] public int LyricsOffsetMs { get; set; } = 0;

    // Local convenience modules. Clipboard capture is opt-in and session-only.
    [JsonPropertyName("clipboardHistoryEnabled")] public bool ClipboardHistoryEnabled { get; set; } = false;
    [JsonPropertyName("quickAccessHiddenPages")] public string[] QuickAccessHiddenPages { get; set; } = [];

    // AI assistants. Built-ins are detected on PATH; custom commands and API profiles are per-user.
    [JsonPropertyName("aiDefaultProvider")] public string AiDefaultProvider { get; set; } = "codex";
    [JsonPropertyName("aiCustomName")] public string AiCustomName { get; set; } = "Custom AI";
    [JsonPropertyName("aiCustomMode")] public string AiCustomMode { get; set; } = "command";
    [JsonPropertyName("aiCustomCommand")] public string AiCustomCommand { get; set; } = "";
    [JsonPropertyName("aiCustomArguments")] public string AiCustomArguments { get; set; } = "";
    [JsonPropertyName("aiApiPreset")] public string AiApiPreset { get; set; } = "grok";
    [JsonPropertyName("aiApiEndpoint")] public string AiApiEndpoint { get; set; } = "https://api.x.ai/v1";
    [JsonPropertyName("aiApiModel")] public string AiApiModel { get; set; } = "grok-4.7";
    [JsonPropertyName("aiApiSystemPrompt")] public string AiApiSystemPrompt { get; set; } =
        "You are a helpful assistant in MiMi. Answer clearly. You have no access to local files, commands, or tools in this API chat.";
    /// <summary>DPAPI-protected, current-user-only ciphertext. Plaintext API keys are never persisted.</summary>
    [JsonPropertyName("aiApiKeyProtected")] public string AiApiKeyProtected { get; set; } = "";

    // System HUDs (Milestone 6)
    [JsonPropertyName("volumeHudEnabled")] public bool VolumeHudEnabled { get; set; } = true;
    [JsonPropertyName("brightnessHudEnabled")] public bool BrightnessHudEnabled { get; set; } = true;
    [JsonPropertyName("batteryHudEnabled")] public bool BatteryHudEnabled { get; set; } = true;
    [JsonPropertyName("networkHudEnabled")] public bool NetworkHudEnabled { get; set; } = true;
    [JsonPropertyName("capsLockHudEnabled")] public bool CapsLockHudEnabled { get; set; } = true;
    [JsonPropertyName("headphoneHudEnabled")] public bool HeadphoneHudEnabled { get; set; } = true;

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
        LyricsOffsetMs = Math.Clamp(LyricsOffsetMs, -10000, 10000);
        AnimationIntensity = Math.Clamp(AnimationIntensity, 0.5, 2);
        if (double.IsNaN(GlowIntensity) || double.IsInfinity(GlowIntensity)) GlowIntensity = 0.6;
        if (double.IsNaN(AnimationIntensity) || double.IsInfinity(AnimationIntensity)) AnimationIntensity = 1.0;
        TopMarginDip = Math.Clamp(TopMarginDip, 0, 200);
        HorizontalOffsetDip = Math.Clamp(HorizontalOffsetDip, -800, 800);
        CollapseDelayMs = Math.Clamp(CollapseDelayMs, 200, 5000);
        PanelIdleTimeoutSeconds = Math.Clamp(PanelIdleTimeoutSeconds, 0, 120);
        var allowedPages = UtilityPinState.QuickAccessKinds;
        QuickAccessHiddenPages = (QuickAccessHiddenPages ?? [])
            .Select(page => page == UtilityPinState.Codex ? UtilityPinState.Ai : page)
            .Where(page => allowedPages.Contains(page, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        AiDefaultProvider = AiDefaultProvider?.Trim().ToLowerInvariant() switch
        {
            "claude" => "claude",
            "opencode" => "opencode",
            "grok" => "grok",
            "custom" => "custom",
            _ => "codex",
        };
        AiCustomName = string.IsNullOrWhiteSpace(AiCustomName) ? "Custom AI" : AiCustomName.Trim();
        if (AiCustomName.Length > 40) AiCustomName = AiCustomName[..40];
        AiCustomMode = AiCustomMode?.Trim().ToLowerInvariant() == "api" ? "api" : "command";
        AiCustomCommand = (AiCustomCommand ?? "").Trim();
        if (AiCustomCommand.Length > 1000) AiCustomCommand = AiCustomCommand[..1000];
        AiCustomArguments = (AiCustomArguments ?? "").Trim();
        if (AiCustomArguments.Length > 2000) AiCustomArguments = AiCustomArguments[..2000];
        AiApiPreset = AiApiPreset?.Trim().ToLowerInvariant() switch
        {
            "openai" => "openai",
            "openrouter" => "openrouter",
            "custom" => "custom",
            _ => "grok",
        };
        AiApiEndpoint = (AiApiEndpoint ?? "").Trim();
        if (AiApiEndpoint.Length > 2000) AiApiEndpoint = AiApiEndpoint[..2000];
        AiApiModel = (AiApiModel ?? "").Trim();
        if (AiApiModel.Length > 200) AiApiModel = AiApiModel[..200];
        AiApiSystemPrompt = string.IsNullOrWhiteSpace(AiApiSystemPrompt)
            ? "You are a helpful assistant in MiMi. You have no access to local files, commands, or tools."
            : AiApiSystemPrompt.Trim();
        if (AiApiSystemPrompt.Length > 4000) AiApiSystemPrompt = AiApiSystemPrompt[..4000];
        AiApiKeyProtected = (AiApiKeyProtected ?? "").Trim();
        if (AiApiKeyProtected.Length > 32768) AiApiKeyProtected = "";
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
