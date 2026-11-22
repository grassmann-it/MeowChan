using System.Text.Json;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;
using MeowChan.PluginSdk;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class BooruSearchTabPersistenceTests
{
    private static SourceDescription Source => DanbooruTestPlugin.Plugin.Describe().Sources.Single();

    [Fact]
    public void RestartRestoresTheTabStripAndSnapshotWithoutCallingAnyProviderPort() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        using TempDirectory profile = new();
        Config config = new() { BooruNewSearchTarget = Config.BooruSearchTargetCurrentTab };
        config.SaveConfig(profile.Path);
        byte[] preview = BooruUiFixtures.Png(5);

        using (ProfileLease lease = ProfileLease.Acquire(profile.Path))
        {
            lease.Admission.CompleteStartup();
            BooruSearchTabStore store = new(lease);
            BooruUiHarness harness = new();
            harness.Search.OnSearch = (query, page, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(
                BooruUiFixtures.Page(query, page, page == 3, Post("102", score: -1))));
            harness.Search.OnPost = (id, _) => Task.FromResult(BooruCallResult<BooruPostView>.Ok(Post(id)));
            harness.Previews.OnPreview = _ => preview;
            using BooruPage page = new(WithSource(harness));
            page.RestoreSearchTabs(store, config);
            BooruSearchModel search = page.Model.Search;
            BooruSearchTabs tabs = page.View.SearchTabs;
            search.SetMode(BooruInputMode.Post);
            search.SetInput("https://danbooru.donmai.us/posts/42");
            BooruComponentDispatcher.Wait(search.SubmitAsync());
            int active = tabs.Add(copyCurrent: false);
            search.ReadPagerLimits = () => new(3, 2);
            search.SetInput("  cat  ");
            BooruComponentDispatcher.Wait(search.SubmitAsync());
            BooruComponentDispatcher.Wait(search.GoToPageAsync(2));
            BooruComponentDispatcher.Wait(BooruTestSupport.WaitAsync(
                () => search.View.Rows is [{ Preview: BooruPreviewState.Ready }], "page preview"));
            search.SetInput("edited_draft");
            tabs.Add(copyCurrent: false);
            search.SetInput("unsent_draft");
            tabs.Select(active);
            page.FlushSearchTabs();
            lease.Admission.BeginShutdown();
            BooruComponentDispatcher.Wait(store.PendingSave);
        }

        using ProfileLease reopened = ProfileLease.Acquire(profile.Path);
        reopened.Admission.CompleteStartup();
        Config restoredConfig = Config.LoadConfig(profile.Path);
        Assert.Equal(Config.BooruSearchTargetCurrentTab, restoredConfig.BooruNewSearchTarget);
        BooruUiHarness offline = new();
        using BooruPage restored = new(WithSource(offline));
        BooruSearchTabStore restoredStore = new(reopened);
        restored.View.RestoreResultView(BooruResultView.Gallery, MediaTileSize.Medium);
        restored.RestoreSearchTabs(restoredStore, restoredConfig);
        using TestWindow window = new(restored);
        BooruComponentDispatcher.Wait(restored.ActivateAsync());
        Dispatcher.UIThread.RunJobs();

        BooruSearchTabs restoredTabs = restored.View.SearchTabs;
        Assert.Equal(3, restoredTabs.Ids.Count);
        Assert.Equal(restoredTabs.Ids[1], restoredTabs.ActiveId);
        TabControl strip = Assert.IsType<TabControl>(restored.View.Body);
        Assert.Equal(restoredTabs.ActiveId, Assert.IsType<TabItem>(strip.SelectedItem).Tag);
        BooruSearchView activeView = restored.Model.Search.View;
        Assert.Equal("edited_draft", activeView.Input);
        Assert.Equal("cat", activeView.ExecutedQuery);
        Assert.Equal(2, activeView.Page);
        Assert.Equal(new BooruPagerLimits(3, 2), activeView.PagerLimits);
        Assert.True(activeView.HasPreviousPage);
        Assert.True(activeView.HasNextPage);
        BooruResultRowView row = Assert.Single(activeView.Rows);
        Assert.Equal(-1, row.Post.Score);
        Assert.Equal(preview, row.PreviewBytes);
        Assert.Equal("102", restored.View.Gallery.CurrentPostId);
        Assert.Equal(BooruGalleryPicture.Preview, restored.View.Gallery.Picture);

        restoredTabs.Select(restoredTabs.Ids[2]);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("unsent_draft", restored.Model.Search.View.Input);
        Assert.Empty(restored.Model.Search.View.Rows);
        Control content = Assert.IsAssignableFrom<Control>(Assert.IsType<TabItem>(strip.SelectedItem).Content);
        Border status = Assert.Single(content.GetLogicalDescendants().OfType<Border>(), border => border.Name == BooruWorkbenchView.StatusName);
        Assert.Contains(status.GetLogicalDescendants().OfType<TextBlock>(), text => text.Text == Texts.T("booru.tabs.restore.load"));
        restoredTabs.Select(BooruSearchTabs.PrimaryId);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(BooruInputMode.Post, restored.Model.Search.View.Mode);
        Assert.Equal("42", restored.Model.Search.View.ExecutedPostId);
        Assert.Equal("42", Assert.Single(restored.Model.Search.View.Rows).Post.PostId);
        Assert.Equal(0, offline.ProviderRequests);
        Assert.Equal(0, offline.Previews.Requests);
        Assert.Empty(offline.Previews.SampleRequests);
        restored.FlushSearchTabs();
        BooruComponentDispatcher.Wait(restoredStore.PendingSave);
    });

    [Theory]
    [InlineData("{\"version\":1, broken")]
    [InlineData("{\"version\":1,\"providerId\":\"danbooru\",\"activeIndex\":6,\"tabs\":[null]}")]
    [InlineData("{\"version\":1,\"version\":1,\"providerId\":\"danbooru\",\"activeIndex\":0,\"tabs\":[null]}")]
    [InlineData("{\"version\":2,\"providerId\":\"danbooru\",\"activeIndex\":0,\"tabs\":[null]}")]
    public async Task InvalidOrFutureFilesAreQuarantinedAndTheNextSaveWorks(string contents)
    {
        using TempDirectory profile = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        lease.Admission.CompleteStartup();
        string path = Path.Combine(profile.Path, BooruSearchTabStore.FileName);
        await File.WriteAllTextAsync(path, contents);
        using BooruWorkbenchModel model = new(new BooruUiHarness().Services);
        BooruSearchTabStore store = new(lease);
        Assert.Null(store.Load(model.Search.View, 2, 1, Source));
        Assert.False(File.Exists(path));
        Assert.Equal(contents, await File.ReadAllTextAsync(path + ".bad"));

        model.Search.SetInput("safe_draft");
        store.Save(new BooruSearchTabs(model.Search).Capture(2), 1);
        await store.PendingSave;
        BooruTabSnapshot loaded = Assert.IsType<BooruTabSnapshot>(store.Load(model.Search.View, 2, 1, Source));
        Assert.Equal("safe_draft", Assert.Single(loaded.Tabs)!.Input);
        Assert.Equal(contents, await File.ReadAllTextAsync(path + ".bad"));
        Assert.Empty(Directory.GetFiles(profile.Path, ".*.tmp"));
    }

    [Fact]
    public async Task OldSnapshotsRetainTheOriginalPagerLimits()
    {
        using TempDirectory profile = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        lease.Admission.CompleteStartup();
        string path = Path.Combine(profile.Path, BooruSearchTabStore.FileName);
        await File.WriteAllTextAsync(path, """
            {"version":1,"providerId":"danbooru","activeIndex":0,"tabs":[{
              "mode":0,"input":"cat","query":"cat","postId":null,"page":5,"isEnd":false,"empty":false,"rows":[]
            }]}
            """);
        using BooruWorkbenchModel model = new(new BooruUiHarness().Services);
        BooruSearchTabStore store = new(lease);
        BooruTabSnapshot loaded = Assert.IsType<BooruTabSnapshot>(store.Load(model.Search.View, 2, 20, Source));
        BooruSearchView tab = Assert.IsType<BooruSearchView>(Assert.Single(loaded.Tabs));
        Assert.Equal(5, tab.Page);
        Assert.Equal(new BooruPagerLimits(5, 20), tab.PagerLimits);
        Assert.True(tab.CanRetry);
    }

    [Fact]
    public async Task SaveAndLoadLimitsKeepThePermanentAndActiveTabsAndBoundTheirResults()
    {
        using TempDirectory profile = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        lease.Admission.CompleteStartup();
        BooruUiHarness harness = new();
        harness.Search.OnSearch = (query, page, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(
            BooruUiFixtures.Page(query, page, page == 3, Post("203"), Post("202"), Post("201"))));
        using BooruWorkbenchModel model = new(harness.Services);
        BooruSearchModel search = model.Search;
        BooruSearchTabs tabs = new(search);
        search.SetInput("primary");
        tabs.Add(copyCurrent: false);
        search.SetInput("middle");
        tabs.Add(copyCurrent: false);
        search.SetInput("active");
        await search.SubmitAsync();
        await search.GoToPageAsync(2);
        BooruSearchTabStore store = new(lease);
        store.Save(tabs.Capture(2), 2);
        await store.PendingSave;

        string path = Path.Combine(profile.Path, BooruSearchTabStore.FileName);
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        JsonElement saved = document.RootElement.GetProperty("tabs");
        Assert.Equal(2, saved.GetArrayLength());
        Assert.Equal(2, saved[1].GetProperty("rows").GetArrayLength());
        BooruTabSnapshot loaded = Assert.IsType<BooruTabSnapshot>(store.Load(search.View, 2, 1, Source));
        Assert.Collection(loaded.Tabs, tab => Assert.Equal("primary", tab!.Input), tab => Assert.Equal("active", tab!.Input));
        Assert.Equal(1, loaded.ActiveIndex);
        Assert.True(loaded.Tabs[1]!.HasNextPage);
        Assert.Equal("203", Assert.Single(loaded.Tabs[1]!.Rows).Post.PostId);

        BooruTabSnapshot drafts = Assert.IsType<BooruTabSnapshot>(store.Load(search.View, 2, 0, Source));
        BooruSearchView active = drafts.Tabs[1]!;
        Assert.Empty(active.Rows);
        Assert.True(active.NeedsLoad);
        Assert.Equal(2, active.Page);
        Assert.Equal("active", active.ExecutedQuery);
        Assert.True(active.CanRetry);
    }

    [Fact]
    public void ProfileSettingsDefaultToRestoringAndValidateBothLimits()
    {
        Config defaults = new();
        Assert.True(defaults.RestoreBooruSearchTabs);
        Assert.Equal(9, defaults.BooruSavedTabs);
        Assert.Equal(20, defaults.BooruSavedResultsPerTab);
        Assert.Throws<ArgumentOutOfRangeException>(() => new Config { BooruSavedTabs = 1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new Config { BooruSavedTabs = 34 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new Config { BooruSavedResultsPerTab = -1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new Config { BooruSavedResultsPerTab = 21 }.Validate());

        using TempDirectory profile = new();
        new Config { RestoreBooruSearchTabs = false, BooruSavedTabs = 2, BooruSavedResultsPerTab = 0 }.SaveConfig(profile.Path);
        Config loaded = Config.LoadConfig(profile.Path);
        Assert.False(loaded.RestoreBooruSearchTabs);
        Assert.Equal(2, loaded.BooruSavedTabs);
        Assert.Equal(0, loaded.BooruSavedResultsPerTab);
    }

    [Fact]
    public void TabEditsAreSavedAfterTheDebounceAndOptOutKeepsThePreviousCopy() => BooruComponentDispatcher.Run(() =>
    {
        using TempDirectory profile = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        lease.Admission.CompleteStartup();
        BooruSearchTabStore store = new(lease);
        using (BooruPage page = new(new BooruUiHarness().Services))
        {
            page.RestoreSearchTabs(store, new Config());
            TabControl strip = Assert.IsType<TabControl>(page.View.Body);
            Control content = Assert.IsAssignableFrom<Control>(Assert.IsType<TabItem[]>(strip.ItemsSource)[0].Content);
            SearchBox input = Assert.Single(content.GetLogicalDescendants().OfType<SearchBox>());
            input.Text = "first";
            input.Text = "latest";
            Assert.Equal("latest", page.Model.Search.View.Input);
            string path = Path.Combine(profile.Path, BooruSearchTabStore.FileName);
            Assert.False(File.Exists(path));
            BooruComponentDispatcher.Wait(BooruTestSupport.WaitAsync(() => File.Exists(path), "debounced search tabs save"));
            BooruComponentDispatcher.Wait(store.PendingSave);
            BooruTabSnapshot snapshot = Assert.IsType<BooruTabSnapshot>(store.Load(page.Model.Search.View, 2, 1, Source));
            Assert.Equal("latest", Assert.Single(snapshot.Tabs)!.Input);
            page.FlushSearchTabs();
            BooruComponentDispatcher.Wait(store.PendingSave);
        }

        using BooruPage optedOut = new(new BooruUiHarness().Services);
        optedOut.RestoreSearchTabs(store, new Config { RestoreBooruSearchTabs = false });
        Assert.Empty(optedOut.Model.Search.View.Input);
        optedOut.Model.Search.SetInput("private_draft");
        optedOut.FlushSearchTabs();
        BooruComponentDispatcher.Wait(store.PendingSave);
        BooruTabSnapshot previous = Assert.IsType<BooruTabSnapshot>(store.Load(optedOut.Model.Search.View, 2, 1, Source));
        Assert.Equal("latest", Assert.Single(previous.Tabs)!.Input);
    });

    private static BooruWorkbenchServices WithSource(BooruUiHarness harness) =>
        new(harness.Search, harness.Queue, harness.Watchers, harness.Previews, harness.Clock, harness.Links.Actions) { Source = Source };

    private static BooruPostView Post(string id, int score = 12)
    {
        BooruPost post = BooruUiFixtures.Post(id, score: score, sample: true).Post;
        return BooruPostView.From(post with
        {
            Media = post.Media!.Select(variant => variant with
            {
                Extension = variant.Extension is { } extension ? "." + extension : null
            }).ToArray()
        });
    }

    private sealed class TestWindow : IDisposable
    {
        private readonly Window _window;

        internal TestWindow(Control content)
        {
            _window = new Window { Content = content, Width = 1100, Height = 800 };
            _window.Show();
        }

        public void Dispose() => _window.Close();
    }
}
