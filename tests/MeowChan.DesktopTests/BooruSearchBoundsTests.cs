using System.Text.Json;
using MeowChan.Booru;
using MeowChan.Desktop;
using MeowChan.PluginHost.Http;
using MeowChan.PluginHost.Sources.Booru;

namespace MeowChan.DesktopTests;

/// <summary>
/// Numbered searches and background watcher passes have independent page budgets.
/// </summary>
public sealed class BooruSearchBoundsTests
{
    [Fact]
    public void ManualSearchUsesTheAnonymousProviderLimitAndWatchersKeepTheirPassBudget()
    {
        Assert.Equal((1000, 20), (DanbooruTagQuery.MaximumSearchPages, DanbooruTagQuery.MaximumSearchPageSize));
        Assert.Equal((5, DanbooruTagQuery.MaximumSearchPageSize),
            (DanbooruWatcher.MaximumPageBudget, DanbooruWatcher.MaximumPageSize));
        Assert.Equal((DanbooruWatcher.MaximumPageBudget, DanbooruWatcher.MaximumPageSize),
            (DanbooruWatcher.DefaultPageBudget, DanbooruWatcher.DefaultPageSize));
        Assert.Equal((DanbooruTagQuery.MaximumSearchPages, DanbooruTagQuery.MaximumSearchPageSize),
            (BooruHostHttp.MaximumSearchPage, BooruHostHttp.MaximumSearchLimit));
        Assert.Equal(DanbooruTagQuery.MaximumSearchPageSize, BooruSearchService.SearchLimit);
        Assert.Equal((DanbooruTagQuery.MaximumSearchPages, DanbooruTagQuery.MaximumSearchPageSize),
            (Desktop.Booru.BooruSearchModel.MaximumPage, Desktop.Booru.BooruSearchModel.PageSize));
    }

    [Fact]
    public void PagerPreferencesDefaultForOldProfilesAndSurviveCopyAndTransfer()
    {
        Config old = JsonSerializer.Deserialize("{}", ConfigJsonContext.Default.Config)!;
        Assert.Equal((5, 20), (old.BooruSearchPages, old.BooruSearchPostsPerPage));
        Config source = new() { BooruSearchPages = 1000, BooruSearchPostsPerPage = 1 };
        source.Validate();

        Config copy = source.Clone();
        Assert.Equal((1000, 1), (copy.BooruSearchPages, copy.BooruSearchPostsPerPage));
        copy = new Config();
        copy.CopyFrom(source);
        Assert.Equal((1000, 1), (copy.BooruSearchPages, copy.BooruSearchPostsPerPage));
        Config exported = SettingsTransfer.Export(source).Deserialize(ConfigJsonContext.Default.Config)!;
        copy = new Config();
        copy.CopySettingsFieldsFrom(exported);
        Assert.Equal((1000, 1), (copy.BooruSearchPages, copy.BooruSearchPostsPerPage));
    }

    [Theory]
    [InlineData(0, 20, nameof(Config.BooruSearchPages))]
    [InlineData(1001, 20, nameof(Config.BooruSearchPages))]
    [InlineData(5, 0, nameof(Config.BooruSearchPostsPerPage))]
    [InlineData(5, 21, nameof(Config.BooruSearchPostsPerPage))]
    public void PagerPreferencesRejectOutOfRangeValues(int pages, int posts, string property)
    {
        Config config = new() { BooruSearchPages = pages, BooruSearchPostsPerPage = posts };
        Assert.Equal(property, Assert.Throws<ArgumentOutOfRangeException>(config.Validate).ParamName);
    }
}
