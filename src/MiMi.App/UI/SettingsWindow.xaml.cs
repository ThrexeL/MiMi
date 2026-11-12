using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using MiMi.Agents;
using MiMi.App.Interop;
using MiMi.Core;

namespace MiMi.App.UI;

/// <summary>Small functional settings editor. Changes apply live; persisted on close.</summary>
public partial class SettingsWindow : Window
{
    private readonly ConfigStore _config;
    private readonly Action<bool> _autoStartChanged;
    private readonly ProviderIntegrationInspector _providerInspector;
    private bool _loading;

    private readonly Func<bool> _hotkeyStatus;

    private readonly StatusIndicatorViewModel? _impressiveBot;

    public SettingsWindow(ConfigStore config, Action<bool> autoStartChanged, Func<bool>? hotkeyStatus = null,
        ProviderIntegrationInspector? providerInspector = null, IslandPresenter? presenter = null)
    {
        _config = config;
        _autoStartChanged = autoStartChanged;
        _hotkeyStatus = hotkeyStatus ?? (() => true);
        _providerInspector = providerInspector ?? new ProviderIntegrationInspector();
        InitializeComponent();
        Load();

        // Live ImpressiveBot tile: same source as the island, so the two always agree.
        if (presenter is not null)
        {
            _impressiveBot = StatusIndicatorViewModel.ForPresenter(presenter);
            ImpressiveBotStatusPanel.DataContext = _impressiveBot;
            Closed += (_, _) => _impressiveBot.Stop();
        }
        else
        {
            ImpressiveBotStatusPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void Load()
    {
        _loading = true;
        var s = _config.Current;
        ScaleSlider.Value = s.Scale;
        TextSizeCombo.SelectedIndex = s.TextSize switch
        {
            "small" => 0,
            "large" => 2,
            _ => 1,
        };
        SpeedSlider.Value = s.AnimationSpeed;
        DelaySlider.Value = s.CollapseDelayMs;
        MediaToggle.IsChecked = s.MediaModuleEnabled;
        AgentsToggle.IsChecked = s.AgentsModuleEnabled;
        MascotToggle.IsChecked = s.MascotEnabled;
        MascotAnimationsToggle.IsChecked = s.MascotAnimationsEnabled;
        MascotReactionsToggle.IsChecked = s.MascotReactionsEnabled;
        MascotSleepyToggle.IsChecked = s.MascotSleepyEnabled;
        GlowToggle.IsChecked = s.GlowEnabled;
        GlowSlider.Value = s.GlowIntensity;
        FaceAnimSlider.Value = s.AnimationIntensity;
        BridgeToggle.IsChecked = s.BridgeEnabled;
        AgentNotificationsToggle.IsChecked = s.AgentNotificationsEnabled;
        CompletionNotificationsToggle.IsChecked = s.CompletionNotificationsEnabled;
        ApprovalNotificationsToggle.IsChecked = s.ApprovalNotificationsEnabled;
        WindowsNotificationsToggle.IsChecked = s.WindowsNotificationsWhenHidden;
        NotificationSoundToggle.IsChecked = s.NotificationSoundEnabled;
        NotificationDurationSlider.Value = s.NotificationDurationMs;
        AutoStartToggle.IsChecked = AutoStart.IsEnabled();
        DesktopPersistToggle.IsChecked = s.DesktopPersistVisible;
        FullscreenToggle.IsChecked = s.FullscreenSuppress;
        HotkeyStatusText.Text = _hotkeyStatus()
            ? "Global toggle shortcut: Ctrl + Alt + M  (registered)"
            : "Global toggle shortcut: Ctrl + Alt + M  (unavailable \u2014 another program owns it)";
        RefreshLabels();
        _loading = false;
        _ = RefreshProvidersAsync();
    }

    private void RefreshLabels()
    {
        ScaleLabel.Text = ScaleSlider.Value.ToString("0.00\u00D7");
        SpeedLabel.Text = SpeedSlider.Value.ToString("0.0\u00D7");
        DelayLabel.Text = $"{DelaySlider.Value:0} ms";
        GlowLabel.Text = $"{GlowSlider.Value:0}%";
        FaceAnimLabel.Text = $"{FaceAnimSlider.Value:0.0\u00D7}";
        NotificationDurationLabel.Text = $"{NotificationDurationSlider.Value / 1000.0:0.0} s";
    }

    private void CaptureToSettings()
    {
        if (_loading) return;
        _config.Update(s =>
        {
            s.Scale = ScaleSlider.Value;
            s.TextSize = TextSizeCombo.SelectedIndex switch
            {
                0 => "small",
                2 => "large",
                _ => "default",
            };
            s.AnimationSpeed = SpeedSlider.Value;
            s.CollapseDelayMs = (int)DelaySlider.Value;
            s.MediaModuleEnabled = MediaToggle.IsChecked == true;
            s.AgentsModuleEnabled = AgentsToggle.IsChecked == true;
            s.MascotEnabled = MascotToggle.IsChecked == true;
            s.MascotAnimationsEnabled = MascotAnimationsToggle.IsChecked == true;
            s.MascotReactionsEnabled = MascotReactionsToggle.IsChecked == true;
            s.MascotSleepyEnabled = MascotSleepyToggle.IsChecked == true;
            s.GlowEnabled = GlowToggle.IsChecked == true;
            s.GlowIntensity = GlowSlider.Value;
            s.AnimationIntensity = FaceAnimSlider.Value;
            s.BridgeEnabled = BridgeToggle.IsChecked == true;
            s.AgentNotificationsEnabled = AgentNotificationsToggle.IsChecked == true;
            s.CompletionNotificationsEnabled = CompletionNotificationsToggle.IsChecked == true;
            s.ApprovalNotificationsEnabled = ApprovalNotificationsToggle.IsChecked == true;
            s.WindowsNotificationsWhenHidden = WindowsNotificationsToggle.IsChecked == true;
            s.NotificationSoundEnabled = NotificationSoundToggle.IsChecked == true;
            s.NotificationDurationMs = (int)NotificationDurationSlider.Value;
            s.DesktopPersistVisible = DesktopPersistToggle.IsChecked == true;
            s.FullscreenSuppress = FullscreenToggle.IsChecked == true;
        });
        RefreshLabels();
    }

    // Sliders: persist when the user releases the thumb (not on every pixel).
    private void Slider_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        => CaptureToSettings();

    private void Checkbox_Changed(object sender, RoutedEventArgs e) => CaptureToSettings();

    private void AutoStart_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var enable = AutoStartToggle.IsChecked == true;
        _autoStartChanged(enable);
        _config.Update(s => s.StartWithWindows = enable, persist: true);
    }

    private void Checkbox_Changed2(object sender, RoutedEventArgs e)
    {
        // Identify which checkbox was clicked based on event source name
        var chk = sender as CheckBox;
        if (chk != null)
        {
            switch (chk.Name)
            {
                case "MascotAnimationsToggle":
                case "MascotReactionsToggle":
                case "MascotSleepyToggle":
                case "AgentNotificationsToggle":
                case "CompletionNotificationsToggle":
                case "ApprovalNotificationsToggle":
                case "WindowsNotificationsToggle":
                case "NotificationSoundToggle":
                case "DesktopPersistToggle":
                case "FullscreenToggle":
                    CaptureToSettings();
                    break;
                default:
                    CaptureToSettings();
                    break;
            }
        }
    }

    private void TextSize_Changed(object sender, SelectionChangedEventArgs e) => CaptureToSettings();

    private async void RefreshProviders_Click(object sender, RoutedEventArgs e) => await RefreshProvidersAsync();

    private async Task RefreshProvidersAsync()
    {
        ProviderCheckedText.Text = "checking\u2026";
        try
        {
            var statuses = await Task.Run(() => _providerInspector.InspectAll(includeVersions: true));
            var claude = statuses.First(s => s.Provider == "claude-code");
            var codex = statuses.First(s => s.Provider == "codex");
            ApplyProvider("Claude Code", claude, ClaudeProviderStatus, ClaudeProviderDetail);
            ApplyProvider("Codex", codex, CodexProviderStatus, CodexProviderDetail);
            ProviderCheckedText.Text = $"checked {DateTime.Now:HH:mm}";
            ProviderHintText.Text = codex.IsHealthy
                ? "Codex: trust is maintained by Codex itself; if approvals do not appear, open /hooks and review the MiMi entries."
                : codex.Instructions;
        }
        catch (Exception ex)
        {
            ProviderCheckedText.Text = "check failed";
            ProviderHintText.Text = ex.Message;
        }
    }

    private static void ApplyProvider(string displayName, ProviderIntegrationStatus status,
        TextBlock statusText, TextBlock detailText)
    {
        statusText.Text = $"{displayName} \u2014 {status.Summary}";
        var version = string.IsNullOrEmpty(status.Version) ? "" : $" \u00B7 {status.Version}";
        var hooks = status.ExpectedHooks > 0
            ? $" \u00B7 hooks {status.InstalledHooks}/{status.ExpectedHooks}"
            : "";
        detailText.Text = status.Detail + version + hooks;
        detailText.ToolTip = $"{status.ConfigPath}\n{status.ExecutablePath}\n{status.Instructions}";
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Logger.LogDirectory,
                UseShellExecute = true,
            });
        }
        catch { /* folder may not exist yet */ }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
