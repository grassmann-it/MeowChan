using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.VisualTree;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

/// <summary>
/// Review 16.11. on the gallery's info line: the status segment (the slideshow reason, "Rating saved", the overlay note) sits in a panel that gives each
/// text the room it has, so a long German reason wraps to two lines and then ends in an ellipsis with the whole text as its tooltip instead of running
/// out of the window at half-screen width; and the rating segment is a link button, so Enter and Space work and the focus adorner shows.
/// </summary>
[Collection("Gallery viewer construction")]
public sealed class GalleryInfoStatusLayoutTests
{
    private static T Find<T>(GalleryViewer viewer, string name) where T : Control =>
        viewer.InfoLine.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);

    // The viewer builds its filmstrip with a hand cursor, which needs the platform's cursor factory: tests without a window bring a recording one.
    private static void WithViewer(Action<GalleryViewer> test) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
        test(new GalleryViewer());
    });

    [Fact]
    public void TheStatusTextsWrapToTwoLinesAndThenEndInAnEllipsisWithTheWholeTextAsTheirTip() => WithViewer(viewer =>
    {
        {
            // A WrapPanel measures each text with the width it has; a horizontal StackPanel measured them without a limit and never wrapped.
            WrapPanel segment = Find<WrapPanel>(viewer, "GalleryStatusSegment");
            Assert.Equal(Avalonia.Layout.Orientation.Horizontal, segment.Orientation);
            Assert.Equal(3, segment.Children.Count);
            const string reason = "Die Diashow wartet, weil das aktuelle Medium noch nicht vollständig gespeichert ist und der Bildschirm gerade nur halb so breit ist";
            foreach (TextBlock text in segment.Children.OfType<TextBlock>())
            {
                Assert.Equal(TextWrapping.Wrap, text.TextWrapping);
                Assert.Equal(GalleryViewer.StatusMaxLines, text.MaxLines);
                Assert.Equal(2, text.MaxLines);
                Assert.Equal(TextTrimming.CharacterEllipsis, text.TextTrimming);
                Assert.Null(ToolTip.GetTip(text));
                text.Text = reason;
                Assert.Equal(reason, ToolTip.GetTip(text));
                text.Text = string.Empty;
                Assert.Null(ToolTip.GetTip(text));
            }
        }
    });

    [Fact]
    public void TheRatingSegmentOfTheInfoLineIsALinkButtonInTheRow() => WithViewer(viewer =>
    {
        {
            Button button = Find<Button>(viewer, "GalleryRatingStatusButton");
            TextBlock text = Find<TextBlock>(viewer, "GalleryRatingStatus");
            Assert.Same(text, button.Content);
            Assert.Contains("link", button.Classes);
            Assert.Same(viewer.InfoLine.GetLogicalDescendants().OfType<GalleryInfoSegmentsPanel>().Single(), button.GetLogicalParent());
            // The text is no stand-alone child of the row any more (it would show twice and could not be focused as a button).
            Assert.DoesNotContain(text, viewer.InfoLine.GetLogicalDescendants().OfType<GalleryInfoSegmentsPanel>().Single().Children);
        }
    });
}
