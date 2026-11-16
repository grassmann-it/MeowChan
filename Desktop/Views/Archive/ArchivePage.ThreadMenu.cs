using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan;

// R2 (plan PLAN-READER-MEDIEN-UND-LEISTE, 14.11.2026): the "Thread" menu next to the settings gear holds what concerns the open thread as
// a download and as a file on disk: post capture, check now, retry media, media rules, post bookmarks, details, folder, source, link and
// removing the watch. The gear (RL1, formerly "View") stays the reader's own look. Every entry is the same command and the same shell callback the archive
// list's context menu uses (target: the open thread), so the reader needs no download logic of its own. An entry that cannot run
// is disabled with its reason in the tip. Deleting a thread permanently stays in the lists.
public partial class ArchivePage
{
    /// <summary>The ids of the "Thread" menu's entries, in their order (separators are not listed).</summary>
    internal static readonly string[] ReaderThreadMenuIds =
    {
        "archive.capture-toggle", "watch.check", "watch.retry-media", "rules.edit-scope",
        "post-bookmarks.open-manager", "thread.details", "thread.show-in-media", "location.open-folder", "location.open-source", "selection.copy-links",
        "watch.remove"
    };

    private void OnReaderThreadMenuClick(object? sender, RoutedEventArgs e)
    {
        if (_ended || sender is not Control origin) return;
        ContextMenu menu = BuildReaderThreadMenu();
        origin.ContextMenu = menu;
        // The menu captured this thread (summary, row, id): once it is closed the button must not keep it for a later right-click or Menu key,
        // which would open it unrebuilt for whatever thread is open by then. It stays attached while open (the toolbar overflow watches it).
        menu.Closed += (_, _) => { if (ReferenceEquals(origin.ContextMenu, menu)) origin.ContextMenu = null; };
        ShowThreadMenu(menu, origin);
    }

    /// <summary>How the "Thread" menu is shown at its button (a test records it instead of opening a popup).</summary>
    internal Action<ContextMenu, Control> ShowThreadMenu { get; set; } = static (menu, origin) => menu.Open(origin);

    /// <summary>Nothing runs and no dialog is open: the open thread is shown and the page is idle.</summary>
    private bool ThreadMenuReady => !_ended && !IsFilenameSearchOpen && _available && !_busy && !IsReaderDialogOpen && _thread != null;

    private bool OpenThreadIsWatched => _thread != null && CanRemoveWatch?.Invoke(_thread.Summary.Id) == true;

