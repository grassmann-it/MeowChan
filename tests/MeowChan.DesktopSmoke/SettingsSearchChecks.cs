using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

/// <summary>The settings search in a real editor: typo-tolerant hits across groups, arrow keys, Enter, collapsed sections, Escape, EN/DE, captures.</summary>
internal static class SettingsSearchChecks
{
    internal static async Task RunAsync(MainWindow owner)
    {
        string profile = Path.Combine(TemporaryWorkspacePaths.TempRoot, "settings-search-" + Guid.NewGuid().ToString("N"));
        string language = Texts.Language;
        ThemeVariant? theme = Application.Current!.RequestedThemeVariant;
        Config config = Config.LoadConfig(profile);
        var failures = new List<Exception>();
        async Task CheckAsync(string name, Func<Task> check)
        {
            try { await check(); }
            catch (Exception error)
            {
                Console.Error.WriteLine($"SMOKE settings search [{name}]: {error}");
                failures.Add(new InvalidOperationException(name, error));
            }
        }
        int commits = 0;
        var window = new SettingsWindow(config, _ => { commits++; return Task.CompletedTask; });
        Task dialog = window.ShowDialog(owner);
        SettingsEditor editor = window.Editor;
        try
        {
            await Wait(() => window.IsVisible);
            window.Activate();
            await Wait(() => window.IsActive);
            Texts.SetLanguage("en");
            window.Width = 1100; window.Height = 760;
            SearchBox box = Descendant<SearchBox>(editor, "SettingsSearchBox");
            ListBox list = Descendant<ListBox>(editor, "SettingsSearchResults");
            Border panel = Descendant<Border>(editor, "SettingsSearchPanel");
            TextBlock none = Descendant<TextBlock>(editor, "SettingsSearchNone");
            ScrollViewer scroll = editor.FindControl<ScrollViewer>("SettingsScrollViewer")!;
            Expander advanced = editor.FindControl<Expander>("SettingsWorkerBudgetsHeading")!;
            Require(!panel.IsVisible && box.PlaceholderText == Texts.T("settings.search.placeholder"), "Search starts closed and named.");
            Require(!advanced.IsExpanded, "The advanced limits start collapsed.");
            decimal? timer = editor.RecheckTimer;

            await CheckAsync("header and shortcut", async () =>
            {
                // UX-LEDGER-09: the search/actions share the right content container, separate from the sidebar.
                // Check half/normal/full widths and both sides of the category breakpoint with the results open.
                Control title = editor.FindControl<Control>("SettingsTitleBlock")!;
                Control searchHost = editor.FindControl<Control>("SettingsSearchHost")!;
                Control moreButton = editor.FindControl<Control>("SettingsMoreButton")!;
                Control settingsContent = editor.FindControl<Control>("SettingsContent")!;
                Control categories = editor.FindControl<Control>("SettingsCategories")!;
                box.Focus();
                box.Text = "cache";
                await Wait(() => panel.IsVisible && list.ItemCount > 0);
                foreach (double width in new[] { 640d, 1100d, 1600d, 819d, 820d, 1100d })
                {
                    window.Width = width;
                    await Task.Delay(200);
                    window.UpdateLayout();
                    Point searchLeft = searchHost.TranslatePoint(new Point(0, 0), editor)!.Value;
                    Point moreRight = moreButton.TranslatePoint(new Point(moreButton.Bounds.Width, 0), editor)!.Value;
                    Point contentLeft = settingsContent.TranslatePoint(new Point(0, 0), editor)!.Value;
                    Point contentRight = settingsContent.TranslatePoint(new Point(settingsContent.Bounds.Width, 0), editor)!.Value;
                    Point titleBottom = title.TranslatePoint(new Point(0, title.Bounds.Height), editor)!.Value;
                    Require(searchHost.Bounds.Width > 0 && searchHost.Bounds.Left < moreButton.Bounds.Left
                    && Math.Abs(searchLeft.X - contentLeft.X) <= 1.5 && Math.Abs(moreRight.X - contentRight.X) <= 1.5
                    && searchLeft.Y >= titleBottom.Y,
                    $"At width {width}, search/menu edges are not flush over the content column: search {searchLeft.X} / content {contentLeft.X}, menu {moreRight.X} / content {contentRight.X}");
                    Point fieldBottom = box.TranslatePoint(new Point(0, box.Bounds.Height), editor)!.Value;
                    Point panelOrigin = panel.TranslatePoint(default, editor)!.Value;
                    Require(panel.IsVisible && Math.Abs(panelOrigin.X - fieldBottom.X) <= 1.5
                    && Math.Abs(panel.Bounds.Width - box.Bounds.Width) <= 1.5
                    && Math.Abs(panelOrigin.Y - fieldBottom.Y - 4) <= 1.5,
                    $"At width {width}, results no longer follow the search field.");
                    if (categories.IsVisible)
                    {
                        Point sidebarRight = categories.TranslatePoint(new Point(categories.Bounds.Width, 0), editor)!.Value;
                        Require(searchLeft.X > sidebarRight.X, $"At width {width}, search overlaps the category sidebar.");
                    }
                    Program.CaptureWindow(window, $"smoke-settings-header-content-{width}.png");
                }
                box.ClearAndFocus();
                await Wait(() => !panel.IsVisible && list.ItemCount == 0);
                editor.FindControl<Button>("SettingsMoreButton")?.Focus();
                editor.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.OemQuestion, KeySymbol = "/", Source = editor });
                Require(box.IsKeyboardFocusWithin, "'/' did not focus the search.");
                editor.FindControl<Button>("SettingsMoreButton")?.Focus();

            });

