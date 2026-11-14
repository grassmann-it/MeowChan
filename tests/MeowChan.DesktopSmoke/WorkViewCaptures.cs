using System.Text;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;
using MeowChan.Gallery;
using MeowChan.Interfaces;

namespace MeowChan.DesktopSmoke;

/// <summary>
/// Work views (AP5/WV0): every page in Barebones, Simple and Advanced, in Light and Dark, English and German, wide and at the
/// minimum size, written as smoke-wv-&lt;page&gt;-&lt;view&gt;-&lt;theme&gt;-&lt;lang&gt;-&lt;W&gt;x&lt;H&gt;.png plus an index.html matrix.
/// Offline showcase archive (no providers), passive synthetic download rows; no watches, no network.
/// Before the captures a probe presses a bound shortcut of a toolbar action that the view moved into the toolbar's closed
/// "More" menu and records whether the action still ran (R2 of the work-view plan).
/// Environment: MEOWCHAN_WORK_VIEWS_&lt;PAGE&gt;_ONLY=1 runs one page (registry alternative work-views-&lt;page&gt;),
/// MEOWCHAN_WORK_VIEWS_MATRIX=full|reduced (default full for every page, reduced for one page: Light in all views plus
/// Barebones in Dark), MEOWCHAN_WORK_VIEWS_OUTPUT=&lt;dir&gt; writes captures and index.html there instead of next to the assembly.
/// </summary>
internal static class WorkViewCaptures
{
    private static readonly string[] Views = ["Barebones", "Simple", "Advanced"];
    private static readonly string[] Themes = ["Light", "Dark"];
    private static readonly string[] Languages = ["en", "de"];
    private static readonly (string Name, double Width, double Height)[] Sizes = [("wide", 1380, 850), ("minimum", 640, 480)];
    // A bindable gesture without a default owner; the probe binds it and restores the profile's bindings afterwards.
    private const string ProbeGesture = "Primary+Shift+F9";
    // Before the foundation this was recorded only (a closed-overflow shortcut did not run); now it is required.
    private const bool RequireReducedShortcuts = true;

    private sealed record Cell(string Page, string View, string Theme, string Language, string Size, string File, string Client);

    internal static async Task RunAsync(MainWindow window)
    {
        // Literal reads: the registry tests check that every nested selector is read by the driver.
        (string Page, string? Value)[] selectors =
        [
            ("downloads", Environment.GetEnvironmentVariable("MEOWCHAN_WORK_VIEWS_DOWNLOADS_ONLY")),
            ("archive", Environment.GetEnvironmentVariable("MEOWCHAN_WORK_VIEWS_ARCHIVE_ONLY")),
            ("reader", Environment.GetEnvironmentVariable("MEOWCHAN_WORK_VIEWS_READER_ONLY")),
            ("gallery", Environment.GetEnvironmentVariable("MEOWCHAN_WORK_VIEWS_GALLERY_ONLY")),
            ("media", Environment.GetEnvironmentVariable("MEOWCHAN_WORK_VIEWS_MEDIA_ONLY")),
            ("rules", Environment.GetEnvironmentVariable("MEOWCHAN_WORK_VIEWS_RULES_ONLY")),
            ("sources", Environment.GetEnvironmentVariable("MEOWCHAN_WORK_VIEWS_SOURCES_ONLY")),
            ("plugins", Environment.GetEnvironmentVariable("MEOWCHAN_WORK_VIEWS_PLUGINS_ONLY")),
            ("diagnostics", Environment.GetEnvironmentVariable("MEOWCHAN_WORK_VIEWS_DIAGNOSTICS_ONLY")),
            ("statistics", Environment.GetEnvironmentVariable("MEOWCHAN_WORK_VIEWS_STATISTICS_ONLY")),
            ("settings", Environment.GetEnvironmentVariable("MEOWCHAN_WORK_VIEWS_SETTINGS_ONLY")),
            ("dialogs", Environment.GetEnvironmentVariable("MEOWCHAN_WORK_VIEWS_DIALOGS_ONLY")),
        ];
        if (!selectors.Select(selector => selector.Page).SequenceEqual(SmokeScenarios.WorkViewPages))
            throw new InvalidOperationException("The work-view page selectors disagree with SmokeScenarios.WorkViewPages.");
        string? only = selectors.Where(selector => selector.Value == "1").Select(selector => selector.Page).FirstOrDefault();
        string matrix = Environment.GetEnvironmentVariable("MEOWCHAN_WORK_VIEWS_MATRIX") ?? (only == null ? "full" : "reduced");
        if (matrix is not ("full" or "reduced"))
            throw new InvalidOperationException("MEOWCHAN_WORK_VIEWS_MATRIX must be full or reduced, not " + matrix + ".");
        string output = Environment.GetEnvironmentVariable("MEOWCHAN_WORK_VIEWS_OUTPUT") is { Length: > 0 } explicitOutput
            ? Path.GetFullPath(explicitOutput) : AppContext.BaseDirectory;
        Directory.CreateDirectory(output);
        string[] pages = only == null ? SmokeScenarios.WorkViewPages : [only];
        string modeBefore = window.SessionHost.Config.WorkspaceMode, themeBefore = window.SessionHost.Config.Theme;
        string languageBefore = window.SessionHost.Config.Language;
        List<Cell> cells = new();
        SyntheticShellRows? rows = null;
        try
        {
            rows = await SyntheticShellRows.CreateAsync(window, DownloadEntries());
            CheckRegistrations(window);
            await ShortcutProbeAsync(window);
            await ViewCycleProbeAsync(window);
            foreach (string language in Languages)
            {
                await FooterNavigationChecks.PersistLanguageAsync(window, language);
                foreach (string page in pages)
                {
                    foreach (string view in Views)
                        foreach (string theme in Themes)
                        {
                            if (matrix == "reduced" && theme == "Dark" && view != "Barebones") continue;
                            await PresentationChecks.SwitchModeAsync(window, view);
                            await SetThemeAsync(window, theme);
                            await EnterAsync(window, page);
                            foreach ((string sizeName, double width, double height) in Sizes)
                            {
                                double w = Math.Max(width, Math.Ceiling(window.MinWidth)), h = Math.Max(height, Math.Ceiling(window.MinHeight));
                                await ResizeAsync(window, w, h);
                                await EnterAsync(window, page); // a resize may relayout the archive into its narrow reader layout
                                await SettleAsync(window, page);
                                RequireState(window, page, view, theme, language);
                                CheckPageRegistrations(window, page);
                                CheckPresence(window, page, view);
                                if (page == "downloads") CheckShellAndDownloads(window, view);
                                CheckSectionsAndMore(window, page, view);
                                string file = $"smoke-wv-{page}-{view.ToLowerInvariant()}-{theme.ToLowerInvariant()}-{language}-{w:0}x{h:0}.png";
                                Program.CaptureWindow(window, file, output);
                                if (page == "archive")
                                {
                                    WorkViewArchiveChecks.RequirePlacement(window, view);
                                    await WorkViewArchiveChecks.CaptureFilterSurfaceAsync(window, view, file.Replace("smoke-wv-archive-", "smoke-wv-archive-filters-"), output);
                                }
                                cells.Add(new(page, view, theme, language, sizeName, file, $"{window.ClientSize.Width:0}x{window.ClientSize.Height:0}"));
                            }
                            if (page == "reader" && view == "Advanced" && theme == "Light") await ReaderExportWidthCheckAsync(window);
                        }
                    await LeaveAsync(window);
                    Console.WriteLine($"SMOKE work-views page={page}; language={language}; matrix={matrix}; captures so far={cells.Count}.");
                }
            }
        }
        finally
        {
            await LeaveAsync(window);
            WriteIndex(output, cells, only, matrix);
            await SetThemeAsync(window, themeBefore);
            await FooterNavigationChecks.PersistLanguageAsync(window, languageBefore);
            await PresentationChecks.SwitchModeAsync(window, modeBefore);
            if (rows != null) await rows.DisposeAsync();
        }
        int expected = pages.Length * Languages.Length * Sizes.Length * (matrix == "full" ? Views.Length * Themes.Length : Views.Length + 1);
        if (cells.Count != expected)
            throw new InvalidOperationException($"The work-view matrix wrote {cells.Count} captures instead of {expected}.");
        Console.WriteLine($"SMOKE work-views: {cells.Count} captures of {string.Join(", ", pages)} ({matrix}); index in {output}.");
    }

