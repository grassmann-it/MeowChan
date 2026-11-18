using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Ursa.Controls;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

/// <summary>
/// One card per plugin however many files it has, active plugins before disabled ones, a "needs attention" chip that the ordering
/// never hides, and the files chosen inside the detail. Passive fixture: nothing is compiled, described or started.
/// </summary>
internal static class PluginGroupChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        PluginUxFixture fixture = await PluginUxFixture.CreateAsync(Path.Combine(Config.GetConfigDirectory(), "plugin-group-checks"));
        await fixture.AddSecondSourceFileAsync();
        // The image filter (alphabetically after the source) is the active one, the source plugin is disabled: order is by state.
        var state = await fixture.Store.ReadAsync();
        state = await fixture.Store.SetEnabledAsync(PluginUxFixture.FilterPluginId, true, state.Revision);
        await fixture.Store.SetEnabledAsync(PluginUxFixture.SourcePluginId, false, state.Revision);
        string language = Texts.Language;
        ContentControl host = window.FindControl<ContentControl>("SourcesPageHost")!;
        object? retained = host.Content;
        SourcesPage sources = MergedSourcesSmoke.Create(fixture.Services, out PluginsPage page); host.Content = sources;
        try
        {
            Texts.SetLanguage("en");
            MergedSourcesSmoke.OpenPlugins(window);
            await page.ActivateAsync();
            SelectionList list = page.FindControl<SelectionList>("PluginCards")!;
            Require(list.Items.Count == 2, $"Three files of two plugins must give two cards, not {list.Items.Count}.");
            Require(page.FindControl<TextBlock>("PluginsCount")!.Text == Texts.T("plugins.count", 2), "The header counts files, not plugins.");
            Require(((SelectionListItem)list.Items[0]!).GetVisualDescendants().OfType<TextBlock>().Any(text => text.Name == "PluginListName" && text.Text == "Image dimensions"),
                "The enabled plugin is not first in the list.");
            SelectionListItem sourceCard = (SelectionListItem)list.Items[1]!;
            Require(sourceCard.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Name == "PluginListIdentity" && text.IsVisible
                && text.Text == Texts.T("plugins.filesCount", 2)), "The card of a plugin with two files does not say so.");

            // The detail lists both files; choosing the other one shows its entry while the card stays the same.
            page.Select(PluginUxFixture.SourceKey);
            await Settle();
            StackPanel files = page.FindControl<StackPanel>("PluginFilesPanel")!;
            Require(files.IsVisible && page.FindControl<TextBlock>("PluginFilesHeading")!.Text == Texts.T("plugins.files", 2), "The detail does not list the plugin's files.");
            Button[] choices = page.FindControl<StackPanel>("PluginFileChoices")!.Children.OfType<Button>().ToArray();
            Require(choices.Length == 2, "The file list does not have one row per file.");
            choices.Single(button => (string?)button.Tag == PluginUxFixture.SecondSourceKey).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Settle();
            Require(page.Selected?.Key == PluginUxFixture.SecondSourceKey && ReferenceEquals(list.SelectedItem, sourceCard) && list.Items.Count == 2,
                "Choosing the second file did not show it under the same card.");
            // A problem is shown on the card, wherever the ordering puts it.
            await fixture.FailSourceAsync(missing: false);
            await page.RefreshAsync();
            await Settle();
            SelectionListItem current = list.Items.OfType<SelectionListItem>().Single(card => card.GetVisualDescendants().OfType<TextBlock>()
                .Any(text => text.Name == "PluginListName" && text.Text == "Discussion reader"));
            Require(current.GetVisualDescendants().OfType<Border>().Any(chip => chip.Name == "PluginListChip"
                && ((TextBlock)chip.Child!).Text == Texts.T("plugins.problem")), "A plugin that needs attention has no chip on its card.");

            Texts.SetLanguage("de");
            await page.RefreshAsync();
            Require(page.FindControl<TextBlock>("PluginFilesHeading")!.Text == Texts.T("plugins.files", 2), "The files heading did not follow the language.");
            Require(fixture.ForbiddenCalls == 0, "The group checks compiled, described, started or probed a plugin.");
            Console.WriteLine("PLUGIN_GROUP_CHECKS PASS: one card per plugin, state order, files in the detail, attention chip.");
        }
        finally { sources.EndLifetime(); host.Content = retained; Texts.SetLanguage(language); }
    }

    private static async Task Settle()
    {
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
