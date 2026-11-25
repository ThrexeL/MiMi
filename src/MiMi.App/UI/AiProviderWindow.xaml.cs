using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using MiMi.App.AI;
using MiMi.App.Interop;

namespace MiMi.App.UI;

public sealed class AiProviderOptionViewModel(AiProviderOption option, Action<AiProviderDefinition> launch)
{
    public string Name => option.Provider.Name;
    public string Glyph => option.Provider.Glyph;
    public string Description => option.Provider.Description;
    public string Detail => option.Detail;
    public string StatusLabel => option.StatusLabel;
    public bool IsAvailable => option.IsAvailable;
    public ICommand LaunchCommand { get; } = new RelayCommand(() => launch(option.Provider));
}

public partial class AiProviderWindow : Window
{
    public ObservableCollection<AiProviderOptionViewModel> Providers { get; } = new();

    private readonly AiProviderService _service;
    private readonly Func<AiProviderDefinition, AiLaunchResult> _launch;
    private readonly Action _openSettings;

    public AiProviderWindow(AiProviderService service, Func<AiProviderDefinition, AiLaunchResult> launch,
        Action openSettings)
    {
        _service = service;
        _launch = launch;
        _openSettings = openSettings;
        InitializeComponent();
        MiMiIcon.Apply(this);
        DataContext = this;
        ProviderItems.ItemsSource = Providers;
        RefreshProviders();
    }

    private void RefreshProviders()
    {
        Providers.Clear();
        AiProviderOptionViewModel? defaultOption = null;
        foreach (var option in _service.GetProviders())
        {
            var vm = new AiProviderOptionViewModel(option, LaunchProvider);
            Providers.Add(vm);
            if (option.Provider.Id == _service.DefaultProviderId) defaultOption = vm;
        }
        DefaultButton.Content = defaultOption is null ? "Launch default" : $"Launch {defaultOption.Name}";
        DefaultButton.IsEnabled = defaultOption?.IsAvailable == true;
    }

    private void LaunchProvider(AiProviderDefinition provider)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        var result = _launch(provider);
        if (result.Success)
        {
            Close();
            return;
        }
        ErrorText.Text = result.Message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void DefaultButton_Click(object sender, RoutedEventArgs e)
    {
        var provider = _service.GetDefaultProvider();
        if (provider is not null) LaunchProvider(provider);
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _openSettings();
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
