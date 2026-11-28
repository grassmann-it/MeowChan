using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using static MeowChan.DesktopTests.ReaderMediaSidebarLayoutTests;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class ReaderSidebarAlignmentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullHeightSidebarKeepsTheStableHeaderAndPostsColumnIsolation(bool right) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        Assert.Equal(48, ReaderMediaSidebar.HeaderLineHeight);
        Assert.Equal(8, ReaderMediaSidebar.HeaderTopInset);
        ArchivePage page = Page();
        Window window = new() { Width = 1440, Height = 720, Content = page };
        void LayoutAt(double height)
        {
            window.PlatformImpl!.Resize(new Size(1440, height), WindowResizeReason.User);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
        try
        {
            window.Show();
            Grid pane = page.FindControl<Grid>("ArchiveReaderPane")!;
            Border posts = page.FindControl<Border>("ArchiveReaderContent")!;
            StackPanel postsHeader = Assert.Single(pane.Children.OfType<StackPanel>(), panel => panel.Name == "ReaderHeaderRows");
            var preferences = ReaderMediaPreferences.From(new Config
            {
                ReaderMediaSidebar = true,
                ReaderMediaSidebarScope = Config.ReaderMediaSidebarScopeAlways,
                ReaderMediaSidebarWidth = Config.ReaderMediaSidebarWidthMinimum,
                ReaderMediaSidebarMinimumWidth = Config.ReaderMediaSidebarMinimumWidthMinimum
            });
            page.ApplyReaderMediaPreferences(preferences with { SidebarRight = right });
            LayoutAt(720);
            ReaderMediaSidebar sidebar = Assert.IsType<ReaderMediaSidebar>(page.MediaSidebar);
            Assert.IsType<MediaCompactHeader>(sidebar.HeaderRow);
            GridSplitter splitter = Assert.Single(pane.Children.OfType<GridSplitter>());
            Assert.True(page.IsMediaSidebarShown);
            Assert.Equal(right ? 2 : 0, Grid.GetColumn(sidebar));
            Assert.Equal(right ? 0 : 2, Grid.GetColumn(posts));
            Assert.Equal(Grid.GetColumn(posts), Grid.GetColumn(postsHeader));
            Assert.Equal(1, Grid.GetColumnSpan(postsHeader));
            Assert.Equal(1, Grid.GetColumn(splitter));
            BooruComponentDispatcher.WaitUntil(
                () => AutomationProperties.GetName(sidebar) == Texts.T("reader.sidebar.regionEmpty"),
                "the empty sidebar announcement");
            Assert.False(sidebar.PositionPill.IsVisible);
            Assert.Empty(sidebar.Title.Text!);
            double emptyHeight = sidebar.HeaderRow.Bounds.Height;
            // Tom 19:55: “Der Kopf ist einzeilig, solange es passt.”
            Assert.Equal(32, emptyHeight);

            string file = new string('W', 250) + ".png";
            sidebar.Describe("No. 123", file, "", true, savedBytes: 100L * 1024 * 1024);
            LayoutAt(720);
            double headerHeight = sidebar.HeaderRow.Bounds.Height;
            double frameHeight = sidebar.Frame.Bounds.Height;
            Control details = sidebar.Details;
            double occupied = frameHeight + details.Bounds.Height;
            Assert.Equal(48, headerHeight);
            // Tom 19:55: “Unten liegt eine feste Infoleiste”.
            Assert.True(details.IsVisible);
            TextBlock meta = ((MediaCompactHeader)sidebar.HeaderRow).Meta;
            Assert.Equal(file + " · 100 MiB", ToolTip.GetTip(meta));
            Assert.Contains("…", meta.Text);
            Assert.EndsWith(".png · 100 MiB", meta.Text);
            Assert.True(meta.TranslatePoint(default, sidebar)!.Value.Y < sidebar.Card.TranslatePoint(default, sidebar)!.Value.Y);
            LayoutAt(900);
            Assert.Equal(headerHeight, sidebar.HeaderRow.Bounds.Height);
            Assert.True(sidebar.Frame.Bounds.Height > frameHeight);
            Assert.Equal(180, sidebar.Frame.Bounds.Height + details.Bounds.Height - occupied, 0.5);
            Assert.Equal(pane.Bounds.Height, sidebar.Bounds.Height);
        }
        finally { window.Close(); page.EndLifetime(); }
    });
}
