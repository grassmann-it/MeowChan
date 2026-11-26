using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;
using MeowChan.PluginSdk;
using MeowChan.Tests.Shared;
using Xunit.Abstractions;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class BooruSearchTabPersistenceTests(ITestOutputHelper output)
{
    private static SourceDescription Source => DanbooruTestPlugin.Plugin.Describe().Sources.Single();

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

    [Fact]
    public void ReenablingRestoreSavesTheLiveSessionOverThePreviousV1File() => BooruComponentDispatcher.Run(() =>
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
    public async Task DamagedFilesAreQuarantinedAndTheNextSaveWorks(string contents)
    {
        using TempDirectory profile = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        lease.Admission.CompleteStartup();
        string path = Path.Combine(profile.Path, BooruSearchTabStore.FileName);
        await File.WriteAllTextAsync(path, contents);
        using BooruWorkbenchModel model = new(new BooruUiHarness().Services);
        BooruSearchTabStore store = new(lease);
        Assert.Null(store.Load(model.Search.View, 1, Source));
        Assert.False(File.Exists(path));
        Assert.Equal(contents, await File.ReadAllTextAsync(path + ".bad"));

        BooruSearchTabs tabs = new(model.Search);
        tabs.Add(copyCurrent: false);
        model.Search.SetInput("safe_draft");
        store.Save(tabs.Capture(2), 1);
        await store.PendingSave;
        BooruTabSnapshot loaded = Assert.IsType<BooruTabSnapshot>(store.Load(model.Search.View, 1, Source));
        Assert.Equal("safe_draft", Assert.Single(loaded.Tabs)!.Input);
        Assert.Equal(contents, await File.ReadAllTextAsync(path + ".bad"));
        Assert.Empty(Directory.GetFiles(profile.Path, ".*.tmp"));
    }

    [Theory]
    [InlineData("empty", 0, null)]
    [InlineData("draft", 0, "start_here")]
    [InlineData("full", Config.BooruSavedTabsMaximum, null)]
    public async Task WriterProducesMigrationFiles(string fixture, int count, string? startDraft)
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
        store.Save(tabs.Capture(Config.BooruSavedTabsMaximum), 2);
        await store.PendingSave;
        string path = Path.Combine(profile.Path, BooruSearchTabStore.FileName);
        byte[] bytes = await File.ReadAllBytesAsync(path);
        using JsonDocument document = JsonDocument.Parse(bytes);
        Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
        Assert.True(document.RootElement.GetProperty("allTabsClosable").GetBoolean());
        Assert.Equal(Math.Max(1, count), document.RootElement.GetProperty("tabs").GetArrayLength());
        Assert.Single(Directory.GetFiles(profile.Path, "booru-search-tabs*.json"));

        BooruTabSnapshot restored = Assert.IsType<BooruTabSnapshot>(new BooruSearchTabStore(lease).Load(search.View, 2, Source));
        Assert.Equal(active, restored.ActiveIndex);
        Assert.Equal(inputs, restored.Tabs.Select(tab => tab!.Input));
        Assert.Equal(startDraft, restored.Draft?.Input);
        if (active >= 0)
        {
            BooruSearchView result = Assert.IsType<BooruSearchView>(restored.Tabs[active]);
            Assert.Equal("draft_17", result.ExecutedQuery);
            Assert.Equal(2, result.Page);
            Assert.Equal(new[] { "101", "102" }, result.Rows.Select(row => row.Post.PostId));
        }

        // The TRX carries the exact bytes to the release migration check after this profile is disposed.
        output.WriteLine($"BOORU112_WRITER {fixture} {Convert.ToBase64String(bytes)}");
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
        BooruTabSnapshot loaded = Assert.IsType<BooruTabSnapshot>(store.Load(model.Search.View, 20, Source));
        BooruSearchView tab = Assert.IsType<BooruSearchView>(Assert.Single(loaded.Tabs));
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
        store.Save(tabs.Capture(2), 2);
        await store.PendingSave;

        string path = Path.Combine(profile.Path, BooruSearchTabStore.FileName);
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        JsonElement saved = document.RootElement.GetProperty("tabs");
        Assert.Equal(2, saved.GetArrayLength());
        Assert.Equal(2, saved[1].GetProperty("rows").GetArrayLength());
        BooruTabSnapshot loaded = Assert.IsType<BooruTabSnapshot>(store.Load(search.View, 1, Source));
        Assert.Collection(loaded.Tabs, tab => Assert.Equal("first", tab!.Input), tab => Assert.Equal("active", tab!.Input));
        Assert.Equal(1, loaded.ActiveIndex);
        Assert.True(loaded.Tabs[1]!.HasNextPage);
        Assert.Equal("203", Assert.Single(loaded.Tabs[1]!.Rows).Post.PostId);

        BooruTabSnapshot drafts = Assert.IsType<BooruTabSnapshot>(store.Load(search.View, 0, Source));
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
            BooruTabSnapshot snapshot = Assert.IsType<BooruTabSnapshot>(store.Load(page.Model.Search.View, 1, Source));
            Assert.Empty(snapshot.Tabs);
            Assert.Equal("latest", snapshot.Draft!.Input);
            page.FlushSearchTabs();
            BooruComponentDispatcher.Wait(store.PendingSave);
        }

        using BooruPage optedOut = new(new BooruUiHarness().Services);
        optedOut.RestoreSearchTabs(store, new Config { RestoreBooruSearchTabs = false });
        Assert.Empty(optedOut.Model.Search.View.Input);
        optedOut.Model.Search.SetInput("private_draft");
        optedOut.FlushSearchTabs();
        BooruComponentDispatcher.Wait(store.PendingSave);
        BooruTabSnapshot previous = Assert.IsType<BooruTabSnapshot>(store.Load(optedOut.Model.Search.View, 1, Source));
        Assert.Empty(previous.Tabs);
        Assert.Equal("latest", previous.Draft!.Input);
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
            Assert.Equal(original, File.ReadAllText(path));
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
    [InlineData("""{"version":2,"state":{"activeIndex":0,"tabs":[{"sourceId":"danbooru","instanceId":"default","query":"cat","page":1}]}}""")]
    [InlineData("""{"Version":1,"State":{"ActiveIndex":0,"Tabs":[{"SourceId":"danbooru","InstanceId":"default","Query":"cat","Page":1}]}}""")]
    [InlineData("""{"version":17,"state":{}}""")]
    [InlineData("""{"version":1,"providerId":"other","activeIndex":0,"tabs":[null]}""")]
    public async Task NewerAndScopedFormatsStayInPlaceEvenAfterAnEdit(string content)
    {
        using TempDirectory profile = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        lease.Admission.CompleteStartup();
        string path = Path.Combine(profile.Path, BooruSearchTabStore.FileName);
        await File.WriteAllTextAsync(path, content);
        using BooruWorkbenchModel model = new(new BooruUiHarness().Services);
        BooruSearchTabStore store = new(lease);
        Assert.Null(store.Load(model.Search.View, 1, Source));
        BooruSearchTabs tabs = new(model.Search);
        tabs.Add(copyCurrent: false);
        model.Search.SetInput("cat");
        store.Save(tabs.Capture(2), 1);
        await store.PendingSave;
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
