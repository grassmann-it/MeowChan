using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

internal static class SettingsAccessibilityChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        string initialLanguage = Texts.Language;
        ThemeVariant? initialTheme = window.RequestedThemeVariant;
        bool settingsWasVisible = window.SettingsPageVisible;
        SettingsEditor editor = window.SettingsEditor;
        window.FindControl<Button>("SettingsNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Program.WaitUntilAsync(() => editor.IsEffectivelyVisible);
        try
        {
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                window.RequestedThemeVariant = theme;
                foreach (string language in new[] { "en", "de" })
                {
                    Texts.SetLanguage(language);
                    window.UpdateLayout();
                    foreach ((string field, string labelKey, string? unitKey) in new[]
                    {
                        ("VideoVolumeInput", "r08d.settings.videoVolume", "settings.unitPercent"),
                        ("RecheckTimerInput", "settings.recheckSeconds", "settings.unitSeconds"),
                        ("BandwidthInput", "settings.bandwidth", "settings.unitBandwidth"),
                        ("ParallelDownloadsInput", "settings.parallelDownloads", (string?)null)
                    })
                    {
                        NumericUpDown input = editor.FindControl<NumericUpDown>(field)!;
                        TextBox text = input.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "PART_TextBox");
                        text.BringIntoView();
                        Require(text.Focus(NavigationMethod.Tab) && ReferenceEquals(window.FocusManager?.GetFocusedElement(), text),
                            field + " did not focus its editable text box.");
                        AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(text)!;
                        string label = Texts.T(labelKey);
                        Require(peer.GetName() == label && peer.GetLabeledBy()?.GetName() == label,
                            $"{language}/{theme}: focused {field} has no localized field name or label relation.");
                        Require(ReferenceEquals(AutomationProperties.GetLabeledBy(text), AutomationProperties.GetLabeledBy(input)),
                            field + " is not related to the visible field label.");
                        if (unitKey != null)
                            Require(peer.GetHelpText() == Texts.T(unitKey),
                                $"{language}/{theme}: focused {field} has no localized unit help.");
                        await CheckStepActionsAsync(input, label, language + "/" + theme);
                    }
                }
            }
        }
        finally
        {
            window.RequestedThemeVariant = initialTheme;
            Texts.SetLanguage(initialLanguage);
            if (!settingsWasVisible)
                window.FindControl<Button>("DownloadsNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        Console.WriteLine("SMOKE Settings V04: focused numeric text boxes expose localized labels, label relations and units; named increase/decrease actions change and restore values in EN/DE and Light/Dark (native speech checked separately).");
    }

    private static async Task CheckStepActionsAsync(NumericUpDown input, string label, string context)
    {
        decimal original = input.Value!.Value;
        try
        {
            foreach ((string part, string key, decimal expected) in new[]
            {
                ("PART_IncreaseButton", "settings.increaseValue", original + input.Increment),
                ("PART_DecreaseButton", "settings.decreaseValue", original)
            })
            {
                Button button = input.GetVisualDescendants().OfType<Button>().Single(control => control.Name == part);
                AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(button)!;
                Require(peer.GetName() == Texts.T(key, label),
                    $"{context}: {input.Name}/{part} has no localized action and field name.");
                Require(button.IsEffectivelyEnabled && peer is IInvokeProvider,
                    $"{context}: {input.Name}/{part} is not an accessible action.");
                ((IInvokeProvider)peer).Invoke();
                await Program.WaitUntilAsync(() => input.Value == expected);
            }
        }
        finally { input.Value = original; }
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
