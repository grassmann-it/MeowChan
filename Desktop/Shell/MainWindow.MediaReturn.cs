using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan;

// MEDIA103-NAV P0: Medien -> "show in thread" -> archive -> "Back to media" (button or Alt+Left) -> the same Medien view.
// One transient return point per jump; a newer jump, leaving the archive or returning ends it. While a gallery is open or
// suspended for the reader, the gallery owns the way back and this one is hidden. Escape and reader.history-back are untouched.
// F5 (16.11.2026): the Tagging page's "Show in thread" has the same way back, "Back to Tagging": the same command (media.return), its
// button and Alt+Left, the same lifecycle; it selects the jumped row on the Tagging page again (ReturnToTaggingPage).
public partial class MainWindow
{
    /// <summary>Where the jump's way back goes: the Media page's view (tile, selection, scroll) or the Tagging page's row.</summary>
    private abstract record PageReturn;
    private sealed record MediaPageReturn(MediaLibraryReturnPoint Point) : PageReturn;
    private sealed record TaggingPageReturn(TagQueueKey Key) : PageReturn;

    private PageReturn? _mediaReturn;
    private int _mediaJumpRequest;

    internal bool MediaReturnAvailableForTests => _mediaReturn != null;
    internal bool TaggingReturnAvailableForTests => _mediaReturn is TaggingPageReturn;
    internal Task MediaReturnTask { get; private set; } = Task.CompletedTask;

    private async Task ShowMediaLibraryEntryInThreadAsync(MediaLibraryEntry entry)
    {
        if (_isClosing || entry.PostId == null || _mediaPage == null) return;
        MediaLibraryReturnPoint point = _mediaPage.CaptureReturnPoint(entry);
        int request = ++_mediaJumpRequest;
        // F1: a thread the archive does not hold leaves the page and the archive's loaded thread as they are.
        if (!await ArchiveHoldsJumpTargetAsync(entry.Thread, JumpStillRequested(request))) return;
        if (!NavigateWorkspace(WorkspacePage.Archive, activateArchive: false)) return;
        _mediaReturn = new MediaPageReturn(point);
        ArchivePage.MediaReturn = ReturnToMedia;
        UpdateMediaReturn();
        await ShowThreadPostInLargeReaderAsync(entry.Thread, entry.PostId, request);
    }

    /// <summary>A jump started on the page shown now still owns it: no later jump, no other page meanwhile.</summary>
    private Func<bool> JumpStillRequested(int request)
    {
        WorkspacePage page = _selectedPage;
        return () => request == _mediaJumpRequest && !_isClosing && _selectedPage == page;
    }

    /// <summary>
    /// F1 (16.11.2026): "Show in thread" checks that the archive holds the thread before it navigates, sets a return point or suspends a
    /// gallery. A missing thread opened an empty large reader ("Select an archived thread") and cleared the thread the archive showed. Now it
    /// says so and stays where it was (an open gallery shows the note itself: the shell's toasts wait while it covers the window).
    /// </summary>
    private async Task<bool> ArchiveHoldsJumpTargetAsync(ResourceId thread, Func<bool> stillRequested)
    {
        if (_archivePage is not { } archive) return true; // no archive page: the jump's own steps refuse
        bool missing = await archive.ThreadMissingAsync(thread);
        if (!stillRequested()) return false;
        if (!missing) return true;
        string message = Texts.T("archive.showInThread.missing");
        bool galleryCovers = GallerySuppressesFeedback;
        ShowNotification(Texts.T("nav.archive"), message, NotificationType.Warning,
            context: thread.Source + "/" + thread.Board + "/" + thread.Thread, operation: "archive.showInThread");
        if (galleryCovers) _gallery?.SetNotice(message);
        return false;
    }

    // "Show in thread" from the Media and the Tagging page (16.11.2026), like the gallery's: the large reader on the post. First the archive is
    // made to agree with it (the thread selected in the list, ArchivePage.ThreadReveal.cs) and the thread loaded there, then the large reader
    // opens over that archive: closing it stays on the archive with this thread selected and shown, its focus back on the list. The return
    // point of a Media jump is in the reader's header (the pane moves into the large reader with it) and Alt+Left is the window's.
    // A later jump, a return or leaving the archive meanwhile owns the page (_mediaJumpRequest): this one stops before its next step.
    private async Task ShowThreadPostInLargeReaderAsync(ResourceId thread, string postId, int request)
    {
        bool Current() => request == _mediaJumpRequest && !_isClosing && _selectedPage == WorkspacePage.Archive;
        ArchivePage archive = ArchivePage;
        ArchivePage.ThreadReveal reveal = await archive.RevealThreadInListAsync(thread);
        if (reveal.RestoreSearch is { } restore && !_isClosing) ShowRevealedSearchToast(thread, restore);
        if (!Current()) return;
        await archive.LoadThreadAsync(thread);
        if (!Current() || _gallery != null) return;
        if (!_largeReaderOpen) OpenLargeReader(archive.ThreadListFocusTarget);
        if (!_largeReaderOpen) return; // a dialog of the reader keeps it inline
        await archive.FocusPostAsync(postId);
        if (!Current() || !_largeReaderOpen) return;
        archive.EnsureLargeReaderFocus();
        // F6: a post the archived copy does not hold opens the thread at its start, and the reader says why (one note, cleared by the next jump).
        bool postHeld = archive.CurrentThread is not { } shown || shown.Summary.Id != thread || shown.FindPostIndex(postId) >= 0;
        archive.ShowReadingPositionMessage(postHeld ? string.Empty : Texts.T("archive.showInThread.postMissing"));
    }

