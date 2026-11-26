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
        page.ChooseMediaTileSize = _ => true;
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
            page.CompactDensity = () => false;
            TaggingLayoutRhythmTests.RenderSnapshot(page, queue);
            page.RestorePreviewColumn(true);

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
            // Let the switch carry keyboard focus to the last focused entry before scrolling elsewhere.
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            ListBoxItem tileItem = ScrollTo(page.TileGrid, 0);
            TaggingQueueTileView tile = ById<TaggingQueueTileView>(tileItem, "TaggingQueueTile");
            Assert.Equal("#1", tile.NumberText.Text);
            TaggingQueueNextMarker tileMarker = Assert.Single(tile.GetVisualDescendants().OfType<TaggingQueueNextMarker>());
            Assert.Same(chevron, MarkerMask(tileMarker));
            Assert.Equal(Texts.T("tagging.page.next"), ToolTip.GetTip(tileMarker));
            Assert.StartsWith(tile.Row!.AccessibleName, AutomationProperties.GetName(tile));
            TextBlock groupLine = ById<TextBlock>(tile, "TaggingTileGroup");
            Assert.Equal(group.Name, groupLine.Text);
            Assert.True(new Rect(tile.Bounds.Size).Contains(BoundsIn(groupLine, tile)));

            // Padding audit (Opus, items 2–4): both caption gaps are 4 DIP, with 8 DIP around the content and below the list.
            Assert.Equal(8, page.Bounds.Height - BoundsIn(ById<Border>(page, "TaggingListSurface"), page).Bottom, 1);
            Assert.True(page.MediaTileSizeInput!.StepBy(-1));
            foreach (MediaTileSize size in Enum.GetValues<MediaTileSize>())
            {
                tileItem = ScrollTo(page.TileGrid, 0);
                tile = ById<TaggingQueueTileView>(tileItem, "TaggingQueueTile");
                groupLine = ById<TextBlock>(tile, "TaggingTileGroup");
                Rect preview = BoundsIn(tile.Preview, tile);
                Rect name = BoundsIn(tile.NameText, tile);
                Rect groupBounds = BoundsIn(groupLine, tile);
                MediaTileAppearance media = MediaTileMetrics.For(size, MediaTileCaption.All);
                Assert.Equal(media.PreviewHeight, preview.Height);
                Assert.Equal(media.CellWidth, tileItem.Bounds.Width);
                Assert.Equal(tileItem.Bounds.Height, tile.Bounds.Height);
                Assert.Equal(8, preview.Top, 1);
                Assert.Equal(4, name.Top - preview.Bottom, 1);
                Assert.Equal(4, groupBounds.Top - name.Bottom, 1);
                Assert.Equal(8, tile.Bounds.Height - groupBounds.Bottom, 1);
                if (size != MediaTileSize.Large) Assert.True(page.MediaTileSizeInput.StepBy(1));
            }
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

    private static T ById<T>(Visual root, string id) where T : Control =>
        Assert.Single(root.GetVisualDescendants().OfType<T>(), control => AutomationProperties.GetAutomationId(control) == id);

    private static object? MarkerMask(TaggingQueueNextMarker marker) =>
        Assert.IsType<ImageBrush>(Assert.IsType<Border>(marker.Child).OpacityMask).Source;
}
