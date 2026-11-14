using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;

namespace MeowChan;

public partial class PluginsPage
{
    // One plugin can come from several files (candidates), each its own catalog entry. The list shows ONE card per plugin;
    // the detail lists its files. Selection, drafts, actions and approvals all stay per entry (file): a card only stands for
    // the group, and the entry shown in the detail is the one chosen in the list of files.
    private string? _filesSignature;
    private bool _focusFileChoice;

    private IEnumerable<PluginCatalogEntry> GroupMembers(PluginCatalogEntry entry) => entry.PluginId == null
        ? new[] { entry } : (_catalog?.Entries ?? Array.Empty<PluginCatalogEntry>()).Where(other => other.PluginId == entry.PluginId);

    /// <summary>The file a plugin's card stands for: the one selected for the next start, else the one in use, else the first.</summary>
    private PluginCatalogEntry Representative(PluginCatalogEntry entry)
    {
        PluginCatalogEntry[] members = GroupMembers(entry).ToArray();
        string? selected = entry.Activation?.SelectedCandidateId, active = entry.Activation?.Active?.CandidateId;
        PluginCatalogEntry? pick = members.Where(Included).FirstOrDefault(member => member.CandidateId != null && member.CandidateId == selected)
            ?? members.Where(Included).FirstOrDefault(member => member.CandidateId != null && member.CandidateId == active)
            ?? members.FirstOrDefault(Included);
        return pick ?? entry;
    }

    /// <summary>The key of the card that stands for the entry's plugin (the entry's own key when it has no sibling files).</summary>
    private string CardKey(string key) => _catalog?.Entries.FirstOrDefault(entry => entry.Key == key) is { } found ? Representative(found).Key : key;

    private static int AvailabilityRank(PluginEntryPresentation presentation) => presentation.Availability switch
    {
        PluginAvailability.Active => 0, PluginAvailability.Disabled => 1, PluginAvailability.Unavailable => 2, _ => 3
    };

    private static bool HasProblem(PluginEntryPresentation presentation) => presentation.CandidateMissing || presentation.HasWorkerProblem
        || !string.IsNullOrEmpty(presentation.CandidateProblem) || !string.IsNullOrEmpty(presentation.ActivationProblem);

    /// <summary>One entry per plugin, active plugins first, then disabled, then those that are unavailable or not loaded; by name within each.</summary>
    private PluginCatalogEntry[] CardEntries()
    {
        if (_catalog == null) return Array.Empty<PluginCatalogEntry>();
        var productionWorkers = _services?.GetProductionWorkerSnapshots?.Invoke();
        return _catalog.Entries.Where(Included).GroupBy(entry => entry.PluginId ?? entry.Key).Select(group => Representative(group.First()))
            .Select(entry => (Entry: entry, Rank: AvailabilityRank(PluginEntryPresentation.Create(entry, productionWorkers))))
            .OrderBy(item => item.Rank).Select(item => item.Entry).ToArray();
    }

    private bool GroupNeedsAttention(PluginCatalogEntry entry)
    {
        var productionWorkers = _services?.GetProductionWorkerSnapshots?.Invoke();
        return GroupMembers(entry).Any(member => HasProblem(PluginEntryPresentation.Create(member, productionWorkers)));
    }

    private int GroupCount => _catalog?.Entries.Select(entry => entry.PluginId ?? entry.Key).Distinct(StringComparer.Ordinal).Count() ?? 0;

    /// <summary>The "Files (n)" part of the detail: shown only when the plugin has several files; choosing one shows that file's details.</summary>
    private void RenderFileChoices(PluginCatalogEntry entry)
    {
        PluginCatalogEntry[] members = GroupMembers(entry).Where(Included).ToArray();
        StackPanel panel = C<StackPanel>("PluginFilesPanel");
        panel.IsVisible = members.Length > 1;
        if (members.Length <= 1) { _filesSignature = null; return; }
        var productionWorkers = _services?.GetProductionWorkerSnapshots?.Invoke();
        var rows = members.Select(member =>
        {
            PluginEntryPresentation view = PluginEntryPresentation.Create(member, productionWorkers);
            string file = Path.GetFileName(member.LoaderPath) ?? member.CandidateId ?? member.Key;
            string[] facts = new[]
            {
                view.CandidateVersion is { } version ? Texts.T("plugins.fileVersion", version) : null,
                view.IsActiveCandidate ? Texts.T("plugins.candidate.current") : view.IsSelectedCandidate ? Texts.T("plugins.candidate.selected") : null,
                HasProblem(view) ? Texts.T("plugins.problem") : null
            }.Where(value => value != null).Select(value => value!).ToArray();
            return (member.Key, Label: file + (facts.Length > 0 ? " · " + string.Join(" · ", facts) : string.Empty), Path: member.LoaderPath);
        }).ToArray();
        string signature = Texts.Language + "|" + entry.Key + "|" + string.Join("|", rows.Select(row => row.Key + "=" + row.Label));
        C<TextBlock>("PluginFilesHeading").Text = Texts.T("plugins.files", members.Length);
        C<TextBlock>("PluginFilesHint").Text = Texts.T("plugins.filesHint", MeowChan.Desktop.Commands.CommandCatalog.Label("plugins.select-candidate"));
        if (signature == _filesSignature) return;
        _filesSignature = signature;
        StackPanel choices = C<StackPanel>("PluginFileChoices");
        choices.Children.Clear();
        Button? shown = null;
        foreach (var row in rows)
        {
            bool isShown = row.Key == entry.Key;
            var button = new Button { Content = (isShown ? "→ " : "   ") + row.Label, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left, Tag = row.Key };
            if (isShown) { button.Classes.Add("primary"); shown = button; }
            ToolTip.SetTip(button, row.Path ?? row.Key);
            AutomationProperties.SetName(button, isShown ? row.Label + " (" + Texts.T("plugins.fileShown") + ")" : row.Label);
            string key = row.Key;
            button.Click += (_, _) => { if (!_busy && key != _selectedKey) { _focusFileChoice = true; Select(key); } };
            choices.Children.Add(button);
        }
        if (_focusFileChoice && shown != null)
        {
            _focusFileChoice = false;
            Dispatcher.UIThread.Post(() => { if (!_ended && shown.IsAttachedToVisualTree()) shown.Focus(); }, DispatcherPriority.Loaded);
        }
    }
}
