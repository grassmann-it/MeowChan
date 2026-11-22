using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan.DesktopSmoke;

// Page, page-mode and work-view-tier scenes of the UI catalog run.
internal static partial class UiCatalogChecks
{
    private static readonly string[] SettingsCategoryIds =
        ["settings.interface", "settings.reading-media", "settings.downloads", "settings.worker-budgets", "settings.updates",
         "settings.notifications", "settings.behavior", "settings.workviews", "settings.shortcuts"];

    private static T Named<T>(ArchivePage page, string name) where T : Control =>
        page.FindControl<T>(name) ?? throw new InvalidOperationException("Missing archive control " + name);

    private static partial void AddPageScenes(List<Scene> s)
    {
        s.Add(new("page-downloads", async w => { await Enter(w, "downloads"); await PageAsync(w, "page-downloads", "Threads tab, passive synthetic rows"); }));
        s.Add(new("page-downloads-boards", async w =>
        {
            await Enter(w, "downloads");
            TabControl tabs = w.FindControl<TabControl>("DownloadsTabs")!;
            tabs.SelectedIndex = 1;
            await PageAsync(w, "page-downloads-boards", "Boards tab", reenter: () => { tabs.SelectedIndex = 1; return Task.CompletedTask; });
            tabs.SelectedIndex = 0;
        }));
        s.Add(new("page-archive", async w => { await Enter(w, "archive"); await PageAsync(w, "page-archive", "Archive list layout, showcase archive of 56 threads", reenter: () => Enter(w, "archive")); }));
        s.Add(new("page-archive-table", async w =>
        {
            await Enter(w, "archive");
            ArchivePage a = w.ArchivePage;
            a.ArchiveLayoutPreference = "Table";
            try
            {
                await UntilAsync(() => Find<DataGrid>(a, "ArchiveThreadTable") is { IsVisible: true, Bounds.Height: > 0 }, "the table layout did not show");
                await PageAsync(w, "page-archive-table", "Archive table layout (columns, favorite star column)", reenter: () => Enter(w, "archive"));
            }
            finally { a.ArchiveLayoutPreference = "List"; await Settle(w, 150); }
        }));
        s.Add(new("page-archive-filters", async w =>
        {
            await Enter(w, "archive");
            ArchivePage a = w.ArchivePage;
            a.ToggleFilterSurfaceByShortcut();
            try
            {
                await UntilAsync(() => a.IsFilterSurfaceOpen && Find<Border>(a, "ArchiveFilterSurface") is { IsVisible: true }, "the filter surface did not open");
                await PageAsync(w, "page-archive-filters", "Filters and sorting surface (OverlayFrame, header with title and cross)", narrow: false);
            }
            finally { a.CloseFilterSurface(restoreFocus: false); await Settle(w, 150); }
        }));
        s.Add(new("reader", ReaderAsync));
        s.Add(new("gallery", GalleryAsync));
        s.Add(new("gallery-tagging", GalleryTaggingAsync));
        s.Add(new("media", MediaAsync));
        s.Add(new("booru", BooruAsync));
        s.Add(new("page-rules", async w =>
        {
            await WorkViewCaptures.EnterAsync(w, "rules");
            // EnterAsync opens the editor wizard (the tier smoke's view); the overview is what the page shows first.
            await StepAsync("page-rules-wizard", async () => { await PageAsync(w, "page-rules-wizard", "Global rule open in the in-page wizard", narrow: false); });
            await StepAsync("page-rules", async () =>
            {
                Click(Need<Button>(w.RulesPage, "RuleWizardCloseButton"));
                await Settle(w, 400);
                await PageAsync(w, "page-rules", "Rules overview (saved, drafts and defaults)");
            });
        }));
        s.Add(new("page-sources", async w => { await Enter(w, "sources"); await PageAsync(w, "page-sources", "Sources & Plugins, Sources list", reenter: () => Enter(w, "sources")); }));
        s.Add(new("page-plugins", async w => { await Enter(w, "plugins"); await PageAsync(w, "page-plugins", "Sources & Plugins filtered to all plugins", reenter: () => Enter(w, "plugins")); }));
        s.Add(new("page-status-statistics", async w => { await Enter(w, "statistics"); await PageAsync(w, "page-status-statistics", "Status page, Statistics segment", reenter: () => Enter(w, "statistics")); }));
        s.Add(new("page-status-diagnostics", async w => { await Enter(w, "diagnostics"); await PageAsync(w, "page-status-diagnostics", "Status page, Diagnostics segment", reenter: () => Enter(w, "diagnostics")); }));
        s.Add(new("settings", SettingsAsync));
        s.Add(new("page-tagging", TaggingPageAsync));
    }

