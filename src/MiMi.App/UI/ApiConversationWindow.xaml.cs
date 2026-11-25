using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MiMi.App.AI;
using MiMi.App.Interop;

namespace MiMi.App.UI;

public sealed class ApiTranscriptRowViewModel(string role, string content, bool isError = false)
{
    public string RoleLabel => role == "user" ? "You" : isError ? "MiMi" : role == "system" ? "System" : "Assistant";
    public string Content { get; } = content;
    public HorizontalAlignment Alignment => role == "user" ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    public Brush Bubble => isError ? new SolidColorBrush(Color.FromRgb(0x35, 0x18, 0x18))
        : new SolidColorBrush(role == "user" ? Color.FromRgb(0x28, 0x24, 0x18) : Color.FromRgb(0x18, 0x18, 0x20));
}

public partial class ApiConversationWindow : Window
{
    private readonly AiApiClient _client = new();
    private readonly List<AiChatMessage> _history = [];
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Action _openSettings;
    private AiApiConfiguration _configuration;
    private string? _apiKey;
    private bool _sending;

    public ObservableCollection<ApiTranscriptRowViewModel> Rows { get; } = new();

    public ApiConversationWindow(AiProviderDefinition provider, AiApiConfiguration configuration,
        string? apiKey, Action openSettings)
    {
        _openSettings = openSettings;
        _configuration = configuration;
        _apiKey = apiKey;
        InitializeComponent();
        Icon = MiMiIcon.ImageSource;
        Title = $"MiMi · {provider.Name}";
        ProviderTitle.Text = provider.Name;
        ConnectionInfo.Text = $"{configuration.Model} · {configuration.Endpoint}";
        ConnectionInfo.ToolTip = configuration.Endpoint;
        TranscriptItems.ItemsSource = Rows;
        Closed += (_, _) =>
        {
            _cancellation.Cancel();
            _cancellation.Dispose();
            _client.Dispose();
        };
    }

    public void UpdateConfiguration(AiProviderDefinition provider, AiApiConfiguration configuration, string? apiKey)
    {
        _configuration = configuration;
        _apiKey = apiKey;
        Title = $"MiMi · {provider.Name}";
        ProviderTitle.Text = provider.Name;
        ConnectionInfo.Text = $"{configuration.Model} · {configuration.Endpoint}";
        ConnectionInfo.ToolTip = configuration.Endpoint;
    }

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendAsync();

    private async Task SendAsync()
    {
        var prompt = PromptInput.Text.Trim();
        if (_sending || prompt.Length == 0) return;
        _sending = true;
        SendButton.IsEnabled = false;
        PromptInput.IsEnabled = false;
        ClearButton.IsEnabled = false;
        StatusText.Visibility = Visibility.Collapsed;
        PromptInput.Text = "";
        _history.Add(new AiChatMessage("user", prompt));
        Rows.Add(new ApiTranscriptRowViewModel("user", prompt));
        ScrollToEnd();
        try
        {
            var result = await _client.SendAsync(_configuration, _apiKey, _history, _cancellation.Token);
            if (!result.Success)
            {
                _history.RemoveAt(_history.Count - 1);
                Rows.RemoveAt(Rows.Count - 1);
                StatusText.Text = result.Error;
                StatusText.Visibility = Visibility.Visible;
                PromptInput.Text = prompt;
                return;
            }
            _history.Add(new AiChatMessage("assistant", result.Content));
            Rows.Add(new ApiTranscriptRowViewModel("assistant", result.Content));
        }
        finally
        {
            _sending = false;
            SendButton.IsEnabled = true;
            PromptInput.IsEnabled = true;
            ClearButton.IsEnabled = true;
            PromptInput.Focus();
            ScrollToEnd();
        }
    }

    private void PromptInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            e.Handled = true;
            _ = SendAsync();
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _history.Clear();
        Rows.Clear();
        StatusText.Visibility = Visibility.Collapsed;
        PromptInput.Focus();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        _openSettings();
        Close();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e) => _cancellation.Cancel();

    private void ScrollToEnd() => Dispatcher.BeginInvoke(() => TranscriptScroll.ScrollToEnd(),
        System.Windows.Threading.DispatcherPriority.Background);
}
