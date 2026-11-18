using MeowChan;

namespace MeowChan.DesktopTests;

/// <summary>Pure lifetime tests for the deferred shell fallback used when the Booru page loses availability.</summary>
public sealed class BooruUnavailableNavigationTests
{
    [Fact]
    public void ABlockedDownloadsRouteRemainsPending()
    {
        BooruUnavailableNavigation fallback = new();
        fallback.Request();
        bool attempted = false;

        Assert.False(fallback.TryComplete(() =>
        {
            attempted = true;
            return false;
        }));
        Assert.True(attempted);
        Assert.True(fallback.IsPending);
    }

    [Fact]
    public void TheNormalDownloadsRouteClearsThePendingFallback()
    {
        BooruUnavailableNavigation fallback = new();
        fallback.Request();

        Assert.True(fallback.TryComplete(() => true));
        Assert.False(fallback.IsPending);
    }

    [Fact]
    public void ARecoveredCompositionCanCancelThePendingFallback()
    {
        BooruUnavailableNavigation fallback = new();
        fallback.Request();

        fallback.Clear();

        Assert.False(fallback.IsPending);
        Assert.False(fallback.TryComplete(() => throw new InvalidOperationException("must not navigate")));
    }
}
