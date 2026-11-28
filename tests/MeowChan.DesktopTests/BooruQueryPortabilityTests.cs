using Avalonia.Automation;
using Avalonia.Controls;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Desktop.Booru;
using MeowChan.PluginSdk;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

[Collection("Avalonia global state")]
public sealed class BooruQueryPortabilityTests
{
    [Theory]
    [InlineData("cat_ears rating:s", "cat_ears", "rating:s")]
    [InlineData("a -b -rating:e order:score", "a -b", "-rating:e order:score")]
    [InlineData("cat_ears -dog", "cat_ears -dog", "")]
    [InlineData("rating:s", "", "rating:s")]
    [InlineData(":3 :d re:zero_kara_hajimeru_isekai_seikatsu", ":3 :d re:zero_kara_hajimeru_isekai_seikatsu", "")]
    [InlineData("cat :3 rating:s id:>5 -user:bob md5:0123456789abcdef0123456789abcdef pool:1 fav:me source:x status:any type:png limit:5 page:2 score:>3",
        "cat :3", "rating:s id:>5 -user:bob md5:0123456789abcdef0123456789abcdef pool:1 fav:me source:x status:any type:png limit:5 page:2 score:>3")]
    public void CopiesTagTermsAndReportsRemovedMetatags(string executed, string query, string removed)
    {
        BooruQueryPortability portable = BooruQueryPortability.ForOtherSource(executed);
        Assert.Equal(query, portable.Query);
        Assert.Equal(removed, portable.RemovedTerms);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HitMenuSearchesAnActiveDestinationWithTheProfileContentBoundary(bool destinationActive) =>
        BooruComponentDispatcher.Run(() =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            BooruUiHarness harness = new();
            harness.Search.OnSearch = (query, page, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(
                BooruUiFixtures.Page(query, page, true, Post(harness.Search.ProviderId, "101", BooruRating.General, "g"))));
            FakeBooruSearchPort destination = new() { ProviderId = "e926", ProviderName = "e926" };
            BooruContentSettings settings = new(BooruPostClaimRating.Sensitive, string.Empty, []);
            BooruWorkspaceContent content = new(new BooruContentProfileSnapshot(
                BooruContentProfileState.Ready, settings, new BooruContentFilter(1, settings)));
            BooruWorkbenchServices original = new(harness.Search, harness.Queue, harness.Watchers, harness.Previews, harness.Clock)
                { Content = content };
            FakeBooruPreviewPort destinationPreviews = new();
            BooruWorkbenchServices other = new(destination, new FakeBooruQueuePort(), new FakeBooruWatcherPort(harness.Clock),
                destinationPreviews, harness.Clock) { Content = content };
            destination.OnSearch = (query, page, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(
                BooruUiFixtures.Page(query, page, true,
                    Post(destination.ProviderId, "43", BooruRating.Explicit, "e"),
                    Post(destination.ProviderId, "42", BooruRating.General, "s", preview: false))));
            using BooruWorkbenchModel model = new(original, destinationActive ? [original, other] : [original]);
            using BooruWorkbenchView view = new(model, ownsModel: false);
            Window window = new() { Content = view, Width = 1440, Height = 920 };
            window.Show();
            try
            {
                BooruComponentDispatcher.Wait(BooruTestSupport.SearchAsync(model.Search, "cat_ears rating:s"));
                int originalTab = Assert.Single(view.SearchTabs.Ids);
                model.Search.SetInput("unsent_draft");
                ContextGestures.Settle(window);
                Border hit = SidePanelDesignTests.ById<Border>(view, "BooruResult:101");
                ContextGestures.MenuKey(hit);
                ContextMenu menu = Assert.IsType<ContextMenu>(hit.ContextMenu);
                MenuItem[] choices = menu.Items.OfType<MenuItem>().Where(item =>
                    AutomationProperties.GetAutomationId(item) == BooruWorkbenchView.MenuSearchOtherSource).ToArray();
                Assert.Empty(destination.Searches);
                if (!destinationActive)
                {
                    Assert.Empty(choices);
                    return;
                }

                MenuItem choice = Assert.Single(Assert.Single(choices).Items.OfType<MenuItem>());
                Assert.Equal(BooruWorkbenchView.OtherSourceMenuId(destination), AutomationProperties.GetAutomationId(choice));
                Assert.NotNull(choice.Command);
                choice.Command.Execute(null);
                BooruComponentDispatcher.WaitUntil(() => model.Search.View.Status == BooruSearchStatus.Results
                    && model.Search.View.ProviderId == destination.ProviderId, "the destination search");
                ContextGestures.Settle(window);
                Assert.Equal(("cat_ears", 1), Assert.Single(destination.Searches));
                Assert.Equal("cat_ears", model.Search.View.Input);
                Assert.Equal(2, view.SearchTabs.Ids.Count);
                Assert.NotEqual(originalTab, view.SearchTabs.ActiveId);
                Assert.Equal("42", Assert.Single(model.Search.View.Rows).Post.PostId);
                Assert.Equal(1, model.Search.View.HiddenCount);
                Assert.Equal(0, destinationPreviews.Requests);
                TextBlock notice = SidePanelDesignTests.ById<TextBlock>(view, BooruWorkbenchView.PortabilityNoticeName);
                Assert.True(notice.IsEffectivelyVisible);
                Assert.Contains("rating:s", notice.Text);

                Assert.True(view.SearchTabs.Select(originalTab));
                ContextGestures.Settle(window);
                Assert.Equal("unsent_draft", model.Search.View.Input);
                Assert.False(notice.IsEffectivelyVisible);
                Assert.Single(harness.Search.Searches);
            }
            finally { window.Close(); }
        });

    private static BooruPostView Post(string sourceId, string id, BooruRating rating, string rawRating, bool preview = true)
    {
        BooruPost post = BooruUiFixtures.Post(id, rating: rating, rawRating: rawRating, preview: preview).Post;
        return BooruPostView.From(post with
        {
            Id = new BooruPostId(sourceId, id),
            Media = post.Media!.Select(media => media.Extension is { } extension
                ? media with { Extension = "." + extension.TrimStart('.') } : media).ToArray()
        });
    }
}
