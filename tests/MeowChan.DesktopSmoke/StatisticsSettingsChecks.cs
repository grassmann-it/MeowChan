using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Stats;

namespace MeowChan.DesktopSmoke;

/// <summary>
/// The "Statistics" block of the Behavior page in the real settings page of the main window: all nine rows with their label,
/// hint and range, the two switch dependencies (unavailable row, reason in the tooltip and the help text), refusal of values
/// outside the ranges, an unavailable row never blocking the save, save and reload through the production apply path
/// (host, settings file, hub options), discard, language and theme refresh, and screenshots in German and English, light and dark.
/// </summary>
internal static class StatisticsSettingsChecks
{
    // Editor property (same name as the Config property), label key, dependency switch or null.
    private static readonly (string Name, string Key, string? Switch)[] Numbers =
    {
        ("StatsSampleIntervalMs", "sampleInterval", null),
        ("StatsHistorySamples", "historySamples", null),
        ("StatsBackgroundIntervalMs", "backgroundInterval", "StatsTrayTooltip"),
        ("StatsRetentionDays", "retentionDays", "StatsPersist"),
        ("StatsPersistIntervalMinutes", "persistInterval", "StatsPersist"),
        ("StatsMaxSources", "maxSources", null),
    };

    private static readonly (string Name, string Key)[] Switches =
    {
        ("StatsTrayTooltip", "trayTooltip"), ("StatsTrayMenu", "trayMenu"), ("StatsPersist", "persist"),
    };

    private static int Constant(string name) =>
        (int)typeof(Config).GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetRawConstantValue()!;

    private static int Get(Config config, string name) => (int)typeof(Config).GetProperty(name)!.GetValue(config)!;

    private static void SetDraft(SettingsEditor editor, string name, decimal? value) =>
        typeof(SettingsEditor).GetProperty(name)!.SetValue(editor, value);

    private static decimal? GetDraft(SettingsEditor editor, string name) => (decimal?)typeof(SettingsEditor).GetProperty(name)!.GetValue(editor);

    internal static async Task RunAsync(MainWindow window)
    {
        SettingsEditor editor = window.SettingsEditor;
        DesktopSessionHost host = window.SessionHost;
        string language = Texts.Language;
        ThemeVariant? theme = Application.Current!.RequestedThemeVariant;
        try
        {
            Texts.SetLanguage("en");
            Click(window.FindControl<Button>("SettingsNavigationButton")!);
            await Program.WaitUntilAsync(() => editor.IsEffectivelyVisible && editor.Bounds.Width > 0);
            await SettingsSectionChecks.OpenAsync(editor, "settings.behavior");
            string file = Path.Combine(Config.GetConfigDirectory(), "settings.json");

            CheckRows(editor);
            CheckDefaults(host.Config, editor);
            await CheckDependenciesAsync(editor);
            await CheckRefusalAsync(window, editor, host, file);
            await CheckUnavailableRowDoesNotBlockAsync(window, editor, host);
            await CheckSaveAndReloadAsync(window, editor, host);
            CheckDiscard(editor, host);
            await CheckLanguageThemeAndScreenshotsAsync(window, editor);
            Console.WriteLine("SMOKE settings statistics: nine rows with label, hint and range on the Behavior page, switch dependencies with reasons, "
                + "refusal outside the ranges, save/reload/discard through the production apply path, EN/DE light/dark captures passed.");
        }
        finally
        {
            Texts.SetLanguage(language);
            Application.Current.RequestedThemeVariant = theme;
            if (!editor.IsBusy) editor.CancelChanges();
        }
    }

