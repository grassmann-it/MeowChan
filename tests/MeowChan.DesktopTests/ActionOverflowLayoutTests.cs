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

    // Toolbar UX review 15.11.: the panel owns the gaps between the items of a bar.
    [Fact]
    public void TheLeadingGapIsTheItemGapTheGroupGapAtAGroupChangeAndNoneBetweenTwoSegments()
    {
        Assert.Equal(8, ActionToolBarPanel.ItemGap);
        Assert.Equal(16, ActionToolBarPanel.GroupGap);
        Assert.Equal(ActionToolBarPanel.ItemGap, ActionToolBarPanel.LeadingGap(false, 0, false, 0));
        Assert.Equal(ActionToolBarPanel.GroupGap, ActionToolBarPanel.LeadingGap(false, 0, false, 1));
        Assert.Equal(0, ActionToolBarPanel.LeadingGap(true, 0, true, 0));
        // Only two joined segments touch: a segment beside a plain item keeps the item gap, a group change the group gap.
        Assert.Equal(ActionToolBarPanel.ItemGap, ActionToolBarPanel.LeadingGap(true, 0, false, 0));
        Assert.Equal(ActionToolBarPanel.ItemGap, ActionToolBarPanel.LeadingGap(false, 0, true, 0));
        Assert.Equal(ActionToolBarPanel.GroupGap, ActionToolBarPanel.LeadingGap(true, 0, true, 1));
        // The first item on the bar has none (no margin at either end, so a right-aligned bar ends flush).
        Avalonia.Controls.Button first = new(), second = new(), list = new() { Classes = { "segment" } }, table = new() { Classes = { "segment" } };
        Assert.Equal(0, ActionToolBarPanel.LeadingGap(null, first));
        Assert.Equal(8, ActionToolBarPanel.LeadingGap(first, second));
        Assert.Equal(0, ActionToolBarPanel.LeadingGap(list, table));
        ActionToolBar.SetGroup(second, 1);
        Assert.Equal(16, ActionToolBarPanel.LeadingGap(table, second));
    }

    [Fact]
    public void TheMeasuredWidthCountsTheGapsBetweenTheItemsThatStayOnTheBar()
    {
        OverflowMode[] modes = [OverflowMode.AsNeeded, OverflowMode.AsNeeded, OverflowMode.AsNeeded];
        // 80 | 8 | 90 | 16 (new group) | 70 = 264, no gap at either end.
        (bool[] overflow, double width) = ActionOverflowLayout.ResolveSpaced([80, 90, 70], [0, 0, 1], [false, false, false], modes, [true, true, true], 264, 40);
        Assert.Equal(new[] { false, false, false }, overflow);
        Assert.Equal(264, width);
        // The 240 of the items alone would fit 250; with the gaps the last item moves under More, and the bar keeps 80 + 8 + 90.
        (overflow, width) = ActionOverflowLayout.ResolveSpaced([80, 90, 70], [0, 0, 1], [false, false, false], modes, [true, true, true], 250, 40);
        Assert.Equal(new[] { false, false, true }, overflow);
        Assert.Equal(178, width);
        // A hidden item takes no gap: the neighbours close up.
        (overflow, width) = ActionOverflowLayout.ResolveSpaced([80, 500, 70], [0, 0, 0], [false, false, false], modes, [true, false, true], 158, 40);
        Assert.Equal(new[] { false, false, false }, overflow);
        Assert.Equal(158, width);
    }

    [Fact]
    public void TwoJoinedSegmentsStandWithoutAGapAndTheNextGroupStandsOneGroupGapAway()
    {
        OverflowMode[] modes = [OverflowMode.AsNeeded, OverflowMode.AsNeeded, OverflowMode.AsNeeded, OverflowMode.AsNeeded];
        // List | Table (joined) || Gallery | Favorites: 64 + 0 + 64 + 16 + 100 + 8 + 90.
        (bool[] overflow, double width) = ActionOverflowLayout.ResolveSpaced([64, 64, 100, 90], [0, 0, 1, 1], [true, true, false, false], modes,
            [true, true, true, true], 1000, 40);
        Assert.Equal(new[] { false, false, false, false }, overflow);
        Assert.Equal(342, width);
    }

    [Fact]
    public void APrioritizedBarWhoseMiddleItemMovedAwayCountsTheGroupGapItsNeighboursNowHave()
    {
        // a (group 0) | i (group 1) | b (group 1); i ranks last. With i gone, b follows a across a group change: 16, not the 8 it had
        // behind i. Counting the 8 would keep b on the bar at 50 + 8 + 50 = 108 within the 110 budget and clip it (real 116).
        OverflowMode[] modes = [OverflowMode.AsNeeded, OverflowMode.AsNeeded, OverflowMode.AsNeeded];
        (bool[] overflow, double width) = ActionOverflowLayout.ResolveSpaced([50, 50, 50], [0, 1, 1], [false, false, false], modes,
            [true, true, true], 150, 40, [0, 2, 1]);
        Assert.Equal(new[] { false, true, true }, overflow);
        Assert.Equal(50, width);
        // With room for the real gap b stays: 50 + 16 + 50 = 116 within 156 - 40.
        (overflow, width) = ActionOverflowLayout.ResolveSpaced([50, 50, 50], [0, 1, 1], [false, false, false], modes, [true, true, true], 156, 40, [0, 2, 1]);
        Assert.Equal(new[] { false, true, false }, overflow);
        Assert.Equal(116, width);
    }

    [Fact]
    public void AGroupStartThatFitsWithTheItemGapButNotTheGroupGapStaysOnTheBar()
    {
        // a (group 1) | m (group 0) | b (group 1); m ranks last. The first guess counts b with the group gap 16 it has behind m, and the
        // passes keep that largest gap although b, once m is under More, stands behind a with the item gap 8: 50 + 8 + 50 = 108.
        // Without the refill b moved under More with 150 - 40 left for it.
        OverflowMode[] modes = [OverflowMode.AsNeeded, OverflowMode.AsNeeded, OverflowMode.AsNeeded];
        (bool[] overflow, double width) = ActionOverflowLayout.ResolveSpaced([50, 50, 50], [1, 0, 1], [false, false, false], modes,
            [true, true, true], 150, 40, [0, 2, 1]);
        Assert.Equal(new[] { false, true, false }, overflow);
        Assert.Equal(108, width);
        // One dip less and the real bar (108) plus More (40) no longer fits: b stays under More, the refill stops at that first miss.
        (overflow, width) = ActionOverflowLayout.ResolveSpaced([50, 50, 50], [1, 0, 1], [false, false, false], modes,
            [true, true, true], 147, 40, [0, 2, 1]);
        Assert.Equal(new[] { false, true, true }, overflow);
        Assert.Equal(50, width);
    }

    [Fact]
    public void TheRefillPassNeverTakesBackAnAlwaysItemAndNeverExceedsTheAvailableWidth()
    {
        Random random = new(15112026);
        OverflowMode[] all = [OverflowMode.Never, OverflowMode.AsNeeded, OverflowMode.AsNeeded, OverflowMode.AsNeeded, OverflowMode.Always];
        for (int round = 0; round < 3000; round++)
        {
            int count = random.Next(1, 9);
            double[] widths = new double[count]; int[] groups = new int[count]; bool[] segments = new bool[count];
            OverflowMode[] modes = new OverflowMode[count]; bool[] visible = new bool[count]; int[] priorities = new int[count];
            int group = 0;
            for (int i = 0; i < count; i++)
            {
                widths[i] = random.Next(20, 120);
                if (random.Next(3) == 0) group++;
                groups[i] = group; segments[i] = random.Next(4) == 0;
                modes[i] = all[random.Next(all.Length)]; visible[i] = random.Next(6) != 0; priorities[i] = random.Next(5);
            }
            double available = random.Next(0, 700), more = random.Next(20, 60);
            (bool[] overflow, double width) = ActionOverflowLayout.ResolveSpaced(widths, groups, segments, modes, visible, available, more,
                random.Next(2) == 0 ? priorities : null);
            bool remaining = false, onlyNever = true;
            for (int i = 0; i < count; i++)
            {
                if (!visible[i]) continue;
                Assert.False(modes[i] == OverflowMode.Always && !overflow[i], "an Always item stayed on the bar");
                Assert.False(modes[i] == OverflowMode.Never && overflow[i], "a Never item moved under More");
                if (overflow[i]) remaining = true;
                else if (modes[i] != OverflowMode.Never) onlyNever = false;
            }
            // Only the items that must stay (Never) may take more than there is; every other result fits with More counted.
            if (!onlyNever) Assert.True(width + (remaining ? more : 0) <= available + 1e-9, $"round {round}: {width} + more exceeds {available}");
        }
    }

    [Fact]
    public void AnItemUnderMoreIsJudgedByTheWidthItHadOnTheBar()
    {
        // Under "More" the item is measured in the popup's plain style (narrower); the bar width it last had decides.
        Assert.Equal(90, ActionToolBarPanel.DecisionWidth(true, 90, 70));
        // On the bar the measured width is the bar width.
        Assert.Equal(70, ActionToolBarPanel.DecisionWidth(false, 90, 70));
        // Never seen on the bar: the width measured where it is.
        Assert.Equal(70, ActionToolBarPanel.DecisionWidth(true, double.NaN, 70));
        Assert.Equal(70, ActionToolBarPanel.DecisionWidth(true, double.PositiveInfinity, 70));
    }

    [Fact]
    public void AnAlwaysItemTakesNoGapOnTheBarAndMoreReservesTheItemGapInFrontOfIt()
    {
        // The Always item sits under More from the start: its neighbours of one group stand one item gap apart (50 + 8 + 50).
        (bool[] overflow, double width) = ActionOverflowLayout.ResolveSpaced([50, 50, 50], [0, 1, 0], [false, false, false],
            [OverflowMode.Never, OverflowMode.Always, OverflowMode.AsNeeded], [true, true, true], 200, 40);
        Assert.Equal(new[] { false, true, false }, overflow);
        Assert.Equal(108, width);
        // "⋮": the toggle at its minimum width and its margins; the left margin is the item gap.
        Avalonia.Thickness margin = ActionToolBar.MoreMargin(new Avalonia.Thickness(0, 2, 0, 2));
        Assert.Equal(new Avalonia.Thickness(ActionToolBarPanel.ItemGap, 2, 0, 2), margin);
        Assert.Equal(32 + ActionToolBarPanel.ItemGap, ActionToolBar.MoreSlot(20, 32, margin));
        Assert.Equal(40 + ActionToolBarPanel.ItemGap, ActionToolBar.MoreSlot(40, 32, margin));
    }

    [Fact]
    public void TheBarArrangesItsItemsWithTheGapsItMeasured()
    {
        // ArrangeOverride places the items with LeadingGap between the shown children; it no longer leaves the row to StackPanel.
        string source = File.ReadAllText(Path.Combine(Root(), "Desktop/ActionToolBar.cs"));
        int start = source.IndexOf("protected override Size ArrangeOverride", StringComparison.Ordinal);
        string arrange = source[start..source.IndexOf("UpdateGroupSeparators();", start, StringComparison.Ordinal)];
        Assert.Contains("x += LeadingGap(previous, child);", arrange);
        Assert.Contains("child.Arrange(new Rect(x, 0, width, finalSize.Height));", arrange);
        Assert.Contains("More.Margin = MoreMargin(More.Margin);", source);
    }

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../.."));
}