    // ---- Pages ----

    private static async Task EnterAsync(MainWindow window, string page)
    {
        ArchivePage archive = window.ArchivePage;
        ContentControl reader = window.FindControl<ContentControl>("ReaderHost")!;
        ContentControl gallery = window.FindControl<ContentControl>("GalleryHost")!;
        bool exportOpen = ExportDialog(archive)?.IsVisible == true;
        switch (page)
        {
            case "reader" when reader.IsVisible && !gallery.IsVisible && !exportOpen:
            case "gallery" when gallery.IsVisible:
            case "dialogs" when exportOpen && !reader.IsVisible && !gallery.IsVisible:
                return;
        }
        await LeaveAsync(window);
        switch (page)
        {
            case "downloads":
                Click(window.FindControl<Button>("DownloadsNavigationButton")!);
                window.FindControl<TabControl>("DownloadsTabs")!.SelectedIndex = 0;
                await Program.WaitUntilAsync(() => window.SelectedPage == MainWindow.WorkspacePage.Downloads);
                return;
            case "archive":
                await OpenArchiveAsync(window);
                return;
            case "reader":
                await OpenArchiveAsync(window);
                await archive.LoadThreadAsync(ShowcaseData.Hero);
                await archive.MediaLoadTask;
                archive.FindControl<TabControl>("ArchiveContentTabs")!.SelectedIndex = 0;
                Click(archive.FindControl<Button>("ArchiveReadPosts")!);
                await Program.WaitUntilAsync(() => reader.IsVisible && !archive.IsBusy
                    && archive.CurrentThread?.Summary.Id == ShowcaseData.Hero);
                await archive.MediaLoadTask;
                return;
            case "gallery":
                await OpenArchiveAsync(window);
                await archive.LoadThreadAsync(ShowcaseData.Hero);
                await archive.MediaLoadTask;
                await window.OpenGalleryAsync(ShowcaseData.Hero, origin: archive.FindControl<Button>("ArchiveResultsGallery"));
                await Program.WaitUntilAsync(() => gallery.IsVisible && gallery.GetVisualDescendants().OfType<Image>()
                    .Any(image => image.Name == "GalleryImage" && image.Source is Bitmap));
                return;
            case "dialogs":
                // The in-page export card: the reduced dialog of the plan (§1.14, format and scope direct, extras collapsed).
                await OpenArchiveAsync(window);
                await archive.LoadThreadAsync(ShowcaseData.Hero);
                await archive.OpenExportForThreadAsync(ShowcaseData.Hero, archive.FindControl<Button>("ExportArchiveButton"));
                await Program.WaitUntilAsync(() => ExportDialog(archive)?.IsVisible == true && !archive.IsBusy);
                return;
        }
        (string item, MainWindow.WorkspacePage target) = page switch
        {
            "media" => ("MediaMenuItem", MainWindow.WorkspacePage.Media),
            "rules" => ("RulesMenuItem", MainWindow.WorkspacePage.Rules),
            "sources" => ("SourcesMenuItem", MainWindow.WorkspacePage.Sources),
            "plugins" => ("PluginsMenuItem", MainWindow.WorkspacePage.Plugins),
            "diagnostics" => ("DiagnosticsMenuItem", MainWindow.WorkspacePage.Diagnostics),
            "statistics" => ("StatisticsMenuItem", MainWindow.WorkspacePage.Statistics),
            "settings" => ("SettingsMenuItem", MainWindow.WorkspacePage.Settings),
            _ => throw new ArgumentOutOfRangeException(nameof(page), page, "Not a work-view page.")
        };
        window.FindControl<MenuItem>(item)!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Program.WaitUntilAsync(() => window.SelectedPage == target);
    }

