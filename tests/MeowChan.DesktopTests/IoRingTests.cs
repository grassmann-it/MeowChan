using MeowChan.Desktop.Stats;

namespace MeowChan.DesktopTests;

public sealed class IoRingTests
{
    private static IoSample S(float v) => new(v, v * 10, v * 100, v * 1000);

    private static float[] Net(IoRing ring) => ring.ToSeries().NetDown;

    [Fact]
    public void FillsInOrderBelowCapacity()
    {
        IoRing ring = new(5);
        ring.Add(S(1)); ring.Add(S(2)); ring.Add(S(3));
        IoSeries series = ring.ToSeries();
        Assert.Equal(3, series.Count);
        Assert.Equal(5, series.Capacity);
        Assert.Equal([1f, 2f, 3f], series.NetDown);
        Assert.Equal([10f, 20f, 30f], series.DiskWrite);
        Assert.Equal([100f, 200f, 300f], series.DiskRead);
        Assert.Equal([1000f, 2000f, 3000f], series.Requests);
    }

    [Fact]
    public void WrapsAroundAndKeepsTheNewestOldestFirst()
    {
        IoRing ring = new(4);
        for (int i = 1; i <= 10; i++) ring.Add(S(i));
        Assert.Equal(4, ring.Count);
        Assert.Equal([7f, 8f, 9f, 10f], Net(ring));
        Assert.Equal([70f, 80f, 90f, 100f], ring.ToSeries().DiskWrite);
    }

    [Fact]
    public void GrowingKeepsEverythingAndContinuesInOrder()
    {
        IoRing ring = new(3);
        for (int i = 1; i <= 5; i++) ring.Add(S(i)); // holds 3,4,5 (wrapped)
        ring.Resize(6);
        Assert.Equal(6, ring.Capacity);
        Assert.Equal([3f, 4f, 5f], Net(ring));
        ring.Add(S(6)); ring.Add(S(7)); ring.Add(S(8)); ring.Add(S(9));
        Assert.Equal([4f, 5f, 6f, 7f, 8f, 9f], Net(ring));
    }

    [Fact]
    public void ShrinkingKeepsTheNewestSamples()
    {
        IoRing ring = new(8);
        for (int i = 1; i <= 11; i++) ring.Add(S(i)); // wrapped: 4..11
        ring.Resize(3);
        Assert.Equal(3, ring.Capacity);
        Assert.Equal([9f, 10f, 11f], Net(ring));
        ring.Add(S(12));
        Assert.Equal([10f, 11f, 12f], Net(ring));
        Assert.Equal([100f, 110f, 120f], ring.ToSeries().DiskWrite);
    }

    [Fact]
    public void ShrinkingBelowTheCountOfAPartlyFilledRingAndResizingToTheSameSizeAreSafe()
    {
        IoRing ring = new(10);
        ring.Add(S(1)); ring.Add(S(2));
        ring.Resize(10);
        Assert.Equal([1f, 2f], Net(ring));
        ring.Resize(1);
        Assert.Equal([2f], Net(ring));
        ring.Resize(30);
        Assert.Equal([2f], Net(ring));
    }

    [Fact]
    public void ClearEmptiesTheRingAndRejectsBadSizes()
    {
        IoRing ring = new(3);
        ring.Add(S(1));
        ring.Clear();
        Assert.Equal(0, ring.Count);
        Assert.Empty(Net(ring));
        ring.Add(S(2));
        Assert.Equal([2f], Net(ring));
        Assert.Throws<ArgumentOutOfRangeException>(() => ring.Resize(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new IoRing(0));
    }
}
