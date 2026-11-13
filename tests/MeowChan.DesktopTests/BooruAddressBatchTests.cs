using MeowChan.Desktop.Booru;

namespace MeowChan.DesktopTests;

public sealed class BooruAddressBatchTests
{
    [Fact]
    public void NoSearchAddressMeansNothingToOpenOrIgnore()
    {
        BooruAddressBatch plan = BooruAddressBatch.Plan([]);
        Assert.Null(plan.SearchIndex);
        Assert.Empty(plan.Ignored);
    }

    [Fact]
    public void OneSearchAddressIsUsedAndNothingIsIgnored()
    {
        BooruAddressBatch plan = BooruAddressBatch.Plan([3]);
        Assert.Equal(3, plan.SearchIndex);
        Assert.Empty(plan.Ignored);
    }

    [Fact]
    public void SeveralSearchAddressesOpenTheLastOneAndIgnoreTheEarlierOnesOnce()
    {
        BooruAddressBatch plan = BooruAddressBatch.Plan([5, 1, 3]);
        Assert.Equal(5, plan.SearchIndex);
        Assert.Equal(new[] { 1, 3 }, plan.Ignored);
    }
}
