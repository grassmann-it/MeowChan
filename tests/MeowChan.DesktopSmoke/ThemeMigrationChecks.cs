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
                    Banner? banner = null;
                    await Program.WaitUntilAsync(() =>
                    {
                        banner = rules.GetVisualDescendants().OfType<Banner>().FirstOrDefault(control => control.IsVisible);
                        return rules.HasValidationError && !rules.CanSave && banner?.Bounds.Height > 0;
                    });
                    banner!.BringIntoView();
                    window.UpdateLayout();
                    Program.CaptureWindow(window, $"smoke-theme-validation-{language}-{theme.ToLowerInvariant()}.png");
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