    private static void CheckRows(SettingsEditor editor)
    {
        foreach ((string name, string key, string? dependsOn) in Numbers)
        {
            NumericUpDown input = Find<NumericUpDown>(editor, name + "Input");
            Require(input.Minimum == Constant(name + "Minimum") && input.Maximum == Constant(name + "Maximum"),
                name + " row range differs from the setting's range.");
            TextBlock label = Find<TextBlock>(editor, name + "FieldLabel");
            Require(label.Text == Texts.T("stats.settings." + key), name + " label is not its text.");
            string hint = Texts.T("stats.settings." + key + "Hint", Constant(name + "Minimum"), Constant(name + "Maximum"));
            Require(Hints(editor).Any(text => text == hint) && hint.Contains(Constant(name + "Minimum").ToString())
                && hint.Contains(Constant(name + "Maximum").ToString()), name + " hint with its range is not shown.");
            Require(AutomationProperties.GetHelpText(input)!.Contains(hint), name + " help text lacks the hint.");
            Require(AutomationProperties.GetName(input) == label.Text, name + " automation name is not its label.");
            Require(!Find<TextBlock>(editor, name + "InputError").IsVisible, name + " shows an error at start.");
            Require(input.IsEnabled && (dependsOn == null || ToolTip.GetShowOnDisabled(input)), name + " is not enabled at start.");
        }
        foreach ((string name, string key) in Switches)
        {
            CheckBox box = Find<CheckBox>(editor, name + "Input");
            Require(box.IsChecked == true, name + " is not on by default.");
            Require(AutomationProperties.GetName(box) == Texts.T("stats.settings." + key), name + " automation name is not its label.");
            Require(Hints(editor).Any(text => text == Texts.T("stats.settings." + key + "Hint")), name + " hint is not shown.");
        }
        Require(Find<TextBlock>(editor, "SettingsStatisticsHeading").Text == Texts.T("stats.settings.title"), "The block heading is missing.");
    }

