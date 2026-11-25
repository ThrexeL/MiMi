using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MiMi.App.Interop;
using MiMi.App.UI;
using MiMi.App.UI.Mascot;
using MiMi.Core;
using MiMi.Lyrics;
using MiMi.Media;
using Xunit;

namespace MiMi.Tests;

[Collection("WPF UI")]
public class IslandVisualLayoutTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExpandedMedia_LoadsNamedControlsAndLiveBindings(bool lyrics)
    {
        OnSta(() => WithIsland(window =>
        {
            var media = new MediaViewModel();
            media.Update(new MediaSnapshot
                        {
                            Title = "Layout review track", Position = TimeSpan.FromSeconds(10), Duration = TimeSpan.FromMinutes(3)
                        });
            if (lyrics)
                media.UpdateLyrics(LyricsState.Synced, new LyricsResult
                {
                    TrackName = "Layout review track",
                    ArtistName = "Review artist",
                    SyncedLines = new[]
                    {
                        new LrcLine(TimeSpan.Zero, "Previous lyric"),
                        new LrcLine(TimeSpan.FromSeconds(5), "Current lyric"),
                        new LrcLine(TimeSpan.FromSeconds(20), "Next lyric")
                    }
                }, 0, true);

            var (root, presenter, template) = Layout(window, new MediaExpandedView(media), 240);
            var slider = Assert.IsType<Slider>(template.FindName("ProgressSlider", presenter));
            Assert.Equal("Playback position", AutomationProperties.GetName(slider));
            Assert.False(slider.Focusable);
            Assert.NotNull(slider.Template.FindName("PART_Track", slider));
            Assert.IsType<Button>(template.FindName("LyricsToggle", presenter));
            var play = Assert.IsType<Button>(template.FindName("PlayPauseButton", presenter));
            Assert.IsType<TextBlock>(template.FindName("PlayPauseGlyph", presenter));
            AssertBinding(play, Button.CommandProperty, "Media.PlayPauseCommand");
            Assert.Same(media.PlayPauseCommand, play.Command);
            foreach (var path in new[] { "Media.PreviousCommand", "Media.NextCommand" })
                Assert.Single(Descendants<Button>(root), b => BindingPath(b, Button.CommandProperty) == path);
            var title = Assert.Single(Descendants<TextBlock>(root),
                t => BindingPath(t, TextBlock.TextProperty) == "Media.Title");
            Assert.Equal(media.Title, title.Text);
            Assert.Equal(lyrics, media.LyricsBlockVisible);
            Assert.Contains(Descendants<StackPanel>(root), p =>
                BindingPath(p, UIElement.VisibilityProperty) == "Media.LyricsBlockVisible" &&
                p.Visibility == (lyrics ? Visibility.Visible : Visibility.Collapsed));
            if (lyrics)
            {
                // The current line is a bound Run, not a TextBlock.Text binding.
                var current = Assert.Single(Descendants<TextBlock>(root).SelectMany(t => t.Inlines.OfType<Run>()),
                    r => BindingPath(r, Run.TextProperty) == "Media.LyricCurrent");
                AssertBinding(current, Run.TextProperty, "Media.LyricCurrent");
                Assert.Equal("Current lyric", current.Text);
            }
            else
            {
                media.Update(new MediaSnapshot { Title = "Updated track", Position = TimeSpan.Zero });
                root.UpdateLayout();
                Assert.Equal("Updated track", title.Text);
            }
            AssertActionsAccessible(root);
            Render(root, lyrics ? "island-media-lyrics" : "island-media-expanded");
        }));
    }

    [Fact]
    public void AgentCompact_WholePillClickableAndStatusIsNotTrimmed()
    {
        OnSta(() => WithIsland(window =>
        {
            var indicator = new AgentIndicatorViewModel();
            indicator.Update("ImpressiveBot · Executing a detailed workspace inspection", MascotExpression.Lively, 0);
            var view = new AgentCompactView(indicator);
            var (root, presenter, template) = Layout(window, view, 60);
            var host = Assert.IsType<Grid>(template.FindName("AgentCompactHost", presenter));
            Assert.NotNull(host.Background);
            Assert.Equal("Open agent activity and options", host.ToolTip);
            var line = Assert.Single(Descendants<TextBlock>(root),
                t => BindingPath(t, TextBlock.TextProperty) == "Indicator.Line");
            Assert.Equal(TextTrimming.None, line.TextTrimming);
            Assert.Equal(TextWrapping.Wrap, line.TextWrapping);
            Assert.True(view.TargetWidth > 420);
            AssertActionsAccessible(root);
            Render(root, "island-agent-compact-gold");
        }));
    }

    [Fact]
    public void AgentOptions_RepeatedClickKeepsActivityPanelOpen()
    {
        OnSta(() =>
        {
            var path = TemporaryConfigPath();
            var panel = new AgentActivityPanelView(Array.Empty<AgentSessionRow>());
            var window = new IslandWindow(new ConfigStore(path), () => null, _ => null,
                new MediaViewModel(), agentPanelProvider: () => panel);
            try
            {
                var indicator = new AgentIndicatorViewModel();
                indicator.Update("Codex · Executing tool", MascotExpression.Lively, 0);
                window.ShowPinned(new AgentCompactView(indicator));
                window.OpenAgentOptions();
                window.OpenAgentOptions(); // simulates click after hover expansion
                var rendered = typeof(IslandWindow).GetField("_renderedContent",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(rendered);
                Assert.Same(panel, rendered!.GetValue(window));
                var hover = typeof(IslandWindow).GetField("_hoverExpanded",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(hover);
                Assert.Equal(true, hover!.GetValue(window));
            }
            finally
            {
                window.Close();
                Assert.False(File.Exists(path));
            }
        });
    }

    [Fact]
    public void HoverPanel_IdleTimerUsesConfiguredTimeoutAndStopsOnCollapse()
    {
        OnSta(() =>
        {
            var path = TemporaryConfigPath();
            var store = new ConfigStore(path);
            store.Update(s => s.PanelIdleTimeoutSeconds = 30);
            var window = new IslandWindow(store, () => null, _ => null, new MediaViewModel(),
                agentPanelProvider: () => new AgentActivityPanelView(Array.Empty<AgentSessionRow>()));
            try
            {
                var indicator = new AgentIndicatorViewModel();
                indicator.Update("Codex · Working", MascotExpression.Focused, 0);
                window.ShowPinned(new AgentCompactView(indicator));
                window.OpenAgentOptions();
                var field = typeof(IslandWindow).GetField("_hoverIdleTimer",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(field);
                var timer = Assert.IsType<DispatcherTimer>(field!.GetValue(window));
                Assert.True(timer.IsEnabled);
                Assert.Equal(TimeSpan.FromSeconds(30), timer.Interval);
                window.CollapseHover();
                Assert.False(timer.IsEnabled);

                store.Update(s => s.PanelIdleTimeoutSeconds = 0);
                window.OpenAgentOptions();
                Assert.False(timer.IsEnabled);
            }
            finally
            {
                window.Close();
                File.Delete(path);
            }
        });
    }

    [Fact]
    public void QuickAccess_RetainsAllCommandsAndAccessibleTiles()
    {
        OnSta(() => WithIsland(window =>
        {
            var vm = new QuickAccessViewModel(_ => UnexpectedCommand(), UnexpectedCommand);
            var (root, _, _) = Layout(window, new QuickAccessView(vm), 320);
            var buttons = Descendants<Button>(root).ToList();
            Assert.Equal(11, buttons.Count);
            var tilePaths = new[]
            {
                "QuickAccess.OpenPomodoroCommand", "QuickAccess.OpenWaterCommand", "QuickAccess.OpenTodoCommand",
                "QuickAccess.OpenShelfCommand", "QuickAccess.OpenVolumeCommand", "QuickAccess.OpenClipboardCommand",
                "QuickAccess.OpenWidgetsCommand", "QuickAccess.OpenBrightnessCommand", "QuickAccess.OpenAiCommand",
            };
            foreach (var path in tilePaths)
            {
                var tile = Assert.Single(buttons, b => BindingPath(b, Button.CommandProperty) == path);
                Assert.Equal(new Thickness(4), tile.Margin);
                Assert.NotNull(tile.Background);
                Assert.NotNull(tile.BorderBrush);
            }
            foreach (var (path, command) in new[]
            {
                ("QuickAccess.OpenPomodoroCommand", vm.OpenPomodoroCommand),
                ("QuickAccess.OpenWaterCommand", vm.OpenWaterCommand),
                ("QuickAccess.OpenTodoCommand", vm.OpenTodoCommand),
                ("QuickAccess.OpenShelfCommand", vm.OpenShelfCommand),
                ("QuickAccess.OpenVolumeCommand", vm.OpenVolumeCommand),
                ("QuickAccess.OpenBrightnessCommand", vm.OpenBrightnessCommand),
                ("QuickAccess.OpenClipboardCommand", vm.OpenClipboardCommand),
                ("QuickAccess.OpenWidgetsCommand", vm.OpenWidgetsCommand),
                ("QuickAccess.OpenAiCommand", vm.OpenAiCommand),
                ("QuickAccess.OpenSettingsCommand", vm.OpenSettingsCommand),
                ("QuickAccess.BackCommand", vm.BackCommand)
            })
            {
                var button = Assert.Single(buttons, b => BindingPath(b, Button.CommandProperty) == path);
                Assert.Same(command, button.Command);
            }
            AssertActionsAccessible(root);
            foreach (var button in buttons)
                AssertInside(root, button);
            Render(root, "island-quick-access");
        }));
    }

    [Fact]
    public void QuickAccess_HiddenPagesAreRemovedFromLayout()
    {
        OnSta(() => WithIsland(window =>
        {
            var vm = new QuickAccessViewModel(_ => UnexpectedCommand(), UnexpectedCommand);
            vm.ApplyHiddenPages([UtilityPinState.Water, UtilityPinState.Ai]);
            var (root, _, _) = Layout(window, new QuickAccessView(vm), 320);
            var buttons = Descendants<Button>(root).ToList();
            Assert.DoesNotContain(buttons, b => b.ToolTip as string == "Open water tracker" && b.Visibility == Visibility.Visible);
            Assert.DoesNotContain(buttons, b => b.ToolTip as string == "Choose an AI assistant" && b.Visibility == Visibility.Visible);
            Assert.Equal(9, buttons.Count(b => b.Visibility == Visibility.Visible));
        }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void TodoAndShelf_LayoutEmptyAndPopulatedStates(int rowCount)
    {
        OnSta(() => WithIsland(window =>
        {
            var todo = new TodoViewModel(_ => throw new InvalidOperationException("Add must not execute"), UnexpectedCommand)
            {
                HeaderCount = $"{rowCount} open",
                Rows = Enumerable.Range(0, rowCount).Select(i => new TodoRowViewModel(
                    Guid.NewGuid(), $"Review task {i + 1}", "Today", _ => UnexpectedCommand())).ToArray()
            };
            var (todoRoot, _, _) = Layout(window, new TodoView(todo), 240);
            var todoItems = Assert.Single(Descendants<ItemsControl>(todoRoot),
                i => BindingPath(i, ItemsControl.ItemsSourceProperty) == "Todo.Rows");
            Assert.Equal(rowCount, todoItems.Items.Count);
            var emptyHint = Assert.Single(Descendants<TextBlock>(todoRoot),
                t => BindingPath(t, UIElement.VisibilityProperty) == "Todo.HasNoRows");
            Assert.Equal(rowCount == 0 ? Visibility.Visible : Visibility.Collapsed, emptyHint.Visibility);
            Assert.Contains("Add a task", emptyHint.Text);
            var add = Assert.Single(Descendants<Button>(todoRoot),
                b => BindingPath(b, Button.CommandProperty) == "AddTaskCommand");
            Assert.Same(window.AddTaskCommand, add.Command);
            Assert.Equal(rowCount, Descendants<Button>(todoRoot)
                .Count(b => BindingPath(b, Button.CommandProperty) == "CompleteCommand"));
            AssertBackCommand(window, todoRoot);
            Render(todoRoot, $"island-todo-{rowCount}");

            var rows = Enumerable.Range(0, rowCount).Select(i => new ShelfRowViewModel(
                $"C:\\layout-preview\\review-{i + 1}.txt", "1 KB", i == 1, _ => UnexpectedCommand())).ToArray();
            var (shelfRoot, _, _) = Layout(window, new ShelfView(rows, $"{rowCount} files"), 240);
            var shelfItems = Assert.Single(Descendants<ItemsControl>(shelfRoot),
                i => BindingPath(i, ItemsControl.ItemsSourceProperty) == "Rows");
            Assert.Equal(rowCount, shelfItems.Items.Count);
            var shelfRows = Descendants<Border>(shelfRoot).Where(b => b.Name == "ShelfRow").ToList();
            Assert.Equal(rowCount, shelfRows.Count);
            for (var i = 0; i < rowCount; i++)
            {
                AssertBinding(shelfRows[i], FrameworkElement.TagProperty, "Path");
                Assert.Equal(rows[i].Path, shelfRows[i].Tag);
            }
            Assert.Equal(rowCount, Descendants<Button>(shelfRoot)
                .Count(b => BindingPath(b, Button.CommandProperty) == "RemoveCommand"));
            Assert.Contains(Descendants<TextBlock>(shelfRoot), t => t.Text.Contains(
                rowCount == 0 ? "Your shelf is empty" : "Originals stay untouched"));
            if (rowCount > 0)
                Assert.Contains(Descendants<TextBlock>(shelfRoot), t => t.Text == "Missing");
            AssertBackCommand(window, shelfRoot);
            Render(shelfRoot, $"island-shelf-{rowCount}");
        }));
    }

    [Fact]
    public void TodoPanel_ExposesExplicitEditorCommandWithoutActivatingIsland()
    {
        OnSta(() => WithIsland(window =>
        {
            var todo = new TodoViewModel(_ => true, () => { });
            var (root, _, _) = Layout(window, new TodoView(todo), 240);
            var add = Assert.Single(Descendants<Button>(root),
                b => BindingPath(b, Button.CommandProperty) == "AddTaskCommand");
            Assert.Same(window.AddTaskCommand, add.Command);
            Assert.False(window.ShowActivated);
            Assert.False(window.Focusable);
        }));
    }

    [Fact]
    public void IdleAndFixedUtilityTemplates_KeepExpectedSizesAndNames()
    {
        OnSta(() => WithIsland(window =>
        {
            Assert.False(window.ShowActivated);
            Assert.False(window.Focusable);
            foreach (var name in new[] { "Pill", "GlowRoot", "GlowBand", "Host" })
                Assert.NotNull(window.FindName(name));
            var pill = Assert.IsType<Border>(window.FindName("Pill"));
            Assert.Equal(new Thickness(1), pill.BorderThickness);
            var host = Assert.IsType<ContentControl>(window.FindName("Host"));
            Assert.Equal(HorizontalAlignment.Stretch, host.HorizontalAlignment);
            Assert.Equal(VerticalAlignment.Stretch, host.VerticalAlignment);
            var aurora = Assert.IsType<LinearGradientBrush>(window.Resources["AuroraBrush"]);
            Assert.Equal("#FFFFE6B0", aurora.GradientStops[0].Color.ToString());
            Assert.Equal("#FFFF9257", aurora.GradientStops[aurora.GradientStops.Count - 1].Color.ToString());
            Assert.Equal(8, ((FrameworkElement)window.FindName("GlowBand")).Height);
            var resting = new RestingView();
            Assert.True(resting.Expandable);
            var (idle, presenter, template) = Layout(window, resting, 42);
            var idleHost = Assert.IsType<Grid>(template.FindName("IdleFaceHost", presenter));
            Assert.Single(idleHost.Children.OfType<MiMiMascotControl>());
            Assert.IsType<MiMiMascotControl>(idleHost.Children[0]);
            Assert.Single(Descendants<MiMiMascotControl>(idle));
            var (compact, compactPresenter, compactTemplate) = Layout(window,
                new MediaCompactView(new MediaViewModel()), 44);
            Assert.NotNull(compactTemplate.FindName("Eq", compactPresenter));
            Assert.Single(Descendants<Image>(compact));
            foreach (var content in new IslandContent[]
            {
                new PomodoroView(new PomodoroViewModel(UnexpectedCommand, UnexpectedCommand)),
                new WaterView(new WaterViewModel(UnexpectedCommand))
            })
            {
                var (root, _, _) = Layout(window, content, 92);
                Assert.Equal(content.TargetHeight, root.ActualHeight);
                Assert.Single(Descendants<ProgressBar>(root));
                AssertBackCommand(window, root);
                AssertActionsAccessible(root);
            }
        }));
    }

    [Fact]
    public void AgentActivity_UsesUniformCardsAndSafeRowActions()
    {
        OnSta(() => WithIsland(window =>
        {
            var rows = new[]
            {
                new AgentSessionRow
                {
                    Section = "ACTIVE", Provider = "ImpressiveBot · Forge", SessionId = "session-forge",
                    Project = "MiMi", StatusLabel = "Executing tool", LastAction = "Reading workspace files",
                    AgeLabel = "8s ago", PendingApprovals = 0, StateBrush = Brushes.Gold,
                    Timeline = new[] { new AgentTimelineItem { TimeLabel = "15:30", Text = "Running tool", Kind = "tool" } },
                },
                new AgentSessionRow
                {
                    Provider = "ImpressiveBot · Cove", SessionId = "session-cove",
                    Project = "Research", StatusLabel = "Waiting for input", LastAction = "Which option?",
                    AgeLabel = "12s ago", PendingApprovals = 1, StateBrush = Brushes.Orange,
                    Timeline = Array.Empty<AgentTimelineItem>(),
                },
            };
            var panel = new AgentActivityPanelView(rows);
            var (root, _, _) = Layout(window, panel, 380);
            Assert.Equal(2, Descendants<Border>(root).Count(b =>
                b.Style == window.Resources["WidgetCard"] as Style && b.Visibility == Visibility.Visible));
            Assert.Equal(2, Descendants<Button>(root).Count(b => b.ToolTip as string == "Copy a safe session reference"));
            Assert.Single(Descendants<Button>(root), b => b.ToolTip as string == "Close agent activity");
            AssertActionsAccessible(root);
            foreach (var button in Descendants<Button>(root)) AssertInside(root, button);
            Render(root, "island-agent-activity-cards");
        }));
    }

    [Fact]
    public void NusaUtilityPanels_LoadBindingsAndStayInsideThePill()
    {
        OnSta(() => WithIsland(window =>
        {
            var clipboard = new ClipboardViewModel(_ => { }, () => { }, _ => { });
            clipboard.Add("A useful copied snippet");
            clipboard.Add("A useful copied snippet"); // deduplication must not duplicate the row
            var (clipboardRoot, _, _) = Layout(window, new ClipboardView(clipboard), 340);
            Assert.Single(Descendants<Border>(clipboardRoot), b => b.Name == "ClipboardRow");
            AssertActionsAccessible(clipboardRoot);
            foreach (var button in Descendants<Button>(clipboardRoot)) AssertInside(clipboardRoot, button);
            Render(clipboardRoot, "island-clipboard");

            var widgets = new WidgetsViewModel();
            widgets.Update(new DateTime(2026, 11, 25, 14, 30, 0), Array.Empty<TodoStore.TodoItem>(),
                3, false, 1250, 1800,
                new WidgetSystemSnapshot(new PowerMonitor.BatterySnapshot(true, false, 82, false),
                    new NetworkMonitor.NetworkState(true, "Wi-Fi")));
            var (widgetsRoot, _, _) = Layout(window, new WidgetsView(widgets), 400);
            Assert.Equal(6, Descendants<Border>(widgetsRoot).Count(b => b.Style == window.Resources["WidgetCard"] as Style));
            AssertActionsAccessible(widgetsRoot);
            Render(widgetsRoot, "island-widgets");

            var brightness = new BrightnessViewModel(
                () => Task.FromResult(new LaptopBrightnessControl.Snapshot(true, 72, "DISPLAY")),
                (_, _) => Task.FromResult(true));
            brightness.RefreshAsync().GetAwaiter().GetResult();
            brightness.PreviewLevel = 64;
            var (brightnessRoot, _, _) = Layout(window, new BrightnessView(brightness), 150);
            var slider = Assert.Single(Descendants<Slider>(brightnessRoot));
            Assert.Equal("Laptop brightness", AutomationProperties.GetName(slider));
            Assert.Equal(64, slider.Value);
            AssertActionsAccessible(brightnessRoot);
            Render(brightnessRoot, "island-brightness");
        }));
    }

    [Fact]
    public void PillAurora_RendersIdleHubAndWidgetsAsOneSurface()
    {
        OnSta(() => WithIsland(window =>
        {
            RenderPill(window, new RestingView { Expression = MascotExpression.Lively }, "island-pill-idle");

            var quick = new QuickAccessViewModel(_ => { }, () => { });
            RenderPill(window, new QuickAccessView(quick), "island-pill-quick-access");

            var widgets = new WidgetsViewModel();
            widgets.Update(DateTime.Now, Array.Empty<TodoStore.TodoItem>(), 2, false, 900, 1800,
                new WidgetSystemSnapshot(new PowerMonitor.BatterySnapshot(true, false, 76, false),
                    new NetworkMonitor.NetworkState(true, "Wi-Fi")));
            RenderPill(window, new WidgetsView(widgets), "island-pill-widgets");
        }));
    }

    private static void RenderPill(IslandWindow window, IslandContent content, string name)
    {
        var pill = Assert.IsType<Border>(window.FindName("Pill"));
        var host = Assert.IsType<ContentControl>(window.FindName("Host"));
        pill.BeginAnimation(FrameworkElement.WidthProperty, null);
        pill.BeginAnimation(FrameworkElement.HeightProperty, null);
        pill.BeginAnimation(Border.CornerRadiusProperty, null);
        pill.Clip = null;
        pill.Width = content.TargetWidth;
        host.Content = content;
        host.ApplyTemplate();
        host.Measure(new Size(content.TargetWidth, double.PositiveInfinity));
        var height = content.AutoHeight
            ? Math.Max(content.TargetHeight, Math.Ceiling(host.DesiredSize.Height) + 2)
            : content.TargetHeight;
        pill.Height = height;
        pill.CornerRadius = new CornerRadius(Math.Min(content.TargetCornerRadius, height / 2));
        pill.Measure(new Size(pill.Width, pill.Height));
        pill.Arrange(new Rect(0, 0, pill.Width, pill.Height));
        pill.UpdateLayout();
        var radius = Math.Min(pill.CornerRadius.TopLeft, Math.Min(pill.Width, pill.Height) / 2);
        pill.Clip = new RectangleGeometry(new Rect(0, 0, pill.Width, pill.Height), radius, radius);
        Render(pill, name);
    }

    [Fact]
    public void Settings_LoadsOffscreenWithoutSavingAndRendersReviewImage()
    {
        OnSta(() =>
        {
            var path = TemporaryConfigPath();
            var window = new SettingsWindow(new ConfigStore(path), _ => UnexpectedCommand());
            try
            {
                Assert.False(window.IsVisible);
                Assert.False(window.IsLoaded);
                var root = Assert.IsType<ScrollViewer>(window.FindName("BodyScroll"));
                root.Width = 840;
                root.Height = 700;
                root.Measure(new Size(840, 700));
                root.Arrange(new Rect(0, 0, 840, 700));
                root.UpdateLayout();
                Assert.Equal(840, root.ActualWidth);
                Assert.Equal(700, root.ActualHeight);
                Assert.IsType<CheckBox>(window.FindName("MediaToggle"));
                Assert.IsType<CheckBox>(window.FindName("ClipboardHistoryToggle"));
                Assert.False(File.Exists(path));
                Render(root, "settings-offscreen");
            }
            finally
            {
                window.Close();
                Assert.False(File.Exists(path), "Offscreen settings layout must not save configuration");
            }
        });
    }

    [Fact]
    public void TodoEditor_OptionalDueTimeValidatesWithoutActivatingIsland()
    {
        OnSta(() =>
        {
            var window = new TodoEntryWindow();
            try
            {
                Assert.False(window.IsVisible);
                var content = Assert.IsType<StackPanel>(window.Content);
                var textboxes = LogicalDescendants<TextBox>(content).ToList();
                Assert.Equal(2, textboxes.Count);
                var date = Assert.Single(LogicalDescendants<DatePicker>(content));
                textboxes[0].Text = "Ship the refresh";
                date.SelectedDate = new DateTime(2026, 11, 25);
                textboxes[1].Text = "15:30";
                Assert.True(window.TryValidateDue(out _));
                Assert.Equal(new DateTime(2026, 11, 25, 15, 30, 0), window.TaskDue);

                textboxes[1].Text = "later";
                Assert.False(window.TryValidateDue(out var error));
                Assert.Contains("HH:mm", error);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void TodoDueCard_ExposesSnoozeAlongsideCompleteAndOpen()
    {
        OnSta(() => WithIsland(window =>
        {
            var view = new TodoDueView("Review the UI", () => { }, () => { }, () => { });
            var (root, _, _) = Layout(window, view, 110);
            var snooze = Assert.Single(Descendants<Button>(root),
                b => BindingPath(b, Button.CommandProperty) == "SnoozeCommand");
            Assert.Same(view.SnoozeCommand, snooze.Command);
            AssertActionsAccessible(root);
        }));
    }

    private static IEnumerable<T> LogicalDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var children = LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>();
        foreach (var child in children)
        {
            if (child is T match) yield return match;
            foreach (var descendant in LogicalDescendants<T>(child)) yield return descendant;
        }
    }

    private static (Border Root, ContentPresenter Presenter, DataTemplate Template) Layout(
        IslandWindow window, IslandContent content, double maxHeight)
    {
        var template = Assert.IsType<DataTemplate>(window.Resources[new DataTemplateKey(content.GetType())]);
        var presenter = new ContentPresenter { Content = content, ContentTemplate = template };
        var root = new Border
        {
            Width = content.TargetWidth,
            Background = new SolidColorBrush(Color.FromRgb(20, 20, 22)),
            Child = presenter
        };
        // Keep the Window ancestor for CloseUtilityCommand, without Show/activation/HWND creation.
        window.Content = root;
        root.Measure(new Size(content.TargetWidth, double.PositiveInfinity));
        Assert.True(double.IsFinite(root.DesiredSize.Height));
        Assert.InRange(root.DesiredSize.Height, 1, maxHeight);
        var height = content.AutoHeight ? Math.Max(content.TargetHeight, root.DesiredSize.Height) : content.TargetHeight;
        root.Measure(new Size(content.TargetWidth, height));
        root.Arrange(new Rect(0, 0, content.TargetWidth, height));
        // Run bindings inside TextBlock.Inlines attach asynchronously, even offscreen.
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        root.UpdateLayout();
        Assert.Equal(content.TargetWidth, root.ActualWidth);
        Assert.Equal(height, root.ActualHeight);
        Assert.InRange(root.ActualHeight, 1, maxHeight);
        Assert.False(window.IsVisible);
        Assert.False(window.IsLoaded);
        Assert.Equal(IntPtr.Zero, window.PillHandle);
        return (root, presenter, template);
    }

    private static void AssertBackCommand(IslandWindow window, FrameworkElement root)
    {
        var back = Assert.Single(Descendants<Button>(root), b => b.ToolTip as string == "Back to island");
        AssertBinding(back, Button.CommandProperty, "CloseUtilityCommand");
        Assert.Same(window.CloseUtilityCommand, back.Command);
    }

    private static void AssertActionsAccessible(FrameworkElement root)
    {
        foreach (var button in Descendants<Button>(root))
        {
            Assert.False(button.Focusable);
            Assert.False(string.IsNullOrWhiteSpace(button.ToolTip as string));
            Assert.False(string.IsNullOrWhiteSpace(new ButtonAutomationPeer(button).GetName()));
            Assert.NotNull(button.FocusVisualStyle);
        }
    }

    private static void AssertInside(FrameworkElement root, FrameworkElement element)
    {
        var position = element.TranslatePoint(new Point(), root);
        Assert.InRange(position.X, -0.5, root.ActualWidth);
        Assert.InRange(position.Y, -0.5, root.ActualHeight);
        Assert.InRange(position.X + element.ActualWidth, 1, root.ActualWidth + 0.5);
        Assert.InRange(position.Y + element.ActualHeight, 1, root.ActualHeight + 0.5);
    }

    private static string? BindingPath(DependencyObject target, DependencyProperty property) =>
        BindingOperations.GetBinding(target, property)?.Path?.Path;

    private static void AssertBinding(DependencyObject target, DependencyProperty property, string path)
    {
        Assert.Equal(path, BindingPath(target, property));
        Assert.Equal(BindingStatus.Active, BindingOperations.GetBindingExpression(target, property)!.Status);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void Render(FrameworkElement root, string name)
    {
        // Opt-in artifacts only; normal test runs perform no image writes.
        var output = Environment.GetEnvironmentVariable("MIMI_QA_DIR");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth),
            (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(output, name + ".png");
        using (var file = File.Create(path)) encoder.Save(file);
        using var image = File.OpenRead(path);
        var decoded = BitmapDecoder.Create(image, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Assert.Equal(bitmap.PixelWidth, decoded.Frames[0].PixelWidth);
        Assert.Equal(bitmap.PixelHeight, decoded.Frames[0].PixelHeight);
    }

    private static string TemporaryConfigPath() =>
        Path.Combine(Path.GetTempPath(), $"mimi-layout-{Guid.NewGuid():N}.json");

    private static void UnexpectedCommand() => throw new InvalidOperationException("Layout must not execute commands");

    private static void WithIsland(Action<IslandWindow> action)
    {
        var path = TemporaryConfigPath();
        var window = new IslandWindow(new ConfigStore(path), () => null, _ => null, new MediaViewModel(),
            closeUtility: UnexpectedCommand);
        try { action(window); }
        finally
        {
            window.Close();
            Assert.False(File.Exists(path), "Offscreen island layout must not save configuration");
        }
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Offscreen WPF layout timed out");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
