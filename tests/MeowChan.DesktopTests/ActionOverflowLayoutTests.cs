using MeowChan.Desktop;
using Ursa.Controls;

namespace MeowChan.DesktopTests;

public sealed class ActionOverflowLayoutTests
{
    [Fact]
    public void BroadToolbarHasNoOverflowOrReservedButton()
    {
        Assert.Equal(new[] {false, false, false}, ActionOverflowLayout.Resolve(
            [80, 90, 70], [OverflowMode.Never, OverflowMode.AsNeeded, OverflowMode.AsNeeded],
            [true, true, true], 240, 40));
    }

    [Fact]
    public void ReservesMeasuredOverflowButtonBeforeChoosingActions()
    {
        Assert.Equal(new[] {false, true, true}, ActionOverflowLayout.Resolve(
            [80, 90, 70], [OverflowMode.Never, OverflowMode.AsNeeded, OverflowMode.AsNeeded],
            [true, true, true], 200, 40));
    }

    [Fact]
    public void BarebonesAndInvisibleColumnsNeverProduceAnEmptyOverflow()
    {
        Assert.Equal(new[] {false, false}, ActionOverflowLayout.Resolve(
            [90, 400], [OverflowMode.Never, OverflowMode.Always], [true, false], 120, 30));
    }

    [Fact]
    public void FutureActionsUseTheirOwnMeasuredWidthsAndPreservePriority()
    {
        Assert.Equal(new[] {false, false, true, true, true}, ActionOverflowLayout.Resolve(
            [60, 40, 200, 20, 15],
            [OverflowMode.Never, OverflowMode.AsNeeded, OverflowMode.AsNeeded, OverflowMode.AsNeeded, OverflowMode.Always],
            [true, true, true, true, true], 200, 30));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void ProportionalLayoutDoesNotDependOnPhysicalPixels(double scale)
    {
        Assert.Equal(new[] {false, true, true}, ActionOverflowLayout.Resolve(
            [60 * scale, 120 * scale, 30 * scale],
            [OverflowMode.Never, OverflowMode.AsNeeded, OverflowMode.AsNeeded], [true, true, true], 200 * scale, 30 * scale));
    }

    // Prioritized toolbars (the reader toolbar): the visual order stays, the keep-visible order comes from the priorities.
    [Fact]
    public void PrioritiesDecideWhatOverflowsFirstWhileItemOrderStays()
    {
        OverflowMode[] modes = [OverflowMode.Never, OverflowMode.AsNeeded, OverflowMode.AsNeeded, OverflowMode.AsNeeded, OverflowMode.AsNeeded];
        // Item 4 (the last on the bar) ranks right after item 1, so items 2 and 3 move into More before it.
        Assert.Equal(new[] { false, false, true, true, false }, ActionOverflowLayout.Resolve(
            [50, 50, 50, 50, 50], modes, [true, true, true, true, true], 200, 30, [0, 10, 30, 40, 20]));
        // Without priorities the historical rule holds: the tail of the bar overflows.
        Assert.Equal(new[] { false, false, false, true, true }, ActionOverflowLayout.Resolve(
            [50, 50, 50, 50, 50], modes, [true, true, true, true, true], 200, 30));
    }

    [Fact]
    public void PrioritizedOverflowIsAlwaysTheLowestRankedTailEvenWhenALaterItemWouldFit()
    {
        // Item 2 (rank 20) does not fit; item 3 (rank 30, narrow) would, but must not jump ahead of a more useful action.
        Assert.Equal(new[] { false, true, true }, ActionOverflowLayout.Resolve(
            [100, 120, 10], [OverflowMode.AsNeeded, OverflowMode.AsNeeded, OverflowMode.AsNeeded], [true, true, true], 150, 30, [10, 20, 30]));
        // Equal ranks keep item order; invisible items take no part.
        Assert.Equal(new[] { false, false, true }, ActionOverflowLayout.Resolve(
            [60, 60, 60], [OverflowMode.AsNeeded, OverflowMode.AsNeeded, OverflowMode.AsNeeded], [true, true, true], 160, 30, [5, 5, 5]));
        Assert.Equal(new[] { false, false, false }, ActionOverflowLayout.Resolve(
            [60, 500, 60], [OverflowMode.AsNeeded, OverflowMode.AsNeeded, OverflowMode.AsNeeded], [true, false, true], 160, 30, [5, 1, 5]));
    }
    [Fact]
    public void AnOverflowModeChangeOfAnItemRemeasuresTheBarPanel()
    {
        // A work-view switch that only turns full screen from Always (Simple) into AsNeeded (Advanced) at the same width must lay the
        // bar out again; the toolbar's own invalidation does not reach a panel whose constraint is unchanged.
        string source = File.ReadAllText(Path.Combine(Root(), "Desktop/ActionToolBar.cs"));
        int start = source.IndexOf("static ActionToolBar()", StringComparison.Ordinal);
        string constructor = source[start..source.IndexOf("PrioritizedOverflowProperty.Changed", start, StringComparison.Ordinal)];
        Assert.Contains("ToolBar.OverflowModeProperty", constructor);
        Assert.Contains("toolbar.DirectHost?.InvalidateMeasure();", constructor);
    }

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../.."));
}
