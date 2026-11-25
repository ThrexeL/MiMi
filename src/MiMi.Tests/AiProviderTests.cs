using System.IO;
using System.Windows;
using System.Windows.Controls;
using MiMi.App.AI;
using MiMi.App.UI;
using MiMi.Agents;
using MiMi.Core;
using Xunit;

namespace MiMi.Tests;

[Collection("WPF UI")]
public class AiProviderTests
{
    [Fact]
    public void DetectCommand_AcceptsPortableAbsolutePaths()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mimi-ai-{Guid.NewGuid():N}.cmd");
        try
        {
            File.WriteAllText(path, "@echo off\r\n");
            Assert.Equal(Path.GetFullPath(path), AiProviderService.DetectCommand($"\"{path}\""));
            Assert.Null(AiProviderService.DetectCommand(Path.Combine(Path.GetTempPath(), "missing-ai.exe")));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Providers_IncludeDetectedBuiltInsAndUserConfiguredCustomAi()
    {
        var command = Path.Combine(Path.GetTempPath(), $"mimi-custom-{Guid.NewGuid():N}.cmd");
        try
        {
            File.WriteAllText(command, "@echo off\r\n");
            var service = new AiProviderService(new AppSettings
            {
                AiDefaultProvider = "custom",
                AiCustomName = "Friend's AI",
                AiCustomCommand = command,
                AiCustomArguments = "--read-only",
            });
            var providers = service.GetProviders();
            Assert.Equal(3, providers.Count);
            Assert.Contains(providers, p => p.Provider.Id == "codex");
            Assert.Contains(providers, p => p.Provider.Id == "claude");
            var custom = Assert.Single(providers, p => p.Provider.Id == "custom");
            Assert.Equal("Friend's AI", custom.Provider.Name);
            Assert.True(custom.IsAvailable);
            Assert.Equal("--read-only", custom.Provider.Arguments);
            Assert.Equal("custom", service.GetDefaultProvider()?.Id);
        }
        finally { File.Delete(command); }
    }

    [Fact]
    public void Settings_RoundTripAiConfigurationAndMigrateLegacyHiddenCodexPage()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mimi-ai-settings-{Guid.NewGuid():N}.json");
        try
        {
            new AppSettings
            {
                AiDefaultProvider = "claude",
                AiCustomName = "Local Assistant",
                AiCustomCommand = "assistant.cmd",
                AiCustomArguments = "--safe",
                QuickAccessHiddenPages = [UtilityPinState.Codex],
            }.Save(path);
            var loaded = AppSettings.Load(path);
            Assert.Equal("claude", loaded.AiDefaultProvider);
            Assert.Equal("Local Assistant", loaded.AiCustomName);
            Assert.Equal("assistant.cmd", loaded.AiCustomCommand);
            Assert.Equal("--safe", loaded.AiCustomArguments);
            Assert.Equal(UtilityPinState.Ai, Assert.Single(loaded.QuickAccessHiddenPages));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CodexWindow_IsTaskbarVisibleAndDisposesCleanly()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var approvals = new ApprovalStore();
                var bus = new AgentEventBus();
                var window = new CodexConversationWindow(approvals, bus, Path.GetTempPath());
                Assert.True(window.ShowInTaskbar);
                Assert.False(window.IsVisible);
                window.Close();
                window.EnsureDisposedAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public void AiPicker_LoadsTaskbarVisibleProviderCardsOffscreen()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var service = new AiProviderService(new AppSettings());
                var window = new AiProviderWindow(service,
                    _ => new AiLaunchResult(false, "test"), () => { });
                Assert.True(window.ShowInTaskbar);
                Assert.False(window.IsVisible);
                Assert.Equal(3, window.Providers.Count);
                var items = (ItemsControl)window.FindName("ProviderItems");
                Assert.Same(window.Providers, items.ItemsSource);
                window.Close();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
