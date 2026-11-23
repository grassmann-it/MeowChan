using System.Collections.Immutable;
using System.Text.Json;
using MeowChan.Desktop;
using MeowChan.Desktop.Booru;
using MeowChan.PluginSdk;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

public sealed class BooruPostFilterTests
{
    [Fact]
    public async Task CombinedFiltersAndResetKeepTheLoadedPageAndSendNothing()
    {
        BooruUiHarness h = new();
        using BooruSearchModel model = new(h.Search, h.Queue, h.Previews, h.Clock);
        h.Search.OnSearch = (query, page, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(BooruUiFixtures.Page(query, page, false,
            BooruUiFixtures.Post("1", rating: BooruRating.General, score: 10, tags: ["cat", "sky"], preview: false),
            BooruUiFixtures.Post("2", rating: BooruRating.General, score: 9, tags: ["cat"], preview: false),
            BooruUiFixtures.Post("3", rating: BooruRating.Explicit, score: 20, tags: ["cat"], preview: false),
            BooruUiFixtures.Post("4", rating: BooruRating.General, score: 10, tags: ["cat"], preview: false))));
        await BooruTestSupport.SearchAsync(model, "cat");
        var loaded = model.View.Rows;
        model.SetPostFilter(new BooruPostFilter { Rating = BooruRating.General, MinimumScore = 10, MediaType = BooruPostMediaType.Image });
        model.SetPostFilterTag("cat", true);
        model.SetPostFilterTag("sky", false);
        Assert.Equal("4", Assert.Single(model.View.VisibleRows).Post.PostId);
        Assert.Equal(4, model.View.Rows.Length);
        Assert.True(model.View.HasNextPage);
        Assert.Equal("cat", model.View.ExecutedQuery);
        model.SetPostFilterTag("not_on_this_page", true);
        Assert.False(model.View.PostFilter.Tags.ContainsKey("not_on_this_page"));
        model.SetPostFilter(model.View.PostFilter with { MinimumScore = 100 });
        Assert.Empty(model.View.VisibleRows);
        Assert.Equal(BooruSearchStatus.Results, model.View.Status);
        Assert.True(model.View.HasNextPage);
        model.ResetPostFilter();
        Assert.Equal(loaded.Select(row => row.Post), model.View.VisibleRows.Select(row => row.Post));
        Assert.Equal(1, h.ProviderRequests);
        Assert.Empty(h.Queue.Loads);
    }

    [Fact]
    public async Task TabsKeepIndependentFiltersAndNewPagesRepeatOnlyTheOriginalQuery()
    {
        BooruUiHarness h = new(); h.Serve(pages: 2);
        using BooruSearchModel model = new(h.Search, h.Queue, h.Previews, h.Clock);
        BooruSearchTabs tabs = new(model);
        await BooruTestSupport.SearchAsync(model, "cat");
        model.SetPostFilter(new BooruPostFilter { MinimumScore = 500 });
        model.SetPostFilterTag("cat", true);
        BooruPostFilter original = model.View.PostFilter;
        int copy = tabs.Add(true);
        Assert.Equal(original, model.View.PostFilter);
        model.ResetPostFilter();
        tabs.Select(0);
        Assert.Equal(original, model.View.PostFilter);
        int blank = tabs.Add(false);
        Assert.False(model.View.PostFilter.IsActive);
        tabs.Select(0);
        Assert.Equal(1, h.ProviderRequests);
        await model.GoToPageAsync(2);
        Assert.Equal(original, model.View.PostFilter);
        Assert.Equal(new[] { ("cat", 1), ("cat", 2) }, h.Search.Searches);
        Assert.Empty(model.View.VisibleRows);
        Assert.True(model.View.HasPreviousPage);
        tabs.Close(copy); tabs.Close(blank);
        model.ResetPostFilter();
        Assert.Equal(3, model.View.VisibleRows.Length);
        Assert.Equal(2, h.ProviderRequests);
    }