    private static async Task OpenArchiveAsync(MainWindow window)
    {
        ArchivePage archive = window.ArchivePage;
        if (window.SelectedPage != MainWindow.WorkspacePage.Archive)
            window.FindControl<MenuItem>("ArchiveMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Program.WaitUntilAsync(() => window.SelectedPage == MainWindow.WorkspacePage.Archive && !archive.IsBusy
            && archive.FindControl<ListBox>("ArchiveThreads")!.ItemCount > 0);
    }

    // Closes every overlay this smoke opens: export card, gallery, large reader.
    private static async Task LeaveAsync(MainWindow window)
    {
        ArchivePage archive = window.ArchivePage;
        if (ExportDialog(archive) is { IsVisible: true } export)
        {
            Click(export.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ArchiveExportClose"));
            await Program.WaitUntilAsync(() => !export.IsVisible);
        }
        ContentControl gallery = window.FindControl<ContentControl>("GalleryHost")!;
        if (gallery.IsVisible)
        {
            Click(gallery.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "GalleryClose"));
            await Program.WaitUntilAsync(() => !gallery.IsVisible);
        }
        ContentControl reader = window.FindControl<ContentControl>("ReaderHost")!;
        if (reader.IsVisible)
        {
            Click(archive.FindControl<Button>("ArchiveCloseReader")!);
            await Program.WaitUntilAsync(() => !reader.IsVisible);
        }
    }

    private static Border? ExportDialog(ArchivePage archive) =>
        archive.GetVisualDescendants().OfType<Border>().FirstOrDefault(border => border.Name == "ArchiveExportDialog");

    private static async Task SettleAsync(MainWindow window, string page)
    {
        for (int pass = 0; pass < 3; pass++)
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
        // Thumbnails and lazily built pages finish on later frames; the same pause keeps before/after captures comparable.
        await Task.Delay(page is "media" or "gallery" or "reader" or "archive" or "dialogs" ? 450 : 200);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
    }

    private static void RequireState(MainWindow window, string page, string view, string theme, string language)
    {
        string scene = $"{page}/{view}/{theme}/{language}";
        if (window.SessionHost.Config.WorkspaceMode != view || Texts.Language != language
            || Application.Current?.RequestedThemeVariant != (theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark))
            throw new InvalidOperationException(scene + ": the window is not in the requested view, theme and language.");
        bool overlay = window.FindControl<ContentControl>("ReaderHost")!.IsVisible || window.FindControl<ContentControl>("GalleryHost")!.IsVisible;
        if (overlay != (page is "reader" or "gallery"))
            throw new InvalidOperationException(scene + ": the reader/gallery overlay state does not match the page.");
    }

    // ---- Shortcut probe (R2) ----

