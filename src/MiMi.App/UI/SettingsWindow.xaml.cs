using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MiMi.Agents;
using MiMi.App.Interop;
using MiMi.Core;

namespace MiMi.App.UI;

/// <summary>Categorized settings editor. Changes apply and persist immediately.</summary>
public partial class SettingsWindow : Window
{
    private readonly ConfigStore _config;
    private readonly Action<bool> _autoStartChanged;
    private readonly ProviderIntegrationInspector _providerInspector;
    private bool _loading = true;
    private bool _sliderPending;
    private bool _refreshingProviders;
    private bool _updatingAutoStart;
    private readonly DispatcherTimer _sliderSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };

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
        _sliderSaveTimer.Tick += (_, _) => SavePendingSlider();
        Load();
        Loaded += (_, _) =>
        {
            // Bound the entire window, including its non-client chrome, to the desktop work area.
            var workArea = SystemParameters.WorkArea;
            MinWidth = Math.Min(MinWidth, workArea.Width);
            MinHeight = Math.Min(MinHeight, workArea.Height);
            MaxWidth = workArea.Width;
            MaxHeight = workArea.Height;
            Width = Math.Min(Width, MaxWidth);
            Height = Math.Min(Height, MaxHeight);
            Left = Math.Clamp(Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - ActualWidth));
            Top = Math.Clamp(Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - ActualHeight));
            SettingsCategories.SelectedItem = SettingsCategories.Items[0];
            ((TabItem)SettingsCategories.Items[0]).Focus();
        };
        Closing += (_, _) => SavePendingSlider();
        Closed += (_, _) => _sliderSaveTimer.Stop();

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
        ExpandOnHoverToggle.IsChecked = s.ExpandOnHover;
        LyricsToggle.IsChecked = s.LyricsEnabled;
        ClipboardHistoryToggle.IsChecked = s.ClipboardHistoryEnabled;
        LyricsOffsetSlider.Value = s.LyricsOffsetMs;
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
        BrightnessHudToggle.IsChecked = s.BrightnessHudEnabled;
        VolumeHudToggle.IsChecked = s.VolumeHudEnabled;
        BatteryHudToggle.IsChecked = s.BatteryHudEnabled;
        NetworkHudToggle.IsChecked = s.NetworkHudEnabled;
        CapsLockHudToggle.IsChecked = s.CapsLockHudEnabled;
        HeadphoneHudToggle.IsChecked = s.HeadphoneHudEnabled;
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
        GlowLabel.Text = GlowSlider.Value.ToString("P0");
        LyricsOffsetLabel.Text = $"{LyricsOffsetSlider.Value:+0;-0;0} ms";
        FaceAnimLabel.Text = $"{FaceAnimSlider.Value:0.0\u00D7}";
        NotificationDurationLabel.Text = $"{NotificationDurationSlider.Value / 1000.0:0.0} s";
    }

    private void CaptureToSettings()
    {
        if (_loading) return;
        _sliderSaveTimer.Stop();
        _sliderPending = false;
        RefreshLabels();
        try
        {
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
                s.ExpandOnHover = ExpandOnHoverToggle.IsChecked == true;
                s.LyricsEnabled = LyricsToggle.IsChecked == true;
                s.ClipboardHistoryEnabled = ClipboardHistoryToggle.IsChecked == true;
                s.LyricsOffsetMs = (int)LyricsOffsetSlider.Value;
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
                s.BrightnessHudEnabled = BrightnessHudToggle.IsChecked == true;
                s.VolumeHudEnabled = VolumeHudToggle.IsChecked == true;
                s.BatteryHudEnabled = BatteryHudToggle.IsChecked == true;
                s.NetworkHudEnabled = NetworkHudToggle.IsChecked == true;
                s.CapsLockHudEnabled = CapsLockHudToggle.IsChecked == true;
                s.HeadphoneHudEnabled = HeadphoneHudToggle.IsChecked == true;
            });
            ShowSaved();
        }
        catch (Exception ex)
        {
            ShowSaveFailure(ex);
        }
    }

    private void ShowSaved()
    {
        SaveStatusText.Text = $"Saved locally · {DateTime.Now:HH:mm:ss}";
        SaveStatusText.ToolTip = "Changes are applied. Music module and agent bridge changes require a restart.";
    }

    private void ShowSaveFailure(Exception ex)
    {
        SaveStatusText.Text = "Could not save · change a setting to retry";
        SaveStatusText.ToolTip = ex.Message;
    }

    // Preview labels while dragging; coalesce keyboard changes and save track clicks too.
    private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        RefreshLabels();
        _sliderPending = true;
        _sliderSaveTimer.Stop();
        if (Mouse.LeftButton != MouseButtonState.Pressed) _sliderSaveTimer.Start();
    }

    private void SavePendingSlider()
    {
        _sliderSaveTimer.Stop();
        if (_sliderPending) CaptureToSettings();
    }

    private void Slider_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        => SavePendingSlider();

    private void Slider_MouseReleased(object sender, MouseButtonEventArgs e)
        => Dispatcher.BeginInvoke(new Action(SavePendingSlider), DispatcherPriority.Background);

    private void Slider_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => SavePendingSlider();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Let an open combo box consume Escape before treating it as a window dismissal.
        if (e.Key == Key.Escape && !TextSizeCombo.IsDropDownOpen)
        {
            Close();
            e.Handled = true;
        }
    }

    private void Checkbox_Changed(object sender, RoutedEventArgs e) => CaptureToSettings();

    private void AutoStart_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _updatingAutoStart) return;
        var enable = AutoStartToggle.IsChecked == true;
        if (!AutoStart.Set(enable))
        {
            _updatingAutoStart = true;
            AutoStartToggle.IsChecked = !enable;
            _updatingAutoStart = false;
            ShowSaveFailure(new IOException("Windows did not accept the Start with Windows change."));
            return;
        }
        try
        {
            _autoStartChanged(enable);
            _config.Update(s => s.StartWithWindows = enable, persist: true);
            ShowSaved();
        }
        catch (Exception ex)
        {
            AutoStart.Set(!enable);
            _autoStartChanged(!enable);
            _updatingAutoStart = true;
            AutoStartToggle.IsChecked = !enable;
            _updatingAutoStart = false;
            ShowSaveFailure(ex);
        }
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
        if (_refreshingProviders) return;
        _refreshingProviders = true;
        RefreshProvidersButton.IsEnabled = false;
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
        finally
        {
            _refreshingProviders = false;
            RefreshProvidersButton.IsEnabled = true;
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
