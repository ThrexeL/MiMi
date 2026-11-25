using System.Globalization;
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
    private readonly DatePicker _due = new()
    {
        Width = 170,
        FirstDayOfWeek = DayOfWeek.Monday,
        SelectedDateFormat = DatePickerFormat.Short,
        VerticalContentAlignment = VerticalAlignment.Center,
    };
    private readonly TextBox _dueTime = new()
    {
        Width = 90,
        Text = "",
        VerticalContentAlignment = VerticalAlignment.Center,
        Padding = new Thickness(9, 7, 9, 7),
        ToolTip = "24-hour time, for example 15:30",
    };
    private readonly TextBlock _error = new()
    {
        Foreground = new SolidColorBrush(Color.FromRgb(255, 143, 143)),
        FontSize = 12,
        Margin = new Thickness(0, 6, 0, 0),
        Visibility = Visibility.Collapsed,
    };

    public string TaskTitle => _title.Text.Trim();
    public DateTime? TaskDue => _due.SelectedDate is { } date
        && TimeSpan.TryParseExact(_dueTime.Text.Trim(), "hh\\:mm", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.TimeSpanStyles.None, out var time)
            ? date.Date + time
            : null;

    public bool TryValidateDue(out string? error)
    {
        error = null;
        if (_due.SelectedDate is null && string.IsNullOrWhiteSpace(_dueTime.Text)) return true;
        if (_due.SelectedDate is null)
        {
            error = "Choose a due date or clear the time.";
            return false;
        }
        if (!TimeSpan.TryParseExact(_dueTime.Text.Trim(), "hh\\:mm", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.TimeSpanStyles.None, out _))
        {
            error = "Enter the due time as HH:mm, for example 15:30.";
            return false;
        }
        return true;
    }

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
            if (!TryValidateDue(out var dueError))
            {
                _error.Text = dueError;
                _error.Visibility = Visibility.Visible;
                if (_due.SelectedDate is null) _due.Focus(); else _dueTime.Focus();
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
        Grid.SetColumn(_due, 1);
        Grid.SetColumn(_dueTime, 2);
        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                new TextBlock { Text = "What needs doing?", FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) },
                _title,
                new Grid
                {
                    Margin = new Thickness(0, 0, 0, 12),
                    ColumnDefinitions =
                    {
                        new ColumnDefinition { Width = GridLength.Auto },
                        new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                        new ColumnDefinition { Width = GridLength.Auto },
                    },
                    Children =
                    {
                        new TextBlock { Text = "Due (optional)", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) },
                        _due,
                        _dueTime,
                    },
                },
                _error,
                buttons,
            },
        };
        Loaded += (_, _) => _title.Focus();
    }
}
