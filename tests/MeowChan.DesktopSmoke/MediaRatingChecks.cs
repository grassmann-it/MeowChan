using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Favorites;
using MeowChan.Gallery;
using MeowChan.Persistence;
using MeowChan.Ratings;
using Texts = MeowChan.Desktop.Localization.Texts;

namespace MeowChan.DesktopSmoke;

/// <summary>Real application controls and durable host executor; input is in-process routed input, not OS/AT input.</summary>
internal static partial class MediaRatingChecks
{
    private static readonly List<object> Evidence = new();
    private static MediaRatingEntry[]? _expectedFinal;
    private static MainWindow _window = null!;
    private static ArchivePage _page = null!;
    private static string _profile = null!;
    private static ArchiveAssetRow[] _rows = Array.Empty<ArchiveAssetRow>();
    private static string RatingPath => Path.Combine(_profile, "media-ratings.json");
    private static string EvidencePath => Path.Combine(AppContext.BaseDirectory, "smoke-media-ratings-evidence.json");
    private static FavoriteAssetKey KeyOf(ArchiveAssetRow row) => FavoriteAssetKey.From(MediaRatingFixture.Thread, row.Asset);
    private static ArchiveAssetRow Row(int index) => _rows.Single(row => row.Asset.OriginalFileName == $"rating-{index:D2}.png");
    private static ListBox Tiles => _page.FindControl<ListBox>("ArchiveMediaTiles")!;
    private static DataGrid Table => _page.FindControl<DataGrid>("ArchiveMediaGrid")!;
    private static ComboBox Filter => _page.FindControl<ComboBox>("ArchiveRatingFilter")!;
    private static int? Rating(ArchiveAssetRow row) => _window.SessionHost.GetMediaRating(KeyOf(row));
    private static string Digest(string path) => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant() : "absent";
    private static T Find<T>(Control root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static MediaRatingEntry[] DiskRatings()
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(RatingPath));
        return json.RootElement.GetProperty("Entries").Deserialize<MediaRatingEntry[]>()!;
    }
    private static string Focus(Window window) => window.FocusManager?.GetFocusedElement() is Control control
        ? control.GetType().Name + ":" + (control.Name ?? control.ToString()) : "none";
    private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException("URSA16: " + message); }
    private static void Log(string intent, string mechanism, Control? target = null)
    {
        Window active = target == null ? _window : TopLevel.GetTopLevel(target) as Window ?? _window;
        Evidence.Add(new { sequence = Evidence.Count + 1, intent, mechanism, target = target?.Name ?? target?.GetType().Name,
            focusBefore = Focus(active), persistedSha256Before = Digest(RatingPath) });
        SaveEvidence("running");
    }
    private static void Observe(string assertion, bool result = true)
    {
        Evidence.Add(new { sequence = Evidence.Count + 1, assertion, result, focusAfter = Focus(_window),
            persistedSha256After = Digest(RatingPath), persisted = DiskRatings(), hostSnapshot = _window.SessionHost.GetMediaRatings() });
        SaveEvidence(result ? "running" : "failed");
        Require(result, assertion);
        MediaRatingEntry[] disk = DiskRatings();
        IReadOnlyList<MediaRatingEntry> cached = _window.SessionHost.GetMediaRatings();
        Require(disk.Length == cached.Count && disk.All(cached.Contains), "Durable file and published host snapshot differ after: " + assertion);
    }
    private static void SaveEvidence(string status) => File.WriteAllText(EvidencePath, JsonSerializer.Serialize(new
    {
        status, suite = Environment.GetEnvironmentVariable("MEOWCHAN_RATINGS_LATE_ERROR_ONLY") == "1" ? "targeted rating state regressions" : Environment.GetEnvironmentVariable("MEOWCHAN_RATINGS_PRESENTATION_ONLY") == "1" ? "12-cell presentation matrix" : "semantic ratings regression",
        sourceSnapshotDigest = Environment.GetEnvironmentVariable("MEOWCHAN_SOURCE_DIGEST") ?? "executor must bind this run to immutable source manifest",
        baseSnapshot = "full-06", baseDigest = "3b69fe4e5796db28b33ee0ca668a8fdcb86a14cd8ca79e2d293186f9d7935269",
        fixtureModelSha256 = MediaRatingFixture.ModelDigest,
        fixtureRealization = "Same twenty model rows, ratings/favorites/version/post relationships; real verified PNG SHA replaces illustrative zero-padded model SHA. Mapping below.",
        language = Texts.Language, theme = Application.Current?.ActualThemeVariant.ToString(), mode = _window?.SessionHost.Config.WorkspaceMode,
        inputScope = "in-process routed KeyDown and real control actions; ComboBox selection property is explicitly logged; no OS keyboard or native accessibility claim",
        nativeAssistiveTechnology = "open", humanScalePreference = "open; no preference inferred from functional tests",
        processRestart = "not performed; fresh store under reacquired profile lease is checked separately",
        identities = _rows.Select(row => new { key = KeyOf(row), row.Asset.AssetId, row.Asset.OriginalFileName,
            row.Asset.IsCurrent, fileDigest = Digest(row.Asset.Path), initialModel = MediaRatingFixture.Specs.Single(s => row.Asset.OriginalFileName == $"rating-{s.Index:D2}.png") }),
        actions = Evidence
    }, new JsonSerializerOptions { WriteIndented = true }));
    private static KeyEventArgs Press(Control control, Key key, string intent, KeyModifiers modifiers = KeyModifiers.None)
    {
        Log(intent, "in-process routed KeyDown", control);
        KeyEventArgs e = new() { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers };
        control.RaiseEvent(e); return e;
    }
    private static void Click(Control control, string intent)
    {
        Require(control.IsEffectivelyVisible && control.IsEffectivelyEnabled, intent + " target is not reachable");
        Log(intent, control is Button { Command: not null } ? "real Button ICommand"
            : control is MenuItem { Command: not null } ? "real MenuItem ICommand" : "real routed Click", control);
        if (control is MenuItem { Command: { } command } item)
        {
            Require(command.CanExecute(item.CommandParameter), intent + " menu command is unavailable");
            command.Execute(item.CommandParameter);
        }
        else SmokeActionClick.Execute(control);
    }
    private static async Task<Control> FocusSelectedAsync(Control list, object selected)
    {
        Control? Container() => list.GetVisualDescendants().OfType<Control>().FirstOrDefault(control =>
            control is ListBoxItem or DataGridRow && ReferenceEquals(control.DataContext, selected)
            && control.IsEffectivelyVisible && control.IsEffectivelyEnabled && control.Bounds.Width > 0 && control.Bounds.Height > 0);
        try
        {
            SelectionSnapshot(list, selected, "before materialized-container wait");
            await Program.WaitUntilAsync(() => Container() != null);
            Control container = Container()!;
            // Native card focus belongs to ListBoxItem, not the non-focusable ListBox.
            // In a table use its actual cell/grid keyboard target without changing product Focusable flags.
            Control? target = container.Focusable ? container : container.GetVisualDescendants().OfType<DataGridCell>()
                .FirstOrDefault(cell => cell.Focusable && cell.IsEffectivelyVisible && cell.IsEffectivelyEnabled);
            if (target == null && list is DataGrid && list.Focusable) target = list;
            Control focusTarget = target ?? throw new InvalidOperationException("URSA16: Selected materialized row has no native keyboard focus target");
            SelectionSnapshot(list, selected, "before selected-container focus request", focusTarget);
            Log("Focus selected materialized row", "native focus target via Focus(NavigationMethod.Tab)", focusTarget);
            Require(focusTarget.Focus(NavigationMethod.Tab), "Selected materialized keyboard target refused focus");
            Require(ReferenceEquals(TopLevel.GetTopLevel(focusTarget)?.FocusManager?.GetFocusedElement(), focusTarget),
                "FocusManager did not retain the selected materialized keyboard target");
            SelectionSnapshot(list, selected, "actual selected-container focus confirmed", focusTarget);
            return focusTarget;
        }
        catch (Exception error)
        {
            try { SelectionSnapshot(list, selected, "selection/container/focus failure", error: error); } catch (Exception) { }
            try
            {
                if (TopLevel.GetTopLevel(list) is Window { IsVisible: true } owner)
                    Program.CaptureWindow(owner, "smoke-rating-selection-readiness-failure.png");
            }
            catch (Exception) { }
            throw;
        }
    }
    private static async Task<Control> SelectAsync(ArchiveAssetRow row)
    {
        ReaderTreeSnapshot("before selecting current visible media row");
        ListBox liveTiles = await CurrentMediaTilesAsync();
        SelectionSnapshot(liveTiles, row, "before resolving current presentation row by exact key");
        // Language changes rebuild ArchiveAssetRow objects. Retain the fixture identity,
        // but select its current visible row rather than a detached old object instance.
        ArchiveAssetRow[] matches = liveTiles.Items.Cast<ArchiveAssetRow>().Where(candidate => KeyOf(candidate).Equals(KeyOf(row))).ToArray();
        Require(matches.Length == 1, "Exact rating identity must resolve to one current visible row");
        ArchiveAssetRow current = matches[0];
        Log("Select exact media " + row.Asset.OriginalFileName, "ListBox selection property on current exact-key row", liveTiles);
        liveTiles.SelectedItem = current; liveTiles.ScrollIntoView(current);
        SelectionSnapshot(liveTiles, current, "after current exact-key selection and ScrollIntoView");
        Require(ReferenceEquals(liveTiles.SelectedItem, current), "ListBox did not select the current exact-key row");
        return await FocusSelectedAsync(liveTiles, current);
    }
    // Review 15.11.: the rating modal is gone. The archive rates through its rating menu (the stars and "Remove rating" as radio items of a
    // context menu); the checks open that real menu and invoke the real item, like a keyboard or pointer user does.
    private static ContextMenu? OpenMenuOn(Control anchor) => anchor.ContextMenu?.IsOpen == true ? anchor.ContextMenu : null;
    private static async Task<ContextMenu> OpenRatingMenu(ArchiveAssetRow row)
    {
        await SelectAsync(row);
        Button edit = _page.FindControl<Button>("ArchiveEditRatingButton")!;
        await Program.WaitUntilAsync(() => edit.IsEnabled);
        Click(edit, "Open the production rating menu for the selected exact identity");
        ContextMenu? menu = null;
        await Program.WaitUntilAsync(() => (menu = OpenMenuOn(_page.RatingMenuAnchor(edit))) != null);
        Require(menu!.Items.OfType<MenuItem>().Count(item => item.Tag is string tag && tag.StartsWith("media.rating.", StringComparison.Ordinal)) == 6,
            "The rating menu must offer five stars and Remove rating");
        return menu!;
    }
    private static MenuItem RatingItem(ContextMenu menu, int? value) =>
        menu.Items.OfType<MenuItem>().Single(item => item.Tag as string == (value is { } star ? "media.rating.set-" + star : "media.rating.clear"));
    private static async Task CloseRatingMenu(ContextMenu menu)
    {
        menu.Close();
        await Program.WaitUntilAsync(() => !menu.IsOpen);
        await Task.Delay(70);
    }
    // The checked radio item is the stored rating (none checked while unrated).
    private static void RequireMenuShows(ContextMenu menu, int? rating, string phase)
    {
        int? checkedStar = Enumerable.Range(1, 5).Cast<int?>().SingleOrDefault(star => RatingItem(menu, star).IsChecked);
        Require(checkedStar == rating, phase + ": the rating menu checks " + (checkedStar?.ToString() ?? "no star") + " instead of " + (rating?.ToString() ?? "no star"));
        Require(RatingItem(menu, null).IsEnabled == rating.HasValue, phase + ": Remove rating must be available exactly while the medium is rated");
    }
    private static async Task Pick(ArchiveAssetRow row, int? value)
    {
        ContextMenu menu = await OpenRatingMenu(row);
        MenuItem item = RatingItem(menu, value);
        Click(item, value is { } star ? "Rate the exact identity " + star : "Remove the exact rating");
        await Program.WaitUntilAsync(() => Rating(row) == value);
        if (menu.IsOpen) await CloseRatingMenu(menu);
        Observe("Menu choice persisted exact identity at " + (value?.ToString() ?? "unrated"), Rating(row) == value);
    }
    private static async Task CompareBasic()
    {
        ArchiveAssetRow row = Row(2);
        Log("Reset shared comparison identity to 3", "explicit fixture reset through same production host callback");
        await _page.SetMediaRatingAsync!(KeyOf(row), 3);
        string before = Digest(RatingPath);
        ContextMenu menu = await OpenRatingMenu(row);
        RequireMenuShows(menu, 3, "initial confirmed rating");
        Program.CaptureWindow(_window, "smoke-rating-menu-open.png");
        await CloseRatingMenu(menu);
        Observe("Closing the menu without a choice leaves a byte-identical ratings file", Rating(row) == 3 && Digest(RatingPath) == before);
        await Pick(row, 4);
        await Pick(row, null);
        Observe("Remove rating means absent/unrated, never zero", Rating(row) == null);
        await Pick(row, 1);
    }
    private static async Task CompareRetry()
    {
        ArchiveAssetRow row = Row(4);
        Log("Reset retry comparison identity to 3", "explicit fixture reset through same production host callback");
        await _page.SetMediaRatingAsync!(KeyOf(row), 3);
        string backup = RatingPath + ".bak", parked = backup + ".comparison-parked";
        Log("Inject real durable-write failure at backup path", "disposable fixture filesystem mutation");
        if (File.Exists(backup)) File.Move(backup, parked);
        Directory.CreateDirectory(backup);
        string before = Digest(RatingPath);
        try
        {
            ContextMenu menu = await OpenRatingMenu(row);
            Click(RatingItem(menu, 4), "Attempt a rating during the durable write failure");
            await Task.Delay(300);
            if (menu.IsOpen) await CloseRatingMenu(menu);
            Observe("Failed write leaves the durable value at 3", Rating(row) == 3 && Digest(RatingPath) == before);
            menu = await OpenRatingMenu(row);
            RequireMenuShows(menu, 3, "after the failed write");
            await CloseRatingMenu(menu);
        }
        finally
        {
            Directory.Delete(backup);
            if (File.Exists(parked)) File.Move(parked, backup);
        }
        // There is no retry button: the next choice of the same star tries again.
        await Pick(row, 4);
    }
    private static void SetChoice(ComboBox combo, int index, string intent)
    { Log(intent, "real ComboBox SelectedIndex property (not native dropdown input)", combo); combo.SelectedIndex = index; }
    private static ArchiveAssetRow[] Visible() => Tiles.ItemsSource!.Cast<ArchiveAssetRow>().ToArray();
    private static void Projection(string description, Func<ArchiveAssetRow, bool> predicate)
    {
        string[] expected = _rows.Where(predicate).Select(r => r.Asset.AssetId).Order().ToArray();
        string[] actual = Visible().Select(r => r.Asset.AssetId).Order().ToArray();
        Observe(description, actual.SequenceEqual(expected));
        Require(Table.ItemsSource!.Cast<ArchiveAssetRow>().Select(r => r.Asset.AssetId).SequenceEqual(Visible().Select(r => r.Asset.AssetId)), "Tiles and table must have identical order");
    }
    internal static async Task RunAsync(MainWindow window)
    {
        _window = window; _page = window.ArchivePage; _profile = Config.GetConfigDirectory();
        try
        {
            Texts.SetLanguage(Environment.GetEnvironmentVariable("MEOWCHAN_RATINGS_LANGUAGE") ?? "en");
            Application.Current!.RequestedThemeVariant = Environment.GetEnvironmentVariable("MEOWCHAN_RATINGS_THEME") == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
            bool barebones = window.SessionHost.Config.WorkspaceMode == "Barebones";
            window.Width = barebones ? 640 : 760; window.Height = barebones ? 480 : 500;
            window.Activate();
            await Program.WaitUntilAsync(() => window.IsActive);
            KeyGesture archiveGesture = CommandBindings.Gesture("navigation.archive")!;
            Press(window, archiveGesture.Key, "Navigate to real archive with configured shortcut", archiveGesture.KeyModifiers);
            int expectedThreads = Environment.GetEnvironmentVariable("MEOWCHAN_RATINGS_LATE_ERROR_ONLY") == "1" ? 2 : 1;
            await Program.WaitUntilAsync(() => !_page.IsBusy && _page.FindControl<ListBox>("ArchiveThreads")!.ItemCount == expectedThreads);
            // At narrow width a selection intentionally leaves the embedded reader hidden.
            // Use the visible results context command; the hidden reader toolbar is not an entry point.
            ListBox cards = _page.FindControl<ListBox>("ArchiveThreads")!;
            DataGrid threadsTable = _page.FindControl<DataGrid>("ArchiveThreadTable")!;
            await Program.WaitUntilAsync(() => cards.IsEffectivelyVisible || threadsTable.IsEffectivelyVisible);
            Control results = cards.IsEffectivelyVisible ? cards : threadsTable;
            Log("Select fixture thread in visible results", "real results selection property", results);
            if (results is ListBox visibleCards) { visibleCards.SelectedIndex = 0; visibleCards.ScrollIntoView(visibleCards.SelectedItem!); }
            else threadsTable.SelectedIndex = 0;
            await Program.WaitUntilAsync(() => !_page.IsBusy && results.IsEffectivelyEnabled
                && (results.ContextMenu != null || results.GetVisualDescendants().OfType<Control>().Any(control => control.ContextMenu != null)));
            object selectedThread = results is ListBox selectedCards ? selectedCards.SelectedItem! : threadsTable.SelectedItem!;
            Control resultFocus = await FocusSelectedAsync(results, selectedThread);
            KeyGesture contextGesture = CommandBindings.Gesture("menu.context-open")!;
            Require(Press(resultFocus, contextGesture.Key, "Open visible selected-thread context menu", contextGesture.KeyModifiers).Handled,
                "Selected-thread context gesture was not handled");
            ContextMenu? OpenResultsMenu() => results.ContextMenu?.IsOpen == true ? results.ContextMenu
                : results.GetVisualDescendants().OfType<Control>().Select(control => control.ContextMenu).FirstOrDefault(menu => menu?.IsOpen == true);
            await Program.WaitUntilAsync(() => OpenResultsMenu() != null);
            ContextMenu resultMenu = OpenResultsMenu()!;
            MenuItem read = resultMenu.Items.OfType<MenuItem>().Single(item => item.Tag as string == "thread.read");
            await Program.WaitUntilAsync(() => read.IsEffectivelyVisible && read.IsEffectivelyEnabled && read.Bounds.Width > 0);
            read.Focus();
            Click(read, "Open selected thread in detached reader from visible context action");
            resultMenu.Close();
            await Program.WaitUntilAsync(() => window.FindControl<ContentControl>("ReaderHost")!.IsVisible && !_page.IsBusy && _page.CurrentThread != null);
            _page.FindControl<TabControl>("ArchiveContentTabs")!.SelectedIndex = 1;
            await _page.MediaLoadTask;
            SetChoice(_page.FindControl<ComboBox>("ArchiveVersionScope")!, 1, "Show all saved versions");
            await Program.WaitUntilAsync(() => Tiles.ItemCount == 20);
            _rows = Visible();
            Dictionary<string, string> mediaBefore = _rows.ToDictionary(row => row.Asset.Path, row => Digest(row.Asset.Path));
            string favoritesBefore = Digest(Path.Combine(_profile, "media-favorites.json"));
            Observe("20 exact identities and current/historical versions", _rows.Select(KeyOf).Distinct().Count() == 20 && !Row(0).Asset.IsCurrent && Row(1).Asset.IsCurrent);
            GalleryEntry[] associations = GallerySelection.Build(_page.CurrentThread!, _rows.Select(r => r.Manifest).Distinct(), true, true).Entries.Where(e => e.Asset.MediaIdentifier == "m2").ToArray();
            Observe("Two posts share one exact media rating identity", associations.Length == 2 && associations.Select(e => FavoriteAssetKey.From(e.Key.Thread, e.Asset)).Distinct().Count() == 1);
            if (Environment.GetEnvironmentVariable("MEOWCHAN_RATINGS_LATE_ERROR_ONLY") == "1")
            {
                await RunTargetedRatingStateChecksAsync();
                Observe("Late-error fixture preserved favorites", Digest(Path.Combine(_profile, "media-favorites.json")) == favoritesBefore);
                Observe("Late-error fixture preserved media bytes and absence", mediaBefore.All(pair => Digest(pair.Key) == pair.Value));
                _expectedFinal = window.SessionHost.GetMediaRatings().ToArray();
                SaveEvidence("Targeted rating state regressions passed; waiting for lease/store reopen");
                return;
            }
            if (Environment.GetEnvironmentVariable("MEOWCHAN_RATINGS_PRESENTATION_ONLY") == "1")
            {
                await RunPresentationMatrix();
                Observe("Presentation matrix preserved favorite bytes", Digest(Path.Combine(_profile, "media-favorites.json")) == favoritesBefore);
                Observe("Presentation matrix preserved original files and absence", mediaBefore.All(pair => Digest(pair.Key) == pair.Value));
                _expectedFinal = window.SessionHost.GetMediaRatings().ToArray();
                SaveEvidence("12-cell presentation matrix passed; waiting for lease/store reopen");
                return;
            }
            await DetachedShortcut();
            // Mutate both versions through the real rating menu, restoring desired 1/5 after distinct intermediate values.
            foreach ((int index, int interim, int final) in new[] { (0, 2, 1), (1, 4, 5) })
            {
                await Pick(Row(index), interim); await Pick(Row(index), final);
            }
            Observe("Historical/current versions retain independent 1 and 5", Rating(Row(0)) == 1 && Rating(Row(1)) == 5);
            await CompareBasic();
            await CompareRetry();
            // An initially unrated medium offers Remove rating disabled; after the first rating the menu offers it.
            ContextMenu unratedMenu = await OpenRatingMenu(Row(15));
            RequireMenuShows(unratedMenu, null, "initially unrated");
            await CloseRatingMenu(unratedMenu);
            await Pick(Row(15), 1);
            ContextMenu firstRated = await OpenRatingMenu(Row(15));
            RequireMenuShows(firstRated, 1, "first rating");
            Click(RatingItem(firstRated, null), "Remove the first rating from the initially unrated medium");
            await Program.WaitUntilAsync(() => Rating(Row(15)) == null);
            if (firstRated.IsOpen) await CloseRatingMenu(firstRated);
            Observe("Remove rating becomes available after the first save and removes it", Rating(Row(15)) == null);
            await Pick(Row(3), 2);
            Observe("Missing local bytes do not prevent exact metadata rating", !File.Exists(Row(3).Asset.Path) && Rating(Row(3)) == 2);
            SetChoice(Filter, 1, "Filter unrated only"); Projection("Unrated projection", row => Rating(row) == null);
            SetChoice(Filter, 5, "Filter minimum 4"); Projection("Minimum 4 projection", row => Rating(row) >= 4);
            Click(_page.FindControl<Button>("ArchiveFavoritesFilter")!, "Intersect rating and favorites");
            Projection("Favorite AND minimum 4", row => Rating(row) >= 4 && MediaRatingFixture.Specs.Single(s => row.Asset.OriginalFileName == $"rating-{s.Index:D2}.png").Favorite);
            SetChoice(_page.FindControl<ComboBox>("ArchiveVersionScope")!, 0, "Intersect current version scope");
            Projection("Current AND favorite AND minimum 4", row => row.Asset.IsCurrent && Rating(row) >= 4 && MediaRatingFixture.Specs.Single(s => row.Asset.OriginalFileName == $"rating-{s.Index:D2}.png").Favorite);
            Click(_page.FindControl<Button>("ArchiveFavoritesFilter")!, "Restore all favorite states");
            SetChoice(_page.FindControl<ComboBox>("ArchiveVersionScope")!, 1, "Restore all versions");
            SetChoice(Filter, 0, "Restore all rating values");
            SetChoice(_page.FindControl<ComboBox>("ArchiveMediaSort")!, 3, "Sort rating descending, unrated last");
            int[] ranks = Visible().Select(row => Rating(row) ?? 0).ToArray();
            Observe("Descending rating order and unrated last", ranks.SequenceEqual(ranks.OrderDescending()));
            string[] stable = Visible().Select(row => row.Asset.AssetId).ToArray();
            SetChoice(Filter, 1, "Exercise rebind away from full projection"); SetChoice(Filter, 0, "Return to full projection for stable tie check");
            Observe("Stable exact-key order after filter round trip", stable.SequenceEqual(Visible().Select(row => row.Asset.AssetId)));
            Click(_page.FindControl<Button>("ArchiveMediaMode")!, "Switch to actual table view");
            Program.CaptureWindow(window, "smoke-rating-table.png");
            Click(_page.FindControl<Button>("ArchiveMediaMode")!, "Return to actual tiles view");
            await Task.Delay(100); CheckTileLabels();
            SetChoice(Filter, 5, "Filter minimum 4 for selection removal");
            await Pick(Row(8), 1);
            Observe("Lowered item disappears without selecting replacement", !Visible().Contains(Row(8)) && Tiles.SelectedItem == null && Table.SelectedItem == null);
            Observe("Filter receives real focus after excluded selected item closes", ReferenceEquals(window.FocusManager?.GetFocusedElement(), Filter));
            await GalleryFreeze();
            // AP6: the manager is reached from where ratings live (the archive rating row here, the Media page and the gallery menu elsewhere).
            Observe("Archive rating row offers the saved-ratings manager", _page.FindControl<Button>("ArchiveManageRatingsButton") is { IsEnabled: true });
            await ManagerCancelFocus();
            Observe("Favorites byte-for-byte independent of ratings", Digest(Path.Combine(_profile, "media-favorites.json")) == favoritesBefore);
            Observe("Original files and absence unchanged", mediaBefore.All(pair => Digest(pair.Key) == pair.Value));
            _expectedFinal = window.SessionHost.GetMediaRatings().ToArray();
            Program.CaptureWindow(window, "smoke-rating-final.png"); SaveEvidence("GUI checks passed; waiting for lease/store reopen");
            Console.WriteLine("SMOKE URSA16 real controls: comparisons, identity, projections, detached shortcut, gallery freeze, manager cancel focus passed; " + EvidencePath);
        }
        catch (Exception error)
        {
            Evidence.Add(new { failure = error.ToString() }); SaveEvidence("failed"); throw;
        }
    }
    private static async Task DetachedShortcut()
    {
        Config original = _window.SessionHost.Config.Clone();
        try
        {
            Config candidate = original.Clone();
            candidate.ShortcutBindings = candidate.ShortcutBindings.Set("media.rating.edit", new[] { ShortcutGesture.Parse("Primary+Shift+Y") });
            Log("Install temporary detached rating shortcut", "fixture settings through production settings writer");
            await _window.SessionHost.ApplySettingsAsync(candidate);
            _window.Activate();
            await Program.WaitUntilAsync(() => _window.IsActive);
            Control selected = await SelectAsync(Row(2));
            Require(!Tiles.GetSelfAndVisualAncestors().Contains(_page), "Regression must target genuinely reparented reader pane");
            KeyGesture gesture = CommandBindings.Gesture("media.rating.edit")!;
            Require(Press(selected, gesture.Key, "Invoke configured rating edit on detached pane", gesture.KeyModifiers).Handled, "Detached shortcut not routed");
            ContextMenu? opened = null;
            await Program.WaitUntilAsync(() => (opened = OpenMenuOn(selected)) != null);
            await CloseRatingMenu(opened!);
            Observe("Configured shortcut opened the real rating menu outside ArchivePage visual ancestry");
        }
        finally { await _window.SessionHost.ApplySettingsAsync(original); }
    }
    private static void CheckTileLabels()
    {
        Control pane = _page.FindControl<Grid>("ArchiveReaderPane")!;
        ArchiveMediaTileView[] realized = pane.GetVisualDescendants().OfType<ArchiveMediaTileView>().Where(t => t.DataContext is ArchiveAssetRow).ToArray();
        Require(realized.Length > 0, "No realized tiles for automation-label check");
        Observe("Realized recycled tile names match current rating", realized.All(tile => AutomationProperties.GetName(tile)?.Contains(((ArchiveAssetRow)tile.DataContext!).RatingLabel, StringComparison.Ordinal) == true));
    }
    private static async Task GalleryFreeze()
    {
        Control selected = await SelectAsync(Row(1));
        string[] order = Visible().Select(row => row.Asset.AssetId).ToArray();
        Press(selected, Key.Enter, "Open gallery from selected visible projection");
        await Program.WaitUntilAsync(() => _window.FindControl<ContentControl>("GalleryHost")!.IsVisible);
        GalleryViewer viewer = Field<GalleryViewer>(_window, "_gallery");
        await Program.WaitUntilAsync(() => viewer.CaptureState().Key?.AssetId == Row(1).Asset.AssetId);
        GalleryEntry[] entries = Field<List<GalleryEntry>>(_window, "_galleryEntries").ToArray();
        Require(order.SequenceEqual(entries.Select(entry => entry.Asset.AssetId)), "Gallery did not freeze visible order");
        int index = Field<int>(_window, "_galleryIndex");
        // The rating key of the gallery (Shift+1) goes through the viewer's own router, the same path as a keyboard user.
        KeyGesture setOne = CommandBindings.Gesture("media.rating.set-1")!;
        Press(viewer, setOne.Key, "Rate the frozen current gallery identity with its key", setOne.KeyModifiers);
        await Program.WaitUntilAsync(() => Rating(Row(1)) == 1);
        Observe("Gallery membership/order/current frozen after current item leaves archive rating filter",
            entries.Select(e => e.EntryKey).SequenceEqual(Field<List<GalleryEntry>>(_window, "_galleryEntries").Select(e => e.EntryKey))
            && Field<int>(_window, "_galleryIndex") == index && viewer.CaptureState().Key?.AssetId == Row(1).Asset.AssetId);
        Program.CaptureWindow(_window, "smoke-rating-gallery-frozen.png");
        Click(Find<Button>(viewer, "GalleryClose"), "Close gallery and return to active archive filter");
        await _window.GalleryCleanupTask;
        await Program.WaitUntilAsync(() => !_window.FindControl<ContentControl>("GalleryHost")!.IsVisible);
        Observe("Gallery return preserves filter and removed selection", Filter.SelectedIndex == 5 && !Visible().Contains(Row(1)) && Tiles.SelectedItem == null);
    }
    private static async Task ManagerCancelFocus(string? captureName = null)
    {
        Log("Open production metadata manager", "production dialog fixture entry point");
        MediaRatingsManagerDialog manager = new(_window.SessionHost);
        Task? showTask = null;
        int expectedRows = _window.SessionHost.GetMediaRatings().Count;
        ListBox? ManagerList() => manager.GetVisualDescendants().OfType<ListBox>().FirstOrDefault(control => control.Name == "MediaRatingsList");
        void Readiness(string stage)
        {
            var state = new { managerReadiness = stage, manager.IsVisible, manager.IsActive, manager.IsEnabled,
                manager.IsKeyboardFocusWithin, bounds = manager.Bounds.ToString(), client = manager.ClientSize.ToString(),
                expectedRows, actualRows = ManagerList()?.ItemCount, focus = Focus(manager),
                correctOwner = ReferenceEquals(manager.Owner, _window), ownerVisible = _window.IsVisible,
                ownerActive = _window.IsActive, ownerEnabled = _window.IsEnabled, ownerFocus = Focus(_window),
                ownedWindows = _window.OwnedWindows.Select(child => new { type = child.GetType().Name, child.IsVisible, child.IsActive, child.IsEnabled }).ToArray(),
                showTaskStatus = showTask?.Status.ToString(), showTaskFault = showTask?.Exception?.ToString(),
                managerError = Field<TextBlock>(manager, "_error").Text };
            Evidence.Add(state); SaveEvidence("running");
            Console.WriteLine("SMOKE ratings manager readiness: " + JsonSerializer.Serialize(state));
        }
        try
        {
            Readiness("before owner activation");
            // The fixture calls ShowDialog directly; establish the active shell that a real opening action supplies.
            Log("Activate real manager owner", "Window.Activate on visible shell", _window);
            _window.Activate();
            await Program.WaitUntilAsync(() => _window.IsVisible && _window.IsActive
                && !_window.OwnedWindows.Any(child => child.IsVisible));
            Readiness("owner ready");
            showTask = manager.ShowDialog(_window);
            Readiness("ShowDialog returned");
            await Program.WaitUntilAsync(() =>
            {
                if (showTask.IsFaulted) throw new InvalidOperationException("Manager ShowDialog faulted", showTask.Exception);
                return manager.IsVisible && manager.Bounds.Width > 0 && manager.Bounds.Height > 0;
            });
            Require(ReferenceEquals(manager.Owner, _window), "Shown manager must belong to the actual shell owner");
            Readiness("shown before manager activation");
            Log("Activate shown real owned manager", "Window.Activate on shown owned dialog", manager);
            manager.Activate();
            await Program.WaitUntilAsync(() =>
            {
                if (showTask.IsFaulted) throw new InvalidOperationException("Manager ShowDialog faulted", showTask.Exception);
                return manager.IsActive && expectedRows > 0 && ManagerList()?.ItemCount == expectedRows;
            });
            Readiness("manager active with exact metadata row count");
        }
        catch
        {
            Readiness("readiness failed");
            if (manager.IsVisible && manager.Bounds.Width > 0 && manager.Bounds.Height > 0)
                Program.CaptureWindow(manager, "smoke-rating-manager-readiness-failure.png");
            throw;
        }
        ListBox list = Find<ListBox>(manager, "MediaRatingsList"); list.SelectedIndex = 0;
        object selected = list.SelectedItem!;
        Button remove = Find<Button>(manager, "MediaRatingsRemove"), cancel = Field<Button>(manager, "_cancel");
        string before = Digest(RatingPath);
        foreach (bool escape in new[] { true, false })
        {
            Evidence.Add(new { assertion = "Manager Remove readiness before focus", manager.IsActive,
                remove.IsEnabled, remove.IsEffectivelyEnabled, remove.IsEffectivelyVisible, remove.Focusable,
                bounds = remove.Bounds.ToString(), ownTopLevel = ReferenceEquals(TopLevel.GetTopLevel(remove), manager),
                commandCanExecute = remove.Command?.CanExecute(remove.CommandParameter), focusBefore = Focus(manager) });
            SaveEvidence("running");
            await Program.WaitUntilAsync(() => manager.IsActive && remove.IsEffectivelyVisible && remove.IsEffectivelyEnabled
                && remove.Bounds.Width > 0 && remove.Bounds.Height > 0);
            Require(ReferenceEquals(TopLevel.GetTopLevel(remove), manager), "Remove must belong to actual manager TopLevel");
            Log("Focus real manager Remove trigger", "Focus(NavigationMethod.Tab) on active owned dialog", remove);
            Require(remove.Focus(NavigationMethod.Tab), "Remove must accept real focus before confirmation");
            Require(ReferenceEquals(manager.FocusManager?.GetFocusedElement(), remove), "Remove must own real focus before confirmation");
            Click(remove, "Open frozen remove confirmation");
            Require(ReferenceEquals(manager.FocusManager?.GetFocusedElement(), cancel), "Cancel must own real focus during confirmation");
            if (escape && captureName != null) Program.CaptureWindow(manager, captureName.Replace(".png", "-confirmation.png", StringComparison.Ordinal));
            if (escape) Press(cancel, Key.Escape, "Escape cancels manager remove confirmation");
            else Click(cancel, "Cancel button dismisses manager remove confirmation");
            await Task.Yield();
            bool restored = ReferenceEquals(manager.FocusManager?.GetFocusedElement(), remove);
            Evidence.Add(new { assertion = "Manager cancel focus", escape, focusAfter = Focus(manager), result = restored });
            Observe("Manager cancel restores reachable original trigger, selection and file", restored && manager.IsVisible && !cancel.IsVisible && ReferenceEquals(list.SelectedItem, selected) && Digest(RatingPath) == before);
        }
        Program.CaptureWindow(manager, captureName ?? "smoke-rating-manager-focus.png");
        Press(remove, Key.Escape, "Close metadata manager outside confirmation");
        await Program.WaitUntilAsync(() => !manager.IsVisible);
    }
    internal static void VerifyAfterClose(string profile, ProfileLease lease)
    {
        Require(_expectedFinal != null, "Expected final rating snapshot missing");
        MediaRatingEntry[] reopened = new MediaRatingStore(profile, lease).List().ToArray();
        Require(reopened.Length == _expectedFinal!.Length && reopened.All(entry => _expectedFinal.Contains(entry)), "Fresh store under reacquired profile lease differs");
        Evidence.Add(new { assertion = "Fresh MediaRatingStore under reacquired lease matches all final exact-key values", result = true, persisted = reopened,
            scope = "store reopen after normal application close, not new process launch" });
        SaveEvidence("passed; native AT and human preference remain open");
        Console.WriteLine("SMOKE URSA16 final durable values verified under newly acquired profile lease.");
    }
}
