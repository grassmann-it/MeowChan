using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Avalonia;
using MeowChan.Desktop;
using Ursa.Controls;

namespace MeowChan.DesktopTests;

/// <summary>
/// The ActionToolBar stretch option (opt-in; the Archive List|Table pair used it until 1.1.1 U04 made it a compact end segment): the two halves share the
/// width equally, with the bar's item gap kept in front of "More", which keeps its size. The panel needs an Avalonia application to
/// measure and arrange, so the contract is checked on the pure slot rule, the shared constants and the XAML; the real look at the default
/// list width, light/dark and the narrow widths are the toolbar smokes.
/// </summary>
public sealed class ArchiveSegmentStretchTests
{
    private static string Root(string path, [CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../..", path));

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XElement Toolbar(string name) => XDocument.Load(Root("Desktop/Views/Archive/ArchivePage.axaml")).Descendants()
        .Single(element => element.Name.LocalName == "ActionToolBar" && (string?)element.Attribute(X + "Name") == name);

    private static bool Stretches(XElement item) =>
        item.Attributes().SingleOrDefault(attribute => attribute.Name.LocalName == "ActionToolBar.Stretch")?.Value == "True";

    // ---- the default list width: the pair fills the bar up to "More" and its gap ---------------------------------------------

    [Fact]
    public void AtTheDefaultListWidthThePairFillsTheBarUpToMoreAndTheItemGapAndMoreKeepsItsSize()
    {
        const double bar = 330;
        // "More" is a 32-wide toggle; its margin in front of it is the bar's item gap (the toggle itself is never resized).
        Thickness moreMargin = ActionToolBar.MoreMargin(new Thickness());
        double moreSlot = ActionToolBar.MoreSlot(32, 32, moreMargin);
        Assert.Equal(ActionToolBarPanel.ItemGap, moreMargin.Left);
        Assert.Equal(8, ActionToolBarPanel.ItemGap);
        Assert.Equal(40, moreSlot);
        // List (64 natural) | Table (64 natural, its edge overlaps the neighbour by 1 through Margin -1,0,0,0 -> desired 63).
        double[] slots = ActionToolBarPanel.StretchWidths(bar - moreSlot, [64, 63], [0, -1]);
        // The two slots end exactly where "More" begins, minus the gap that is part of its slot ...
        Assert.Equal(bar - moreSlot, slots.Sum(), 6);
        Assert.Equal(bar - 32 - ActionToolBarPanel.ItemGap, slots.Sum(), 6);
        // ... and the two halves look equally wide (Table's visible width is its slot plus the 1 it overlaps).
        Assert.Equal(slots[0], slots[1] + 1, 6);
        Assert.True(slots[0] > 64 && slots[1] + 1 > 64);
        // The joined segments stand with no gap, so the slot sum is also the run's width on the bar.
        Assert.Equal(0, ActionToolBarPanel.LeadingGap(true, 0, true, 0));
    }

    [Theory]
    [InlineData(260)]
    [InlineData(330)]
    [InlineData(480)]
    [InlineData(900)]
    public void TheStretchedHalvesAlwaysFillTheSpanAndNeverGetNarrowerThanTheirNaturalWidth(double span)
    {
        double[] natural = [64, 63];
        double[] slots = ActionToolBarPanel.StretchWidths(span, natural, [0, -1]);
        Assert.Equal(Math.Max(span, natural.Sum()), slots.Sum(), 6);
        Assert.All(slots.Zip(natural), pair => Assert.True(pair.First >= pair.Second));
    }

    [Fact]
    public void WithoutSpareRoomTheNaturalWidthsStayAndNothingIsEverShrunk()
    {
        Assert.Equal(new double[] { 64, 63 }, ActionToolBarPanel.StretchWidths(127, [64, 63], [0, -1]));
        Assert.Equal(new double[] { 64, 63 }, ActionToolBarPanel.StretchWidths(100, [64, 63], [0, -1]));
        Assert.Empty(ActionToolBarPanel.StretchWidths(200, Array.Empty<double>(), Array.Empty<double>()));
    }

    [Fact]
    public void AWiderLabelKeepsItsNaturalWidthAndTheSpareRoomIsSplitEvenlyInsteadOfClipping()
    {
        // A long label (a 12-language case): the equal share (100.5 visible) would be narrower than its 120, so no half is cut.
        double[] slots = ActionToolBarPanel.StretchWidths(200, [120, 63], [0, -1]);
        Assert.Equal(200, slots.Sum(), 6);
        Assert.Equal(120 + 17.0 / 2, slots[0], 6);
        Assert.Equal(63 + 17.0 / 2, slots[1], 6);
        // The same pair with room for equal halves becomes equal.
        slots = ActionToolBarPanel.StretchWidths(300, [120, 63], [0, -1]);
        Assert.Equal(slots[0], slots[1] + 1, 6);
    }

    [Fact]
    public void ALoneStretchSegmentTakesTheWholeSpan()
    {
        Assert.Equal(new double[] { 210 }, ActionToolBarPanel.StretchWidths(210, [64], [0]));
    }

    // ---- the option ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void StretchIsAnOptInOfTheItemAndOffByDefault()
    {
        Avalonia.Controls.Primitives.ToggleButton item = new();
        Assert.False(ActionToolBar.GetStretch(item));
        ActionToolBar.SetStretch(item, true);
        Assert.True(ActionToolBar.GetStretch(item));
    }

    [Fact]
    public void TheArchiveListAndTablePairIsACompactEndSegmentThatNeverOverflows()
    {
        XElement[] items = Toolbar("ArchiveResultActions").Elements().Where(element => !element.Name.LocalName.Contains('.')).ToArray();
        // U04 (1.1.1): the toggle is a compact segment docked at the right edge (End), not a stretched half of the bar.
        Assert.Empty(items.Where(Stretches));
        XElement[] pair = items.TakeLast(2).ToArray();
        Assert.Equal(["ArchiveLayoutList", "ArchiveLayoutTable"], pair.Select(item => (string)item.Attribute(X + "Name")!));
        Assert.All(pair, item =>
        {
            Assert.Contains("segment", (string)item.Attribute("Classes")!);
            Assert.Equal("True", item.Attributes().Single(attribute => attribute.Name.LocalName == "ActionToolBar.End").Value);
            Assert.Equal("Never", item.Attributes().Single(attribute => attribute.Name.LocalName == "ToolBar.OverflowMode").Value);
            Assert.Null(item.Attribute("HorizontalAlignment"));
        });
    }

    [Fact]
    public void TheReaderMediaPairStaysAsItWasBecauseItsBarDropsThePairBeforeAnythingElse()
    {
        // ReaderMediaTools is prioritized: Sort, Favorites and Rating outrank Tiles|Table, so the pair is the first thing under More and
        // never the last thing on a bar that shows More. The rule would never act there; no option is set.
        Assert.DoesNotContain(Toolbar("ReaderMediaTools").Elements().Where(element => !element.Name.LocalName.Contains('.')), Stretches);
    }

    // ---- the overflow keeps the pair atomic ------------------------------------------------------------------------------------

    [Fact]
    public void TheOverflowDecisionUsesTheNaturalWidthsSoTheStretchNeverChangesWhatMovesUnderMore()
    {
        OverflowMode[] modes = [OverflowMode.AsNeeded, OverflowMode.AsNeeded, OverflowMode.AsNeeded];
        // List | Table | Gallery (group 1): 64 + 63 + 16 + 90 = 233; More 40.
        for (double available = 0; available <= 400; available++)
        {
            (bool[] overflow, _) = ActionOverflowLayout.ResolveSpaced([64, 63, 90], [0, 0, 1], [true, true, false], modes, [true, true, true], available, 40);
            Assert.True(overflow[0] == overflow[1], $"{available}: the pair was split");
        }
        // Wide enough for everything: nothing is under More, so the panel does not stretch (the pair is not followed by More).
        (bool[] all, double width) = ActionOverflowLayout.ResolveSpaced([64, 63, 90], [0, 0, 1], [true, true, false], modes, [true, true, true], 330, 40);
        Assert.Equal(new[] { false, false, false }, all);
        Assert.Equal(233, width);
        // Narrow: the gallery moves, the pair is the last thing on the bar beside More and its natural width (127) still fits 330 - 40.
        (bool[] narrow, _) = ActionOverflowLayout.ResolveSpaced([64, 63, 90], [0, 0, 1], [true, true, false], modes, [true, true, true], 200, 40);
        Assert.Equal(new[] { false, false, true }, narrow);
        // Narrower than the pair plus More: the whole pair is under More, nothing is left on the bar to stretch.
        (bool[] gone, double goneWidth) = ActionOverflowLayout.ResolveSpaced([64, 63, 90], [0, 0, 1], [true, true, false], modes, [true, true, true], 150, 40);
        Assert.Equal(new[] { true, true, true }, gone);
        Assert.Equal(0, goneWidth);
    }
}
