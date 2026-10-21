using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class ArchivePage
{
    private static bool MatchesArchiveCommand(KeyEventArgs e, string id)
    {
        string? shortcut = CommandCatalog.Get(id).Shortcut;
        if (shortcut == null) return false;
        KeyGesture gesture = KeyGesture.Parse(shortcut.Replace("Primary", OperatingSystem.IsMacOS() ? "Meta" : "Ctrl", StringComparison.Ordinal));
        return gesture.Key == e.Key && gesture.KeyModifiers == e.KeyModifiers;
    }

    private ResourceId[] EligibleArchiveWatches(IEnumerable<ResourceId> ids, bool retry) =>
        ids.Where(id => (retry ? CanRetryWatch : CanCheckWatch)?.Invoke(id) == true).ToArray();

    private Task CheckArchiveSelectionAsync(bool retry) => CheckArchiveTargetsAsync(FrozenArchiveSelection(), retry);

    private Task CheckArchiveTargetsAsync(IEnumerable<ResourceId> targets, bool retry)
    {
        ResourceId[] eligible = EligibleArchiveWatches(targets, retry);
        if (eligible.Length > 0 && CheckSelectedWatches != null) return CheckSelectedWatches(eligible, retry);
        Message(Texts.T(retry ? "archive.command.noRetry" : "archive.command.noCheck"));
        return Task.CompletedTask;
    }

    private void RunArchiveLeadAction(ArchiveThreadRow row, string action)
    {
        // Shell callbacks resolve locations synchronously. Retain the menu's row even
        // if the current page has been replaced while this menu was open.
        _archiveCommandLead = row;
        try { SelectionLeadAction?.Invoke(row.Summary.Id, action); }
        finally { _archiveCommandLead = null; }
    }

    private Task ShowArchiveDetailsAsync(ArchiveThreadRow row)
    {
        if (CanRemoveWatch?.Invoke(row.Summary.Id) == true && SelectionDetailsRequested != null)
        { SelectionDetailsRequested(row.Summary.Id); return Task.CompletedTask; }
        string content = row.Title + Environment.NewLine + row.Identity + Environment.NewLine +
            row.Status + Environment.NewLine + row.SourceStatus + Environment.NewLine + row.LastCapture +
            Environment.NewLine + row.Summary.Url + Environment.NewLine + row.Summary.MediaDirectory;
        return ShowArchiveMetadataAsync(Texts.T("archive.command.detailsTitle"), content);
    }

    private async Task ShowArchiveMetadataAsync(string title, string content)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) { Message(content); return; }
        Window dialog = new() { Title = title, Width = 600, Height = 360, MinWidth = 360, MinHeight = 220,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        TextBox details = new() { Text = content, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
        Button close = new() { Content = Texts.T("action.close") };
        Grid layout = new() { Margin = new Thickness(16), RowDefinitions = new RowDefinitions("*,Auto"), RowSpacing = 12 };
        layout.Children.Add(details); layout.Children.Add(close); Grid.SetRow(close, 1);
        close.Click += (_, _) => dialog.Close();
        dialog.KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; dialog.Close(); } };
        dialog.Content = layout;
        await dialog.ShowDialog(owner);
    }

    private void OnArchiveSelectionMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        ArchiveThreadRow[] targets = ArchiveVisibleRows.Where(row => _archiveSelectionIds.Contains(row.Summary.Id)).ToArray();
        ArchiveThreadRow? lead = ArchiveLead;
        menu.Items.Clear();
        if (targets.Length == 0 || lead == null) { menu.Close(); return; }
        Control origin = ArchiveSelectionList;
        ResourceId[] ids = targets.Select(row => row.Summary.Id).ToArray();
        string[] urls = targets.Select(row => row.Summary.Url).ToArray();
        var unavailable = new List<string>();
        bool Ready() => _available && !_ended && !_busy;
        void Add(string id, Action execute, Func<bool>? eligible = null, string? reason = null)
        {
            bool Enabled() => Ready() && (eligible?.Invoke() ?? true);
            string? Reason() => !Ready() ? Texts.T("archive.command.busy") : !Enabled() ? reason : null;
            MenuItem item = CommandBindings.CreateMenuItem(id, execute, Enabled, Reason);
            item.Tag = id;
            menu.Items.Add(item);
            if (!Enabled() && Reason() is string why) unavailable.Add(CommandCatalog.Label(id) + ": " + why);
        }
        Add("thread.read", async () => { OpenReaderRequested?.Invoke(origin); await LoadThreadAsync(lead.Summary.Id); },
            () => ids.Length == 1, Texts.T("archive.command.singleRead"));
        Add("gallery.open", async () => { if (OpenGallery != null) await OpenGallery(lead.Summary.Id, null, origin); });
        Add("thread.details", async () => await ShowArchiveDetailsAsync(lead));
        menu.Items.Add(new Separator());
        Add("export.open-html-dialog", async () => await OpenExportForThreadsAsync(ids, origin));
        Add("selection.copy-links", async () => { if (CopySelectedUrls != null) await CopySelectedUrls(urls); });
        Add("location.open-folder", () => RunArchiveLeadAction(lead, "folder"),
            () => !string.IsNullOrWhiteSpace(lead.Summary.MediaDirectory), Texts.T("archive.command.noFolder"));
        Add("location.open-source", () => RunArchiveLeadAction(lead, "browser"),
            () => !string.IsNullOrWhiteSpace(lead.Summary.Url), Texts.T("archive.command.noSource"));
        menu.Items.Add(new Separator());
        Add("watch.check", async () => await CheckArchiveTargetsAsync(ids, false),
            () => CheckSelectedWatches != null && EligibleArchiveWatches(ids, false).Length > 0, Texts.T("archive.command.noCheck"));
        Add("watch.retry-media", async () => await CheckArchiveTargetsAsync(ids, true),
            () => CheckSelectedWatches != null && EligibleArchiveWatches(ids, true).Length > 0, Texts.T("archive.command.noRetry"));
        Add("rules.edit-scope", () => RunArchiveLeadAction(lead, "rules"),
            () => CanRemoveWatch?.Invoke(lead.Summary.Id) == true && SelectionLeadAction != null, Texts.T("archive.command.noWatch"));
        menu.Items.Add(new Separator());
        Add("watch.remove", async () =>
        {
            ResourceId[] eligible = ids.Where(id => CanRemoveWatch?.Invoke(id) == true).ToArray();
            if (eligible.Length > 0 && RemoveSelectedWatches != null) await RemoveSelectedWatches(eligible);
        }, () => RemoveSelectedWatches != null && ids.Any(id => CanRemoveWatch?.Invoke(id) == true), Texts.T("archive.command.noWatch"));
        if (ids.Length > 1)
        {
            string count = string.Format(Texts.T("archive.command.selectionCount"), ids.Length);
            menu.Items.Add(new Separator());
            var info = new MenuItem { Header = count, Icon = CommandBindings.Icon("thread.details") };
            info.Click += (_, _) => Message(count);
            AutomationProperties.SetName(info, count);
            menu.Items.Add(info);
        }
        if (unavailable.Count > 0)
        {
            // Disabled items cannot receive keyboard focus. This enabled explanation
            // exposes the same reasons to keyboard and assistive technology users.
            string explanation = string.Join(Environment.NewLine, unavailable);
            var info = new MenuItem { Header = Texts.T("archive.command.unavailable"), Icon = CommandBindings.Icon("thread.details") };
            ToolTip.SetTip(info, explanation);
            AutomationProperties.SetHelpText(info, explanation);
            info.Click += (_, _) => Message(explanation);
            menu.Items.Add(info);
        }
    }
}
