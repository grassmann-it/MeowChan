using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Theming;
using MeowChan.Desktop.Views.Settings;

namespace MeowChan.DesktopSmoke;

/// <summary>
/// Colour dialog (TH3/TH4) in the real main window: Settings › Appearance › "Customize colors…" opens the dialog on the variant shown,
/// a preset and a typed hex change the draft and the in-dialog preview, a text colour below 4.5 : 1 disables Apply and says why,
/// Apply commits only the colours through the window's settings path and the live resources follow, Cancel with a dirty draft asks first.
/// Captures: smoke-theme-colors-{light,dark}-{en,de}.png (the dialog with an accent override applied). Managed input only.
/// </summary>
internal static class ThemeColorsChecks
{
    private static readonly ThemeAccentPreset Green = ThemeAccentPresets.Find("green")!;

    internal static async Task RunAsync(MainWindow window)
    {
        SettingsEditor editor = window.SettingsEditor;
        DesktopSessionHost host = window.SessionHost;
        Application application = Application.Current ?? throw new InvalidOperationException("The desktop application is unavailable.");
        string language = Texts.Language;
        ThemeVariant variant = application.RequestedThemeVariant ?? ThemeVariant.Light;
        Config original = host.Config.Clone();
        try
        {
            Texts.SetLanguage("en");
            // Light through the theme preference, the way a person chooses it: Apply saves through the window's settings path, which puts the
            // saved theme back on the window, so a variant set only on the application would turn Dark under the check at Apply.
            await WorkViewCaptures.SetThemeAsync(window, "Light");
            Click(window.FindControl<Button>("SettingsNavigationButton")!);
            await Program.WaitUntilAsync(() => editor.IsEffectivelyVisible && editor.Bounds.Width > 0);
            await SettingsSectionChecks.OpenAsync(editor, "settings.interface");
            Button open = editor.FindControl<Button>("ThemeColorsButton")!;
            await Program.WaitUntilAsync(() => Shown(open), () => "the Customize colors button is not shown");
            Require(open.IsEnabled && AutomationName(open) == Texts.T("settings.themeColors.open"), "The button is disabled or has no accessible name.");

            Click(open);
            await Program.WaitUntilAsync(() => editor.ThemeColorsWindow is { IsVisible: true } dialogWindow && dialogWindow.Bounds.Width > 0);
            ThemeColorsDialog dialog = editor.ThemeColorsWindow!;
            await Program.WaitUntilAsync(() => dialog.Rows.All(row => row.Bounds.Width > 0));

            // The start state: the shown variant, eight rows with their contrast written out, six presets, nothing to apply.
            Require(dialog.Variant == ThemeTokenTheme.Light && dialog.VariantToggle.SelectedIndex == 0, "The dialog did not open on the variant shown (Light).");
            Require(dialog.Rows.Count == ThemeUserColors.All.Count && dialog.PresetButtons.Count == 6, "Eight role rows and six presets are expected.");
            Require(!dialog.ApplyButton.IsEnabled && !dialog.ResetAllButton.IsEnabled && !dialog.StatusText.IsVisible, "Apply/Reset all are enabled without a change.");
            Require(dialog.Rows.All(row => row.ContrastText.Text!.Contains(" : 1 – ")), "A role row does not write its contrast out.");
            Require(dialog.Rows.All(row => !string.IsNullOrEmpty(AutomationName(row.Swatch)) && !string.IsNullOrEmpty(AutomationName(row.HexBox))
                && !string.IsNullOrEmpty(AutomationName(row.ResetButton))), "A role row control has no accessible name.");
            Require(dialog.PresetButtons.All(button => !string.IsNullOrEmpty(AutomationName(button))), "A preset has no accessible name.");
            Require(!string.IsNullOrEmpty(AutomationName(dialog.ApplyButton)) && !string.IsNullOrEmpty(AutomationName(dialog.CancelButton)), "A footer button has no accessible name.");

            // A preset changes the draft and the preview card; Apply becomes available.
            Color builtInAccent = dialog.Model.BuiltInAccent(ThemeTokenTheme.Light);
            Click(dialog.PresetButtons[3]);
            Color green = Green.For(ThemeTokenTheme.Light)!.Value;
            Require(dialog.Model.Role(ThemeTokenTheme.Light, ThemeUserColors.Accent).Color == green, "The green preset did not set the Light accent in the draft.");
            Require(dialog.Model.Role(ThemeTokenTheme.Dark, ThemeUserColors.Accent).Color == Green.For(ThemeTokenTheme.Dark), "The green preset did not set the Dark accent in the draft.");
            Require(AccentOfPreview(dialog) == green && green != builtInAccent, "The preview card did not take the preset accent.");
            Require(dialog.PresetButtons[3].IsChecked == true && dialog.ApplyButton.IsEnabled && dialog.ResetAllButton.IsEnabled, "The preset button or Apply/Reset all did not follow the draft.");
            Require(SmokeThemeResources.Color(window, "Workspace.Accent") == builtInAccent, "A draft reached the live window before Apply.");

            // TH5: the help button has its note, and "Try in window" shows the unsaved draft on the whole window and takes it back
            // when switched off; it never saves anything.
            Require(dialog.HelpButton.IsEffectivelyVisible && !string.IsNullOrEmpty(AutomationName(dialog.HelpButton)) && dialog.HelpButton.Resolve() is { Bullets.Count: 3 },
                "The help button of the colour dialog is missing or has no note.");
            Require(dialog.TryCheckBox.IsEffectivelyVisible && dialog.TryCheckBox.IsChecked != true && !string.IsNullOrEmpty(AutomationName(dialog.TryCheckBox)),
                "Try in window is missing, on from the start or unnamed.");
            dialog.TryCheckBox.IsChecked = true;
            await Program.WaitUntilAsync(() => SmokeThemeResources.Color(window, "Workspace.Accent") == green, () => "Try in window did not put the draft on the window");
            Require(host.Config.ThemeOverrides.Equals(original.ThemeOverrides), "Try in window saved the draft.");
            dialog.TryCheckBox.IsChecked = false;
            await Program.WaitUntilAsync(() => SmokeThemeResources.Color(window, "Workspace.Accent") == builtInAccent, () => "Switching Try in window off did not restore the saved colours");

            // A typed hex sets the accent and the preview follows; the swatch name stays; a non-colour is taken back.
            ThemeColorRow accent = dialog.Rows[0];
            accent.HexBox.Text = "#074E85";
            await Program.WaitUntilAsync(() => AccentOfPreview(dialog) == ThemeCustomization.ParseHex("#074E85"), () => "preview accent " + AccentOfPreview(dialog));
            Require(accent.ContrastText.Text!.Contains(" : 1 – "), "The accent row lost its contrast text.");
            accent.HexBox.Text = "#12";
            await Program.WaitUntilAsync(() => accent.HasPendingInvalidText, () => "no error for a hex that is no colour");
            Require(AccentOfPreview(dialog) == ThemeCustomization.ParseHex("#074E85"), "A non-colour changed the preview.");
            accent.RestoreHex();
            Require(accent.HexBox.Text == "#074E85" && !accent.HasPendingInvalidText, "The invalid entry was not taken back.");

            // Text as light as its panel is unreadable: Apply is refused with a reason in words; resetting the role lifts it.
            ThemeColorRow text = dialog.Rows.Single(row => row.Role.Id == ThemeUserColors.Text);
            Color panel = dialog.Model.Preview(ThemeTokenTheme.Light).Palette.Colors["Workspace.Panel"];
            text.HexBox.Text = ThemeColorMath.Format(panel);
            await Program.WaitUntilAsync(() => !dialog.ApplyButton.IsEnabled && dialog.StatusText.IsVisible, () => "Apply was not refused for unreadable text");
            Require(dialog.StatusText.Text!.Contains(Texts.T("theme.light")) && dialog.StatusText.Text.Contains(Texts.T("theme.color.text")), "The refusal does not name the variant and the role: " + dialog.StatusText.Text);
            Require(text.ContrastText.Text!.Contains("too weak for text"), "The text row did not say that the contrast is too weak: " + text.ContrastText.Text);
            Click(text.ResetButton);
            await Program.WaitUntilAsync(() => dialog.ApplyButton.IsEnabled && !dialog.StatusText.IsVisible, () => "Apply stayed refused after the role was reset");

            // Back to the green preset, then Apply: only the colours are committed; the live resources follow without a restart.
            Click(dialog.PresetButtons[3]);
            Require(dialog.Model.Role(ThemeTokenTheme.Light, ThemeUserColors.Accent).Color == green, "The preset click did not replace the typed accent.");
            Task applying = dialog.ApplyNowAsync();
            await applying;
            await Program.WaitUntilAsync(() => editor.ThemeColorsWindow == null || !editor.ThemeColorsWindow.IsVisible, () => "the dialog did not close after Apply");
            Require(!host.Config.ThemeOverrides.IsEmpty && host.Config.ThemeOverrides.Equals(Config.LoadConfig().ThemeOverrides), "The colours were not saved to the profile.");
            Require(SmokeThemeResources.Color(window, "Workspace.Accent") == green,
                $"The live Light accent did not follow Apply: live {SmokeThemeResources.Color(window, "Workspace.Accent")}, expected {green}, window {window.ActualThemeVariant}.");
            Require(host.Config.RecheckTimer == original.RecheckTimer, "Apply changed a setting that is not a colour.");

            // Reopen on the saved state and take the four captures with the override applied. The closed dialog is released once its
            // ShowDialog returns, and the button is usable again once the settings are no longer busy: a click comes after both, as a person's would.
            await Program.WaitUntilAsync(() => editor.ThemeColorsWindow == null && open.IsEffectivelyEnabled,
                () => $"the button cannot reopen the dialog: dialog {(editor.ThemeColorsWindow == null ? "released" : "still held")}, button enabled {open.IsEffectivelyEnabled}");
            Click(open);
            await Program.WaitUntilAsync(() => editor.ThemeColorsWindow is { IsVisible: true });
            dialog = editor.ThemeColorsWindow!;
            await Program.WaitUntilAsync(() => dialog.Rows.All(row => row.Bounds.Width > 0));
            Require(!dialog.ApplyButton.IsEnabled && dialog.PresetButtons[3].IsChecked == true && dialog.Rows[0].StateText.Text == Texts.T("theme.colors.state.custom"),
                "The reopened dialog does not show the saved override as changed and nothing to apply.");
            foreach (string lang in new[] { "en", "de" })
                foreach (ThemeTokenTheme theme in new[] { ThemeTokenTheme.Light, ThemeTokenTheme.Dark })
                {
                    Texts.SetLanguage(lang);
                    application.RequestedThemeVariant = theme == ThemeTokenTheme.Light ? ThemeVariant.Light : ThemeVariant.Dark;
                    dialog.ShowVariant(theme);
                    await Program.WaitUntilAsync(() => dialog.Variant == theme && dialog.ActualThemeVariant == application.RequestedThemeVariant,
                        () => "the dialog did not switch to " + theme);
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    Require(dialog.Title == Texts.T("theme.colors.title"), "The dialog title did not follow the language.");
                    Require(dialog.Rows.All(row => row.ContrastText.Text!.Contains(" : 1 – ")), "A contrast text is missing in " + lang + " " + theme);
                    Require(dialog.Preview.Palette!.Colors["Workspace.Accent"] == Green.For(theme), "The preview does not show the applied accent in " + theme);
                    Program.CaptureWindow(dialog, $"smoke-theme-colors-{(theme == ThemeTokenTheme.Light ? "light" : "dark")}-{lang}.png");
                }

            // Cancel with a dirty draft asks first: Continue editing keeps the dialog and the draft, Discard changes closes it and the
            // saved override stays.
            Texts.SetLanguage("en");
            application.RequestedThemeVariant = ThemeVariant.Light;
            dialog.ShowVariant(ThemeTokenTheme.Light);
            Click(dialog.Rows[0].ResetButton);
            Require(dialog.Model.IsDirty && dialog.ApplyButton.IsEnabled, "Resetting the accent did not make the draft dirty.");
            dialog.TryCheckBox.IsChecked = true; // TH5: the trial shows the reset accent now; discarding must put the saved green back
            await Program.WaitUntilAsync(() => SmokeThemeResources.Color(window, "Workspace.Accent") == dialog.Model.BuiltInAccent(ThemeTokenTheme.Light),
                () => "Try in window did not show the reset accent");
            Click(dialog.CancelButton);
            await Program.WaitUntilAsync(() => dialog.IsAsking && dialog.OwnedWindows.Count == 1, () => "Cancel on a dirty draft did not ask");
            Window question = dialog.OwnedWindows.Single();
            Click(Answer(question, "dialog.keepEditing"));
            await Program.WaitUntilAsync(() => !dialog.IsAsking && dialog.OwnedWindows.Count == 0, () => "Continue editing did not close the question");
            Require(dialog.IsVisible && dialog.Model.IsDirty, "Continue editing closed the dialog or dropped the draft.");
            Click(dialog.CancelButton);
            await Program.WaitUntilAsync(() => dialog.IsAsking && dialog.OwnedWindows.Count == 1, () => "the second Cancel did not ask");
            Click(Answer(dialog.OwnedWindows.Single(), "dialog.discardChanges"));
            await Program.WaitUntilAsync(() => !dialog.IsVisible && editor.ThemeColorsWindow == null, () => "Discard changes did not close the dialog");
            Require(!host.Config.ThemeOverrides.IsEmpty && SmokeThemeResources.Color(window, "Workspace.Accent") == green, "Discarding changed the saved colours.");
            Console.WriteLine("SMOKE theme colors: dialog, presets, hex, gate, apply, live resources and four captures passed.");
        }
        finally
        {
            try
            {
                if (editor.ThemeColorsWindow is { } leftover)
                {
                    leftover.Model.ResetAll();
                    if (leftover.IsVisible) leftover.Close();
                }
                Config restored = host.Config.Clone();
                restored.ThemeOverrides = original.ThemeOverrides;
                restored.Theme = original.Theme;
                await host.ApplySettingsAsync(restored);
                ThemeRuntime.ApplyOverrides(original.ThemeOverrides);
            }
            finally
            {
                application.RequestedThemeVariant = variant;
                Texts.SetLanguage(language);
            }
        }
    }

    private static Button Answer(Window question, string textKey) =>
        question.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, Texts.T(textKey)));

    private static Color AccentOfPreview(ThemeColorsDialog dialog) => dialog.Preview.Palette!.Colors["Workspace.Accent"];

    private static string AutomationName(Control control) => Avalonia.Automation.AutomationProperties.GetName(control) ?? string.Empty;

    private static bool Shown(Control control) => control.IsAttachedToVisualTree() && control.IsEffectivelyVisible;

    private static void Click(Control control) => SmokeActionClick.Execute(control);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
