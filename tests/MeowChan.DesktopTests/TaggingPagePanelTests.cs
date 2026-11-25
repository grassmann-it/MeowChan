using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Motion;
using MeowChan.Desktop.Updates;
using MeowChan.Favorites;
using MeowChan.Gallery;
using MeowChan.Storage;
using MeowChan.Tags;
using SkiaSharp;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class TaggingPagePanelTests
{
    private const string PanelId = "TaggingPanel";
    private const string PictureId = "ReaderMediaSidebarPicture";
    private const string EditId = "ReaderMediaSidebarEditTags";
    private const string CloseId = "ReaderMediaSidebarClose";
    private const string ToggleId = "TaggingPanelToggle";
    private const string HandleId = "TaggingPanelHandle";
    private const string MetaId = "ReaderMediaSidebarMeta";
    private const string FavoriteId = "ReaderMediaSidebarFavorite";
    private const string ThirdStarId = "ReaderMediaSidebarDecisionRating3";
    private static readonly ResourceId ThreadId = new("4chan", "g", "123");
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DownArrowShowsTheNextMediumInThePanel(bool grid) => WithPage(grid, async fixture =>
    {
        await fixture.ShowsAsync(1);
        fixture.ClickEntry(1);
        fixture.Key(PhysicalKey.ArrowDown);
        int next = fixture.FocusedNumber();
        Assert.True(next > 1);
        await fixture.ShowsAsync(next);
        fixture.Page.SetViewMode(grid ? TaggingViewMode.List : TaggingViewMode.Grid, save: false);
        await fixture.ShowsAsync(next);
    });

    [Fact]
    public void TheSidebarStartsAtTheNextOpenEntryWithoutASelection() => WithPage(false, async fixture =>
    {
        Assert.Empty(fixture.List.SelectedItems!);
        await fixture.ShowsAsync(2);
    }, skipFirst: true);

    [Fact]
    public void AnEntryWithoutAPostShowsItsFilenameAndPosition() => WithPage(false, async fixture =>
    {
        fixture.ClickEntry(1);
        fixture.Key(PhysicalKey.End);
        Assert.Equal(12, fixture.FocusedNumber());
        await fixture.ShowsAsync(12);
        Assert.DoesNotContain("No.", fixture.PanelText("ReaderMediaSidebarHeading"));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RapidNavigationKeepsTheLatestPictureAfterAnOldDecodeCompletes(bool grid)
    {
        ManualClock clock = new();
        List<(string File, long Time)> decodes = [];
        TaskCompletionSource firstDecoded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<Bitmap> firstBitmap = new(TaskCreationOptions.RunContinuationsAsynchronously);
        WithPage(grid, async fixture =>
        {
            try
            {
                await fixture.ShowsAsync(1);
                await fixture.WaitAsync(() => fixture.Picture.Source != null, "initial image");
                fixture.ClickEntry(1);
                fixture.Key(grid ? PhysicalKey.ArrowRight : PhysicalKey.ArrowDown);
                await firstDecoded.Task.WaitAsync(Deadline);
                for (int index = 1; index < 9; index++)
                    fixture.Key(!grid ? PhysicalKey.ArrowDown : index % 2 == 0 ? PhysicalKey.ArrowRight : PhysicalKey.ArrowLeft);
                fixture.Key(PhysicalKey.ArrowDown);
                int final = fixture.FocusedNumber();
                if (grid) Assert.True(final > 2);
                else Assert.Equal(11, final);
                clock.Advance(119);
                fixture.Pump();
                Assert.Equal(2, decodes.Count);
                clock.Advance(1);
                await fixture.ShowsAsync(final);
                await fixture.WaitAsync(() => fixture.Picture.Source != null, "latest image");
                Assert.Equal(new[] { ("file-1.png", 0L), ("file-2.png", 0L), ($"file-{final}.png", 120L) }, decodes);
                object? shown = fixture.Picture.Source;
                releaseFirst.TrySetResult();
                Bitmap late = await firstBitmap.Task.WaitAsync(Deadline);
                fixture.Pump();
                Assert.NotSame(late, shown);
                Assert.Same(shown, fixture.Picture.Source);

                await fixture.Session.SetTagQueueStatesAsync([KeyValuePair.Create(fixture.Items[final - 1].Key, TagQueueState.Skipped)]);
                fixture.Find<ComboBox>(fixture.Window, "TaggingFilter").SelectedIndex = (int)TaggingQueueFilter.Open;
                await fixture.Page.ActivateAsync();
                await fixture.WaitAsync(() => !fixture.PanelText(MetaId).Contains($"file-{final}.png", StringComparison.Ordinal)
                    && !ReferenceEquals(fixture.Picture.Source, shown), "filtered medium released");
                await fixture.Session.ClearTagQueueAsync();
                await fixture.Page.ActivateAsync();
                await fixture.WaitAsync(() => fixture.Picture.Source == null && !fixture.Picture.IsEffectivelyVisible
                    && !fixture.Find<Button>(fixture.Panel, "ReaderMediaSidebarOpen").IsEffectivelyEnabled, "empty sidebar");
            }
            finally { releaseFirst.TrySetResult(); }
        }, clock: clock, decode: async (entry, request, token) =>
        {
            bool gated = entry.Asset.RequestedFileName == "file-2.png";
            decodes.Add((entry.Asset.RequestedFileName, clock.Now));
            var image = await GalleryImageCache.DecodeAsync(entry, request, gated ? CancellationToken.None : token);
            if (gated)
            {
                firstDecoded.TrySetResult();
                await releaseFirst.Task;
                firstBitmap.TrySetResult(image.Bitmap);
            }
            return image;
        });
    }

    [Fact]
    public void ATagEditorCloseResumesThePendingCursorAfterItsTick() => WithPage(false, async fixture =>
    {
        fixture.ClickEntry(1);
        fixture.Key(PhysicalKey.ArrowDown);
        await fixture.ShowsAsync(2);
        await fixture.WaitForEditorAsync();
        fixture.Key(PhysicalKey.ArrowDown);
        Assert.Equal(3, fixture.FocusedNumber());
        fixture.Click(fixture.Find<Button>(fixture.Panel, EditId));
        Control popup = await fixture.WaitForPopoverAsync();
        fixture.Clock!.Advance(120);
        fixture.Pump();
        Assert.Contains("file-2.png", fixture.PanelText(MetaId));
        fixture.Key(PhysicalKey.Escape, root: TopLevel.GetTopLevel(popup));
        await fixture.ShowsAsync(3);
        Assert.Empty(fixture.Session.GetPublishedPrivateTagSnapshot().Snapshot!.Tags);
    }, clock: new ManualClock());

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RatingAndTaggingWriteOnlyTheCursorWhileTheSelectionStaysInTheQueue(bool grid) => WithPage(grid, async fixture =>
    {
        // The stamp gives existing tags only.
        await fixture.Session.DefinePrivateTagAsync("cursor tag");
        fixture.ClickEntry(1);
        await fixture.WaitForEditorAsync();
        fixture.ClickEntry(2, RawInputModifiers.Control);
        fixture.ClickEntry(3, RawInputModifiers.Control);
        Assert.Equal(3, fixture.List.SelectedItems!.Count);
        fixture.Key(grid ? PhysicalKey.ArrowRight : PhysicalKey.ArrowDown, RawInputModifiers.Control);
        Assert.Equal(4, fixture.FocusedNumber());
        await fixture.ShowsAsync(4);
        await fixture.WaitForEditorAsync();
        fixture.Key(PhysicalKey.F);
        fixture.Key(PhysicalKey.Digit3);
        Assert.Empty(fixture.Session.GetMediaRatings());
        Assert.Empty(fixture.Session.GetFavorites());
        Assert.True(fixture.Find<TextBlock>(fixture.Window, "TaggingStatus").IsEffectivelyVisible);
        Assert.Contains("R", fixture.Find<TextBlock>(fixture.Window, "TaggingStatus").Text);

        ToggleButton star = fixture.Star();
        Assert.False(star.Focusable);
        fixture.Click(star);
        FavoriteAssetKey key = AssetKey(fixture.Items[3]);
        await fixture.WaitAsync(() => fixture.Session.GetMediaRating(key) == 3, "the cursor rating");
        Assert.Equal(key, Assert.Single(fixture.Session.GetMediaRatings()).Key);
        await fixture.WaitAsync(() => fixture.Star().IsChecked == true, "the selected third star");
        Assert.Equal(4, fixture.FocusedNumber());
        if (fixture.FindOrDefault<Control>(fixture.Panel, "ReaderMediaSidebarInfoBar") != null)
        {
            fixture.Click(fixture.Star());
            await fixture.WaitAsync(() => fixture.Session.GetMediaRating(key) == null, "clear the active star");
            fixture.Click(fixture.Star());
            await fixture.WaitAsync(() => fixture.Session.GetMediaRating(key) == 3, "restore the third star");
        }

        Button chip = fixture.FindOrDefault<Button>(fixture.Panel, "ReaderMediaSidebarTagsEmpty") ?? fixture.Find<Button>(fixture.Panel, EditId);
        fixture.Click(chip);
        Control popup = await fixture.WaitForPopoverAsync();
        Assert.Contains("file-4.png", fixture.Find<TextBlock>(popup, "TaggingTagPopoverTitle").Text);
        Button stamp = fixture.Find<Button>(popup, "TaggingTagPopoverStamp");
        Assert.True(stamp.IsEffectivelyEnabled);
        fixture.Click(stamp);
        await fixture.WaitAsync(() => fixture.StoredTags(4).Contains("cursor tag"), "stamped cursor tag");
        fixture.Key(PhysicalKey.Escape, root: TopLevel.GetTopLevel(popup));
        await fixture.WaitAsync(() => fixture.FindOrDefault<Control>(fixture.Window, "TaggingTagPopoverFrame") == null, "closed popover");

        for (int number = 1; number <= 4; number++)
            Assert.Equal(number == 4 ? new[] { "cursor tag" } : Array.Empty<string>(), fixture.StoredTags(number));
        TagQueueSnapshot queue = fixture.Session.GetTagQueue()!;
        Assert.Equal(fixture.Items.Select(item => item.Key), queue.Items.Select(item => item.Key));
        Assert.All(queue.Items, item => Assert.Equal(TagQueueState.Open, item.State));
        fixture.Page.SetViewMode(TaggingViewMode.List, save: false);
        await fixture.WaitAsync(() => fixture.FindOrDefault<TextBlock>(fixture.Entry(4), "TaggingQueueRowTags")?.Text == "1", "updated cursor tag count");
        if (fixture.FindOrDefault<Control>(fixture.Panel, "ReaderMediaSidebarInfoBar") != null)
            Assert.Equal(Texts.T("tags.media.buttonCount", 1),
                AutomationProperties.GetName(fixture.Find<Control>(fixture.Panel, "ReaderMediaSidebarTagsToggle")));
        Assert.Equal("3", fixture.Find<TextBlock>(fixture.Entry(4), "TaggingQueueRowRating").Text);

        fixture.ClickEntry(4);
        fixture.Key(PhysicalKey.F);
        await fixture.WaitAsync(() => fixture.Session.GetFavorites().Any(entry => entry.Key == key), "favorite from F");
        await fixture.WaitAsync(() => fixture.Find<ToggleButton>(fixture.Panel, FavoriteId).IsChecked == true, "sidebar favorite");
        fixture.Key(PhysicalKey.Digit0, RawInputModifiers.Shift);
        await fixture.WaitAsync(() => fixture.Session.GetMediaRating(key) == null, "clear rating by Shift+0");
        fixture.Key(PhysicalKey.Digit3);
        await fixture.WaitAsync(() => fixture.Session.GetMediaRating(key) == 3, "rating by 3");
        fixture.Key(PhysicalKey.Digit0);
        await fixture.WaitAsync(() => fixture.Session.GetMediaRating(key) == null, "clear rating by 0");
        fixture.Key(PhysicalKey.Digit3, RawInputModifiers.Shift);
        await fixture.WaitAsync(() => fixture.Session.GetMediaRating(key) == 3, "rating by Shift+3");
        fixture.Page.SetViewMode(TaggingViewMode.Grid, save: false);
        await fixture.ShowsAsync(4);
        MediaBadgeView badges = fixture.Find<MediaBadgeView>(fixture.Entry(4), "TaggingTileBadges");
        await fixture.WaitAsync(() => badges.IsEffectivelyVisible && badges.Plan.Badges.Any(b => b.Badge.Id == MediaBadgeId.Favorite)
            && badges.Plan.Badges.Any(b => b.Badge.Id == MediaBadgeId.Rating && b.Badge.Label == "3"), "visible grid badges");
        fixture.Key(PhysicalKey.ArrowLeft);
        await fixture.ShowsAsync(fixture.FocusedNumber());
        Assert.NotEqual(4, fixture.FocusedNumber());
    });

    [Fact]
    public void TheEditorIsDisabledUntilTheSavedFileIsConfirmed()
    {
        TaskCompletionSource available = new(TaskCreationOptions.RunContinuationsAsynchronously);
        WithPage(false, async fixture =>
        {
            try
            {
                await fixture.ShowsAsync(1);
                Button? editor = fixture.FindOrDefault<Button>(fixture.Panel, EditId);
                Assert.False(editor is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true });
                available.TrySetResult();
                await fixture.WaitForEditorAsync();
                fixture.Click(fixture.Find<Button>(fixture.Panel, EditId));
                await fixture.WaitForPopoverAsync();
            }
            finally { available.TrySetResult(); }
        }, openManifest: async (directory, token) =>
        {
            await available.Task.WaitAsync(token);
            return await MediaAssetReader.OpenAsync(directory, token);
        });
    }

    [Fact]
    public void AnUnavailableFileLosesTheEditorAndTheNextValidFileCanBeEdited() => WithPage(false, async fixture =>
    {
        await fixture.WaitForEditorAsync();
        fixture.Click(fixture.Find<Button>(fixture.Panel, EditId));
        Control popup = await fixture.WaitForPopoverAsync();
        fixture.Key(PhysicalKey.Escape, root: TopLevel.GetTopLevel(popup));
        fixture.ClickEntry(2);
        await fixture.ShowsAsync(2);
        Assert.False(fixture.FindOrDefault<Button>(fixture.Panel, EditId)?.IsEffectivelyEnabled == true
            && fixture.FindOrDefault<Button>(fixture.Panel, EditId)?.IsEffectivelyVisible == true);
        fixture.ClickEntry(3);
        await fixture.ShowsAsync(3);
        await fixture.WaitForEditorAsync();
        fixture.Click(fixture.Find<Button>(fixture.Panel, EditId));
        popup = await fixture.WaitForPopoverAsync();
        Assert.Contains("file-3.png", fixture.Find<TextBlock>(popup, "TaggingTagPopoverTitle").Text);
        fixture.Key(PhysicalKey.Escape, root: TopLevel.GetTopLevel(popup));
    }, missing: 2);

    [Theory]
    [InlineData(1000, 360, false)]
    [InlineData(1400, 600, true)]
    public void FullTableFitsBesideEitherDockAndDraggingKeepsTheCursor(int width, int panelWidth, bool left) => WithPage(false, async fixture =>
    {
        MediaPageLayout layout = fixture.Layout with { PanelWidth = panelWidth, PanelSide = left ? MediaPanelSide.Left : MediaPanelSide.Right };
        fixture.Layout = layout;
        await fixture.Session.CreateTagQueueGroupAsync("A group with a very long label", [fixture.Items[0].Key]);
        await fixture.Page.ActivateAsync();
        fixture.Page.ApplyPanelLayout();
        BooruReviewTestPlatform.Resize(fixture.Window, width);
        Control listArea = fixture.Find<Control>(fixture.Window, "TaggingListArea");
        Rect list = BoundsIn(listArea, fixture.Window), panel = BoundsIn(fixture.Panel, fixture.Window);
        Assert.InRange(panel.Width, Math.Min(panelWidth, width - 646) - .5, Math.Min(panelWidth, width - 646) + .5);
        Assert.InRange(panel.Top - list.Top, -.5, .5);
        Assert.InRange(panel.Bottom - fixture.Page.Bounds.Height, -.5, .5);
        Assert.True(left ? panel.Right < list.Left : panel.Left > list.Right);
        Control state = fixture.Find<Control>(fixture.Entry(1), "TaggingQueueRowState");
        Control columns = fixture.Find<Control>(fixture.Window, "TaggingColumns");
        Assert.True(BoundsIn(state, fixture.Window).Right <= list.Right + .5);
        Assert.True(columns.GetVisualChildren().OfType<Control>().Where(child => child.IsEffectivelyVisible)
            .All(child => BoundsIn(child, fixture.Window).Right <= list.Right + .5));
        Assert.Equal(ScrollBarVisibility.Disabled, ScrollViewer.GetHorizontalScrollBarVisibility(fixture.List));

        fixture.ClickEntry(1);
        fixture.ClickEntry(2, RawInputModifiers.Control);
        fixture.Key(PhysicalKey.ArrowDown, RawInputModifiers.Control);
        await fixture.ShowsAsync(3);
        if (width == 1000) BooruReviewTestPlatform.Resize(fixture.Window, 1400);
        await fixture.WaitAsync(() => fixture.Picture.Source is Bitmap, "the starting decode before dragging");
        // The first decode goes back to the cache once the larger one is shown; keep its size, not the bitmap.
        int before = ((Bitmap)fixture.Picture.Source!).PixelSize.Width;
        GridSplitter handle = fixture.Find<GridSplitter>(fixture.Window, HandleId);
        Assert.True(handle.Focus());
        Point center = BoundsIn(handle, fixture.Window).Center;
        const int drag = 96;
        Point end = new(center.X + (left ? drag : -drag), center.Y);
        fixture.Window.MouseDown(center, MouseButton.Left);
        fixture.Window.MouseMove(end, RawInputModifiers.LeftMouseButton);
        fixture.Window.MouseUp(end, MouseButton.Left);
        await fixture.WaitAsync(() => fixture.Layout.PanelWidth == panelWidth + drag, "saved width after dragging");
        await fixture.ShowsAsync(3);
        await fixture.WaitAsync(() => fixture.Picture.Source is Bitmap bigger
            && bigger.PixelSize.Width > before, "larger decode of the same cursor");
    }, video: 2, wideImage: 3);

    [Fact]
    public void ToggleAndCloseUsePointerInputAndNarrowingKeepsTheChoice() => WithPage(false, async fixture =>
    {
        Rect header = fixture.Find<Control>(fixture.Window, "TaggingHeader").Bounds;
        BooruReviewTestPlatform.Resize(fixture.Window, 900);
        ToggleButton toggle = fixture.Find<ToggleButton>(fixture.Window, ToggleId);
        Assert.False(fixture.Panel.IsEffectivelyVisible);
        Assert.False(fixture.Find<Control>(fixture.Window, HandleId).IsEffectivelyVisible);
        Assert.True(toggle.IsEffectivelyEnabled);
        Assert.True(toggle.IsChecked);
        Assert.Contains(Texts.T("tagging.page.panel.narrow"), Assert.IsType<string>(ToolTip.GetTip(toggle)));
        BooruReviewTestPlatform.Resize(fixture.Window, 1400);
        await fixture.WaitAsync(() => fixture.Panel.IsEffectivelyVisible, "sidebar after widening");
        Assert.All(fixture.Panel.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible),
            button => Assert.False(button.Focusable, AutomationProperties.GetAutomationId(button)));
        fixture.Click(toggle);
        Assert.False(fixture.Panel.IsEffectivelyVisible);
        Assert.Equal(header, fixture.Find<Control>(fixture.Window, "TaggingHeader").Bounds);
        fixture.Click(toggle);
        await fixture.ShowsAsync(1);
        fixture.Click(fixture.Find<Button>(fixture.Panel, CloseId));
        Assert.False(fixture.Panel.IsEffectivelyVisible);
        Assert.True(fixture.List.IsKeyboardFocusWithin);
        await fixture.WaitAsync(() => !fixture.Session.Config.TaggingPagePanel, "saved off preference");
    });

    [Theory]
    [InlineData(ShortcutPlatform.Linux, KeyModifiers.Control)]
    [InlineData(ShortcutPlatform.MacOS, KeyModifiers.Meta)]
    public void SharedSidebarAndDecisionBindingsUseTheTaggingContext(ShortcutPlatform platform, KeyModifiers primary)
    {
        const string toggle = "media.panel.toggle";
        const string rate = "media.rating.set-3";
        const string favorite = "favorites.toggle";
        string done = TaggingCommands.MarkDone;
        EffectiveShortcutBindings defaults = EffectiveShortcutBindings.Build(platform: platform);
        foreach (string context in new[] { "tagging", "media" })
        {
            Assert.True(defaults.Matches(new KeyEventArgs { Key = Key.M, KeyModifiers = primary | KeyModifiers.Shift }, toggle, context));
            Assert.True(defaults.Matches(new KeyEventArgs { Key = Key.D3, KeyModifiers = KeyModifiers.Shift }, rate, context));
        }
        var rebind = new ShortcutBindingsSection().Set(done, [ShortcutGesture.Parse("Shift+3")])
            .Set(favorite, [ShortcutGesture.Parse("Alt+F")]).Set(TaggingCommands.MarkSkipped, [ShortcutGesture.Parse("Alt+F")]);
        var effective = EffectiveShortcutBindings.Build(rebind, platform);
        Assert.True(effective.Matches(new KeyEventArgs { Key = Key.D3, KeyModifiers = KeyModifiers.Shift }, rate, "media"));
        Assert.False(effective.Matches(new KeyEventArgs { Key = Key.D3, KeyModifiers = KeyModifiers.Shift }, rate, "tagging"));
        Assert.Contains(effective.Issues, issue => issue.BlocksSave && (issue.CommandId == favorite || issue.OtherCommandId == favorite));
    }

    [Fact]
    public async Task SidebarChoiceSurvivesCopyTransferAStaleSettingsSaveAndProfileRestart()
    {
        using TempDirectory temp = new();
        WriteProfile(temp.Path);
        await using (DesktopSessionHost session = NewSession(temp.Path))
        {
            await session.StartAsync();
            Config draft = session.Config.Clone();
            await session.SaveViewPreferencesAsync(config => config.TaggingPagePanel = false);
            Assert.False(session.Config.Clone().TaggingPagePanel);
            Config copied = new();
            copied.CopyFrom(session.Config);
            Assert.False(copied.TaggingPagePanel);
            copied.CopySettingsFieldsFrom(draft);
            Assert.False(copied.TaggingPagePanel);
            JsonObject exported = SettingsTransfer.Export(session.Config);
            Assert.False(exported[nameof(Config.TaggingPagePanel)]!.GetValue<bool>());
            string transferred = Path.Combine(temp.Path, "transferred");
            Directory.CreateDirectory(transferred);
            File.WriteAllText(Path.Combine(transferred, "settings.json"), exported.ToJsonString());
            Assert.False(Config.LoadConfig(transferred).TaggingPagePanel);
            await session.ApplySettingsAsync(draft);
            Assert.False(session.Config.TaggingPagePanel);
        }
        await using DesktopSessionHost reopened = NewSession(temp.Path);
        await reopened.StartAsync();
        Assert.False(reopened.Config.TaggingPagePanel);
    }

    [Fact]
    public void SettingsResetPreservesTheDirectSidebarChoice() => WithPage(false, async fixture =>
    {
        fixture.Click(fixture.Find<ToggleButton>(fixture.Window, ToggleId));
        await fixture.WaitAsync(() => !fixture.Session.Config.TaggingPagePanel, "confirmed panel preference");

        TaskCompletionSource saved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SettingsEditor editor = new(fixture.Session.Config, async draft =>
        {
            await fixture.Session.ApplySettingsAsync(draft);
            saved.TrySetResult();
        });
        Window settings = new() { Width = 1400, Height = 900, Content = editor };
        settings.Show();
        try
        {
            // "Reset all" also resets the download destination, which this bare profile cannot validate; the interface reset saves.
            editor.ResetDraft(SettingsEditor.SettingsResetScope.Interface);
            settings.UpdateLayout();
            Button save = fixture.Find<Button>(settings, "SaveSettingsButton");
            fixture.Click(save);
            await fixture.WaitAsync(() => saved.Task.IsCompleted || !editor.IsBusy, "the settings save to finish");
            Assert.True(saved.Task.IsCompleted, $"The save did not run: error '{editor.ErrorMessage}', available {editor.IsAvailable}, "
                + $"button enabled {save.IsEffectivelyEnabled}");
            Assert.False(fixture.Session.Config.TaggingPagePanel);
            Assert.False(fixture.Find<ToggleButton>(fixture.Window, ToggleId).IsChecked);
        }
        finally
        {
            editor.EndLifetime();
            settings.Close();
        }
    });

    [Fact]
    public void RemappedShiftDigitRatesTheFocusedEntryThroughTheList() => WithPage(false, async fixture =>
    {
        const string rating = "media.rating.set-3";
        ShortcutService.Publish(ShortcutService.CurrentSection.Set(rating, [ShortcutGesture.Parse("Shift+7")]));
        fixture.ClickEntry(1);
        await fixture.ShowsAsync(1);
        fixture.Key(PhysicalKey.Digit3, RawInputModifiers.Shift);
        Assert.Empty(fixture.Session.GetMediaRatings());
        fixture.Key(PhysicalKey.Digit7, RawInputModifiers.Shift);
        await fixture.WaitAsync(() => fixture.Session.GetMediaRating(AssetKey(fixture.Items[0])) == 3, "remapped rating gesture");
        Assert.True(fixture.Star().IsChecked);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OldAndDamagedProfilesKeepTheDefaultAndAnIndependentPreference(bool invalid)
    {
        using TempDirectory temp = new();
        WriteProfile(temp.Path);
        string path = Path.Combine(temp.Path, "settings.json");
        JsonObject json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        if (invalid) json[nameof(Config.TaggingPagePanel)] = "invalid";
        else json.Remove(nameof(Config.TaggingPagePanel));
        File.WriteAllText(path, json.ToJsonString());
        Config config = Config.LoadConfig(temp.Path);
        Assert.True(config.TaggingPagePanel);
        Assert.False(config.SaveUrlsOnExit);
    }

    private static Rect BoundsIn(Control control, Visual root) => new(control.TranslatePoint(default, root)!.Value, control.Bounds.Size);

    private static FavoriteAssetKey AssetKey(TagQueueItem item) => MediaLibraryFilterEngine.AssetKey(item.ToLibraryEntry())!;

    private static void WriteProfile(string profile) => new Config
    {
        DownloadPath = Path.Combine(profile, "downloads"),
        FirstRunSetupVersion = 1,
        DownloadsPaused = true,
        SaveUrlsOnExit = false
    }.SaveConfig(profile);

    private static DesktopSessionHost NewSession(string profile) => new(profile, [],
        updateSessionFactory: (path, config) => new UpdateSession(path, config, disableEnvironment: () => "1"));

    private static void WithPage(bool grid, Func<Fixture, Task> test, ManualClock? clock = null, int missing = 0, int video = 0, int wideImage = 0, bool skipFirst = false,
        Func<GalleryEntry, GalleryDecodeRequest, CancellationToken, Task<(Bitmap, GalleryImageInfo)>>? decode = null,
        Func<string, CancellationToken, Task<MediaAssetSnapshot>>? openManifest = null) =>
        BooruComponentDispatcher.Run(() =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            using TempDirectory profile = new();
            BooruComponentDispatcher.Wait(RunAsync());

            async Task RunAsync()
            {
                WriteProfile(profile.Path);
                string downloads = Path.Combine(profile.Path, "downloads");
                string directory = ArchiveCleanupFixtures.DirectoryFor(downloads, ThreadId);
                string[] names = Enumerable.Range(1, 12).Select(n => $"file-{n}.{(n == video ? "webm" : "png")}").ToArray();
                var assets = names.Select((name, index) =>
                    new ArchiveCleanupFixtures.Asset("media-" + (index + 1), "original", name, name,
                        index + 1 == wideImage ? WideImageBytes() : ImageBytes(index + 1))).ToArray();
                await ArchiveCleanupFixtures.AssetsAsync(directory, assets);
                await ArchiveCleanupFixtures.RegisterAsync(profile.Path, downloads, ThreadId);
                MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(directory);
                IReadOnlyList<MediaAssetReference> savedAssets = manifest.GetPage();
                TagQueueItem[] items = names.Select((name, index) =>
                {
                    int number = index + 1;
                    MediaAssetReference asset = savedAssets.Single(candidate => candidate.RequestedFileName == name);
                    return new TagQueueItem(ThreadId, directory, asset.AssetId, asset.Sha256!, asset.Length,
                        asset.MediaIdentifier, asset.Variant, number == 12 ? null : number.ToString(), null, null, null,
                        false, name, name, number == video ? SavedMediaKind.Video : SavedMediaKind.Image,
                        TagQueueState.Open, DateTimeOffset.UnixEpoch.AddSeconds(number));
                }).ToArray();
                if (missing > 0) File.Delete(Path.Combine(directory, names[missing - 1]));
                await using DesktopSessionHost session = NewSession(profile.Path);
                await session.StartAsync();
                await session.EnqueueTagQueueAsync(items);
                if (skipFirst)
                    await session.SetTagQueueStatesAsync([KeyValuePair.Create(items[0].Key, TagQueueState.Skipped)]);
                using TaggingPage page = new(session)
                {
                    PanelClock = clock ?? TimeProvider.System,
                    DecodePanelImageAsync = decode ?? GalleryImageCache.DecodeAsync,
                    OpenPanelManifestAsync = openManifest ?? MediaAssetReader.OpenAsync,
                    ReadUserData = () => new(session.GetPublishedPrivateTagSnapshot().Snapshot, session.GetFavorites(), session.GetMediaRatings()),
                    TagServices = new(session.GetPublishedPrivateTagSnapshot, session.DefineAndSetMediaSubjectTagsAsync, session.SetMediaSubjectTagsAsync),
                    Stamp = new QuickTagStamp(["cursor tag"]),
                    OpenGallery = (_, _) => Task.CompletedTask,
                    ExecutePanelDecisionAsync = async (id, media, current) =>
                    {
                        Assert.True(current());
                        FavoriteAssetKey key = MediaLibraryFilterEngine.AssetKey(media)!;
                        if (id == "favorites.toggle")
                            await session.SetFavoriteAsync(new FavoriteEntry(key, media.Length!.Value, media.Name),
                                !session.GetFavorites().Any(entry => entry.Key == key));
                        else
                            await session.SetMediaRatingAsync(key, id == "media.rating.clear" ? null : id[^1] - '0');
                    }
                };
                if (grid) page.RestoreViewMode(TaggingViewMode.Grid);
                Window window = new() { Width = 1400, Height = 900, Content = page, Classes = { MotionPolicy.OffClass } };
                Fixture fixture = new(window, page, session, items, clock);
                page.ReadPanelLayout = () => fixture.Layout;
                page.SavePanelWidth = value => fixture.Layout = fixture.Layout with { PanelWidth = value };
                page.SavePanelEnabled = enabled => _ = session.SaveViewPreferencesAsync(config => config.TaggingPagePanel = enabled);
                window.Show();
                try
                {
                    await page.ActivateAsync();
                    fixture.Pump();
                    await test(fixture);
                }
                finally { window.Close(); }
            }
        });

    private static byte[] WideImageBytes()
    {
        using SKSurface surface = SKSurface.Create(new SKImageInfo(1600, 800));
        surface.Canvas.Clear(SKColors.Teal);
        using SKImage image = surface.Snapshot();
        using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }

    private static byte[] ImageBytes(int number)
    {
        byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAABEAAAALCAIAAAAWQvFQAAAAE0lEQVR4nGNgmMlAMhrVM6oHiADlWm/EglqV/gAAAABJRU5ErkJggg==");
        return [.. png, (byte)number];
    }

    private sealed class Fixture(Window window, TaggingPage page, DesktopSessionHost session, TagQueueItem[] items, ManualClock? clock)
    {
        internal Window Window { get; } = window;
        internal TaggingPage Page { get; } = page;
        internal DesktopSessionHost Session { get; } = session;
        internal TagQueueItem[] Items { get; } = items;
        internal ManualClock? Clock { get; } = clock;
        internal MediaPageLayout Layout { get; set; } = MediaPageLayout.Parse(session.Config.MediaLayout, session.Config.MediaBooruMode);
        internal Control Panel => Find<Control>(Window, PanelId);
        internal Image Picture => Find<Image>(Panel, PictureId);
        internal ListBox List => Find<ListBox>(Window, Page.ViewMode == TaggingViewMode.List ? "TaggingList" : "TaggingGrid");
        internal ListBoxItem Entry(int number) => Find<ListBoxItem>(List, Id(List) + "Entry" + number);

        internal string PanelText(string id) => FindOrDefault<TextBlock>(Panel, id) is { IsEffectivelyVisible: true } text
            ? text.Text ?? string.Empty : string.Empty;
        internal ToggleButton Star() => FindOrDefault<ToggleButton>(Panel, ThirdStarId) is { IsEffectivelyVisible: true } star
            ? star : Find<ToggleButton>(Panel, "ReaderMediaSidebarOverlayRatingRating3");
        internal void ClickEntry(int number, RawInputModifiers modifiers = RawInputModifiers.None) => Click(Entry(number), modifiers);

        internal void Click(Control target, RawInputModifiers modifiers = RawInputModifiers.None)
        {
            TopLevel root = TopLevel.GetTopLevel(target)!;
            Point center = BoundsIn(target, root).Center;
            root.MouseMove(center);
            Pump();
            center = BoundsIn(target, root).Center;
            Assert.Contains(target, Assert.IsAssignableFrom<Visual>(root.InputHitTest(center)).GetSelfAndVisualAncestors());
            root.MouseDown(center, MouseButton.Left, modifiers);
            root.MouseUp(center, MouseButton.Left, modifiers);
            Pump();
        }

        internal void Key(PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None, TopLevel? root = null)
        {
            (root ?? Window).KeyPressQwerty(key, modifiers);
            (root ?? Window).KeyReleaseQwerty(key, modifiers);
            Pump();
        }

        internal int FocusedNumber()
        {
            var target = Assert.IsAssignableFrom<Control>(Window.FocusManager!.GetFocusedElement());
            string id = Id(target);
            Assert.StartsWith(Id(List) + "Entry", id);
            return int.Parse(id.AsSpan((Id(List) + "Entry").Length), System.Globalization.CultureInfo.InvariantCulture);
        }

        internal void Pump()
        {
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
        }

        internal async Task WaitAsync(Func<bool> predicate, string expected)
        {
            await BooruTestSupport.WaitAsync(() =>
            {
                Window.UpdateLayout();
                return predicate();
            }, expected, (int)Deadline.TotalMilliseconds);
        }

        internal Task ShowsAsync(int number) => WaitAsync(() =>
        {
            string meta = PanelText(MetaId), position = $"{number} / {Items.Length}";
            Control? pill = FindOrDefault<Control>(Panel, "ReaderMediaSidebarThreadPosition");
            bool positionShown = PanelText("ReaderMediaSidebarPosition") == position
                || meta.Contains(position, StringComparison.Ordinal)
                || pill is { IsEffectivelyVisible: true, Bounds.Width: > 0 }
                && (AutomationProperties.GetName(pill)?.Contains(position, StringComparison.Ordinal) == true
                    || AutomationProperties.GetName(pill) == Texts.T("tagging.position", number, Items.Length));
            string heading = PanelText("ReaderMediaSidebarHeading");
            TagQueueItem item = Items[number - 1];
            return positionShown && (item.PostId == null
                ? heading == item.Name
                : heading == Texts.T("reader.postNumber", item.PostId) && meta.Contains(item.Name, StringComparison.Ordinal));
        }, $"visible header for {Items[number - 1].Name}");

        internal Task WaitForEditorAsync() => WaitAsync(() => FindOrDefault<Button>(Panel, EditId) is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true }, "tag editor");

        internal async Task<Control> WaitForPopoverAsync()
        {
            await WaitAsync(() => FindOrDefault<Control>(Window, "TaggingTagPopoverFrame") is { IsEffectivelyVisible: true }, "tag popover");
            return Find<Control>(Window, "TaggingTagPopoverFrame");
        }

        internal string[] StoredTags(int number) => Session.GetPublishedPrivateTagSnapshot().Snapshot!
            .TagsOf(MediaTagSubjects.Of(Items[number - 1].ToLibraryEntry())!).Select(tag => tag.Name.Display).ToArray();

        internal T Find<T>(Control root, string id) where T : Control => Assert.IsAssignableFrom<T>(FindOrDefault<T>(root, id));
        internal T? FindOrDefault<T>(Control root, string id) where T : Control
        {
            IEnumerable<Control> controls = root.GetSelfAndVisualDescendants().OfType<Control>();
            if (root is TopLevel top)
                controls = controls.Concat(top.OpenedPopups.SelectMany(popup =>
                    popup.Child?.GetSelfAndVisualDescendants().OfType<Control>() ?? []));
            return controls.OfType<T>().FirstOrDefault(control => Id(control) == id);
        }

        private static string Id(Control control) => AutomationProperties.GetAutomationId(control) is { Length: > 0 } id
            ? id : ControlAutomationPeer.CreatePeerForElement(control)?.GetAutomationId() ?? string.Empty;
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly List<Timer> _timers = [];
        internal long Now { get; private set; }
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state, dueTime);
            _timers.Add(timer);
            return timer;
        }

        internal void Advance(long milliseconds)
        {
            Now += milliseconds;
            foreach (Timer timer in _timers.Where(timer => timer.Due <= Now).ToArray())
                timer.Fire();
        }

        private sealed class Timer(ManualClock clock, TimerCallback callback, object? state, TimeSpan due) : ITimer
        {
            internal long Due { get; private set; } = clock.Now + (long)due.TotalMilliseconds;
            internal void Fire() { Dispose(); callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Due = clock.Now + (long)dueTime.TotalMilliseconds;
                return true;
            }

            public void Dispose() => clock._timers.Remove(this);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
