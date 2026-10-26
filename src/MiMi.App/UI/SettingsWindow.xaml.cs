using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using MiMi.App.Interop;
using MiMi.Core;

namespace MiMi.App.UI;

/// <summary>Small functional settings editor. Changes apply live; persisted on close.</summary>
public partial class SettingsWindow : Window
{
    private readonly ConfigStore _config;
    private readonly Action<bool> _autoStartChanged;
    private bool _loading;

    public SettingsWindow(ConfigStore config, Action<bool> autoStartChanged)
    {
        _config = config;
        _autoStartChanged = autoStartChanged;
        InitializeComponent();
        Load();
    }

    private void Load()
    {
        _loading = true;
        var s = _config.Current;
        ScaleSlider.Value = s.Scale;
        SpeedSlider.Value = s.AnimationSpeed;
        DelaySlider.Value = s.CollapseDelayMs;
        MediaToggle.IsChecked = s.MediaModuleEnabled;
        AgentsToggle.IsChecked = s.AgentsModuleEnabled;
        ClockToggle.IsChecked = s.ClockEnabled;
        MascotToggle.IsChecked = s.MascotEnabled;
        GlowToggle.IsChecked = s.GlowEnabled;
        GlowSlider.Value = s.GlowIntensity;
        FaceAnimSlider.Value = s.AnimationIntensity;
        BridgeToggle.IsChecked = s.BridgeEnabled;
        AutoStartToggle.IsChecked = AutoStart.IsEnabled();
        RefreshLabels();
        _loading = false;
    }

    private void RefreshLabels()
    {
        ScaleLabel.Text = ScaleSlider.Value.ToString("0.00×");
        SpeedLabel.Text = SpeedSlider.Value.ToString("0.0×");
        DelayLabel.Text = $"{DelaySlider.Value:0} ms";
        GlowLabel.Text = $"{GlowSlider.Value:0}%";
        FaceAnimLabel.Text = $"{FaceAnimSlider.Value:0.0×}";
    }

    private void CaptureToSettings()
    {
        if (_loading) return;
        _config.Update(s =>
        {
            s.Scale = ScaleSlider.Value;
            s.AnimationSpeed = SpeedSlider.Value;
            s.CollapseDelayMs = (int)DelaySlider.Value;
            s.MediaModuleEnabled = MediaToggle.IsChecked == true;
            s.AgentsModuleEnabled = AgentsToggle.IsChecked == true;
            s.ClockEnabled = ClockToggle.IsChecked == true;
            s.MascotEnabled = MascotToggle.IsChecked == true;
            s.GlowEnabled = GlowToggle.IsChecked == true;
            s.GlowIntensity = GlowSlider.Value;
            s.AnimationIntensity = FaceAnimSlider.Value;
            s.BridgeEnabled = BridgeToggle.IsChecked == true;
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
