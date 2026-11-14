using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.DesktopTests;

/// <summary>
/// Tom's rule (14.11.2026): back, close, "close details" and every "Back to …" stand at the far right edge of their header or
/// tool row, apart from the actions (an isolating gap), last in the row's tab order, through the one shared <see cref="HeaderBar"/>. Layout runs
/// without an Avalonia application (plain borders); the page rows are checked in their XAML.
/// </summary>
public sealed class HeaderBarContractTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private const string Wv = "using:MeowChan.Desktop.WorkViews";

    private static Border Box(double width, double height = 32) => new() { Width = width, Height = height };

    private static void Layout(Control control, double width, double height = 40)
    {
        control.Measure(new Size(width, height));
        control.Arrange(new Rect(0, 0, width, height));
    }

    [Fact]
    public void TheEndSlotStandsAtTheRightEdgeAfterAnIsolatingGap()
    {
        Border start = Box(300), back = Box(120), close = Box(32);
        HeaderBar bar = HeaderBar.Create(start, back, close);
        Layout(bar, 600);
        Assert.Equal(600, close.Bounds.Right, 3);
        Assert.Equal(close.Bounds.Left - HeaderBar.EndSpacing, back.Bounds.Right, 3);
        // The start area ends EndGap before the way out (the 24 dip of the quick-tag header).
        double startRight = back.Bounds.Left - HeaderBar.EndGap;
        Assert.True(start.Bounds.Right <= startRight + 0.001, $"start {start.Bounds} reaches into the gap before {back.Bounds}");
        Assert.Equal(startRight, bar.GapStart!.Value, 3);
        Assert.Equal(24, HeaderBar.EndGap);
    }

    [Fact]
    public void AHiddenWayOutTakesNoRoomAndARowWithoutOneHasNoGap()
    {
        Border start = Box(200), back = Box(120), close = Box(32);
        back.IsVisible = false;
        HeaderBar bar = HeaderBar.Create(start, back, close);
        Layout(bar, 500);
        Assert.Equal(500, close.Bounds.Right, 3);
        Assert.NotNull(bar.GapStart);
        close.IsVisible = false;
        Layout(bar, 500);
        Assert.Null(bar.GapStart); // nothing to set apart: the start content gets the whole row
    }

    [Fact]
    public void TheWayOutIsLastInTheRowsTabOrder()
    {
        Button action = new(), close = new();
        HeaderBar bar = HeaderBar.Create(new StackPanel { Children = { action } }, close);
        Assert.True(bar.EndSlotIsLast);
        Assert.Same(close, bar.Children[^1]);
        Assert.Equal([close], bar.EndChildren);
        HeaderBar wrong = new();
        Button early = new();
        HeaderBar.SetIsEnd(early, true);
        wrong.Children.Add(early);
        wrong.Children.Add(new TextBlock());
        Assert.False(wrong.EndSlotIsLast);
    }

    [Fact]
    public void TheCloseIsTheDrawnCrossOnASquareWithItsNameAndRenamingKeepsTheIcon()
    {
        Button close = new();
        HeaderBar.MakeClose(close, "Close find");
        Assert.Equal(WorkspaceIcons.Close, WorkspaceIcons.KeyOf(close.Content));
        Assert.Contains(WorkspaceIcons.SquareClass, close.Classes);
        Assert.Equal("Close find", AutomationProperties.GetName(close));
        Assert.Equal("Close find", ToolTip.GetTip(close));
        object icon = close.Content!;
        HeaderBar.MakeClose(close, "Suche schließen", "Suche schließen · Esc");
        Assert.Same(icon, close.Content);
        Assert.Equal("Suche schließen", AutomationProperties.GetName(close));
        Assert.Equal("Suche schließen · Esc", ToolTip.GetTip(close));
    }

    // Inventory (14.11.): every way out moved to an end slot. The gallery viewer keeps its own overlay placement (WV4), Rules,
    // Sources, Plugins, Diagnostics and Statistics are other workers' surfaces; window modals have the caption's ×.
    public static TheoryData<string, string> WaysOut => new()
    {
        { "Desktop/Views/Archive/ArchivePage.axaml", "ArchiveGalleryReturn" },
        { "Desktop/Views/Archive/ArchivePage.axaml", "ArchiveMediaReturn" },
        { "Desktop/Views/Archive/ArchivePage.axaml", "ArchiveBackButton" },
        { "Desktop/Views/Archive/ArchivePage.axaml", "ArchiveCloseReader" },
        { "Desktop/Views/Archive/ArchivePage.axaml", "ReaderFindClose" },
        { "Desktop/Views/Archive/ArchivePage.axaml", "ReaderTagClose" },
        { "Desktop/Views/Archive/ArchivePage.axaml", "ClosePreviewButton" },
        { "Desktop/Shell/MainWindow.axaml", "DownloadSearchClose" },
        { "Desktop/Shell/MainWindow.axaml", "InspectorCloseButton" },
        { "Desktop/Shell/MainWindow.axaml", "ShellHelpClose" },
    };

    [Theory]
    [MemberData(nameof(WaysOut))]
    public void EveryWayOutSitsInAHeaderBarEndSlot(string file, string name)
    {
        XElement control = Named(file, name);
        Assert.Equal("True", EndAttribute(control));
        Assert.Equal("HeaderBar", control.Parent!.Name.LocalName);
        // Text content would come back with the localizer: the closes take their names in code, the returns from their command.
        Assert.DoesNotContain(control.Attributes(), attribute => attribute.Name.LocalName == "Grid.Column");
    }

    [Fact]
    public void EveryHeaderBarDeclaresItsEndSlotLastAndOnlyButtonsInIt()
    {
        int bars = 0;
        foreach (string file in Directory.EnumerateFiles(Root("Desktop"), "*.axaml", SearchOption.AllDirectories))
            foreach (XElement bar in XDocument.Load(file).Descendants().Where(element => element.Name.LocalName == "HeaderBar"))
            {
                bars++;
                XElement[] children = bar.Elements().ToArray();
                int firstEnd = Array.FindIndex(children, child => EndAttribute(child) == "True");
                string where = Path.GetFileName(file) + "/" + ((string?)bar.Attribute(X + "Name") ?? "HeaderBar");
                Assert.True(firstEnd > 0, where + " has no start content or no end slot.");
                Assert.All(children.Skip(firstEnd), child => Assert.True(EndAttribute(child) == "True", where + ": start content after the way out."));
                Assert.All(children.Skip(firstEnd), child => Assert.Contains(child.Name.LocalName, new[] { "Button", "ToggleButton" }));
            }
        Assert.True(bars >= 7, "Expected the reader, find, tag, preview, search, inspector and help rows: " + bars);
    }

    // The close of the large reader is the rightmost control of the reader header; the state returns sit directly left of it.
    [Fact]
    public void TheReaderHeaderEndsWithTheCloseAndTheToolbarKeepsNoWayOut()
    {
        XElement page = XDocument.Load(Root("Desktop/Views/Archive/ArchivePage.axaml")).Root!;
        XElement header = page.Descendants().Single(element => (string?)element.Attribute(X + "Name") == "ReaderHeaderBar");
        Assert.Equal(["ArchiveReaderTitle", "ArchiveGalleryReturn", "ArchiveMediaReturn", "ArchiveBackButton", "ArchiveCloseReader"],
            header.Elements().Select(element => (string?)element.Attribute(X + "Name")));
        XElement toolbar = page.Descendants().Single(element => (string?)element.Attribute(X + "Name") == "ReaderToolbar");
        string[] ids = toolbar.Descendants().Select(element => (string?)element.Attribute(XName.Get("WorkViewElement.Id", Wv))).OfType<string>().ToArray();
        foreach (string wayOut in new[] { "reader.toolbar.close", "reader.toolbar.gallery-return", "reader.toolbar.media-return", "reader.header.back" })
            Assert.DoesNotContain(wayOut, ids);
    }

    // Work views never reduce a way out: an end-slot control with a work-view id is Required in every view.
    [Fact]
    public void EveryWayOutWithAWorkViewIdIsRequiredInEveryView()
    {
        int checkedIds = 0;
        foreach (string file in Directory.EnumerateFiles(Root("Desktop"), "*.axaml", SearchOption.AllDirectories))
            foreach (XElement end in XDocument.Load(file).Descendants().Where(element => EndAttribute(element) == "True"))
                if ((string?)end.Attribute(XName.Get("WorkViewElement.Id", Wv)) is { } id)
                {
                    checkedIds++;
                    Assert.All(WorkViewSetting.All, view => Assert.Equal(Presence.Required, WorkViewPolicy.Resolve(id, view)));
                }
        Assert.Equal(4, checkedIds);
    }

    // The converted closes are named in code (MakeClose / an icon-only command), never given text content again.
    [Fact]
    public void TheConvertedClosesAreNotGivenTextContentAgain()
    {
        string find = File.ReadAllText(Root("Desktop/Views/Archive/ArchivePage.ThreadFind.cs"));
        string tags = File.ReadAllText(Root("Desktop/Views/Archive/ArchivePage.Tags.cs"));
        string presentation = File.ReadAllText(Root("Desktop/Shell/MainWindow.Presentation.cs"));
        string fullscreen = File.ReadAllText(Root("Desktop/Shell/MainWindow.Fullscreen.cs"));
        Assert.DoesNotContain("ReaderFindClose.Content", find);
        Assert.Contains("HeaderBar.MakeClose(ReaderFindClose", find);
        Assert.DoesNotContain("ReaderTagClose.Content", tags);
        Assert.Contains("HeaderBar.MakeClose(ReaderTagClose", tags);
        Assert.Contains("HeaderBar.MakeClose(DownloadSearchClose", presentation);
        Assert.Contains("CommandBindings.Apply(ShellHelpClose, \"window.close\", iconOnly: true)", fullscreen);
        Assert.Contains("HeaderBar.MakeClose(ClosePreviewButton", File.ReadAllText(Root("Desktop/Views/Archive/ArchivePage.axaml.cs")));
    }

    private static string? EndAttribute(XElement element) =>
        element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "HeaderBar.IsEnd")?.Value;

    private static XElement Named(string file, string name) =>
        XDocument.Load(Root(file)).Descendants().Single(element => (string?)element.Attribute(X + "Name") == name);

    private static string Root(string relative, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative));
}
