using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Threading;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Ursa.Controls;
using MeowChan.Rules;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Commands;
using Semi.Avalonia;
using Ursa.Themes.Semi;

namespace MeowChan.DesktopSmoke;

/// <summary>Real product templates, live locale/theme changes and compact render evidence.</summary>
internal static class ThemeMigrationChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        Application application = Application.Current!;
        SemiTheme semi = application.Styles.OfType<SemiTheme>().Single();
        UrsaSemiTheme ursa = application.Styles.OfType<UrsaSemiTheme>().Single();
        if (AppDomain.CurrentDomain.GetAssemblies().Any(assembly =>
            assembly.GetName().Name?.StartsWith("Actipro", StringComparison.Ordinal) == true))
            throw new InvalidOperationException("The desktop loaded a removed vendor assembly.");

        string originalLanguage = Texts.Language;
        try
        {
            foreach (string language in new[] { "en", "de" })
            {
                Texts.SetLanguage(language);
                CultureInfo expected = CultureInfo.GetCultureInfo(language == "de" ? "de-DE" : "en-US");
                await Program.WaitUntilAsync(() => Equals(semi.Locale, expected) && Equals(ursa.Locale, expected));
                foreach (string theme in new[] { "Dark", "Light" })
                {
                    ThemeVariant variant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
                    if (application.RequestedThemeVariant != variant)
                        window.FindControl<Button>("ThemeToggleButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Program.WaitUntilAsync(() => window.ActualThemeVariant == variant);
                    foreach (string mode in new[] { "Barebones", "Simple", "Advanced" })
                    {
                        await PresentationChecks.SwitchModeAsync(window, mode);
                        // Mode changes reapply the saved configuration, including its
                        // language. Set the fixture language after that real operation.
                        Texts.SetLanguage(language);
                        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                        await Program.WaitUntilAsync(() => Texts.Language == language && Equals(semi.Locale, expected)
                            && Equals(ursa.Locale, expected)
                            && AutomationProperties.GetName(window.FindControl<Button>("SettingsNavigationButton")!) == CommandCatalog.Label("navigation.settings"));
                        double width = mode == "Barebones" ? 640 : 760;
                        double height = mode == "Barebones" ? 480 : 500;
                        window.WindowState = WindowState.Normal;
                        window.Width = width;
                        window.Height = height;
                        await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(window, width, height));
                        Button add = window.FindControl<Button>("AddUrlButton")!;
                        add.Focus(NavigationMethod.Tab);
                        await Program.WaitUntilAsync(() => add.IsFocused && add.Bounds.Width > 0);
                        Console.WriteLine($"SMOKE Semi/Ursa minimum: language={language}; theme={theme}; mode={mode}; client={window.ClientSize}; scale={window.RenderScaling}; locale={semi.Locale}; keyboard focus={add.IsFocused}");
                        Program.CaptureWindow(window, $"smoke-theme-minimum-{language}-{theme.ToLowerInvariant()}-{mode.ToLowerInvariant()}.png");
                    }
                    window.FindControl<MenuItem>("RulesMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    RulesPage rules = window.RulesPage;
                    await Program.WaitUntilAsync(() => rules.IsEditable && rules.IsEffectivelyVisible);
                    rules.RuleEnabled = true;
                    rules.SizeMode = (int)MediaSizeLimitMode.Limited;
                    rules.MegabytesText = "invalid";
                    // A field error stays at its field (edge + text); the banner below the preview is for errors without a field.
                    TextBlock megabytesError = rules.FindControl<TextBlock>("RuleMegabytesErrorText")!;
                    await Program.WaitUntilAsync(() => rules.HasValidationError && rules.CanSave
                        && megabytesError.IsEffectivelyVisible && megabytesError.Bounds.Height > 0
                        && !rules.GetVisualDescendants().OfType<Banner>().Any(control => control.IsVisible));
                    megabytesError.BringIntoView();
                    window.UpdateLayout();
                    Program.CaptureWindow(window, $"smoke-theme-validation-{language}-{theme.ToLowerInvariant()}.png");
                    // Same minimum size, invalid extension: the field, its edge and its text must be in the picture.
                    rules.MegabytesText = "10";
                    rules.TypesMode = (int)MediaTypeSelectionMode.Selected;
                    RuleExtensionsInput extensionsField = rules.FindControl<RuleExtensionsInput>("RuleExtensionsInput")!;
                    TextBox? extensionsEditor = null;
                    await Program.WaitUntilAsync(() =>
                    {
                        window.UpdateLayout();
                        return extensionsField.IsEffectivelyVisible && extensionsField.Bounds.Height > 0
                            && (extensionsEditor = extensionsField.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(value => value.IsEffectivelyVisible && value.Bounds.Width > 0)) != null;
                    });
                    extensionsEditor!.Focus(NavigationMethod.Tab);
                    await Program.WaitUntilAsync(() => extensionsEditor.IsFocused);
                    extensionsEditor.Text = "image/jpeg"; // typed through the real editor, as a user would
                    await Program.WaitUntilAsync(() => rules.HasValidationError && rules.ExtensionsError.Length > 0);
                    // Focus elsewhere first: the picture must show the field's own error edge, not its focus ring.
                    rules.FindControl<CheckBox>("RuleEnabledInput")!.Focus(NavigationMethod.Tab);
                    extensionsField.BringIntoView();
                    window.UpdateLayout();
                    Program.CaptureWindow(window, $"smoke-rules-field-error-{language}-{theme.ToLowerInvariant()}.png");
                    // Save stays enabled: the click refuses, focuses the field and names it in the footer.
                    string revision = window.SessionHost.MediaRules.Revision;
                    rules.FindControl<Button>("SaveRuleButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Program.WaitUntilAsync(() => extensionsField.GetVisualDescendants().OfType<Control>().Any(value => value.IsFocused)
                        && rules.StatusMessage == Texts.T("rules.saveRejected", rules.ExtensionsLabel));
                    if (window.SessionHost.MediaRules.Revision != revision) throw new InvalidOperationException("A refused Rules save wrote durable rules.");
                    window.UpdateLayout();
                    Program.CaptureWindow(window, $"smoke-rules-save-rejected-{language}-{theme.ToLowerInvariant()}.png");
                    rules.DiscardCurrentDraft();
                    window.FindControl<Button>("DownloadsNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
            }
        }
        finally
        {
            Texts.SetLanguage(originalLanguage);
        }
    }
}
