using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan;

// The tagging page (PT2) in the shell: a main page of MainPageOrder behind Media with a footer button (TaggingNavigationButton, a More
// entry in the footer's More menu, all declared in MainWindow.axaml) and the command navigation.tagging, whose default digit follows the footer
// numbering. The page is built when it is first opened. "Start tagging" goes through StartTaggingFromPageAsync, which starts the queue
// walk in PT3's tagging mode.
public partial class MainWindow
{
    private TaggingPage? _taggingPage;
    private IDisposable? _taggingRegistration;

    /// <summary>The tagging page once it has been opened; null before.</summary>
    internal TaggingPage? TaggingPage => _taggingPage;

    private bool CanShowTaggingPage => !_isClosing && _session.State == DesktopSessionState.Ready && !_largeReaderOpen && _gallery == null;

    private void InitializeTaggingPage()
    {
        // The More entry follows the old gating of the ⋯ entry: no tagging page while a reader or the gallery covers the workspace.
        if (ToolsNavigationButton.Flyout is { } moreFlyout)
            moreFlyout.Opened += (_, _) => SetMoreMenuItemEnabled("nav.tagging", TaggingNavigationButton.IsEnabled && CanShowTaggingPage);
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
