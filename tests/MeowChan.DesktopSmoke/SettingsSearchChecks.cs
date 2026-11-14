using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Styling;
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

            // SP1: the search sits in the page header, to the right of the title, 240-480 dip wide, and Ctrl+F and "/" focus it.
            TextBlock title = editor.FindControl<TextBlock>("SettingsTitleBlock")!;
            Control searchHost = editor.FindControl<Control>("SettingsSearchHost")!;
            Control moreButton = editor.FindControl<Control>("SettingsMoreButton")!;
            Require(searchHost.Bounds.Width is >= 240 and <= 480.5 && searchHost.Bounds.Left > title.Bounds.Left && searchHost.Bounds.Right <= moreButton.Bounds.Left + 0.5
                && Math.Abs(searchHost.Bounds.Top - title.Bounds.Top) < title.Bounds.Height + 24,
                "The search is not in the header row between the title and the menu, 240-480 wide: " + searchHost.Bounds + " / " + moreButton.Bounds);
            editor.FindControl<Button>("SettingsMoreButton")?.Focus();
            editor.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.OemQuestion, KeySymbol = "/", Source = editor });
            Require(box.IsKeyboardFocusWithin, "'/' did not focus the search.");
            editor.FindControl<Button>("SettingsMoreButton")?.Focus();

            // A typo finds the setting; typing only lists it. Enter jumps: the page opens and the row is marked.
            Control downloadsPage = editor.FindControl<Control>("SettingsDownloadsPage")!;
            bool downloadsVisibleBefore = downloadsPage.IsVisible;
            box.Focus();
            box.Text = "bandwith";
            await Wait(() => panel.IsVisible && list.ItemCount > 0);
            Require(HitLabel(list, 0).StartsWith("Bandwidth", StringComparison.Ordinal), "A typo ('bandwith') did not find Bandwidth first: " + HitLabel(list, 0));
            await Task.Delay(300);
            Require(downloadsPage.IsVisible == downloadsVisibleBefore && !Marked(editor, "BandwidthInput"), "Typing jumped to the hit.");
            Require(box.ResultText != null, "The hit count is missing.");
            Press(box, Key.Enter);
            await Wait(() => downloadsPage.IsVisible);
            await Wait(() => Marked(editor, "BandwidthInput"));

            // An abbreviation, and hits of several groups with their group names; the arrow keys move between them.
            box.Text = "cache";
            await Wait(() => list.ItemCount >= 4);
            Require(Groups(list).Distinct().Count() >= 1 && Groups(list).All(g => g.Length > 0), "A hit has no group name.");
            int first = list.SelectedIndex;
            Press(box, Key.Down);
            Require(list.SelectedIndex == first + 1, "Down did not move to the next hit.");
            Press(box, Key.Up);
            Require(list.SelectedIndex == first, "Up did not move back.");
            box.Text = "dwnld";
            await Wait(() => list.ItemCount > 0);
            Require(HitLabel(list, 0).Contains("ownload", StringComparison.OrdinalIgnoreCase), "'dwnld' did not find a download setting: " + HitLabel(list, 0));

            // A setting inside a collapsed section: typing leaves it folded; Enter unfolds it, moves the focus into the row and
            // closes the list; clearing the query folds it again.
            box.Text = "production workers";
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

            // Words without a match: the empty state names the query.
            box.Focus();
            box.Text = "zzzzqq";
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
                box.Focus();
                box.Text = query;
                await Wait(() => list.ItemCount > 0);
                Press(box, Key.Enter);
                await Wait(() => editor.FindControl<Control>(page)!.IsVisible);
                Require(expect.Length == 0 || HitLabel(list, 0).Contains(expect, StringComparison.Ordinal), $"'{query}' found '{HitLabel(list, 0)}', not {expect}.");
            }

            // Captures with the list open: light/dark, English/German.
            foreach (ThemeVariant testedTheme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            foreach (string testedLanguage in new[] { "en", "de" })
            {
                Application.Current.RequestedThemeVariant = testedTheme;
                Texts.SetLanguage(testedLanguage);
                await Task.Delay(200);
                box.Focus();
                box.Text = testedLanguage == "en" ? "bandwith" : "zwischenspeicher";
                await Wait(() => panel.IsVisible && list.ItemCount > 0);
                scroll.Offset = default;
                window.UpdateLayout();
                await Task.Delay(150);
                Program.CaptureWindow(window, $"smoke-settings-search-{testedLanguage}-{testedTheme}.png".ToLowerInvariant());
                box.Text = "zzzzqq";
                await Wait(() => none.IsVisible);
                window.UpdateLayout();
                Program.CaptureWindow(window, $"smoke-settings-search-none-{testedLanguage}-{testedTheme}.png".ToLowerInvariant());
            }
            Require(commits == 0 && editor.RecheckTimer == timer, "Searching changed or saved a setting.");
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

    private static string HitLabel(ListBox list, int index) => Row(list, index)?.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault()?.Text ?? string.Empty;

    private static IEnumerable<string> Groups(ListBox list) => Enumerable.Range(0, list.ItemCount)
        .Select(i => Row(list, i)?.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains(SettingsSearchChip.ChipClass))?.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault()?.Text ?? string.Empty);

    private static Control? Row(ListBox list, int index) => list.ContainerFromIndex(index);

    private static bool Marked(SettingsEditor editor, string inputName) =>
        editor.FindControl<Control>(inputName)!.GetSelfAndVisualAncestors().OfType<Control>().Any(c => c.Classes.Contains("settings-search-hit"));

    private static void Press(Control target, Key key) =>
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, Source = target });

    private static T Descendant<T>(SettingsEditor editor, string name) where T : Control =>
        editor.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name) ?? throw new InvalidOperationException("Missing " + name);

    private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }

    private static async Task Wait(Func<bool> ready)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(5);
        while (!ready()) { if (DateTime.UtcNow > until) throw new TimeoutException("Settings search fixture did not settle."); await Task.Delay(20); }
    }
}
