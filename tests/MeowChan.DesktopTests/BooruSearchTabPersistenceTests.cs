using System.IO.Compression;
using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using MeowChan.Booru;
using Avalonia.VisualTree;
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
    public async Task OfflineRestartsKeepThreeTagForeignQueryAndDanbooruTab()
    {
        using TempDirectory profile = new();
        BooruSearchTabTarget foreign = new("fixture-source", BooruPostKey.DefaultInstance, null, "cat dog bird");
        BooruSearchTabTarget danbooru = new("danbooru", BooruPostKey.DefaultInstance, null, "cat dog");
        using (ProfileLease lease = ProfileLease.Acquire(profile.Path))
        {
            lease.Admission.CompleteStartup();
            Assert.True(await new BooruSearchTabStore(lease).Save(new(0, [foreign, danbooru])));
        }

        for (int restart = 0; restart < 2; restart++)
        {
            using ProfileLease lease = ProfileLease.Acquire(profile.Path);
            lease.Admission.CompleteStartup();
            BooruSearchTabStore store = new(lease);
            BooruSearchTabState saved = Assert.IsType<BooruSearchTabState>(store.Read());
            Assert.Equal(new[] { foreign, danbooru }, saved.Tabs);
            Assert.Equal(restart, saved.ActiveIndex);
            Assert.True(await store.Save(saved with { ActiveIndex = 1 - restart }));
        }
        Assert.Equal(Path.Combine(profile.Path, BooruSearchTabStore.FileName), Assert.Single(
            Directory.EnumerateFiles(profile.Path, "booru-search-tabs*")));
    }

    [Theory]
    [InlineData(false, false, "42")]
    [InlineData(false, true, "42")]
    [InlineData(true, false, "42")]
    [InlineData(true, true, "42")]
    [InlineData(true, false, "")]
    public void FirstPlusOrHandoffKeepsTheStartDraftAcrossRestart(bool restoreDraft, bool handoff, string input) =>
        BooruComponentDispatcher.Run(() =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            using TempDirectory profile = new();
            using ProfileLease lease = ProfileLease.Acquire(profile.Path);
            lease.Admission.CompleteStartup();
            BooruUiHarness offline = new();
            BooruSearchTabStore store = new(lease);
            if (restoreDraft)
            {
                using (BooruPage initial = new(WithSource(offline)))
                {
                    initial.RestoreSearchTabs(store, new Config());
                    initial.Model.Search.SetMode(BooruInputMode.Post);
                    initial.Model.Search.SetInput(input);
                }
                BooruComponentDispatcher.Wait(store.PendingSave);
            }
            using (BooruPage page = new(WithSource(offline)))
            {
                page.RestoreSearchTabs(store, new Config());
                using TestWindow window = new(page);
                if (!restoreDraft)
                {
                    page.Model.Search.SetMode(BooruInputMode.Post);
                    page.Model.Search.SetInput(input);
                }
                Assert.Empty(page.View.SearchTabs.Ids);
                if (handoff) Assert.True(page.View.TryShowQueryInNewTab("second_draft"));
                else
                {
                    Button plus = Assert.Single(page.GetVisualDescendants().OfType<Button>(),
                        button => AutomationProperties.GetAutomationId(button) == BooruWorkbenchView.NewSearchTabName);
                    BooruResultActionsTests.Click(plus);
                }
            }
            BooruComponentDispatcher.Wait(store.PendingSave);

            using BooruPage reopened = new(WithSource(offline));
            reopened.RestoreSearchTabs(new BooruSearchTabStore(lease), new Config());
            BooruSearchTabs tabs = reopened.View.SearchTabs;
            Assert.Equal(2, tabs.Ids.Count);
            Assert.Equal(tabs.Ids[1], tabs.ActiveId);
            Assert.Equal(BooruInputMode.Tags, reopened.Model.Search.View.Mode);
            Assert.Equal(handoff ? "second_draft" : "", reopened.Model.Search.View.Input);
            Assert.True(tabs.Select(tabs.Ids[0]));
            Assert.Equal(BooruInputMode.Post, reopened.Model.Search.View.Mode);
            Assert.Equal(input, reopened.Model.Search.View.Input);
            Assert.Equal(0, offline.ProviderRequests);
        });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReenablingRestoreSavesTheLiveSessionOverThePreviousFile(bool legacy) => BooruComponentDispatcher.Run(() =>
    {
        using TempDirectory profile = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        lease.Admission.CompleteStartup();
        BooruUiHarness offline = new();
        BooruSearchTabStore initialStore = new(lease);
        using (BooruPage initial = new(WithSource(offline)))
        {
            initial.RestoreSearchTabs(initialStore, new Config());
            Assert.True(initial.View.TryShowQueryInNewTab("old_session"));
        }
        BooruComponentDispatcher.Wait(initialStore.PendingSave);
        if (legacy)
        {
            File.WriteAllText(Path.Combine(profile.Path, BooruSearchTabStore.FileName), """
                {"version":1,"providerId":"danbooru","activeIndex":0,"tabs":[
                    {"mode":0,"input":"old_session","query":null,"postId":null,"page":1,"isEnd":true,"empty":false,"rows":[]}
                ],"allTabsClosable":true}
                """);
        }

        Config config = new() { RestoreBooruSearchTabs = false };
        BooruSearchTabStore store = new(lease);
        using (BooruPage page = new(WithSource(offline)))
        {
            page.RestoreSearchTabs(store, config);
            Assert.Empty(page.View.SearchTabs.Ids);
            config.RestoreBooruSearchTabs = true;
            Assert.True(page.View.TryShowQueryInNewTab("new_session"));
            page.Model.Search.SetInput("edited_session");
        }
        BooruComponentDispatcher.Wait(store.PendingSave);
        using BooruPage reopened = new(WithSource(offline));
        reopened.RestoreSearchTabs(new BooruSearchTabStore(lease), config);
        Assert.Equal(Assert.Single(reopened.View.SearchTabs.Ids), reopened.View.SearchTabs.ActiveId);
        Assert.Equal("edited_session", reopened.Model.Search.View.Input);
        Assert.Equal(0, offline.ProviderRequests);
    });

    [Fact]
    public void SessionLimitKeepsTheActiveTabAndLeavesTheLargerSavedSessionIntact() => BooruComponentDispatcher.Run(() =>
    {
        using TempDirectory profile = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        lease.Admission.CompleteStartup();
        BooruUiHarness offline = new();
        Config config = new() { BooruSearchTabs = 9, BooruSavedTabs = 9 };
        BooruSearchTabStore store = new(lease);
        using (BooruPage initial = new(WithSource(offline)))
        {
            initial.RestoreSearchTabs(store, config);
            for (int i = 0; i < 9; i++) Assert.True(initial.View.TryShowQueryInNewTab($"draft_{i}"));
            Assert.True(initial.View.SearchTabs.Select(initial.View.SearchTabs.Ids[6]));
        }
        BooruComponentDispatcher.Wait(store.PendingSave);
        string path = Path.Combine(profile.Path, BooruSearchTabStore.FileName);
        byte[] original = File.ReadAllBytes(path);
        config.BooruSearchTabs = 2;
        using (BooruPage page = new(WithSource(offline)))
        {
            page.RestoreSearchTabs(store, config);
            BooruSearchTabs tabs = page.View.SearchTabs;
            Assert.Equal(new[] { "draft_0", "draft_6" }, tabs.Ids.Select(id => tabs.ViewOf(id)!.Input));
            Assert.Equal(tabs.Ids[1], tabs.ActiveId);
            Assert.False(tabs.CanAdd);
        }
        BooruComponentDispatcher.Wait(store.PendingSave);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(0, offline.ProviderRequests);
    });

    [Theory]
    [InlineData(false, new[] { "draft_1", "edited", "draft_3" })]
    [InlineData(true, new[] { "draft_1", "edited" })]
    public void SavingUnderAReducedOpenLimitKeepsUnopenedTabsUpToTheSaveLimit(bool reduceSaveLimit, string[] expected) =>
        BooruComponentDispatcher.Run(() =>
        {
            using TempDirectory profile = new();
            using ProfileLease lease = ProfileLease.Acquire(profile.Path);
            lease.Admission.CompleteStartup();
            Config config = new() { BooruSearchTabs = 4, BooruSavedTabs = 4 };
            BooruSearchTabStore store = new(lease);
            BooruUiHarness online = new();
            online.Search.OnPost = (id, _) => Task.FromResult(BooruCallResult<BooruPostView>.Ok(Post(id)));
            using (BooruPage initial = new(WithSource(online)))
            {
                initial.RestoreSearchTabs(store, config);
                for (int i = 0; i < 4; i++) Assert.True(initial.View.TryShowQueryInNewTab($"draft_{i}"));
                BooruSearchTabs tabs = initial.View.SearchTabs;
                Assert.True(tabs.Select(tabs.Ids[1]));
                BooruSearchModel search = initial.Model.Search;
                search.SetMode(BooruInputMode.Post);
                search.SetInput("42");
                BooruComponentDispatcher.Wait(search.SubmitAsync());
                search.SetInput("draft_1");
                Assert.True(tabs.Select(tabs.Ids[2]));
            }
            BooruComponentDispatcher.Wait(store.PendingSave);

            BooruUiHarness offline = new();
            config.BooruSearchTabs = 2;
            if (reduceSaveLimit) config.BooruSavedTabs = 2;
            using (BooruPage page = new(WithSource(offline)))
            {
                page.RestoreSearchTabs(store, config);
                BooruSearchTabs tabs = page.View.SearchTabs;
                Assert.Equal(new[] { "draft_0", "draft_2" }, tabs.Ids.Select(id => tabs.ViewOf(id)!.Input));
                Assert.Equal(tabs.Ids[1], tabs.ActiveId);
                Assert.True(tabs.Select(tabs.Ids[0]));
                Assert.True(tabs.Close(tabs.ActiveId!.Value));
                Assert.Equal("draft_2", page.Model.Search.View.Input);
                Assert.True(tabs.CanAdd);
                page.Model.Search.SetInput("edited");
                page.FlushSearchTabs();
                BooruComponentDispatcher.Wait(store.PendingSave);
            }

            config.BooruSearchTabs = 4;
            config.BooruSavedTabs = 4;
            using BooruPage reopened = new(WithSource(offline));
            reopened.RestoreSearchTabs(new BooruSearchTabStore(lease), config);
            BooruSearchTabs restored = reopened.View.SearchTabs;
            Assert.Equal(expected, restored.Ids.Select(id => restored.ViewOf(id)!.Input));
            Assert.Equal(restored.Ids[1], restored.ActiveId);
            BooruSearchView hidden = Assert.IsType<BooruSearchView>(restored.ViewOf(restored.Ids[0]));
            Assert.Equal(BooruInputMode.Post, hidden.Mode);
            Assert.Equal("42", hidden.ExecutedPostId);
            Assert.Equal("42", Assert.Single(hidden.Rows).Post.PostId);
            Assert.Equal(0, offline.ProviderRequests);
        });

    [Fact]
    public void ClosingAllVisibleTabsKeepsUnopenedTabsAndTheNewDraftAcrossRestart() => BooruComponentDispatcher.Run(() =>
    {
        using TempDirectory profile = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        lease.Admission.CompleteStartup();
        Config config = new() { BooruSearchTabs = 3, BooruSavedTabs = 3 };
        BooruSearchTabStore store = new(lease);
        BooruUiHarness offline = new();
        using (BooruPage initial = new(WithSource(offline)))
        {
            initial.RestoreSearchTabs(store, config);
            foreach (string query in new[] { "first", "hidden", "last" })
                Assert.True(initial.View.TryShowQueryInNewTab(query));
        }
        BooruComponentDispatcher.Wait(store.PendingSave);

        config.BooruSearchTabs = 2;
        using (BooruPage page = new(WithSource(offline)))
        {
            page.RestoreSearchTabs(store, config);
            BooruSearchTabs tabs = page.View.SearchTabs;
            Assert.Equal(new[] { "first", "last" }, tabs.Ids.Select(id => tabs.ViewOf(id)!.Input));
            Assert.True(tabs.Close(tabs.ActiveId!.Value));
            Assert.Equal("first", page.Model.Search.View.Input);
            Assert.True(tabs.Close(Assert.Single(tabs.Ids)));
            Assert.Null(tabs.ActiveId);
            Assert.Empty(page.Model.Search.View.Input);
            page.Model.Search.SetMode(BooruInputMode.Post);
            page.Model.Search.SetInput("42");
        }
        BooruComponentDispatcher.Wait(store.PendingSave);

        config.BooruSearchTabs = 3;
        using BooruPage reopened = new(WithSource(offline));
        reopened.RestoreSearchTabs(new BooruSearchTabStore(lease), config);
        BooruSearchTabs restored = reopened.View.SearchTabs;
        Assert.Equal(new[] { "hidden", "42" }, restored.Ids.Select(id => restored.ViewOf(id)!.Input));
        Assert.Equal(restored.Ids[1], restored.ActiveId);
        Assert.Equal(BooruInputMode.Post, reopened.Model.Search.View.Mode);
        Assert.Equal(0, offline.ProviderRequests);
    });

    [Fact]
    public void RestartKeepsTheLastResultPageAfterPagingBack() => BooruComponentDispatcher.Run(() =>
    {
        using TempDirectory profile = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        lease.Admission.CompleteStartup();
        BooruSearchTabStore store = new(lease);
        BooruUiHarness online = new();
        online.Search.OnSearch = (query, page, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(
            BooruUiFixtures.Page(query, page, page == 9, Post("101"))));
        using (BooruPage page = new(WithSource(online)))
        {
            page.RestoreSearchTabs(store, new Config());
            BooruSearchModel search = page.Model.Search;
            search.ReadPagerLimits = () => new(12, 20);
            page.ShowQuery("cat");
            BooruComponentDispatcher.Wait(search.SubmitAsync());
            BooruComponentDispatcher.Wait(search.GoToPageAsync(9));
            Assert.Equal(9, search.View.LastResultPage);
            BooruComponentDispatcher.Wait(search.GoToPageAsync(8));
            Assert.Equal(9, search.View.LastResultPage);
        }
        BooruComponentDispatcher.Wait(store.PendingSave);

        BooruUiHarness offline = new();
        using BooruPage reopened = new(WithSource(offline));
        reopened.RestoreSearchTabs(new BooruSearchTabStore(lease), new Config());
        BooruSearchModel restored = reopened.Model.Search;
        Assert.Equal(8, restored.View.Page);
        Assert.Equal(9, restored.View.LastResultPage);
        Assert.True(restored.View.HasNextPage);
        BooruComponentDispatcher.Wait(restored.GoToPageAsync(10));
        Assert.Equal(8, restored.View.Page);
        Assert.Equal(0, offline.ProviderRequests);
    });

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
        restoredTabs.Select(restoredTabs.Ids[0]);
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
    [InlineData("""
        {"version":1,"providerId":"danbooru","activeIndex":0,"tabs":[null],"allTabsClosable":true,
         "draft":{"mode":0,"input":"invalid","query":null,"postId":null,"page":0,"isEnd":true,"empty":false,"rows":[]}}
        """)]
    [InlineData("""
        {"version":1,"providerId":"danbooru","activeIndex":1,"tabs":[null,null],"allTabsClosable":true,
         "draft":{"mode":0,"input":"ambiguous","query":null,"postId":null,"page":1,"isEnd":true,"empty":false,"rows":[]}}
        """)]
    [InlineData("{\"version\":2,\"providerId\":\"danbooru\",\"activeIndex\":0,\"tabs\":[null]}")]
    public async Task DamagedFilesAreQuarantinedAndTheNextSaveWorks(string contents)
    {
        using TempDirectory profile = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        lease.Admission.CompleteStartup();
        string path = Path.Combine(profile.Path, BooruSearchTabStore.FileName);
        await File.WriteAllTextAsync(path, contents);
        using BooruWorkbenchModel model = new(new BooruUiHarness().Services);
        BooruSearchTabStore store = new(lease);
        Assert.Null(store.Read());
        Assert.False(File.Exists(path));
        Assert.Equal(contents, await File.ReadAllTextAsync(path + ".bad"));

        BooruSearchTabs tabs = new(model.Search);
        tabs.Add(copyCurrent: false);
        model.Search.SetInput("safe_draft");
        Assert.True(await store.Save(tabs.CaptureTargets(2, 1)));
        BooruSearchTabState loaded = Assert.IsType<BooruSearchTabState>(store.Read());
        Assert.Equal("safe_draft", Assert.Single(loaded.Tabs)!.Input);
        Assert.Equal(contents, await File.ReadAllTextAsync(path + ".bad"));
        Assert.Empty(Directory.GetFiles(profile.Path, ".*.tmp"));
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(0, "start_here")]
    [InlineData(Config.BooruSavedTabsMaximum, null)]
    public async Task WriterRoundTripsEmptyStripStartDraftAndFullSession(int count, string? startDraft)
    {
        using TempDirectory profile = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        lease.Admission.CompleteStartup();
        BooruUiHarness harness = new();
        harness.Search.OnSearch = (query, page, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(
            BooruUiFixtures.Page(query, page, page == 2, Post("101"), Post("102"))));
        using BooruWorkbenchModel model = new(harness.Services);
        BooruSearchModel search = model.Search;
        BooruSearchTabs tabs = new(search, () => Config.BooruSearchTabsMaximum);
        string[] inputs = Enumerable.Range(0, count).Select(index => index == 0 ? "" : $"draft_{index}").ToArray();
        foreach (string input in inputs)
        {
            Assert.True(tabs.Add(copyCurrent: false) > 0);
            search.SetInput(input);
        }
        if (startDraft is not null) search.SetInput(startDraft);
        int active = count == 0 ? -1 : 17;
        if (active >= 0)
        {
            Assert.True(tabs.Select(tabs.Ids[active]));
            await search.SubmitAsync();
            await search.GoToPageAsync(2);
        }

        BooruSearchTabStore store = new(lease);
        Assert.True(await store.Save(tabs.CaptureTargets(Config.BooruSavedTabsMaximum, 2)));
        Assert.True(await store.PendingSave);
        string path = Path.Combine(profile.Path, BooruSearchTabStore.FileName);
        byte[] bytes = await File.ReadAllBytesAsync(path);
        using JsonDocument document = JsonDocument.Parse(bytes);
        Assert.Equal(2, document.RootElement.GetProperty("version").GetInt32());
        JsonElement state = document.RootElement.GetProperty("state");
        Assert.Equal(count == 0, state.TryGetProperty("startDraft", out _));
        Assert.Equal(Math.Max(1, count), state.GetProperty("tabs").GetArrayLength());
        Assert.Single(Directory.GetFiles(profile.Path, "booru-search-tabs*.json"));

        BooruSearchTabState restored = Assert.IsType<BooruSearchTabState>(new BooruSearchTabStore(lease).Read());
        Assert.Equal(Math.Max(0, active), restored.ActiveIndex);
        using BooruWorkbenchModel offline = new(new BooruUiHarness().Services);
        BooruSearchTabs reopened = new(offline.Search, () => Config.BooruSearchTabsMaximum);
        reopened.RestoreTargets(restored, Config.BooruSavedTabsMaximum, 2, Source);
        Assert.Equal(inputs, reopened.Ids.Select(id => reopened.ViewOf(id)!.Input));
        if (active < 0)
        {
            Assert.Null(reopened.ActiveId);
            Assert.Equal(startDraft ?? string.Empty, offline.Search.View.Input);
        }
        if (active >= 0)
        {
            BooruSearchView result = offline.Search.View;
            Assert.Equal("draft_17", result.ExecutedQuery);
            Assert.Equal(2, result.Page);
            Assert.Equal(new[] { "101", "102" }, result.Rows.Select(row => row.Post.PostId));
        }

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
        BooruSearchTabState loaded = Assert.IsType<BooruSearchTabState>(store.Read());
        BooruSearchTabs tabs = new(model.Search);
        tabs.RestoreTargets(loaded, 2, 20, Source);
        BooruSearchView tab = model.Search.View;
        Assert.Equal(5, tab.Page);
        Assert.Equal(new BooruPagerLimits(5, 20), tab.PagerLimits);
        Assert.True(tab.CanRetry);
    }

    [Fact]
    public async Task SaveLimitKeepsTheEarliestAndActiveTabsAndBoundsTheirResults()
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
        tabs.Add(copyCurrent: false);
        search.SetInput("first");
        tabs.Add(copyCurrent: false);
        search.SetInput("middle");
        tabs.Add(copyCurrent: false);
        search.SetInput("active");
        await search.SubmitAsync();
        await search.GoToPageAsync(2);
        BooruSearchTabStore store = new(lease);
        await store.Save(tabs.CaptureTargets(2, 2));

        string path = Path.Combine(profile.Path, BooruSearchTabStore.FileName);
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        JsonElement saved = document.RootElement.GetProperty("state").GetProperty("tabs");
        Assert.Equal(2, saved.GetArrayLength());
        Assert.Equal(2, saved[1].GetProperty("rows").GetArrayLength());
        BooruSearchTabState loaded = Assert.IsType<BooruSearchTabState>(store.Read());
        Assert.Collection(loaded.Tabs, tab => Assert.Equal("first", tab!.Input), tab => Assert.Equal("active", tab!.Input));
        Assert.Equal(1, loaded.ActiveIndex);
        tabs.RestoreTargets(loaded, 2, 1, Source);
        Assert.True(search.View.HasNextPage);
        Assert.Equal("203", Assert.Single(search.View.Rows).Post.PostId);

        tabs.RestoreTargets(loaded, 2, 0, Source);
        BooruSearchView active = search.View;
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

    [Theory]
    [InlineData("empty", 0)]
    [InlineData("draft", 0)]
    [InlineData("full", 17)]
    public void Legacy112WriterFilesMigrate(string fixture, int activeIndex)
    {
        using TempDirectory profile = new();
        string path = Path.Combine(profile.Path, BooruSearchTabStore.FileName);
        using (Stream original = Assert.IsAssignableFrom<Stream>(typeof(BooruSearchTabPersistenceTests).Assembly
            .GetManifestResourceStream($"MeowChan.DesktopTests.Fixtures.Booru112.{fixture}.json")))
        using (FileStream copy = File.Create(path))
            original.CopyTo(copy);

        using JsonDocument writer = JsonDocument.Parse(File.ReadAllBytes(path));
        JsonElement writerTabs = writer.RootElement.GetProperty("tabs");
        string[] expectedInputs = fixture == "full" ? Enumerable.Range(0, 33)
            .Select(index => index == 0 ? "" : $"draft_{index}").ToArray() : [];
        ReadExpectedTabs();
        byte[] migratedBytes = File.ReadAllBytes(path);
        using JsonDocument migrated = JsonDocument.Parse(migratedBytes);
        Assert.Equal(2, migrated.RootElement.GetProperty("version").GetInt32());
        ReadExpectedTabs();
        Assert.Equal(migratedBytes, File.ReadAllBytes(path));

        void ReadExpectedTabs()
        {
            using ProfileLease lease = ProfileLease.Acquire(profile.Path);
            BooruSearchTabState? state = new BooruSearchTabStore(lease).Read();
            Assert.False(File.Exists(path + ".bad"), "The 1.1.2 writer file was quarantined.");
            Assert.NotNull(state);
            Assert.Equal(activeIndex, state.ActiveIndex);
            Assert.Equal(writerTabs.GetArrayLength(), state.Tabs.Length);

            BooruUiHarness harness = new();
            using BooruWorkbenchModel model = new(WithSource(harness));
            BooruSearchTabs tabs = new(model.Search, () => Config.BooruSearchTabsMaximum);
            tabs.RestoreTargets(state, Config.BooruSavedTabsMaximum, Config.BooruSavedResultsPerTabMaximum, Source);
            Assert.Equal(expectedInputs, tabs.Ids.Select(id => tabs.ViewOf(id)?.Input));
            if (fixture == "full")
            {
                Assert.Null(state.StartDraft);
                Assert.Equal(tabs.Ids[activeIndex], tabs.ActiveId);
                for (int index = 0; index < state.Tabs.Length; index++)
                    AssertTarget(writerTabs[index], state.Tabs[index]);
                BooruSearchView active = model.Search.View;
                Assert.Equal("draft_17", active.ExecutedQuery);
                Assert.Equal(2, active.Page);
                Assert.Equal(new[] { "101", "102" }, active.Rows.Select(row => row.Post.PostId));
                Assert.True(tabs.Close(tabs.Ids[0]));
            }
            else
            {
                Assert.Null(Assert.Single(state.Tabs));
                Assert.Null(tabs.ActiveId);
                Assert.Equal(fixture == "draft" ? "start_here" : "", model.Search.View.Input);
                if (fixture == "draft") AssertTarget(writer.RootElement.GetProperty("draft"), state.StartDraft);
            }
            Assert.Equal(0, harness.ProviderRequests);
        }

        static void AssertTarget(JsonElement expected, BooruSearchTabTarget? target)
        {
            Assert.NotNull(target);
            Assert.Equal("danbooru", target.SourceId);
            Assert.Equal(BooruPostKey.DefaultInstance, target.InstanceId);
            JsonElement actual = JsonSerializer.SerializeToElement(target, JsonSerializerOptions.Web);
            foreach (JsonProperty property in expected.EnumerateObject())
                Assert.True(JsonElement.DeepEquals(property.Value, actual.GetProperty(property.Name)),
                    $"The target did not retain {property.Name}.");
        }
    }

    [Fact]
    public void Legacy113WriterRestoresVisibleAndHiddenTabs()
    {
        // Gzip of the exact full snapshot emitted by WriterProducesMigrationFiles at fa1961e70.
        const string snapshot = """
            H4sIAAAAAAAC/+VYTW/bMAz9L7rObSylaVPftm6HHgYUXXcahkKxGEebLHmSnKQL8t9H2XGTbl32dSEwIDEgihQfKdFP9IYtwQft
            LCt4xhrvllqBv1asYEramXO+ZRmTZdRLuLYK1qh3kbEoZ4EVHzasdgpYkWdM26aNaIXaX1rwD6ywrTG4pAsxLbcbyQo6Tzq8sSiN
            voWMQd1ENJhLE3Dk3Sqt/TFjtVzruq1vOqNJv1a4Ad8LBHo1MsRbCK2JvSx52WbPwFJezuM9J41OkEY3Jo3ujDS6CWl056TRXZBG
            NyWN7pL22zinDY82W3DadMFp8wWnTRicNmNw2pTBDzjjUPQcSPE7IDedKSs2TKv0DK71JXx/Sx6WZzznbJuxuQaj0HzCeT/53huc
            XcTYhGI0GixPlbO11KdtGHWRj5J5xkoPMoJ6mcISuTg/4fwk53f5tMhz/L3onqjnZdS26rLt5ep2N2IVToXS+bQPGONcLp3XEa5c
            a3HFcbrCV31oVtaoxEoZk1f0WbmUOJ4SvJubmRbu4QHCTzXC54cnc2KbNhaUlp0P70yf6/ZJCkplD6Ln03yN/xT/6acmBQDrCLbv
            TtijqIES8/JOf4VhJ1daxQXimeJJWICuFnE3GJTfqsn+0PRY8uNYMFmVttJ0YBr7A5hBdAiGi/HZ5Pxij+dSHALKf4GIH0cUZN0Y
            eD45fRqOpKYfDFCequ+h4Jb1J/vKSF0Pejq8BgOo+FgVOryS1j4KtqlxhKWG1T6kP6wX8W/1Iv7zehGU6kUQqxdBvl7+igPFMQKk
            3ZZw2n2JoN2XCOJfsWj3JYJ2XyJo9yWCdl8iaPclgva3LEGbNQRt1hjTZo0xbdYYU2ANtJTG3MlZuDIuyFm6ZfaOOpA7rW/Z0hog
            MxsAAA==
            """;
        using TempDirectory profile = new();
        string path = Path.Combine(profile.Path, BooruSearchTabStore.FileName);
        using (MemoryStream bytes = new(Convert.FromBase64String(snapshot)))
        using (GZipStream original = new(bytes, CompressionMode.Decompress))
        using (FileStream copy = File.Create(path))
            original.CopyTo(copy);

        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        lease.Admission.CompleteStartup();
        BooruSearchTabState? restored = new BooruSearchTabStore(lease).Read();
        Assert.False(File.Exists(path + ".bad"), "The 1.1.3 tabs were quarantined.");
        Assert.NotNull(restored);

        BooruUiHarness offline = new();
        using BooruWorkbenchModel model = new(WithSource(offline));
        int openLimit = Config.BooruSearchTabsMinimum;
        BooruSearchTabs tabs = new(model.Search, () => openLimit);
        tabs.RestoreTargets(restored, source: Source);
        Assert.Equal(new[] { "", "draft_17" }, tabs.Ids.Select(id => tabs.ViewOf(id)!.Input));
        Assert.Equal(tabs.Ids[1], tabs.ActiveId);
        BooruSearchView active = model.Search.View;
        Assert.Equal("draft_17", active.ExecutedQuery);
        Assert.Equal(2, active.Page);
        Assert.True(active.IsEnd);
        Assert.False(active.HasNextPage);
        Assert.Equal(new[] { "101", "102" }, active.Rows.Select(row => row.Post.PostId));

        openLimit = Config.BooruSearchTabsMaximum;
        BooruSearchTabState reopened = Assert.IsType<BooruSearchTabState>(new BooruSearchTabStore(lease).Read());
        tabs.RestoreTargets(reopened, source: Source);
        Assert.Equal(Config.BooruSavedTabsMaximum, tabs.Ids.Count);
        Assert.Equal(tabs.Ids[17], tabs.ActiveId);
        Assert.True(tabs.Select(tabs.Ids[1]));
        Assert.Equal("draft_1", model.Search.View.Input);
        Assert.Null(model.Search.View.ExecutedQuery);
        Assert.Empty(model.Search.View.Rows);
        Assert.Equal(0, offline.ProviderRequests);
    }

    internal static async Task ReopenLegacyPageWithKnownEndAsync(string directory, bool editDeferredTab)
    {
        using ProfileLease lease = ProfileLease.Acquire(directory);
        lease.Admission.CompleteStartup();
        var store = new BooruSearchTabStore(lease);
        var snapshot = Assert.IsType<BooruSearchTabState>(store.Read());
        var offline = new BooruUiHarness();
        using var model = new BooruWorkbenchModel(WithSource(offline));
        int openLimit = Config.BooruSearchTabsMinimum;
        var tabs = new BooruSearchTabs(model.Search, () => openLimit);
        tabs.RestoreTargets(snapshot, source: Source);
        Assert.Equal(new[] { "", "draft_17" }, tabs.Ids.Select(id => tabs.ViewOf(id)!.Input));
        Assert.Equal(tabs.Ids[1], tabs.ActiveId);
        AssertPaging(model.Search.View);

        openLimit = Config.BooruSearchTabsMaximum;
        tabs.RestoreTargets(snapshot, source: Source);
        Assert.Equal(33, tabs.Ids.Count);
        Assert.True(tabs.Select(tabs.Ids[1]));
        Assert.Equal(editDeferredTab ? "draft_1" : "offline_edit", model.Search.View.Input);
        if (editDeferredTab) model.Search.SetInput("offline_edit");
        Assert.True(tabs.Select(tabs.Ids[17]));
        AssertPaging(model.Search.View);
        Assert.True(await store.Save(tabs.CaptureTargets()));
        Assert.False(File.Exists(Path.Combine(directory, BooruSearchTabStore.FileName + ".bad")));
        Assert.Equal(0, offline.ProviderRequests);

        static void AssertPaging(BooruSearchView view)
        {
            Assert.Equal(2, view.Page);
            Assert.Equal(5, view.LastResultPage);
            Assert.Equal(8, view.PagerLimits.Pages);
            Assert.True(view.HasNextPage);
            Assert.True(view.HasPreviousPage);
            Assert.Equal(new[] { "101", "102" }, view.Rows.Select(row => row.Post.PostId));
        }
    }

    [Legacy113WriterFact]
    public async Task ReleasedWriterDraftSurvivesTwoOfflineRestarts()
    {
        using TempDirectory profile = new();
        using Legacy113ProfileWriter writer = new();
        await writer.WriteTabsAsync(profile.Path, draft: true);
        for (int restart = 0; restart < 2; restart++)
        {
            using ProfileLease lease = ProfileLease.Acquire(profile.Path);
            lease.Admission.CompleteStartup();
            var store = new BooruSearchTabStore(lease);
            var snapshot = Assert.IsType<BooruSearchTabState>(store.Read());
            var offline = new BooruUiHarness();
            using var model = new BooruWorkbenchModel(WithSource(offline));
            var tabs = new BooruSearchTabs(model.Search);
            tabs.RestoreTargets(snapshot, source: Source);
            Assert.Empty(tabs.Ids);
            Assert.Equal("start_here", model.Search.View.Input);
            Assert.True(await store.Save(tabs.CaptureTargets()));
            Assert.Equal(0, offline.ProviderRequests);
            Assert.False(File.Exists(Path.Combine(profile.Path, BooruSearchTabStore.FileName + ".bad")));
        }
    }

    [Fact]
    public void Legacy113WriterRestoresStartDraftWithoutRewritingIt()
    {
        // The exact draft snapshot emitted by WriterProducesMigrationFiles at fa1961e70.
        const string snapshot = """
            {"version":1,"providerId":"danbooru","activeIndex":0,"tabs":[null],"allTabsClosable":true,"draft":{"mode":0,"input":"start_here","query":null,"postId":null,"page":1,"isEnd":true,"empty":false,"rows":[],"maximumPage":5,"postsPerPage":20,"lastResultPage":null}}
            """;
        using TempDirectory profile = new();
        string path = Path.Combine(profile.Path, BooruSearchTabStore.FileName);
        File.WriteAllText(path, snapshot);

        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        lease.Admission.CompleteStartup();
        BooruSearchTabState? restored = new BooruSearchTabStore(lease).Read(migrateLegacy: false);
        Assert.False(File.Exists(path + ".bad"), "The 1.1.3 start draft was quarantined.");
        Assert.NotNull(restored);
        Assert.Equal(snapshot, File.ReadAllText(path));

        BooruUiHarness offline = new();
        using BooruWorkbenchModel model = new(WithSource(offline));
        BooruSearchTabs tabs = new(model.Search);
        tabs.RestoreTargets(restored, source: Source);
        Assert.Empty(tabs.Ids);
        Assert.Null(tabs.ActiveId);
        Assert.Equal("start_here", model.Search.View.Input);
        Assert.Equal(BooruInputMode.Tags, model.Search.View.Mode);
        Assert.Equal(0, offline.ProviderRequests);
    }

    [Fact]
    public void Legacy111MigrationKeepsDraftsResultsAndTheActiveTabBeyondTheViewLimits() =>
        BooruComponentDispatcher.Run(() =>
        {
            using TempDirectory profile = new();
            using ProfileLease lease = ProfileLease.Acquire(profile.Path);
            lease.Admission.CompleteStartup();
            string path = Path.Combine(profile.Path, BooruSearchTabStore.FileName);
            byte[] preview = BooruUiFixtures.Png(5);
            string row = JsonSerializer.Serialize(new { Post = Post("42", -1).Post, Preview = preview }, JsonSerializerOptions.Web);
            File.WriteAllText(path, $$"""
                {"version":1,"providerId":"danbooru","activeIndex":2,"tabs":[
                    {"mode":0,"input":"primary","query":null,"postId":null,"page":1,"isEnd":true,"empty":false,"rows":[]},
                    {"mode":0,"input":"middle","query":null,"postId":null,"page":1,"isEnd":true,"empty":false,"rows":[]},
                    {"mode":1,"input":"https://danbooru.donmai.us/posts/42","query":null,"postId":"42","page":1,
                     "isEnd":true,"empty":false,"rows":[{{row}}],"maximumPage":3,"postsPerPage":2}
                ]}
                """);

            BooruSearchTabStore store = new(lease);
            BooruUiHarness harness = new();
            using BooruPage page = new(WithSource(harness));
            page.RestoreSearchTabs(store, new Config { BooruSavedTabs = 2, BooruSavedResultsPerTab = 0 });
            Assert.Equal(2, page.View.SearchTabs.Ids.Count);
            Assert.Equal(new BooruPostKey("danbooru", BooruPostKey.DefaultInstance, "42"), page.Model.Search.View.ExecutedPostIdentity);
            Assert.Equal(new BooruPagerLimits(3, 2), page.Model.Search.View.PagerLimits);
            Assert.Empty(page.Model.Search.View.Rows);
            Assert.Equal(0, harness.ProviderRequests);
            Assert.Equal(0, harness.Previews.Requests);
            page.FlushSearchTabs();
            BooruComponentDispatcher.Wait(store.PendingSave);

            using JsonDocument migrated = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(2, migrated.RootElement.GetProperty("version").GetInt32());
            BooruSearchTabState complete = Assert.IsType<BooruSearchTabState>(store.Read());
            Assert.Equal(2, complete.ActiveIndex);
            Assert.Equal("middle", complete.Tabs[1]!.Input);
            BooruSearchTabRow result = Assert.Single(complete.Tabs[2]!.Rows!);
            Assert.Equal(-1, result.Post.Score);
            Assert.Equal(preview, result.Preview);
            Assert.False(File.Exists(path + ".bad"));
        });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SwitchingTabsUnderReducedLimitsPreservesDeferredTargetsAcrossRestart(bool reopenWithReducedLimits) =>
        BooruComponentDispatcher.Run(() =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            using TempDirectory profile = new();
            Config config = new() { BooruSavedTabs = 4, BooruSearchTabs = 4 };
            using (ProfileLease lease = ProfileLease.Acquire(profile.Path))
            {
                BooruSearchTabStore store = new(lease);
                store.Write(new BooruSearchTabState(3,
                [
                    new(null, null, null, "primary"),
                    new("danbooru", "test-instance", "42", null),
                    new(null, null, null, "middle"),
                    new(null, null, null, "active")
                ]));
            }

            if (reopenWithReducedLimits) LowerLimits();
            using (ProfileLease lease = ProfileLease.Acquire(profile.Path))
            {
                BooruSearchTabStore store = new(lease);
                using BooruPage page = new(WithSource(new BooruUiHarness()));
                page.View.SearchTabsLimit = () => config.BooruSearchTabs;
                page.RestoreSearchTabs(store, config);
                BooruSearchTabs tabs = page.View.SearchTabs;
                Assert.Equal(reopenWithReducedLimits ? 2 : 4, tabs.Ids.Count);
                Assert.Equal("active", page.Model.Search.View.ExecutedQuery);
                LowerLimits();
                Assert.True(tabs.Select(BooruSearchTabs.PrimaryId));
                page.FlushSearchTabs();
                BooruComponentDispatcher.Wait(store.PendingSave);
            }

            config.BooruSavedTabs = 4;
            config.BooruSearchTabs = 4;
            using ProfileLease reopened = ProfileLease.Acquire(profile.Path);
            BooruSearchTabStore restored = new(reopened);
            BooruUiHarness offline = new();
            using BooruPage restoredPage = new(WithSource(offline));
            restoredPage.View.SearchTabsLimit = () => config.BooruSearchTabs;
            restoredPage.RestoreSearchTabs(restored, config);
            BooruSearchTabs restoredTabs = restoredPage.View.SearchTabs;
            Assert.Equal(BooruSearchTabs.PrimaryId, restoredTabs.ActiveId);
            Assert.Collection(restoredTabs.Ids,
                id => Assert.Equal("primary", restoredTabs.ViewOf(id)!.ExecutedQuery),
                id => Assert.Equal("test-instance", restoredTabs.ViewOf(id)!.InstanceId),
                id => Assert.Equal("middle", restoredTabs.ViewOf(id)!.ExecutedQuery),
                id => Assert.Equal("active", restoredTabs.ViewOf(id)!.ExecutedQuery));
            Assert.True(restoredTabs.Close(restoredTabs.Ids[1]));
            restoredPage.FlushSearchTabs();
            BooruComponentDispatcher.Wait(restored.PendingSave);
            Assert.Equal(new[] { "primary", "middle", "active" }, restored.Read()!.Tabs.Select(tab => tab!.Query));
            Assert.Equal(0, offline.ProviderRequests);

            void LowerLimits()
            {
                config.BooruSavedTabs = 2;
                config.BooruSearchTabs = 2;
            }
        });

    [Fact]
    public void TabSwitchesStayResponsiveAndShutdownDrainsTheLatestSnapshotWhenTheProfileIsBusy() =>
        BooruComponentDispatcher.Run(() =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            using TempDirectory profile = new();
            using (ProfileLease lease = ProfileLease.Acquire(profile.Path))
            {
                lease.Admission.CompleteStartup();
                BooruSearchTabStore store = new(lease);
                using BooruPage page = new(WithSource(new BooruUiHarness()));
                page.RestoreSearchTabs(store, new Config());
                TaskCompletionSource locked = new(TaskCreationOptions.RunContinuationsAsynchronously);
                TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Task<bool> blocker = Task.Run(() => lease.RunExclusive(() =>
                {
                    locked.SetResult();
                    release.Task.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
                    return true;
                }));
                Task drain;
                try
                {
                    BooruComponentDispatcher.Wait(locked.Task);
                    page.Model.Search.SetInput("first");
                    BooruComponentDispatcher.Wait(BooruTestSupport.WaitAsync(() => !store.PendingSave.IsCompleted, "queued tab snapshot"));
                    Task<bool> first = store.PendingSave;

                    BooruSearchTabs tabs = page.View.SearchTabs;
                    int second = tabs.Add(copyCurrent: false);
                    page.Model.Search.SetInput("intermediate");
                    Assert.True(tabs.Select(BooruSearchTabs.PrimaryId));
                    page.Model.Search.SetInput("latest_primary");
                    Assert.True(tabs.Select(second));
                    page.Model.Search.SetInput("latest");
                    page.FlushSearchTabs();
                    Assert.Same(first, store.PendingSave);

                    lease.Admission.BeginShutdown();
                    drain = lease.Admission.DrainAsync();
                    Assert.False(drain.IsCompleted);
                    Assert.False(File.Exists(Path.Combine(profile.Path, BooruSearchTabStore.FileName)));
                }
                finally
                {
                    release.TrySetResult();
                    BooruComponentDispatcher.Wait(blocker);
                }
                Assert.True(BooruComponentDispatcher.Wait(store.PendingSave));
                BooruComponentDispatcher.Wait(drain);
            }

            using ProfileLease reopened = ProfileLease.Acquire(profile.Path);
            BooruSearchTabState saved = Assert.IsType<BooruSearchTabState>(new BooruSearchTabStore(reopened).Read());
            Assert.Equal(1, saved.ActiveIndex);
            Assert.Equal(new[] { "latest_primary", "latest" }, saved.Tabs.Select(tab => tab!.Input));
        });

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
            TabItem selected = Assert.IsType<TabItem>(strip.SelectedItem);
            Control content = Assert.IsAssignableFrom<Control>(selected.Content);
            SearchBox input = Assert.Single(content.GetLogicalDescendants().OfType<SearchBox>(),
                box => AutomationProperties.GetAutomationId(box) == BooruWorkbenchView.InputName);
            input.Text = "first";
            input.Text = "latest";
            Assert.Equal("latest", page.Model.Search.View.Input);
            string path = Path.Combine(profile.Path, BooruSearchTabStore.FileName);
            Assert.False(File.Exists(path));
            BooruComponentDispatcher.Wait(BooruTestSupport.WaitAsync(() => File.Exists(path), "debounced search tabs save"));
            BooruComponentDispatcher.Wait(store.PendingSave);
            BooruSearchTabState snapshot = Assert.IsType<BooruSearchTabState>(store.Read());
            Assert.Null(Assert.Single(snapshot.Tabs));
            Assert.Equal("latest", snapshot.StartDraft!.Input);
            page.FlushSearchTabs();
            BooruComponentDispatcher.Wait(store.PendingSave);
        }

        string file = Path.Combine(profile.Path, BooruSearchTabStore.FileName);
        byte[] saved = File.ReadAllBytes(file);
        BooruSearchTabStore optedOutStore = new(lease);
        using BooruPage optedOut = new(new BooruUiHarness().Services);
        optedOut.RestoreSearchTabs(optedOutStore, new Config { RestoreBooruSearchTabs = false });
        Assert.Empty(optedOut.Model.Search.View.Input);
        optedOut.Model.Search.SetInput("private_draft");
        optedOut.FlushSearchTabs();
        BooruComponentDispatcher.Wait(optedOutStore.PendingSave);
        Assert.Equal(saved, File.ReadAllBytes(file));
        BooruSearchTabState previous = Assert.IsType<BooruSearchTabState>(optedOutStore.Read());
        Assert.Null(Assert.Single(previous.Tabs));
        Assert.Equal("latest", previous.StartDraft!.Input);
    });

    [Fact]
    public void ClearedDraftsKeepTheirMarkersAfterDuplicationAndRestart() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        using TempDirectory profile = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        lease.Admission.CompleteStartup();
        BooruSearchTabStore store = new(lease);
        BooruUiHarness online = new();
        online.Search.OnSearch = (query, page, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(
            BooruUiFixtures.Page(query, page, true, Post("101"))));
        using (BooruPage page = new(WithSource(online)))
        {
            page.RestoreSearchTabs(store, new Config());
            page.ShowQuery("cat");
            BooruComponentDispatcher.Wait(page.Model.Search.SubmitAsync());
            page.Model.Search.SetInput("");
            Assert.True(page.View.SearchTabs.Add(copyCurrent: true) > 0);
            Assert.True(page.View.SearchTabs.Add(copyCurrent: false) > 0);
            page.FlushSearchTabs();
            BooruComponentDispatcher.Wait(store.PendingSave);
        }
        Assert.Equal(("cat", 1), Assert.Single(online.Search.Searches));

        BooruUiHarness offline = new();
        using BooruPage restored = new(WithSource(offline));
        restored.RestoreSearchTabs(new BooruSearchTabStore(lease), new Config());
        using TestWindow window = new(restored);
        Dispatcher.UIThread.RunJobs();
        TabControl strip = Assert.IsType<TabControl>(restored.View.Body);
        var ids = restored.View.SearchTabs.Ids;
        Assert.Equal(3, ids.Count);
        Assert.Equal(ids[2], restored.View.SearchTabs.ActiveId);
        foreach (int id in ids)
        {
            TabItem tab = Assert.Single(strip.Items.OfType<TabItem>(), item => Equals(item.Tag, id));
            ContentControl marker = Assert.Single(tab.GetVisualDescendants().OfType<ContentControl>(),
                item => AutomationProperties.GetAutomationId(item) == BooruWorkbenchView.TabDraftMarkerName);
            Assert.Equal(id != ids[2], marker.IsVisible);
            BooruSearchView view = Assert.IsType<BooruSearchView>(restored.View.SearchTabs.ViewOf(id));
            Assert.Empty(view.Input);
            Assert.Equal(id == ids[2] ? null : "cat", view.ExecutedQuery);
        }
        Assert.Equal(0, offline.ProviderRequests);
    });

    [Theory]
    [InlineData(0, false, 33, 0)]
    [InlineData(32, false, 33, 32)]
    [InlineData(0, true, 32, 0)]
    [InlineData(32, true, 32, 31)]
    public void LegacyFullProfileKeepsAllOccupiedSlots(int active, bool placeholder, int expectedCount, int expectedIndex) =>
        BooruComponentDispatcher.Run(() =>
        {
            using TempDirectory profile = new();
            Config config = JsonSerializer.Deserialize("""{"BooruSearchTabs":32,"BooruSavedTabs":33}""", ConfigJsonContext.Default.Config)!;
            using ProfileLease lease = ProfileLease.Acquire(profile.Path);
            lease.Admission.CompleteStartup();
            var legacyTabs = Enumerable.Range(0, 33).Select(index => new
            {
                mode = index == 32 ? 1 : 0,
                input = index == 0 && placeholder ? "" : $"draft_{index}",
                query = index == 1 ? "cat" : null,
                postId = index == 32 ? "42" : null,
                page = index == 1 ? 2 : 1,
                isEnd = false,
                empty = false,
                rows = Array.Empty<object>(),
                maximumPage = 3,
                postsPerPage = 7
            }).ToArray();
            string path = Path.Combine(profile.Path, BooruSearchTabStore.FileName);
            string original = JsonSerializer.Serialize(new { version = 1, providerId = "danbooru", activeIndex = active, tabs = legacyTabs });
            File.WriteAllText(path, original);
            BooruUiHarness offline = new();
            using BooruPage page = new(WithSource(offline));
            BooruSearchTabStore store = new(lease);
            page.RestoreSearchTabs(store, config);
            BooruSearchTabs tabs = page.View.SearchTabs;
            string migrated = File.ReadAllText(path);
            using JsonDocument v2 = JsonDocument.Parse(migrated);
            Assert.Equal(2, v2.RootElement.GetProperty("version").GetInt32());
            Assert.Equal(33, tabs.Limit);
            Assert.Equal(expectedCount, tabs.Ids.Count);
            Assert.Equal(tabs.Ids[expectedIndex], tabs.ActiveId);
            foreach (int index in Enumerable.Range(0, expectedCount))
                Assert.Equal($"draft_{index + (placeholder ? 1 : 0)}", tabs.ViewOf(tabs.Ids[index])!.Input);
            BooruSearchView paged = tabs.ViewOf(tabs.Ids[placeholder ? 0 : 1])!;
            Assert.Equal("cat", paged.ExecutedQuery);
            Assert.Equal(2, paged.Page);
            Assert.Equal(new BooruPagerLimits(3, 7), paged.PagerLimits);
            BooruSearchView post = tabs.ViewOf(tabs.Ids[^1])!;
            Assert.Equal("42", post.ExecutedPostId);
            Assert.Equal(BooruInputMode.Post, post.Mode);
            Assert.Equal(0, offline.ProviderRequests);
            page.FlushSearchTabs();
            BooruComponentDispatcher.Wait(store.PendingSave);
            Assert.Equal(migrated, File.ReadAllText(path));
            Assert.False(File.Exists(path + ".bad"));
        });

    [Fact]
    public void ClosingTheLastTabPersistsEmptyAcrossRestart() => BooruComponentDispatcher.Run(() =>
    {
        using TempDirectory profile = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        lease.Admission.CompleteStartup();
        BooruSearchTabStore store = new(lease);
        BooruUiHarness harness = new();
        using (BooruPage page = new(WithSource(harness)))
        {
            page.RestoreSearchTabs(store, new Config());
            Assert.True(page.View.TryShowQueryInNewTab("cat"));
            page.FlushSearchTabs();
            BooruComponentDispatcher.Wait(store.PendingSave);
        }
        using (BooruPage page = new(WithSource(harness)))
        {
            page.RestoreSearchTabs(store, new Config());
            Assert.Equal("cat", page.Model.Search.View.Input);
            Assert.True(page.View.SearchTabs.Close(Assert.Single(page.View.SearchTabs.Ids)));
            page.FlushSearchTabs();
            BooruComponentDispatcher.Wait(store.PendingSave);
        }
        using BooruPage reopened = new(WithSource(harness));
        reopened.RestoreSearchTabs(store, new Config());
        Assert.Empty(reopened.View.SearchTabs.Ids);
        Assert.Null(reopened.View.SearchTabs.ActiveId);
        Assert.Empty(reopened.Model.Search.View.Input);
        Assert.Equal(0, harness.ProviderRequests);
        Assert.False(File.Exists(Path.Combine(profile.Path, BooruSearchTabStore.FileName + ".bad")));
    });

    [Theory]
    [InlineData("""{"version":2,"state":{"activeIndex":0,"tabs":[null]}}""", true)]
    [InlineData("""{"Version":1,"State":{"ActiveIndex":0,"Tabs":[{"SourceId":"danbooru","InstanceId":"default","Query":"cat","Page":1}]}}""", true)]
    [InlineData("""{"version":17,"state":{}}""", false)]
    [InlineData("""{"version":2,"state":{"activeIndex":0,"tabs":[{"sourceId":"danbooru","instanceId":"default","postId":null,"query":"cat","page":1}]}}""", true)]
    [InlineData("""{"version":1,"providerId":"other","activeIndex":0,"tabs":[null]}""", false)]
    public async Task UnexaminedOrNewerFilesStayInPlaceEvenAfterAnEdit(string content, bool supported)
    {
        using TempDirectory profile = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        lease.Admission.CompleteStartup();
        string path = Path.Combine(profile.Path, BooruSearchTabStore.FileName);
        await File.WriteAllTextAsync(path, content);
        using BooruWorkbenchModel model = new(new BooruUiHarness().Services);
        BooruSearchTabStore store = new(lease);
        BooruSearchTabs tabs = new(model.Search);
        tabs.Add(copyCurrent: false);
        model.Search.SetInput("cat");
        Assert.False(await store.Save(tabs.CaptureTargets(2, 1)));
        Assert.False(await store.PendingSave);
        BooruSearchTabState? examined = store.Read(migrateLegacy: false);
        if (supported)
        {
            Assert.NotNull(examined);
            tabs.RestoreTargets(examined);
            Assert.Equal(Assert.Single(tabs.Ids), tabs.ActiveId);
        }
        else
        {
            Assert.Null(examined);
            Assert.False(await store.Save(tabs.CaptureTargets(2, 1)));
        }
        Assert.Equal(content, await File.ReadAllTextAsync(path));
        Assert.False(File.Exists(path + ".bad"));
    }

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
