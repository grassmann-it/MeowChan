using System.Reflection;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Motion;
using MeowChan.Desktop.Updates;
using MeowChan.Desktop.WorkViews;
using MeowChan.Storage;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Favorites;
using MeowChan.Persistence;
using MeowChan.Tags;
using Semi.Avalonia.DataGrid;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class SavedMediaReaderTests
{
    private static readonly ResourceId ThreadId = new("fixture", "g", "42");
    private static readonly ResourceId NextThreadId = new("fixture", "g", "50");
    private static readonly string[] FooterActions = ["ArchiveEditRatingButton", "PreviewArchiveMediaButton", "ArchiveManageRatingsButton"];

    [Fact]
    public void NavigationModesFollowTheReaderTabAndAutoRevealsThenHides() => RunWithShell(async (page, window, host, _) =>
    {
        long now = 0;
        page.UseManualReaderTicks(() => now);
        TabControl tabs = ById<TabControl>(window, "ArchiveContentTabs");
        TabItem posts = ById<TabItem>(tabs, "ArchivePostsTab");
        TabItem savedMedia = ById<TabItem>(tabs, "ArchiveMediaTab");
        TabItem references = await PopulateReferencesAsync(page, window, host);
        Control bar = ById<Control>(window, "ReaderNavigation");
        int delay = Config.ControlsIdleHideMsDefault;
        page.ReaderNavigationIdleDelayMs = delay;
        foreach (string mode in new[] { Config.ReaderNavigationModeOff, Config.ReaderNavigationModeAlways, Config.ReaderNavigationModeAuto })
        {
            SelectTab(window, posts);
            page.ReaderNavigationMode = mode;
            foreach (TabItem tab in new[] { posts, references })
            {
                SelectTab(window, tab);
                Assert.Same(tab, tabs.SelectedItem);
                page.NotifyReaderPointerMoved(default);
                bool visible = mode != Config.ReaderNavigationModeOff;
                Assert.Equal(visible, bar.IsEffectivelyVisible);
                Assert.Equal(visible, bar.IsHitTestVisible);
                Assert.Equal(visible ? 1 : 0, bar.Opacity);
                now += delay - 2;
                page.TickReaderNavigationIdle();
                Assert.Equal(visible, bar.IsHitTestVisible);
                now += 2;
                page.TickReaderNavigationIdle();
                bool staysShown = mode == Config.ReaderNavigationModeAlways;
                Assert.Equal(staysShown, bar.IsHitTestVisible);
                Assert.Equal(staysShown ? 1 : 0, bar.Opacity);
                if (mode == Config.ReaderNavigationModeAuto)
                {
                    page.NotifyReaderPointerMoved(default);
                    Assert.True(bar.IsHitTestVisible);
                    Assert.Equal(1, bar.Opacity);
                }
            }
            SelectTab(window, savedMedia);
            page.NotifyReaderPointerMoved(default);
            Assert.False(bar.IsEffectivelyVisible);
            Assert.False(bar.IsHitTestVisible);
        }
    });

    [Fact]
    public void NavigationBarTakesTabWithoutLettingItsArrowsChangeThePostOrThread() => Run((page, window) =>
    {
        page.FindControl<TabControl>("ArchiveContentTabs")!.SelectedIndex = 0;
        page.SetReaderScope([ThreadId, NextThreadId]);
        page.ReaderNavigationMode = Config.ReaderNavigationModeAlways;
        BooruComponentDispatcher.Wait(page.FocusPostAsync("42"));
        Layout(window);
        Border bar = VisibleById<Border>(window, "ReaderNavigationBar");
        Button nextPost = VisibleById<Button>(bar, "ReaderNextPost");
        Button nextThread = VisibleById<Button>(bar, "ReaderNextThread");
        Assert.True(nextPost.IsEffectivelyEnabled && nextThread.IsEffectivelyEnabled);
        Assert.All(new[] { nextPost, nextThread }, button =>
        {
            Assert.False(button.Focusable);
            Assert.False(button.IsTabStop);
        });
        int tabStops = window.GetVisualDescendants().OfType<InputElement>().Count(control => control.IsTabStop);
        for (int index = 0; index < tabStops && !bar.IsFocused; index++)
            PressKey(window, PhysicalKey.Tab);
        Assert.True(bar.IsFocused);
        PressKey(window, PhysicalKey.ArrowRight);
        PressKey(window, PhysicalKey.ArrowDown);
        PressKey(window, PhysicalKey.Enter);
        BooruComponentDispatcher.Wait(page.ReaderNavigationTask);
        Assert.Equal(ThreadId, page.CurrentThread!.Summary.Id);
        Assert.Equal("42", page.CurrentReaderPostId);

        Click(window, nextPost);
        BooruComponentDispatcher.Wait(page.ReaderNavigationTask);
        Assert.Equal("43", page.CurrentReaderPostId);
        ShortcutBindingsSection previous = ShortcutService.CurrentSection;
        try
        {
            ShortcutService.Publish(previous.Clone().Set("reader.next-thread", [ShortcutGesture.Parse("F8")]));
            PressKey(window, PhysicalKey.F8);
            BooruComponentDispatcher.Wait(page.ReaderNavigationTask);
            Assert.Equal(NextThreadId, page.CurrentThread!.Summary.Id);
            Assert.Equal("50", page.CurrentReaderPostId);
        }
        finally { ShortcutService.Publish(previous); }
    }, withNavigation: true);

    [Fact]
    public void MacPagingShortcutsChangeReferencePagesFromTheFocusedNavigationBar() => BooruComponentDispatcher.Run(async () =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        Application.Current!.Styles.Insert(1, new DataGridSemiTheme());
        using TempDirectory profile = new();
        CapturedPost[] posts = Enumerable.Range(42, 52).Select(id => new CapturedPost(
            id.ToString(System.Globalization.CultureInfo.InvariantCulture), id, PostFieldPresence.All,
            Html: id == 42 ? "Opening post" : "<a href='#42'>&gt;&gt;42</a>")).ToArray();
        using (ProfileLease lease = ProfileLease.Acquire(profile.Path))
        await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile.Path, lease))
            await catalog.CaptureAsync(new(ThreadId, "https://offline.invalid/g/thread/42", "References",
                ThreadSourceState.Active, DateTimeOffset.UnixEpoch, profile.Path, posts));

        ArchivePage page = new(token => ArchiveReader.OpenAsync(profile.Path, token), (_, _) => Task.CompletedTask, _ => { })
            { IsAvailable = true, ReaderNavigationMode = Config.ReaderNavigationModeAlways };
        Window window = new() { Width = 1440, Height = 840 };
        FieldInfo bindings = typeof(ShortcutService).GetField("_current", BindingFlags.Static | BindingFlags.NonPublic)!;
        EffectiveShortcutBindings previous = ShortcutService.Current;
        try
        {
            bindings.SetValue(null, EffectiveShortcutBindings.Build(platform: ShortcutPlatform.MacOS));
            await page.LoadThreadAsync(ThreadId);
            window.Content = page.DetachReader();
            window.Show();
            window.PlatformImpl!.Activated?.Invoke();
            await page.LoadBacklinksAsync(new(ThreadId, "42"));
            Layout(window);
            Assert.True(ById<TabItem>(window, "ArchiveReferencesTab").IsSelected);
            var references = Assert.Single(window.GetVisualDescendants().OfType<ListBox>(), list =>
                ControlAutomationPeer.CreatePeerForElement(list).GetAutomationId() == "ArchiveReferences");
            string[] ReplyIds() => references.Items.Cast<ArchiveQuoteRow>().Select(row => row.Quote.Origin.PostId).ToArray();
            string[] firstPage = ReplyIds();
            Assert.Equal(50, firstPage.Length);
            Border bar = VisibleById<Border>(window, "ReaderNavigationBar");
            Assert.True(bar.Focus(NavigationMethod.Tab) && bar.IsFocused);
            const RawInputModifiers commandOption = RawInputModifiers.Meta | RawInputModifiers.Alt;
            PressKey(window, PhysicalKey.ArrowRight, commandOption);
            await BooruTestSupport.WaitAsync(() => references.ItemCount == 1, "the second page of replies");
            Assert.DoesNotContain(Assert.Single(ReplyIds()), firstPage);
            Assert.True(bar.IsFocused);
            PressKey(window, PhysicalKey.ArrowLeft, commandOption);
            await BooruTestSupport.WaitAsync(() => references.ItemCount == 50, "the first page of replies");
            Assert.Equal(firstPage, ReplyIds());
            Assert.True(bar.IsFocused);
        }
        finally
        {
            bindings.SetValue(null, previous);
            page.EndLifetime();
            window.Close();
        }
    });

    [Fact]
    public void ReferenceListKeepsItsNativeArrowNavigationFromTheLink() => Run((page, window) =>
    {
        ListBox references = page.FindControl<ListBox>("ArchiveReferences")!;
        TabItem tab = page.FindControl<TabItem>("ArchiveReferencesTab")!;
        TabControl tabs = page.FindControl<TabControl>("ArchiveContentTabs")!;
        ArchivePostId origin = new(ThreadId, "42");
        ArchiveQuoteRow[] rows = new[] { "43", "44" }.Select(post =>
            new ArchiveQuoteRow(new ArchiveQuote(origin, new ArchivePostId(ThreadId, post), ">>" + post, null))).ToArray();
        references.ItemsSource = rows;
        tab.IsVisible = true;
        tabs.SelectedItem = tab;
        references.SelectedIndex = 0;
        Layout(window);
        Control first = Assert.IsAssignableFrom<Control>(references.ContainerFromIndex(0));
        Button link = VisibleById<Button>(first, "ArchiveReferenceOpen");
        Assert.True(link.Focus());
        PressKey(window, PhysicalKey.ArrowDown);
        Assert.Same(rows[1], references.SelectedItem);
        Assert.Same(tab, tabs.SelectedItem);
        Assert.Equal("42", page.CurrentReaderPostId);
    });

    [Fact]
    public void FooterPreviewReturnsKeyboardFocusToTheRevisionItOpened() => CheckFooterPreviewFocus();

    internal static void CheckFooterPreviewFocus(Action<ActionToolBar, Button>? reduce = null) => RunWithShell(async (page, window, _, _) =>
    {
        page.ArchiveColumnModePreference = WorkViewSetting.AdvancedName;
        SelectTab(window, ById<TabItem>(window, "ArchiveMediaTab"));
        ListBox tiles = ById<ListBox>(window, "ArchiveMediaTiles");
        ArchiveAssetRow row = tiles.Items.OfType<ArchiveAssetRow>().Single(item => !item.Asset.IsCurrent);
        Control lead = Assert.IsAssignableFrom<Control>(tiles.ContainerFromItem(row));
        Click(window, lead);
        Button preview = VisibleById<Button>(window, "PreviewArchiveMediaButton");
        if (reduce == null) Click(window, preview);
        else
        {
            Assert.False(preview.Focusable);
            reduce(ById<ActionToolBar>(window, "ReaderMediaActions"), preview);
            // A reduced command invokes the original action while its toolbar presentation is closed.
            preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        await BooruTestSupport.WaitAsync(() => window.GetVisualDescendants().OfType<GalleryViewer>()
            .Any(viewer => viewer.IsEffectivelyVisible && viewer.CurrentEntry?.Asset.AssetId == row.Asset.AssetId), "the selected revision in the gallery");
        PressKey(window, PhysicalKey.Escape);
        await BooruTestSupport.WaitAsync(() =>
        {
            Layout(window);
            return tiles.ContainerFromItem(row) is Control restored && restored.IsFocused;
        }, "keyboard focus on the saved media tile after closing the gallery");
        Assert.Same(row, tiles.SelectedItem);
    });

    [Fact]
    public void TagEditorRejectsThePendingRevisionAndWritesOnlyAfterItIsShown() => RunWithShell(async (page, window, host, subjects) =>
    {
        long now = 0;
        page.UseManualReaderTicks(() => now);
        TagId existing = (await host.DefinePrivateTagAsync("existing-tag")).Tag;
        await host.SetThreadTagAsync(existing, ThreadId, true);
        ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
        ListBox tiles = page.FindControl<ListBox>("ArchiveMediaTiles")!;
        TabControl tabs = page.FindControl<TabControl>("ArchiveContentTabs")!;
        ReaderMediaSidebar sidebar = Assert.IsType<ReaderMediaSidebar>(page.MediaSidebar);
        posts.SelectedIndex = 0;
        await ShowSidebarSelectionAsync(page, window, sidebar, "post.png");
        ArchiveAssetRow[] rows = ShownRows(page, grid: true);
        ArchiveAssetRow a = rows.Single(row => Path.GetFileName(row.Asset.Path) == "a.png");
        ArchiveAssetRow b = rows.Single(row => Path.GetFileName(row.Asset.Path) == "b.png");
        tiles.SelectedItem = a;
        tabs.SelectedIndex = 1;
        await ShowSidebarSelectionAsync(page, window, sidebar, a.Version);
        Button edit = SidebarEditButton(window, sidebar);

        PrivateTagStoreSnapshot before = ReadTags(host);
        tiles.SelectedItem = b;
        Layout(window);
        Assert.Contains(a.Version, sidebar.HeaderLine);
        Click(window, edit);
        Assert.Null(window.ArchiveTagPopover);
        Assert.Null(QuickTagPopover.Current);
        AssertTagsUnchanged(host, before);

        now += ReaderMediaSidebarRules.FollowIntervalMs;
        await ShowSidebarSelectionAsync(page, window, sidebar, b.Version);
        QuickTagPopover shown = OpenSidebarEditor(window, sidebar);
        IdentitySuggestionBox input = ById<IdentitySuggestionBox>(shown.Frame, "ArchiveTagPopoverInput");
        EnterTag(window, input, "chosen-revision");
        await BooruTestSupport.WaitAsync(() => ReadTags(host).TagsOf(subjects['b']).Any(tag => tag.Name.Display == "chosen-revision"),
            "a new tag on the shown revision");
        await BooruTestSupport.WaitAsync(() => !shown.Surface!.Session.Busy && shown.Surface.Shown.Any(tag => tag.Id == existing),
            "the existing tag suggestion");
        Layout(Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(shown.Frame)));
        int suggestion = shown.Surface!.Shown.ToList().FindIndex(tag => tag.Id == existing) + 1;
        Button offered = ById<Button>(shown.Frame, "ArchiveTagPopoverSuggestion" + suggestion);
        Assert.True(offered.IsEffectivelyVisible && offered.IsEffectivelyEnabled);
        Assert.IsAssignableFrom<IInvokeProvider>(ControlAutomationPeer.CreatePeerForElement(offered)).Invoke();
        await BooruTestSupport.WaitAsync(() => ReadTags(host).Carries(existing, subjects['b']), "the existing tag on the shown revision");
        PrivateTagStoreSnapshot after = ReadTags(host);
        Assert.Empty(after.TagsOf(subjects['a']));
        Assert.Empty(after.TagsOf(subjects['c']));
        Assert.Equal(new[] { "chosen-revision", "existing-tag" }, after.TagsOf(subjects['b']).Select(tag => tag.Name.Display).Order());
    });

    [Fact]
    public void SidebarTagsEditTheShownPostInTheReferencesTab() => RunWithShell(async (page, window, host, subjects) =>
    {
        long now = 0;
        page.UseManualReaderTicks(() => now);
        ReaderMediaSidebar sidebar = Assert.IsType<ReaderMediaSidebar>(page.MediaSidebar);
        TabControl tabs = ById<TabControl>(window, "ArchiveContentTabs");
        page.FindControl<ListBox>("ArchivePosts")!.SelectedIndex = 0;
        TabItem references = await PopulateReferencesAsync(page, window, host);
        SelectTab(window, references);
        Assert.Same(references, tabs.SelectedItem);
        now += ReaderMediaSidebarRules.FollowIntervalMs;
        await ShowSidebarSelectionAsync(page, window, sidebar, "post.png");

        QuickTagPopover editor = OpenSidebarEditor(window, sidebar);
        EnterTag(window, ById<IdentitySuggestionBox>(editor.Frame, "ArchiveTagPopoverInput"), "reference-post");
        await BooruTestSupport.WaitAsync(() => ReadTags(host).TagsOf(subjects['c']).Any(tag => tag.Name.Display == "reference-post"),
            "the tag on the post shown beside References");
        Assert.Empty(ReadTags(host).TagsOf(subjects['a']));
        Assert.Empty(ReadTags(host).TagsOf(subjects['b']));
        editor.Close();
        Layout(window);
        FocusSidebarFrame(sidebar);
        long revision = (await host.LoadPostFiltersAsync()).Revision;
        PressKey(window, PhysicalKey.H);
        await page.PostFilterTask;
        Assert.Equal(revision, (await host.LoadPostFiltersAsync()).Revision);
    });

    [Theory]
    [InlineData(false, true, null)]
    [InlineData(true, true, null)]
    [InlineData(false, false, null)]
    [InlineData(true, false, null)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    public void SidebarTagWriteIsRevokedDuringLocalValidation(bool advanced, bool openGallery, bool? advanceDuringValidation) =>
        RunWithShell(async (page, window, host, subjects) =>
        {
            page.UseManualReaderTicks(static () => 0);
            SelectTab(window, ById<TabItem>(window, "ArchiveMediaTab"));
            ListBox tiles = ById<ListBox>(window, "ArchiveMediaTiles");
            ArchiveAssetRow selected = tiles.ItemsSource!.Cast<ArchiveAssetRow>().Single(row => Path.GetFileName(row.Asset.Path) == "b.png");
            tiles.SelectedItem = selected;
            ReaderMediaSidebar sidebar = Assert.IsType<ReaderMediaSidebar>(page.MediaSidebar);
            await ShowSidebarSelectionAsync(page, window, sidebar, selected.Version);
            QuickTagPopover editor = OpenSidebarEditor(window, sidebar);
            MediaTagDialog? dialog = null;
            if (advanced)
            {
                OpenAdvancedEditor(editor);
                await BooruTestSupport.WaitAsync(() => window.GalleryMediaTagWindow?.IsVisible == true, "the advanced tag dialog");
                dialog = window.GalleryMediaTagWindow!;
            }

            TaskCompletionSource<(LocalMediaObservation Observation, CancellationToken Token)> validating =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<IReadOnlyList<LocalMediaCheck>> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            host.LocalMediaChecksForTests = (observations, token) =>
            {
                validating.SetResult((Assert.Single(observations), token));
                return release.Task;
            };

            PrivateTagStoreSnapshot before = ReadTags(host);
            try
            {
                if (dialog != null)
                {
                    dialog.PlatformImpl!.Activated?.Invoke();
                    EnterTag(dialog, dialog.Input, "covered-write");
                }
                else EnterTag(window, ById<IdentitySuggestionBox>(editor.Frame, "ArchiveTagPopoverInput"), "covered-write");
                (LocalMediaObservation observation, CancellationToken token) = await validating.Task.WaitAsync(BooruComponentDispatcher.Cancellation);
                Assert.Equal(Assert.IsType<MediaTagSubject.Local>(subjects['b']).Observation, observation);

                if (advanceDuringValidation is bool openAdvanced)
                {
                    bool revokedAtClose = false;
                    editor.Closed += _ =>
                    {
                        revokedAtClose = token.IsCancellationRequested;
                        // Finish local validation before Closed returns and the editor's Result can resume its awaiters.
                        release.TrySetResult([LocalMediaCheck.Verified]);
                    };
                    if (openAdvanced) OpenAdvancedEditor(editor);
                    else
                    {
                        TopLevel root = Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(editor.Frame));
                        // Esc clears the draft before it closes the editor.
                        PressKey(root, PhysicalKey.Escape);
                        if (editor.IsOpen) PressKey(root, PhysicalKey.Escape);
                    }
                    QuickTagPopoverCloseReason expected = openAdvanced ? QuickTagPopoverCloseReason.Advanced : QuickTagPopoverCloseReason.Closed;
                    Assert.Equal(expected, await editor.Result.WaitAsync(BooruComponentDispatcher.Cancellation));
                    Assert.True(revokedAtClose);
                    if (openAdvanced)
                        await BooruTestSupport.WaitAsync(() => window.GalleryMediaTagWindow?.IsVisible == true, "the advanced tag dialog after revocation");
                }
                if (openGallery)
                {
                    await window.OpenGalleryAsync(ThreadId);
                    Assert.True(page.IsObscuredByGallery);
                }
                else
                {
                    page.RequestExternalLink(new Uri("https://offline.invalid/confirmation"));
                    Assert.True(page.IsExternalLinkDialogOpen);
                }
                Assert.True(token.IsCancellationRequested);
            }
            finally { release.TrySetResult([LocalMediaCheck.Verified]); }

            await BooruTestSupport.WaitAsync(() => dialog == null ? !editor.Surface!.Session.Busy : !dialog.Session.Busy,
                "the rejected host write to finish");
            Assert.False(dialog?.IsVisible ?? editor.IsOpen);
            Assert.False(window.GalleryMediaTagWindow?.IsVisible == true);
            AssertTagsUnchanged(host, before);
            Assert.All(subjects.Values, subject => Assert.Empty(ReadTags(host).TagsOf(subject)));
        });

    [Fact]
    public void HideShortcutOnlyChangesTheStoreInThePostsTab() => Run((page, window) =>
    {
        using TempDirectory profile = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        (PostFilterSession filters, PostFilterStore store) = PostFilterDialogModelTests.Open(lease, profile.Path);
        BooruComponentDispatcher.Wait(filters.LoadAsync());
        page.PostFilters = filters;
        page.MediaTilesPreference = true;
        TabControl tabs = page.FindControl<TabControl>("ArchiveContentTabs")!;
        ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
        ListBox tiles = page.FindControl<ListBox>("ArchiveMediaTiles")!;
        tabs.SelectedIndex = 0;
        posts.SelectedIndex = 0;
        ArchivePostRow selected = Assert.IsType<ArchivePostRow>(posts.SelectedItem);
        tabs.SelectedIndex = 1;
        ArchiveAssetRow saved = ShownRows(page, grid: true).Single(row => row.Asset.IsCurrent);
        tiles.SelectedItem = saved;
        Layout(window);
        Assert.Same(selected, posts.SelectedItem);
        ReaderMediaSidebar sidebar = Assert.IsType<ReaderMediaSidebar>(page.MediaSidebar);
        WaitUntil(() => sidebar.HeaderLine.Contains(saved.Version, StringComparison.Ordinal) && sidebar.Close.IsEffectivelyVisible);
        Control frame = FocusSidebarFrame(sidebar);
        Assert.True(frame.IsFocused);
        long before = store.Load().Revision;
        PressKey(window, PhysicalKey.H);
        BooruComponentDispatcher.Wait(page.PostFilterTask);
        Assert.Equal(before, store.Load().Revision);
        Assert.False(filters.IsHidden(ThreadId, selected.Id));

        tabs.SelectedIndex = 0;
        posts.ScrollIntoView(selected);
        Layout(window);
        Assert.Same(selected, posts.SelectedItem);
        ListBoxItem card = Assert.IsAssignableFrom<ListBoxItem>(posts.ContainerFromItem(selected));
        Assert.True(card.Focus(NavigationMethod.Tab));
        Assert.True(card.IsFocused);
        PressKey(window, PhysicalKey.H);
        BooruComponentDispatcher.Wait(page.PostFilterTask);
        Assert.True(filters.IsHidden(ThreadId, selected.Id));
        Assert.True(store.Load().Revision > before);
    });

    [Fact]
    public void FavoriteKeysAndMiddleClickFollowTheShownSavedRevision()
    {
        List<string> opened = [];
        Run((page, window) =>
        {
            page.MediaTilesPreference = true;
            Layout(window);
            ListBox tiles = ById<ListBox>(window, "ArchiveMediaTiles");
            ReaderMediaSidebar sidebar = Assert.IsType<ReaderMediaSidebar>(page.MediaSidebar);
            page.OpenMediaExternallyAsync = entry => { opened.Add(entry.Asset.Path); return Task.CompletedTask; };
            ArchiveAssetRow first = ShownRows(page, grid: true).Single(row => row.Asset.IsCurrent);
            ArchiveAssetRow second = ShownRows(page, grid: true).Single(row => !row.Asset.IsCurrent);
            FavoriteAssetKey firstKey = FavoriteAssetKey.From(ThreadId, first.Asset);
            FavoriteAssetKey secondKey = FavoriteAssetKey.From(ThreadId, second.Asset);
            HashSet<FavoriteAssetKey> favorites = [secondKey];
            List<(FavoriteAssetKey, bool)> writes = [];
            page.IsArchiveFavorite = entry => favorites.Contains(FavoriteAssetKey.From(entry.Key.Thread, entry.Asset));
            page.ToggleArchiveFavoriteAsync = _ => throw new InvalidOperationException("Expected an explicit favorite write.");
            page.SetArchiveFavoriteAsync = (entry, value) =>
            {
                FavoriteAssetKey key = FavoriteAssetKey.From(entry.Key.Thread, entry.Asset);
                writes.Add((key, value));
                if (value) favorites.Add(key);
                else favorites.Remove(key);
                return Task.CompletedTask;
            };

            long now = 0;
            page.UseManualReaderTicks(() => now);
            tiles.SelectedItem = first;
            page.TickMediaSidebarFollow();
            WaitUntil(() => sidebar.Load.ShownSource == new PixelSize(17, 11));
            // W112-72 §§1,3: “Die Leisten nehmen im Overlay nie Layoutplatz”.
            sidebar.ConfigureInfo(ReaderMediaPreferences.Default with { MediaSidebarAutoHide = true });
            FocusSidebarFrame(sidebar);
            Layout(window);
            Control overlay = ById<Control>(sidebar, "ReaderMediaSidebarOverlay");
            Image picture = ById<Image>(sidebar, "ReaderMediaSidebarPicture");
            Point imagePoint = HitPoint(window, picture);
            Assert.False(overlay.IsVisualAncestorOf(Assert.IsAssignableFrom<Visual>(window.InputHitTest(imagePoint))));
            PressKey(window, PhysicalKey.M);
            BooruComponentDispatcher.Wait(page.FavoriteKeyTask);
            MiddleClick(imagePoint, imagePoint);
            Assert.Equal(first.Asset.Path, Assert.Single(opened));
            sidebar.RevealBars();
            Point overlayPoint = HitPoint(window, overlay);
            MiddleClick(overlayPoint, overlayPoint);
            Assert.Single(opened);
            MiddleClick(imagePoint, overlayPoint);
            Assert.Single(opened);

            window.MouseDown(imagePoint, MouseButton.Middle, RawInputModifiers.None);
            tiles.SelectedItem = second;
            Layout(window);
            Assert.Contains(first.Version, sidebar.HeaderLine);
            favorites.Clear();
            PressKey(window, PhysicalKey.M);
            BooruComponentDispatcher.Wait(page.FavoriteKeyTask);
            favorites.UnionWith([firstKey, secondKey]);
            PressKey(window, PhysicalKey.U);
            BooruComponentDispatcher.Wait(page.FavoriteKeyTask);
            Assert.Single(writes);

            now += ReaderMediaSidebarRules.FollowIntervalMs;
            page.TickMediaSidebarFollow();
            WaitUntil(() => sidebar.Load.ShownSource == new PixelSize(19, 13));
            Layout(window);
            Point secondImagePoint = HitPoint(window, picture);
            window.MouseUp(secondImagePoint, MouseButton.Middle, RawInputModifiers.None);
            Assert.Single(opened);
            FocusSidebarFrame(sidebar);
            PressKey(window, PhysicalKey.U);
            BooruComponentDispatcher.Wait(page.FavoriteKeyTask);
            MiddleClick(secondImagePoint, secondImagePoint);

            Assert.Equal(new[] { (firstKey, true), (secondKey, false) }, writes);
            Assert.Equal(firstKey, Assert.Single(favorites));
            Assert.Equal(new[] { first.Asset.Path, second.Asset.Path }, opened);

            void MiddleClick(Point press, Point release)
            {
                window.MouseDown(press, MouseButton.Middle, RawInputModifiers.None);
                window.MouseUp(release, MouseButton.Middle, RawInputModifiers.None);
                Layout(window);
            }
        });
    }

    private static async Task ShowSidebarSelectionAsync(ArchivePage page, Window window, ReaderMediaSidebar sidebar, string label)
    {
        page.TickMediaSidebarFollow();
        await page.MediaSidebarTask;
        Layout(window);
        Assert.Contains(label, sidebar.HeaderLine);
        Assert.True(sidebar.Load.HasPicture);
    }

    private static async Task<TabItem> PopulateReferencesAsync(ArchivePage page, Window window, DesktopSessionHost host)
    {
        await using ArchiveReader reader = await host.OpenArchiveReaderAsync();
        ArchiveQuotePage backlinks = await reader.GetBacklinksAsync(new(ThreadId, "42"));
        ArchiveQuote backlink = Assert.Single(backlinks.Items);
        Assert.Equal("43", backlink.Origin.PostId);
        TabItem tab = ById<TabItem>(window, "ArchiveReferencesTab");
        // The fixture supplies the captured reply to the retained References view.
        page.FindControl<ListBox>("ArchiveReferences")!.ItemsSource = new[] { new ArchiveQuoteRow(backlink, true) };
        tab.IsVisible = true;
        Layout(window);
        return tab;
    }

    private static PrivateTagStoreSnapshot ReadTags(DesktopSessionHost host) =>
        Assert.IsType<PrivateTagStoreSnapshot>(host.GetPublishedPrivateTagSnapshot().Snapshot);

    private static void AssertTagsUnchanged(DesktopSessionHost host, PrivateTagStoreSnapshot before)
    {
        PrivateTagStoreSnapshot after = Assert.IsType<PrivateTagStoreSnapshot>(host.GetPrivateTagSnapshot().Snapshot);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.Fingerprint, after.Fingerprint);
        Assert.Equal(before.LocalAssignments, after.LocalAssignments);
        Assert.Equal(before.MediaAssignments, after.MediaAssignments);
    }

    private static Button SidebarEditButton(Window window, ReaderMediaSidebar sidebar)
    {
        Layout(window);
        if (sidebar.InfoToggle.IsChecked != true)
            Click(window, VisibleById<ToggleButton>(sidebar, "ReaderMediaSidebarInfoToggle"));
        Button edit = VisibleById<Button>(sidebar, "ReaderMediaSidebarEditTags");
        edit.BringIntoView();
        Layout(window);
        Assert.True(edit.IsEffectivelyVisible);
        return edit;
    }

    private static QuickTagPopover OpenSidebarEditor(MainWindow window, ReaderMediaSidebar sidebar)
    {
        Click(window, SidebarEditButton(window, sidebar));
        QuickTagPopover editor = Assert.IsType<QuickTagPopover>(window.ArchiveTagPopover);
        Assert.Same(editor, QuickTagPopover.Current);
        Assert.True(editor.IsOpen);
        return editor;
    }

    private static T VisibleById<T>(Visual root, string id) where T : Control =>
        Assert.Single(root.GetVisualDescendants().OfType<T>(), control =>
            control.IsEffectivelyVisible && AutomationProperties.GetAutomationId(control) == id);

    private static void OpenAdvancedEditor(QuickTagPopover editor)
    {
        Button button = ById<Button>(editor.Frame, "ArchiveTagPopoverAdvanced");
        Click(Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(button)), button);
    }

    private static T ById<T>(Visual root, string id, ArchiveAssetRow? row = null) where T : Control =>
        Assert.Single(root.GetVisualDescendants().OfType<T>(), control => AutomationProperties.GetAutomationId(control) == id
            && (row == null || ReferenceEquals(control.DataContext, row)));

    private static void SelectTab(Window window, TabItem tab)
    {
        Assert.True(tab.IsEffectivelyVisible);
        Assert.IsAssignableFrom<ISelectionItemProvider>(ControlAutomationPeer.CreatePeerForElement(tab)).Select();
        Layout(window);
    }

    private static Control FocusSidebarFrame(ReaderMediaSidebar sidebar)
    {
        // Kürzelschema §Vorrang: “Panelknöpfe nehmen in 1.1.2 keinen Fokus.”
        Control frame = VisibleById<Control>(sidebar, "ReaderMediaSidebarFrame");
        Assert.True(frame.Focus());
        Assert.True(frame.IsFocused);
        return frame;
    }

    private static void Click(TopLevel root, Control control, RawInputModifiers modifiers = RawInputModifiers.None, Point? local = null)
    {
        Point point = HitPoint(root, control, local);
        root.MouseDown(point, MouseButton.Left, modifiers);
        root.MouseUp(point, MouseButton.Left, modifiers);
        Layout(root);
    }

    private static Point HitPoint(TopLevel root, Control control, Point? local = null)
    {
        Assert.True(control.IsEffectivelyVisible);
        Assert.True(control.IsEffectivelyEnabled);
        Layout(root);
        Point point = default;
        WaitUntil(() =>
        {
            root.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            point = control.TranslatePoint(local ?? new Rect(control.Bounds.Size).Center, root)!.Value;
            return root.InputHitTest(point) is Visual hit && (ReferenceEquals(hit, control) || control.IsVisualAncestorOf(hit));
        });
        Visual target = Assert.IsAssignableFrom<Visual>(root.InputHitTest(point));
        Assert.True(ReferenceEquals(target, control) || control.IsVisualAncestorOf(target),
            $"Expected {AutomationProperties.GetAutomationId(control)} at {point}; hit {target}.");
        return point;
    }

    private static void EnterTag(TopLevel window, IdentitySuggestionBox input, string tag)
    {
        Click(Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(input)), input);
        Assert.True(input.IsKeyboardFocusWithin);
        window.KeyTextInput(tag);
        Assert.Equal(tag, input.Text);
        PressKey(window, PhysicalKey.Enter);
    }

    // Help.md, "In a thread's media tab you select several files as on the Media page: Ctrl/Cmd+click".
    private static void ToggleSelection(Window window, Control row, Point position, bool macOS)
    {
        KeyModifiers command = macOS ? KeyModifiers.Meta : KeyModifiers.Control;
        AvaloniaLocator.CurrentMutable.Bind<PlatformHotkeyConfiguration>().ToConstant(new PlatformHotkeyConfiguration(command));
        bool? previous = ContextClick.MacOSForTests;
        try
        {
            ContextClick.MacOSForTests = macOS;
            Click(window, row, macOS ? RawInputModifiers.Meta : RawInputModifiers.Control, position);
        }
        finally { ContextClick.MacOSForTests = previous; }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ClicksArrowsAndMultiSelectionMakeTheOpenSidebarFollowTheExactRevision(bool grid, bool macOS) => Run((page, window) =>
    {
        ListBox tiles = ById<ListBox>(window, "ArchiveMediaTiles");
        DataGrid table = ById<DataGrid>(window, "ArchiveMediaGrid");
        Assert.Equal(grid, tiles.IsEffectivelyVisible);
        Assert.Equal(!grid, table.IsEffectivelyVisible);
        ArchiveAssetRow[] rows = (grid ? tiles.ItemsSource : table.ItemsSource)!.Cast<ArchiveAssetRow>().ToArray();
        Assert.Equal(rows[0].Name, rows[1].Name);
        ReaderMediaSidebar sidebar = Assert.IsType<ReaderMediaSidebar>(page.MediaSidebar);
        TextBlock heading = ById<TextBlock>(window, "ReaderMediaSidebarHeading");
        Control Target(ArchiveAssetRow row) => grid ? ById<ListBoxItem>(tiles, "ArchiveMediaTile", row)
            : ById<DataGridRow>(table, "ArchiveMediaTableRow", row);
        void AssertShown(int index, PixelSize size) => WaitUntil(() => heading.IsEffectivelyVisible
            && heading.Text == rows[index].Version && sidebar.Load.ShownSource == size);

        Assert.True(page.IsMediaSidebarShown);
        Control first = Target(rows[0]);
        Point rowPoint = new(16, grid ? 32 : first.Bounds.Height / 2);
        Click(window, first, local: rowPoint);
        Assert.Equal(0, grid ? tiles.SelectedIndex : table.SelectedIndex);
        AssertShown(0, new(17, 11));
        PressKey(window, grid ? PhysicalKey.ArrowRight : PhysicalKey.ArrowDown);
        Assert.Equal(1, grid ? tiles.SelectedIndex : table.SelectedIndex);
        AssertShown(1, new(19, 13));

        ToggleSelection(window, Target(rows[0]), rowPoint, macOS);
        Assert.Equal(rows, page.SelectedMediaRows());
        if (grid) Assert.True(ById<Border>(Target(rows[0]), "ArchiveMediaSelected").IsEffectivelyVisible);
        AssertShown(0, new(17, 11));

        if (grid) Click(window, ById<Border>(Target(rows[1]), "ArchiveMediaSelected"));
        else Click(window, Target(rows[1]), local: rowPoint);
        Assert.Same(rows[1], grid ? tiles.SelectedItem : table.SelectedItem);
        AssertShown(1, new(19, 13));
        PressKey(window, grid ? PhysicalKey.ArrowLeft : PhysicalKey.ArrowUp);
        Assert.Equal(0, grid ? tiles.SelectedIndex : table.SelectedIndex);
        AssertShown(0, new(17, 11));

        if (!grid)
        {
            DataGridRow row = ById<DataGridRow>(table, "ArchiveMediaTableRow", rows[0]);
            Border background = row.GetVisualDescendants().OfType<Border>().Single(item => item.Name == "BackgroundBorder");
            Assert.Equal(new Thickness(3, 1, 1, 1), background.BorderThickness);
            Visual[] outlines = row.GetVisualDescendants().Where(item => item is Control { Name: "CurrencyVisual" or "FocusVisual" }).ToArray();
            Assert.NotEmpty(outlines);
            Assert.All(outlines, outline => Assert.Equal(0, outline.Opacity));
        }
    }, gridPreference: grid);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void QueueShortcutUsesTheShownRevisionFromTheSidebarAndTheSelectionFromTheList(bool grid, bool macOS) => Run((page, window) =>
    {
        ListBox tiles = ById<ListBox>(window, "ArchiveMediaTiles");
        DataGrid table = ById<DataGrid>(window, "ArchiveMediaGrid");
        Control list = grid ? tiles : table;
        string rowId = grid ? "ArchiveMediaTile" : "ArchiveMediaTableRow";
        ArchiveAssetRow[] rows = (grid ? tiles.ItemsSource : table.ItemsSource)!.Cast<ArchiveAssetRow>().ToArray();
        Control first = ById<Control>(list, rowId, rows[0]);
        Control second = ById<Control>(list, rowId, rows[1]);
        List<string[]> queued = [];
        page.AddToTagQueue = entries =>
        {
            queued.Add(entries.Select(entry => entry.Asset.Path).ToArray());
            return Task.FromResult(string.Empty);
        };

        Click(window, first, local: new Point(16, grid ? 32 : first.Bounds.Height / 2));
        ToggleSelection(window, second, new Point(16, grid ? 32 : second.Bounds.Height / 2), macOS);
        Assert.Equal(rows, page.SelectedMediaRows());
        ReaderMediaSidebar sidebar = Assert.IsType<ReaderMediaSidebar>(page.MediaSidebar);
        TextBlock heading = ById<TextBlock>(sidebar, "ReaderMediaSidebarHeading");
        WaitUntil(() => heading.IsEffectivelyVisible && heading.Text == rows[1].Version);

        Assert.True(list.IsKeyboardFocusWithin);
        PressKey(window, PhysicalKey.Q);
        FocusSidebarFrame(sidebar);
        PressKey(window, PhysicalKey.Q);
        // 73-2: the sidebar's queue key first checks the shown original.
        WaitUntil(() => queued.Count >= 2);
        Assert.Collection(queued,
            paths => Assert.Equal(rows.Select(row => row.Asset.Path), paths),
            paths => Assert.Equal(rows[1].Asset.Path, Assert.Single(paths)));
    }, gridPreference: grid);

    [Theory]
    [InlineData("de", false)]
    [InlineData("fr", true)]
    [InlineData("ru", false)]
    public void FooterSharesALineAndTheFavoriteToggleKeepsItsLabel(string language, bool grid) => Run((page, window) =>
    {
        page.ArchiveColumnModePreference = WorkViewSetting.AdvancedName;
        window.SetRenderScaling(1.5);
        Assert.Equal(1.5, window.RenderScaling);
        Texts.SetLanguage(language);
        BooruComponentDispatcher.Wait(page.ReaderLocalizationTask);
        page.MediaTilesPreference = grid;
        Layout(window);
        ToggleButton favorites = page.FindControl<ToggleButton>("ArchiveFavoritesFilter")!;
        Assert.Equal(Texts.T("archive.favoritesOnly"), favorites.Content);
        IToggleProvider toggle = Assert.IsAssignableFrom<IToggleProvider>(ControlAutomationPeer.CreatePeerForElement(favorites));
        Assert.Equal(ToggleState.Off, toggle.ToggleState);
        CommandBindings.InvokeAction(favorites);
        Layout(window);
        Assert.Equal(Texts.T("archive.favoritesOnly"), favorites.Content);
        Assert.Equal(ToggleState.On, toggle.ToggleState);
        Assert.True(Assert.Single(ShownRows(page, grid)).Asset.IsCurrent);
        toggle.Toggle();
        Layout(window);
        Assert.Equal(Texts.T("archive.favoritesOnly"), favorites.Content);
        Assert.Equal(ToggleState.Off, toggle.ToggleState);
        Assert.Equal(2, ShownRows(page, grid).Length);

        TextBlock total = page.FindControl<TextBlock>("ArchiveThreadMediaTotal")!;
        Assert.True(total.IsEffectivelyVisible);
        Assert.Equal(Texts.T("thread.media.total", 1), total.Text);
        foreach (string name in FooterActions)
        {
            Button action = page.FindControl<Button>(name)!;
            Assert.True(action.IsEffectivelyVisible);
            SameFooterLine(action);
            Assert.True(total.TranslatePoint(new Point(total.Bounds.Width, 0), window)!.Value.X
                <= action.TranslatePoint(default, window)!.Value.X);
        }
        BooruReviewTestPlatform.Resize(window, 760);
        Layout(window);
        SameFooterLine(page.FindControl<ActionToolBar>("ReaderMediaActions")!);

        void SameFooterLine(Control action)
        {
            double CenterY(Control control) => control.TranslatePoint(new Point(0, control.Bounds.Height / 2), window)!.Value.Y;
            Assert.InRange(Math.Abs(CenterY(total) - CenterY(action)), 0, 1);
        }

        page.ReaderNavigationMode = Config.ReaderNavigationModeAlways;
        Assert.False(page.ReaderNavigationShown);
        page.ReaderNavigationMode = Config.ReaderNavigationModeAuto;
        page.NotifyReaderPointerMoved(new Point(100, 100));
        Assert.False(page.ReaderNavigationShown);
        Assert.False(page.FindControl<Control>("ReaderNavigation")!.IsVisible);
        page.FindControl<TabControl>("ArchiveContentTabs")!.SelectedIndex = 0;
        page.ReaderNavigationMode = Config.ReaderNavigationModeAlways;
        Assert.True(page.ReaderNavigationShown);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothViewsCopyTheFullHashOfTheSelectedRevision(bool grid) => Run((page, window) =>
    {
        page.MediaTilesPreference = grid;
        Layout(window);
        ArchiveAssetRow row = ShownRows(page, grid).Single(asset => !asset.Asset.IsCurrent);
        if (grid)
        {
            ListBox tiles = page.FindControl<ListBox>("ArchiveMediaTiles")!;
            tiles.SelectedItem = row;
            Assert.True(((Control)tiles.ContainerFromItem(row)!).Focus(NavigationMethod.Tab));
        }
        else SelectTableRow(window, page.FindControl<DataGrid>("ArchiveMediaGrid")!, row);
        window.KeyPress(Key.F10, RawInputModifiers.Shift, PhysicalKey.F10, null);
        Layout(window);
        MenuItem copy = Assert.IsType<MenuItem>(page.ArchiveMediaMenu!.Items.OfType<MenuItem>()
            .Single(item => Equals(item.Tag, "archive.media.copySha256")));
        Assert.Equal(Texts.T("archive.media.copySha256"), copy.Header);
        Assert.True(copy.Command!.CanExecute(null));
        copy.Command.Execute(null);
        WaitUntil(() => BooruComponentDispatcher.Wait(window.Clipboard!.TryGetTextAsync()) == row.Asset.Sha256);
        Assert.Equal(64, row.Asset.Sha256.Length);
    });

    [Fact]
    public void SavedColumnsKeepTheirOrderAndVisibilityAcrossAViewSwitch() => Run((page, window) =>
    {
        page.ArchiveColumnOrderPreference = new Dictionary<string, int> { ["ArchiveMediaGrid.version"] = 0, ["ArchiveMediaGrid.name"] = 1 };
        page.ArchiveColumnVisibilityPreference = new Dictionary<string, bool> { ["ArchiveMediaGrid.rating"] = false };
        page.ArchiveColumnWidthsPreference = new Dictionary<string, double> { ["ArchiveMediaGrid.1"] = 70, ["ArchiveMediaGrid.version"] = 100 };
        page.MediaTilesPreference = false;
        Layout(window);
        DataGrid table = page.FindControl<DataGrid>("ArchiveMediaGrid")!;
        Assert.Equal(0, table.Columns[2].DisplayIndex);
        Assert.False(table.Columns[3].IsVisible);
        Assert.Equal(88, table.Columns[1].Width.Value);
        Assert.Equal(128, table.Columns[2].Width.Value);
        ArchiveAssetRow row = ShownRows(page, false).Single(asset => asset.Asset.IsCurrent);
        DataGridRow visual = table.GetVisualDescendants().OfType<DataGridRow>()
            .Single(item => ReferenceEquals(item.DataContext, row));
        foreach (string text in new[] { row.Size, row.Version })
        {
            TextBlock cell = visual.GetVisualDescendants().OfType<TextBlock>().Single(item => item.Text == text);
            Assert.Equal(TextWrapping.NoWrap, cell.TextWrapping);
            Assert.True(cell.Bounds.Height > 0);
        }
        Assert.StartsWith("Current", row.Version);
        Assert.StartsWith("Earlier", ShownRows(page, false).Single(asset => !asset.Asset.IsCurrent).Version);
        Assert.Contains("\u00A0KiB", row.TileSummary);
        Assert.Equal(Texts.T("media.fact.byteSize", row.Asset.Length, SizeText.Format(row.Asset.Length)), row.ExactSize);
        page.MediaTilesPreference = true;
        page.MediaTilesPreference = false;
        Assert.Equal(0, table.Columns[2].DisplayIndex);
        Assert.False(table.Columns[3].IsVisible);
    });

    [Fact]
    public void TheMediaPagePanelAlsoFollowsGridArrows() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        MediaPage page = new(_ => throw new InvalidOperationException("Use the fixture snapshot")) { IsAvailable = true };
        Window window = new() { Width = 1440, Height = 840, Content = page };
        try
        {
            MediaLibraryEntry[] entries = [MediaTagCountsTests.Entry(1), MediaTagCountsTests.Entry(2)];
            typeof(MediaPage).GetField("_snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(page, new MediaLibrarySnapshot(entries, [], [], complete: true));
            page.ResetFilters();
            page.ApplyLayout(page.PageLayout with { Panel = true }, save: false);
            window.Show();
            Layout(window);
            Assert.True(page.IsPanelShown);
            MediaLibraryTileRow[] rows = page.Grid.ItemsSource!.Cast<MediaLibraryTileRow>().ToArray();
            Assert.Equal(2, rows.Length);
            page.Grid.SelectedIndex = 0;
            Assert.True(((Control)page.Grid.ContainerFromIndex(0)!).Focus(NavigationMethod.Tab));
            WaitUntil(() => ReferenceEquals(rows[0], page.PanelRow));

            PressKey(window, PhysicalKey.ArrowRight);
            Assert.Equal(1, page.Grid.SelectedIndex);
            WaitUntil(() => ReferenceEquals(rows[1], page.PanelRow));

            PressKey(window, PhysicalKey.ArrowLeft);
            Assert.Equal(0, page.Grid.SelectedIndex);
            WaitUntil(() => ReferenceEquals(rows[0], page.PanelRow));
        }
        finally
        {
            page.EndLifetime();
            window.Close();
        }
    });

    [Fact]
    public void NarrowMediaPageShowsThePanelWhenFocusModeHidesTheToolbar() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        MediaPage page = new(_ => throw new InvalidOperationException("Use the fixture snapshot"))
        {
            IsAvailable = true,
            PanelMinimumPageWidthSource = () => Config.MediaPanelMinimumPageWidthMinimum
        };
        Window window = new() { Width = 832, Height = 700, Content = page };
        try
        {
            typeof(MediaPage).GetField("_snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(page, new MediaLibrarySnapshot([MediaTagCountsTests.Entry(1)], [], [], complete: true));
            page.ResetFilters();
            page.ApplyLayout(page.PageLayout with { Panel = true, TagList = false }, save: false);
            window.Show();
            Layout(window);
            Grid body = page.GetVisualDescendants().OfType<Grid>().Single(grid => grid.Name == "MediaBodyRow");
            Grid content = page.GetVisualDescendants().OfType<Grid>().Single(grid => grid.Name == "MediaContentRow");
            Control toolbar = page.FocusHiddenRows[0];
            double width = body.Bounds.Width;
            Assert.True(toolbar.IsEffectivelyVisible);
            Assert.False(page.IsPanelShown);

            page.ToggleFocusModeByShortcut();
            Layout(window);
            Assert.Equal(width, body.Bounds.Width);
            Assert.False(toolbar.IsEffectivelyVisible);
            Assert.True(page.IsPanelShown);
            ColumnDefinition panel = content.ColumnDefinitions[page.PageLayout.PanelSide == MediaPanelSide.Right ? 2 : 0];
            Assert.True(panel.ActualWidth > panel.MinWidth);
            Assert.True(panel.MaxWidth > panel.ActualWidth);

            page.FocusExitButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout(window);
            Assert.Equal(width, body.Bounds.Width);
            Assert.True(toolbar.IsEffectivelyVisible);
            Assert.False(page.IsPanelShown);
        }
        finally
        {
            page.EndLifetime();
            window.Close();
        }
    });

    private static void SelectTableRow(Window window, DataGrid table, ArchiveAssetRow row)
    {
        DataGridRow visual = table.GetVisualDescendants().OfType<DataGridRow>()
            .Single(item => ReferenceEquals(item.DataContext, row));
        Point position = visual.TranslatePoint(new Point(16, visual.Bounds.Height / 2), window)!.Value;
        window.MouseDown(position, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(position, MouseButton.Left, RawInputModifiers.None);
        Assert.Same(row, table.SelectedItem);
        Assert.True(table.IsKeyboardFocusWithin);
    }

    [Fact]
    public void ArrowsAfterATileFavoriteClickMoveTheSelectionAndTheSidebar() => Run((page, window) =>
    {
        ListBox tiles = page.FindControl<ListBox>("ArchiveMediaTiles")!;
        ArchiveAssetRow[] rows = ShownRows(page, true);
        Control first = (Control)tiles.ContainerFromIndex(0)!;
        Click(window, first);
        Assert.Equal(0, tiles.SelectedIndex);
        ReaderMediaSidebar sidebar = Assert.IsType<ReaderMediaSidebar>(page.MediaSidebar);
        WaitUntil(() => sidebar.HeaderLine.Contains(rows[0].Version, StringComparison.Ordinal)
            && sidebar.Load.ShownSource == new PixelSize(17, 11));

        ToggleButton star = VisibleById<ToggleButton>(first, "ArchiveMediaFavorite");
        bool? before = star.IsChecked;
        Click(window, star);
        Assert.NotEqual(before, star.IsChecked);
        Assert.False(star.IsFocused);
        Assert.True(first.IsFocused);
        PressKey(window, PhysicalKey.ArrowRight, RawInputModifiers.Meta);
        Assert.Equal(0, tiles.SelectedIndex);
        Assert.True(first.IsFocused);
        PressKey(window, PhysicalKey.ArrowRight);
        Assert.Equal(1, tiles.SelectedIndex);
        WaitUntil(() => sidebar.HeaderLine.Contains(rows[1].Version, StringComparison.Ordinal)
            && sidebar.Load.ShownSource == new PixelSize(19, 13));
    });

    [Fact]
    public void ArrowsPressedBeforeTheLoadFinishesEndOnTheLatestRevision() => Run((page, window) =>
    {
        long now = Environment.TickCount64;
        page.UseManualReaderTicks(() => now);
        using WriteableBitmap first = Picture(17, 11), older = Picture(19, 13), latest = Picture(23, 15);
        TaskCompletionSource<ReaderMediaSidebarContent.Picture> oldDecode = new(), latestDecode = new();
        List<Bitmap> released = [];
        ListBox tiles = page.FindControl<ListBox>("ArchiveMediaTiles")!;
        ArchiveAssetRow[] rows = ShownRows(page, true);
        ReaderMediaSidebar sidebar = Assert.IsType<ReaderMediaSidebar>(page.MediaSidebar);
        Control frame = VisibleById<Control>(sidebar, "ReaderMediaSidebarFrame");
        Image image = Assert.Single(frame.GetVisualDescendants().OfType<Image>(), child => ReferenceEquals(child, sidebar.Picture));
        int started = -1;
        page.SavedSidebarImageLoaderForTests = (entry, _, _) =>
        {
            started = Array.FindIndex(rows, row => row.Asset.AssetId == entry.Asset.AssetId);
            return started switch
            {
                0 => Task.FromResult(Content(first)),
                1 => oldDecode.Task,
                2 => latestDecode.Task,
                _ => throw new InvalidOperationException("Unexpected saved revision")
            };
        };
        try
        {
            Click(window, (Control)tiles.ContainerFromIndex(0)!);
            FollowSelection();
            WaitUntil(() => started == 0 && ReferenceEquals(image.Source, first));
            AssertShown(0, first);

            PressKey(window, PhysicalKey.ArrowRight);
            FollowSelection();
            WaitUntil(() => started == 1);
            Task oldLoad = page.MediaSidebarTask;
            AssertPending(1);

            PressKey(window, PhysicalKey.ArrowRight);
            FollowSelection();
            WaitUntil(() => started == 2);
            AssertPending(2);
            Assert.False(oldLoad.IsCompleted);

            latestDecode.SetResult(Content(latest));
            BooruComponentDispatcher.Wait(page.MediaSidebarTask);
            AssertShown(2, latest);
            Assert.False(oldLoad.IsCompleted);

            oldDecode.SetResult(Content(older));
            BooruComponentDispatcher.Wait(oldLoad);
            Assert.Equal(1, released.Count(bitmap => ReferenceEquals(bitmap, older)));
            AssertShown(2, latest);
        }
        finally { sidebar.ReleasePicture(); }

        void FollowSelection()
        {
            now += ReaderMediaSidebarRules.FollowIntervalMs;
            page.TickMediaSidebarFollow();
        }

        ReaderMediaSidebarContent.Picture Content(Bitmap bitmap) => new(bitmap, released.Add, false, bitmap.PixelSize);
        static WriteableBitmap Picture(int width, int height) =>
            new(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

        void AssertPending(int index)
        {
            Assert.Same(rows[index], tiles.SelectedItem);
            Assert.Contains(rows[index].Name, sidebar.HeaderLine, StringComparison.Ordinal);
            Assert.Same(first, image.Source);
            Assert.True(sidebar.IsDimmed);
            Assert.True(sidebar.Load.Awaiting);
        }

        void AssertShown(int index, Bitmap expected)
        {
            Assert.Same(rows[index], tiles.SelectedItem);
            Assert.Contains(rows[index].Name, sidebar.HeaderLine, StringComparison.Ordinal);
            Assert.Contains(rows[index].Version, sidebar.HeaderLine, StringComparison.Ordinal);
            Assert.Same(expected, image.Source);
            Assert.Equal(expected.PixelSize, sidebar.Load.ShownSource);
            Assert.False(sidebar.IsDimmed);
            Assert.False(sidebar.Load.Awaiting);
        }
    }, additionalRevisions: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavedMediaFooterKeepsTheRangeLeadInBothViews(bool tilesShown) => Run((page, window) =>
    {
        int clicks = 0;
        page.ManageMediaRatingsAsync = () => { clicks++; return Task.CompletedTask; };
        page.ArchiveColumnModePreference = WorkViewSetting.AdvancedName;
        page.MediaTilesPreference = tilesShown;
        Layout(window);
        ListBox tiles = page.FindControl<ListBox>("ArchiveMediaTiles")!;
        DataGrid grid = page.FindControl<DataGrid>("ArchiveMediaGrid")!;
        Control list = tilesShown ? tiles : grid;
        ArchiveAssetRow[] rows = ShownRows(page, tilesShown);
        if (tilesShown) Click(window, (Control)tiles.ContainerFromIndex(0)!);
        else SelectTableRow(window, grid, rows[0]);
        PhysicalKey next = tilesShown ? PhysicalKey.ArrowRight : PhysicalKey.ArrowDown;
        PressKey(window, next, RawInputModifiers.Shift);
        PressKey(window, next, RawInputModifiers.Shift);
        Assert.Equal(rows[..3], page.SelectedMediaRows());

        ReaderMediaSidebar sidebar = Assert.IsType<ReaderMediaSidebar>(page.MediaSidebar);
        WaitUntil(() => sidebar.HeaderLine.Contains(rows[2].Name, StringComparison.Ordinal));
        Button manage = VisibleById<Button>(window, "ArchiveManageRatingsButton");
        Assert.False(manage.Focusable);
        Assert.False(manage.IsTabStop);
        Click(window, manage);
        Assert.Equal(1, clicks);
        Assert.True(list.IsKeyboardFocusWithin);
        if (tilesShown) Assert.True(((Control)tiles.ContainerFromItem(rows[2])!).IsFocused);
        else Assert.Same(rows[2], Assert.IsType<RowFocusDataGrid>(grid).CurrentRowItem);

        PressKey(window, next, RawInputModifiers.Shift);
        Assert.Equal(rows, page.SelectedMediaRows());
        WaitUntil(() => sidebar.HeaderLine.Contains(rows[3].Name, StringComparison.Ordinal)
            && sidebar.Load.ShownSource == new PixelSize(19, 13) && !sidebar.Load.Awaiting && !sidebar.IsDimmed);
        Assert.Equal("42", page.CurrentReaderPostId);
        Assert.Equal(1, page.FindControl<TabControl>("ArchiveContentTabs")!.SelectedIndex);

        PressKey(window, PhysicalKey.Tab);
        Assert.NotSame(manage, window.FocusManager!.GetFocusedElement());
        PressKey(window, PhysicalKey.Enter);
        Assert.Equal(1, clicks);
        Click(window, manage);
        PressKey(window, PhysicalKey.F10, RawInputModifiers.Shift);
        ContextMenu menu = Assert.IsType<ContextMenu>(page.ArchiveMediaMenu);
        MenuItem manageFromMenu = VisibleById<MenuItem>(menu, "ArchiveManageRatingsMenu");
        Assert.True(manageFromMenu.Focus());
        PressKey(window, PhysicalKey.Enter);
        Assert.Equal(3, clicks);
    }, additionalRevisions: true);

    private static ArchiveAssetRow[] ShownRows(ArchivePage page, bool grid) =>
        (grid ? page.FindControl<ListBox>("ArchiveMediaTiles")!.ItemsSource : page.FindControl<DataGrid>("ArchiveMediaGrid")!.ItemsSource)!
        .Cast<ArchiveAssetRow>().ToArray();

    private static void Run(Action<ArchivePage, Window> test, Action<string>? openFolder = null,
        bool additionalRevisions = false, bool withNavigation = false, bool? gridPreference = null) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        Application.Current!.Styles.Insert(1, new DataGridSemiTheme());
        using TempDirectory temp = new();
        (ArchivedThreadView thread, MediaAssetSnapshot manifest) = CreateFixture(temp.Path, additionalRevisions: additionalRevisions);
        HashSet<string> favorites = [manifest.GetPage().Single(asset => Path.GetFileName(asset.Path) == "a.png").AssetId];
        if (withNavigation) BooruComponentDispatcher.Wait(CaptureNextThreadAsync());
        ArchivePage page = new(token => withNavigation ? ArchiveReader.OpenAsync(temp.Path, token)
                : throw new InvalidOperationException("The prepared thread should be used"),
            (_, _) => Task.CompletedTask, openFolder ?? (_ => { }), (_, _) => Task.FromResult(manifest))
        {
            IsAvailable = true,
            IsArchiveFavorite = entry => favorites.Contains(entry.Asset.AssetId),
            ToggleArchiveFavoriteAsync = entry =>
            {
                if (!favorites.Add(entry.Asset.AssetId)) favorites.Remove(entry.Asset.AssetId);
                return Task.CompletedTask;
            }
        };
        if (gridPreference is bool grid) page.MediaTilesPreference = grid;
        Window window = new() { Width = 1440, Height = 840 };
        try
        {
            BooruComponentDispatcher.Wait(page.LoadThreadAsync(ThreadId, preparedThread: thread));
            BooruComponentDispatcher.Wait(page.MediaLoadTask);
            window.Content = page.DetachReader();
            window.Show();
            window.PlatformImpl!.Activated?.Invoke();
            Layout(window);
            SelectTab(window, ById<TabItem>(window, "ArchiveMediaTab"));
            test(page, window);
        }
        finally
        {
            page.EndLifetime();
            window.Close();
        }

        async Task CaptureNextThreadAsync()
        {
            using ProfileLease lease = ProfileLease.Acquire(temp.Path);
            await using ArchiveCatalog archive = await ArchiveCatalog.OpenAsync(temp.Path, lease);
            await archive.CaptureAsync(new CapturedThreadSnapshot(NextThreadId, "https://offline.invalid/g/thread/50", "Next",
                ThreadSourceState.Active, DateTimeOffset.UnixEpoch, temp.Path,
                [new CapturedPost("50", 50, PostFieldPresence.All, Html: "Next thread")]));
        }
    });

    internal static void RunWithShell(Func<ArchivePage, MainWindow, DesktopSessionHost, IReadOnlyDictionary<char, MediaTagSubject>, Task> test,
        bool replyHasMedia = false) =>
        BooruComponentDispatcher.Run(() =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            Application.Current!.Styles.Insert(1, new DataGridSemiTheme());
            BooruComponentDispatcher.Wait(ExerciseAsync());

            async Task ExerciseAsync()
            {
                using TempDirectory profile = new();
                string downloads = Path.Combine(profile.Path, "downloads");
                string mediaDirectory = StorageLayout.GetThreadDirectory(downloads, ThreadId.Source, ThreadId.Board, ThreadId.Thread!);
                new Config
                {
                    DownloadPath = downloads, FirstRunSetupVersion = 1, DownloadsPaused = true,
                    WorkspaceMode = "Advanced", Language = "en", MediaSidebarInfo = "Always"
                }.SaveConfig(profile.Path);
                (ArchivedThreadView thread, MediaAssetSnapshot manifest) = CreateFixture(mediaDirectory, includePostAndReply: true, replyHasMedia: replyHasMedia);
                await RateAndTagWindowFixture.SeedAsync(profile.Path, thread);
                using IDisposable manifests = VirtualMediaManifests.Register(Path.GetDirectoryName(mediaDirectory)!,
                    (directory, _, _) => Task.FromResult(directory == manifest.Directory ? manifest
                        : new MediaAssetSnapshot(directory, MediaManifestStatus.Missing, null, null, [])));
                await using DesktopSessionHost host = new(profile.Path, Array.Empty<IImageBoard>(),
                    updateSessionFactory: (path, config) => new UpdateSession(path, config, disableEnvironment: () => "1"));
                await using MainWindow window = new(host) { Width = 1440, Height = 900, Classes = { MotionPolicy.OffClass } };
                window.Show();
                window.PlatformImpl!.Activated?.Invoke();
                await window.StartupTimingRestoreTask;
                Assert.Equal(DesktopSessionState.Ready, host.State);
                ArchivePage page = window.ArchivePage;
                await page.OpenReaderOnThreadAsync(ThreadId, () => page.OpenReaderRequested!.Invoke(null), thread);
                await page.MediaLoadTask;
                Assert.True(page.IsLargeReader);
                page.MediaTilesPreference = true;
                Layout(window);
                Assert.True(page.IsMediaSidebarShown);
                SelectTab(window, ById<TabItem>(window, "ArchivePostsTab"));
                Dictionary<char, MediaTagSubject> subjects = GallerySelection.Build(thread, [manifest], allSavedVersions: true, includeOtherMedia: true)
                    .Entries.ToDictionary(entry => Path.GetFileName(entry.Asset.Path)[0],
                        entry => Assert.IsAssignableFrom<MediaTagSubject>(MediaTagSubjects.Of(entry)));
                Assert.Equal(new[] { 'a', 'b', 'c' }, subjects.Keys.Order());
                _ = host.GetPrivateTagSnapshot();
                try { await test(page, window, host, subjects); }
                finally
                {
                    QuickTagPopover.Current?.Close();
                    window.GalleryMediaTagWindow?.CloseForLifetime();
                }
            }
        });

    private static (ArchivedThreadView Thread, MediaAssetSnapshot Manifest) CreateFixture(string directory,
        bool includePostAndReply = false, bool additionalRevisions = false, bool replyHasMedia = false)
    {
        string filename = "same.png";
        const string currentImage = "iVBORw0KGgoAAAANSUhEUgAAABEAAAALCAIAAAAWQvFQAAAAE0lEQVR4nGNgmMlAMhrVM6oHiADlWm/EglqV/gAAAABJRU5ErkJggg==";
        MediaAssetReference current = WriteRevision('a', true, currentImage, filename);
        MediaAssetReference earlier = WriteRevision('b', false,
            "iVBORw0KGgoAAAANSUhEUgAAABMAAAANCAIAAADE7sJwAAAAFklEQVR4nGMwdgklDzGM6hzVOQR1AgDPR8TVSHrz+wAAAABJRU5ErkJggg==", filename);

        MediaAssetReference WriteRevision(char id, bool isCurrent, string png, string name)
        {
            byte[] bytes = Convert.FromBase64String(png);
            Array.Resize(ref bytes, 4096);
            string folder = Path.Combine(directory, id.ToString());
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, id + ".png");
            File.WriteAllBytes(path, bytes);
            return new(new string(id, 64), "image", "original", name, path, bytes.Length,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), "https://offline.invalid/" + id, name, isCurrent);
        }
        string postName = includePostAndReply ? "post.png" : filename;
        List<MediaAssetReference> assets = [current, earlier];
        if (includePostAndReply) assets.Add(WriteRevision('c', true, replyHasMedia
            ? "iVBORw0KGgoAAAANSUhEUgAAABEAAAALCAIAAAAWQvFQAAAAF0lEQVR4nGO8oxHAQCJgIlXDqJ5hqQcAEJEBagzqPS4AAAAASUVORK5CYII="
            : currentImage, postName));
        if (additionalRevisions)
        {
            assets.Add(WriteRevision('d', true, currentImage, "z-a.png"));
            assets.Add(WriteRevision('e', true,
                "iVBORw0KGgoAAAANSUhEUgAAABMAAAANCAIAAADE7sJwAAAAFklEQVR4nGMwdgklDzGM6hzVOQR1AgDPR8TVSHrz+wAAAABJRU5ErkJggg==", "z-b.png"));
        }
        MediaAssetSnapshot manifest = new(directory, MediaManifestStatus.Available, 1, "fixture", assets);
        List<CapturedPost> posts =
        [
            new("42", 42, PostFieldPresence.All, Html: "Opening post", Attachments:
            [
                new CapturedAttachment(0, "image", Url: includePostAndReply ? "https://offline.invalid/c" : null, OriginalFileName: postName)
            ])
        ];
        posts.Add(new("43", 43, PostFieldPresence.All, Html: includePostAndReply ? "<a href='#42'>&gt;&gt;42</a>" : null,
            Attachments: replyHasMedia ? [new CapturedAttachment(0, "image", Url: current.SourceUrl, OriginalFileName: filename)] : null));
        ArchiveThreadSummary summary = new(ThreadId, "https://offline.invalid/g/thread/42", "Fixture", directory,
            ThreadSourceState.Active, true, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, posts.Count, 1);
        return (new ArchivedThreadView(summary, posts, [directory]), manifest);

    }

    private static void Layout(TopLevel window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static void PressKey(TopLevel window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPressQwerty(key, modifiers);
        window.KeyReleaseQwerty(key, modifiers);
        Layout(window);
    }

    private static void WaitUntil(Func<bool> condition)
    {
        if (condition()) return;
        // Both real panels throttle selection updates through dispatcher timers.
        using CancellationTokenSource finished = new(TimeSpan.FromSeconds(5));
        DispatcherTimer poll = new() { Interval = TimeSpan.FromMilliseconds(10) };
        poll.Tick += (_, _) => { if (condition()) finished.Cancel(); };
        poll.Start();
        try { Dispatcher.UIThread.MainLoop(finished.Token); }
        finally { poll.Stop(); }
        Assert.True(condition());
    }
}
