using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Controls.Notifications;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Commands;

namespace MeowChan;

// The Danbooru workbench entry of the Workspace menu (command "booru.open"). InitializeBooru() runs from
// MainWindow.Navigation.cs; the entry is visible only while DesktopSessionHost.BooruServices is set, i.e. while
// the Danbooru plugin is delivered, enabled and composed for this profile.
public partial class MainWindow
{
    private MenuItem? _booruMenu;

    private bool CanOpenBooru => !_isClosing && _config.ShowBooruTools
        && _session.State == DesktopSessionState.Ready && _session.BooruServices != null;

    private void InitializeBooru()
    {
        // Machine codes inside the page's sentences use the shared Booru message catalog.
        BooruText.CodeText = BooruCodeTexts.Localized;
        _booruMenu = CommandBindings.CreateMenuItem("booru.open", ShowBooruWorkbench, () => CanOpenBooru);
        _booruMenu.IsVisible = CanOpenBooru;
        int at = _postBookmarksMenu is null ? -1 : WorkspaceMenuItem.Items.IndexOf(_postBookmarksMenu);
        WorkspaceMenuItem.Items.Insert(at >= 0 ? at + 1 : WorkspaceMenuItem.Items.Count, _booruMenu);
        // The entry exists only while ports are connected; checked when the menu opens.
        WorkspaceMenuItem.SubmenuOpened += (_, _) =>
        {
            _booruMenu.IsVisible = CanOpenBooru;
            CommandBindings.Refresh(_booruMenu);
        };
        // One quiet information notification per finished automatic check that queued new originals.
        _session.BooruWatcherQueued += OnBooruWatcherQueued;
        _session.BooruChanged += OnBooruChanged;
        InitializeBooruMedia();
        Closed += (_, _) =>
        {
            _session.BooruWatcherQueued -= OnBooruWatcherQueued;
            _session.BooruChanged -= OnBooruChanged;
            _booruAddLifetime.Cancel();
        };
    }

    internal bool UseAsBooruSearch(string query) => OpenBooruSearchDraft(query).Opened;

    private (bool Opened, int? Limit) OpenBooruSearchDraft(string query, BooruWorkbenchServices? source = null)
    {
        if (!CanOpenBooru || !NavigateWorkspace(WorkspacePage.Booru) || _booruPage is not { } page)
            return default;
        return OpenBooruSearchDraft(page, query, source);
    }

    // Source tags and addresses hand over an editable query. The user submits it from the workbench.
    private (bool Opened, int? Limit) OpenBooruSearchDraft(BooruPage page, string query, BooruWorkbenchServices? source = null)
    {
        if (!page.View.TryShowQueryInNewTab(query, source?.Search)) return (false, page.View.SearchTabs.Limit);
        Dispatcher.UIThread.Post(() =>
        {
            if (CanOpenBooru && _selectedPage == WorkspacePage.Booru && ReferenceEquals(_booruPage, page)
                && ReferenceEquals(_session.BooruServices, page.Services))
                page.View.FocusSearchInput();
        });
        return (true, null);
    }

    // Uses the shell's ordinary notification path, so the user's "information notifications" setting applies.
    // Failures, cooldowns and checks that found nothing never come here, and a check started on the page reports there.
    private void OnBooruWatcherQueued(BooruWatcherCheckNotice notice)
    {
        if (notice.Queued <= 0) return;
        ShowBooruWatcherQueued(notice);
    }

    internal void ShowBooruWatcherQueued(BooruWatcherCheckNotice notice) =>
        ShowNotification(Texts.T("booru.ui.notify.queued.title"), Texts.T("booru.ui.notify.queued", notice.Query, notice.Queued),
            NotificationType.Information, context: notice.WatcherId, operation: "booru.watcher.queued");

    private void ShowBooruWorkbench()
    {
        if (CanOpenBooru) NavigateWorkspace(WorkspacePage.Booru);
    }
}
