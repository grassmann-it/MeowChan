using System.Runtime.CompilerServices;
using Avalonia;
using MeowChan.Archive;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

// Light/Dark review 2026-11-13, item 8: a hidden spoiler area is about 1.1:1 against the active card in both themes, so the reader
// body strokes a 1-dip interactive outline around the area each spoiler run paints. These are the pure parts (ranges, merging,
// pixel snapping) and the source contract; the drawing itself is covered by the reader smokes.
public sealed class ReaderSpoilerOutlineTests
{
    private static readonly ResourceId Thread = new("4chan", "g", "100");

    private static ArchiveQuoteRow Quote(string label) => new(new ArchiveQuote(
        new ArchivePostId(Thread, "105"), new ArchivePostId(Thread, label.TrimStart('>')), label, null));

    private static List<(int Start, int End)> Ranges(ReaderBodyDocument document)
    {
        List<(int Start, int End)> ranges = [(99, 100)]; // Cleared first.
        ReaderSpoilerOutline.Ranges(document, ranges);
        return ranges;
    }

    [Fact]
    public void ASpoilerCutByAReferenceOrALinkIsOneRangeAndSeparateSpoilersStaySeparate()
    {
        const string text = "a >>101 b and c https://example.org/x d";
        ArchiveTextMark[] marks = [new(ArchiveTextMarkKind.Spoiler, 0, 9), new(ArchiveTextMarkKind.Spoiler, 14, 24)];
        ReaderBodyDocument document = ReaderBodyFormatting.Format(text, [Quote(">>101")], marks);
        Assert.True(document.Segments.Count(segment => segment.IsSpoiler) > 2); // The reference and the link cut the spoilers.
        Assert.Equal(new List<(int Start, int End)> { (0, 9), (14, 38) }, Ranges(document));
    }

    [Fact]
    public void ABodyWithoutFormattingOrSpoilersHasNoRanges()
    {
        Assert.Empty(Ranges(ReaderBodyFormatting.Format("plain text", [])));
        Assert.Empty(Ranges(ReaderBodyFormatting.Format(">green\nno spoiler", [])));
        List<(int Start, int End)> ranges = [(1, 2)];
        ReaderSpoilerOutline.Ranges(null, ranges);
        Assert.Empty(ranges);
    }

    [Fact]
    public void RunsThatTouchOnOneLineBecomeOneBoxButOtherLinesAndGapsStayApart()
    {
        List<Rect> areas = [new Rect(0, 0, 30, 18)]; // A box of the previous line: never merged.
        int lineFirst = areas.Count;
        ReaderSpoilerOutline.Add(areas, lineFirst, new Rect(10, 20, 20, 17));
        ReaderSpoilerOutline.Add(areas, lineFirst, new Rect(30.3, 19, 15, 19)); // The next run (other font height) touches it.
        ReaderSpoilerOutline.Add(areas, lineFirst, new Rect(60, 20, 10, 17)); // A gap: a second spoiler on the same line.
        ReaderSpoilerOutline.Add(areas, lineFirst, new Rect(45, 20, 15, 17)); // Bridges both boxes (right-to-left order).
        ReaderSpoilerOutline.Add(areas, lineFirst, new Rect(80, 20, 0.2, 17)); // A line break: no width, no box.
        Assert.Equal(new[] { new Rect(0, 0, 30, 18), new Rect(10, 19, 60, 19) }, areas);
    }

    [Theory]
    [InlineData(1.0, 10.5, 20.5, 29, 15)]
    [InlineData(2.0, 10.5, 21, 29.5, 14.5)]
    public void TheStrokeCoversTheOutermostDevicePixelsOfTheArea(double scale, double x, double y, double width, double height)
    {
        Rect stroke = ReaderSpoilerOutline.Stroke(new Rect(10.2, 20.4, 30.1, 15.7), scale);
        Assert.Equal(new Rect(x, y, width, height), stroke);
        Assert.Equal(new Rect(0.5, 0.5, 0, 0), ReaderSpoilerOutline.Stroke(new Rect(0, 0, 0.4, 0.4), double.NaN)); // Never negative.
    }

    // The outline is the theme's interactive outline colour, drawn only while the spoilers are hidden and after the text (so the
    // selection highlight and the hidden text colour stay as they were); the run background stays the spoiler area.
    [Fact]
    public void TheBodyStrokesHiddenSpoilersWithTheInteractiveOutlineAfterTheText()
    {
        string source = File.ReadAllText(Root("Desktop/ReaderBodyTextBlock.cs")).ReplaceLineEndings("\n");
        Assert.Contains("this[!SpoilerOutlineBrushProperty] = new DynamicResourceExtension(\"Workspace.ControlBorder\");", source);
        Assert.Contains("base.RenderTextLayout(context, origin);\n        if (_spoilerOutlinePen is not { } pen || _spoilerRanges.Count == 0 || SpoilersShown) return;", source);
        Assert.Contains("run[!TextElement.BackgroundProperty] = new DynamicResourceExtension(\"Workspace.ReaderSpoiler\");", source);
        Assert.Contains("new ImmutablePen(outline.ToImmutable(), 1)", source);
    }

    private static string Root(string path, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", path));
}