    private static async Task ShortcutProbeAsync(MainWindow window)
    {
        Config current = window.SessionHost.Config;
        ShortcutBindingsSection originalBindings = current.ShortcutBindings.Clone();
        string modeBefore = current.WorkspaceMode;
        Button copy = window.FindControl<Button>("CopyAllUrlsButton")!;
        ActionToolBar toolbar = window.FindControl<ActionToolBar>("WorkspaceListActions")!;
        ICommand? productCommand = copy.Command;
        int executed = 0;
        List<string> findings = new();
        try
        {
            Click(window.FindControl<Button>("DownloadsNavigationButton")!);
            await Program.WaitUntilAsync(() => window.SelectedPage == MainWindow.WorkspacePage.Downloads);
            Config candidate = current.Clone();
            candidate.ShortcutBindings = candidate.ShortcutBindings.Set("watch.copy-all-links", [ShortcutGesture.Parse(ProbeGesture)]);
            long revision = ShortcutService.Revision;
            await window.SessionHost.ApplySettingsAsync(candidate);
            await Program.WaitUntilAsync(() => ShortcutService.Revision > revision);
            KeyGesture gesture = CommandBindings.Gesture("watch.copy-all-links")
                ?? throw new InvalidOperationException("The probe gesture was not published.");
            // Observe the existing executor without copying anything to the clipboard.
            copy.Command = new DelegateCommand(() => executed++);
            foreach ((string view, string expected) in new[] { ("Advanced", "direct"), ("Barebones", "overflow") })
            {
                await PresentationChecks.SwitchModeAsync(window, view);
                for (int pass = 0; pass < 3; pass++)
                    await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
                bool inOverflow = toolbar.OverflowHost?.Children.Contains(copy) == true;
                bool popupOpen = toolbar.More?.IsChecked == true;
                string placement = inOverflow ? (popupOpen ? "overflow-open" : "overflow-closed") : "direct";
                if (placement != (expected == "direct" ? "direct" : "overflow-closed"))
                    throw new InvalidOperationException($"Shortcut probe {view}: Copy URLs is {placement}, expected {expected}.");
                Control source = window.FindControl<DataGrid>("ThreadsDataGrid")!;
                int before = executed;
                KeyEventArgs press = new() { RoutedEvent = InputElement.KeyDownEvent, Key = gesture.Key, KeyModifiers = gesture.KeyModifiers };
                source.RaiseEvent(press);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                bool ran = executed == before + 1;
                findings.Add($"{view}:{placement}:handled={press.Handled}:ran={ran}");
                Console.WriteLine($"SMOKE work-views shortcut probe: view={view}; Copy URLs placement={placement}; "
                    + $"bound {ProbeGesture} handled={press.Handled}; action ran={ran}; effectively visible={copy.IsEffectivelyVisible}.");
                if (!ran && (expected == "direct" || RequireReducedShortcuts))
                    throw new InvalidOperationException($"Shortcut probe {view}: the bound shortcut did not run the {placement} action.");
            }
            // "K": omitted from bar and popup (a context or app menu carries it), and hidden by WorkViewGate.
            foreach (string reduction in new[] { "omitted", "gate-hidden" })
            {
                // Synchronous: a coalesced page refresh re-applies the view and must not run between placing and pressing.
                if (reduction == "omitted") ActionToolBar.SetOmitted(copy, true);
                else WorkViewGate.Apply(copy, WorkView.Barebones);
                window.UpdateLayout();
                bool placed = reduction == "omitted"
                    ? toolbar.OmittedHost.Children.Contains(copy) && toolbar.OverflowHost?.Children.Contains(copy) != true && !toolbar.DirectHost!.Children.Contains(copy)
                    : !copy.IsVisible && WorkViewGate.HiddenByView(copy);
                int before = executed;
                KeyEventArgs press = new() { RoutedEvent = InputElement.KeyDownEvent, Key = gesture.Key, KeyModifiers = gesture.KeyModifiers };
                window.FindControl<DataGrid>("ThreadsDataGrid")!.RaiseEvent(press);
                bool ran = executed == before + 1;
                findings.Add($"Barebones:{reduction}:placed={placed}:handled={press.Handled}:ran={ran}");
                Console.WriteLine($"SMOKE work-views shortcut probe: Copy URLs {reduction}; placed={placed}; handled={press.Handled}; action ran={ran}.");
                if (reduction == "omitted") ActionToolBar.SetOmitted(copy, false);
                else { copy.IsVisible = true; WorkViewElement.SetStateVisible(copy, true); }
                if (!placed || !ran)
                    throw new InvalidOperationException($"Shortcut probe: the {reduction} Copy URLs action was not placed or its shortcut did not run it.");
            }
        }
        finally
        {
            copy.Command = productCommand;
            Config restore = window.SessionHost.Config.Clone();
            restore.ShortcutBindings = originalBindings;
            long revision = ShortcutService.Revision;
            await window.SessionHost.ApplySettingsAsync(restore);
            await Program.WaitUntilAsync(() => ShortcutService.Revision > revision);
            await PresentationChecks.SwitchModeAsync(window, modeBefore);
        }
        Console.WriteLine("SMOKE work-views shortcut finding: " + string.Join("; ", findings) + ".");
    }

    // ---- View switch at run time (R8): the command, the radio group and the footer agree, and the session is kept ----

    private static async Task ViewCycleProbeAsync(MainWindow window)
    {
        string modeBefore = window.SessionHost.Config.WorkspaceMode;
        try
        {
            Click(window.FindControl<Button>("DownloadsNavigationButton")!);
            await Program.WaitUntilAsync(() => window.SelectedPage == MainWindow.WorkspacePage.Downloads);
            await PresentationChecks.SwitchModeAsync(window, "Barebones");
            int rows = window.FindControl<DataGrid>("ThreadsDataGrid")!.ItemsSource is System.Collections.IEnumerable list
                ? list.Cast<object>().Count() : -1;
            List<string> visited = new() { window.SessionHost.Config.WorkspaceMode };
            for (int step = 0; step < 3; step++)
            {
                await window.CycleWorkViewAsync();
                await Program.WaitUntilAsync(() => window.SessionHost.Config.WorkspaceMode != visited[^1]);
                for (int pass = 0; pass < 2; pass++)
                    await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
                visited.Add(window.SessionHost.Config.WorkspaceMode);
                CheckShellAndDownloads(window, visited[^1]);
            }
            if (!visited.SequenceEqual(["Barebones", "Simple", "Advanced", "Barebones"]))
                throw new InvalidOperationException("workspace.view-cycle visited " + string.Join(" > ", visited) + ".");
            int rowsAfter = window.FindControl<DataGrid>("ThreadsDataGrid")!.ItemsSource is System.Collections.IEnumerable after
                ? after.Cast<object>().Count() : -1;
            if (window.SelectedPage != MainWindow.WorkspacePage.Downloads || rows != rowsAfter)
                throw new InvalidOperationException("Switching the view changed the page or the Downloads rows.");
            Console.WriteLine("SMOKE work-views view cycle: " + string.Join(" > ", visited) + "; page and rows kept.");
        }
        finally { await PresentationChecks.SwitchModeAsync(window, modeBefore); }
    }