    [Fact]
    public void MissingMetadataIsNotInventedAndTagsMatchExactlyAcrossCategories()
    {
        BooruPostView unknown = BooruPostView.From(new BooruPost(new BooruPostId("danbooru", "1"), BooruFieldPresence.None));
        Assert.True(new BooruPostFilter().Matches(unknown));
        Assert.False(new BooruPostFilter { MinimumScore = -10 }.Matches(unknown));
        Assert.False(new BooruPostFilter { MediaType = BooruPostMediaType.Image }.Matches(unknown));
        Assert.True(new BooruPostFilter { MediaType = BooruPostMediaType.Unknown, Rating = BooruRating.Unknown }.Matches(unknown));
        BooruPostView post = BooruUiFixtures.Post("2", tags: ["cat_ears", "blue_eyes", "artist_(name)"], score: -5, preview: false);
        Assert.True(new BooruPostFilter { MinimumScore = -5 }.Matches(post));
        Assert.False(new BooruPostFilter { MinimumScore = -4 }.Matches(post));
        Assert.False(new BooruPostFilter { Tags = ImmutableDictionary<string, bool>.Empty.Add("cat", true) }.Matches(post));
        Assert.True(new BooruPostFilter { Tags = ImmutableDictionary<string, bool>.Empty.Add("artist_(name)", true) }.Matches(post));
    }

    [Theory]
    [InlineData(".JPG", (int)BooruPostMediaType.Image)]
    [InlineData("gif", (int)BooruPostMediaType.Animation)]
    [InlineData("webm", (int)BooruPostMediaType.Video)]
    [InlineData("mp3", (int)BooruPostMediaType.Audio)]
    [InlineData(null, (int)BooruPostMediaType.Unknown)]
    [InlineData("something", (int)BooruPostMediaType.Unknown)]
    public void MediaTypeUsesOriginalMetadataEvenIfItsUrlIsRestricted(string? extension, int expected)
    {
        BooruPostView post = BooruPostView.From(new BooruPost(new BooruPostId("danbooru", "1"), BooruFieldPresence.All,
            Media: [new BooruMediaVariant(BooruMediaRole.Preview, null, "jpg"), new BooruMediaVariant(BooruMediaRole.Original, null, extension)]));
        Assert.Equal(expected, (int)BooruPostFilter.TypeOf(post));
    }

    [Fact]
    public async Task FilteringWhileARequestFinishesAndBulkDownloadKeepTheirOwnScope()
    {
        BooruUiHarness h = new();
        using BooruSearchModel model = new(h.Search, h.Queue, h.Previews, h.Clock);
        TaskCompletionSource<BooruCallResult<BooruResultPage>> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Search.OnSearch = (_, _, _) => response.Task;
        Task search = BooruTestSupport.SearchAsync(model, "cat");
        model.SetPostFilter(new BooruPostFilter { MinimumScore = 100 });
        response.SetResult(BooruCallResult<BooruResultPage>.Ok(BooruUiFixtures.Page("cat", 1, true,
            BooruUiFixtures.Post("1", score: 1, preview: false))));
        await search;
        Assert.Empty(model.View.VisibleRows);
        Assert.Single(model.View.Rows);
        Assert.Equal(1, h.ProviderRequests);
        await model.DownloadAllAsync();
        Assert.Equal("1", Assert.Single(h.Queue.Loads).PostId);
        Assert.All(h.Search.Searches, call => Assert.Equal("cat", call.Query));
        Assert.Empty(model.View.VisibleRows);
    }

    [Fact]
    public void ScoreSettingSurvivesCloneCopyTransferAndValidation()
    {
        Assert.Equal(0, new Config().BooruPostFilterScore);
        Config source = new() { BooruPostFilterScore = -15 };
        source.Validate();
        Assert.Equal(-15, source.Clone().BooruPostFilterScore);
        Config target = new(); target.CopyFrom(source);
        Assert.Equal(-15, target.BooruPostFilterScore);
        target = new(); target.CopySettingsFieldsFrom(source);
        Assert.Equal(-15, target.BooruPostFilterScore);
        string json = SettingsTransfer.Export(source).ToJsonString();
        Config imported = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.Config)!;
        imported.Validate();
        Assert.Equal(-15, imported.BooruPostFilterScore);
        Assert.Throws<ArgumentOutOfRangeException>(() => new Config { BooruPostFilterScore = Config.BooruPostFilterScoreMinimum - 1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new Config { BooruPostFilterScore = Config.BooruPostFilterScoreMaximum + 1 }.Validate());
        new Config { BooruPostFilterScore = Config.BooruPostFilterScoreMinimum }.Validate();
        new Config { BooruPostFilterScore = Config.BooruPostFilterScoreMaximum }.Validate();
    }
}
