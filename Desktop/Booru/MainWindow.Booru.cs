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

    /// <summary>
    /// Puts a checked tag query into the hosted Danbooru page's search field (opening the page like the menu entry does) and
    /// returns true, or returns false when the page cannot be reached. It only fills the field: nothing is requested until
    /// the user presses Search there.
    /// </summary>
    internal bool UseAsBooruSearch(string query)
    {
        if (!CanOpenBooru) return false;
        if (!NavigateWorkspace(WorkspacePage.Booru)) return false;
        if (_booruPage is not { } page) return false;
        page.ShowQuery(query);
        // Handoffs only prefill the real page draft. The workbench owns the Search action;
        // no provider request is scheduled by this route.
        Dispatcher.UIThread.Post(() =>
        {
            if (!_isClosing && _selectedPage == WorkspacePage.Booru && ReferenceEquals(_booruPage, page))
                page.View.FocusSearchInput();
        });
        return true;
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