    // ---- Reader: Posts/Media/References tabs, find bar, tag bar, the media sidebar and the view options ----

    private static async Task ReaderAsync(MainWindow w)
    {
        await Enter(w, "reader");
        ArchivePage a = w.ArchivePage;
        await PageAsync(w, "page-reader", "Reader, Posts tab, showcase thread", reenter: () => Enter(w, "reader"));
        TabControl tabs = Named<TabControl>(a, "ArchiveContentTabs");
        await StepAsync("reader-media-tab", async () => { tabs.SelectedIndex = 1; await Settle(w, 600); UiCatalog.Record(w, "reader-media-tab", Standard, "Media tab of the reader"); });
        await StepAsync("reader-references-tab", async () => { tabs.SelectedIndex = 2; await Settle(w, 400); UiCatalog.Record(w, "reader-references-tab", Standard, "References tab"); });
        tabs.SelectedIndex = 0;
        await Settle(w, 200);
        await StepAsync("reader-find-bar", async () =>
        {
            Click(Named<Button>(a, "ReaderFindOpen"));
            await UntilAsync(() => Named<Control>(a, "ReaderFindBar").IsVisible, "the find bar did not open");
            await Settle(w, 250);
            UiCatalog.Record(w, "reader-find-bar", Standard, "Find in thread bar (scope chip, previous/next, close at the right edge)");
            Click(Named<Button>(a, "ReaderFindOpen"));
            await Settle(w, 150);
        });
        await StepAsync("reader-tag-bar", async () =>
        {
            Click(Named<Button>(a, "ReaderTagsOpen"));
            await UntilAsync(() => Named<Control>(a, "ReaderTagBar").IsVisible, "the thread tag bar did not open");
            await Settle(w, 250);
            UiCatalog.Record(w, "reader-tag-bar", Standard, "Thread tag bar");
            Click(Named<Button>(a, "ReaderTagsOpen"));
            await Settle(w, 150);
        });
        foreach ((string option, string key, string note) in new[]
        {
            ("reader.sidebar.toggle", "reader-sidebar", "Media sidebar on (Primary+Shift+M)"),
            ("reader.view.conversation", "reader-view-conversation", "View: conversation (indented replies)"),
            ("reader.view.media-only", "reader-view-media-only", "View: posts with media only"),
            ("reader.view.hide-media", "reader-view-hide-media", "View: media hidden"),
            ("reader.view.textless-large", "reader-view-textless-large", "View: textless posts large"),
            ("reader.view.focused-enlarge", "reader-view-focused-enlarge", "View: enlarge the focused post"),
            ("reader.view.hover-enlarge", "reader-view-hover-enlarge", "View: enlarge on hover"),
        })
            await StepAsync(key, async () =>
            {
                if (!a.ReaderViewOptionIds.Contains(option)) throw new InvalidOperationException("The reader has no view option " + option);
                a.ToggleReaderViewOption(option);
                try { await a.WaitForReaderViewAsync(); await Settle(w, 450); UiCatalog.Record(w, key, Standard, note); }
                finally { a.ToggleReaderViewOption(option); await a.WaitForReaderViewAsync(); await Settle(w, 150); }
            });
        await StepAsync("reader-quick-settings", async () =>
        {
            a.OpenReaderQuickSettings();
            await UntilAsync(() => a.IsQuickSettingsOpen && a.ReaderQuickSettings?.Surface is { Bounds.Width: > 0 }, "the quick settings popover did not open");
            await Settle(w, 300);
            UiCatalog.RecordSurface(SurfaceOf(a.ReaderQuickSettings!.Surface, w), "reader-quick-settings", "popover", "Reader settings popover (title left, cross alone at the right edge)");
            Press(a.ReaderQuickSettings.Surface, Key.Escape);
            await Settle(w, 150);
        });
        await StepAsync("reader-thread-menu", async () =>
            await OpenedMenuAsync(w, a.BuildReaderThreadMenu(), Named<Button>(a, "ReaderThreadOpen"), "reader-thread-menu", "Thread menu of the reader bar"));
    }

