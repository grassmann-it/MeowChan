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
using MeowChan.Desktop.Controls;
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
        if (IsFilenameSearchOpen) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) { Message(content); return; }
        // Modals plan MD2: a read-only modal details dialog on the prompt step. The title stands in the title bar only,
        // and a lone Close button is left out: × at the top right and Escape close it.
        Window dialog = MeowChan.Desktop.Controls.DialogWindow.Create(title, DialogSize.S, modal: true);
        TextBox details = new() { Text = content, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
        Border body = new() { Child = details }; body.Classes.Add("workspace-dialog-body");
        // Read-only metadata has no failure path of its own, so the frame carries no error strip.
        Grid layout = MeowChan.Desktop.Controls.DialogFrame.Build(header: null, body, footer: null);
        MeowChan.Desktop.Controls.DialogWindow.Frame(dialog, layout, modal: true);
        MeowChan.Desktop.Controls.DialogEscape.Attach(dialog);
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
        DataGrid table = ArchiveThreadTable;
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
        if (targets.Length == 0 || lead == null) { menu.Items.Clear(); return; }
        Control origin = ArchiveSelectionList;
        ResourceId[] ids = targets.Select(row => row.Summary.Id).ToArray();
        string[] urls = targets.Select(row => row.Summary.Url).ToArray();
        List<Func<string?>> disabledReasons = new();
        ContextMenuBuilder builder = new ContextMenuBuilder().Named(Texts.T("nav.archive"));
        bool Ready() => _available && !_ended && !_busy && !IsReaderDialogOpen;
        static string BatchLabel(string action, int targets) => Texts.Count("archive.command.batchCount", targets, action, targets);
        void Add(string id, Action execute, Func<bool>? eligible = null, string? reason = null,
            Func<int>? count = null, bool leadAction = false, Func<string>? actionLabel = null)
        {
            string Label() => count != null ? BatchLabel(actionLabel?.Invoke() ?? CommandCatalog.Label(id), count())
                : leadAction ? Texts.T("archive.command.leadTarget", actionLabel?.Invoke() ?? CommandCatalog.Label(id), ExportMemberLabel(lead.Summary.Id))
                : actionLabel?.Invoke() ?? CommandCatalog.Label(id);
            bool Enabled() => Ready() && (eligible?.Invoke() ?? true);
            string? Reason() => !Ready() ? Texts.T("archive.command.busy") : !Enabled() ? reason : null;
            builder.Command(id, execute, Enabled(), Reason(), Label(), availableWhen: Enabled, reasonWhen: Reason, labelWhen: Label);
            disabledReasons.Add(() => !Enabled() && Reason() is string why ? CommandCatalog.Label(id) + ": " + why : null);
        }
        Add("thread.favorite-toggle", async () => await ToggleThreadFavoriteAsync(lead.Summary.Id),
            () => CanToggleThreadFavorite(lead.Summary.Id), _threadFavoriteLoadError ?? Texts.T("archive.command.busy"),
            leadAction: true, actionLabel: () => ThreadFavoriteText.Action(_threadFavoriteIds.Contains(lead.Summary.Id)));
        Add("thread.read", async () => { OpenReaderRequested?.Invoke(origin); await LoadThreadAsync(lead.Summary.Id); },
            () => ids.Length == 1, Texts.T("archive.command.singleRead"), leadAction: true);
        // RH1: the one "continue reading" command with the place of this thread; its shortcut opens the newest place, so none is shown here.
        if (ids.Length == 1 && ReadingPlaceFor?.Invoke(lead.Summary.Id) is { } readingPlace && ResumeReadingPlace is { } resumeReading)
        {
            MenuItem continueItem = CommandBindings.CreateMenuItem("navigation.continue-reading", () => _ = resumeReading(readingPlace), Ready,
                () => Ready() ? null : Texts.T("archive.command.busy"), () => Texts.T("resume.thread.action", readingPlace.PostId), showShortcut: false);
            continueItem.Tag = "navigation.continue-reading";
            string continueTip = ReadingHistoryMenu.EntryTip(readingPlace);
            ToolTip.SetTip(continueItem, continueTip);
            AutomationProperties.SetHelpText(continueItem, continueTip);
            commands.Add(continueItem);
            menu.Items.Add(continueItem);
        }
        Add("gallery.open", async () => { if (OpenGallery != null) await OpenGallery(lead.Summary.Id, null, origin); }, leadAction: true);
        Add("thread.details", async () => await ShowArchiveDetailsAsync(lead), leadAction: true);
        if (ids.Length == 1) Add("thread.tags", async () => await OpenThreadTagsAsync(lead.Summary.Id), leadAction: true);
        builder.Section();
        Add("export.open-html-dialog", async () => await OpenExportForThreadsAsync(ids, origin, offerResults: true), count: () => ids.Length);
        if (ids.Length == 1)
            Add("export.open-urltext-dialog", async () => await OpenExportForThreadsAsync(new[] { ids[0] }, origin, offerResults: true, format: ExportFormat.LinkList),
                () => CanOpenUrlTextExport, Texts.T("urltext.export.error.busy"));
        Add("selection.copy-links", async () => { if (CopySelectedUrls != null) await CopySelectedUrls(urls); }, count: () => urls.Length);
        Add("location.open-folder", () => RunArchiveLeadAction(lead, "folder"),
            () => !string.IsNullOrWhiteSpace(lead.Summary.MediaDirectory), Texts.T("archive.command.noFolder"), leadAction: true);
        Add("location.open-source", () => RunArchiveLeadAction(lead, "browser"),
            () => !string.IsNullOrWhiteSpace(lead.Summary.Url), Texts.T("archive.command.noSource"), leadAction: true);
        builder.Section();
        Add("watch.check", async () => await CheckArchiveTargetsAsync(ids, false),
            () => CheckSelectedWatches != null && EligibleArchiveWatches(ids, false).Length > 0, Texts.T("archive.command.noCheck"), count: () => EligibleArchiveWatches(ids, false).Length);
        Add("watch.retry-media", async () => await CheckArchiveTargetsAsync(ids, true),
            () => CheckSelectedWatches != null && EligibleArchiveWatches(ids, true).Length > 0, Texts.T("archive.command.noRetry"), count: () => EligibleArchiveWatches(ids, true).Length);
        Add("rules.edit-scope", () => RunArchiveLeadAction(lead, "rules"),
            () => CanRemoveWatch?.Invoke(lead.Summary.Id) == true && SelectionLeadAction != null, Texts.T("archive.command.noWatch"), leadAction: true);
        builder.Section();
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

        builder.Populate(menu);
        if (ids.Length > 1)
        {
            string count = Texts.Count("archive.command.selectionCount", ids.Length);
            menu.Items.Add(new Separator());
            MenuItem info = new() { Header = new TextBlock { Text = count, TextWrapping = TextWrapping.Wrap, MaxWidth = 280 }, Icon = CommandBindings.Icon("thread.details") };
            info.Click += (_, _) => Message(count);
            AutomationProperties.SetName(info, count);
            menu.Items.Add(info);
        }
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
            foreach (MenuItem item in menu.Items.OfType<MenuItem>()) CommandBindings.Refresh(item);
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
