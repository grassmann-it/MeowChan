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
using MeowChan.Favorites;
using MeowChan.Gallery;
using MeowChan.Persistence;
using MeowChan.PluginSdk;
using MeowChan.Storage;
using MeowChan.Tags;
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
    public void ConfirmingTheFirstChoiceAtTheTabLimitKeepsTheAllowedGalleryImageAndDropsBlockedTagTargets() => BooruComponentDispatcher.Run(async () =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        using TempDirectory profile = new();
        new Config
        {
            DownloadPath = Path.Combine(profile.Path, "media"), FirstRunSetupVersion = 1, Language = "en",
            ShowBooruTools = true, BooruSearchTabs = 2, BooruSavedTabs = 2, RestoreBooruSearchTabs = true
        }.SaveConfig(profile.Path);
        FakeBooruSearchPort search = new();
        BooruPostKey general = new(search.ProviderId, search.InstanceId, "101");
        BooruPostKey explicitPost = new(search.ProviderId, search.InstanceId, "102");
        GalleryEntry allowed = await SaveEntryAsync(profile.Path, general, 41);
        GalleryEntry blocked = await SaveEntryAsync(profile.Path, explicitPost, 42);
        FavoriteAssetKey allowedRating = FavoriteAssetKey.From(allowed.Key.Thread, allowed.Asset);
        FavoriteAssetKey blockedRating = FavoriteAssetKey.From(blocked.Key.Thread, blocked.Asset);
        using (ProfileLease lease = ProfileLease.Acquire(profile.Path))
        {
            lease.Admission.CompleteStartup();
            BooruCatalogStore catalog = new(profile.Path, lease);
            catalog.UpsertPost(new(general.SourceId, general.PostId, BooruPostClaimPresence.Rating,
                Rating: BooruPostClaimRating.General, RawRating: "g", InstanceId: general.InstanceId), DateTimeOffset.UnixEpoch);
            catalog.UpsertPost(new(explicitPost.SourceId, explicitPost.PostId, BooruPostClaimPresence.Rating,
                Rating: BooruPostClaimRating.Explicit, RawRating: "e", InstanceId: explicitPost.InstanceId), DateTimeOffset.UnixEpoch);
            catalog.LinkLocalAsset(general.SourceId, general.PostId, allowed.Asset.AssetId, allowed.Asset.Path);
            catalog.LinkLocalAsset(explicitPost.SourceId, explicitPost.PostId, blocked.Asset.AssetId, blocked.Asset.Path);
            new BooruSearchTabStore(lease).Write(new(0,
                [new(search.ProviderId, search.InstanceId, null, "cat", Empty: true),
                 new(search.ProviderId, search.InstanceId, null, "dog", Empty: true)]));
        }
        await using DesktopSessionHost session = new(profile.Path, [],
            updateSessionFactory: (path, config) => new UpdateSession(path, config, disableEnvironment: () => "1"));
        BooruWorkbenchServices source = new(search, new FakeBooruQueuePort(), new FakeBooruWatcherPort(new BooruManualClock()),
            new FakeBooruPreviewPort()) { Content = session.LocalMediaContent };
        session.BooruServices = source;
        await using MainWindow main = new(session) { Width = 1200, Height = 800 };
        main.Show();
        main.PlatformImpl!.Activated?.Invoke();
        await main.StartupTimingRestoreTask;
        Click(ById<Button>(main, "MediaNavigationButton"));
        Assert.Equal(MainWindow.WorkspacePage.Media, main.SelectedPage);
        Assert.False(source.Content.HasChoice);
        Assert.NotNull(await main.OpenMediaGalleryAsync([blocked, allowed], blocked.EntryKey));
        ContentControl gallery = ById<ContentControl>(main, "GalleryHost");
        GalleryViewer viewer = Assert.IsType<GalleryViewer>(gallery.Content);
        Assert.Equal(2, viewer.Filmstrip.Count);
        Assert.Equal(blocked.EntryKey, viewer.Filmstrip.EntryAt(0)!.EntryKey);
        Assert.Equal(0, viewer.Filmstrip.CurrentIndex);
        Assert.True(viewer.TagsButton.IsEnabled);
        Click(viewer.ModeTagButton);
        Assert.True(viewer.IsTagMode);
        QuickTagSurface surface = Assert.IsType<QuickTagSurface>(viewer.TagSurface);
        GalleryTagPanelController controller = Assert.IsType<GalleryTagPanelController>(viewer.TagPanelController);
        Assert.True(surface.Execute(new QuickTagKeyRoute(QuickTagKeyAction.Next)));
        await BooruTestSupport.WaitAsync(() => controller.Entry.EntryKey == allowed.EntryKey, "the panel to show the allowed image");
        await surface.Session.AddAsync("before_filter");
        Assert.Single(surface.Session.UndoSteps);
        Assert.True(Assert.IsType<GalleryDecisionRow>(viewer.DecisionRow).RequestRating(3));
        await BooruTestSupport.WaitAsync(() => controller.Decisions?.UndoSteps.Count == 1 && controller.Decisions.CanUndo,
            "the rating of the allowed image to be saved");
        Assert.Equal(3, session.GetMediaRating(allowedRating));
        Assert.True(surface.Execute(new QuickTagKeyRoute(QuickTagKeyAction.Previous)));
        await BooruTestSupport.WaitAsync(() => controller.Entry.EntryKey == blocked.EntryKey, "the panel to return to the image to mark");
        Assert.True(surface.Execute(new QuickTagKeyRoute(QuickTagKeyAction.MarkNext)));
        await BooruTestSupport.WaitAsync(() => viewer.TagPanelController?.Entry.EntryKey == allowed.EntryKey, "the tag panel to follow the allowed image");
        Assert.Equal(1, viewer.Filmstrip.CurrentIndex);
        Assert.Equal(1, surface.MarkedCount);
        Assert.True(surface.Session.IsSelection);
        Assert.Equal(blocked.EntryKey, Assert.Single(controller.DecisionTargets()).EntryKey);
        await surface.Session.AddAsync("blocked_before_filter");
        await surface.Session.RemoveAsync(surface.Session.UndoSteps[^1].Tag);
        Assert.Equal(3, surface.Session.UndoSteps.Count);
        Assert.True(Assert.IsType<GalleryDecisionRow>(viewer.DecisionRow).RequestRating(2));
        await BooruTestSupport.WaitAsync(() => controller.Decisions?.UndoSteps.Count == 2 && controller.Decisions.CanUndo,
            "the rating of the marked image to be saved");
        Assert.Equal(2, session.GetMediaRating(blockedRating));
        main.UpdateLayout();
        Assert.True(surface.Execute(new QuickTagKeyRoute(QuickTagKeyAction.ThreadSection)));
        GalleryThreadTagSection threadSection = ThreadSection(surface);
        Assert.True(threadSection.IsVisible);
        Assert.Equal(allowed.Key.Thread, threadSection.Model?.Thread);
        threadSection.Input.Text = "thread draft";
        surface.Input.Text = "media draft";
        int generation = viewer.MediaRequestGeneration;

        BooruMd5Lookup lookup = Assert.IsType<BooruMd5Lookup>(MainWindow.Md5LookupFor(main));
        Task request = lookup.SearchAsync(source, Query);
        var choice = Assert.Single(main.OwnedWindows.OfType<BooruContentDialog>());
        ById<ComboBox>(choice, "BooruContentMaximum").SelectedIndex = 1;
        Click(ById<Button>(choice, "BooruContentConfirm"));
        await BooruTestSupport.WaitAsync(() => main.OwnedWindows.Any(IsTabLimitNotice), "the MD5 tab limit notice");
        Window notice = Assert.Single(main.OwnedWindows, IsTabLimitNotice);
        Assert.True(notice.IsVisible);
        Assert.Equal(Texts.T("booru.tabs.limit", 2), ById<TextBlock>(notice, "BooruMd5TabLimit.message").Text);
        Assert.True(gallery.IsEffectivelyVisible);
        Click(ById<Button>(notice, "BooruMd5TabLimit.accept"));
        await request;

        Assert.Equal(BooruPostClaimRating.Sensitive, source.Content.Snapshot.Settings.MaximumRating);
        Assert.Equal(MainWindow.WorkspacePage.Media, main.SelectedPage);
        Assert.True(gallery.IsEffectivelyVisible);
        Assert.Same(viewer, gallery.Content);
        Assert.Equal(allowed.EntryKey, viewer.CurrentEntry?.EntryKey);
        Assert.Equal(generation, viewer.MediaRequestGeneration);
        Assert.Equal(1, viewer.Filmstrip.Count);
        Assert.Equal(0, viewer.Filmstrip.CurrentIndex);
        Assert.Equal(allowed.EntryKey, viewer.Filmstrip.EntryAt(0)!.EntryKey);
        Assert.Equal(2, Assert.IsType<BooruPage>(main.BooruPageForTests).View.SearchTabs.Ids.Count);
        Assert.Empty(search.Searches);

        Assert.Empty(Assert.IsType<GalleryMarks>(viewer.Marks).Snapshot());
        QuickTagSurface filteredSurface = Assert.IsType<QuickTagSurface>(viewer.TagSurface);
        GalleryTagPanelController filteredController = Assert.IsType<GalleryTagPanelController>(viewer.TagPanelController);
        Assert.True(viewer.IsTagMode);
        Assert.Equal("media draft", filteredSurface.Input.Text);
        main.UpdateLayout();
        GalleryThreadTagSection filteredThread = ThreadSection(filteredSurface);
        Assert.True(filteredThread.IsVisible);
        Assert.True(filteredThread.IsEffectivelyVisible);
        Assert.Equal(allowed.Key.Thread, filteredThread.Model?.Thread);
        Assert.Equal("thread draft", filteredThread.Input.Text);
        Assert.Equal(0, filteredSurface.MarkedCount);
        Assert.False(filteredSurface.Session.IsSelection);
        Assert.Equal(allowed.EntryKey, Assert.Single(filteredController.DecisionTargets()).EntryKey);
        Assert.Single(filteredSurface.Session.UndoSteps);
        Assert.Equal(allowed.EntryKey, Assert.Single(Assert.Single(filteredController.Decisions!.UndoSteps).Changes).Entry.EntryKey);
        await filteredSurface.UndoAsync();
        Assert.Null(session.GetMediaRating(allowedRating));
        Assert.Equal(2, session.GetMediaRating(blockedRating));
        await filteredSurface.UndoAsync();
        Assert.Empty(filteredSurface.Session.UndoSteps);
        PrivateTagStoreSnapshot afterUndo = Assert.IsType<PrivateTagStoreSnapshot>(session.GetPrivateTagSnapshot().Snapshot);
        Assert.Empty(afterUndo.TagsOf(MediaTagSubjects.Of(allowed)!));

        Assert.True(Assert.IsType<GalleryDecisionRow>(viewer.DecisionRow).RequestRating(4));
        await BooruTestSupport.WaitAsync(() => session.GetMediaRating(allowedRating) == 4, "the rating of the allowed image to be saved");
        Assert.Equal(2, session.GetMediaRating(blockedRating));
        await filteredSurface.Session.AddAsync("after_filter");
        Assert.Single(filteredSurface.Session.UndoSteps);
        PrivateTagStoreSnapshot tags = Assert.IsType<PrivateTagStoreSnapshot>(session.GetPrivateTagSnapshot().Snapshot);
        Assert.Equal("after_filter", Assert.Single(tags.TagsOf(MediaTagSubjects.Of(allowed)!)).Name.Display);
        Assert.Empty(tags.TagsOf(MediaTagSubjects.Of(blocked)!));
        viewer.CloseTagPanel();
        Assert.False(viewer.IsTagPanelOpen);
        Assert.False(viewer.IsTagMode);

        static bool IsTabLimitNotice(Window window) => AutomationProperties.GetAutomationId(window) == "BooruMd5TabLimit";

        static GalleryThreadTagSection ThreadSection(QuickTagSurface tags) =>
            Assert.Single(Assert.IsType<StackPanel>(tags.ExtraContent).Children.OfType<GalleryThreadTagSection>());
    });

    [Fact]
    public void FilteringAnActiveGroupWalkRetiresItsPanelAndRestoresTheProfileStamp() => BooruComponentDispatcher.Run(async () =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        using TempDirectory profile = new();
        new Config
        {
            DownloadPath = Path.Combine(profile.Path, "media"), FirstRunSetupVersion = 1, Language = "en",
            ShowBooruTools = true, TagStamp = ["profile_stamp"]
        }.SaveConfig(profile.Path);
        FakeBooruSearchPort search = new();
        BooruPostKey general = new(search.ProviderId, search.InstanceId, "103");
        BooruPostKey explicitPost = new(search.ProviderId, search.InstanceId, "104");
        GalleryEntry allowed = await SaveEntryAsync(profile.Path, general, 43);
        GalleryEntry blocked = await SaveEntryAsync(profile.Path, explicitPost, 44);
        using (ProfileLease lease = ProfileLease.Acquire(profile.Path))
        {
            lease.Admission.CompleteStartup();
            BooruCatalogStore catalog = new(profile.Path, lease);
            SaveClaim(general, allowed, BooruPostClaimRating.General);
            SaveClaim(explicitPost, blocked, BooruPostClaimRating.Explicit);

            void SaveClaim(BooruPostKey post, GalleryEntry entry, BooruPostClaimRating rating)
            {
                catalog.UpsertPost(new(post.SourceId, post.PostId, BooruPostClaimPresence.Rating,
                    Rating: rating, InstanceId: post.InstanceId), DateTimeOffset.UnixEpoch);
                catalog.LinkLocalAsset(post.SourceId, post.PostId, entry.Asset.AssetId, entry.Asset.Path);
            }
        }
        await using DesktopSessionHost session = new(profile.Path, [],
            updateSessionFactory: (path, config) => new UpdateSession(path, config, disableEnvironment: () => "1"));
        session.BooruServices = new(search, new FakeBooruQueuePort(), new FakeBooruWatcherPort(new BooruManualClock()),
            new FakeBooruPreviewPort()) { Content = session.LocalMediaContent };
        await using MainWindow main = new(session) { Width = 1200, Height = 800 };
        main.Show();
        main.PlatformImpl!.Activated?.Invoke();
        await main.StartupTimingRestoreTask;
        Click(ById<Button>(main, "MediaNavigationButton")); // subscribes the shell to content filter changes
        await session.DefinePrivateTagAsync("profile_stamp");
        await session.DefinePrivateTagAsync("group_stamp");

        TagQueueItem[] items = [Assert.IsType<TagQueueItem>(TagQueueItem.From(allowed, DateTimeOffset.UnixEpoch)),
            Assert.IsType<TagQueueItem>(TagQueueItem.From(blocked, DateTimeOffset.UnixEpoch))];
        await session.EnqueueTagQueueAsync(items);
        TagQueueGroup group = Assert.Single((await session.CreateTagQueueGroupAsync("Fixture group", items.Select(item => item.Key).ToArray())).Groups);
        TagQueueSnapshot queue = await session.SetTagQueueGroupStampAsync(group.Id, ["group_stamp"]);
        await main.OpenTaggingWalkAsync(queue, queue.Items, [allowed, blocked], allowed.EntryKey, group.Name, group.Id);
        GalleryViewer viewer = Assert.IsType<GalleryViewer>(ById<ContentControl>(main, "GalleryHost").Content);
        Assert.True(viewer.IsTaggingMode);
        Assert.Equal(2, viewer.Filmstrip.Count);
        QuickTagSurface walkingPanel = Assert.IsType<QuickTagSurface>(viewer.TagSurface);
        GalleryDecisionSession walkingDecisions = Assert.IsType<GalleryDecisionSession>(viewer.TagPanelController?.Decisions);
        await walkingDecisions.SetRatingAsync([allowed], 3);
        Assert.Single(walkingDecisions.UndoSteps);
        Assert.Equal("group_stamp", Assert.Single(walkingPanel.Session.Stamp.ToNames()));
        Assert.NotSame(main.QuickTagStampSet, walkingPanel.Session.Stamp);

        await session.SaveBooruContentAsync(new(BooruPostClaimRating.Sensitive, "", []), session.LocalMediaContent.Snapshot.Revision);
        await BooruTestSupport.WaitAsync(() => viewer.Filmstrip.Count == 1, "the content filter to remove the blocked image");

        Assert.Null(main.GalleryTagging);
        Assert.False(viewer.IsTaggingMode);
        Assert.Equal(allowed.EntryKey, viewer.CurrentEntry?.EntryKey);
        QuickTagSurface refreshedPanel = Assert.IsType<QuickTagSurface>(viewer.TagSurface);
        Assert.NotSame(walkingPanel.Session, refreshedPanel.Session);
        Assert.Same(main.QuickTagStampSet, refreshedPanel.Session.Stamp);
        Assert.Empty(Assert.IsType<GalleryDecisionSession>(viewer.TagPanelController?.Decisions).UndoSteps);
        Assert.Equal(GalleryDecisionStatusKind.None, (await walkingDecisions.UndoAsync()).Kind);
        Assert.True((await refreshedPanel.ApplyStampAsync()).Complete);
        PrivateTagStoreSnapshot tags = Assert.IsType<PrivateTagStoreSnapshot>(session.GetPrivateTagSnapshot().Snapshot);
        Assert.Equal("profile_stamp", Assert.Single(tags.TagsOf(MediaTagSubjects.Of(allowed)!)).Name.Display);
        Assert.Equal("group_stamp", Assert.Single((await session.LoadTagQueueAsync()).Group(group.Id)!.Stamp));
    });

    private static async Task<GalleryEntry> SaveEntryAsync(string profile, BooruPostKey post, int marker)
    {
        string name = post.PostId + ".png";
        string directory = Path.Combine(profile, "media", post.PostId);
        Directory.CreateDirectory(directory);
        byte[] png = BooruUiFixtures.Png(marker);
        await File.WriteAllBytesAsync(Path.Combine(directory, name), png);
        await File.WriteAllTextAsync(Path.Combine(directory, ".meowchan-manifest.json"), JsonSerializer.Serialize(new
        {
            Version = 1,
            Entries = new Dictionary<string, object>
            {
                [name] = new
                {
                    MediaIdentifier = BooruMediaKey.Original(post).StorageId, Variant = "original", FileName = name,
                    SourceUrl = "https://offline.invalid/" + name, OriginalFileName = name, Length = png.Length,
                    Sha256 = Convert.ToHexStringLower(SHA256.HashData(png)), Md5 = Convert.ToBase64String(MD5.HashData(png))
                }
            }
        }));
        MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(directory);
        MediaAssetReference asset = Assert.Single(manifest.GetPage());
        return new GalleryEntry(new(BooruMediaIdentity.Thread(post), directory, asset.AssetId), manifest, asset, null, null, false, false)
        { LocalTagEligible = true };
    }

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
    [InlineData("md5:0123456789abcdef0123456789abcdeg")]
    [InlineData(Query + " cat")]
    public void BadMd5InputIsRejectedAtTheHostQueryBoundary(string query)
    {
        Assert.Equal(BooruInputKind.Invalid, BooruInput.Parse(BooruInputMode.Tags, query).Kind);
        Assert.Equal(Query + " rating:g", BooruInput.Parse(BooruInputMode.Tags, Query + " rating:g").Canonical);
        Assert.Equal("cat -dog rating:g", BooruInput.Parse(BooruInputMode.Tags, "cat -dog rating:g").Canonical);
    }

    [Fact]
    public void UppercaseMd5InputIsAcceptedAndCanonicalizedToLowercase()
    {
        BooruParsedInput parsed = BooruInput.Parse(BooruInputMode.Tags, "md5:0123456789ABCDEF0123456789abcdeF");
        Assert.NotEqual(BooruInputKind.Invalid, parsed.Kind);
        Assert.Equal(Query, parsed.Canonical);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeletingTheRulesDisablesANewBatchOrBlocksTheInFlightResult(bool removeWhileRunning) =>
        BooruComponentDispatcher.Run(async () =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            using TempDirectory profile = new();
            new Config
            {
                DownloadPath = Path.Combine(profile.Path, "media"), FirstRunSetupVersion = 1, WorkspaceMode = "Advanced",
                ShowBooruTools = true, AutomaticUpdateChecks = false, BooruMd5BatchEnabled = true
            }.SaveConfig(profile.Path);
            string path = Path.Combine(profile.Path, BooruContentProfileStore.FileName);
            const string initial = """{"schemaVersion":1,"revision":1,"settings":{"maximumRating":"explicit","globalBlacklist":"","sourceBlacklists":[]}}""";
            await File.WriteAllTextAsync(path, initial);
            await using DesktopSessionHost session = new(profile.Path, []);
            BooruWorkspaceContent content = BooruWorkspaceContent.ForProfile(session.ProfileLeaseForTests);
            BooruUiHarness harness = new();
            BooruWorkbenchServices ports = harness.Services;
            session.BooruServices = new(ports.Search, ports.Queue, ports.Watchers, ports.Previews) { Content = content };
            TaskCompletionSource<BooruCallResult<BooruResultPage>> providerResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
            harness.Search.OnSearch = (_, _, token) => providerResult.Task.WaitAsync(token);
            await using MainWindow window = new(session) { Width = 1440, Height = 800 };
            window.Show();
            window.PlatformImpl!.Activated?.Invoke();
            await window.StartupTimingRestoreTask;
            Assert.True(content.HasChoice);

            if (!removeWhileRunning) File.Delete(path);
            Task batch = window.OpenMd5BatchAsync([Entry("first")], (_, _) => Task.FromResult<string?>(Base64), CancellationToken.None);
            BooruMd5BatchWindow dialog = Assert.Single(window.OwnedWindows.OfType<BooruMd5BatchWindow>());
            try
            {
                ListBox results = Find<ListBox>("matches");
                Button run = Find<Button>("run");
                Assert.Equal(removeWhileRunning, run.IsEnabled);
                run.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (removeWhileRunning)
                {
                    await BooruTestSupport.WaitAsync(() => harness.Search.Searches.Count == 1, "the MD5 provider request");
                    File.Delete(path);
                    providerResult.SetResult(BooruCallResult<BooruResultPage>.Ok(
                        BooruUiFixtures.Page(Query, 1, true, WithHash("1", Base64, BooruRating.Explicit))));
                    await BooruTestSupport.WaitAsync(() => !Find<Button>("stop").IsEnabled, "the batch to stop after rechecking the rules");
                    Assert.Single(harness.Search.Searches);
                }
                else
                {
                    Assert.Empty(harness.Search.Searches);
                }
                Assert.Empty(results.Items);
                Assert.Equal(BooruContentProfileState.Missing, session.ReadBooruMd5ContentProfile().State);
                Assert.True(content.HasChoice);
            }
            finally
            {
                dialog.Close();
                await batch;
            }

            T Find<T>(string id) where T : Control => Assert.Single(dialog.GetVisualDescendants().OfType<T>(),
                control => AutomationProperties.GetAutomationId(control) == "booru.md5.batch." + id);
        });

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
