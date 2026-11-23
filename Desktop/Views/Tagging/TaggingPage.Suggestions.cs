using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Tags;

namespace MeowChan;

public sealed partial class TaggingPage
{
    private async Task ShowTagSuggestionsAsync(TagQueueItem item)
    {
        if (!IsSuggestionTargetCurrent(item) || TopLevel.GetTopLevel(this) is not Window owner) return;
        IReadOnlyList<StoredTagSuggestion> suggestions;
        try { suggestions = await _host.LoadTagSuggestionsAsync(item.Sha256, item.Length, _lifetime.Token); }
        catch (OperationCanceledException) when (_queueWork.Ended) { return; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
            or InvalidOperationException or TagSuggestionStoreException)
        {
            if (IsSuggestionTargetCurrent(item)) ShowStatus(Texts.T("tagging.page.suggestions.unavailable"));
            return;
        }
        if (IsSuggestionTargetCurrent(item)) await CreateSuggestionsDialog(item.Name, suggestions).ShowDialog(owner);
    }

    private bool IsSuggestionTargetCurrent(TagQueueItem item) => !_queueWork.Ended && IsEffectivelyVisible && SelectedCount == 1
        && Lead?.Item is { } current && current.Key == item.Key && current.Sha256 == item.Sha256 && current.Length == item.Length;

    internal static Window CreateSuggestionsDialog(string mediaName, IReadOnlyList<StoredTagSuggestion> suggestions)
    {
        string title = Texts.T("tagging.page.suggestions.title");
        Window dialog = DialogWindow.Create(title, DialogSize.M, modal: true, heightToContent: suggestions.Count == 0);
        TextBlock medium = new() { Text = mediaName, TextWrapping = TextWrapping.Wrap };
        Control body;
        if (suggestions.Count == 0)
            body = new TextBlock { Text = Texts.T("tagging.page.suggestions.empty"), TextWrapping = TextWrapping.Wrap };
        else
        {
            ListBox list = new()
            {
                Name = "TaggingModelSuggestions",
                ItemsSource = suggestions,
                ItemTemplate = new FuncDataTemplate<StoredTagSuggestion>((row, _) => row is null ? null : SuggestionRow(row))
            };
            AutomationProperties.SetName(list, title);
            body = list;
        }
        body.Margin = new Thickness(WorkspaceMetrics.SectionInset, 0, WorkspaceMetrics.SectionInset, WorkspaceMetrics.SectionInset);
        Button close = new() { Content = Texts.T("action.close") };
        close.Click += (_, _) => dialog.Close();
        DialogWindow.Frame(dialog, DialogFrame.Build(medium, body, DialogFooter.Build(close), fillBody: suggestions.Count > 0), modal: true);
        DialogEscape.Attach(dialog, close: () => dialog.Close());
        return dialog;
    }

    private static StackPanel SuggestionRow(StoredTagSuggestion row)
    {
        TagSuggestionProvenance source = row.Provenance;
        string categoryKey = "booru.tagCategory." + row.Category;
        string category = row.Category == "Rating" ? Texts.T("tagging.page.suggestions.contentRating")
            : Texts.T(BuiltInCatalog.English.ContainsKey(categoryKey) ? categoryKey : "booru.tagCategory.Unknown");
        string details = string.Join(" · ", category, row.Confidence.ToString("P1", Texts.Culture),
            Texts.T("tagging.page.suggestions.state." + row.State));
        string origin = Texts.T("tagging.page.suggestions.origin", source.ModelId, source.ModelRevision,
            source.PluginId, source.PluginVersion, source.TaggerKey);
        return new StackPanel
        {
            Children =
            {
                new TextBlock { Text = row.Name, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = details, TextWrapping = TextWrapping.Wrap, Classes = { "muted" } },
                new TextBlock { Text = origin, TextWrapping = TextWrapping.Wrap, Classes = { "muted" } }
            }
        };
    }
}
