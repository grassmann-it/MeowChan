using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.LogicalTree;
using Avalonia.Interactivity;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginHost.Configuration;

namespace MeowChan.DesktopSmoke;

/// <summary>
/// "Add plugin…" and the Installed → Enabled → Active steps over the real plugin folder and inventory: a file is copied through the
/// existing inspection and shown as an unregistered entry (nothing built, enabled or started), a rejected file leaves the folder
/// untouched, and the steps name the next action. The file dialog is replaced by the page's picker seam.
/// </summary>
internal static class PluginAddChecks
{
    internal static string[] ChipTexts(PluginsPage page) =>
        page.FindControl<WrapPanel>("PluginStepChips")!.Children.OfType<Border>().Select(chip => chip.GetLogicalDescendants().OfType<TextBlock>().Single().Text ?? string.Empty).ToArray();

    internal static string NextLine(PluginsPage page)
    {
        TextBlock line = page.FindControl<TextBlock>("PluginLifecycleNext")!;
        return line.IsVisible ? line.Text ?? string.Empty : string.Empty;
    }

    internal static void RequireSteps(PluginsPage page, PluginStepState[] states, string? nextText, string phase)
    {
        Border[] chips = page.FindControl<WrapPanel>("PluginStepChips")!.Children.OfType<Border>().ToArray();
        Require(chips.Length == 3 && states.Length == 3, phase + ": expected three lifecycle steps.");
        for (int index = 0; index < chips.Length; index++)
        {
            string stateClass = states[index] switch { PluginStepState.Done => "chip-step-done", PluginStepState.Next => "chip-step-next",
                PluginStepState.Problem => "chip-warning", _ => "chip-neutral" };
            string stateKey = states[index] switch { PluginStepState.Done => "done", PluginStepState.Next => "next",
                PluginStepState.Problem => "problem", _ => "later" };
            TextBlock label = chips[index].GetLogicalDescendants().OfType<TextBlock>().Single();
            Require(chips[index].Classes.Contains(stateClass) && chips[index].Child is StackPanel content
                && content.Children.Count == 2 && content.Children[0] is not TextBlock
                && AutomationProperties.GetName(label) == Texts.T("plugins.step.name", index + 1, 3, label.Text!, Texts.T("plugins.step.state." + stateKey)),
                $"{phase}: step {index + 1} lost its drawn icon, {states[index]} state or spoken name.");
        }
        Require(NextLine(page) == (nextText ?? string.Empty), $"{phase}: next action is '{NextLine(page)}', expected '{nextText}'.");
    }

    internal static async Task RunAsync(PluginsPage page, PluginProfileStore profile, string pluginDirectory, string scratch, Func<int> httpRequests)
    {
        Texts.SetLanguage("en");
        Directory.CreateDirectory(scratch);
        string name = "Added plugin ü.cs";
        string chosen = Path.Combine(scratch, name);
        await File.WriteAllTextAsync(chosen, "public sealed class AddedPluginFixture { }\n");
        long revision = (await profile.ReadAsync()).Revision;
        int requests = httpRequests();
        string[] before = Directory.GetFileSystemEntries(pluginDirectory).Select(Path.GetFileName).Order().ToArray()!;
        await page.ActivateAsync();
        Button add = page.FindControl<Button>("PluginsAdd")!;
        Require(add.IsEffectivelyVisible && add.IsEnabled && AutomationProperties.GetName(add) == Texts.T("plugins.add")
            && add.Content is StackPanel addLabel && addLabel.Children.Count == 2
            && addLabel.Children.OfType<TextBlock>().Single().Text == Texts.T("plugins.add"),
            "The header has no usable 'Add plugin…' action.");

        // A rejected file: reason shown, folder unchanged.
        string rejected = Path.Combine(scratch, "readme.txt");
        await File.WriteAllTextAsync(rejected, "not a plugin");
        page.PickPluginFile = () => Task.FromResult<string?>(rejected);
        add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Program.WaitUntilAsync(() => !page.IsBusy && !string.IsNullOrEmpty(page.FindControl<TextBlock>("PluginPageError")!.Text));
        Require(page.FindControl<TextBlock>("PluginPageError")!.Text == Texts.T("plugins.add.rejected.unsupported", "readme.txt"),
            "A .txt file was not rejected with its reason: " + page.FindControl<TextBlock>("PluginPageError")!.Text);
        Require(Directory.GetFileSystemEntries(pluginDirectory).Select(Path.GetFileName).Order().SequenceEqual(before),
            "A rejected file changed the plugin folder.");

        // A cancelled dialog changes nothing and shows nothing.
        page.PickPluginFile = () => Task.FromResult<string?>(null);
        add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(100);
        Require(!page.IsBusy && Directory.GetFileSystemEntries(pluginDirectory).Length == before.Length, "A cancelled dialog changed the plugin folder.");

        // The accepted file: copied, listed as an unregistered entry, selected, with the restart named as the next action.
        page.PickPluginFile = () => Task.FromResult<string?>(chosen);
        add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        string target = Path.Combine(pluginDirectory, name);
        await Program.WaitUntilAsync(() => !page.IsBusy && page.Selected?.InventoryPath == target
            && page.FindControl<TextBlock>("PluginTestStatus")!.Text == Texts.T("plugins.add.done", name));
        Require(File.ReadAllText(target) == File.ReadAllText(chosen), "The copy differs from the chosen file.");
        Require(page.Selected!.Activation == null && page.Selected.Description == null,
            "Adding a file registered or described it; that must wait for the restart.");
        Require(!page.FindControl<Button>("PluginEnabled")!.IsEnabled, "The Enable action is available for a file that is not registered yet.");
        RequireSteps(page, new[] { PluginStepState.Done, PluginStepState.Next, PluginStepState.Later }, Texts.T("plugins.step.next.restart"), "after adding");
        Require((await profile.ReadAsync()).Revision == revision, "Adding a file changed the plugin profile.");
        Require(httpRequests() == requests, "Adding a file made a network request.");

        // The same name again is never overwritten.
        await File.WriteAllTextAsync(chosen, "public sealed class Replacement { }\n");
        add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Program.WaitUntilAsync(() => !page.IsBusy && !string.IsNullOrEmpty(page.FindControl<TextBlock>("PluginPageError")!.Text));
        Require(page.FindControl<TextBlock>("PluginPageError")!.Text == Texts.T("plugins.add.rejected.exists", name)
            && File.ReadAllText(target) == "public sealed class AddedPluginFixture { }\n", "A same-named file was not refused or was overwritten.");

        // German: the status, the steps and the next action follow the language.
        Texts.SetLanguage("de");
        await page.RefreshAsync();
        Require(AutomationProperties.GetName(page.FindControl<Button>("PluginsAdd")!) == Texts.T("plugins.add")
            && page.FindControl<Button>("PluginsAdd")!.Content is StackPanel translated
            && translated.Children.OfType<TextBlock>().Single().Text == Texts.T("plugins.add"), "The add action did not follow the language.");
        RequireSteps(page, new[] { PluginStepState.Done, PluginStepState.Next, PluginStepState.Later }, Texts.T("plugins.step.next.restart"), "after adding (de)");
        Texts.SetLanguage("en");

        // Leave the folder as it was found.
        File.Delete(target);
        page.PickPluginFile = null;
        await page.RefreshAsync();
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