    // Header and app menu of the cell (plan §1.1, R3/R8) plus what the Downloads page promises (plan §1.3).
    private static void CheckShellAndDownloads(MainWindow window, string view)
    {
        WorkView workView = WorkViewSetting.Parse(view);
        string scene = "downloads/" + view;
        foreach (string name in new[] { "TransferSpeed", "PauseButton", "FullscreenButton", "ThemeToggleButton" })
            if (!window.FindControl<Control>(name)!.IsEffectivelyVisible)
                throw new InvalidOperationException($"{scene}: the neutral header element {name} is not visible.");
        MenuItem appMenu = window.FindControl<MenuItem>("WorkspaceMenuItem")!;
        string[] required = ["DownloadsMenuItem", "ArchiveMenuItem", "MediaMenuItem", "RulesMenuItem", "SourcesMenuItem", "PluginsMenuItem",
            "DiagnosticsMenuItem", "StatisticsMenuItem", "ManualImportMenuItem", "PauseMenuItem", "ThemeMenuItem", "FullscreenMenuItem",
            "WorkViewMenuItem", "SettingsMenuItem", "ShortcutHelpMenuItem", "AboutMenuItem", "ExitMenuItem"];
        string[] missing = required.Where(name => !appMenu.Items.OfType<MenuItem>().Any(item => item.Name == name)).ToArray();
        if (missing.Length > 0) throw new InvalidOperationException($"{scene}: the app menu lost {string.Join(", ", missing)}.");
        MenuItem group = window.FindControl<MenuItem>("WorkViewMenuItem")!;
        MenuItem[] radios = group.Items.OfType<MenuItem>().Where(item => item.ToggleType == MenuItemToggleType.Radio).ToArray();
        string checkedViews = string.Join(",", radios.Where(item => item.IsChecked).Select(item => item.Name));
        if (radios.Length != 3 || checkedViews != view + "WorkViewMenuItem" || window.FindControl<MenuItem>("WorkViewCycleMenuItem") == null)
            throw new InvalidOperationException($"{scene}: the view radio group shows {checkedViews} of {radios.Length} entries.");
        if (window.FindControl<ComboBox>("WorkspaceModeComboBox")!.SelectedItem is not ComboBoxItem { Tag: string footer } || footer != view)
            throw new InvalidOperationException($"{scene}: the footer selector disagrees with the work view.");
        // Columns… offers the technical work columns from Simple on, off by default.
        bool offered = WorkViewPolicy.ColumnOffered(WorkViewPolicy.DownloadColumnId("ThreadsDataGrid", "MissingMedia"), workView);
        DataGridColumn missingMedia = window.FindControl<DataGrid>("ThreadsDataGrid")!.Columns.Single(column => column.SortMemberPath == "MissingMedia");
        if (offered != (workView != WorkView.Barebones) || (!offered && missingMedia.IsVisible))
            throw new InvalidOperationException($"{scene}: the technical work columns do not follow the policy.");
        Console.WriteLine($"SMOKE work-views {scene}: app menu complete, view group checked {checkedViews}, header neutral.");
    }

    // ---- Policy self-checks (plan §3.3) ----

    // No orphaned rows: every control-backed row of the policy is registered by a control of this window.
    private static void CheckRegistrations(MainWindow window)
    {
        _ = window.RulesPage; // builds every deferred page (Rules, Sources, Plugins), so their ids are registered before the check
        Control[] mine = WorkViewElement.All().Where(control => ReferenceEquals(TopLevel.GetTopLevel(control), window)
            || control.FindLogicalAncestorOfType<MainWindow>() == window).ToArray();
        HashSet<string> registered = mine.Select(control => WorkViewElement.GetId(control)!).ToHashSet(StringComparer.Ordinal);
        string[] orphaned = WorkViewPolicy.Entries
            .Where(entry => entry.Kind is WorkViewKind.Action or WorkViewKind.Element or WorkViewKind.Section
                && !LazyPages.Contains(entry.Page) // built on first visit: CheckPageRegistrations runs after entering them
                && !entry.Id.EndsWith(".other", StringComparison.Ordinal) && !registered.Contains(entry.Id))
            .Select(entry => entry.Id).ToArray();
        if (orphaned.Length > 0)
            throw new InvalidOperationException("Work-view rows without a registered control: " + string.Join(", ", orphaned));
        Console.WriteLine($"SMOKE work-views registrations: {registered.Count} ids registered, no orphaned policy row.");
    }