    // Only a thread the archive's search did not find replaces that search (by its board); the toast says so and puts the previous search back.
    private void ShowRevealedSearchToast(ResourceId thread, Func<Task<bool>> restore) => ShowActionNotification(Texts.T("nav.archive"),
        Texts.T("archive.reveal.searchReplaced", ArchivePage.RevealedBoardLabel(thread)), NotificationType.Information,
        new[] { new FeedbackToastAction(Texts.T("archive.reveal.restoreSearch"), restore, "rotate-ccw") },
        context: thread.Source + "/" + thread.Board + "/" + thread.Thread, operation: "archive.reveal");

    private void ForgetMediaReturn()
    {
        if (_mediaReturn == null) return;
        _mediaReturn = null;
        ++_mediaJumpRequest;
        UpdateMediaReturn();
    }

    private void UpdateMediaReturn() => _archivePage?.ShowMediaReturn(_mediaReturn != null && !_isClosing
        && _gallery == null && _selectedPage == WorkspacePage.Archive, _mediaReturn is TaggingPageReturn ? "command.media.return.tagging" : null);

    private bool CanReturnToMedia => _mediaReturn != null && !_isClosing && _gallery == null
        && (_mediaReturn is TaggingPageReturn ? _taggingPage != null : _mediaPage != null)
        && _selectedPage == WorkspacePage.Archive && _archivePage?.IsArchiveCleanupOpen != true && _archivePage?.IsReaderDialogOpen != true;

    private void ReturnToMedia()
    {
        if (!CanReturnToMedia || _mediaReturn is not { } target) return;
        if (_largeReaderOpen) CloseLargeReader(restoreOrigin: false);
        switch (target)
        {
            case TaggingPageReturn tagging:
                ReturnToTaggingPage(tagging.Key); // the page navigation also ends the return point and any still running jump
                break;
            case MediaPageReturn media:
                if (!NavigateWorkspace(WorkspacePage.Media)) return; // also ends the return point and any still running jump
                StartMediaPageReturn(media.Point, "media.return"); // observed: a failure of the restore is logged and shown (F8)
                break;
        }
    }

    // One Back gesture (Alt+Left, Command-Left on macOS) for three "back" buttons that are never needed at the same time:
    // the previous reading position, the gallery the reader was opened from and the media page. The innermost one wins;
    // a command rebound to another gesture only answers to its own. It is resolved here, before the archive's lists see
    // it: a post list would otherwise keep the arrow. Text fields, combo boxes, sliders, menus and open popups keep the
    // key; so does an unrelated page.
    private static readonly string[] BackCommands = { "reader.history-back", "gallery.return", "media.return" };

    // F3: an open gallery owns the way back; the reader hidden under it keeps its history (a suspended gallery shows the reader again).
    private bool CanRunBack(string id) => id switch
    {
        "reader.history-back" => (_gallery == null || _gallerySuspended) && _archivePage?.CanGoHistoryBack == true,
        "gallery.return" => _archivePage?.CanReturnToGallery == true,
        _ => CanReturnToMedia
    };

    private void RunBack(string id)
    {
        switch (id)
        {
            case "reader.history-back": _archivePage?.GoHistoryBackByShortcut(); break;
            case "gallery.return": _archivePage?.ReturnToGalleryByShortcut(); break;
            default: ReturnToMedia(); break;
        }
    }

    private bool TryRouteMediaReturn(KeyEventArgs e)
    {
        if (e.Handled || _isClosing || _selectedPage != WorkspacePage.Archive || ShortcutService.IsCapturing) return false;
        string? id = BackCommands.FirstOrDefault(candidate => ShortcutService.Current.Matches(e, candidate));
        if (id == null) return false;
        if (_archivePage?.IsArchiveCleanupOpen == true || _archivePage?.IsReaderDialogOpen == true) return false;
        if (!CommandRouter.IsRoutingContextActive(this, e.Source) || OwnsArrowKeys(e.Source)) return false;
        // A rebound command is tried only for its own gesture; each of the three has its own availability.
        id = BackCommands.FirstOrDefault(candidate => ShortcutService.Current.Matches(e, candidate) && CanRunBack(candidate));
        if (id == null) return false;
        e.Handled = true;
        RunBack(id);
        return true;
    }

    private static bool OwnsArrowKeys(object? source) => CommandRouter.IsNativeInput(source, Key.None)
        || source is Visual visual && visual.GetSelfAndVisualAncestors().Any(v => v is Slider);
}
