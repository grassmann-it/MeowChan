using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Persistence;
using MeowChan.Desktop.Localization;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

internal static class SettingsCategoryChecks
{
    private static readonly (string Id, string Name)[] Pages = {
        ("interface", "SettingsInterfacePage"), ("reading-media", "SettingsReadingMediaPage"),
        ("downloads", "SettingsDownloadsPage"), ("worker-budgets", "SettingsWorkerBudgetsPage"),
        ("updates", "SettingsUpdatesPage"), ("notifications", "SettingsNotificationsSection"),
        ("behavior", "SettingsBehaviorPage"), ("workviews", "SettingsWorkViewsPage"), ("shortcuts", "ShortcutsEditor") };

    internal static async Task RunAsync(MainWindow owner)
    {
        string profile = Path.Combine(TemporaryWorkspacePaths.TempRoot, "settings-categories-" + Guid.NewGuid().ToString("N"));
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
            // Showing an owned dialog does not guarantee activation on the smoke
            // compositor. Drive focus only after this disposable fixture owns it.
            window.Activate();
            await Wait(() => window.IsActive);
            var timer = Find<NumericUpDown>(editor, "RecheckTimerInput");
            var checkbox = Find<ToggleSwitch>(editor, "MinimizeToTrayInput");
            var shortcuts = Find<ShortcutEditor>(editor, "ShortcutsEditor");
            decimal? initialTimer = editor.RecheckTimer;
            bool initialTray = config.MinimizeToTray;
            string initialBindings = config.ShortcutBindings.Raw.GetRawText();
            await Select(editor, "downloads");
            timer.ApplyTemplate();
            TextBox input = timer.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "PART_TextBox");
            input.Focus(); input.Text = "invalid category draft";
            await Wait(() => timer.Text == "invalid category draft");
            decimal? draftValue = timer.Value;
            await Select(editor, "behavior");
            checkbox.IsChecked = !initialTray;
            await Select(editor, "shortcuts");
            shortcuts.Load(shortcuts.Draft.Set("navigation.archive", new[] { ShortcutGesture.Parse("Primary+Shift+K") }));
            string draftBindings = shortcuts.Draft.Raw.GetRawText();
            foreach (var page in Pages)
            {
                await Select(editor, page.Id);
                Require(timer.Text == "invalid category draft" && timer.Value == draftValue
                    && checkbox.IsChecked == !initialTray && shortcuts.Draft.Raw.GetRawText() == draftBindings,
                    "Switching category changed an editor draft: " + page.Id);
                Require(config.RecheckTimer == initialTimer && config.MinimizeToTray == initialTray
                    && config.ShortcutBindings.Raw.GetRawText() == initialBindings && commits == 0,
                    "Category navigation mutated config or saved.");
            }
            await Select(editor, "interface");
            window.Activate();
            await Wait(() => window.IsActive);
            Click(Find<Button>(editor, "SaveSettingsButton"));
            await Wait(() => PagesVisible(editor, "downloads") && input.IsFocused);
            Require(timer.Text == "invalid category draft" && commits == 0, "Invalid save lost raw draft or committed.");
            editor.CancelChanges();
            Require(timer.Value == initialTimer && checkbox.IsChecked == initialTray
                && shortcuts.Draft.Raw.GetRawText() == initialBindings && commits == 0,
                "Discard did not restore drafts across all categories.");

            // Exercise the separate shortcut-validation focus path from another page.
            shortcuts.Load(shortcuts.Draft.Set("navigation.archive", new[] { ShortcutGesture.Parse("Escape") }));
            Require(EffectiveShortcutBindings.Build(shortcuts.Draft).Issues.Any(i => i.BlocksSave), "Shortcut error fixture is not blocking.");
            await Select(editor, "interface");
            window.Activate();
            await Wait(() => window.IsActive);
            Click(Find<Button>(editor, "SaveSettingsButton"));
            await Wait(() => PagesVisible(editor, "shortcuts") && shortcuts.IsKeyboardFocusWithin);
            Require(commits == 0, "Invalid hidden shortcut draft was saved.");
            editor.CancelChanges();