    // ---- Gallery viewer: View mode, post panel, tag panel, filmstrip ----

    private static GalleryViewer ViewerOf(MainWindow w) =>
        w.FindControl<ContentControl>("GalleryHost")!.Content as GalleryViewer ?? throw new InvalidOperationException("The gallery is not open.");

    private static async Task GalleryAsync(MainWindow w)
    {
        await Enter(w, "gallery");
        GalleryViewer viewer = ViewerOf(w);
        await PageAsync(w, "page-gallery", "Gallery viewer, View mode (toolbar, picture, controls)", reenter: () => Enter(w, "gallery"));
        await StepAsync("gallery-post-panel-segment", async () =>
        {
            if (!viewer.CaptureState().ContextOpen) Press(viewer, Key.P);
            await UntilAsync(() => viewer.CaptureState().ContextOpen, "the post panel did not open");
            await Settle(w, 400);
            UiCatalog.Record(w, "gallery-post-panel-segment", Standard, "Side panel, Post segment (post card)");
        });
        await StepAsync("gallery-tag-panel-segment", async () =>
        {
            if (!viewer.IsTagPanelOpen) Press(viewer, Key.T, Primary | KeyModifiers.Shift);
            await UntilAsync(() => viewer.IsTagPanelOpen && viewer.TagSurface is { IsVisible: true }, "the tag panel did not open");
            await Settle(w, 500);
            UiCatalog.Record(w, "gallery-tag-panel-segment", Standard, "Side panel, Tags segment (quick tag surface)");
        });
        await StepAsync("gallery-filmstrip", async () =>
        {
            if (!viewer.IsFilmstripVisible) viewer.ToggleFilmstrip();
            await Settle(w, 450);
            UiCatalog.Record(w, "gallery-filmstrip", Standard, "Filmstrip on");
        });
    }

    // Tagging mode (Tag) and card mode over a queue of one bound and two loose synthetic media.
    private static async Task GalleryTaggingAsync(MainWindow w)
    {
        string root = Path.Combine(w.SessionHost.ProfileDirectory, "ui-catalog-tagging");
        DesktopSessionHost host = w.SessionHost;
        GalleryEntry bound = await GalleryMediaTagChecks.BoundAsync(Path.Combine(root, "bound"));
        GalleryEntry looseA = await GalleryMediaTagChecks.UnboundAsync(Path.Combine(root, "loose-a"));
        GalleryEntry looseB = await GalleryMediaTagChecks.UnboundAsync(Path.Combine(root, "loose-b"));
        GalleryEntry[] entries = [bound, looseA, looseB];
        (IReadOnlyList<TagQueueItem> items, int _) = TagQueueActions.ItemsOf(entries, DateTimeOffset.UtcNow);
        await host.EnqueueTagQueueAsync(items);
        try
        {
            TagQueueSnapshot queue = await host.LoadTagQueueAsync();
            await w.OpenTaggingWalkAsync(queue, queue.Items, entries, bound.EntryKey);
            await GalleryViewerChecksAccess.WaitForLayoutAsync(w);
            GalleryViewer viewer = ViewerOf(w);
            await UntilAsync(() => viewer.IsTaggingMode && viewer.IsTagPanelOpen && viewer.TaggingStrip.IsEffectivelyVisible, "the tagging strip or panel is missing");
            await Settle(w, 500);
            Task Reenter() => Task.CompletedTask;
            await PageAsync(w, "gallery-tagging-mode", "Gallery Tag mode: decision strip (Back, Skip, Done, Stamp) and tag panel", reenter: Reenter);
            await StepAsync("gallery-tagging-cards", async () =>
            {
                viewer.SetCardMode(true, persist: false);
                await UntilAsync(() => viewer.IsCardMode && viewer.TaggingCardsRow.IsEffectivelyVisible, "the card mode did not show the cards");
                await Settle(w, 500);
                await PageAsync(w, "gallery-tagging-cards", "Gallery Tag mode with up to five decision cards (K)", reenter: Reenter);
                viewer.SetCardMode(false, persist: false);
            });
        }
        finally
        {
            try
            {
                if (w.FindControl<ContentControl>("GalleryHost")!.Content is GalleryViewer open) { GalleryViewerChecksAccess.Click(open, "GalleryClose"); await w.GalleryCleanupTask.WaitAsync(TimeSpan.FromSeconds(12)); }
            }
            finally { try { await host.ClearTagQueueAsync(); } catch (Exception e) when (e is IOException or InvalidOperationException) { } }
        }
    }

