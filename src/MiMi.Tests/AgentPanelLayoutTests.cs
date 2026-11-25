using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MiMi.Agents;
using MiMi.App.UI;
using MiMi.Core;
using Xunit;

namespace MiMi.Tests;

public class AgentPanelLayoutTests
{
    [Fact]
    public void ActivityAndApprovalTemplates_FitBoundedIsland()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new IslandWindow(new ConfigStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")),
                    () => null, _ => null, new MediaViewModel());
                var rows = Enumerable.Range(0, 12).Select(i => new AgentSessionRow {
                    Provider = i == 0 ? "Thursday" : "Codex CLI", Project = "ImpressiveBackup",
                    StatusLabel = i == 0 ? "Waiting for approval" : "Working", LastAction = "Reading workspace files",
                    AgeLabel = "2s ago", PendingApprovals = i == 0 ? 1 : 0,
                    StateBrush = new SolidColorBrush(i == 0 ? Colors.Orange : Colors.LightBlue),
                    Section = i == 0 ? "NEEDS APPROVAL" : i == 1 ? "ACTIVE" : "",
                    Timeline = new[] { new AgentTimelineItem { TimeLabel = "13:32", Text = "Running read_file", Kind = "tool" } }
                }).ToList();
                // Live ImpressiveBot line, exactly as the presenter builds it from a real status.
                using var mgr = new AgentSessionManager();
                mgr.Apply(AgentEventFactory.Create("impressivebot:Turnip", "t1", AgentEventTypes.ToolStarted, message: "Researching options"));
                mgr.Apply(AgentEventFactory.Create("impressivebot:Cove", "c1", AgentEventTypes.WaitingForInput, message: "Which option?"));
                var status = ImpressiveBotStatusReader.Build(mgr.All, "C:\\x\\MiMi.AgentCli.exe");
                Render(window, new AgentActivityPanelView(rows, status), "activity", 470);
                var indicator = new AgentIndicatorViewModel();
                indicator.Update("Thursday · Thinking", MiMi.App.UI.Mascot.MascotExpression.Focused, 0);
                Render(window, new AgentCompactView(indicator), "compact", 60);
                indicator.Update("3 agents active", MiMi.App.UI.Mascot.MascotExpression.Focused, 0);
                Render(window, new AgentCompactView(indicator), "compact-multiple", 60);
                var shortWidth = new AgentCompactView(indicator).TargetWidth;
                indicator.Update("Thursday · Waiting for your approval to run a command", MiMi.App.UI.Mascot.MascotExpression.Alert, 1);
                var longWidth = new AgentCompactView(indicator).TargetWidth;
                Assert.True(longWidth > shortWidth);
                Assert.True(longWidth > 420, "Long agent status must expand beyond the old 380 DIP cap.");
                Render(window, new AgentCompactView(indicator), "compact-long", 60);
                var card = new AgentCardViewModel { ApprovalId = "preview" };
                card.Update(AgentEventFactory.Create("thursday", "chat", AgentEventTypes.ApprovalRequested,
                    title: "Permission required", message: "Allow this tool family for this task?", project: "ImpressiveBackup",
                    approvalRequestId: "preview", metadata: new Dictionary<string, string> {
                        ["action_kind"] = "bash", ["command"] = "cd C:/Users/demo/ImpressiveBackup\nls -la\ncat package.json" }));
                Render(window, new ApprovalView(card), "approval", 470);
                window.Close();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "WPF layout timed out");
        Assert.Null(failure);
    }

    [Fact]
    public void ApprovalButtons_AreNotClippedByThePreviousPillHeight()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new IslandWindow(new ConfigStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")),
                    () => null, _ => null, new MediaViewModel());
                var card = new AgentCardViewModel { ApprovalId = "layout-approval" };
                card.Update(AgentEventFactory.Create("claude", "chat", AgentEventTypes.ApprovalRequested,
                    title: "Approval Required", message: "Wants to run a terminal command.", project: "MiMi",
                    approvalRequestId: "layout-approval", metadata: new Dictionary<string, string> {
                        ["action_kind"] = "bash", ["command"] = "dotnet test src/MiMi.Tests/MiMi.Tests.csproj\n" +
                            "dotnet build MiMi.slnx -c Release\ngit status --short" }));
                var content = new ApprovalView(card);
                window.Host.Content = content;
                window.Host.ContentTemplate = (DataTemplate)window.Resources[new DataTemplateKey(content.GetType())];
                window.Host.ApplyTemplate();
                window.Pill.Width = content.TargetWidth;
                window.Pill.Height = 236; // Reproduce the fixed height that clipped the user's demo card.
                var root = (FrameworkElement)window.Content;
                root.Measure(new Size(960, 560));
                root.Arrange(new Rect(0, 0, 960, 560));
                root.UpdateLayout();

                var measuredHeight = window.MeasureContentHeight(content);
                window.Pill.Height = measuredHeight;
                root.Measure(new Size(960, 560));
                root.Arrange(new Rect(0, 0, 960, 560));
                root.UpdateLayout();

                // Match the post-load correction: command visibility bindings may appear after first measure.
                var correctedHeight = window.MeasureContentHeight(content);
                window.Pill.Height = correctedHeight;
                root.Measure(new Size(960, 560));
                root.Arrange(new Rect(0, 0, 960, 560));
                root.UpdateLayout();

                var approve = FindButton(root, "Approve request");
                var deny = FindButton(root, "Deny request");
                foreach (var button in new[] { approve, deny })
                {
                    Assert.Equal(Visibility.Visible, button.Visibility);
                    var bottom = button.TranslatePoint(new Point(0, button.ActualHeight), window.Pill).Y;
                    Assert.True(bottom <= window.Pill.ActualHeight,
                        $"{System.Windows.Automation.AutomationProperties.GetName(button)} bottom {bottom:0.#} exceeds pill height {window.Pill.ActualHeight:0.#}.");
                }
                var qa = Environment.GetEnvironmentVariable("MIMI_QA_DIR");
                if (!string.IsNullOrWhiteSpace(qa))
                {
                    Directory.CreateDirectory(qa);
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.Pill.ActualWidth),
                        (int)Math.Ceiling(window.Pill.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(window.Pill);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(qa, "approval-buttons-fit.png"));
                    encoder.Save(file);
                }
                window.Close();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Approval layout test timed out");
        Assert.Null(failure);
    }

    private static Button FindButton(DependencyObject parent, string name)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Button button && System.Windows.Automation.AutomationProperties.GetName(button) == name) return button;
            var match = FindButton(child, name);
            if (match is not null) return match;
        }
        return null!;
    }

    private static void Render(IslandWindow window, IslandContent content, string name, double maxHeight)
    {
        var presenter = new ContentPresenter { Content = content, ContentTemplate =
            (DataTemplate)window.Resources[new DataTemplateKey(content.GetType())] };
        // Exact height, like the real pill: a fixed-height panel must be measured at that height.
        var root = new Border { Width = content.TargetWidth, Height = content.TargetHeight, Background = new SolidColorBrush(Color.FromRgb(20, 20, 22)),
            CornerRadius = new CornerRadius(24), Child = presenter, Resources = window.Resources };
        root.Measure(new Size(content.TargetWidth, double.PositiveInfinity));
        Assert.InRange(root.DesiredSize.Height, Math.Min(38, content.TargetHeight), maxHeight);
        root.Arrange(new Rect(root.DesiredSize));
        root.UpdateLayout();
        var output = Environment.GetEnvironmentVariable("MIMI_QA_DIR");
        if (string.IsNullOrEmpty(output)) return;
        Directory.CreateDirectory(output);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        if (name.StartsWith("compact"))
        {
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            var groups = 0;
            var previous = false;
            for (var x = 18; x < 56; x++)
            {
                var bright = Enumerable.Range(0, bitmap.PixelHeight).Any(y => {
                    var offset = (y * bitmap.PixelWidth + x) * 4;
                    return pixels[offset] > 210 && pixels[offset + 1] > 210 && pixels[offset + 2] > 210;
                });
                if (bright && !previous) groups++;
                previous = bright;
            }
            Assert.Equal(2, groups);
        }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(output, name + ".png"));
        encoder.Save(file);
    }
}
