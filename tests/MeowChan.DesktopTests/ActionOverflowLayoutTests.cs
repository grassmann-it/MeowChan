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
}