    // The media page has no ActionToolBar: its header and selection bar keep what the view reduces under their own "More"
    // (MediaPage.WorkView.cs). Every id of the page is on the surface exactly as the policy says (no filter is active in this
    // fixture, so R6 raises nothing), and More holds exactly the policy's overflow rows in policy order (WV5).
    private static void CheckMediaPage(MainWindow window, WorkView workView, string view)
    {
        MediaPage page = window.MediaPageForTests ?? throw new InvalidOperationException("media/" + view + ": the media page does not exist.");
        if (page.CurrentWorkView != workView)
            throw new InvalidOperationException($"media/{view}: the page applied {page.CurrentWorkView}.");
        // Visibility of these follows page state or is derived from the lists below.
        string[] derived = ["media.toolbar.more", "media.selection.more", "media.toolbar.shuffle", "media.status.untagged", "media.filter.more"];
        Control[] controls = WorkViewElement.For(WorkViewPage.Media).Where(control => control.FindLogicalAncestorOfType<MediaPage>() == page).ToArray();
        foreach (Control control in controls)
        {
            string id = WorkViewElement.GetId(control)!;
            if (derived.Contains(id)) continue;
            bool expected = WorkViewPolicy.OnSurface(WorkViewPolicy.Resolve(id, workView));
            if (control.IsVisible != expected)
                throw new InvalidOperationException($"media/{view}: {control.Name ?? id} is {(control.IsVisible ? "shown" : "hidden")} but {id} says {WorkViewPolicy.Resolve(id, workView)}.");
        }
        string[] header = ["media.toolbar.refresh", "media.toolbar.shuffle", "media.toolbar.missing", "media.toolbar.export-all", "media.toolbar.manage-ratings"];
        string[] expectedMore = header.Where(id => WorkViewPolicy.Resolve(id, workView) == Presence.Overflow
                && (id != "media.toolbar.shuffle" || page.Sort == MediaLibrarySort.Random))
            .OrderBy(id => WorkViewPolicy.Get(id).OverflowOrder).ToArray();
        if (!page.HeaderMoreIds.SequenceEqual(expectedMore))
            throw new InvalidOperationException($"media/{view}: More holds [{string.Join(", ", page.HeaderMoreIds)}], the policy says [{string.Join(", ", expectedMore)}].");
        Button more = page.GetLogicalDescendants().OfType<Button>().First(button => button.Name == "MediaMore");
        if (more.IsVisible != (expectedMore.Length > 0))
            throw new InvalidOperationException($"media/{view}: the More button is {(more.IsVisible ? "shown" : "hidden")} for {expectedMore.Length} entries.");
        Console.WriteLine($"SMOKE work-views media: view={view}; More=[{string.Join(", ", page.HeaderMoreIds)}].");
    }

    // Pages the window builds lazily (EnsureDeferredWorkspacePages); their rows are checked once the page was entered.
    private static readonly HashSet<WorkViewPage> LazyPages = [WorkViewPage.Diagnostics, WorkViewPage.Statistics];

    private static void CheckPageRegistrations(MainWindow window, string page)
    {
        if (!Enum.TryParse(page, ignoreCase: true, out WorkViewPage workViewPage) || !LazyPages.Contains(workViewPage)) return;
        HashSet<string> registered = WorkViewElement.For(workViewPage)
            .Where(control => ReferenceEquals(TopLevel.GetTopLevel(control), window) || control.FindLogicalAncestorOfType<MainWindow>() == window)
            .Select(control => WorkViewElement.GetId(control)!).ToHashSet(StringComparer.Ordinal);
        string[] orphaned = WorkViewPolicy.Entries
            .Where(entry => entry.Page == workViewPage && entry.Kind is WorkViewKind.Action or WorkViewKind.Element or WorkViewKind.Section
                && !registered.Contains(entry.Id)).Select(entry => entry.Id).ToArray();
        if (orphaned.Length > 0)
            throw new InvalidOperationException($"{page}: work-view rows without a registered control: " + string.Join(", ", orphaned));
        Console.WriteLine($"SMOKE work-views registrations: {page}: {registered.Count} ids registered, no orphaned policy row.");
    }

    // Sections and "More" of the diagnostics and statistics pages are placed as the policy says for this view (WV7).
    private static void CheckSectionsAndMore(MainWindow window, string page, string view)
    {
        WorkView workView = WorkViewSetting.Parse(view);
        if (page == "diagnostics")
        {
            Control root = window.FindControl<ContentControl>("DiagnosticsPageHost")!;
            Expander Section(string name) => root.GetVisualDescendants().OfType<Expander>().Single(control => control.Name == name);
            // Nothing is filtered in this matrix, so the group takes its view default; session messages start closed in every view.
            bool filtersOpen = WorkViewPolicy.Resolve("diagnostics.filters", workView) != Presence.Collapsed;
            if (Section("DiagnosticFiltersSection").IsExpanded != filtersOpen)
                throw new InvalidOperationException($"diagnostics/{view}: the filter group is {(filtersOpen ? "closed" : "open")}, the policy says the opposite.");
            if (Section("SessionFeedbackSection").IsExpanded)
                throw new InvalidOperationException($"diagnostics/{view}: the session messages are open on entering the view.");
            // "Retry filtered" and "Copy details" sit under More exactly where the policy says; the More button counts them.
            Button more = root.GetVisualDescendants().OfType<Button>().Single(control => control.Name == "DiagnosticMoreButton");
            int expected = 0;
            foreach ((string name, string id) in new[] { ("RetryDiagnosticFilteredButton", "diagnostics.toolbar.retry-filtered"),
                ("CopyDiagnosticButton", "diagnostics.toolbar.copy") })
            {
                Button button = root.GetVisualDescendants().OfType<Button>().Single(control => control.Name == name);
                bool underMore = WorkViewPolicy.Resolve(id, workView) == Presence.Overflow;
                if (underMore) expected++;
                if (button.IsVisible == underMore)
                    throw new InvalidOperationException($"diagnostics/{view}: {name} is {(button.IsVisible ? "on the bar" : "hidden")}, the policy puts it {(underMore ? "under More" : "on the bar")}.");
            }
            if (more.IsVisible != (expected > 0))
                throw new InvalidOperationException($"diagnostics/{view}: the More button is {(more.IsVisible ? "shown" : "missing")} for {expected} moved actions.");
            string moreName = Avalonia.Automation.AutomationProperties.GetName(more) ?? string.Empty;
            if (expected > 0 && !moreName.Contains(expected.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal))
                throw new InvalidOperationException($"diagnostics/{view}: the More button does not announce {expected} actions (\"{moreName}\").");
        }
        else if (page == "statistics")
        {
            StatisticsPage statistics = window.FindControl<ContentControl>("StatisticsPageHost")!.Content as StatisticsPage
                ?? throw new InvalidOperationException("The statistics page is not built.");
            bool underMore = WorkViewPolicy.Resolve("statistics.reset", workView) == Presence.Overflow;
            if (!statistics.ResetIsReachable)
                throw new InvalidOperationException($"statistics/{view}: Reset cannot be reached (on the bar or under More).");
            if (underMore == statistics.ResetButton.IsEffectivelyVisible)
                throw new InvalidOperationException($"statistics/{view}: Reset is {(underMore ? "on the bar" : "under More")}, the policy says the opposite.");
        }
    }