            foreach (ThemeVariant testedTheme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            foreach (string testedLanguage in new[] { "en", "de" })
            foreach (double width in new[] { 640d, 850d, 1440d })
            {
                Application.Current.RequestedThemeVariant = testedTheme;
                Texts.SetLanguage(testedLanguage);
                window.Width = width; window.Height = 760;
                await Wait(() => Math.Abs(window.Bounds.Width - width) < 1);
                foreach (var page in Pages)
                {
                    await Select(editor, page.Id);
                    foreach (string name in new[] { "SettingsFormLayout", "SettingsHeader", "SettingsFooter", "SaveSettingsButton", "CancelSettingsButton" })
                        AssertBounded(Find<Control>(editor, name), editor, name);
                    Grid layout = Find<Grid>(editor, "SettingsFormLayout");
                    Point? p = layout.TranslatePoint(default, editor);
                    Require(p.HasValue && Math.Abs(p.Value.X + layout.Bounds.Width / 2 - editor.Bounds.Width / 2) <= 2,
                        "Navigation and content block is not centered.");
                    if (page.Id == "interface") Program.CaptureWindow(window, $"smoke-settings-category-{testedLanguage}-{testedTheme}-{width}.png");
                    if (page.Id == "worker-budgets" && width == 1440d)
                    {
                        // Evidence for the storage and limits page: its top, middle and end, since it is a long page.
                        ScrollViewer scroll = Find<ScrollViewer>(editor, "SettingsScrollViewer");
                        foreach (int percent in new[] { 0, 50, 100 })
                        {
                            scroll.Offset = new Vector(0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height) * percent / 100);
                            window.UpdateLayout();
                            Program.CaptureWindow(window, $"smoke-settings-storage-limits-{testedLanguage}-{testedTheme}-{percent}.png");
                        }
                        scroll.Offset = default;
                    }
                }
            }
            Require(commits == 0, "Layout/language/theme changes saved settings.");
            // Evidence for the 75 percent status-bar warning: a measurement near the limit, shown in the main window's status bar.
            System.Reflection.FieldInfo usage = typeof(MainWindow).GetField("_storeUsage", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            System.Reflection.MethodInfo show = typeof(MainWindow).GetMethod("ShowStoreLimitWarning", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            try
            {
                foreach (ThemeVariant warnTheme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                foreach (string warnLanguage in new[] { "de", "en" })
                {
                    Application.Current.RequestedThemeVariant = warnTheme;
                    Texts.SetLanguage(warnLanguage);
                    usage.SetValue(owner, new[] { new StoreLimitUsage.Item(PrivateStoreLimitKind.FavoriteEntries, 8_000, 10_000, StoreLimitUsage.StoreList.Ratings) });
                    show.Invoke(owner, null);
                    await Program.WaitUntilAsync(() => owner.FindControl<TextBlock>("WorkspaceStoreWarning")!.IsEffectivelyVisible);
                    owner.UpdateLayout();
                    Require(owner.FindControl<TextBlock>("WorkspaceStoreWarning")!.Text!.Contains(Texts.T("limit.warn.list.ratings"), StringComparison.Ordinal),
                        "The status-bar warning does not name the ratings list.");
                    Program.CaptureWindow(owner, $"smoke-store-limit-warning-{warnLanguage}-{warnTheme}.png");
                }
            }
            finally
            {
                usage.SetValue(owner, Array.Empty<StoreLimitUsage.Item>());
                show.Invoke(owner, null);
            }
            Console.WriteLine("SMOKE settings categories: eight exclusive pages, centered bounds EN/DE Dark/Light 640/850/1440, raw/checkbox/shortcut draft preservation, hidden numeric/shortcut error focus and global discard passed.");
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

    private static async Task Select(SettingsEditor editor, string suffix)
    {
        editor.OpenSectionNavigation();
        string id = "settings." + suffix;
        Require(editor.CanGoToSection(id), "Section command unavailable: " + id);
        editor.GoToSection(id);
        string pageName = Pages.Single(p => p.Id == suffix).Name;
        await Wait(() => PagesVisible(editor, suffix) && Find<Control>(editor, pageName).IsKeyboardFocusWithin);
        Require(Pages.Count(p => Find<Control>(editor, p.Name).IsVisible) == 1, "More than one category page visible.");
        var links = Find<StackPanel>(editor, "SettingsCategoryLinks").Children.OfType<ToggleButton>().ToArray();
        string selection = string.Join(", ", links.Select(b => $"{b.Tag}:{b.IsChecked}"));
        ToggleButton selected = links.SingleOrDefault(b => b.IsChecked == true)!;
        string? selectedTag = selected?.Tag as string;
        Require(links.Length == Pages.Length && selected != null && string.Equals(selectedTag, id, StringComparison.Ordinal),
            $"Accessible category selection disagrees with page: expected={id}; actual={selection}; selectedTag={selectedTag ?? "<null>"}");
    }
    private static bool PagesVisible(SettingsEditor editor, string suffix) => Pages.All(p => Find<Control>(editor, p.Name).IsVisible == (p.Id == suffix));
    private static void AssertBounded(Control control, Control parent, string label)
    {
        Point? p = control.TranslatePoint(default, parent);
        Require(control.IsEffectivelyVisible && control.Bounds.Width > 0 && control.Bounds.Height > 0 && p.HasValue
            && p.Value.X >= -1 && p.Value.Y >= -1 && p.Value.X + control.Bounds.Width <= parent.Bounds.Width + 1
            && p.Value.Y + control.Bounds.Height <= parent.Bounds.Height + 1, "Settings chrome clipped: " + label);
    }
    private static T Find<T>(SettingsEditor editor, string name) where T : Control => editor.FindControl<T>(name) ?? throw new InvalidOperationException("Missing " + name);
    private static void Click(Button button) { button.Focus(); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
    private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static async Task Wait(Func<bool> ready)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(5);
        while (!ready()) { if (DateTime.UtcNow > until) throw new TimeoutException("Settings category fixture did not settle."); await Task.Delay(20); }
    }
}
