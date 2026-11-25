using MiMi.App.Interop;
using MiMi.App.UI;
using MiMi.Core;
using Xunit;

namespace MiMi.Tests;

public class NusaFeaturesTests
{
    [Fact]
    public void Settings_ClampIdleTimeoutAndSanitizeHiddenPages()
    {
        var settings = new AppSettings
        {
            PanelIdleTimeoutSeconds = 999,
            QuickAccessHiddenPages = ["unknown", UtilityPinState.Water, UtilityPinState.Water],
        };
        settings.Clamp();
        Assert.Equal(120, settings.PanelIdleTimeoutSeconds);
        Assert.Equal(UtilityPinState.Water, Assert.Single(settings.QuickAccessHiddenPages));
    }

    [Fact]
    public void QuickAccess_HiddenPagesReflowWithoutDisablingCommands()
    {
        var opened = new List<string>();
        var vm = new QuickAccessViewModel(opened.Add, () => { });
        vm.ApplyHiddenPages([UtilityPinState.Water, UtilityPinState.Codex]);
        Assert.True(vm.ShowFocus);
        Assert.False(vm.ShowWater);
        Assert.False(vm.ShowCodex);
        Assert.True(vm.ShowWidgets);
        vm.OpenWaterCommand.Execute(null);
        Assert.Equal(UtilityPinState.Water, Assert.Single(opened));
    }

    [Fact]
    public void Clipboard_DeduplicatesBoundsAndStripsControlCharacters()
    {
        var copied = new List<string>();
        var clipboard = new ClipboardViewModel(_ => { }, () => { }, copied.Add);
        Assert.True(clipboard.Add("  hello\u0001 world  "));
        Assert.Equal("hello world", clipboard.Entries[0].Text);
        Assert.True(clipboard.Add("hello world"));
        Assert.Single(clipboard.Entries);

        Assert.True(clipboard.Add(new string('x', ClipboardViewModel.MaxTextLength + 100)));
        Assert.Equal(ClipboardViewModel.MaxTextLength, clipboard.Entries[0].Text.Length);
        for (var i = 0; i < ClipboardViewModel.Capacity + 5; i++)
            Assert.True(clipboard.Add($"clip {i}"));
        Assert.Equal(ClipboardViewModel.Capacity, clipboard.Count);
        Assert.Equal("clip 24", clipboard.Entries[0].Text);
        Assert.False(clipboard.Add("   "));

        clipboard.Entries[0].CopyCommand.Execute(null);
        Assert.Equal("clip 24", Assert.Single(copied));
        clipboard.Clear();
        Assert.Empty(clipboard.Entries);
    }

    [Fact]
    public void Clipboard_CaptureTogglePersistsOnlyThePreference()
    {
        bool? persisted = null;
        var clipboard = new ClipboardViewModel(value => persisted = value, () => { }, _ => { });
        Assert.False(clipboard.IsCapturing);
        clipboard.ToggleCaptureCommand.Execute(null);
        Assert.True(clipboard.IsCapturing);
        Assert.True(persisted);
        clipboard.ToggleCaptureCommand.Execute(null);
        Assert.False(clipboard.IsCapturing);
        Assert.False(persisted);
    }

    [Fact]
    public void Widgets_BuildMondayWeekAndTruthfulSystemCards()
    {
        var widgets = new WidgetsViewModel();
        var today = new DateTime(2026, 11, 25, 14, 30, 0); // Wednesday
        var tasks = new[]
        {
            new TodoStore.TodoItem { Title = "Standup", Due = today.AddHours(1) },
            new TodoStore.TodoItem { Title = "Review", Due = today.AddDays(-1) },
            new TodoStore.TodoItem { Title = "Done", Done = true, Due = today },
        };
        widgets.Update(today, tasks, 3, true, 1250, 1800,
            new WidgetSystemSnapshot(new PowerMonitor.BatterySnapshot(true, true, 82, false),
                new NetworkMonitor.NetworkState(true, "Wi-Fi")));

        Assert.Equal(7, widgets.WeekDays.Count);
        Assert.Equal("Mon", widgets.WeekDays[0].Label);
        Assert.Single(widgets.WeekDays, d => d.IsToday);
        Assert.Equal("1 due today", widgets.TodayTasksLabel);
        Assert.Contains("Review", widgets.NextTaskLabel);
        Assert.Equal("3 sessions", widgets.FocusLabel);
        Assert.Equal("1.3 / 1.8 L", widgets.WaterLabel);
        Assert.Equal("82%", widgets.BatteryLabel);
        Assert.Equal("Charging", widgets.BatteryDetail);
        Assert.Equal(0.82, widgets.BatteryProgress);
        Assert.Equal("Wi-Fi", widgets.NetworkLabel);
        Assert.Contains("illuminated", widgets.MoonDetail);
    }

    [Fact]
    public void Widgets_DesktopAndOfflineStatesDoNotInventBatteryOrNetwork()
    {
        var widgets = new WidgetsViewModel();
        widgets.Update(new DateTime(2026, 11, 25, 9, 0, 0), Array.Empty<TodoStore.TodoItem>(),
            0, false, 0, 1800,
            new WidgetSystemSnapshot(new PowerMonitor.BatterySnapshot(false, false, 0, true),
                new NetworkMonitor.NetworkState(false, "")));
        Assert.Equal("Desktop", widgets.BatteryLabel);
        Assert.Null(widgets.BatteryProgress);
        Assert.Equal("Offline", widgets.NetworkLabel);
        Assert.Equal("Nothing scheduled", widgets.NextTaskLabel);
    }

    [Fact]
    public void MoonPhase_IsBoundedAndIlluminationTracksPhase()
    {
        var newMoon = MoonPhaseCalculator.Calculate(new DateTime(2000, 1, 6));
        var halfMoon = MoonPhaseCalculator.Calculate(new DateTime(2000, 1, 21));
        Assert.InRange(newMoon.Phase, 0, 1);
        Assert.InRange(halfMoon.Phase, 0, 1);
        Assert.InRange(newMoon.Illumination, 0, 1);
        Assert.True(halfMoon.Illumination > newMoon.Illumination);
        Assert.False(string.IsNullOrWhiteSpace(MoonPhaseCalculator.Name(halfMoon.Phase)));
    }

    [Fact]
    public async Task Brightness_AppliesClampedPreviewAndReportsUnavailable()
    {
        var requests = new List<(string Instance, int Percent)>();
        var vm = new BrightnessViewModel(
            () => Task.FromResult(new LaptopBrightnessControl.Snapshot(true, 72, "DISPLAY")),
            (instance, percent) =>
            {
                requests.Add((instance, percent));
                return Task.FromResult(true);
            });
        await vm.RefreshAsync();
        Assert.True(vm.IsAvailable);
        Assert.Equal(72, vm.PreviewLevel);
        vm.PreviewLevel = 140;
        Assert.Equal(100, vm.PreviewLevel);
        await vm.ApplyPreviewAsync();
        Assert.Equal(("DISPLAY", 100), Assert.Single(requests));

        var unavailable = new BrightnessViewModel(
            () => Task.FromResult(new LaptopBrightnessControl.Snapshot(false, 0, null)),
            (_, _) => throw new InvalidOperationException("must not be called"));
        await unavailable.RefreshAsync();
        Assert.False(unavailable.IsAvailable);
        Assert.Equal("Not available on this display", unavailable.StatusLabel);
    }
}