            await CheckAsync("typo ranking and navigation", async () =>
            {
                // A typo finds the setting; typing only lists it. Enter jumps: the page opens and the row is marked.
                Control downloadsPage = editor.FindControl<Control>("SettingsDownloadsPage")!;
                bool downloadsVisibleBefore = downloadsPage.IsVisible;
                box.Focus();
                await SearchAsync(box, list, "bandwith");
                await Wait(() => panel.IsVisible && list.ItemCount > 0);
                Console.WriteLine("SMOKE settings search 'bandwith' ranked hits: " + string.Join("; ", list.Items.Cast<SettingsSearchHit>().Take(5)
                .Select(hit => $"{hit.Document.Label} [key={hit.Document.Key}, score={hit.Score}]")));
                Require(HitLabel(list, 0).StartsWith("Bandwidth", StringComparison.Ordinal), "A typo ('bandwith') did not find Bandwidth first: " + HitLabel(list, 0));
                await Task.Delay(300);
                Require(downloadsPage.IsVisible == downloadsVisibleBefore && !Marked(editor, "BandwidthInput"), "Typing jumped to the hit.");
                Require(box.ResultText != null, "The hit count is missing.");
                Press(box, Key.Enter);
                await Wait(() => downloadsPage.IsVisible);
                await Wait(() => Marked(editor, "BandwidthInput"));

            });

            await CheckAsync("groups and abbreviation", async () =>
            {
                // An abbreviation, and hits of several groups with their group names; the arrow keys move between them.
                await SearchAsync(box, list, "cache");
                await Wait(() => list.ItemCount >= 4);
                Require(list.Items.Cast<SettingsSearchHit>().All(hit => !string.IsNullOrWhiteSpace(hit.Document.Group)), "A hit has no group name.");
                CheckRealizedGroups(list);
                int first = list.SelectedIndex;
                Press(box, Key.Down);
                Require(list.SelectedIndex == first + 1, "Down did not move to the next hit.");
                Press(box, Key.Up);
                Require(list.SelectedIndex == first, "Up did not move back.");
                await SearchAsync(box, list, "dwnld");
                await Wait(() => list.ItemCount > 0);
                Require(HitLabel(list, 0).Contains("ownload", StringComparison.OrdinalIgnoreCase), "'dwnld' did not find a download setting: " + HitLabel(list, 0));

            });

