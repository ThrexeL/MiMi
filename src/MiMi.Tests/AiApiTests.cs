using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using MiMi.App.AI;
using MiMi.App.UI;
using MiMi.Core;
using Xunit;

namespace MiMi.Tests;

[Collection("WPF UI")]
public class AiApiTests
{
    [Fact]
    public void ApiKey_IsEncryptedAtRestAndRoundTripsForTheCurrentUser()
    {
        const string key = "test-key-never-persisted-in-plaintext";
        var path = Path.Combine(Path.GetTempPath(), $"mimi-api-settings-{Guid.NewGuid():N}.json");
        try
        {
            new AppSettings
            {
                AiCustomMode = "api",
                AiApiKeyProtected = AiApiKeyProtector.Protect(key),
            }.Save(path);
            var json = File.ReadAllText(path);
            Assert.DoesNotContain(key, json, StringComparison.Ordinal);
            var settings = AppSettings.Load(path);
            var service = new AiProviderService(settings);
            Assert.True(service.TryGetApiConfiguration(out _, out var recovered));
            Assert.Equal(key, recovered);
            Assert.Contains("grok-4.7", json, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ApiClient_SendsCompatibleRequestAndReadsTextResponse()
    {
        string? authorization = null;
        string? requestBody = null;
        Uri? requestUri = null;
        using var handler = new RecordingHandler(async (request, _) =>
        {
            requestUri = request.RequestUri;
            authorization = request.Headers.Authorization?.Parameter;
            requestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"choices":[{"message":{"role":"assistant","content":"Hello from MiMi."}}]}""",
                    Encoding.UTF8, "application/json"),
            };
        });
        using var http = new HttpClient(handler);
        using var client = new AiApiClient(http);
        var result = await client.SendAsync(
            new AiApiConfiguration("https://example.test/v1/", "test-model", "Text only.", true),
            "test-secret", [new("user", "Hello")], CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("Hello from MiMi.", result.Content);
        Assert.Equal("https://example.test/v1/chat/completions", requestUri?.AbsoluteUri);
        Assert.Equal("test-secret", authorization);
        using var payload = JsonDocument.Parse(requestBody!);
        Assert.Equal("test-model", payload.RootElement.GetProperty("model").GetString());
        Assert.False(payload.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("system", payload.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal("Text only.", payload.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.DoesNotContain("test-secret", requestBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApiClient_RejectsPlaintextRemoteKeyAndSanitizesProviderErrors()
    {
        using var client = new AiApiClient(new HttpClient(new RecordingHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("provider echoed test-secret", Encoding.UTF8, "application/json"),
            }))));
        var configuration = new AiApiConfiguration("https://example.test/v1", "test-model", "", true);
        var insecure = await client.SendAsync(configuration with { Endpoint = "http://example.test/v1" },
            "test-secret", [new("user", "Hello")]);
        Assert.False(insecure.Success);
        Assert.Contains("HTTPS", insecure.Error, StringComparison.Ordinal);

        var rejected = await client.SendAsync(configuration, "test-secret", [new("user", "Hello")]);
        Assert.False(rejected.Success);
        Assert.Contains("rejected", rejected.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("test-secret", rejected.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ApiSettings_LoadModeAndEncryptNewKeyWithoutExposingIt()
    {
        OnSta(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
            new AppSettings
            {
                AiDefaultProvider = "custom",
                AiCustomMode = "api",
                AiApiPreset = "openrouter",
                AiApiEndpoint = "https://openrouter.ai/api/v1",
                AiApiModel = "openai/gpt-4.1-mini",
                AiApiKeyProtected = AiApiKeyProtector.Protect("old-test-key"),
            }.Save(path);
            var store = new ConfigStore(path);
            var window = new SettingsWindow(store, _ => { }, aiProviders: new AiProviderService(store.Current));
            try
            {
                Assert.Equal(1, ((ComboBox)window.FindName("AiCustomModeCombo")).SelectedIndex);
                Assert.Equal(Visibility.Visible, ((StackPanel)window.FindName("AiApiPanel")).Visibility);
                Assert.Equal(Visibility.Collapsed, ((StackPanel)window.FindName("AiCommandPanel")).Visibility);
                Assert.Equal("", ((PasswordBox)window.FindName("AiApiKeyInput")).Password);
                Assert.Contains("encrypted", ((TextBlock)window.FindName("AiApiKeyStatus")).Text,
                    StringComparison.OrdinalIgnoreCase);

                var keyInput = (PasswordBox)window.FindName("AiApiKeyInput");
                keyInput.Password = "new-test-key";
                keyInput.RaiseEvent(new System.Windows.Input.KeyboardFocusChangedEventArgs(
                    System.Windows.Input.Keyboard.PrimaryDevice, 0, keyInput, null)
                {
                    RoutedEvent = System.Windows.Input.Keyboard.LostKeyboardFocusEvent,
                });
                var savedJson = File.ReadAllText(path);
                Assert.DoesNotContain("new-test-key", savedJson, StringComparison.Ordinal);
                Assert.DoesNotContain("old-test-key", savedJson, StringComparison.Ordinal);
                var service = new AiProviderService(AppSettings.Load(path));
                Assert.True(service.TryGetApiConfiguration(out var configuration, out var key));
                Assert.Equal("new-test-key", key);
                Assert.Equal("https://openrouter.ai/api/v1", configuration.Endpoint);
                var qa = Environment.GetEnvironmentVariable("MIMI_QA_DIR");
                if (!string.IsNullOrWhiteSpace(qa))
                {
                    Directory.CreateDirectory(qa);
                    var panel = (StackPanel)window.FindName("AiApiPanel");
                    panel.Width = 620;
                    panel.Measure(new Size(620, double.PositiveInfinity));
                    panel.Arrange(new Rect(0, 0, 620, panel.DesiredSize.Height));
                    panel.UpdateLayout();
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                        (int)Math.Ceiling(panel.ActualWidth), (int)Math.Ceiling(panel.ActualHeight),
                        96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    bitmap.Render(panel);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(qa, "api-settings-panel.png"));
                    encoder.Save(file);
                }
            }
            finally
            {
                window.Close();
                File.Delete(path);
            }
        });
    }

    [Fact]
    public void EmbeddedMiMiIcon_ContainsAllTaskbarResolutions()
    {
        var stream = System.Windows.Application.GetResourceStream(new Uri(
            "pack://application:,,,/MiMi;component/Assets/mimi.ico")).Stream;
        using var reader = new BinaryReader(stream);
        Assert.Equal(0, reader.ReadUInt16());
        Assert.Equal(1, reader.ReadUInt16());
        var count = reader.ReadUInt16();
        Assert.Equal(7, count);
        var sizes = new List<int>();
        for (var i = 0; i < count; i++)
        {
            var width = reader.ReadByte();
            reader.ReadBytes(15);
            sizes.Add(width == 0 ? 256 : width);
        }
        Assert.Equal(new[] { 16, 24, 32, 48, 64, 128, 256 }, sizes);
    }

    [Fact]
    public void ApiConversationWindow_IsTaskbarVisibleWithMiMiIcon()
    {
        OnSta(() =>
        {
            var settings = new AppSettings { AiCustomMode = "api", AiApiKeyProtected = AiApiKeyProtector.Protect("test-key") };
            var service = new AiProviderService(settings);
            var provider = service.GetProviders().Single(option => option.Provider.Id == "custom").Provider;
            Assert.True(service.TryGetApiConfiguration(out var configuration, out var key));
            var window = new ApiConversationWindow(provider, configuration, key, () => { });
            Assert.True(window.ShowInTaskbar);
            Assert.NotNull(window.Icon);
            Assert.Equal(64, window.Icon.Width);
            window.UpdateConfiguration(provider,
                configuration with { Endpoint = "https://example.test/v1", Model = "updated-model" }, key);
            Assert.Equal("MiMi · Custom AI", window.Title);
            Assert.Contains("updated-model", ((TextBlock)window.FindName("ConnectionInfo")).Text,
                StringComparison.Ordinal);
            Assert.Contains("https://example.test/v1", ((TextBlock)window.FindName("ConnectionInfo")).Text,
                StringComparison.Ordinal);
            window.Close();
        });
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "WPF API test timed out");
        Assert.Null(failure);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => respond(request, cancellationToken);
    }
}
