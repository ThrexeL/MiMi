using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MiMi.App.UI;

/// <summary>Explicit, activating editor for a new task. The island itself never activates.</summary>
public sealed class TodoEntryWindow : Window
{
    private readonly TextBox _title = new()
    {
        MinWidth = 320,
        Margin = new Thickness(0, 0, 0, 12),
        VerticalContentAlignment = VerticalAlignment.Center,
        Padding = new Thickness(9, 7, 9, 7),
    };

    public string TaskTitle => _title.Text.Trim();

    public TodoEntryWindow(Window? owner = null)
    {
        Title = "Add a task";
        Width = 420;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(24, 24, 27));
        Foreground = new SolidColorBrush(Color.FromRgb(242, 242, 244));
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 14;
        if (owner is not null) Owner = owner;

        var add = new Button
        {
            Content = "Add task",
            IsDefault = true,
            MinWidth = 96,
            Padding = new Thickness(12, 7, 12, 7),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var cancel = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            MinWidth = 80,
            Padding = new Thickness(12, 7, 12, 7),
            Margin = new Thickness(0, 0, 8, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        add.Click += (_, _) =>
        {
            if (TaskTitle.Length == 0)
            {
                _title.Focus();
                return;
            }
            DialogResult = true;
            Close();
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { cancel, add },
        };
        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                new TextBlock { Text = "What needs doing?", FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) },
                _title,
                buttons,
            },
        };
        Loaded += (_, _) => _title.Focus();
    }
}
