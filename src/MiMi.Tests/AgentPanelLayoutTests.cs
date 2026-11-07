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
                Render(window, new AgentActivityPanelView(rows), "activity", 470);
                var indicator = new AgentIndicatorViewModel();
                indicator.Update("Thursday · Thinking", MiMi.App.UI.Mascot.MascotExpression.Focused, 0);
                Render(window, new AgentCompactView(indicator), "compact", 60);
                indicator.Update("3 agents active", MiMi.App.UI.Mascot.MascotExpression.Focused, 0);
                Render(window, new AgentCompactView(indicator), "compact-multiple", 60);
                var shortWidth = new AgentCompactView(indicator).TargetWidth;
                indicator.Update("Thursday · Waiting for your approval to run a command", MiMi.App.UI.Mascot.MascotExpression.Alert, 1);
                Assert.True(new AgentCompactView(indicator).TargetWidth > shortWidth);
                Render(window, new AgentCompactView(indicator), "compact-long", 60);
                var card = new AgentCardViewModel { ApprovalId = "preview" };
                card.Update(AgentEventFactory.Create("thursday", "chat", AgentEventTypes.ApprovalRequested,
                    title: "Permission required", message: "Allow this tool family for this task?", project: "ImpressiveBackup",
                    approvalRequestId: "preview", metadata: new Dictionary<string, string> {
                        ["action_kind"] = "bash", ["command"] = "cd C:/Users/basit/ImpressiveBackup\nls -la\ncat package.json" }));
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

    private static void Render(IslandWindow window, IslandContent content, string name, double maxHeight)
    {
        var presenter = new ContentPresenter { Content = content, ContentTemplate =
            (DataTemplate)window.Resources[new DataTemplateKey(content.GetType())] };
        var root = new Border { Width = content.TargetWidth, MinHeight = content.TargetHeight, Background = new SolidColorBrush(Color.FromRgb(20, 20, 22)),
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
