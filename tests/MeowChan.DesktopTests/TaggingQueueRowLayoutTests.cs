using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class TaggingQueueRowLayoutTests
{
    [Fact]
    public void PreviewAndFocusFitInsideRealRowsAfterTogglingAndScrolling() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        Texts.Initialize();
        Texts.SetLanguage("en");
        using TempDirectory profile = new();
        DesktopSessionHost host = new(profile.Path, []);
        TaggingPage page = new(host);
        Window window = new() { Width = 1100, Height = 550, Content = page };
        try
        {
            window.Show();
            TagQueueGroup group = new("fixture", "Fixture group", 0, []);
            TagQueueItem[] items = Enumerable.Range(0, 80).Select(index => TaggingLayoutRhythmTests.Item() with
            {
                AssetId = "asset-" + index, MediaId = "media-" + index, GroupId = index == 0 ? group.Id : null,
                Name = "A filename long enough to wrap into a second line in the normal preview of the tagging queue.png"
            }).ToArray();
            TagQueueSnapshot queue = new(1, 0, [], items, TagQueueLoadState.Ready, [group]);
            object? chevron = null;
            foreach (bool compact in new[] { false, true })
            {
                page.CompactDensity = () => compact;
                TaggingLayoutRhythmTests.RenderSnapshot(page, queue);
                foreach (bool preview in new[] { true, false, true })
                {
                    page.RestorePreviewColumn(preview);
                    double height = compact ? WorkspaceMetrics.CompactRowHeight
                        : preview ? WorkspaceMetrics.TwoLineRowHeight : WorkspaceMetrics.NormalRowHeight;
                    chevron = CheckRow(0, height, preview, compact);
                    CheckRow(65, height, preview, compact);
                }
            }
            page.RestoreViewMode(TaggingViewMode.Grid);
            ListBoxItem tileItem = ScrollTo(page.TileGrid, 0);
            TaggingQueueTileView tile = Assert.Single(tileItem.GetVisualDescendants().OfType<TaggingQueueTileView>());
            Assert.Equal("#1", tile.NumberText.Text);
            TaggingQueueNextMarker tileMarker = Assert.Single(tile.GetVisualDescendants().OfType<TaggingQueueNextMarker>());
            Assert.Same(chevron, MarkerMask(tileMarker));
            Assert.Equal(Texts.T("tagging.page.next"), ToolTip.GetTip(tileMarker));
            Assert.StartsWith(tile.Row!.AccessibleName, AutomationProperties.GetName(tile));
            TextBlock groupLine = Assert.Single(tile.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == group.Name);
            Assert.True(new Rect(tile.Bounds.Size).Contains(BoundsIn(groupLine, tile)));
        }
        finally
        {
            window.Close();
            page.End();
            BooruComponentDispatcher.Wait(host.DisposeAsync().AsTask());
        }

        ListBoxItem ScrollTo(ListBox list, int index)
        {
            window.UpdateLayout();
            list.ScrollIntoView(index);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            return Assert.IsType<ListBoxItem>(list.ContainerFromIndex(index));
        }

        object? CheckRow(int index, double height, bool preview, bool compact)
        {
            ListBoxItem item = ScrollTo(page.List, index);
            TaggingQueueRowView row = Assert.Single(item.GetVisualDescendants().OfType<TaggingQueueRowView>());
            Assert.True(item.Focus(NavigationMethod.Tab));
            Assert.NotNull(item.FocusAdorner);
            Assert.Equal(preview, row.Preview.IsEffectivelyVisible);
            Rect outer = new(item.Bounds.Size);
            Rect inner = BoundsIn(row, item);
            Assert.True(outer.Height == height && outer.Inflate(1).Contains(inner),
                $"compact={compact}, preview={preview}, index={index}, outer={outer}, inner={inner}, expected height={height}");
            Assert.Equal(preview && !compact ? 2 : 1, row.FileName.TextLayout.TextLines.Count);
            Assert.True(outer.Contains(BoundsIn(row.FileName, item)));
            if (preview)
            {
                Rect picture = BoundsIn(row.Preview, item);
                Assert.Equal(compact ? 24 : 48, picture.Height);
                Assert.True(outer.Contains(picture), $"outer={outer}, picture={picture}");
            }
            TaggingQueueNextMarker marker = Assert.Single(row.GetVisualDescendants().OfType<TaggingQueueNextMarker>());
            Assert.Equal(row.Row!.AccessibleName, AutomationProperties.GetName(item));
            Assert.Equal(index == 0, marker.IsEffectivelyVisible);
            Assert.Equal(index == 0 ? Texts.T("tagging.page.next") : null, ToolTip.GetTip(marker));
            return index == 0 ? MarkerMask(marker) : null;
        }
    });

    private static Rect BoundsIn(Control control, Control ancestor) =>
        new(control.TranslatePoint(default, ancestor)!.Value, control.Bounds.Size);

    private static object? MarkerMask(TaggingQueueNextMarker marker) =>
        Assert.IsType<ImageBrush>(Assert.IsType<Border>(marker.Child).OpacityMask).Source;
}
