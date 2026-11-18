using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

// P0 responsive contract for the Tagging queue table. The compact geometry keeps every column in the viewport;
// trimmed cells expose their full value through TrimTipTextBlock and the row help text.
[Collection("Localization")]
public sealed class TaggingResponsiveLayoutTests : IDisposable
{
    private static readonly ResourceId Thread = new("4chan", "g", "123");
    private static readonly DateTimeOffset T0 = new(2026, 11, 14, 8, 0, 0, TimeSpan.Zero);

    public TaggingResponsiveLayoutTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    [Theory]
    [InlineData(760, true)]
    [InlineData(920, false)]
    [InlineData(1100, false)]
    [InlineData(1440, false)]
    public void QueueTableGeometryKeepsAllColumnsReachableAtTheTargetWidths(double pageWidth, bool compact)
    {
        double viewport = pageWidth - TaggingQueueRowView.TableViewportInset;

        Assert.Equal(compact, TaggingQueueRowView.UseCompactColumns(pageWidth));
        Assert.True(TaggingQueueRowView.MinimumTableWidth(showPreview: true, compact) <= viewport,
            $"preview table needs {TaggingQueueRowView.MinimumTableWidth(true, compact)} dip but has {viewport} dip at {pageWidth} dip");
        Assert.True(TaggingQueueRowView.MinimumTableWidth(showPreview: false, compact) <= viewport,
            $"table without preview needs {TaggingQueueRowView.MinimumTableWidth(false, compact)} dip but has {viewport} dip at {pageWidth} dip");
    }

    [Fact]
    public void NarrowRowHelpTextRetainsEveryValueWhenVisibleCellsTrim()
    {
        TagQueueItem item = new(Thread, "/media/123", "asset-1", new string('a', 64), 101, "media-1", "original", "p1",
            null, null, null, false, null, "file-1.png", SavedMediaKind.Image, TagQueueState.Done, T0);
        TaggingQueueRow row = new(item, 0, false, "Review");

        string details = row.AccessibleDetails(3);

        Assert.Contains("Type: Image", details);
        Assert.Contains("File: file-1.png", details);
        Assert.Contains("Thread or source: 4chan · /g/ · 123", details);
        Assert.Contains("Group: Review", details);
        Assert.Contains("Tags: 3", details);
        Assert.Contains("State: Finished", details);
    }
}