    // ---- Media: grid, focus mode, filter surface and picker, selection bar, More menu ----

    private static async Task MediaAsync(MainWindow w)
    {
        await Enter(w, "media");
        MediaPage page = w.MediaPageForTests ?? throw new InvalidOperationException("The media page does not exist.");
        await UntilAsync(() => page.Tiles.Count > 0, "the media grid has no tiles", 15);
        await PageAsync(w, "page-media", "Media grid over the showcase archive", reenter: () => Enter(w, "media"));
        await StepAsync("media-filter-surface", async () =>
        {
            ToggleButton filters = Need<ToggleButton>(page, "MediaFilters");
            filters.IsChecked = true;
            await UntilAsync(() => page.IsFilterSurfaceOpen, "the filter surface did not open");
            await Settle(w, 500);
            UiCatalog.Record(w, "media-filter-surface", Standard, "Media filter surface with the fields of the view");
            await StepAsync("media-filter-picker", async () =>
            {
                FilterTermPicker picker = page.FilterAdd;
                picker.Open();
                await UntilAsync(() => picker.IsPopoverOpen && TopLevel.GetTopLevel(picker.List) is { Bounds.Width: > 0 }, "the filter picker did not open");
                await Settle(w, 300);
                UiCatalog.RecordSurface(SurfaceOf(picker.List, w), "media-filter-picker", "popover", "+ Filter picker: search field over every field's terms");
                picker.Search.Focus();
                Press(picker.Search, Key.Escape);
                await Settle(w, 150);
            });
            page.CloseFilterSurface(restoreFocus: false);
            await Settle(w, 150);
        });
        await StepAsync("media-selection-bar", async () =>
        {
            ListBox grid = Need<ListBox>(page, "MediaGrid");
            grid.Focus();
            grid.SelectAll();
            await UntilAsync(() => Find<Control>(page, "MediaSelectionRow") is { IsVisible: true }, "the selection bar did not show");
            await Settle(w, 400);
            UiCatalog.Record(w, "media-selection-bar", Standard, "Selection bar after Select all (Tag, Gallery, Export, More, Clear)");
            grid.UnselectAll();
            await Settle(w, 150);
        });
        await StepAsync("media-more-menu", async () =>
        {
            // Simple shows every command directly; the More menu exists where the view holds commands back (Barebones).
            await PresentationChecks.SwitchModeAsync(w, "Barebones");
            await Enter(w, "media");
            await UntilAsync(() => Find<Button>(page, "MediaMore") is { IsVisible: true }, "the Barebones view has no More menu on the Media page");
            Button more = Need<Button>(page, "MediaMore");
            more.Flyout!.ShowAt(more);
            try
            {
                await UntilAsync(() => more.Flyout.IsOpen, "the More menu did not open");
                await UntilAsync(() => ((MenuFlyout)more.Flyout).Items.OfType<Control>().FirstOrDefault(i => i.IsVisible && i is not Separator) is { Bounds.Width: > 0 }, "the More menu has no visible entries");
                await Settle(w, 250);
                Control first = ((MenuFlyout)more.Flyout).Items.OfType<Control>().First(i => i.IsVisible && i is not Separator);
                Control surface = first.GetVisualAncestors().OfType<MenuFlyoutPresenter>().FirstOrDefault() ?? SurfaceOf(first, w);
                UiCatalog.RecordSurface(surface, "media-more-menu", "flyout", "Header More menu (rare actions with shortcuts and reasons)");
            }
            finally { more.Flyout.Hide(); await Settle(w, 120); await PresentationChecks.SwitchModeAsync(w, _baselineMode); await Enter(w, "media"); }
        });
        await StepAsync("media-focus-mode", async () =>
        {
            page.SetFocusMode(true);
            try
            {
                await UntilAsync(() => page.IsFocusMode, "focus mode did not start");
                await Settle(w, 450);
                await PageAsync(w, "media-focus-mode", "Media focus mode (Shift+F): header rows hidden, grid with the focus bar", reenter: () => Task.CompletedTask);
            }
            finally { page.SetFocusMode(false); await Settle(w, 150); }
        });
    }

