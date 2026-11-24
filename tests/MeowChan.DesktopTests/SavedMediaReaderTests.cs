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
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
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
    private static readonly string[] FooterActions = ["ArchiveEditRatingButton", "PreviewArchiveMediaButton", "ArchiveManageRatingsButton"];

    [Fact]
    public void NavigationModesFollowTheReaderTabAndAutoRevealsThenHides() => RunWithShell(async (page, window, host, _) =>
    {
        long now = 0;
        page.UseManualReaderTicks(() => now);
        TabControl tabs = page.FindControl<TabControl>("ArchiveContentTabs")!;
        TabItem references = await PopulateReferencesAsync(page, window, host);
        Control bar = ById<Control>(window, "ReaderNavigation");
        int delay = Config.ControlsIdleHideMsDefault;
        page.ReaderNavigationIdleDelayMs = delay;
        foreach (string mode in new[] { Config.ReaderNavigationModeOff, Config.ReaderNavigationModeAlways, Config.ReaderNavigationModeAuto })
        {
            tabs.SelectedIndex = 0;
            page.ReaderNavigationMode = mode;
            foreach (TabItem tab in new[] { (TabItem)tabs.Items[0]!, references })
            {
                Click(window, tab);
                Assert.Same(tab, tabs.SelectedItem);
                page.NotifyReaderPointerMoved(default);
                bool visible = mode != Config.ReaderNavigationModeOff;
                Assert.Equal(visible, bar.IsEffectivelyVisible);
                Assert.Equal(visible, bar.IsHitTestVisible);
                now += delay - 2;
                page.TickReaderNavigationIdle();
                Assert.Equal(visible, bar.IsHitTestVisible);
                now += 2;
                page.TickReaderNavigationIdle();
                Assert.Equal(mode == Config.ReaderNavigationModeAlways, bar.IsHitTestVisible);
                if (mode == Config.ReaderNavigationModeAuto)
                {
                    Assert.Equal(0, bar.Opacity);
                    page.NotifyReaderPointerMoved(default);
                    Assert.True(bar.IsHitTestVisible);
                    Assert.Equal(1, bar.Opacity);
                }
            }
            tabs.SelectedIndex = 1;
            Layout(window);
            page.NotifyReaderPointerMoved(default);
            Assert.False(bar.IsEffectivelyVisible);
            Assert.False(bar.IsHitTestVisible);
        }
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
        int suggestion = shown.Surface!.Shown.ToList().FindIndex(tag => tag.Id == existing) + 1;
        Button offered = ById<Button>(shown.Frame, "ArchiveTagPopoverSuggestion" + suggestion);
        Click(Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(offered)), offered);
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
        TabControl tabs = page.FindControl<TabControl>("ArchiveContentTabs")!;
        page.FindControl<ListBox>("ArchivePosts")!.SelectedIndex = 0;
        TabItem references = await PopulateReferencesAsync(page, window, host);
        Click(window, references);
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
        Assert.True(SidebarEditButton(window, sidebar).Focus());
        long revision = (await host.LoadPostFiltersAsync()).Revision;
        PressKey(window, PhysicalKey.H);
        await page.PostFilterTask;
        Assert.Equal(revision, (await host.LoadPostFiltersAsync()).Revision);
    });

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void SidebarTagWriteIsRevokedDuringLocalValidation(bool advanced, bool openGallery) =>
        RunWithShell(async (page, window, host, subjects) =>
        {
            page.UseManualReaderTicks(static () => 0);
            ListBox tiles = page.FindControl<ListBox>("ArchiveMediaTiles")!;
            page.FindControl<TabControl>("ArchiveContentTabs")!.SelectedIndex = 1;
            ArchiveAssetRow selected = ShownRows(page, grid: true).Single(row => Path.GetFileName(row.Asset.Path) == "b.png");
            tiles.SelectedItem = selected;
            ReaderMediaSidebar sidebar = Assert.IsType<ReaderMediaSidebar>(page.MediaSidebar);
            await ShowSidebarSelectionAsync(page, window, sidebar, selected.Version);
            QuickTagPopover editor = OpenSidebarEditor(window, sidebar);
            MediaTagDialog? dialog = null;
            if (advanced)
            {
                Button advancedButton = ById<Button>(editor.Frame, "ArchiveTagPopoverAdvanced");
                Click(Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(advancedButton)), advancedButton);
                await BooruTestSupport.WaitAsync(() => window.GalleryMediaTagWindow?.IsVisible == true, "the advanced tag dialog");
                dialog = window.GalleryMediaTagWindow!;
            }

            TaskCompletionSource<(LocalMediaObservation Observation, CancellationToken Token)> validating =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            host.LocalMediaChecksForTests = async (observations, token) =>
            {
                validating.SetResult((Assert.Single(observations), token));
                await release.Task;
                return [LocalMediaCheck.Verified];
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
            finally { release.TrySetResult(); }

            await BooruTestSupport.WaitAsync(() => dialog == null ? !editor.Surface!.Session.Busy : !dialog.Session.Busy,
                "the rejected host write to finish");
            Assert.False(dialog?.IsVisible ?? editor.IsOpen);
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
        Assert.True(sidebar.Close.Focus());
        Assert.True(sidebar.Close.IsFocused);
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
        List<string> folders = [];
        Run((page, window) =>
        {
            page.MediaTilesPreference = true;
            Layout(window);
            ListBox tiles = page.FindControl<ListBox>("ArchiveMediaTiles")!;
            ReaderMediaSidebar sidebar = Assert.IsType<ReaderMediaSidebar>(page.MediaSidebar);
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
            sidebar.ConfigureInfo(ReaderMediaPreferences.Default with { MediaSidebarInfo = "Overlay" });
            Assert.True(sidebar.Close.Focus());
            Layout(window);
            Control frame = ById<Control>(sidebar, "ReaderMediaSidebarFrame");
            Control overlay = ById<Control>(sidebar, "ReaderMediaSidebarOverlay");
            Point imagePoint = HitPoint(window, frame, new Point(frame.Bounds.Width / 2, 8));
            Point overlayPoint = HitPoint(window, overlay);
            Assert.False(overlay.IsVisualAncestorOf(Assert.IsAssignableFrom<Visual>(window.InputHitTest(imagePoint))));
            PressKey(window, PhysicalKey.M);
            BooruComponentDispatcher.Wait(page.FavoriteKeyTask);
            MiddleClick(imagePoint, imagePoint);
            Assert.Single(folders);
            MiddleClick(overlayPoint, overlayPoint);
            MiddleClick(imagePoint, overlayPoint);
            Assert.Single(folders);

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
            Point secondImagePoint = HitPoint(window, frame, new Point(frame.Bounds.Width / 2, 8));
            window.MouseUp(secondImagePoint, MouseButton.Middle, RawInputModifiers.None);
            Assert.Single(folders);
            Assert.True(sidebar.Close.Focus());
            PressKey(window, PhysicalKey.U);
            BooruComponentDispatcher.Wait(page.FavoriteKeyTask);
            MiddleClick(secondImagePoint, secondImagePoint);

            Assert.Equal(new[] { (firstKey, true), (secondKey, false) }, writes);
            Assert.Equal(firstKey, Assert.Single(favorites));
            Assert.Equal(new[] { Path.GetDirectoryName(first.Asset.Path), Path.GetDirectoryName(second.Asset.Path) }, folders);

            void MiddleClick(Point press, Point release)
            {
                window.MouseDown(press, MouseButton.Middle, RawInputModifiers.None);
                window.MouseUp(release, MouseButton.Middle, RawInputModifiers.None);
                Layout(window);
            }
        }, openFolder: folders.Add);
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
        Button edit = ById<Button>(sidebar, "ReaderMediaSidebarEditTags");
        if (!edit.IsEffectivelyVisible) Click(window, ById<ToggleButton>(sidebar, "ReaderMediaSidebarInfoToggle"));
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

    private static T ById<T>(Visual root, string id) where T : Control =>
        Assert.Single(root.GetVisualDescendants().OfType<T>(), control => AutomationProperties.GetAutomationId(control) == id);

    private static void Click(TopLevel root, Control control)
    {
        Point point = HitPoint(root, control);
        root.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
        root.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
        Layout(root);
    }

    private static Point HitPoint(TopLevel root, Control control, Point? local = null)
    {
        Assert.True(control.IsEffectivelyVisible);
        Assert.True(control.IsEffectivelyEnabled);
        Layout(root);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Point point = control.TranslatePoint(local ?? new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)!.Value;
        Visual hit = Assert.IsAssignableFrom<Visual>(root.InputHitTest(point));
        Assert.True(ReferenceEquals(hit, control) || control.IsVisualAncestorOf(hit),
            $"Expected {AutomationProperties.GetAutomationId(control)} at {point}; hit {hit}.");
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ArrowKeysMakeTheOpenSidebarFollowTheExactRevision(bool grid) => Run((page, window) =>
    {
        page.MediaTilesPreference = grid;
        Layout(window);
        ListBox tiles = page.FindControl<ListBox>("ArchiveMediaTiles")!;
        DataGrid table = page.FindControl<DataGrid>("ArchiveMediaGrid")!;
        ArchiveAssetRow[] rows = (grid ? tiles.ItemsSource : table.ItemsSource)!.Cast<ArchiveAssetRow>().ToArray();
        Assert.Equal(rows[0].Name, rows[1].Name);

        if (grid)
        {
            tiles.SelectedIndex = 0;
            Layout(window);
            Assert.True(((Control)tiles.ContainerFromIndex(0)!).Focus(NavigationMethod.Tab));
        }
        else SelectTableRow(window, table, rows[0]);
        ReaderMediaSidebar sidebar = Assert.IsType<ReaderMediaSidebar>(page.MediaSidebar);
        Assert.True(page.IsMediaSidebarShown);
        WaitUntil(() => sidebar.HeaderLine.Contains(rows[0].Version, StringComparison.Ordinal)
            && sidebar.Load.ShownSource == new PixelSize(17, 11));
        PressKey(window, grid ? PhysicalKey.ArrowRight : PhysicalKey.ArrowDown);
        Assert.Equal(1, grid ? tiles.SelectedIndex : table.SelectedIndex);
        WaitUntil(() => sidebar.HeaderLine.Contains(rows[1].Version, StringComparison.Ordinal)
            && sidebar.Load.ShownSource == new PixelSize(19, 13));
        PressKey(window, grid ? PhysicalKey.ArrowLeft : PhysicalKey.ArrowUp);
        Assert.Equal(0, grid ? tiles.SelectedIndex : table.SelectedIndex);
        WaitUntil(() => sidebar.HeaderLine.Contains(rows[0].Version, StringComparison.Ordinal)
            && sidebar.Load.ShownSource == new PixelSize(17, 11));

        if (!grid)
        {
            DataGridRow row = table.GetVisualDescendants().OfType<DataGridRow>()
                .Single(item => ReferenceEquals(item.DataContext, rows[0]));
            Border background = row.GetVisualDescendants().OfType<Border>().Single(item => item.Name == "BackgroundBorder");
            Assert.Equal(new Thickness(3, 1, 1, 1), background.BorderThickness);
            Visual[] outlines = row.GetVisualDescendants().Where(item => item is Control { Name: "CurrencyVisual" or "FocusVisual" }).ToArray();
            Assert.NotEmpty(outlines);
            Assert.All(outlines, outline => Assert.Equal(0, outline.Opacity));
        }
    });

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

    private static ArchiveAssetRow[] ShownRows(ArchivePage page, bool grid) =>
        (grid ? page.FindControl<ListBox>("ArchiveMediaTiles")!.ItemsSource : page.FindControl<DataGrid>("ArchiveMediaGrid")!.ItemsSource)!
        .Cast<ArchiveAssetRow>().ToArray();

    private static void Run(Action<ArchivePage, Window> test, Action<string>? openFolder = null) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        Application.Current!.Styles.Insert(1, new DataGridSemiTheme());
        using TempDirectory temp = new();
        (ArchivedThreadView thread, MediaAssetSnapshot manifest) = CreateFixture(temp.Path);
        ArchivePage page = new(_ => throw new InvalidOperationException("The prepared thread should be used"),
            (_, _) => Task.CompletedTask, openFolder ?? (_ => { }), (_, _) => Task.FromResult(manifest))
        {
            IsAvailable = true,
            IsArchiveFavorite = entry => entry.Asset.IsCurrent
        };
        Window window = new() { Width = 1440, Height = 840 };
        try
        {
            BooruComponentDispatcher.Wait(page.LoadThreadAsync(ThreadId, preparedThread: thread));
            BooruComponentDispatcher.Wait(page.MediaLoadTask);
            window.Content = page.DetachReader();
            window.Show();
            window.PlatformImpl!.Activated?.Invoke();
            Layout(window);
            page.FindControl<TabControl>("ArchiveContentTabs")!.SelectedIndex = 1;
            Layout(window);
            test(page, window);
        }
        finally
        {
            page.EndLifetime();
            window.Close();
        }
    });

    private static void RunWithShell(Func<ArchivePage, MainWindow, DesktopSessionHost, IReadOnlyDictionary<char, MediaTagSubject>, Task> test) =>
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
                (ArchivedThreadView thread, MediaAssetSnapshot manifest) = CreateFixture(mediaDirectory, includePostAndReply: true);
                new Config
                {
                    DownloadPath = downloads, FirstRunSetupVersion = 1, DownloadsPaused = true,
                    WorkspaceMode = "Advanced", Language = "en", MediaSidebarInfo = "Always"
                }.SaveConfig(profile.Path);
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
                page.FindControl<TabControl>("ArchiveContentTabs")!.SelectedIndex = 0;
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

    private static (ArchivedThreadView Thread, MediaAssetSnapshot Manifest) CreateFixture(string directory, bool includePostAndReply = false)
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
        MediaAssetReference[] assets = includePostAndReply ? [current, earlier, WriteRevision('c', true, currentImage, postName)] : [current, earlier];
        MediaAssetSnapshot manifest = new(directory, MediaManifestStatus.Available, 1, "fixture", assets);
        List<CapturedPost> posts =
        [
            new("42", 42, PostFieldPresence.All, Html: "Opening post", Attachments:
            [
                new CapturedAttachment(0, "image", Url: includePostAndReply ? "https://offline.invalid/c" : null, OriginalFileName: postName)
            ])
        ];
        if (includePostAndReply)
            posts.Add(new("43", 43, PostFieldPresence.All, Html: "<a href='#42'>&gt;&gt;42</a>"));
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

    private static void PressKey(TopLevel window, PhysicalKey key)
    {
        window.KeyPressQwerty(key, RawInputModifiers.None);
        window.KeyReleaseQwerty(key, RawInputModifiers.None);
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
