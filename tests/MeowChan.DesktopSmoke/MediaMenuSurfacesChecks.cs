using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.MediaActions;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

// Root dispatch only: MEOWCHAN_MEDIA_MENU_SURFACES_ONLY. One real main window over one synthetic archive: the media
// page, the archive (table, tiles, inline attachment) and the gallery open the shared media menu on the exact
// right-clicked occurrence, agree on order, labels and availability reasons, and refuse after the target is gone.
// Managed routed pointer and key events; no native desktop or screen-reader claim, no Windows native-video overlay.
internal static class MediaMenuSurfacesChecks
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private static readonly ResourceId ThreadA = new("media-fixture", "g", "100");

    // The common section in its one order; the open command differs by id only (the media page opens its own window).
    private static readonly string[] Order =
    {
        "gallery.open", "favorites.toggle", "media.rating.edit", "media.tags.edit",
        "media.export-original", "media.show-in-folder", "media.details", "gallery.show-in-thread"
    };

    internal static async Task RunAsync(MainWindow owner)
    {
        string language = Texts.Language;
        string profile = TemporaryWorkspacePaths.CreateDirectory("media-menu-surfaces-");
        new Config { DownloadsPaused = true, SaveUrlsOnExit = false, CloseToTray = false,
            MinimizeToTray = false, WorkspaceMode = "Simple" }.SaveConfig(profile);
        Texts.SetLanguage("en");
        await MediaPageChecks.SeedAsync(profile);
        DesktopSessionHost session = new(profile, Array.Empty<IImageBoard>());
        MainWindow shell = new(session) { Width = 1400, Height = 950 };
        Exception? failure = null;
        try
        {
            shell.Show(owner);
            await UntilAsync(() => session.State == DesktopSessionState.Ready && shell.FindControl<Button>("AddUrlButton")!.IsEnabled);

            string[] mediaPageBound = await MediaPageJourneyAsync(shell);
            (string[] table, string[] tiles, string[] inline, string[] tilesLoose) = await ArchiveJourneyAsync(shell);
            await GalleryJourneyAsync(shell, mediaPageBound, tiles);

            Same("media page", mediaPageBound, "archive tiles", tiles);
            Same("archive table", table, "archive tiles", tiles);
            Same("archive inline", inline, "archive tiles", tiles);
            Console.WriteLine("SMOKE media menu surfaces [en]: right-click on B while A is selected targets B on the media page, archive table, tiles and inline attachment and the gallery; Menu key and Shift+F10 open the same menu; order, labels, availability and reasons are identical across surfaces; the gallery offers media tags; loose and unbound media state honest reasons; the menus refuse after rebuild, thread change and navigation. Loose tile menu: " + string.Join(" / ", tilesLoose));
        }
        catch (Exception thrown) { failure = thrown; throw; }
        finally
        {
            await SmokeCleanup.RunAsync(failure, async () =>
            {
                foreach (Window child in shell.OwnedWindows.Where(child => child.IsVisible).ToArray()) child.Close();
                if (shell.FindControl<ContentControl>("GalleryHost")?.Content is GalleryViewer open)
                    GalleryViewerChecksAccess.Click(open, "GalleryClose");
                shell.Close();
                await UntilAsync(() => !shell.IsVisible);
                await shell.GalleryCleanupTask.WaitAsync(Deadline);
                owner.Activate();
            }, exception => Console.Error.WriteLine("SMOKE cleanup failed in MediaMenuSurfacesChecks: " + exception));
            Texts.SetLanguage(language);
        }
    }

    // Media page: the three present tiles are B's file (thread 200), A's bound file and A's loose file.
    private static async Task<string[]> MediaPageJourneyAsync(MainWindow shell)
    {
        Button media = shell.FindControl<Button>("MediaNavigationButton")!;
        await UntilAsync(() => media.IsVisible && media.IsEffectivelyEnabled);
        media.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ContentControl host = shell.FindControl<ContentControl>("MediaPageHost")!;
        await UntilAsync(() => host.IsVisible && host.Content is MediaPage);
        MediaPage page = (MediaPage)host.Content!;
        await page.BuildTask.WaitAsync(Deadline);
        await UntilAsync(() => page.ShownEntries.Count == 3);
        MediaLibraryTileRow[] rows = page.Grid.ItemsSource!.Cast<MediaLibraryTileRow>().ToArray();
        MediaLibraryTileRow other = rows[0], bound = rows.Single(row => row.Entry.PostId == "100"), loose = rows.Single(row => row.Entry.PostId == null && row.Entry.Thread == ThreadA);
        page.Grid.SelectedItem = other;
        Control boundControl = await ContainerAsync(page.Grid, bound.Index);

        await RightClickAsync(boundControl, shell);
        ContextMenu menu = page.MediaMenu ?? throw new InvalidOperationException("A right-click on a media tile did not open the shared menu.");
        Require(menu.IsOpen && page.MediaMenuSession!.Target == MediaMenuTarget.Of(bound.Entry),
            "Right-clicking the bound tile while another tile is selected did not freeze the clicked occurrence.");
        Require(ReferenceEquals(page.Grid.SelectedItem, other), "A right-click moved the selection.");
        string[] boundSnapshot = Snapshot(menu);
        Console.WriteLine("SMOKE media menu media page (bound): " + string.Join(" | ", boundSnapshot));
        RequireOrder(menu);
        RequireContextSectionLast(menu);
        Require(menu.Items.OfType<MenuItem>().Any(item => item.Tag as string == "media.filter.onlyThread"),
            "The media page lost its own context-specific section.");
        Capture(menu, "media-page");
        menu.Close();

        // Rebuild: sorting replaces the rows, so the frozen menu must refuse instead of acting on a different tile.
        await RightClickAsync(boundControl, shell);
        MediaMenuSession frozen = page.MediaMenuSession!;
        ContextMenu frozenMenu = page.MediaMenu!;
        MenuItem details = frozenMenu.Items.OfType<MenuItem>().Single(item => item.Tag as string == "media.details");
        ComboBox sort = C<ComboBox>(page, "MediaSort");
        int index = sort.SelectedIndex;
        sort.SelectedIndex = index == 1 ? 0 : 1;
        await UntilAsync(() => page.BuildTask.IsCompleted);
        Require(!frozen.IsCurrent && !details.IsEnabled && details.Command?.CanExecute(null) != true,
            "A rebuilt media page left the frozen menu usable.");
        sort.SelectedIndex = index;
        await UntilAsync(() => page.BuildTask.IsCompleted);

        // Keyboard: Menu key and Shift+F10 on the focused tile open the same menu for that tile.
        rows = page.Grid.ItemsSource!.Cast<MediaLibraryTileRow>().ToArray();
        bound = rows.Single(row => row.Entry.PostId == "100");
        boundControl = await ContainerAsync(page.Grid, bound.Index);
        page.Grid.SelectedItem = rows.First(row => row != bound);
        foreach ((Key key, KeyModifiers modifiers) in new[] { (Key.Apps, KeyModifiers.None), (Key.F10, KeyModifiers.Shift) })
        {
            boundControl.Focus();
            await KeyAsync(boundControl, key, modifiers);
            Require(page.MediaMenu?.IsOpen == true && page.MediaMenuSession!.Target == MediaMenuTarget.Of(bound.Entry),
                "The " + key + " gesture did not open the menu on the focused tile.");
            Same("media page keyboard", Snapshot(page.MediaMenu!), "media page pointer", boundSnapshot);
            page.MediaMenu!.Close();
            await IdleAsync();
            Require(shell.FocusManager?.GetFocusedElement() is Control focused && (ReferenceEquals(focused, boundControl)
                || focused.GetVisualAncestors().Contains(boundControl) || boundControl.GetVisualDescendants().Contains(focused)),
                "Closing the keyboard menu did not return the focus to the tile it came from.");
        }

        // Loose file: tagged as its own local occurrence, no post to show in a thread, said honestly.
        loose = rows.Single(row => row.Entry.PostId == null && row.Entry.Thread == ThreadA);
        await RightClickAsync(await ContainerAsync(page.Grid, loose.Index), shell);
        Require(page.MediaMenuSession?.Target == MediaMenuTarget.Of(loose.Entry), "The loose tile menu is not frozen to the loose occurrence.");
        MenuItem looseThread = page.MediaMenu!.Items.OfType<MenuItem>().Single(item => item.Tag as string == "gallery.show-in-thread");
        Require(!looseThread.IsEnabled && (AutomationProperties.GetHelpText(looseThread) ?? "").Contains(Texts.T("gallery.noPost"), StringComparison.Ordinal),
            "A loose file must say that it has no post instead of showing in a thread.");
        page.MediaMenu.Close();
        return boundSnapshot;
    }

    private static async Task<(string[] Table, string[] Tiles, string[] Inline, string[] Loose)> ArchiveJourneyAsync(MainWindow shell)
    {
        shell.FindControl<MenuItem>("ArchiveMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        ArchivePage page = shell.ArchivePage;
        await UntilAsync(() => shell.SelectedPage == MainWindow.WorkspacePage.Archive && !page.IsBusy);
        await page.LoadThreadAsync(ThreadA).WaitAsync(Deadline);
        await page.MediaLoadTask.WaitAsync(Deadline);

        // Inline attachment first: the reader opens on the posts, which hold the bound file under post 100.
        ArchiveAttachmentView? inlineView = null;
        await UntilAsync(() => (inlineView = page.FindControl<ListBox>("ArchivePosts")!.GetVisualDescendants().OfType<ArchiveAttachmentView>()
            .FirstOrDefault(view => view.IsEffectivelyVisible && view.DataContext is ArchiveAttachmentRow)) != null);
        await RightClickAsync(inlineView!, shell);
        ContextMenu inlineMenu = page.ArchiveMediaMenu ?? throw new InvalidOperationException("A right-click on an inline attachment did not open the shared menu.");
        Require(inlineMenu.IsOpen && page.ArchiveMediaMenuSession!.Target?.PostId == "100", "The inline menu is not frozen to post 100's attachment.");
        string[] inline = Snapshot(inlineMenu);
        Console.WriteLine("SMOKE media menu archive inline: " + string.Join(" | ", inline));
        RequireOrder(inlineMenu);
        RequireContextSectionLast(inlineMenu);
        Capture(inlineMenu, "archive-inline");
        inlineMenu.Close();
        await KeyAsync(inlineView!, Key.Apps, KeyModifiers.None);
        Require(page.ArchiveMediaMenu?.IsOpen == true && page.ArchiveMediaMenuSession!.Target?.PostId == "100", "The Menu key did not open the inline attachment's menu.");
        Same("archive inline keyboard", Snapshot(page.ArchiveMediaMenu!), "archive inline pointer", inline);
        page.ArchiveMediaMenu!.Close();

        page.FindControl<TabControl>("ArchiveContentTabs")!.SelectedIndex = 1;
        string[] table = Array.Empty<string>(), tiles = Array.Empty<string>(), loose = Array.Empty<string>();
        foreach (bool useTiles in new[] { false, true })
        {
            page.MediaTilesPreference = useTiles;
            await IdleAsync();
            Control list = useTiles ? page.FindControl<ListBox>("ArchiveMediaTiles")! : page.FindControl<DataGrid>("ArchiveMediaGrid")!;
            ArchiveAssetRow[] rows = ((System.Collections.IEnumerable)(useTiles ? ((ListBox)list).ItemsSource! : ((DataGrid)list).ItemsSource!)).Cast<ArchiveAssetRow>().ToArray();
            Require(rows.Length == 2, "The archive fixture must show its bound and its loose file.");
            ArchiveAssetRow bound = rows.Single(row => (row.Asset.OriginalFileName ?? row.Asset.Path).EndsWith("one.png", StringComparison.Ordinal));
            ArchiveAssetRow looseRow = rows.Single(row => !ReferenceEquals(row, bound));
            void Select(ArchiveAssetRow row) { if (list is ListBox box) box.SelectedItem = row; else ((DataGrid)list).SelectedItem = row; }
            Select(looseRow);
            Control boundControl = await RowControlAsync(list, bound);
            await RightClickAsync(boundControl, shell);
            ContextMenu menu = page.ArchiveMediaMenu ?? throw new InvalidOperationException("A right-click on an archive row did not open the shared menu.");
            MediaMenuTarget target = page.ArchiveMediaMenuSession!.Target ?? throw new InvalidOperationException("The archive menu has no target.");
            Require(menu.IsOpen && target.AssetId == bound.Asset.AssetId && target.PostId == "100"
                && page.ArchiveMediaMenuSession.Surface == (useTiles ? MediaMenuSurface.ArchiveTiles : MediaMenuSurface.ArchiveTable),
                "Right-clicking the bound row while the loose row is selected did not freeze the clicked occurrence.");
            // The platform may move the list's selection to the clicked row; the menu stays on the clicked one regardless.
            object? selected = list is ListBox b ? b.SelectedItem : ((DataGrid)list).SelectedItem;
            Require(ReferenceEquals(selected, looseRow) || ReferenceEquals(selected, bound), "A right-click selected a third row.");
            string[] snapshot = Snapshot(menu);
            Console.WriteLine("SMOKE media menu archive " + (useTiles ? "tiles" : "table") + ": " + string.Join(" | ", snapshot));
            RequireOrder(menu);
            Capture(menu, useTiles ? "archive-tiles" : "archive-table");
            menu.Close();
            if (useTiles) tiles = snapshot; else table = snapshot;

            // Keyboard on the focused row.
            foreach ((Key key, KeyModifiers modifiers) in new[] { (Key.Apps, KeyModifiers.None), (Key.F10, KeyModifiers.Shift) })
            {
                boundControl.Focus();
                await KeyAsync(boundControl, key, modifiers);
                Require(page.ArchiveMediaMenu?.IsOpen == true && page.ArchiveMediaMenuSession!.Target?.AssetId == bound.Asset.AssetId,
                    "The " + key + " gesture did not open the archive menu on the focused row (menu=" + (page.ArchiveMediaMenu == null ? "none" : page.ArchiveMediaMenu.IsOpen ? "open" : "closed")
                    + ", target=" + page.ArchiveMediaMenuSession?.Target?.AssetId + ", focus=" + shell.FocusManager?.GetFocusedElement()?.GetType().Name + ").");
                Same("archive keyboard " + key, Snapshot(page.ArchiveMediaMenu!), "archive pointer", snapshot);
                page.ArchiveMediaMenu!.Close();
                await IdleAsync();
            }

            // The loose row: its own local occurrence, no post.
            Select(bound);
            await RightClickAsync(await RowControlAsync(list, looseRow), shell);
            Require(page.ArchiveMediaMenuSession?.Target?.AssetId == looseRow.Asset.AssetId && page.ArchiveMediaMenuSession.Target.PostId == null,
                "The loose row menu is not frozen to the loose occurrence.");
            MenuItem inThread = page.ArchiveMediaMenu!.Items.OfType<MenuItem>().Single(item => item.Tag as string == "gallery.show-in-thread");
            Require(!inThread.IsEnabled && (AutomationProperties.GetHelpText(inThread) ?? "").Contains(Texts.T("gallery.noPost"), StringComparison.Ordinal),
                "A loose archive file must say that it has no post.");
            if (useTiles) loose = Snapshot(page.ArchiveMediaMenu);
            page.ArchiveMediaMenu.Close();
            await ArchiveSelectionJourneyAsync(shell, page, list, bound, looseRow, useTiles);
        }

        // Thread change retires an open menu: the open menu never acts on the new thread's rows.
        ListBox tileList = page.FindControl<ListBox>("ArchiveMediaTiles")!;
        ArchiveAssetRow current = tileList.ItemsSource!.Cast<ArchiveAssetRow>().First();
        await RightClickAsync(await RowControlAsync(tileList, current), shell);
        MediaMenuSession session = page.ArchiveMediaMenuSession!;
        ContextMenu retiring = page.ArchiveMediaMenu!;
        MenuItem retiringDetails = retiring.Items.OfType<MenuItem>().Single(item => item.Tag as string == "media.details");
        await page.LoadThreadAsync(new ResourceId("media-fixture", "g", "200")).WaitAsync(Deadline);
        await page.MediaLoadTask.WaitAsync(Deadline);
        Require(!session.IsCurrent && !retiringDetails.IsEnabled && retiringDetails.Command?.CanExecute(null) != true,
            "Loading another thread left the frozen archive menu usable.");
        retiring.Close();
        await page.LoadThreadAsync(ThreadA).WaitAsync(Deadline);
        await page.MediaLoadTask.WaitAsync(Deadline);

        // Open the gallery from the archive's own menu so the gallery shows exactly the entries users get.
        page.MediaTilesPreference = true;
        await IdleAsync();
        ListBox tilesNow = page.FindControl<ListBox>("ArchiveMediaTiles")!;
        ArchiveAssetRow openRow = tilesNow.ItemsSource!.Cast<ArchiveAssetRow>().Single(row => (row.Asset.OriginalFileName ?? row.Asset.Path).EndsWith("one.png", StringComparison.Ordinal));
        await RightClickAsync(await RowControlAsync(tilesNow, openRow), shell);
        MenuItem open = page.ArchiveMediaMenu!.Items.OfType<MenuItem>().Single(item => item.Tag as string == "gallery.open");
        Require(open.IsEnabled && open.Command!.CanExecute(null), "The archive menu cannot open the gallery.");
        open.Command!.Execute(null);
        page.ArchiveMediaMenu.Close();
        return (table, tiles, inline, loose);
    }

    // T3: several files selected in the thread's Media tab. Ctrl+A selects every shown file, the status line counts them, the menu keeps its
    // common section on the clicked file and adds the selection actions, Esc clears. One selected file never shows the selection actions.
    private static async Task ArchiveSelectionJourneyAsync(MainWindow shell, ArchivePage page, Control list, ArchiveAssetRow bound,
        ArchiveAssetRow looseRow, bool useTiles)
    {
        string mode = useTiles ? "tiles" : "table";
        list.Focus();
        await KeyAsync(list, Key.A, KeyModifiers.Control);
        Require(page.SelectedMediaRows().Count == 2, "Ctrl+A did not select both archive files in " + mode + " mode.");
        WrapPanel bar = page.FindControl<WrapPanel>("ArchiveMediaSelectionBar")!;
        TextBlock status = page.FindControl<TextBlock>("ArchiveMediaSelectionStatus")!;
        Require(bar.IsVisible && status.Text == Texts.T("media.selection.count", 2), "The selection status is missing or wrong in " + mode + " mode: " + status.Text);

        await RightClickAsync(await RowControlAsync(list, bound), shell);
        ContextMenu menu = page.ArchiveMediaMenu ?? throw new InvalidOperationException("The archive menu did not open on a selected file.");
        Require(page.SelectedMediaRows().Count == 2, "A right-click on a selected file collapsed the selection in " + mode + " mode.");
        Require(page.ArchiveMediaMenuSession!.Target?.AssetId == bound.Asset.AssetId, "The menu is not frozen to the clicked file of the selection.");
        string[] tags = menu.Items.OfType<MenuItem>().Select(item => item.Tag as string ?? string.Empty).ToArray();
        foreach (string expected in new[] { "media.menu.note", "media.selection.tag", "media.selection.gallery", "media.selection.export", "media.selection.clear", "gallery.open" })
            Require(tags.Contains(expected), "The menu on a selection lacks " + expected + " in " + mode + " mode: " + string.Join(",", tags));
        MenuItem open = menu.Items.OfType<MenuItem>().Single(item => item.Tag as string == "gallery.open");
        Require(open.IsEnabled, "Open in gallery must stay available on the focused file while several are selected.");
        Require(menu.Items.OfType<MenuItem>().Single(item => item.Tag as string == "media.selection.export").IsEnabled
            && menu.Items.OfType<MenuItem>().Single(item => item.Tag as string == "media.selection.gallery").IsEnabled, "The selection export and gallery must be available.");
        Console.WriteLine("SMOKE media menu archive selection " + mode + ": " + string.Join(" | ", Snapshot(menu)));
        MenuItem tag = menu.Items.OfType<MenuItem>().Single(item => item.Tag as string == "media.selection.tag");
        if (tag.IsEnabled)
        {
            tag.Command!.Execute(null);
            await UntilAsync(() => page.MediaSelectionTagWindow != null);
            Require(((MediaSelectionTagDialog)page.MediaSelectionTagWindow!).Session.Candidates.Count >= 2, "Tag together did not list the occurrences of both selected files.");
            page.MediaSelectionTagWindow!.Close();
            await UntilAsync(() => page.MediaSelectionTagWindow == null);
        }
        menu.Close();
        await IdleAsync();

        list.Focus();
        await KeyAsync(list, Key.Escape, KeyModifiers.None);
        Require(page.SelectedMediaRows().Count == 0 && !bar.IsVisible, "Esc did not clear the selection in " + mode + " mode.");

        // One selected file is just the current file: no selection actions, no status line.
        if (list is ListBox box) box.SelectedItem = looseRow; else ((DataGrid)list).SelectedItem = looseRow;
        await IdleAsync();
        Require(!bar.IsVisible, "A single selected file must not show the selection status.");
        await RightClickAsync(await RowControlAsync(list, looseRow), shell);
        Require(!page.ArchiveMediaMenu!.Items.OfType<MenuItem>().Any(item => (item.Tag as string ?? string.Empty).StartsWith("media.selection.", StringComparison.Ordinal)),
            "The menu on a single selected file offers selection actions.");
        page.ArchiveMediaMenu.Close();
        await IdleAsync();
    }

    private static async Task GalleryJourneyAsync(MainWindow shell, string[] mediaPageBound, string[] archiveTiles)
    {
        ContentControl host = shell.FindControl<ContentControl>("GalleryHost")!;
        await UntilAsync(() => host.Content is GalleryViewer);
        GalleryViewer viewer = (GalleryViewer)host.Content!;
        await UntilAsync(() => viewer.CurrentEntry != null && viewer.TagsButton.IsEffectivelyEnabled);
        Grid? found = null;
        await UntilAsync(() => (found = viewer.GetGalleryVisualDescendants().OfType<Grid>().FirstOrDefault(grid => grid.Name == "GalleryViewport")) != null);
        Grid viewport = found!;
        GalleryEntry shown = viewer.CurrentEntry!;
        Require(shown.Post?.PostId == "100", "The gallery did not start at the clicked bound occurrence.");

        await RightClickAsync(viewport, shell);
        ContextMenu menu = viewer.MediaMenu ?? throw new InvalidOperationException("A right-click on the gallery viewport did not open the shared menu.");
        Require(menu.IsOpen && viewer.MediaMenuSession!.Target == MediaMenuTarget.Of(shown), "The gallery menu is not frozen to the shown medium.");
        string[] pointer = Snapshot(menu);
        Console.WriteLine("SMOKE media menu gallery: " + string.Join(" | ", pointer));
        RequireOrder(menu, inGallery: true);
        MenuItem tags = menu.Items.OfType<MenuItem>().Single(item => item.Tag as string == "media.tags.edit");
        Require(tags.IsEnabled && Equals(tags.Header, Texts.T("media.tags.edit")), "The gallery menu does not offer the media tags.");
        // "Open in gallery" is meaningless inside the gallery; every other common action agrees with the other surfaces.
        Same("gallery pointer", pointer, "archive tiles", WithoutOpen(archiveTiles));
        Same("gallery pointer", pointer, "media page", WithoutOpen(mediaPageBound));
        Capture(menu, "gallery");
        menu.Close();
        await IdleAsync();

        foreach ((Key key, KeyModifiers modifiers) in new[] { (Key.Apps, KeyModifiers.None), (Key.F10, KeyModifiers.Shift) })
        {
            viewer.Focus();
            await KeyAsync(viewer, key, modifiers);
            Require(viewer.MediaMenu?.IsOpen == true && viewer.MediaMenuSession!.Target == MediaMenuTarget.Of(shown),
                "The " + key + " gesture did not open the gallery menu.");
            Same("gallery " + key, Snapshot(viewer.MediaMenu!), "gallery pointer", pointer);
            viewer.MediaMenu!.Close();
        }

        // The archive's gallery holds one medium; navigation needs a bound and a loose one (the gallery tag fixtures).
        GalleryViewerChecksAccess.Click(viewer, "GalleryClose");
        await shell.GalleryCleanupTask.WaitAsync(Deadline);
        string fixtures = Path.Combine(shell.SessionHost.ProfileDirectory, "media-menu-gallery-fixture");
        GalleryEntry boundFixture = await GalleryMediaTagChecks.BoundAsync(Path.Combine(fixtures, "bound"));
        GalleryEntry looseFixture = await GalleryMediaTagChecks.UnboundAsync(Path.Combine(fixtures, "loose"));
        await shell.OpenMediaGalleryAsync([boundFixture, looseFixture], boundFixture.EntryKey);
        await UntilAsync(() => host.Content is GalleryViewer);
        viewer = (GalleryViewer)host.Content!;
        await UntilAsync(() => viewer.CurrentEntry != null && viewer.TagsButton.IsEffectivelyEnabled);
        viewport = null!;
        await UntilAsync(() => (viewport = viewer.GetGalleryVisualDescendants().OfType<Grid>().FirstOrDefault(grid => grid.Name == "GalleryViewport")!) != null);

        // Navigation retires an open menu: it must never act on the next medium.
        await RightClickAsync(viewport, shell);
        MediaMenuSession session = viewer.MediaMenuSession!;
        MenuItem details = viewer.MediaMenu!.Items.OfType<MenuItem>().Single(item => item.Tag as string == "media.details");
        GalleryViewerChecksAccess.Click(viewer, "GalleryNext");
        try { await UntilAsync(() => viewer.CurrentEntry != null && viewer.CurrentEntry.Post == null); }
        catch (TimeoutException)
        {
            throw new InvalidOperationException("The gallery did not move to the loose medium: current post=" + viewer.CurrentEntry?.Post?.PostId
                + ", asset=" + viewer.CurrentEntry?.Asset.OriginalFileName + ", next enabled="
                + viewer.GetGalleryVisualDescendants().OfType<Button>().FirstOrDefault(button => button.Name == "GalleryNext")?.IsEffectivelyEnabled);
        }
        Require(!session.IsCurrent && !details.IsEnabled && details.Command?.CanExecute(null) != true,
            "Navigating the gallery left the frozen menu usable.");

        // The loose medium: the menu belongs to the new medium and states honest reasons.
        await RightClickAsync(viewport, shell);
        GalleryEntry loose = viewer.CurrentEntry!;
        Require(viewer.MediaMenuSession!.Target == MediaMenuTarget.Of(loose), "The gallery menu did not follow the shown loose medium.");
        MenuItem looseThread = viewer.MediaMenu!.Items.OfType<MenuItem>().Single(item => item.Tag as string == "gallery.show-in-thread");
        Require(!looseThread.IsEnabled && (AutomationProperties.GetHelpText(looseThread) ?? "").Contains(Texts.T("gallery.noPost"), StringComparison.Ordinal),
            "The gallery must say that a loose medium has no post.");
        viewer.MediaMenu.Close();
        GalleryViewerChecksAccess.Click(viewer, "GalleryClose");
        await shell.GalleryCleanupTask.WaitAsync(Deadline);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------------

    private static string[] Snapshot(ContextMenu menu) => menu.Items.OfType<MenuItem>()
        .Where(item => item.Tag is string tag && (tag == "media.open" || Order.Contains(tag)))
        .Select(item =>
        {
            string tag = item.Tag as string == "media.open" ? "gallery.open" : (string)item.Tag!;
            string check = item.ToggleType == MenuItemToggleType.CheckBox ? (item.IsChecked ? "checked" : "unchecked") : "-";
            return tag + "|" + item.Header + "|" + (item.IsEnabled ? "enabled" : "disabled:" + AutomationProperties.GetHelpText(item)) + "|" + check;
        }).ToArray();

    private static string[] WithoutOpen(string[] snapshot) => snapshot.Where(line => !line.StartsWith("gallery.open|", StringComparison.Ordinal)).ToArray();

    private static void RequireOrder(ContextMenu menu, bool inGallery = false)
    {
        string[] tags = menu.Items.OfType<MenuItem>().Select(item => item.Tag as string == "media.open" ? "gallery.open" : item.Tag as string ?? "")
            .Where(Order.Contains).ToArray();
        Require(tags.SequenceEqual(inGallery ? Order.Skip(1) : Order), "The common media section is not in the shared order: " + string.Join(", ", tags));
    }

    // Everything the surface adds itself must come after the common section.
    private static void RequireContextSectionLast(ContextMenu menu)
    {
        string[] tags = menu.Items.OfType<MenuItem>().Select(item => item.Tag as string ?? "<untagged>").ToArray();
        int lastCommon = Array.FindLastIndex(tags, tag => tag == "media.open" || Order.Contains(tag));
        Require(lastCommon >= 0 && tags.Take(lastCommon + 1).All(tag => tag == "media.open" || Order.Contains(tag)),
            "A context-specific item sits inside the common section: " + string.Join(", ", tags));
    }

    private static void Same(string leftName, string[] left, string rightName, string[] right) =>
        Require(left.SequenceEqual(right), "The media menus of " + leftName + " and " + rightName + " disagree:" + Environment.NewLine
            + string.Join(Environment.NewLine, left) + Environment.NewLine + "--" + Environment.NewLine + string.Join(Environment.NewLine, right));

    private static void Capture(ContextMenu menu, string surface)
    {
        if (menu.Bounds.Width <= 0 || menu.Bounds.Height <= 0) return;
        double scale = TopLevel.GetTopLevel(menu)?.RenderScaling ?? 1;
        using RenderTargetBitmap image = new(PixelSize.FromSize(menu.Bounds.Size, scale), new Vector(96 * scale, 96 * scale));
        image.Render(menu);
        image.Save(Path.Combine(AppContext.BaseDirectory, "smoke-media-menu-" + surface + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    private static T C<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private static async Task<Control> ContainerAsync(ListBox grid, int index)
    {
        grid.ScrollIntoView(index);
        Control? container = null;
        await UntilAsync(() => (container = grid.ContainerFromIndex(index)) is { IsEffectivelyVisible: true, Bounds.Width: > 0 });
        return container!;
    }

    private static async Task<Control> RowControlAsync(Control list, ArchiveAssetRow row)
    {
        Control? found = null;
        await UntilAsync(() => (found = list.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(control => ReferenceEquals(control.DataContext, row) && control is ListBoxItem or DataGridRow
                && control.IsEffectivelyVisible && control.Bounds.Width > 0)) != null);
        return found!;
    }

    // A real right-button press and release at the middle of the control: Avalonia turns it into ContextRequested.
    private static async Task RightClickAsync(Control target, TopLevel root)
    {
        Point at = target.TranslatePoint(new Point(Math.Max(1, target.Bounds.Width / 2), Math.Max(1, target.Bounds.Height / 2)), root) ?? new Point(40, 40);
        using Pointer pointer = new(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        target.RaiseEvent(new PointerPressedEventArgs(target, pointer, root, at, 0,
            new PointerPointProperties(RawInputModifiers.RightMouseButton, PointerUpdateKind.RightButtonPressed), KeyModifiers.None));
        target.RaiseEvent(new PointerReleasedEventArgs(target, pointer, root, at, 1,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.RightButtonReleased), KeyModifiers.None, MouseButton.Right));
        pointer.Capture(null);
        await IdleAsync();
    }

    private static async Task KeyAsync(Control target, Key key, KeyModifiers modifiers)
    {
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = key, KeyModifiers = modifiers });
        await IdleAsync();
    }

    private static Task IdleAsync() => Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background).GetTask();

    private static async Task UntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + Deadline;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Timed out waiting for media menu surface state.");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Task.Delay(10);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