    internal ContextMenu BuildReaderThreadMenu()
    {
        ArchiveThreadSummary? summary = _thread?.Summary;
        // The row the shell callbacks resolve folder and source from when the thread is not (or no longer) in the lists.
        ArchiveThreadRow? row = summary == null ? null : new ArchiveThreadRow(summary);
        ResourceId? id = summary?.Id;
        ContextMenuBuilder builder = new ContextMenuBuilder().Named(Texts.T("reader.thread.menu"));

        void Add(string commandId, Action execute, Func<bool>? eligible = null, Func<string?>? whyNot = null,
            Func<string>? label = null, bool showShortcut = false)
        {
            bool CanExecute() => ThreadMenuReady && (eligible?.Invoke() ?? true);
            string? Reason() => !ThreadMenuReady ? Texts.T("archive.command.busy")
                : eligible?.Invoke() == false ? whyNot?.Invoke() : null;
            builder.Command(commandId, execute, CanExecute(), Reason(), label?.Invoke(), showShortcut: showShortcut,
                availableWhen: CanExecute, reasonWhen: Reason, labelWhen: label);
        }

        bool capturing = summary?.CaptureEnabled == true;
        Add("archive.capture-toggle", () => _ = ToggleThreadCaptureAsync(), () => _setCapture != null,
            () => null, () => Texts.T("archive.captureOn"));
        Add("watch.check", () => _ = CheckThreadNowAsync(retry: false), CanRunThreadWatchAction, () => NoWatchActionReason("archive.command.noCheck"));
        Add("watch.retry-media", () => _ = CheckThreadNowAsync(retry: true), CanRunThreadWatchAction, () => NoWatchActionReason("archive.command.noRetry"));
        Add("rules.edit-scope", EditThreadRulesFromReader, () => CanEditThreadRules, () => Texts.T("archive.command.noWatch"),
            () => Texts.T("rules.thisThread"), showShortcut: true);
        // The first builder section is the first native separator.
        builder.Section();

        // RL1: the post bookmarks manager (the archive list's button and the app menu open the same dialog); not from the large reader,
        // where the shell does not open it.
        // QW-A #10 (glue wave8): one entry for RL1 and QW-A; RL1's place and large-reader guard, QW-A's menu label.
        Add("post-bookmarks.open-manager", () => OpenPostBookmarks?.Invoke(), () => OpenPostBookmarks != null && !_largeReader,
            () => Texts.T("reader.thread.bookmarksUnavailable"), () => Texts.T("reader.thread.postBookmarks"));
        Add("thread.details", () => { if (row != null) _ = ShowArchiveDetailsAsync(row); });
        // QW-A #7: the thread's media on the Media page (the thread: chip).
        Add("thread.show-in-media", () => { if (id != null) ShowThreadInMediaRequested?.Invoke(id, summary?.Title); }, () => ShowThreadInMediaRequested != null && id?.Thread != null);
        Add("location.open-folder", () => { if (row != null) RunArchiveLeadAction(row, "folder"); },
            () => SelectionLeadAction != null && !string.IsNullOrWhiteSpace(summary?.MediaDirectory), () => Texts.T("archive.command.noFolder"));
        Add("location.open-source", () => { if (row != null) RunArchiveLeadAction(row, "browser"); },
            () => SelectionLeadAction != null && !string.IsNullOrWhiteSpace(summary?.Url), () => Texts.T("archive.command.noSource"));
        Add("selection.copy-links", () => { if (summary != null && CopySelectedUrls != null) _ = CopySelectedUrls(new[] { summary.Url }); },
            () => CopySelectedUrls != null && !string.IsNullOrWhiteSpace(summary?.Url), () => Texts.T("archive.command.noSource"),
            () => Texts.T("reader.thread.copyLink"));
        builder.Section(); // new Separator() before the destructive watch action; the second section mirrors the second native separator: new Separator().
        Add("watch.remove", () => { if (id != null && RemoveSelectedWatches != null) _ = RemoveSelectedWatches(new[] { id }); },
            () => RemoveSelectedWatches != null && OpenThreadIsWatched, () => Texts.T("archive.command.noWatch"));

        ContextMenu menu = builder.Build()!;
        MenuItem? capture = menu.Items.OfType<MenuItem>().FirstOrDefault(item => Equals(item.Tag, "archive.capture-toggle"));
        if (capture != null)
        {
            capture.ToggleType = MenuItemToggleType.CheckBox;
            capture.IsChecked = capturing;
            capture.Icon = CommandBindings.Icon("archive.capture-toggle", 16, capturing ? "capturing" : "stopped");
            string captureTip = Texts.T("archive.captureOn") + " · " + Texts.T("archive.captureHint");
            ToolTip.SetTip(capture, captureTip);
            Avalonia.Automation.AutomationProperties.SetHelpText(capture, captureTip);
        }
        return menu;
    }

    // Installed once on the pane (it moves between the archive view and the large reader). The large reader's tunnel (OnReaderKeyDown) has already taken
    // "Rules for this thread" there; the archive view's reader had no handler for either key (audit 16.11.2026: Alt+M claimed the reader and did nothing).
    private void InitializeReaderThreadKeys() => ArchiveReaderPane.AddHandler(KeyDownEvent, (_, e) => TryRouteReaderThreadKey(e), RoutingStrategies.Bubble);

    /// <summary>
    /// "Show in Media" (Alt+M) and "Rules for this thread" for the thread the reader shows, the Thread menu's own entries, from any focus in the reader
    /// that is not a native editor. Unavailable: a message says why instead of nothing.
    /// </summary>
    internal bool TryRouteReaderThreadKey(KeyEventArgs e)
    {
        if (e.Handled || ShortcutService.IsCapturing || CommandRouter.IsNativeInput(e.Source, e.Key) || IsReaderDialogOpen || _ended || _thread == null) return false;
        if (CommandBindings.Matches(e, "thread.show-in-media"))
        {
            e.Handled = true;
            if (ThreadMenuReady && ShowThreadInMediaRequested != null && _thread.Summary.Id.Thread != null)
                ShowThreadInMediaRequested(_thread.Summary.Id, _thread.Summary.Title);
            else Message(Texts.T("archive.command.busy"));
            return true;
        }
        if (!_largeReader && CommandBindings.Matches(e, "rules.edit-scope"))
        {
            e.Handled = true;
            if (CanEditThreadRules) EditThreadRulesFromReader(); else Message(Texts.T("archive.command.noWatch"));
            return true;
        }
        return false;
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
