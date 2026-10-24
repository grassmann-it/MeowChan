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
    private static async Task<MediaRatingDialog> OpenStar(ArchiveAssetRow row)
    {
        await SelectAsync(row);
        Button edit = _page.FindControl<Button>("ArchiveEditRatingButton")!;
        await Program.WaitUntilAsync(() => edit.IsEnabled);
        Click(edit, "Open production rating dialog for selected exact identity");
        await Program.WaitUntilAsync(() => _window.OwnedWindows.OfType<MediaRatingDialog>().Any());
        MediaRatingDialog dialog = _window.OwnedWindows.OfType<MediaRatingDialog>().Single();
        await Program.WaitUntilAsync(() => dialog.IsVisible && Find<Ursa.Controls.Rating>(dialog, "MediaRatingEditor").IsEffectivelyVisible);
        await AssertEditorRender(dialog, Rating(row) ?? 0, "initial confirmed rating");
        await NativeFeatureCheckpoint.HoldAsync("rating-star", dialog, null, Find<Ursa.Controls.Rating>(dialog, "MediaRatingEditor"), Find<Button>(dialog, "MediaRatingClose"));
        return dialog;
    }
    private static Control Editor(Window dialog) => dialog is MediaRatingDialog
        ? Find<Ursa.Controls.Rating>(dialog, "MediaRatingEditor") : Find<ComboBox>(dialog, "NumericRatingEditor");
    private static string Prefix(Window dialog) => dialog is MediaRatingDialog ? "MediaRating" : "NumericRating";
    private static double Draft(Window dialog) => Editor(dialog) is Ursa.Controls.Rating star ? star.Value : ((ComboBox)Editor(dialog)).SelectedIndex;
    private static async Task Choose(Window dialog, int value)
    {
        await EditorReady(dialog);
        int guard = 0;
        while (Draft(dialog) != value && guard++ < 6)
        {
            double before = Draft(dialog);
            Press(Editor(dialog), before < value ? Key.Right : Key.Left, "Choose draft " + (before < value ? before + 1 : before - 1));
            await Task.Yield();
        }
        Require(Draft(dialog) == value, "Arrow input did not reach requested draft " + value);
        await AssertEditorRender(dialog, value, "chosen draft");
    }
    private static async Task Commit(Window dialog, ArchiveAssetRow row, int value)
    {
        await Choose(dialog, value);
        Press(Editor(dialog), Key.Enter, "Confirm draft " + value + " for " + row.Asset.OriginalFileName);
        await Program.WaitUntilAsync(() => Rating(row) == value && Editor(dialog).IsEnabled);
        await AssertEditorRender(dialog, value, "confirmed keyboard save");
        Observe("Keyboard confirmation persisted exact identity at " + value, Rating(row) == value);
    }
    private static async Task Close(Window dialog)
    {
        Press(Editor(dialog), Key.Escape, "Dismiss rating dialog with Escape");
        await Program.WaitUntilAsync(() => !dialog.IsVisible);
        await Task.Delay(70);
    }
    private static async Task<Window> OpenAlternative(ArchiveAssetRow row, bool numeric)
    {
        if (!numeric) return await OpenStar(row);
        Log("Open test-only labelled numeric alternative on same key and MainWindow commit delegate", "test fixture window creation");
        NumericRatingComparisonDialog dialog = new(KeyOf(row), Rating(row), value => _page.SetMediaRatingAsync!(KeyOf(row), value));
        _ = dialog.ShowDialog(_window);
        await Program.WaitUntilAsync(() => dialog.IsVisible);
        await AssertEditorRender(dialog, Rating(row) ?? 0, "initial numeric confirmed rating");
        await NativeFeatureCheckpoint.HoldAsync("rating-numeric", dialog, null, Find<ComboBox>(dialog, "NumericRatingEditor"), Find<Button>(dialog, "NumericRatingClose"));
        return dialog;
    }
    private static async Task CompareBasic(bool numeric)
    {
        ArchiveAssetRow row = Row(2);
        Log("Reset shared comparison identity to 3", "explicit fixture reset through same production host callback");
        await _page.SetMediaRatingAsync!(KeyOf(row), 3);
        string before = Digest(RatingPath);
        Window dialog = await OpenAlternative(row, numeric);
        await Choose(dialog, 4);
        Observe("Draft is not written", Rating(row) == 3 && Digest(RatingPath) == before);
        Program.CaptureWindow(dialog, numeric ? "smoke-rating-numeric-draft.png" : "smoke-rating-star-draft.png");
        await Close(dialog);
        Observe("Escape cancels draft with byte-identical ratings file", Rating(row) == 3 && Digest(RatingPath) == before);
        dialog = await OpenAlternative(row, numeric);
        await Commit(dialog, row, 4);
        Click(Find<Button>(dialog, Prefix(dialog) + "Clear"), "Clear exact rating to unrated");
        await Program.WaitUntilAsync(() => Rating(row) == null && Editor(dialog).IsEnabled);
        await AssertEditorRender(dialog, 0, "confirmed Clear");
        Observe("Clear means absent/unrated, never zero", Rating(row) == null);
        await Commit(dialog, row, 1);
        await Close(dialog);
    }
    private static async Task CompareRetry(bool numeric)
    {
        ArchiveAssetRow row = Row(4);
        Log("Reset retry comparison identity to 3", "explicit fixture reset through same production host callback");
        await _page.SetMediaRatingAsync!(KeyOf(row), 3);
        Window dialog = await OpenAlternative(row, numeric);
        string backup = RatingPath + ".bak", parked = backup + ".comparison-parked";
        Log("Inject real durable-write failure at backup path", "disposable fixture filesystem mutation");
        if (File.Exists(backup)) File.Move(backup, parked);
        Directory.CreateDirectory(backup);
        string before = Digest(RatingPath);
        try
        {
            await Choose(dialog, 4);
            Press(Editor(dialog), Key.Enter, "Attempt commit during durable write failure");
            await Program.WaitUntilAsync(() => Find<Button>(dialog, Prefix(dialog) + "Retry").IsVisible && Editor(dialog).IsEnabled);
            await AssertEditorRender(dialog, 3, "failed save restored confirmed rating");
            Observe("Failed commit leaves durable value and confirmed control at 3", Rating(row) == 3 && Digest(RatingPath) == before && Draft(dialog) == 3);
            Require(!string.IsNullOrWhiteSpace(Find<TextBlock>(dialog, Prefix(dialog) + "Error").Text), "Failure must be visible");
            Program.CaptureWindow(dialog, numeric ? "smoke-rating-numeric-retry.png" : "smoke-rating-star-retry.png");
        }
        finally
        {
            Directory.Delete(backup);
            if (File.Exists(parked)) File.Move(parked, backup);
        }
        Click(Find<Button>(dialog, Prefix(dialog) + "Retry"), "Retry same frozen attempted value after restoring backup path");
        await Program.WaitUntilAsync(() => Rating(row) == 4 && Editor(dialog).IsEnabled);
        await AssertEditorRender(dialog, 4, "confirmed retry");
        Observe("Retry commits original attempted 4", Rating(row) == 4 && Draft(dialog) == 4);
        await Close(dialog);
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
            // Mutate both versions through real dialog controls, restoring desired 1/5 after distinct intermediate values.
            foreach ((int index, int interim, int final) in new[] { (0, 2, 1), (1, 4, 5) })
            {
                MediaRatingDialog dialog = await OpenStar(Row(index)); await Commit(dialog, Row(index), interim); await Commit(dialog, Row(index), final); await Close(dialog);
            }
            Observe("Historical/current versions retain independent 1 and 5", Rating(Row(0)) == 1 && Rating(Row(1)) == 5);
            // Order crossover: star/numeric for draft+clear; numeric/star for failure+retry, same key and initial state within each task.
            await CompareBasic(false); await CompareBasic(true);
            await CompareRetry(true); await CompareRetry(false);
            // An initially unrated dialog creates Clear with CanExecute=false. Its first save must republish command availability.
            MediaRatingDialog firstRating = await OpenStar(Row(15));
            await Commit(firstRating, Row(15), 1);
            Button newlyAvailableClear = Find<Button>(firstRating, "MediaRatingClear");
            Require(newlyAvailableClear.IsEffectivelyEnabled, "First rating must make Clear genuinely available");
            Click(newlyAvailableClear, "Clear first rating from initially unrated dialog");
            await Program.WaitUntilAsync(() => Rating(Row(15)) == null && Editor(firstRating).IsEnabled);
            Observe("Initially unrated dialog enables actual Clear after first save", Rating(Row(15)) == null);
            await Close(firstRating);
            MediaRatingDialog missingDialog = await OpenStar(Row(3)); await Commit(missingDialog, Row(3), 2); await Close(missingDialog);
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
            MediaRatingDialog excluded = await OpenStar(Row(8)); await Commit(excluded, Row(8), 1); await Close(excluded);
            Observe("Lowered item disappears without selecting replacement", !Visible().Contains(Row(8)) && Tiles.SelectedItem == null && Table.SelectedItem == null);
            Observe("Filter receives real focus after excluded selected item closes", ReferenceEquals(window.FocusManager?.GetFocusedElement(), Filter));
            await GalleryFreeze();
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
            await Program.WaitUntilAsync(() => _window.OwnedWindows.OfType<MediaRatingDialog>().Any());
            await Close(_window.OwnedWindows.OfType<MediaRatingDialog>().Single());
            Observe("Configured shortcut reached real rating dialog outside ArchivePage visual ancestry");
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
        Click(Find<Button>(viewer, "GalleryRating"), "Edit frozen current gallery identity");
        await Program.WaitUntilAsync(() => _window.OwnedWindows.OfType<MediaRatingDialog>().Any());
        MediaRatingDialog dialog = _window.OwnedWindows.OfType<MediaRatingDialog>().Single();
        await Commit(dialog, Row(1), 1); await Close(dialog);
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
