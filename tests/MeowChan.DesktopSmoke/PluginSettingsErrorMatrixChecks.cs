using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;

namespace MeowChan.DesktopSmoke;

// Opt-in presentation evidence for an already compiled fixture and its real rejected worker values.
// Does not synthesize field errors, assign focus, rebuild plugins or bypass the semantic smoke gates.
internal static class PluginSettingsErrorMatrixChecks
{
    internal static async Task CaptureAsync(MainWindow window, RulesPage page, string scene, string key,
        string raw, string expectedError, Func<PluginSettingsEditor> editor, Func<Task> revalidate,
        Func<bool> unchanged)
    {
        if (Environment.GetEnvironmentVariable("MEOWCHAN_FORM_ERROR_MATRIX") != "1") return;
        string modeBefore = window.SessionHost.Config.WorkspaceMode;
        string languageBefore = Texts.Language;
        ThemeVariant? themeBefore = Application.Current!.RequestedThemeVariant;
        Size sizeBefore = window.Bounds.Size;
        WindowState stateBefore = window.WindowState;
        double scale = window.RenderScaling;
        try
        {
            foreach (string mode in new[] { "Barebones", "Simple", "Advanced" })
            {
                await PresentationChecks.SwitchModeAsync(window, mode);
                foreach (string language in new[] { "en", "de" })
                foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                {
                    // Mode saves restore persisted appearance. Apply the presentation probe afterward.
                    // Avoid Settings navigation: it would intentionally close the retained template draft.
                    Texts.SetLanguage(language);
                    Application.Current!.RequestedThemeVariant = theme;
                    window.WindowState = WindowState.Normal;
                    double width = mode == "Barebones" ? 640 : 760;
                    double height = mode == "Barebones" ? 480 : 500;
                    window.Width = width; window.Height = height;
                    await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(window, width, height));
                    window.FindControl<MenuItem>("RulesMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    await SettleAsync();
                    TextBox field = (TextBox)editor().FindField(key)!;
                    TextBlock? error = ErrorLabel();
                    string stem = "smoke-form-error-" + scene + "-" + mode.ToLowerInvariant() + "-" + language + "-" + theme.Key.ToString()!.ToLowerInvariant();
                    try
                    {
                        Require(field.Text == raw && AutomationProperties.GetHelpText(field)?.Contains(expectedError, StringComparison.Ordinal) == true && unchanged(),
                            "Presentation change lost the retained rejected raw value/error or wrote the fixture store.");
                        await revalidate(); // Existing real Save -> same compiled artifact -> real worker rejection.
                        await Program.WaitUntilAsync(() =>
                        {
                            window.UpdateLayout();
                            field = (TextBox)editor().FindField(key)!; error = ErrorLabel();
                            return !page.IsBusy && Focused(window, field) && FullyVisible(window, field)
                                && error != null && FullyVisible(window, error);
                        });
                        Config persisted = Config.LoadConfig();
                        string? actualMode = (window.FindControl<ComboBox>("WorkspaceModeComboBox")!.SelectedItem as ComboBoxItem)?.Tag as string;
                        Require(actualMode == mode && persisted.WorkspaceMode == mode && window.SessionHost.Config.WorkspaceMode == mode,
                            "Form matrix mode metadata differs from the actual UI/session/persisted mode.");
                        Require(Texts.Language == language && window.ActualThemeVariant.Equals(theme) && field.ActualThemeVariant.Equals(theme),
                            "Form matrix language/theme differs from the actual rendered controls.");
                        Require(page.GetVisualDescendants().OfType<TextBlock>().Any(label => label.IsEffectivelyVisible && label.Text == Texts.T("nav.rules")),
                            "Form matrix lacks the actual localized Rules page label.");
                        Require(field.Text == raw && error?.Text == expectedError && AutomationProperties.GetHelpText(field)?.Contains(expectedError, StringComparison.Ordinal) == true,
                            "Form matrix lacks the real rejected field, visible error and accessible error help.");
                        string schemaLabel = editor().Draft.Schema.Fields.Single(definition => definition.Key == key).Label;
                        Require(editor().GetVisualDescendants().OfType<TextBlock>().Any(label => label.Text == schemaLabel && label.IsEffectivelyVisible),
                            "Form matrix lacks the actual schema label for the rejected field.");
                        Require(Math.Abs(window.RenderScaling - scale) < 0.001 && unchanged(),
                            "Form matrix changed scale or persisted a rejected parameter.");
                        Capture("pass");
                    }
                    catch
                    {
                        Capture("failure");
                        throw;
                    }
                    TextBlock? ErrorLabel() => editor().GetVisualDescendants().OfType<TextBlock>()
                        .FirstOrDefault(label => label.Classes.Contains("error") && label.Text == expectedError);
                    void Capture(string result)
                    {
                        string name = stem + (result == "pass" ? "" : "-failure");
                        string json = JsonSerializer.Serialize(new
                        {
                            scene, result, requestedMode = mode, requestedLanguage = language, requestedTheme = theme.Key.ToString(),
                            actualMode = (window.FindControl<ComboBox>("WorkspaceModeComboBox")!.SelectedItem as ComboBoxItem)?.Tag,
                            actualLanguage = Texts.Language, actualTheme = window.ActualThemeVariant.ToString(),
                            actualFieldTheme = field.ActualThemeVariant.ToString(), localizedLabel = page.Title,
                            clientDip = window.ClientSize.ToString(), boundsDip = window.Bounds.ToString(), actualScale = window.RenderScaling,
                            fieldIdentity = AutomationProperties.GetAutomationId(field), fieldText = field.Text,
                            schemaLabel = editor().Draft.Schema.Fields.Single(definition => definition.Key == key).Label,
                            fieldHelp = AutomationProperties.GetHelpText(field), errorText = error?.Text,
                            fieldBounds = field.Bounds.ToString(), errorBounds = error?.Bounds.ToString(),
                            actualFieldFocus = Focused(window, field), visibleField = FullyVisible(window, field),
                            visibleError = error != null && FullyVisible(window, error), unchanged = unchanged(),
                            scope = "real worker rejection; explicit Save each cell; retained draft; rendered labels/theme/minima; no plugin recompilation"
                        });
                        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, name + ".json"), json);
                        Console.WriteLine("FORM ERROR MATRIX " + json);
                        Program.CaptureWindow(window, name + ".png");
                    }
                }
            }
        }
        finally
        {
            await PresentationChecks.SwitchModeAsync(window, modeBefore);
            Texts.SetLanguage(languageBefore); Application.Current!.RequestedThemeVariant = themeBefore;
            window.Width = sizeBefore.Width; window.Height = sizeBefore.Height; window.WindowState = stateBefore;
            await SettleAsync();
        }
    }

    private static async Task SettleAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
    private static bool Focused(Window window, Control field) => field.IsFocused
        && ReferenceEquals(window.FocusManager?.GetFocusedElement(), field);
    private static bool FullyVisible(Window window, Control control)
    {
        if (!control.IsAttachedToVisualTree() || !control.IsEffectivelyVisible || !control.IsEffectivelyEnabled
            || control.Bounds.Width <= 0 || control.Bounds.Height <= 0) return false;
        Point? windowPoint = control.TranslatePoint(default, window);
        if (windowPoint == null || windowPoint.Value.X < -1 || windowPoint.Value.Y < -1
            || windowPoint.Value.X + control.Bounds.Width > window.ClientSize.Width + 1
            || windowPoint.Value.Y + control.Bounds.Height > window.ClientSize.Height + 1) return false;
        foreach (ScrollViewer scroll in control.GetVisualAncestors().OfType<ScrollViewer>())
        {
            Point? location = control.TranslatePoint(default, scroll);
            if (location == null || location.Value.X < -1 || location.Value.Y < -1
                || location.Value.X + control.Bounds.Width > scroll.Bounds.Width + 1
                || location.Value.Y + control.Bounds.Height > scroll.Bounds.Height + 1) return false;
        }
        return true;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
