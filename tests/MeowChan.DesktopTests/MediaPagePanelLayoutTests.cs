using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

[Collection("Booru exact navigation")]
public sealed class MediaPagePanelLayoutTests
{
    [Fact]
    public void PanelToggleStaysInTheToolbarWhenThePreviewCannotFit()
    {
        using IDisposable platform = AvaloniaTestPlatform.EnterSkia();
        MediaTileEnterTests.Run(fixture =>
        {
            MediaPage page = fixture.Page;
            page.PanelMinimumPageWidthSource = () => 1000;

            void Layout(double width)
            {
                for (int pass = 0; pass < 3; pass++)
                {
                    page.InvalidateMeasure();
                    page.Measure(new Size(width, 800));
                    page.Arrange(new Rect(0, 0, width, 800));
                }
            }

            page.ApplyLayout(page.PageLayout with { Panel = true }, save: false);
            Layout(900);
            Assert.True(page.PageLayout.Panel);
            Assert.True(page.PanelToggleButton.IsVisible);
            Layout(1300);
            Assert.True(page.PanelToggleButton.IsVisible);

            page.ApplyLayout(page.PageLayout with { Panel = false }, save: false);
            Layout(900);
            Assert.False(page.PageLayout.Panel);
            Assert.True(page.PanelToggleButton.IsVisible);
            Layout(1300);
            Assert.True(page.PanelToggleButton.IsVisible);
        });
    }

    [Theory]
    [InlineData(900, 600, true)]
    [InlineData(1100, 780, false)]
    [InlineData(1440, 1000, true)]
    public void SidebarAndDividerFillHostIndependentlyOfGridHeader(int width, int height, bool right)
    {
        using IDisposable platform = AvaloniaTestPlatform.EnterSkia();
        MediaTileEnterTests.Run(fixture =>
        {
            MediaPage page = fixture.Page;
            MediaPanelSide side = right ? MediaPanelSide.Right : MediaPanelSide.Left;
            page.Resources["Workspace.Size.Control"] = 32d;
            page.PanelMinimumPageWidthSource = () => Config.MediaPanelMinimumPageWidthMinimum;
            page.ApplyLayout(page.PageLayout with { Panel = true, PanelSide = side }, save: false);
            Grid host = page.GetLogicalDescendants().OfType<Grid>().Single(control => control.Name == "MediaContentRow");
            ReaderMediaSidebar panel = null!;
            Border surface = page.GetLogicalDescendants().OfType<Border>().Single(control => control.Name == "MediaGridSurface");

            void Layout(int availableHeight)
            {
                // SizeChanged applies responsive columns; repeat a normal invalidation/layout pass to settle them.
                for (int pass = 0; pass < 3; pass++)
                {
                    foreach (Layoutable child in page.GetVisualDescendants().OfType<Layoutable>().Reverse()) child.InvalidateMeasure();
                    page.InvalidateMeasure();
                    page.Measure(new Size(width, availableHeight));
                    page.Arrange(new Rect(0, 0, width, availableHeight));
                }
                Assert.True(page.IsPanelShown);
                panel = Assert.IsType<ReaderMediaSidebar>(page.Panel);
                Assert.Equal(host.Bounds.Height, panel.Bounds.Height);
                Assert.Equal(0, panel.Bounds.Y);
                Assert.Equal(host.Bounds.Height, page.PanelHandle.Bounds.Height);
                Assert.Equal(2, Grid.GetRowSpan(panel));
                Assert.Equal(2, Grid.GetRowSpan(page.PanelHandle));
                Assert.Equal(1, Grid.GetRow(surface));
                Assert.Equal(2, Grid.GetRowSpan(panel));
                Assert.NotEqual(Grid.GetColumn(surface), Grid.GetColumn(panel));
                Assert.Equal(side == MediaPanelSide.Right ? new Thickness(1, 0, 0, 0) : new Thickness(0, 0, 1, 0), panel.BorderThickness);
                Assert.Equal(0, panel.Margin.Top);
                Assert.Equal(0, panel.Margin.Bottom);
                Assert.False(page.IsPanelHeaderLifted);
                Assert.Equal(32, panel.HeaderRow.Bounds.Height);
                Assert.Equal(8, panel.HeaderRow.TranslatePoint(default, panel)!.Value.Y);
                Assert.Equal(40, panel.Card.TranslatePoint(default, panel)!.Value.Y);
                Assert.True(panel.Card.Bounds.Height > 0);
                Assert.Equal(panel.Bounds.Height, panel.Card.TranslatePoint(default, panel)!.Value.Y + panel.Card.Bounds.Height);
            }
            Layout(height);
            double firstFrame = panel.Frame.Bounds.Height;
            double firstRatio = firstFrame / panel.Bounds.Height;
            Layout(height + 200);
            double expandedFrame = panel.Frame.Bounds.Height;
            Assert.True(expandedFrame > firstFrame);
            Assert.True(expandedFrame / panel.Bounds.Height >= firstRatio);

            // Expanding chrome in the neighbouring column must not add any inset or cap to the sidebar.
            Control filters = (Control)typeof(MediaPage).GetField("_filterSurface", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
            filters.IsVisible = true;
            Layout(height + 200);
            Assert.True(panel.Frame.Bounds.Height > 0);
            page.ApplyLayout(page.PageLayout with { Panel = false }, save: false);
            LayoutWithoutPanel();
            Assert.False(panel.IsVisible);
            Assert.Equal(host.Bounds.Width, surface.Bounds.Width + surface.Margin.Left + surface.Margin.Right);

            void LayoutWithoutPanel()
            {
                page.InvalidateMeasure();
                page.Measure(new Size(width, height));
                page.Arrange(new Rect(0, 0, width, height));
            }
        });
    }
}