    // The toolbar items of the visible surfaces are placed as the policy says for this view.
    private static void CheckPresence(MainWindow window, string page, string view)
    {
        WorkView workView = WorkViewSetting.Parse(view);
        if (page == "media") CheckMediaPage(window, workView, view);
        if (page == "reader")
        {
            if (window.ArchivePage.ReaderWorkView != workView)
                throw new InvalidOperationException($"reader/{view}: the reader bars are laid out for {window.ArchivePage.ReaderWorkView}.");
            ActionToolBar readerBar = window.ArchivePage.FindControl<ActionToolBar>("ReaderToolbar")!;
            ReaderWorkViewChecks.RequireOverflowFollowsPolicy(window.ArchivePage, readerBar, () => $"reader/{view} at {window.ClientSize}");
        }
        CheckPagePresence(window, page, view, workView);
        foreach (ActionToolBar toolbar in window.GetVisualDescendants().OfType<ActionToolBar>().Where(toolbar => toolbar.IsEffectivelyVisible))
        {
            string? fallback = toolbar.Name == "ArchiveResultActions" ? "archive.results.other" : null;
            foreach (Control item in toolbar.Items.OfType<Control>())
            {
                string? id = WorkViewElement.GetId(item) ?? fallback;
                if (id == null) continue;
                Presence presence = WorkViewPolicy.Resolve(id, workView);
                if (Ursa.Controls.ToolBar.GetOverflowMode(item) != WorkViewPresenter.ToolBarMode(presence)
                    || ActionToolBar.GetOmitted(item) != (presence == Presence.Hidden))
                    throw new InvalidOperationException($"{page}/{view}: {item.Name ?? id} is not placed as {id} says ({presence}).");
                if (presence == Presence.Overflow && item.IsVisible && toolbar.DirectHost?.Children.Contains(item) == true)
                    throw new InvalidOperationException($"{page}/{view}: {item.Name ?? id} belongs under More but sits on the bar.");
            }
        }
    }

    // WV3, owner 14.11.: HTML export is an important reader action; at 1520 and 1280 it stays on the Advanced bar while
    // the rarely used actions (full screen, View, history, post capture) take the overflow. No capture: placement only.
    private static async Task ReaderExportWidthCheckAsync(MainWindow window)
    {
        ArchivePage archive = window.ArchivePage;
        ActionToolBar toolbar = archive.FindControl<ActionToolBar>("ReaderToolbar")!;
        Button export = archive.FindControl<Button>("ExportArchiveButton")!;
        foreach ((double width, double height) in new[] { (1520d, 850d), (1280d, 800d) })
        {
            await ResizeAsync(window, width, height);
            await EnterAsync(window, "reader");
            await SettleAsync(window, "reader");
            string[] under = toolbar.OverflowHost?.Children.OfType<Control>().Where(item => item.IsVisible).Select(item => item.Name ?? "?").ToArray() ?? [];
            string Describe() => $"client {window.ClientSize}, toolbar {toolbar.Bounds.Width:0}; under More: {string.Join(", ", under)}";
            Console.WriteLine($"SMOKE work-views reader export width: {width:0}x{height:0}; {Describe()}.");
            if (!export.IsVisible || toolbar.OverflowHost?.Children.Contains(export) == true || !export.IsEffectivelyVisible)
                throw new InvalidOperationException($"reader/Advanced/{width:0}: Export as HTML… moved under More: " + Describe());
            ReaderWorkViewChecks.RequireOverflowFollowsPolicy(archive, toolbar, Describe);
        }
    }

    // Rules, Sources and Plugins (WV6): Save/Discard and Refresh stay on the surface in every view, and the page's own More (⋯)
    // button is shown exactly when the view moved something under it (the plugin header actions here).
    private static void CheckPagePresence(MainWindow window, string page, string viewName, WorkView view)
    {
        static Control Find(MainWindow window, WorkViewPage workViewPage, string id) =>
            WorkViewElement.For(workViewPage).First(control => WorkViewElement.GetId(control) == id
                && control.FindLogicalAncestorOfType<MainWindow>() == window);
        switch (page)
        {
            case "rules":
                foreach (string id in new[] { "rules.footer.save", "rules.footer.discard" })
                    if (!Find(window, WorkViewPage.Rules, id).IsEffectivelyVisible)
                        throw new InvalidOperationException($"rules/{viewName}: {id} is a required element but is not shown.");
                break;
            case "plugins":
                if (!Find(window, WorkViewPage.Plugins, "plugins.header.refresh").IsEffectivelyVisible)
                    throw new InvalidOperationException($"plugins/{viewName}: Refresh is not shown.");
                Control more = Find(window, WorkViewPage.Plugins, "plugins.header.add").FindLogicalAncestorOfType<PluginsPage>()!
                    .FindControl<Button>("PluginsMoreButton")!;
                bool moved = new[] { "plugins.header.add", "plugins.header.folder" }
                    .Any(id => WorkViewPolicy.Resolve(id, view) == Presence.Overflow
                        && WorkViewElement.GetStateVisible(Find(window, WorkViewPage.Plugins, id)));
                if (more.IsVisible != moved)
                    throw new InvalidOperationException($"plugins/{viewName}: the More button is {(more.IsVisible ? "shown" : "hidden")} but the view {(moved ? "moved" : "moved no")} header action(s) under it.");
                break;
        }
    }

