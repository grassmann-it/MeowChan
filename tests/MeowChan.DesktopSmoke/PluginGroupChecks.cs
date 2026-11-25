using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Ursa.Controls;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

/// <summary>
/// One card per plugin or undescribed candidate, active plugins before disabled ones, a "needs attention" chip that the ordering
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
            const int expectedCardCount = 3;
            Require(list.Items.Count == expectedCardCount,
                $"Expected two plugin cards and an undescribed candidate card, but found {list.Items.Count} cards.");
            Require(page.FindControl<TextBlock>("PluginsCount")!.Text == Texts.T("plugins.count", expectedCardCount),
                "The header must count the plugin and candidate cards.");

            SelectionListItem filterCard = SelectCard(page, list, PluginUxFixture.FilterKey, PluginUxFixture.FilterPluginId);
            Require(list.SelectedIndex == 0, "The enabled filter plugin must have the first card.");
            SelectionListItem unknownCard = SelectCard(page, list, PluginUxFixture.UnknownKey, null);
            Require(page.Selected is { Description: null } && !ReferenceEquals(unknownCard, filterCard),
                "The undescribed candidate must have a separate card.");
            SelectionListItem sourceCard = SelectCard(page, list, PluginUxFixture.SourceKey, PluginUxFixture.SourcePluginId);
            await Settle();
            Require(!ReferenceEquals(sourceCard, filterCard) && !ReferenceEquals(sourceCard, unknownCard),
                "The source plugin must have its own card.");
            Require(sourceCard.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Name == "PluginListIdentity" && text.IsVisible
                && text.Text == Texts.T("plugins.filesCount", 2)), "The card of a plugin with two files does not say so.");

            // The detail lists both files; choosing the other one shows its entry while the card stays the same.
            StackPanel files = page.FindControl<StackPanel>("PluginFilesPanel")!;
            Require(files.IsVisible && page.FindControl<TextBlock>("PluginFilesHeading")!.Text == Texts.T("plugins.files", 2), "The detail does not list the plugin's files.");
            Button[] choices = page.FindControl<StackPanel>("PluginFileChoices")!.Children.OfType<Button>().ToArray();
            Button? secondFile = choices.FirstOrDefault(button => (string?)button.Tag == PluginUxFixture.SecondSourceKey);
            Require(choices.Length == 2 && secondFile != null && choices.Any(button => (string?)button.Tag == PluginUxFixture.SourceKey),
                "The file list must contain exactly the two source candidates.");
            secondFile!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Settle();
            Require(page.Selected is { Key: PluginUxFixture.SecondSourceKey, PluginId: PluginUxFixture.SourcePluginId }
                && ReferenceEquals(list.SelectedItem, sourceCard) && list.Items.Count == expectedCardCount,
                "Choosing the second file did not show it under the same card.");
            // A problem is shown on the card, wherever the ordering puts it.
            await fixture.FailSourceAsync(missing: false);
            await page.RefreshAsync();
            SelectionListItem current = SelectCard(page, list, PluginUxFixture.SecondSourceKey, PluginUxFixture.SourcePluginId);
            await Settle();
            Require(current.GetVisualDescendants().OfType<Border>().Any(chip => chip.Name == "PluginListChip"
                && ((TextBlock)chip.Child!).Text == Texts.T("plugins.problem")), "A plugin that needs attention has no chip on its card.");

            Texts.SetLanguage("de");
            await page.RefreshAsync();
            Require(page.FindControl<TextBlock>("PluginFilesHeading")!.Text == Texts.T("plugins.files", 2), "The files heading did not follow the language.");
            Require(fixture.ForbiddenCalls == 0, "The group checks compiled, described, started or probed a plugin.");
            Console.WriteLine("PLUGIN_GROUP_CHECKS PASS: plugin IDs, separate undescribed candidate, state order, files in the detail, attention chip.");
        }
        finally { sources.EndLifetime(); host.Content = retained; Texts.SetLanguage(language); }
    }

    private static SelectionListItem SelectCard(PluginsPage page, SelectionList list, string candidateKey, string? pluginId)
    {
        page.Select(candidateKey);
        Require(page.Selected is { } entry && entry.Key == candidateKey && entry.PluginId == pluginId,
            $"Selecting candidate {candidateKey} did not show the expected entry.");
        return list.SelectedItem as SelectionListItem
            ?? throw new InvalidOperationException($"Selecting candidate {candidateKey} did not select a card.");
    }

    private static async Task Settle()
    {
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
