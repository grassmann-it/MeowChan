using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using System.Security.Cryptography;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Desktop;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.MediaActions;
using MeowChan.Desktop.Updates;
using MeowChan.Gallery;
using MeowChan.PluginSdk;
using MeowChan.Storage;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class BooruMd5LookupTests
{
    private const string Base64 = "ASNFZ4mrze8BI0VniavN7w==";
    private const string Query = "md5:0123456789abcdef0123456789abcdef";
    private const string SecondHash = "EAAAAAAAAAAAAAAAAAAAAA==";
    private const string SecondQuery = "md5:10000000000000000000000000000000";

    [Fact]
    public void FirstChoiceAndTabLimitKeepTheGalleryUntilTheMd5SearchCanOpen() => BooruComponentDispatcher.Run(async () =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        using TempDirectory profile = new();
        new Config
        {
            DownloadPath = Path.Combine(profile.Path, "media"), FirstRunSetupVersion = 1, Language = "en",
            ShowBooruTools = true, BooruSearchTabs = 2, RestoreBooruSearchTabs = false
        }.SaveConfig(profile.Path);
        await using DesktopSessionHost session = new(profile.Path, [],
            updateSessionFactory: (path, config) => new UpdateSession(path, config, disableEnvironment: () => "1"));
        FakeBooruSearchPort search = new();
        BooruWorkbenchServices source = new(search, new FakeBooruQueuePort(), new FakeBooruWatcherPort(new BooruManualClock()),
            new FakeBooruPreviewPort()) { Content = session.LocalMediaContent };
        session.BooruServices = source;
        await using MainWindow main = new(session) { Width = 1200, Height = 800 };
        main.Show();
        main.PlatformImpl!.Activated?.Invoke();
        await main.StartupTimingRestoreTask;
        Assert.False(source.Content.HasChoice);

        const string name = "gallery.png";
        string file = Path.Combine(profile.Path, name);
        byte[] png = BooruUiFixtures.Png(42);
        await File.WriteAllBytesAsync(file, png);
        MediaAssetReference asset = new(name, name, "original", name, file, png.Length,
            Convert.ToHexStringLower(SHA256.HashData(png)), "", name, true);
        MediaAssetSnapshot manifest = new(profile.Path, MediaManifestStatus.Available, 1, "fixture", [asset]);
        GalleryEntry entry = new(new(new("fixture", "g", "1"), profile.Path, name), manifest, asset, null, null, false, false);
        ContentControl gallery = main.FindControl<ContentControl>("GalleryHost")!;
        Assert.NotNull(await main.OpenMediaGalleryAsync([entry], entry.EntryKey));
        var origin = main.SelectedPage;
        BooruMd5Lookup lookup = Assert.IsType<BooruMd5Lookup>(MainWindow.Md5LookupFor(main));

        Task request = lookup.SearchAsync(source, Query);
        var choice = Assert.Single(main.OwnedWindows.OfType<BooruContentDialog>());
        Assert.True(gallery.IsVisible);
        Click(ById<Button>(choice, "BooruContentCancel"));
        await request;
        Assert.True(gallery.IsVisible);
        Assert.Equal(origin, main.SelectedPage);
        Assert.Empty(search.Searches);
        Assert.Null(main.BooruPageForTests);

        request = lookup.SearchAsync(source, Query);
        choice = Assert.Single(main.OwnedWindows.OfType<BooruContentDialog>());
        Assert.True(gallery.IsVisible);
        Click(ById<Button>(choice, "BooruContentConfirm"));
        await request;
        Assert.True(source.Content.HasChoice);
        Assert.False(gallery.IsVisible);
        Assert.Equal(MainWindow.WorkspacePage.Booru, main.SelectedPage);
        Assert.Equal((Query, 1), Assert.Single(search.Searches));

        BooruPage page = Assert.IsType<BooruPage>(main.BooruPageForTests);
        Assert.True(page.View.TryShowQueryInNewTab("cat"));
        Assert.NotNull(await main.OpenMediaGalleryAsync([entry], entry.EntryKey));
        request = lookup.SearchAsync(source, SecondQuery);
        Window notice = Assert.Single(main.OwnedWindows, window => AutomationProperties.GetAutomationId(window) == "BooruMd5TabLimit");
        Assert.True(notice.IsVisible);
        Assert.Equal(Texts.T("booru.tabs.limit", 2), ById<TextBlock>(notice, "BooruMd5TabLimit.message").Text);
        Assert.True(gallery.IsVisible);
        Click(ById<Button>(notice, "BooruMd5TabLimit.accept"));
        await request;
        Assert.True(gallery.IsVisible);
        Assert.Single(search.Searches);
    });

    [Fact]
    public void BatchRetryRestoresTheContentStatusAndRelocalizesTheOpenWindow() => BooruComponentDispatcher.Run(async () =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        int attempts = 0;
        BooruMd5Batch batch = new([Entry("first")], (_, _) => Task.FromResult<string?>(Base64),
            new FakeBooruSearchPort(), () => false, () => null, new BooruManualClock(), 20);
        BooruMd5BatchWindow window = new(batch, contentReady: false, enabled: false,
            _ => attempts++ == 0 ? Task.FromException(new IOException("offline")) : Task.CompletedTask, CancellationToken.None);
        string previous = Texts.Language;
        try
        {
            Texts.SetLanguage("en");
            window.Show();
            CheckBox optIn = ById<CheckBox>(window, "booru.md5.batch.optIn");
            TextBlock status = ById<TextBlock>(window, "booru.md5.batch.status");
            await BooruTestSupport.WaitAsync(() => optIn.IsFocused, "the opt-in checkbox to have initial focus");
            Assert.Equal(Texts.T("booru.md5.batch.contentRequired"), status.Text);
            optIn.IsChecked = true;
            Click(optIn);
            Assert.False(optIn.IsChecked);
            Assert.Equal(Texts.T("booru.md5.batch.saveFailed"), status.Text);
            optIn.IsChecked = true;
            Click(optIn);
            Assert.True(optIn.IsChecked);
            Assert.Equal(Texts.T("booru.md5.batch.contentRequired"), status.Text);
            Assert.False(ById<Button>(window, "booru.md5.batch.run").IsEnabled);
            Texts.SetLanguage("de");
            Assert.Equal(Texts.T("booru.md5.batch.contentRequired"), status.Text);
            Assert.Equal(Texts.T("booru.md5.batch.title"), window.Title);
        }
        finally
        {
            window.Close();
            Texts.SetLanguage(previous);
        }
    });

    [Fact]
    public void BatchSelectsTheFirstMatchAndShowsItsTags() => BooruComponentDispatcher.Run(async () =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        FakeBooruSearchPort search = new();
        search.OnSearch = (query, _, _) => Task.FromResult(
            BooruCallResult<BooruResultPage>.Ok(BooruUiFixtures.Page(query, 1, true, WithHash("3", Base64))));
        BooruContentFilter filter = new(1, new(BooruPostClaimRating.General, "", []));
        BooruMd5Batch batch = new([Entry("first")], (_, _) => Task.FromResult<string?>(Base64),
            search, () => true, () => filter, new BooruManualClock(), 20);
        BooruMd5BatchWindow window = new(batch, contentReady: true, enabled: true, _ => Task.CompletedTask, CancellationToken.None);
        try
        {
            window.Show();
            Button start = ById<Button>(window, "booru.md5.batch.run");
            await BooruTestSupport.WaitAsync(() => start.IsFocused, "Start lookup to have initial focus");
            Click(start);
            ListBox results = ById<ListBox>(window, "booru.md5.batch.matches");
            await BooruTestSupport.WaitAsync(() => results.Items.Count == 1, "the first MD5 match");
            Assert.Equal(0, results.SelectedIndex);
            Assert.Contains("/posts/3", ById<TextBox>(window, "booru.md5.batch.details").Text);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void AttachmentMenuOnlySubmitsAnActiveSupportedSourceAndUsesTheExistingTabLimit()
    {
        BooruComponentDispatcher.Run(async () =>
        {
            FakeBooruSearchPort search = new();
            BooruWorkbenchServices danbooru = Services(search);
            BooruWorkbenchServices unsupported = Services(new FakeBooruSearchPort { ProviderId = "e926", ProviderName = "e926" });
            IReadOnlyList<BooruWorkbenchServices> active = [danbooru, unsupported];
            using BooruPage page = new(danbooru);
            page.View.SearchTabsLimit = () => 2;
            BooruMd5Lookup lookup = new(() => active, async (source, query) =>
            {
                if (page.View.TryShowQueryInNewTab(query, source.Search)) await page.View.SubmitSearchAsync();
            });
            ContextMenu Menu()
            {
                CapturedAttachment attachment = new(0, "media", "original", null, ExpectedMd5: Base64);
                ContextMenuBuilder builder = new();
                MediaSourceMenu.AddTo(builder, new(null, null, MediaSourceMenu.Md5(null, attachment)), () => true,
                    _ => Task.CompletedTask, lookup: lookup);
                return Assert.IsType<ContextMenu>(builder.Build());
            }

            Assert.Equal(Query, BooruMd5Lookup.Query(Base64));
            ContextMenu menu = Menu();
            MenuItem parent = FindItem(menu, MediaSourceMenu.Md5Id);
            MenuItem action = Assert.IsType<MenuItem>(Assert.Single(parent.Items));
            Assert.Equal($"{MediaSourceMenu.Md5Id}.danbooru.{search.InstanceId}", AutomationProperties.GetAutomationId(action));
            Assert.Null(action.InputGesture);
            Assert.Empty(search.Searches);

            active = [unsupported];
            Assert.DoesNotContain(Menu().Items.OfType<MenuItem>(), item => AutomationProperties.GetAutomationId(item) == MediaSourceMenu.Md5Id);
            action.Command!.Execute(null);
            Assert.Empty(search.Searches);

            active = [danbooru, unsupported];
            action.Command.Execute(null);
            await BooruTestSupport.WaitAsync(() => search.Requests == 1, "the explicit MD5 request");
            Assert.Equal((Query, 1), Assert.Single(search.Searches));
            Assert.Equal(Query, page.Model.Search.View.Input);
            Assert.False(page.Model.Search.CanCreateWatcher);

            Assert.True(page.View.TryShowQueryInNewTab("cat"));
            action.Command.Execute(null);
            TextBlock notice = Assert.Single(page.View.Header.GetVisualDescendants().OfType<TextBlock>(),
                block => AutomationProperties.GetAutomationId(block) == BooruWorkbenchView.TabLimitNoticeName);
            Assert.Equal(Texts.T("booru.tabs.limit", 2), notice.Text);
            Assert.Single(search.Searches);
        });
    }

    [Fact]
    public void TheMd5IssueIsLocalizedAndTheQueryIsNeverSavedAsOriginalProvenance()
    {
        BooruParsedInput invalid = BooruInput.Parse(BooruInputMode.Tags, Query + " cat");
        Assert.Equal(BooruInputIssue.Md5, invalid.Issue);
        Assert.Contains("32", BooruText.Issue(invalid.Issue), StringComparison.Ordinal);
        Assert.Null(BooruOriginalQueuePort.Provenance(BooruFindingKind.Search, Query));
        Assert.Equal("cat", BooruOriginalQueuePort.Provenance(BooruFindingKind.Search, "cat"));
    }

    [Theory]
    [InlineData("md5:0123456789abcdef0123456789abcde")]
    [InlineData(Query + "0")]
    [InlineData("md5:0123456789abcdef0123456789abcdeF")]
    [InlineData("md5:0123456789abcdef0123456789abcdeg")]
    [InlineData(Query + " cat")]
    public void BadMd5InputIsRejectedAtTheHostQueryBoundary(string query)
    {
        Assert.Equal(BooruInputKind.Invalid, BooruInput.Parse(BooruInputMode.Tags, query).Kind);
        Assert.Equal(Query + " rating:g", BooruInput.Parse(BooruInputMode.Tags, Query + " rating:g").Canonical);
        Assert.Equal("cat -dog rating:g", BooruInput.Parse(BooruInputMode.Tags, "cat -dog rating:g").Canonical);
    }

    [Fact]
    public void ProfileOptInDefaultsOffAndSurvivesTheNormalConfigRoundTrip()
    {
        Config defaults = Assert.IsType<Config>(JsonSerializer.Deserialize("{}", ConfigJsonContext.Default.Config));
        Assert.False(defaults.BooruMd5BatchEnabled);
        defaults.BooruMd5BatchEnabled = true;
        string json = JsonSerializer.Serialize(defaults.Clone(), ConfigJsonContext.Default.Config);
        Config loaded = Assert.IsType<Config>(JsonSerializer.Deserialize(json, ConfigJsonContext.Default.Config));
        Config published = new();
        published.CopyFrom(loaded);
        Assert.True(published.BooruMd5BatchEnabled);
    }

    [Fact]
    public async Task BatchOptInAndCooldownGateEveryRequestAndDuplicateFilesReuseTheMatch()
    {
        BooruManualClock clock = new();
        FakeBooruSearchPort search = new();
        bool enabled = false;
        BooruContentFilter content = new(1, new(BooruPostClaimRating.General, "", []));
        BooruMd5Batch batch = new([Entry("first"), Entry("duplicate"), Entry("second"), Entry("unreadable")],
            (entry, _) => Task.FromResult(entry.Name switch { "second" => SecondHash, "unreadable" => null, _ => Base64 }),
            search, () => enabled, () => content, clock, 20);
        List<BooruMd5Match> matches = [];
        CancellationToken token = CancellationToken.None;
        Assert.Equal(BooruCallStatus.Unavailable, await batch.RunAsync(matches.AddRange, token));
        Assert.Empty(search.Searches);

        enabled = true;
        DateTimeOffset retryAt = clock.GetUtcNow().AddMinutes(1);
        search.OnSearch = (query, _, _) => Task.FromResult(query == Query
            ? BooruCallResult<BooruResultPage>.Ok(BooruUiFixtures.Page(query, 1, true,
                WithHash("3", Base64), WithHash("2", Base64, BooruRating.Explicit), WithHash("1", SecondHash)))
            : BooruCallResult<BooruResultPage>.Of(BooruCallStatus.RateLimited, retryAt: retryAt));
        Assert.Equal(BooruCallStatus.RateLimited, await batch.RunAsync(matches.AddRange, token));
        Assert.Equal(2, batch.Completed);
        Assert.Equal(new[] { "first", "duplicate" }, matches.Select(match => match.Name));
        Assert.All(matches, match => Assert.Equal("3", match.Post.PostId));
        Assert.Equal(BooruCallStatus.Cooldown, await batch.RunAsync(matches.AddRange, token));
        Assert.Equal(new[] { (Query, 1), (SecondQuery, 1) }, search.Searches);

        clock.Advance(TimeSpan.FromMinutes(1));
        search.OnSearch = null;
        Assert.Equal(BooruCallStatus.Ok, await batch.RunAsync(matches.AddRange, token));
        Assert.Equal(new[] { (Query, 1), (SecondQuery, 1), (SecondQuery, 1) }, search.Searches);
        Assert.Equal(4, batch.Completed);
        Assert.Equal(1, batch.MissingHash);
    }

    [Fact]
    public async Task CancellationKeepsTheInterruptedFilePendingForResume()
    {
        CancellationToken token = CancellationToken.None;
        using CancellationTokenSource cancel = new();
        FakeBooruSearchPort search = new();
        BooruContentFilter filter = new(1, new(BooruPostClaimRating.General, "", []));
        BooruMd5Batch batch = new([Entry("first"), Entry("second")],
            (entry, _) => Task.FromResult<string?>(entry.Name == "first" ? Base64 : SecondHash),
            search, () => true, () => filter, new BooruManualClock(), 20);
        search.OnSearch = (query, _, current) =>
        {
            if (query == SecondQuery) cancel.Cancel();
            current.ThrowIfCancellationRequested();
            return Task.FromResult(BooruCallResult<BooruResultPage>.Ok(BooruUiFixtures.Page(query, 1, true)));
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => batch.RunAsync(_ => { }, cancel.Token));
        Assert.Equal(1, batch.Completed);
        search.OnSearch = null;
        Assert.Equal(BooruCallStatus.Ok, await batch.RunAsync(_ => { }, token));
        Assert.Equal(2, batch.Completed);
        Assert.Equal(new[] { (Query, 1), (SecondQuery, 1), (SecondQuery, 1) }, search.Searches);
    }

    private static MenuItem FindItem(ItemsControl menu, string id) => Assert.Single(menu.Items.OfType<MenuItem>(),
        item => AutomationProperties.GetAutomationId(item) == id);

    private static T ById<T>(Control root, string id) where T : Control => SidePanelDesignTests.ById<T>(root, id);

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static BooruWorkbenchServices Services(IBooruSearchPort search) =>
        new(search, new FakeBooruQueuePort(), new FakeBooruWatcherPort(new BooruManualClock()), new FakeBooruPreviewPort());

    private static BooruPostView WithHash(string id, string hash, BooruRating rating = BooruRating.General)
    {
        BooruPost post = BooruUiFixtures.Post(id, rating: rating, preview: false).Post;
        MeowChan.PluginSdk.BooruMediaVariant original = Assert.Single(post.Media!);
        return BooruPostView.From(post with { Media = [original with { Extension = ".png", ExpectedMd5 = hash }] });
    }

    private static MediaLibraryEntry Entry(string name) => new(new ResourceId("test", "board", "1"), "thread", 1,
        DateTimeOffset.UnixEpoch, null, null, null, null, null, null, null, name, null, 1, name, "original", name,
        SavedMediaKind.Image, MediaLibraryPresence.Present, false, false);
}
