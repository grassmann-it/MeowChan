using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan;

// The tagging page (PT2) in the shell: an entry of the app menu's tools group (in front of "Walk through tagging queue", PT1) and the
// command navigation.tagging, no footer button. The page is built when it is first opened. "Start tagging" goes through
// StartTaggingFromPageAsync starts the queue walk in PT3's tagging mode.
public partial class MainWindow
{
    private TaggingPage? _taggingPage;
    private MenuItem? _taggingMenu;
    private IDisposable? _taggingRegistration;

    /// <summary>The tagging page once it has been opened; null before.</summary>
    internal TaggingPage? TaggingPage => _taggingPage;

    private bool CanShowTaggingPage => !_isClosing && _session.State == DesktopSessionState.Ready && !_largeReaderOpen && _gallery == null;

    private void InitializeTaggingPage()
    {
        _taggingMenu = CommandBindings.CreateMenuItem("navigation.tagging", () => ShowTaggingPage(), () => CanShowTaggingPage);
        int at = _tagQueueMenu is null ? -1 : WorkspaceMenuItem.Items.IndexOf(_tagQueueMenu);
        if (at < 0) at = WorkspaceMenuItem.Items.IndexOf(MenuPagesToolsSeparator) + 1;
        WorkspaceMenuItem.Items.Insert(at, _taggingMenu);
        WorkspaceMenuItem.SubmenuOpened += (_, _) => { if (_taggingMenu != null) CommandBindings.Refresh(_taggingMenu); };
        _workViews.Changed += (_, view) => _taggingPage?.ApplyWorkView(view);
        Closed += (_, _) =>
        {
            _taggingRegistration?.Dispose();
            _taggingPage?.End();
        };
    }

    private void RegisterTaggingPageCommands() =>
        _taggingRegistration = _shortcutRouter!.Register(new CommandRegistration("navigation.tagging", this,
            () => !_isClosing && !_largeReaderOpen && _gallery == null, () => CanShowTaggingPage, () => ShowTaggingPage()));

    private void RefreshTaggingPageLabels()
    {
        if (_taggingMenu != null) { CommandBindings.Apply(_taggingMenu, "navigation.tagging"); CommandBindings.Refresh(_taggingMenu); }
    }

    /// <summary>Opens the tagging page. False while the window cannot navigate (no restored session, busy editors, gallery or reader open).</summary>
    internal bool ShowTaggingPage()
    {
        if (!CanShowTaggingPage) return false;
        EnsureTaggingPage();
        return NavigateWorkspace(WorkspacePage.Tagging);
    }

    private void EnsureTaggingPage()
    {
        if (_taggingPage != null || _isClosing) return;
        _taggingPage = new TaggingPage(_session) { StartTagging = StartTaggingFromPageAsync, AddRequested = AddToTaggingQueueFromPage, Stamp = QuickTagStampSet };
        _taggingPage.ApplyWorkView(_workViews.Current);
        TaggingPageHost.Content = _taggingPage;
    }

    // Called by NavigateWorkspace after the page became the shown one.
    private void ActivateTaggingPage() => _ = _taggingPage?.ActivateAsync();

    /// <summary>
    /// The seam of the tagging mode: the page has already moved the queue's cursor to where the walk starts. The queue walk opens the
    /// gallery in PT3's tagging mode (decision strip, keys, resume point; MainWindow.TagQueue.cs, StartTaggingMode).
    /// </summary>
    private Task StartTaggingFromPageAsync() => OpenTagQueueGalleryAsync();

    private void AddToTaggingQueueFromPage()
    {
        if (_isClosing || !NavigateWorkspace(WorkspacePage.Media)) return;
        ShowNotification(Texts.T("queue.title"), Texts.T("tagging.page.add.how"), NotificationType.Information, operation: "tagging.add");
    }
}