    // ---- Booru page: Search tab and Watchers tab over in-memory ports ----

    // The shell's own route: the app menu entry when its command is wired, else the window's navigation entry point.
    private static async Task EnterBooruAsync(MainWindow w)
    {
        System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
        bool Can() => (bool)typeof(MainWindow).GetProperty("CanOpenBooru", all)!.GetValue(w)!;
        Console.WriteLine("SMOKE ui catalog booru route: canOpen=" + Can() + " state=" + w.SessionHost.State + " show=" + w.SessionHost.Config.ShowBooruTools);
        await UntilAsync(Can, "the shell does not offer Booru (CanOpenBooru is false)", 10);
        try { await WorkViewBooruChecks.EnterAsync(w); }
        catch (InvalidOperationException)
        {
            bool navigated = (bool)typeof(MainWindow).GetMethod("NavigateWorkspace", all)!.Invoke(w, [MainWindow.WorkspacePage.Booru, true])!;
            Require(navigated, "the shell refused the Booru navigation");
            await Program.WaitUntilAsync(() => w.SelectedPage == MainWindow.WorkspacePage.Booru && w.BooruPageForTests is { IsEffectivelyVisible: true }
                && w.FindControl<ContentControl>("BooruPageHost")!.IsVisible, () => "Booru did not open through the shell navigation.");
        }
        await Settle(w, 500);
    }

    private static async Task BooruAsync(MainWindow w)
    {
        WorkViewBooruChecks booru = _booru ?? throw new InvalidOperationException("The Booru fixture was not installed.");
        MenuItem route = w.FindControl<MenuItem>("BooruMenuItem")!;
        Console.WriteLine("SMOKE ui catalog booru state: show=" + w.SessionHost.Config.ShowBooruTools + " services=" + (w.SessionHost.BooruServices != null) + " mode=" + w.SessionHost.Config.WorkspaceMode + " visible=" + route.IsVisible + " enabled=" + route.IsEffectivelyEnabled + " command=" + (route.Command != null));
        await EnterBooruAsync(w);
        await booru.MaterializeAsync();
        await PageAsync(w, "page-booru", "Booru page, Search tab with synthetic results", reenter: async () => { await EnterBooruAsync(w); await booru.MaterializeAsync(); });
        await StepAsync("page-booru-watchers", async () =>
        {
            await booru.SelectTabAsync(watchers: true);
            try
            {
                await Settle(w, 400);
                UiCatalog.Record(w, "page-booru-watchers", Standard, "Booru page, Watchers tab with three synthetic watchers");
            }
            finally { await booru.SelectTabAsync(watchers: false); }
        });
    }

    // ---- Settings page: first category and each category ----

    private static async Task SettingsAsync(MainWindow w)
    {
        await Enter(w, "settings");
        SettingsEditor editor = w.SettingsEditor;
        await PageAsync(w, "page-settings", "Settings page, first category", reenter: () => Enter(w, "settings"));
        if (SettingsCategoryIds.Length != 9 || SettingsCategoryIds[^1] != "settings.shortcuts")
            throw new InvalidOperationException("FP4 expects nine Settings categories, ending with shortcuts.");
        foreach (string id in SettingsCategoryIds)
        {
            string key = "page-" + id.Replace('.', '-');
            await StepAsync(key, async () =>
            {
                await Dispatcher.UIThread.InvokeAsync(editor.UpdateLayout, DispatcherPriority.Background);
                editor.OpenSectionNavigation();
                if (!editor.CanGoToSection(id)) throw new InvalidOperationException("The editor offers no " + id);
                editor.GoToSection(id);
                await Settle(w, 500);
                UiCatalog.Record(w, key, Standard, "Settings category " + id);
            });
        }
        await StepAsync("settings-section-contents", async () =>
        {
            editor.OpenSectionNavigation();
            await Settle(w, 400);
            UiCatalog.Record(w, "settings-section-contents", Standard, "Section contents menu open");
        });
        // Help popovers of the settings categories (HelpButton): work views, limits, board admission and statistics notes.
        foreach ((string section, string button, string key) in new[]
        {
            ("settings.interface", "WorkspaceModeHelpButton", "help-popover-workviews"),
            ("settings.worker-budgets", "StorageLimitsHelpButton", "help-popover-limits"),
            ("settings.behavior", "BoardAdmissionHelpButton", "help-popover-admission-wizard"),
            ("settings.behavior", "StatisticsSettingsHelpButton", "help-popover-stats-settings"),
        })
            await StepAsync(key, async () =>
            {
                await Dispatcher.UIThread.InvokeAsync(editor.UpdateLayout, DispatcherPriority.Background);
                editor.OpenSectionNavigation();
                if (editor.CanGoToSection(section)) editor.GoToSection(section);
                await Settle(w, 300);
                HelpButton help = Find<HelpButton>(editor, button) ?? throw new InvalidOperationException("No help button " + button + " in " + section);
                help.BringIntoView();
                await Settle(w, 150);
                await FlyoutAsync(w, help.Flyout!, help, () => ((Flyout)help.Flyout!).Content as Control, key, "popover", "Help popover " + button);
            });
    }

