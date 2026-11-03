using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

internal static class PreviewLanguagePackChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        var session = window.SessionHost;
        string profile = session.ProfileDirectory;
        CultureInfo processCulture = CultureInfo.CurrentCulture;
        Require(!Directory.Exists(Path.Combine(profile, "LanguagePacks")), "Bundled language smoke must not install a profile pack.");
        Require(Texts.Languages.Where(l => l.Code is "es" or "fr" or "ru").All(l => l.NativeName.EndsWith("(Untested)", StringComparison.Ordinal)), "Bundled preview marker missing.");
        byte[] initialSettings = File.ReadAllBytes(Path.Combine(profile, "settings.json"));
        Require(Texts.Warnings.Count == 0 && Texts.Languages.Count == 5, "Bundled preview packs did not load through normal initialization.");
        TextBox urls = window.FindControl<TextBox>("UrlInput")!;
        const string draft = "https://unsupported.invalid/été/mañana\npreview draft — not submitted";
        urls.Text = draft;
        foreach (string code in new[] { "es", "fr", "ru" })
        {
            // A passive catalog switch is not a settings commit.
            Texts.SetLanguage(code);
            Require(Texts.Language == code && Texts.Warnings.Count == 0,
                code + ": bundled preview language was not effective without loader warnings.");
            await Program.WaitUntilAsync(() => AutomationProperties.GetName(window.FindControl<Button>("AddUrlButton")!) == Texts.T("action.add"));
            Require(initialSettings.SequenceEqual(File.ReadAllBytes(Path.Combine(profile, "settings.json"))), "Passive switch wrote preferences.");
        }
        Texts.SetLanguage("en");
        foreach (string code in new[] { "es", "fr", "ru" })
        {
            window.FindControl<MenuItem>("SettingsMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Program.WaitUntilAsync(() => window.SettingsPageVisible);
            SettingsEditor editor = window.SettingsEditor;
            await SettingsSectionChecks.OpenFieldAsync(editor, "LanguageInput");
            ComboBox languages = editor.FindControl<ComboBox>("LanguageInput")!;
            languages.SelectedItem = editor.LanguageChoices.Single(l => l.Code == code);
            editor.FindControl<Button>("SaveSettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => !editor.IsBusy && Config.LoadConfig(profile).Language == code && Texts.Language == code);
            Require(ReferenceEquals(session, window.SessionHost) && urls.Text == draft, "Language commit replaced session or URL draft.");
            Require(CultureInfo.CurrentCulture.Equals(processCulture), "UI locale changed process numeric culture.");
            Require(editor.SettingsTitle == Texts.T("settings.title"), "Settings heading did not refresh.");
            await SettingsSectionChecks.OpenFieldAsync(editor, "DownloadPathInput");
            TextBox path = editor.FindControl<TextBox>("DownloadPathInput")!;
            NumericUpDown timer = editor.FindControl<NumericUpDown>("RecheckTimerInput")!;
            timer.ApplyTemplate();
            TextBox numeric = timer.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "PART_TextBox");
            Config saved = Config.LoadConfig(profile);
            byte[] savedSettings = File.ReadAllBytes(Path.Combine(profile, "settings.json"));
            decimal savedValue = saved.RecheckTimer;
            Require(timer.Value == savedValue && path.Text == saved.DownloadPath, "Committed values not in the retained editor.");
            path.Text = "relative unfinished été mañana";
            numeric.Focus();
            const string invalid = "invalid numeric draft";
            numeric.Text = invalid;
            await Program.WaitUntilAsync(() => numeric.IsFocused && numeric.Text == invalid && timer.Text == invalid);
            Require(timer.Value == savedValue, "Invalid numeric fixture unexpectedly changed the numeric value.");
            Texts.SetLanguage(code == "fr" ? "es" : "fr");
            await Program.WaitUntilAsync(() => editor.SettingsTitle == Texts.T("settings.title"));
            Require(numeric.IsFocused && numeric.Text == invalid && timer.Text == invalid
                && timer.Value == savedValue && path.Text == "relative unfinished été mañana",
                "Passive preview switch replaced invalid input, value or focus.");
            Require(Config.LoadConfig(profile).Language == code, "Passive preview switch committed a language.");
            Texts.SetLanguage(code);
            // Discard draft through actual editor command, without saving its invalid values.
            editor.FindControl<Button>("CancelSettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => !editor.IsBusy && editor.SelectedLanguage?.Code == code
                && path.Text == saved.DownloadPath && editor.DownloadPath == saved.DownloadPath
                && timer.Value == savedValue && editor.RecheckTimer == saved.RecheckTimer
                && numeric.Text != invalid && timer.Text != invalid);
            Require(decimal.TryParse(timer.Text, timer.ParsingNumberStyle, timer.NumberFormat, out decimal restored)
                && restored == savedValue && numeric.Text == timer.Text, "Discard did not restore the raw numeric value.");
            Require(savedSettings.SequenceEqual(File.ReadAllBytes(Path.Combine(profile, "settings.json"))),
                "Discard or passive preview changes wrote settings.");
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                window.RequestedThemeVariant = theme;
                await Program.WaitUntilAsync(() => window.ActualThemeVariant == theme);
                await SettingsSectionChecks.OpenFieldAsync(editor, "LanguageInput");
                window.UpdateLayout();
                Require(languages.Bounds.Width > 100 && languages.IsEffectivelyVisible, "Preview language choice not laid out.");
                Program.CaptureWindow(window, $"smoke-language-{code}-{theme.ToString().ToLowerInvariant()}-settings.png");
            }
            window.FindControl<Button>("DownloadsNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => !window.SettingsPageVisible);
            Require(AutomationProperties.GetName(window.FindControl<Button>("WindowCloseButton")!) == Texts.T("action.close"), "Caption not translated.");
            Require(urls.Text == draft && ReferenceEquals(session, window.SessionHost), "Preview roundtrip lost session or draft.");
            Program.CaptureWindow(window, $"smoke-language-{code}-downloads.png");
            Console.WriteLine($"SMOKE preview language {code}: embedded bundled pack load without profile copy, Settings commit/readback, live labels, retained URL/invalid numeric draft and focus; CurrentCulture={processCulture.Name}.");
        }
        // Bundled catalog rediscovery emulates next startup only, not a process restart.
        Texts.Initialize(Path.Combine(profile, "LanguagePacks"));
        Texts.SetLanguage(Config.LoadConfig(profile).Language);
        Require(Texts.Language == "ru" && Texts.Warnings.Count == 0, "Saved preview preference cannot be loaded again.");
        Console.WriteLine("SMOKE preview limits: managed/routed input on this Linux desktop; screenshots for accents/layout; no native linguistic, whole-UI, Windows/macOS, process restart or CJK acceptance.");
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