    // ---- Fixture, helpers, index ----

    private static DesktopThreadEntry[] DownloadEntries()
    {
        (string Title, int Saved, int Total, ThreadActivity Activity, int Queued, int Active)[] scenes =
        [
            ("Landscape sketchbook — mountains & reflections", 8, 8, ThreadActivity.Finished, 0, 0),
            ("Coastal light and quiet harbours", 38, 64, ThreadActivity.Downloading, 24, 2),
            ("Forest colours through the seasons", 24, 24, ThreadActivity.Finished, 0, 0),
            ("Architecture — lines, light & shadow", 17, 18, ThreadActivity.Pending, 0, 0),
            ("Night skies and distant horizons", 56, 56, ThreadActivity.Finished, 0, 0),
            ("Weekend drawing studies", 0, 12, ThreadActivity.Pending, 12, 0)
        ];
        return scenes.Select((scene, index) =>
        {
            DesktopThreadEntry entry = SnapshotFixtures.Thread(970000001 + index, (960000001 + index).ToString(System.Globalization.CultureInfo.InvariantCulture),
                scene.Title, scene.Saved, scene.Total, scene.Queued, scene.Active, "wg");
            return entry with { Snapshot = entry.Snapshot with { Activity = scene.Activity,
                SourceState = scene.Activity == ThreadActivity.Finished ? ThreadSourceState.Archived : ThreadSourceState.Active } };
        }).ToArray();
    }

    private static async Task SetThemeAsync(MainWindow window, string theme)
    {
        if (window.SessionHost.Config.Theme != theme)
            Click(window.FindControl<Button>("ThemeToggleButton")!);
        ThemeVariant variant = theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
        await Program.WaitUntilAsync(() => window.SessionHost.Config.Theme == theme && Application.Current?.RequestedThemeVariant == variant);
    }

    private static async Task ResizeAsync(MainWindow window, double width, double height)
    {
        if (window.WindowState != WindowState.Normal) window.WindowState = WindowState.Normal;
        window.Width = width;
        window.Height = height;
        await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(window, width, height),
            () => $"client={window.ClientSize}; requested={width}x{height}; minimum={window.MinWidth}x{window.MinHeight}");
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void WriteIndex(string output, IReadOnlyList<Cell> cells, string? only, string matrix)
    {
        if (cells.Count == 0) return;
        string Escape(string text) => System.Net.WebUtility.HtmlEncode(text);
        string[] columns = cells.Select(cell => cell.Theme + "|" + cell.Language + "|" + cell.Size).Distinct().ToArray();
        StringBuilder html = new();
        html.AppendLine("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>MeowChan work views</title><style>")
            .AppendLine("body{font:13px system-ui,sans-serif;margin:16px;background:#f4f4f4;color:#222}table{border-collapse:collapse}")
            .AppendLine("th,td{border:1px solid #ccc;padding:4px;vertical-align:top;background:#fff}th{position:sticky;top:0;background:#eee}")
            .AppendLine("img{width:260px;height:auto;display:block}td.page{font-weight:600;white-space:nowrap}</style></head><body>")
            .AppendLine($"<h1>Work views · {Escape(only ?? "all pages")} · {Escape(matrix)} matrix</h1>")
            .AppendLine($"<p>{cells.Count} captures, {DateTimeOffset.Now:yyyy-MM-dd HH:mm}; click an image for full size.</p><table><tr><th>Page</th><th>View</th>");
        foreach (string column in columns) html.Append("<th>").Append(Escape(column.Replace("|", " · "))).Append("</th>");
        html.AppendLine("</tr>");
        foreach (IGrouping<(string Page, string View), Cell> row in cells.GroupBy(cell => (cell.Page, cell.View)))
        {
            html.Append("<tr><td class=\"page\">").Append(Escape(row.Key.Page)).Append("</td><td>").Append(Escape(row.Key.View)).Append("</td>");
            foreach (string column in columns)
            {
                Cell? cell = row.FirstOrDefault(candidate => candidate.Theme + "|" + candidate.Language + "|" + candidate.Size == column);
                html.Append("<td>");
                if (cell != null)
                    html.Append($"<a href=\"{Escape(cell.File)}\"><img loading=\"lazy\" src=\"{Escape(cell.File)}\" alt=\"{Escape(cell.File)}\"></a>{Escape(cell.Client)}");
                html.Append("</td>");
            }
            html.AppendLine("</tr>");
        }
        html.AppendLine("</table></body></html>");
        string index = Path.Combine(output, output == AppContext.BaseDirectory ? "smoke-wv-index.html" : "index.html");
        File.WriteAllText(index, html.ToString());
        Console.WriteLine("SMOKE work-views index: " + index);
    }
}
