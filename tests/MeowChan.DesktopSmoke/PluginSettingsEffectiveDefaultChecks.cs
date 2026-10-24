using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginSdk;

namespace MeowChan.DesktopSmoke;

internal static class PluginSettingsEffectiveDefaultChecks
{
    internal static async Task RunAsync(MainWindow window, PluginsPage page, PluginProfileStore profile)
    {
        const string rejection = "Fixture rejects effective default two";
        PluginSettingsEditor Editor() => page.GetVisualDescendants().OfType<PluginSettingsEditor>().Single(editor => editor.Draft.Scope == SettingScope.Plugin);
        TextBox Field() => (TextBox)Editor().FindField("number")!;
        CheckBox Toggle() => Field().GetLogicalAncestors().OfType<StackPanel>().First().Children.OfType<CheckBox>().Single();
        async Task SaveAsync()
        {
            page.FindControl<Button>("PluginSave")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => !page.IsBusy);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
        await Dispatcher.UIThread.InvokeAsync(() => { window.UpdateLayout(); }, DispatcherPriority.Background);
        long initialRevision = (await profile.ReadAsync()).Revision;
        // Prove this actual compiled worker rejects the effective default when explicitly present.
        Field().Text = "3"; Field().Text = "2";
        await SaveAsync();
        bool explicitRejected = (await profile.ReadAsync()).Revision == initialRevision
            && AutomationProperties.GetHelpText(Field())?.Contains(rejection, StringComparison.Ordinal) == true;
        Program.CaptureWindow(window, "smoke-form-effective-default-explicit.png");
        Toggle().IsChecked = false;
        await SaveAsync();
        PluginProfileSnapshot afterUnset = await profile.ReadAsync();
        bool unsetRejected = afterUnset.Revision == initialRevision;
        bool focusedToggle = ReferenceEquals(window.FocusManager?.GetFocusedElement(), Toggle());
        bool localError = AutomationProperties.GetHelpText(Field())?.Contains(rejection, StringComparison.Ordinal) == true;
        long? persistedPending = afterUnset.Plugins.Single().Pending?.Configuration.PluginSettings.Values.SingleOrDefault(value => value.Key == "number")?.Integer;
        string json = JsonSerializer.Serialize(new
        {
            explicitRejected, unsetRejected, focusedToggle, localError, initialRevision, afterRevision = afterUnset.Revision,
            persistedPending, raw = Field().Text, actualSet = Editor().Draft.IsSet("number"),
            identity = AutomationProperties.GetAutomationId(Field()), scale = window.RenderScaling,
            claim = "same real worker rejects explicit default2; unchecked default2 must not bypass it before Host reinserts2"
        });
        Console.WriteLine("FORM EFFECTIVE DEFAULT " + json);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "form-effective-default.json"), json);
        Program.CaptureWindow(window, "smoke-form-effective-default-unset.png");
        // Keep a concrete correction/success oracle, including after the failed baseline guard.
        Toggle().IsChecked = true; Field().Text = "3";
        long beforeCorrection = afterUnset.Revision;
        await SaveAsync();
        PluginProfileSnapshot corrected = await profile.ReadAsync();
        bool correctedOnce = corrected.Revision == beforeCorrection + 1
            && corrected.Plugins.Single().Pending?.Configuration.PluginSettings.Values.Single(value => value.Key == "number").Integer == 3;
        Console.WriteLine("FORM EFFECTIVE DEFAULT CORRECTED " + JsonSerializer.Serialize(new { correctedOnce, beforeCorrection, afterCorrection = corrected.Revision }));
        if (!explicitRejected || !unsetRejected || !focusedToggle || !localError || !correctedOnce)
            throw new InvalidOperationException("Optional effective default bypassed actual worker validation, lost correction focus, or failed corrected Save.");
    }
}
