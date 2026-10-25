using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Controls;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class ArchivePage
{
    private readonly Dictionary<ContextMenu, DispatcherTimer> _archiveMenuRefreshTimers = new();

    private static bool MatchesArchiveCommand(KeyEventArgs e, string id) => CommandBindings.Matches(e, id);

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
        TextBlock heading = new() { Text = title, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 16, 16, 0) };
        heading.Classes.Add("section-title");
        Button close = new() { Content = Texts.T("action.close"), MinWidth = 96, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        Border body = new() { Child = details }; body.Classes.Add("workspace-dialog-body");
        Border footer = new() { Child = close }; footer.Classes.Add("workspace-dialog-footer");
        Grid layout = new() { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        layout.Children.Add(heading); layout.Children.Add(body); Grid.SetRow(body, 1);
        layout.Children.Add(footer); Grid.SetRow(footer, 2);
        close.Click += (_, _) => dialog.Close();
        dialog.KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; dialog.Close(); } };
        dialog.Content = layout;
        await dialog.ShowDialog(owner);
    }

    private void OnArchiveSelectionMenuOpening(object? sender, CancelEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        BuildArchiveSelectionMenu(menu);
        e.Cancel = menu.ItemCount == 0;
    }

    private void OpenArchiveSelectionMenu(Control owner, Point anchor)
    {
        if (owner.ContextMenu is not ContextMenu menu) return;
        // Explicit Open(control) bypasses Opening in Avalonia 12. Populate before
        // the native popup measures, including on the first pointer/key request.
        BuildArchiveSelectionMenu(menu);
        if (menu.ItemCount == 0) { menu.Close(); return; }
        // Avalonia 12 freezes both position and native size for Pointer popups.
        // Retain this opening point while allowing live labels to resize in place.
        menu.Placement = PlacementMode.AnchorAndGravity;
        menu.PlacementTarget = owner;
        menu.PlacementRect = new Rect(anchor, new Size(1, 1));
        menu.PlacementAnchor = PopupAnchor.TopLeft;
        menu.PlacementGravity = PopupGravity.BottomRight;
        menu.Open(owner);
    }

    private void BuildArchiveSelectionMenu(ContextMenu menu)
    {
        StopArchiveMenuRefresh(menu);
        // The table's current selection is authoritative when its menu opens.
        // Freeze these IDs before building commands, including an empty selection.
        DataGrid table = Control<DataGrid>("ArchiveThreadTable");
        if (ReferenceEquals(menu, table.ContextMenu))
        {
            ArchiveThreadRow[] selected = table.SelectedItems.OfType<ArchiveThreadRow>().ToArray();
            _archiveSelectionIds.Clear();
            foreach (ArchiveThreadRow row in selected) _archiveSelectionIds.Add(row.Summary.Id);
            if (_archiveLeadId == null || !_archiveSelectionIds.Contains(_archiveLeadId))
                _archiveLeadId = selected.FirstOrDefault()?.Summary.Id;
        }
        ArchiveThreadRow[] targets = ArchiveVisibleRows.Where(row => _archiveSelectionIds.Contains(row.Summary.Id)).ToArray();
        ArchiveThreadRow? lead = ArchiveLead;
        menu.Items.Clear();
        if (targets.Length == 0 || lead == null) return;
        Control origin = ArchiveSelectionList;
        ResourceId[] ids = targets.Select(row => row.Summary.Id).ToArray();
        string[] urls = targets.Select(row => row.Summary.Url).ToArray();
        List<MenuItem> commands = new();
        List<Func<string?>> disabledReasons = new();
        bool Ready() => _available && !_ended && !_busy;
        void Add(string id, Action execute, Func<bool>? eligible = null, string? reason = null,
            Func<int>? count = null, bool leadAction = false, Func<string>? actionLabel = null)
        {
            MenuItem? item = null;
            string Label() => count != null ? Texts.T("archive.command.batchCount", actionLabel?.Invoke() ?? CommandCatalog.Label(id), count())
                : leadAction ? Texts.T("archive.command.leadTarget", actionLabel?.Invoke() ?? CommandCatalog.Label(id), ExportMemberLabel(lead.Summary.Id))
                : actionLabel?.Invoke() ?? CommandCatalog.Label(id);
            void UpdateLabel()
            {
                if (item == null) return;
                string label = Label();
                item.Header = label;
                AutomationProperties.SetName(item, label);
                string tip = label + (CommandCatalog.ShortcutDisplay(id) is string shortcut ? " · " + shortcut : "");
                if (!Ready()) tip += " · " + Texts.T("archive.command.busy");
                else if (eligible?.Invoke() == false && reason != null) tip += " · " + reason;
                ToolTip.SetTip(item, tip);
                AutomationProperties.SetHelpText(item, tip);
            }
            bool Enabled() { UpdateLabel(); return Ready() && (eligible?.Invoke() ?? true); }
            string? Reason() => !Ready() ? Texts.T("archive.command.busy") : !Enabled() ? reason : null;
            item = CommandBindings.CreateMenuItem(id, execute, Enabled, Reason, Label);
            item.Tag = id;
            UpdateLabel();
            commands.Add(item);
            menu.Items.Add(item);
            disabledReasons.Add(() => !Enabled() && Reason() is string why ? CommandCatalog.Label(id) + ": " + why : null);
        }
        Add("thread.favorite-toggle", async () => await ToggleThreadFavoriteAsync(lead.Summary.Id),
            () => CanToggleThreadFavorite(lead.Summary.Id), _threadFavoriteLoadError ?? Texts.T("archive.command.busy"),
            leadAction: true, actionLabel: () => ThreadFavoriteText.Action(_threadFavoriteIds.Contains(lead.Summary.Id)));
        Add("thread.read", async () => { OpenReaderRequested?.Invoke(origin); await LoadThreadAsync(lead.Summary.Id); },
            () => ids.Length == 1, Texts.T("archive.command.singleRead"), leadAction: true);
        Add("gallery.open", async () => { if (OpenGallery != null) await OpenGallery(lead.Summary.Id, null, origin); }, leadAction: true);
        Add("thread.details", async () => await ShowArchiveDetailsAsync(lead), leadAction: true);
        menu.Items.Add(new Separator());
        Add("export.open-html-dialog", async () => await OpenExportForThreadsAsync(ids, origin), count: () => ids.Length);
        Add("selection.copy-links", async () => { if (CopySelectedUrls != null) await CopySelectedUrls(urls); }, count: () => urls.Length);
        Add("location.open-folder", () => RunArchiveLeadAction(lead, "folder"),
            () => !string.IsNullOrWhiteSpace(lead.Summary.MediaDirectory), Texts.T("archive.command.noFolder"), leadAction: true);
        Add("location.open-source", () => RunArchiveLeadAction(lead, "browser"),
            () => !string.IsNullOrWhiteSpace(lead.Summary.Url), Texts.T("archive.command.noSource"), leadAction: true);
        menu.Items.Add(new Separator());
        Add("watch.check", async () => await CheckArchiveTargetsAsync(ids, false),
            () => CheckSelectedWatches != null && EligibleArchiveWatches(ids, false).Length > 0, Texts.T("archive.command.noCheck"), count: () => EligibleArchiveWatches(ids, false).Length);
        Add("watch.retry-media", async () => await CheckArchiveTargetsAsync(ids, true),
            () => CheckSelectedWatches != null && EligibleArchiveWatches(ids, true).Length > 0, Texts.T("archive.command.noRetry"), count: () => EligibleArchiveWatches(ids, true).Length);
        Add("rules.edit-scope", () => RunArchiveLeadAction(lead, "rules"),
            () => CanRemoveWatch?.Invoke(lead.Summary.Id) == true && SelectionLeadAction != null, Texts.T("archive.command.noWatch"), leadAction: true);
        menu.Items.Add(new Separator());
        Add("watch.remove", async () =>
        {
            ResourceId[] eligible = ids.Where(id => CanRemoveWatch?.Invoke(id) == true).ToArray();
            if (eligible.Length > 0 && RemoveSelectedWatches != null) await RemoveSelectedWatches(eligible);
        }, () => RemoveSelectedWatches != null && ids.Any(id => CanRemoveWatch?.Invoke(id) == true), Texts.T("archive.command.noWatch"),
            count: () => ids.Count(id => CanRemoveWatch?.Invoke(id) == true));
        Add("archive.delete-permanently", async () =>
        {
            if (DeletePermanentlyRequested != null) await DeletePermanentlyRequested(ids, origin);
        }, () => DeletePermanentlyRequested != null, count: () => ids.Length);
        if (ids.Length > 1)
        {
            string count = string.Format(Texts.T("archive.command.selectionCount"), ids.Length);
            menu.Items.Add(new Separator());
            // Keep the explanatory prose compact even when the menu theme adds
            // the shared shortcut column to this shortcut-free row.
            MenuItem info = new() { Header = new TextBlock { Text = count, TextWrapping = TextWrapping.Wrap, MaxWidth = 280 },
                Icon = CommandBindings.Icon("thread.details") };
            info.Click += (_, _) => Message(count);
            AutomationProperties.SetName(info, count);
            menu.Items.Add(info);
        }
        // Disabled commands cannot receive keyboard focus. Refresh this enabled
        // explanation together with changing counts and command availability.
        string explanation = string.Empty;
        MenuItem unavailable = new() { Header = Texts.T("archive.command.unavailable"), Icon = CommandBindings.Icon("thread.details") };
        void UpdateExplanation()
        {
            explanation = string.Join(Environment.NewLine, disabledReasons.Select(reason => reason()).Where(reason => reason != null));
            unavailable.Header = Texts.T("archive.command.unavailable");
            unavailable.IsVisible = explanation.Length > 0;
            ToolTip.SetTip(unavailable, explanation);
            AutomationProperties.SetHelpText(unavailable, explanation);
        }
        unavailable.Click += (_, _) => Message(explanation);
        menu.Items.Add(unavailable);
        UpdateExplanation();
        DispatcherTimer refresh = new() { Interval = TimeSpan.FromMilliseconds(250) };
        refresh.Tick += (_, _) =>
        {
            foreach (MenuItem item in commands) CommandBindings.Refresh(item);
            UpdateExplanation();
        };
        _archiveMenuRefreshTimers.Add(menu, refresh);
        menu.Closed += OnArchiveSelectionMenuClosed;
        refresh.Start();
    }

    private void OnArchiveSelectionMenuClosed(object? sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu) StopArchiveMenuRefresh(menu);
    }

    private void StopArchiveMenuRefresh(ContextMenu menu)
    {
        if (_archiveMenuRefreshTimers.Remove(menu, out DispatcherTimer? refresh)) refresh.Stop();
        menu.Closed -= OnArchiveSelectionMenuClosed;
    }
}