    private static IEnumerable<string?> Hints(SettingsEditor editor) =>
        Find<StackPanel>(editor, "SettingsStatisticsSection").GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text);

    private static void CheckDefaults(Config config, SettingsEditor editor)
    {
        foreach ((string name, _, _) in Numbers)
            Require(GetDraft(editor, name) == Get(config, name) && Find<NumericUpDown>(editor, name + "Input").Text == Get(config, name).ToString(),
                name + " draft does not show the saved value.");
        Require(Get(new Config(), "StatsSampleIntervalMs") == 1000 && Get(new Config(), "StatsHistorySamples") == 120
            && Get(new Config(), "StatsRetentionDays") == 30, "The settings defaults changed.");
    }

    private static async Task CheckDependenciesAsync(SettingsEditor editor)
    {
        // The dependent rows are available while their switch is on, and then carry no tooltip.
        foreach ((string name, _, string? dependsOn) in Numbers.Where(n => n.Switch != null))
        {
            NumericUpDown input = Find<NumericUpDown>(editor, name + "Input");
            Require(input.IsEnabled && Find<TextBlock>(editor, name + "FieldLabel").IsEnabled && ToolTip.GetTip(input) == null,
                name + " is unavailable or has a tooltip while " + dependsOn + " is on.");
        }
        editor.StatsPersist = false;
        await Program.WaitUntilAsync(() => !Find<NumericUpDown>(editor, "StatsRetentionDaysInput").IsEnabled);
        string reason = Texts.T("stats.settings.needsOption", Texts.T("stats.settings.persist"));
        foreach (string name in new[] { "StatsRetentionDays", "StatsPersistIntervalMinutes" })
        {
            NumericUpDown input = Find<NumericUpDown>(editor, name + "Input");
            Require(!input.IsEnabled && !Find<TextBlock>(editor, name + "FieldLabel").IsEnabled, name + " stays available while saving is off.");
            Require(ToolTip.GetTip(input) as string == reason && ToolTip.GetShowOnDisabled(input), name + " does not say why it is unavailable.");
            Require(AutomationProperties.GetHelpText(input)!.EndsWith(reason), name + " help text does not give the reason.");
        }
        Require(Find<NumericUpDown>(editor, "StatsBackgroundIntervalMsInput").IsEnabled && Find<NumericUpDown>(editor, "StatsSampleIntervalMsInput").IsEnabled,
            "Switching saving off made an unrelated row unavailable.");
        editor.StatsPersist = true;
        await Program.WaitUntilAsync(() => Find<NumericUpDown>(editor, "StatsRetentionDaysInput").IsEnabled);
        Require(ToolTip.GetTip(Find<NumericUpDown>(editor, "StatsRetentionDaysInput")) == null, "The reason stays after saving is on again.");

        editor.StatsTrayTooltip = false;
        await Program.WaitUntilAsync(() => !Find<NumericUpDown>(editor, "StatsBackgroundIntervalMsInput").IsEnabled);
        NumericUpDown background = Find<NumericUpDown>(editor, "StatsBackgroundIntervalMsInput");
        Require(ToolTip.GetTip(background) as string == Texts.T("stats.settings.needsOption", Texts.T("stats.settings.trayTooltip")),
            "The tray interval does not say that the tooltip switch is off.");
        Require(Find<NumericUpDown>(editor, "StatsRetentionDaysInput").IsEnabled, "Switching the tooltip off made the retention row unavailable.");
        // The tray menu switch has no dependent row.
        editor.StatsTrayMenu = false;
        editor.StatsTrayMenu = true;
        editor.StatsTrayTooltip = true;
        await Program.WaitUntilAsync(() => background.IsEnabled && ToolTip.GetTip(background) == null);
    }

    private static async Task CheckRefusalAsync(MainWindow window, SettingsEditor editor, DesktopSessionHost host, string file)
    {
        string original = File.ReadAllText(file);
        IoStatsOptions options = host.IoStats.Options;
        foreach ((string name, _, _) in Numbers)
        foreach (int invalid in new[] { Constant(name + "Minimum") - 1, Constant(name + "Maximum") + 1 })
        {
            NumericUpDown input = Find<NumericUpDown>(editor, name + "Input");
            input.Text = invalid.ToString();
            Click(Find<Button>(editor, "SaveSettingsButton"));
            TextBlock error = Find<TextBlock>(editor, name + "InputError");
            await Program.WaitUntilAsync(() => !editor.IsBusy && editor.HasError && error.IsEffectivelyVisible,
                () => name + "=" + invalid + " busy=" + editor.IsBusy + " hasError=" + editor.HasError);
            Require(error.Text!.Contains(Constant(name + "Minimum").ToString()) && error.Text.Contains(Constant(name + "Maximum").ToString()),
                name + "=" + invalid + ": the error does not give the allowed range: " + error.Text);
            Require(input.Text == invalid.ToString(), name + ": the rejected text was replaced.");
            Require(File.ReadAllText(file) == original && ReferenceEquals(options, host.IoStats.Options) && Get(host.Config, name) == Get(new Config(), name),
                name + "=" + invalid + " was saved or applied.");
            editor.CancelChanges();
            Require(!editor.HasError && input.Text == Get(host.Config, name).ToString(), name + ": discard did not restore the row.");
        }
    }

    private static async Task CheckUnavailableRowDoesNotBlockAsync(MainWindow window, SettingsEditor editor, DesktopSessionHost host)
    {
        editor.StatsPersist = false;
        await Program.WaitUntilAsync(() => !Find<NumericUpDown>(editor, "StatsRetentionDaysInput").IsEnabled);
        Find<NumericUpDown>(editor, "StatsRetentionDaysInput").Text = "not a number";
        Find<NumericUpDown>(editor, "StatsPersistIntervalMinutesInput").Text = "not a number";
        Click(Find<Button>(editor, "SaveSettingsButton"));
        await Program.WaitUntilAsync(() => !editor.IsBusy && !host.Config.StatsPersist);
        Require(!editor.HasError && !Find<TextBlock>(editor, "StatsRetentionDaysInputError").IsVisible,
            "A text nobody can correct in an unavailable row blocked the save.");
        Require(host.Config.StatsRetentionDays == Constant("StatsRetentionDaysDefault")
            && host.Config.StatsPersistIntervalMinutes == Constant("StatsPersistIntervalMinutesDefault"),
            "An unavailable row's unusable text replaced its saved value.");
        Require(Find<NumericUpDown>(editor, "StatsRetentionDaysInput").Text == Constant("StatsRetentionDaysDefault").ToString(),
            "The saved value was not shown again after the save.");
        Require(!host.IoStats.Options.Persist && Config.LoadConfig().StatsPersist == false, "Switching saving off did not reach the hub and the file.");
        editor.StatsPersist = true;
        Click(Find<Button>(editor, "SaveSettingsButton"));
        await Program.WaitUntilAsync(() => !editor.IsBusy && host.Config.StatsPersist);
        Require(host.IoStats.Options.Persist, "Switching saving on did not reach the hub.");
    }

    private static async Task CheckSaveAndReloadAsync(MainWindow window, SettingsEditor editor, DesktopSessionHost host)
    {
        // Smallest, largest and ordinary values, each saved through the production apply path and read back.
        (string Label, Dictionary<string, int> Values, bool Tooltip, bool Menu, bool Persist)[] rounds =
        {
            ("minimum", Numbers.ToDictionary(n => n.Name, n => Constant(n.Name + "Minimum")), false, false, false),
            ("maximum", Numbers.ToDictionary(n => n.Name, n => Constant(n.Name + "Maximum")), true, true, true),
            ("ordinary", new Dictionary<string, int>
            {
                ["StatsSampleIntervalMs"] = 2500, ["StatsHistorySamples"] = 300, ["StatsBackgroundIntervalMs"] = 15_000,
                ["StatsRetentionDays"] = 90, ["StatsPersistIntervalMinutes"] = 10, ["StatsMaxSources"] = 128
            }, true, false, true),
        };
        foreach ((string label, Dictionary<string, int> values, bool tooltip, bool menu, bool persist) in rounds)
        {
            foreach (var pair in values) SetDraft(editor, pair.Key, pair.Value);
            editor.StatsTrayTooltip = tooltip;
            editor.StatsTrayMenu = menu;
            editor.StatsPersist = persist;
            Click(Find<Button>(editor, "SaveSettingsButton"));
            await Program.WaitUntilAsync(() => !editor.IsBusy && editor.IsAvailable && Get(host.Config, "StatsMaxSources") == values["StatsMaxSources"]
                && host.Config.StatsTrayMenu == menu, () => label + " busy=" + editor.IsBusy + " error=" + editor.ErrorMessage);
            Require(!editor.HasError, label + ": the save reported an error: " + editor.ErrorMessage);
            Config reloaded = Config.LoadConfig();
            foreach (var pair in values)
                Require(Get(host.Config, pair.Key) == pair.Value && Get(reloaded, pair.Key) == pair.Value
                    && Find<NumericUpDown>(editor, pair.Key + "Input").Text == pair.Value.ToString(), label + ": " + pair.Key + " was not kept.");
            Require((reloaded.StatsTrayTooltip, reloaded.StatsTrayMenu, reloaded.StatsPersist) == (tooltip, menu, persist)
                && (host.Config.StatsTrayTooltip, host.Config.StatsTrayMenu, host.Config.StatsPersist) == (tooltip, menu, persist),
                label + ": a switch was not kept.");
            // The running hub follows the saved settings (a hub check value, not a screen).
            IoStatsOptions options = host.IoStats.Options;
            Require(options.SampleIntervalMs == values["StatsSampleIntervalMs"] && options.HistorySamples == values["StatsHistorySamples"]
                && options.BackgroundIntervalMs == values["StatsBackgroundIntervalMs"] && options.RetentionDays == values["StatsRetentionDays"]
                && options.PersistIntervalMinutes == values["StatsPersistIntervalMinutes"] && options.MaxSources == values["StatsMaxSources"]
                && (options.TrayTooltip, options.TrayMenu, options.Persist) == (tooltip, menu, persist),
                label + ": the hub did not take over the saved settings.");
            Require(options == IoStatsOptions.FromConfig(host.Config), label + ": the hub options differ from the saved configuration.");
        }
    }

    private static void CheckDiscard(SettingsEditor editor, DesktopSessionHost host)
    {
        editor.StatsMaxSources = 200;
        editor.StatsHistorySamples = 99;
        editor.StatsTrayMenu = !host.Config.StatsTrayMenu;
        editor.StatsPersist = !host.Config.StatsPersist;
        editor.CancelChanges();
        foreach ((string name, _, _) in Numbers)
            Require(GetDraft(editor, name) == Get(host.Config, name) && Find<NumericUpDown>(editor, name + "Input").Text == Get(host.Config, name).ToString(),
                name + ": discard did not restore the saved value.");
        Require(editor.StatsTrayMenu == host.Config.StatsTrayMenu && editor.StatsPersist == host.Config.StatsPersist
            && editor.StatsTrayTooltip == host.Config.StatsTrayTooltip, "Discard did not restore a switch.");
    }

    private static async Task CheckLanguageThemeAndScreenshotsAsync(MainWindow window, SettingsEditor editor)
    {
        ScrollViewer scroll = Find<ScrollViewer>(editor, "SettingsScrollViewer");
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        foreach (string language in new[] { "en", "de" })
        foreach (bool unavailable in new[] { false, true })
        {
            Application.Current!.RequestedThemeVariant = theme;
            Texts.SetLanguage(language);
            editor.CancelChanges();
            if (unavailable)
            {
                editor.StatsPersist = false;
                editor.StatsTrayTooltip = false;
            }
            window.UpdateLayout();
            await SettingsSectionChecks.OpenAsync(editor, "settings.behavior");
            string reason = Texts.T("stats.settings.needsOption", Texts.T("stats.settings.persist"));
            foreach ((string name, string key, _) in Numbers)
            {
                Require(Find<TextBlock>(editor, name + "FieldLabel").Text == Texts.T("stats.settings." + key), name + " label was not refreshed to " + language + ".");
                string hint = Texts.T("stats.settings." + key + "Hint", Constant(name + "Minimum"), Constant(name + "Maximum"));
                Require(Hints(editor).Contains(hint), name + " hint was not refreshed to " + language + ".");
            }
            Require(!unavailable || ToolTip.GetTip(Find<NumericUpDown>(editor, "StatsRetentionDaysInput")) as string == reason,
                "The reason was not refreshed to " + language + ".");
            Control heading = Find<Control>(editor, "SettingsStatisticsHeading");
            for (int part = 0; part < 2; part++)
            {
                double top = heading.TranslatePoint(default, (Visual)scroll.Content!)?.Y ?? 0;
                scroll.Offset = part == 0 ? new Vector(0, Math.Max(0, top - 8)) : new Vector(0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
                window.UpdateLayout();
                await Task.Delay(30);
                foreach ((string name, _, _) in Numbers)
                {
                    NumericUpDown input = Find<NumericUpDown>(editor, name + "Input");
                    Point? at = input.TranslatePoint(default, scroll);
                    // Rows inside the viewport must not be clipped on either side.
                    if (at is { Y: >= 0 } p && p.Y + input.Bounds.Height <= scroll.Bounds.Height)
                        Require(p.X >= 0 && p.X + input.Bounds.Width <= scroll.Bounds.Width + 1 && input.Bounds.Width > 40, name + " input is clipped.");
                }
                Program.CaptureWindow(window, $"smoke-settings-statistics-{(unavailable ? "unavailable-" : string.Empty)}{language}-{theme.Key.ToString()!.ToLowerInvariant()}-{(part == 0 ? "top" : "end")}.png");
            }
            editor.CancelChanges();
        }
        scroll.Offset = default;
    }

    private static T Find<T>(SettingsEditor editor, string name) where T : Control => editor.FindControl<T>(name) ?? throw new InvalidOperationException("Missing " + name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