            await CheckAsync("collapsed section", async () =>
            {
                // A setting inside a collapsed section: typing leaves it folded; Enter unfolds it, moves the focus into the row and
                // closes the list; clearing the query folds it again.
                await SearchAsync(box, list, "production workers");
                await Wait(() => list.ItemCount > 0);
                await Task.Delay(300);
                Require(!advanced.IsExpanded, "Typing unfolded the collapsed section.");
                Press(box, Key.Enter);
                await Wait(() => editor.FindControl<Control>("SettingsWorkerBudgetsPage")!.IsVisible && advanced.IsExpanded);
                await Wait(() => !panel.IsVisible);
                await Wait(() => editor.FindControl<Control>("ProductionWorkersInput")!.IsKeyboardFocusWithin);
                Require(Marked(editor, "ProductionWorkersInput"), "The chosen row is not marked.");
                box.Focus();
                box.ClearAndFocus();
                await Wait(() => !advanced.IsExpanded);

            });

            await CheckAsync("empty state and Escape", async () =>
            {
                // Words without a match: the empty state names the query.
                box.Focus();
                await SearchAsync(box, list, "zzzzqq");
                await Wait(() => panel.IsVisible && none.IsVisible);
                Require(none.Text!.Contains("zzzzqq", StringComparison.Ordinal) && !list.IsVisible, "The empty state is missing.");
                Press(box, Key.Enter);
                Require(none.IsVisible, "Enter with no hit changed the page.");

                // Escape clears first, the second Escape leaves the field.
                Press(box, Key.Escape);
                await Wait(() => !box.HasText && !panel.IsVisible);
                Require(box.IsKeyboardFocusWithin, "The first Escape left the field.");
                Press(box, Key.Escape);
                await Wait(() => !box.IsKeyboardFocusWithin);

            });

            // German interface: German words, typos, and the English name all find the setting; synonyms work too.
            Texts.SetLanguage("de");
            await Task.Delay(200);
            box.Focus();
            foreach ((string query, string expect, string page) in new[]
            {
                ("bandbrite", "Bandbreite", "SettingsDownloadsPage"), ("bandwidth", "Bandbreite", "SettingsDownloadsPage"),
                ("dark mode", "Farbschema", "SettingsInterfacePage"), ("speicher", "", "SettingsWorkerBudgetsPage"),
                ("stempel", "Stempel", "SettingsReadingMediaPage"), ("kartenmodus", "Kartenmodus", "SettingsReadingMediaPage"),
            })
            {
                await CheckAsync("German query " + query, async () =>
                {
                    box.Focus();
                    await SearchAsync(box, list, query);
                    await Wait(() => list.ItemCount > 0);
                    Press(box, Key.Enter);
                    await Wait(() => editor.FindControl<Control>(page)!.IsVisible);
                    Require(expect.Length == 0 || HitLabel(list, 0).Contains(expect, StringComparison.Ordinal), $"'{query}' found '{HitLabel(list, 0)}', not {expect}.");
                });
            }