    // ---- Tagging page (app menu destination): queue with three synthetic items ----

    private static async Task TaggingPageAsync(MainWindow w)
    {
        DesktopSessionHost host = w.SessionHost;
        ResourceId thread = new("4chan", "g", "123");
        TagQueueItem Item(int n) => new(thread, "/media/123", "asset-" + n, n.ToString("x") + new string('b', 63), 100 + n, "media-" + n,
            "original", "p" + n, null, null, null, false, null, "file-" + n + ".png", SavedMediaKind.Image, TagQueueState.Open,
            new DateTimeOffset(2026, 11, 14, 8, 0, n, TimeSpan.Zero));
        await host.EnqueueTagQueueAsync([Item(1), Item(2), Item(3)]);
        try
        {
            if (!w.ShowTaggingPage()) throw new InvalidOperationException("The tagging page is not available.");
            await UntilAsync(() => w.SelectedPage == MainWindow.WorkspacePage.Tagging && w.TaggingPage is { IsBusy: false } page && page.Rows.Count == 3, "the tagging page did not load the queue");
            await PageAsync(w, "page-tagging", "Tagging page with a queue of three synthetic entries", reenter: () => Task.CompletedTask);
            await StepAsync("tagging-list-menu", async () =>
            {
                TaggingPage page = w.TaggingPage!;
                page.List.SelectedItem = page.Rows[0];
                await Settle(w, 250);
                // TG1: the row menu is composed per selection (ContextMenuBuilder host), there is no standing ListMenu any more.
                page.ShowListMenu();
                await UntilAsync(() => page.OpenListMenu is { IsOpen: true, Bounds.Width: > 0, Bounds.Height: > 0 }, "tagging-list-menu: the menu did not open", 6);
                await Settle(w, 200);
                ContextMenu menu = page.OpenListMenu!;
                UiCatalog.RecordSurface(menu, "tagging-list-menu", "context menu", "Context menu of a queue row (continue here, mark, move, remove)");
                menu.Close();
                await Settle(w, 120);
            });
        }
        finally { try { await host.ClearTagQueueAsync(); } catch (Exception e) when (e is IOException or InvalidOperationException) { } }
    }

    // ---- Work-view tiers: each page in Barebones and Advanced (the default tier is the page image) ----

    private static partial void AddTierScenes(List<Scene> s)
    {
        foreach ((string page, string name) in new[]
        {
            ("downloads", "Downloads"), ("archive", "Archive"), ("reader", "Reader"), ("gallery", "Gallery"), ("media", "Media"), ("booru", "Booru"),
            ("rules", "Rules"), ("sources", "Sources"), ("plugins", "Plugins"), ("statistics", "Statistics"), ("diagnostics", "Diagnostics"), ("settings", "Settings"),
        })
            s.Add(new("tier-" + page, async w =>
            {
                foreach (string tier in new[] { "Barebones", "Advanced" })
                {
                    string key = "tier-" + page + "-" + tier.ToLowerInvariant();
                    await StepAsync(key, async () =>
                    {
                        await PresentationChecks.SwitchModeAsync(w, tier);
                        if (page == "booru")
                        {
                            await EnterBooruAsync(w);
                            await (_booru ?? throw new InvalidOperationException("The Booru fixture was not installed.")).MaterializeAsync();
                        }
                        else await Enter(w, page);
                        await Settle(w, 400);
                        UiCatalog.Record(w, key, Standard, "Work view " + tier);
                    });
                    await ResetAsync(w);
                }
            }, TiersOnly: true));
    }
}
