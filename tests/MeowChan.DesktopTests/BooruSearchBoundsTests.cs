using MeowChan.Booru;
using MeowChan.PluginHost.Http;
using MeowChan.PluginHost.Sources.Booru;

namespace MeowChan.DesktopTests;

/// <summary>
/// "5 pages of 20 posts" is one fixed limit with one source (DanbooruTagQuery): the search watcher, the host transport,
/// the search service and the search view all read it from there instead of carrying their own copies.
/// </summary>
public sealed class BooruSearchBoundsTests
{
    [Fact]
    public void EveryLayerUsesTheSharedPageBounds()
    {
        Assert.Equal((5, 20), (DanbooruTagQuery.MaximumSearchPages, DanbooruTagQuery.MaximumSearchPageSize));
        Assert.Equal((DanbooruTagQuery.MaximumSearchPages, DanbooruTagQuery.MaximumSearchPageSize),
            (DanbooruWatcher.MaximumPageBudget, DanbooruWatcher.MaximumPageSize));
        Assert.Equal((DanbooruWatcher.MaximumPageBudget, DanbooruWatcher.MaximumPageSize),
            (DanbooruWatcher.DefaultPageBudget, DanbooruWatcher.DefaultPageSize));
        Assert.Equal((DanbooruTagQuery.MaximumSearchPages, DanbooruTagQuery.MaximumSearchPageSize),
            (BooruHostHttp.MaximumSearchPage, BooruHostHttp.MaximumSearchLimit));
        Assert.Equal(DanbooruTagQuery.MaximumSearchPageSize, BooruSearchService.SearchLimit);
        Assert.Equal((DanbooruTagQuery.MaximumSearchPages, DanbooruTagQuery.MaximumSearchPageSize),
            (Desktop.Booru.BooruSearchModel.MaximumPage, Desktop.Booru.BooruSearchModel.PageSize));
    }
}
