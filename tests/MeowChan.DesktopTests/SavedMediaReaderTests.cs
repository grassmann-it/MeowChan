using System.Reflection;
using System.Security.Cryptography;
using Avalonia;
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
    public void NavigationModesFollowTheReaderTabAndAutoRevealsThenHides() => Run((page, window) =>
    {
        TabControl tabs = page.FindControl<TabControl>("ArchiveContentTabs")!;
        Control bar = page.FindControl<Control>("ReaderNavigation")!;
        page.ReaderNavigationIdleDelayMs = ReaderNavigationLayout.IdleHideDelayMsMinimum;
        foreach (string mode in new[] { Config.ReaderNavigationModeOff, Config.ReaderNavigationModeAlways, Config.ReaderNavigationModeAuto })
        {
            page.ReaderNavigationMode = mode;
            tabs.SelectedIndex = 0;
            Layout(window);
            page.NotifyReaderPointerMoved(default);
            if (mode == Config.ReaderNavigationModeOff) Assert.False(bar.IsEffectivelyVisible);
            else
            {
                Assert.True(bar.IsEffectivelyVisible);
                WaitUntil(() => bar.IsHitTestVisible && bar.Opacity == 1);
                if (mode == Config.ReaderNavigationModeAuto)
                {
                    WaitUntil(() => !bar.IsHitTestVisible && bar.Opacity == 0);
                    page.NotifyReaderPointerMoved(default);
                    WaitUntil(() => bar.IsHitTestVisible && bar.Opacity == 1);
                }
                else
                {
                    long expiry = Environment.TickCount64 + page.ReaderNavigationIdleDelayMs;
                    WaitUntil(() => Environment.TickCount64 > expiry);
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
    public void TagEditorRejectsThePendingRevisionAndWritesOnlyAfterItIsShown() => Run((page, window) =>
    {
        using TempDirectory profile = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        PrivateTagStore store = new(profile.Path, lease);
        List<string> opened = [];
        page.TagPopoverAsync = async (entry, anchor, _, current, request) =>
        {
            opened.Add(entry.Asset.AssetId);
            MediaTagSubject subject = Assert.IsAssignableFrom<MediaTagSubject>(MediaTagSubjects.Of(entry));
            MediaTagSession media = new(subject, () => new(PrivateTagLoadState.Ready, store.ReadSnapshot(), null),
                (name, target, token) =>
                {
                    if (!current()) throw new OperationCanceledException();
                    request.SidebarToken.ThrowIfCancellationRequested();
                    return Task.FromResult(store.DefineAndSetMediaSubjects(name, [target], null, token));
                },
                (tag, target, assigned, token) => Task.FromResult(store.SetMediaSubjects(tag, [target], assigned, null, token)));
            using QuickTagSession session = new(QuickTagTarget.For(media));
            QuickTagPopover popover = QuickTagPopover.Open(anchor, session, entry.Asset.OriginalFileName!, new() { IsCurrent = current });
            await popover.Result;
        };

        page.MediaTilesPreference = true;
        TabControl tabs = page.FindControl<TabControl>("ArchiveContentTabs")!;
        ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
        ListBox tiles = page.FindControl<ListBox>("ArchiveMediaTiles")!;
        ReaderMediaSidebar sidebar = Assert.IsType<ReaderMediaSidebar>(page.MediaSidebar);
        tabs.SelectedIndex = 0;
        posts.SelectedIndex = 0;
        WaitUntil(() => sidebar.HeaderLine.Contains("post.png", StringComparison.Ordinal));
        ArchiveAssetRow[] rows = ShownRows(page, grid: true);
        ArchiveAssetRow a = rows.Single(row => Path.GetFileName(row.Asset.Path) == "a.png");
        ArchiveAssetRow b = rows.Single(row => Path.GetFileName(row.Asset.Path) == "b.png");
        tiles.SelectedItem = a;
        tabs.SelectedIndex = 1;
        WaitUntil(() => sidebar.HeaderLine.Contains(a.Version, StringComparison.Ordinal));
        Button edit = ShowSidebarEditButton(sidebar, window);
        tiles.SelectedItem = b;
        Assert.Contains(a.Version, sidebar.HeaderLine);
        edit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Empty(opened);
        Assert.Null(QuickTagPopover.Current);

        WaitUntil(() => sidebar.HeaderLine.Contains(b.Version, StringComparison.Ordinal));
        ShowSidebarEditButton(sidebar, window).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        QuickTagPopover shown = Assert.IsType<QuickTagPopover>(QuickTagPopover.Current);
        try
        {
            Assert.Equal(b.Asset.AssetId, Assert.Single(opened));
            QuickTagSession editor = shown.Surface!.Session;
            BooruComponentDispatcher.Wait(editor.AddAsync("chosen-revision"));
            GalleryEntry[] entries = GallerySelection.Build(page.CurrentThread!, [b.Manifest], allSavedVersions: true).Entries.ToArray();
            PrivateTagStoreSnapshot persisted = store.ReadSnapshot();
            foreach (GalleryEntry entry in entries)
            {
                MediaTagSubject subject = Assert.IsAssignableFrom<MediaTagSubject>(MediaTagSubjects.Of(entry));
                Assert.Equal(entry.Asset.AssetId == b.Asset.AssetId ? ["chosen-revision"] : Array.Empty<string>(),
                    persisted.TagsOf(subject).Select(tag => tag.Name.Display));
            }
            page.SetObscuredByGallery(true);
            BooruComponentDispatcher.Wait(editor.AddAsync("covered"));
            Assert.DoesNotContain(store.ReadSnapshot().Tags, tag => tag.Name.Display == "covered");
        }
        finally { shown.Close(); }
    }, includePostAsset: true);

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

            Select(first, new PixelSize(17, 11));
            PressKey(window, PhysicalKey.M);
            BooruComponentDispatcher.Wait(page.FavoriteKeyTask);
            MiddleClick();
            Select(second, new PixelSize(19, 13));
            PressKey(window, PhysicalKey.U);
            BooruComponentDispatcher.Wait(page.FavoriteKeyTask);
            MiddleClick();

            Assert.Equal(new[] { (firstKey, true), (secondKey, false) }, writes);
            Assert.Equal(firstKey, Assert.Single(favorites));
            Assert.Equal(new[] { Path.GetDirectoryName(first.Asset.Path), Path.GetDirectoryName(second.Asset.Path) }, folders);

            void Select(ArchiveAssetRow row, PixelSize size)
            {
                tiles.SelectedItem = row;
                WaitUntil(() => sidebar.Load.ShownSource == size);
                Layout(window);
                Assert.True(sidebar.Close.Focus());
            }

            void MiddleClick()
            {
                Control frame = sidebar.FrameArea;
                Point p = frame.TranslatePoint(new Point(frame.Bounds.Width / 2, frame.Bounds.Height / 2), window)!.Value;
                window.MouseDown(p, MouseButton.Middle, RawInputModifiers.None);
                window.MouseUp(p, MouseButton.Middle, RawInputModifiers.None);
                Layout(window);
            }
        }, openFolder: folders.Add);
    }

    private static Button ShowSidebarEditButton(ReaderMediaSidebar sidebar, Window window)
    {
        Layout(window);
        if (sidebar.InfoToggle.IsChecked != true)
        {
            sidebar.InfoToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout(window);
        }
        return Assert.Single(sidebar.GetVisualDescendants().OfType<Button>(),
            button => button.Name == "ReaderMediaSidebarEditTags" && button.IsEffectivelyVisible);
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

        ArchiveFavoriteButton star = first.GetVisualDescendants().OfType<ArchiveFavoriteButton>().Single();
        bool? before = star.IsChecked;
        Click(window, star);
        Assert.NotEqual(before, star.IsChecked);
        Assert.False(star.IsFocused);
        Assert.True(first.IsFocused);
        PressKey(window, PhysicalKey.ArrowRight);
        Assert.Equal(1, tiles.SelectedIndex);
        WaitUntil(() => sidebar.HeaderLine.Contains(rows[1].Version, StringComparison.Ordinal)
            && sidebar.Load.ShownSource == new PixelSize(19, 13));
    });

    [Fact]
    public void ArrowsPressedBeforeTheLoadFinishesEndOnTheLatestRevision() => Run((page, window) =>
    {
        ListBox tiles = page.FindControl<ListBox>("ArchiveMediaTiles")!;
        ArchiveAssetRow[] rows = ShownRows(page, true);
        Click(window, (Control)tiles.ContainerFromIndex(0)!);
        ReaderMediaSidebar sidebar = Assert.IsType<ReaderMediaSidebar>(page.MediaSidebar);
        PixelSize first = new(17, 11), latest = new(19, 13);
        PixelSize[] sizes = [first, latest];
        bool selectionOutranPreview = false;
        bool headerMatchedAtFullOpacity = true;
        WaitUntil(() => !sidebar.Load.Awaiting && sidebar.Load.ShownSource == first);

        PressKey(window, PhysicalKey.ArrowRight);
        Assert.Equal(1, tiles.SelectedIndex);
        SampleSidebar();
        PressKey(window, PhysicalKey.ArrowLeft);
        Assert.Equal(0, tiles.SelectedIndex);
        SampleSidebar();
        PressKey(window, PhysicalKey.ArrowRight);
        Assert.Equal(1, tiles.SelectedIndex);
        SampleSidebar();
        WaitUntil(() =>
        {
            SampleSidebar();
            return !sidebar.Load.Awaiting && sidebar.Load.ShownSource == latest;
        });
        Assert.True(selectionOutranPreview);
        Assert.True(headerMatchedAtFullOpacity);
        Assert.Contains(rows[1].Version, sidebar.HeaderLine, StringComparison.Ordinal);

        void SampleSidebar()
        {
            selectionOutranPreview |= sidebar.Load.Awaiting || sidebar.Load.ShownSource != sizes[tiles.SelectedIndex];
            if (sidebar.IsDimmed || sidebar.Load.ShownSource is not { } shown) return;
            headerMatchedAtFullOpacity &= sidebar.HeaderLine.Contains(rows[shown == first ? 0 : 1].Version, StringComparison.Ordinal);
        }
    });

    [Fact]
    public void ArrowsAfterClickingASavedMediaFooterButtonKeepTheReaderOnItsPost() => Run((page, window) =>
    {
        int clicks = 0;
        page.ManageMediaRatingsAsync = () => { clicks++; return Task.CompletedTask; };
        page.ArchiveColumnModePreference = WorkViewSetting.AdvancedName;
        Layout(window);
        ListBox tiles = page.FindControl<ListBox>("ArchiveMediaTiles")!;
        Click(window, (Control)tiles.ContainerFromIndex(0)!);
        Button manage = page.FindControl<Button>("ArchiveManageRatingsButton")!;
        Click(window, manage);
        Assert.True(manage.IsFocused);
        Assert.Equal(1, clicks);
        string? before = page.CurrentReaderPostId;
        Assert.Equal("42", before);
        Task navigation = page.ReaderNavigationTask;

        PressKey(window, PhysicalKey.ArrowDown);
        BooruComponentDispatcher.Wait(page.ReaderNavigationTask);
        Assert.Same(navigation, page.ReaderNavigationTask);
        Assert.Equal(before, page.CurrentReaderPostId);
        Assert.Equal(1, page.FindControl<TabControl>("ArchiveContentTabs")!.SelectedIndex);

        Click(window, manage);
        PressKey(window, PhysicalKey.ArrowRight);
        Assert.Equal(1, tiles.SelectedIndex);
        Assert.Same(navigation, page.ReaderNavigationTask);
        Assert.Equal(before, page.CurrentReaderPostId);
        ReaderMediaSidebar sidebar = Assert.IsType<ReaderMediaSidebar>(page.MediaSidebar);
        WaitUntil(() => sidebar.Load.ShownSource == new PixelSize(19, 13));
    });

    private static ArchiveAssetRow[] ShownRows(ArchivePage page, bool grid) =>
        (grid ? page.FindControl<ListBox>("ArchiveMediaTiles")!.ItemsSource : page.FindControl<DataGrid>("ArchiveMediaGrid")!.ItemsSource)!
        .Cast<ArchiveAssetRow>().ToArray();

    private static void Click(Window window, Control control)
    {
        Point position = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseMove(position);
        window.MouseDown(position, MouseButton.Left);
        window.MouseUp(position, MouseButton.Left);
        Layout(window);
    }

    private static void Run(Action<ArchivePage, Window> test, bool includePostAsset = false, Action<string>? openFolder = null) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        Application.Current!.Styles.Insert(1, new DataGridSemiTheme());
        using TempDirectory temp = new();
        string filename = "same.png";
        const string currentImage = "iVBORw0KGgoAAAANSUhEUgAAABEAAAALCAIAAAAWQvFQAAAAE0lEQVR4nGNgmMlAMhrVM6oHiADlWm/EglqV/gAAAABJRU5ErkJggg==";
        MediaAssetReference current = WriteRevision('a', true, currentImage, filename);
        MediaAssetReference earlier = WriteRevision('b', false,
            "iVBORw0KGgoAAAANSUhEUgAAABMAAAANCAIAAADE7sJwAAAAFklEQVR4nGMwdgklDzGM6hzVOQR1AgDPR8TVSHrz+wAAAABJRU5ErkJggg==", filename);

        MediaAssetReference WriteRevision(char id, bool isCurrent, string png, string name)
        {
            byte[] bytes = Convert.FromBase64String(png);
            Array.Resize(ref bytes, 4096);
            string folder = Path.Combine(temp.Path, id.ToString());
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, id + ".png");
            File.WriteAllBytes(path, bytes);
            return new(new string(id, 64), "image", "original", name, path, bytes.Length,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), "https://offline.invalid/" + id, name, isCurrent);
        }
        string postName = includePostAsset ? "post.png" : filename;
        MediaAssetReference[] assets = includePostAsset ? [current, earlier, WriteRevision('c', true, currentImage, postName)] : [current, earlier];
        MediaAssetSnapshot manifest = new(temp.Path, MediaManifestStatus.Available, 1, "fixture", assets);
        ArchiveThreadSummary summary = new(ThreadId, "https://offline.invalid/g/thread/42", "Fixture", temp.Path,
            ThreadSourceState.Active, true, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 2, 1);
        ArchivedThreadView thread = new(summary, [new CapturedPost("42", 42, PostFieldPresence.All,
            Attachments: [new CapturedAttachment(0, "image", Url: includePostAsset ? "https://offline.invalid/c" : null,
                OriginalFileName: postName)]),
            new CapturedPost("43", 43, PostFieldPresence.All)], [temp.Path]);
        HashSet<string> favorites = [current.AssetId];
        ArchivePage page = new(_ => throw new InvalidOperationException("The prepared thread should be used"),
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

    private static void Layout(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static void PressKey(Window window, PhysicalKey key)
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
