using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MeowChan.Archive;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class ArchivePage
{
    private bool _listLoaded;

    internal static bool HasArchiveConstraints(ArchiveThreadQuery query, int workFilter) =>
        !string.IsNullOrWhiteSpace(query.Text) || query.Source != null || query.Board != null
        || query.SourceState != null || query.CaptureEnabled != null || query.HasPosts != null
        || workFilter != 0;

    internal static bool HasArchiveConstraints(ArchiveSearchSpec spec) =>
        !string.IsNullOrWhiteSpace(spec.Text) || spec.Sources.Count > 0 || spec.Boards.Count > 0
        || spec.SourceStates.Count > 0 || spec.CaptureEnabled != null || spec.HasPosts != null
        || spec.WorkFilter != MeowChan.Archive.ArchiveWorkFilter.All || spec.DateRange != null || spec.Tags != null;

    private void UpdateArchiveEmptyState()
    {
        bool constrained = HasArchiveConstraints(_query);
        ArchiveEmptyState.IsVisible = _listLoaded && _listTotal == 0;
        ArchiveEmpty.Text = Texts.T(constrained ? "archive.noResults" : "archive.empty");
        Button reset = ArchiveResetFilters;
        reset.Content = Texts.T("archive.resetFilters");
        reset.IsVisible = constrained;
        // An archive that has nothing yet is filled from Downloads; a filter that matches nothing is reset instead.
        ArchiveGoDownloads.IsVisible = !constrained;
        // AP2/A6: a text search that finds nothing here can be tried in Media in one step.
        string text = _query.Text ?? string.Empty;
        Button media = ArchiveEmptySearchInMedia;
        media.IsVisible = constrained && text.Length > 0 && SearchInMediaRequested != null;
        if (media.IsVisible)
        {
            media.Content = Texts.T("search.carry.toMedia", text);
            Avalonia.Automation.AutomationProperties.SetName(media, Texts.T("search.carry.toMedia", text));
        }
    }

    // ---- the reader toolbar without a thread (AR-03) ----------------------------------------------------------------------------------

    /// <summary>
    /// Why the actions of the open thread (Find, Thread, Tags) cannot run: no thread is open ("Open a thread first."), or one is still being
    /// read (the page is busy). Null while a thread is shown.
    /// </summary>
    internal string? ReaderThreadActionsReason => _thread != null ? null
        : Texts.T(_busy && _selectedThread != null ? "archive.command.busy" : "reader.thread.openFirst");

    // The reason of the actions that act on the selected thread (the reader may still be reading it): only "no thread" is named, a busy page keeps its own.
    private string? NoThreadSelectedReason() => _selectedThread == null ? Texts.T("reader.thread.openFirst") : null;

    private bool _noThreadSelectedTips;

    /// <summary>
    /// Find, Thread and Tags belong to the thread the reader shows: without one they are disabled and the tip says why (the empty reader no longer
    /// offers a Find that does nothing and a menu that blames a running operation). Read, Gallery and Export carry the same reason.
    /// Called with every change of the page's state and of the language; it only writes what changed.
    /// </summary>
    private void UpdateReaderThreadActions()
    {
        bool shown = !_ended && _thread != null;
        ReaderFindOpen.IsEnabled = shown && !_busy;
        ReaderThreadOpen.IsEnabled = shown;
        string? why = shown ? null : ReaderThreadActionsReason;
        string? findWhy = ReaderFindOpen.IsEnabled ? null : why ?? Texts.T("archive.command.busy");
        string threadLabel = Texts.T("reader.thread.menu");
        SetReaderActionTip(ReaderFindOpen, CommandBindings.Tooltip("reader.find", findWhy));
        SetReaderActionTip(ReaderThreadOpen, why == null ? threadLabel : threadLabel + " · " + why);
        SetReaderActionTip(ReaderTagsOpen, ReaderTagsTip());
        // Read, Gallery and Export are applied from their commands: the reason is theirs to name, and they are applied again only when it appears or goes.
        bool noThread = _selectedThread == null;
        if (noThread != _noThreadSelectedTips)
        {
            _noThreadSelectedTips = noThread;
            ApplyThreadScopedCommands();
        }
    }

    private string ReaderTagsTip() =>
        CommandBindings.Tooltip("thread.tags", _thread == null ? ReaderThreadActionsReason : null) + Environment.NewLine + Texts.T("tags.openHint");

    private void ApplyThreadScopedCommands()
    {
        CommandBindings.Apply(ArchiveOpenGallery, "gallery.open", NoThreadSelectedReason, iconWithLabel: true);
        CommandBindings.Apply(ArchiveReadPosts, "reader.expand", NoThreadSelectedReason, iconWithLabel: true);
        CommandBindings.Apply(ExportArchiveButton, "export.open-html-dialog", NoThreadSelectedReason, iconWithLabel: true);
        foreach (Button button in new[] { ArchiveOpenGallery, ArchiveReadPosts, ExportArchiveButton }) ToolTip.SetShowOnDisabled(button, true);
    }

    private static void SetReaderActionTip(Button button, string tip)
    {
        if (!Equals(ToolTip.GetTip(button), tip)) ToolTip.SetTip(button, tip);
        ToolTip.SetShowOnDisabled(button, true);
        Avalonia.Automation.AutomationProperties.SetHelpText(button, tip);
    }

    private void OnEmptySearchInMediaClick(object? sender, RoutedEventArgs e)
    {
        string text = _query.Text ?? string.Empty;
        if (!_ended && text.Length > 0) SearchInMediaRequested?.Invoke(text);
    }

    /// <summary>The empty archive's next step: the Downloads command, labelled as a destination.</summary>
    private void LocalizeEmptyState() =>
        CommandBindings.ApplyLabeled(ArchiveGoDownloads, "navigation.downloads", Texts.T("nav.goDownloads"));

    /// <summary>Set by the shell: opens Downloads like the footer button does.</summary>
    internal Action? GoToDownloadsRequested { get; set; }

    private void OnGoDownloadsClick(object? sender, RoutedEventArgs e) => GoToDownloadsRequested?.Invoke();

    private async void OnResetArchiveFiltersClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _ended || !CanRead) return;
        // The sort stays, except a default Relevance order, which follows the cleared text back to its default.
        ArchiveSearchSpec reset = new(sort: SortAfterTextChange(_query.Sort, _query.Text, ""));
        WriteSearchDraft(reset);
        _showReader = false; UpdateLayoutMode();
        await LoadListAsync(NewListLoad(ArchiveSearchRequest.Create(reset)));
        ArchiveScopedBox.Focus();
    }
}
