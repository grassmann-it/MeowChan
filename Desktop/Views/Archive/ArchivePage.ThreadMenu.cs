using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan;

// R2 (plan PLAN-READER-MEDIEN-UND-LEISTE, 14.11.2026): the "Thread" menu next to "View" holds what concerns the open thread as
// a download and as a file on disk: post capture, check now, retry media, media rules, details, folder, source, link and
// removing the watch. "View" stays the reader's own look. Every entry is the same command and the same shell callback the archive
// list's context menu uses (target: the open thread), so the reader needs no download logic of its own. An entry that cannot run
// is disabled with its reason in the tip. Deleting a thread permanently stays in the lists.
public partial class ArchivePage
{
    /// <summary>The ids of the "Thread" menu's entries, in their order (separators are not listed).</summary>
    internal static readonly string[] ReaderThreadMenuIds =
    {
        "archive.capture-toggle", "watch.check", "watch.retry-media", "rules.edit-scope",
        "thread.details", "location.open-folder", "location.open-source", "selection.copy-links",
        "watch.remove"
    };

    private void OnReaderThreadMenuClick(object? sender, RoutedEventArgs e)
    {
        if (_ended || sender is not Control origin) return;
        ContextMenu menu = BuildReaderThreadMenu();
        origin.ContextMenu = menu;
        menu.Open(origin);
    }

    /// <summary>Nothing runs and no dialog is open: the open thread is shown and the page is idle.</summary>
    private bool ThreadMenuReady => !_ended && !IsFilenameSearchOpen && _available && !_busy && !IsReaderDialogOpen && _thread != null;

    private bool OpenThreadIsWatched => _thread != null && CanRemoveWatch?.Invoke(_thread.Summary.Id) == true;

    internal ContextMenu BuildReaderThreadMenu()
    {
        ContextMenu menu = new();
        ArchiveThreadSummary? summary = _thread?.Summary;
        // The row the shell callbacks resolve folder and source from when the thread is not (or no longer) in the lists.
        ArchiveThreadRow? row = summary == null ? null : new ArchiveThreadRow(summary);
        ResourceId? id = summary?.Id;

        MenuItem Add(string commandId, Action execute, Func<bool>? eligible = null, Func<string?>? whyNot = null,
            Func<string>? label = null, bool showShortcut = false)
        {
            string? Reason() => !ThreadMenuReady ? Texts.T("archive.command.busy")
                : eligible?.Invoke() == false ? whyNot?.Invoke() : null;
            MenuItem item = CommandBindings.CreateMenuItem(commandId, execute, () => ThreadMenuReady && (eligible?.Invoke() ?? true),
                Reason, label, showShortcut);
            item.Tag = commandId;
            menu.Items.Add(item);
            return item;
        }

        // Post capture: a check item whose check mark is the state (the status line says it too), the icon changes with it.
        bool capturing = summary?.CaptureEnabled == true;
        MenuItem capture = Add("archive.capture-toggle", () => _ = ToggleThreadCaptureAsync(), () => _setCapture != null,
            () => null, () => Texts.T("archive.captureOn"));
        capture.ToggleType = MenuItemToggleType.CheckBox;
        capture.IsChecked = capturing;
        capture.Icon = CommandBindings.Icon("archive.capture-toggle", 16, capturing ? "capturing" : "stopped");
        string captureTip = Texts.T("archive.captureOn") + " · " + Texts.T("archive.captureHint");
        ToolTip.SetTip(capture, captureTip);
        Avalonia.Automation.AutomationProperties.SetHelpText(capture, captureTip);

        Add("watch.check", () => _ = CheckThreadNowAsync(retry: false), CanRunThreadWatchAction, () => NoWatchActionReason("archive.command.noCheck"));
        Add("watch.retry-media", () => _ = CheckThreadNowAsync(retry: true), CanRunThreadWatchAction, () => NoWatchActionReason("archive.command.noRetry"));
        // The media rules of the open thread (the same command as the lists' "Media rules…"); its Ctrl+R is a reader shortcut already.
        Add("rules.edit-scope", EditThreadRulesFromReader, () => CanEditThreadRules, () => Texts.T("archive.command.noWatch"),
            () => Texts.T("rules.thisThread"), showShortcut: true);
        menu.Items.Add(new Separator());

        Add("thread.details", () => { if (row != null) _ = ShowArchiveDetailsAsync(row); });
        Add("location.open-folder", () => { if (row != null) RunArchiveLeadAction(row, "folder"); },
            () => SelectionLeadAction != null && !string.IsNullOrWhiteSpace(summary?.MediaDirectory), () => Texts.T("archive.command.noFolder"));
        Add("location.open-source", () => { if (row != null) RunArchiveLeadAction(row, "browser"); },
            () => SelectionLeadAction != null && !string.IsNullOrWhiteSpace(summary?.Url), () => Texts.T("archive.command.noSource"));
        Add("selection.copy-links", () => { if (summary != null && CopySelectedUrls != null) _ = CopySelectedUrls(new[] { summary.Url }); },
            () => CopySelectedUrls != null && !string.IsNullOrWhiteSpace(summary?.Url), () => Texts.T("archive.command.noSource"),
            () => Texts.T("reader.thread.copyLink"));
        menu.Items.Add(new Separator());

        Add("watch.remove", () => { if (id != null && RemoveSelectedWatches != null) _ = RemoveSelectedWatches(new[] { id }); },
            () => RemoveSelectedWatches != null && OpenThreadIsWatched, () => Texts.T("archive.command.noWatch"));
        return menu;
    }

    private string NoWatchActionReason(string fallbackKey) => Texts.T(OpenThreadIsWatched ? fallbackKey : "archive.command.noWatch");

    /// <summary>Check now / retry missing media act on a watched thread; the host decides what is due and says so in its notice.</summary>
    private bool CanRunThreadWatchAction() => CheckSelectedWatches != null && OpenThreadIsWatched;

    internal Task CheckThreadNowAsync(bool retry)
    {
        if (!ThreadMenuReady || _thread == null || !CanRunThreadWatchAction()) return Task.CompletedTask;
        return CheckSelectedWatches!(new[] { _thread.Summary.Id }, retry);
    }
}
