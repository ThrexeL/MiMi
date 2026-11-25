using System.IO;
using System.Text.Json;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MiMi.App.AI;
using MiMi.App.UI;
using MiMi.Core;
using Xunit;

namespace MiMi.Tests;

[Collection("WPF UI")]
public class SettingsWindowTests
{
    [Fact]
    public void Categories_KeepControlsAndFooterWithinBoundedLayout()
    {
        OnSta(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
            var window = new SettingsWindow(new ConfigStore(path), _ => { });
            try
            {
                Assert.False(File.Exists(path)); // Opening settings must not save partially initialized controls.
                var categories = (TabControl)window.FindName("SettingsCategories");
                Assert.Equal(5, categories.Items.Count);
                foreach (var size in new[] { new Size(600, 420), new Size(840, 700) })
                {
                    foreach (TabItem category in categories.Items)
                    {
                        categories.SelectedItem = category;
                        var scroll = Assert.IsType<ScrollViewer>(category.Content);
                        scroll.Width = Math.Max(320, size.Width - 48);
                        scroll.Height = Math.Max(180, size.Height - 190);
                        scroll.Measure(new Size(scroll.Width, double.PositiveInfinity));
                        scroll.Arrange(new Rect(0, 0, scroll.Width, scroll.Height));
                        scroll.UpdateLayout();
                        Assert.Equal(ScrollBarVisibility.Auto, scroll.VerticalScrollBarVisibility);
                        Assert.InRange(scroll.ActualHeight, 1, size.Height);
                        Render(scroll, "settings-" + (category.Header?.ToString()?.TrimStart('_').ToLowerInvariant() ?? "category"));
                    }
                }
                Assert.IsType<Button>(window.FindName("CloseButton"));
                foreach (var name in new[] { "MediaToggle", "AgentsToggle", "MascotToggle", "GlowToggle",
                    "BridgeToggle", "AutoStartToggle", "DesktopPersistToggle", "FullscreenToggle",
                    "MascotAnimationsToggle", "MascotReactionsToggle", "MascotSleepyToggle",
                    "AgentNotificationsToggle", "CompletionNotificationsToggle", "ApprovalNotificationsToggle",
                    "WindowsNotificationsToggle", "NotificationSoundToggle", "BrightnessHudToggle",
                                        "ExpandOnHoverToggle", "LyricsToggle", "VolumeHudToggle", "BatteryHudToggle",
                                        "NetworkHudToggle", "CapsLockHudToggle", "HeadphoneHudToggle" })
                    Assert.IsType<CheckBox>(window.FindName(name));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void PanelIdleAndQuickAccessVisibility_LoadAndSave()
    {
        OnSta(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
            new AppSettings
            {
                PanelIdleTimeoutSeconds = 45,
                QuickAccessHiddenPages = [UtilityPinState.Water, UtilityPinState.Codex, "unknown-page"],
            }.Save(path);
            var window = new SettingsWindow(new ConfigStore(path), _ => { });
            try
            {
                Assert.Equal(45, ((Slider)window.FindName("PanelIdleTimeoutSlider")).Value);
                Assert.False(((CheckBox)window.FindName("PageWaterToggle")).IsChecked);
                Assert.False(((CheckBox)window.FindName("PageAiToggle")).IsChecked);
                Assert.True(((CheckBox)window.FindName("PageFocusToggle")).IsChecked);
                Assert.False(((Button)window.FindName("RestartButton")).IsEnabled);

                ((Slider)window.FindName("PanelIdleTimeoutSlider")).Value = 60;
                ((CheckBox)window.FindName("PageWaterToggle")).IsChecked = true;
                var saved = AppSettings.Load(path);
                Assert.Equal(60, saved.PanelIdleTimeoutSeconds);
                Assert.DoesNotContain(UtilityPinState.Water, saved.QuickAccessHiddenPages);
                Assert.Contains(UtilityPinState.Ai, saved.QuickAccessHiddenPages);
                Assert.DoesNotContain("unknown-page", saved.QuickAccessHiddenPages);

                var happy = Assert.IsType<Button>(window.FindName("PreviewHappyButton"));
                happy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(MiMi.App.UI.Mascot.MascotExpression.Happy,
                    ((MiMi.App.UI.Mascot.MiMiMascotControl)window.FindName("MascotPreview")).Expression);
            }
            finally
            {
                window.Close();
                File.Delete(path);
            }
        });
    }

    [Fact]
    public void AiConfiguration_LoadsSavesAndReportsCustomCommand()
    {
        OnSta(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
            var command = Path.Combine(Path.GetTempPath(), $"mimi-settings-ai-{Guid.NewGuid():N}.cmd");
            File.WriteAllText(command, "@echo off\r\n");
            new AppSettings
            {
                AiDefaultProvider = "custom",
                AiCustomName = "Portable AI",
                AiCustomCommand = command,
                AiCustomArguments = "--read-only",
            }.Save(path);
            var store = new ConfigStore(path);
            var service = new AiProviderService(store.Current);
            var window = new SettingsWindow(store, _ => { }, aiProviders: service);
            try
            {
                Assert.True(window.ShowInTaskbar);
                Assert.NotNull(window.Icon);
                Assert.True(window.Icon.Width >= 32);
                Assert.Equal(4, ((ComboBox)window.FindName("AiDefaultCombo")).SelectedIndex);
                Assert.Equal("Portable AI", ((TextBox)window.FindName("AiCustomNameInput")).Text);
                Assert.Contains("Portable AI: Ready", ((TextBlock)window.FindName("AiAvailabilityText")).Text);

                ((TextBox)window.FindName("AiCustomNameInput")).Text = "Friend's Assistant";
                ((TextBox)window.FindName("AiCustomArgumentsInput")).Text = "--safe";
                var saved = AppSettings.Load(path);
                Assert.Equal("Friend's Assistant", saved.AiCustomName);
                Assert.Equal("--safe", saved.AiCustomArguments);
            }
            finally
            {
                window.Close();
                File.Delete(path);
                File.Delete(command);
            }
        });
    }

    [Fact]
    public void Changes_SaveImmediatelyAndSliderFocusLossFlushesPendingValue()
    {
        OnSta(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
            var store = new ConfigStore(path);
            var window = new SettingsWindow(store, _ => { });
            try
            {
                var media = (CheckBox)window.FindName("MediaToggle");
                media.IsChecked = !store.Current.MediaModuleEnabled;
                Assert.Equal(media.IsChecked == true, AppSettings.Load(path).MediaModuleEnabled);
                Assert.StartsWith("Saved locally", ((TextBlock)window.FindName("SaveStatusText")).Text);

                var scale = (Slider)window.FindName("ScaleSlider");
                scale.Value = 1.25;
                scale.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, scale, null)
                    { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
                Assert.Equal(1.25, AppSettings.Load(path).Scale);
                Assert.Contains("%", ((TextBlock)window.FindName("GlowLabel")).Text);
            }
            finally
            {
                window.Close();
                File.Delete(path);
            }
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BrightnessToggle_LoadsAndPersistsValue(bool enabled)
    {
        OnSta(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
            Assert.True(new AppSettings().BrightnessHudEnabled);
            new AppSettings { BrightnessHudEnabled = enabled }.Save(path);
            var window = new SettingsWindow(new ConfigStore(path), _ => { });
            try
            {
                var toggle = (CheckBox)window.FindName("BrightnessHudToggle");
                Assert.True(toggle.IsEnabled);
                Assert.Equal(enabled, toggle.IsChecked);
                toggle.IsChecked = !enabled;
                Assert.Equal(!enabled, AppSettings.Load(path).BrightnessHudEnabled);
                Assert.StartsWith("Saved locally", ((TextBlock)window.FindName("SaveStatusText")).Text);
            }
            finally
            {
                window.Close();
                File.Delete(path);
            }
        });
    }

    [Fact]
    public void ConfigWriteFailure_ReplacesPreviousSaveSuccessWithFailure()
    {
        OnSta(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
            var window = new SettingsWindow(new ConfigStore(path), _ => { });
            try
            {
                ((CheckBox)window.FindName("BrightnessHudToggle")).IsChecked = false;
                var feedback = (TextBlock)window.FindName("SaveStatusText");
                Assert.StartsWith("Saved locally", feedback.Text);
                var savedJson = File.ReadAllText(path);

                // Block the atomic-write staging path without relying on OS-specific permissions.
                Directory.CreateDirectory(path + ".tmp");
                var toggle = (CheckBox)window.FindName("BrightnessHudToggle");
                toggle.IsChecked = toggle.IsChecked != true;
                Assert.StartsWith("Could not save", feedback.Text);
                Assert.False(string.IsNullOrWhiteSpace(feedback.ToolTip as string));
                Assert.Equal(savedJson, File.ReadAllText(path));

                Directory.Delete(path + ".tmp");
                toggle.IsChecked = toggle.IsChecked != true;
                Assert.StartsWith("Saved locally", feedback.Text);
            }
            finally
            {
                window.Close();
                if (Directory.Exists(path + ".tmp")) Directory.Delete(path + ".tmp");
                File.Delete(path);
            }
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NewToggles_LoadAndSaveWithoutOverwritingOtherPreferences(bool enabled)
    {
        OnSta(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
            var expected = new AppSettings
            {
                LyricsEnabled = enabled, ExpandOnHover = !enabled, VolumeHudEnabled = enabled,
                BatteryHudEnabled = !enabled, NetworkHudEnabled = enabled, CapsLockHudEnabled = !enabled,
                HeadphoneHudEnabled = enabled, BrightnessHudEnabled = false, LyricsOffsetMs = -2300,
                CollapseDelayMs = 5000, NotificationDurationMs = 10000, ApprovalTtlMinutes = 17,
                HorizontalOffsetDip = 123, TopMarginDip = 23, Scale = 1.35, TextSize = "large",
            };
            expected.Save(path);
            var original = File.ReadAllText(path);
            var window = new SettingsWindow(new ConfigStore(path), _ => { });
            try
            {
                Assert.Equal(original, File.ReadAllText(path));
                var mappings = new[]
                {
                    ("LyricsToggle", nameof(AppSettings.LyricsEnabled)),
                    ("ExpandOnHoverToggle", nameof(AppSettings.ExpandOnHover)),
                    ("VolumeHudToggle", nameof(AppSettings.VolumeHudEnabled)),
                    ("BatteryHudToggle", nameof(AppSettings.BatteryHudEnabled)),
                    ("NetworkHudToggle", nameof(AppSettings.NetworkHudEnabled)),
                    ("CapsLockHudToggle", nameof(AppSettings.CapsLockHudEnabled)),
                    ("HeadphoneHudToggle", nameof(AppSettings.HeadphoneHudEnabled)),
                };
                foreach (var (controlName, propertyName) in mappings)
                {
                    var toggle = (CheckBox)window.FindName(controlName);
                    var property = typeof(AppSettings).GetProperty(propertyName)!;
                    var previous = (bool)property.GetValue(expected)!;
                    Assert.Equal(previous, toggle.IsChecked);
                    toggle.ApplyTemplate();
                    var peer = new CheckBoxAutomationPeer(toggle);
                    Assert.Equal(AutomationControlType.CheckBox, peer.GetAutomationControlType());
                    Assert.Equal(toggle.Content.ToString(), peer.GetName());
                    ((IToggleProvider)peer.GetPattern(PatternInterface.Toggle)).Toggle();
                    property.SetValue(expected, !previous);
                    Assert.Equal(JsonSerializer.Serialize(expected, AppSettings.JsonOpts),
                        JsonSerializer.Serialize(AppSettings.Load(path), AppSettings.JsonOpts));
                    var glyph = (System.Windows.Shapes.Path)toggle.Template.FindName("CheckGlyph", toggle);
                    Assert.Equal(!previous ? Visibility.Visible : Visibility.Collapsed, glyph.Visibility);
                }
            }
            finally
            {
                window.Close();
                File.Delete(path);
            }
        });
    }

    [Theory]
    [InlineData(-12000, -10000)]
    [InlineData(-2500, -2500)]
    [InlineData(0, 0)]
    [InlineData(2700, 2700)]
    [InlineData(12000, 10000)]
    public void LyricsOffset_SavesSignedMillisecondsWithinExistingClamps(int requested, int expectedOffset)
    {
        OnSta(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
            var expected = new AppSettings { LyricsOffsetMs = 1000, LyricsEnabled = false, ApprovalTtlMinutes = 19 };
            expected.Save(path);
            var window = new SettingsWindow(new ConfigStore(path), _ => { });
            try
            {
                var slider = (Slider)window.FindName("LyricsOffsetSlider");
                Assert.Equal(1000, slider.Value);
                slider.Value = requested;
                slider.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, slider, null)
                    { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
                expected.LyricsOffsetMs = expectedOffset;
                Assert.Equal(JsonSerializer.Serialize(expected, AppSettings.JsonOpts),
                    JsonSerializer.Serialize(AppSettings.Load(path), AppSettings.JsonOpts));
                Assert.Equal($"{expectedOffset:+0;-0;0} ms", ((TextBlock)window.FindName("LyricsOffsetLabel")).Text);
                Assert.StartsWith("Saved locally", ((TextBlock)window.FindName("SaveStatusText")).Text);
            }
            finally
            {
                window.Close();
                File.Delete(path);
            }
        });
    }

    [Fact]
    public void ThemedSlider_PreservesCommandsThumbDraggingAndSaveOnRelease()
    {
        OnSta(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
            var window = new SettingsWindow(new ConfigStore(path), _ => { });
            try
            {
                var root = (FrameworkElement)window.Content;
                root.Measure(new Size(840, 700));
                root.Arrange(new Rect(0, 0, 840, 700));
                root.UpdateLayout();
                var slider = (Slider)window.FindName("LyricsOffsetSlider");
                Slider.IncreaseLarge.Execute(null, slider);
                Assert.Equal(1000, slider.Value);
                var track = (System.Windows.Controls.Primitives.Track)slider.Template.FindName("PART_Track", slider);
                Assert.True(track.ActualWidth > 0);
                track.Thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(20, 0)
                    { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragDeltaEvent });
                Assert.True(slider.Value > 1000);
                track.Thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragCompletedEventArgs(20, 0, false)
                    { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragCompletedEvent });
                Assert.Equal((int)slider.Value, AppSettings.Load(path).LyricsOffsetMs);
            }
            finally
            {
                window.Close();
                File.Delete(path);
            }
        });
    }

    [Fact]
    public void ThemedScrollBar_UsesNativeTrackAndScrollCommands()
    {
        OnSta(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
            var window = new SettingsWindow(new ConfigStore(path), _ => { });
            try
            {
                var scroll = (ScrollViewer)window.FindName("BodyScroll");
                scroll.Width = 600;
                scroll.Height = 300;
                scroll.Measure(new Size(600, double.PositiveInfinity));
                scroll.Arrange(new Rect(0, 0, 600, 300));
                scroll.UpdateLayout();
                var bar = (System.Windows.Controls.Primitives.ScrollBar)scroll.Template.FindName("PART_VerticalScrollBar", scroll);
                bar.ApplyTemplate();
                Assert.Equal(12, bar.Width);
                Assert.Equal("#FF1B1B1F", bar.Background.ToString());
                var track = (System.Windows.Controls.Primitives.Track)bar.Template.FindName("PART_Track", bar);
                Assert.Equal(Orientation.Vertical, track.Orientation);
                Assert.True(track.IsDirectionReversed);
                Assert.Equal(scroll.ViewportHeight, track.ViewportSize);
                Assert.True(scroll.ScrollableHeight > 0);
                var pageDown = (System.Windows.Input.RoutedCommand)track.IncreaseRepeatButton.Command;
                Assert.Same(System.Windows.Controls.Primitives.ScrollBar.PageDownCommand, pageDown);
                pageDown.Execute(null, bar);
                scroll.UpdateLayout();
                Assert.True(scroll.VerticalOffset > 0);
                var offset = scroll.VerticalOffset;
                System.Windows.Controls.Primitives.ScrollBar.LineUpCommand.Execute(null, bar);
                scroll.UpdateLayout();
                Assert.True(scroll.VerticalOffset < offset);
                var pageUp = (System.Windows.Input.RoutedCommand)track.DecreaseRepeatButton.Command;
                Assert.Same(System.Windows.Controls.Primitives.ScrollBar.PageUpCommand, pageUp);
                pageUp.Execute(null, bar);
                scroll.UpdateLayout();
                Assert.Equal(0, scroll.VerticalOffset);
                Assert.False(File.Exists(path));
            }
            finally { window.Close(); }
        });
    }

    private static void Render(ScrollViewer root, string name)
    {
        var output = Environment.GetEnvironmentVariable("MIMI_QA_DIR");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        root.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(8, 9, 13));
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96,
            System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(output, name + ".png"));
        encoder.Save(file);
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Settings UI test timed out");
        Assert.Null(failure);
    }
}