            // Captures with the list open: light/dark, English/German.
            foreach (ThemeVariant testedTheme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            foreach (string testedLanguage in new[] { "en", "de" })
            {
                await CheckAsync($"capture {testedLanguage}/{testedTheme}", async () =>
                {
                    Application.Current.RequestedThemeVariant = testedTheme;
                    Texts.SetLanguage(testedLanguage);
                    await Task.Delay(200);
                    box.Focus();
                    await SearchAsync(box, list, testedLanguage == "en" ? "bandwith" : "zwischenspeicher");
                    await Wait(() => panel.IsVisible && list.ItemCount > 0);
                    scroll.Offset = default;
                    window.UpdateLayout();
                    await Task.Delay(150);
                    Program.CaptureWindow(window, $"smoke-settings-search-{testedLanguage}-{testedTheme}.png".ToLowerInvariant());
                    await SearchAsync(box, list, "zzzzqq");
                    await Wait(() => none.IsVisible);
                    window.UpdateLayout();
                    Program.CaptureWindow(window, $"smoke-settings-search-none-{testedLanguage}-{testedTheme}.png".ToLowerInvariant());
                });
            }
            await CheckAsync("no mutations", () =>
            {
                Require(commits == 0 && editor.RecheckTimer == timer, "Searching changed or saved a setting.");
                return Task.CompletedTask;
            });
            if (failures.Count > 0) throw new AggregateException("Settings search failures (all scenarios attempted).", failures);
            Console.WriteLine("SMOKE settings search: typo/abbreviation/synonym hits across groups with group names, typing without a jump, arrow keys, Enter into a collapsed section and folding it again on clear, empty state, Escape twice, EN/DE, light/dark captures passed.");
        }
        finally
        {
            Texts.SetLanguage(language);
            Application.Current.RequestedThemeVariant = theme;
            if (window.IsVisible) window.Close();
            await dialog;
            if (Directory.Exists(profile)) Directory.Delete(profile, recursive: true);
        }
    }

    private static string HitLabel(ListBox list, int index)
    {
        TextBlock? label = Row(list, index)?.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault();
        // Search highlights are Runs, not TextBlock.Text. Read the rendered label, not the section chip or model.
        return label?.Inlines is { Count: > 0 } inlines
        ? string.Concat(inlines.OfType<Run>().Select(run => run.Text))
        : label?.Text ?? string.Empty;
    }

    private static async Task SearchAsync(SearchBox box, ListBox list, string query)
    {
        // Enter posts its focus/scroll work at Loaded priority. Finish that jump before typing the next query.
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        box.Focus();
        bool applied = false;
        void Changed(object? sender, EventArgs args) => applied = true;
        box.QueryChanged += Changed;
        try
        {
            box.Text = query;
            // ItemCount may still describe the previous query during the debounce; containers follow on layout.
            await Wait(() => applied);
            await Wait(() => list.ItemCount == 0 || list.IsEffectivelyVisible
            && HitLabel(list, 0) == ((SettingsSearchHit)list.Items[0]!).Document.Label);
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine($"SMOKE query '{query}': applied={applied}, count={list.ItemCount}, visible={list.IsEffectivelyVisible}, rendered='{HitLabel(list, 0)}'; ranked hits: "
            + string.Join("; ", list.Items.Cast<SettingsSearchHit>().Take(10).Select(hit => $"{hit.Document.Label} [key={hit.Document.Key}, score={hit.Score}]")));
            throw;
        }
        finally { box.QueryChanged -= Changed; }
    }

    private static void CheckRealizedGroups(ListBox list)
    {
        int checkedRows = 0;
        for (int i = 0; i < list.ItemCount; i++)
        {
            // Off-screen hits have no container. Check all documents above, and every realized section chip here.
            if (Row(list, i) is not { } row) continue;
            string group = row.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(b => b.Classes.Contains(SettingsSearchChip.ChipClass))?
            .GetVisualDescendants().OfType<TextBlock>().FirstOrDefault()?.Text ?? string.Empty;
            Require(group.Length > 0 && group == SettingsSearchChip.Split(((SettingsSearchHit)list.Items[i]!).Document.Group).Section,
            "A hit has a missing or incorrect section chip: " + group);
            checkedRows++;
        }
        Require(checkedRows > 0, "No search result rows were realized.");
    }

    private static Control? Row(ListBox list, int index) => list.ContainerFromIndex(index);

    private static bool Marked(SettingsEditor editor, string inputName) =>
    editor.FindControl<Control>(inputName)!.GetSelfAndVisualAncestors().OfType<Control>().Any(c => c.Classes.Contains("settings-search-hit"));

    private static void Press(Control target, Key key)
    {
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, Source = target });
        // A press includes release; otherwise the editor correctly treats the next Enter as a held-key repeat.
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = key, Source = target });
    }

    private static T Descendant<T>(SettingsEditor editor, string name) where T : Control =>
    editor.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name) ?? throw new InvalidOperationException("Missing " + name);

    private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }

    private static async Task Wait(Func<bool> ready, [CallerArgumentExpression(nameof(ready))] string condition = "")
    {
        DateTime until = DateTime.UtcNow.AddSeconds(5);
        while (!ready()) { if (DateTime.UtcNow > until) throw new TimeoutException("Settings search fixture did not settle: " + condition); await Task.Delay(20); }
    }
}
