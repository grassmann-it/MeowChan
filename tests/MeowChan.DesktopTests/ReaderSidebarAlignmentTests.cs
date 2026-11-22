using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using static MeowChan.DesktopTests.ReaderMediaSidebarLayoutTests;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class ReaderSidebarAlignmentTests
{
    [Fact]
    public void FullHeightSidebarKeepsTheStableHeaderAndPostsColumnIsolation() => OnDispatcher(() =>
    {
        Assert.Equal(32, ReaderMediaSidebar.HeaderLineHeight);
        Assert.Equal(8, ReaderMediaSidebar.HeaderGap);
        Assert.Equal(MediaPage.PanelHeaderLineHeight, ReaderMediaSidebar.HeaderLineHeight);
        ArchivePage page = Page();
        try
        {
            Grid root = page.FindControl<Grid>("ArchiveRoot")!;
            Grid pane = page.FindControl<Grid>("ArchiveReaderPane")!;
            Border posts = page.FindControl<Border>("ArchiveReaderContent")!;
            StackPanel postsHeader = Assert.Single(pane.Children.OfType<StackPanel>());
            var preferences = ReaderMediaPreferences.From(new Config
            {
                ReaderMediaSidebar = true,
                ReaderMediaSidebarScope = Config.ReaderMediaSidebarScopeAlways,
                ReaderMediaSidebarWidth = Config.ReaderMediaSidebarWidthMinimum,
                ReaderMediaSidebarMinimumWidth = Config.ReaderMediaSidebarMinimumWidthMinimum
            });
            foreach (bool right in new[] { false, true })
            {
                page.ApplyReaderMediaPreferences(preferences with { SidebarRight = right });
                Layout(root, new Size(1440, 720));
                ReaderMediaSidebar sidebar = Assert.IsType<ReaderMediaSidebar>(page.MediaSidebar);
                GridSplitter splitter = Assert.Single(pane.Children.OfType<GridSplitter>());
                Assert.True(page.IsMediaSidebarShown);
                Assert.Equal(right ? 2 : 0, Grid.GetColumn(sidebar));
                Assert.Equal(right ? 0 : 2, Grid.GetColumn(posts));
                Assert.Equal(Grid.GetColumn(posts), Grid.GetColumn(postsHeader));
                Assert.Equal(1, Grid.GetColumnSpan(postsHeader));
                Assert.Equal(1, Grid.GetColumn(splitter));
                sidebar.ShowEmpty();
                Layout(root, new Size(1440, 720));
                Assert.Contains(sidebar.HeaderRow.GetVisualDescendants().OfType<TextBlock>(),
                    title => title.IsVisible && title.Classes.Contains("section-title") && title.Text == Texts.T("reader.sidebar.compact"));
                double emptyHeight = sidebar.HeaderRow.Bounds.Height;
                Assert.True(emptyHeight >= ReaderMediaSidebar.HeaderLineHeight);

                string file = new string('W', 250) + ".png";
                sidebar.Describe("No. 123", file, "100 MiB", true, "Copy post reference");
                Layout(root, new Size(1440, 720));
                double headerHeight = sidebar.HeaderRow.Bounds.Height;
                double frameHeight = sidebar.Frame.Bounds.Height;
                Assert.InRange(headerHeight, emptyHeight + 1, emptyHeight + 2 * ReaderMediaSidebar.HeaderLineHeight);
                TextBlock metadata = Assert.Single(sidebar.HeaderRow.GetVisualDescendants().OfType<TextBlock>(),
                    text => text.Name == "ReaderMediaSidebarHeader");
                Assert.Equal(sidebar.HeaderLine, ToolTip.GetTip(metadata));

                Layout(root, new Size(1440, 900));
                Assert.Equal(headerHeight, sidebar.HeaderRow.Bounds.Height);
                Assert.Equal(180, sidebar.Frame.Bounds.Height - frameHeight);
                Assert.Equal(pane.Bounds.Height, sidebar.Bounds.Height);
            }
        }
        finally { page.EndLifetime(); }
    });
}
