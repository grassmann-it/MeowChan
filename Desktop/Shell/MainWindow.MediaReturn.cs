using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;
using MeowChan.Gallery;

namespace MeowChan;

// MEDIA103-NAV P0: Medien -> "show in thread" -> archive -> "Back to media" (button or Alt+Left) -> the same Medien view.
// One transient return point per jump; a newer jump, leaving the archive or returning ends it. While a gallery is open or
// suspended for the reader, the gallery owns the way back and this one is hidden. Escape and reader.history-back are untouched.
public partial class MainWindow
{
    private MediaLibraryReturnPoint? _mediaReturn;
    private int _mediaJumpRequest;

    internal bool MediaReturnAvailableForTests => _mediaReturn != null;
    internal Task MediaReturnTask { get; private set; } = Task.CompletedTask;

    private async Task ShowMediaLibraryEntryInThreadAsync(MediaLibraryEntry entry)
    {
        if (_isClosing || entry.PostId == null || _mediaPage == null) return;
        MediaLibraryReturnPoint point = _mediaPage.CaptureReturnPoint(entry);
        int request = ++_mediaJumpRequest;
        if (!NavigateWorkspace(WorkspacePage.Archive, activateArchive: false)) return;
        _mediaReturn = point;
        ArchivePage.MediaReturn = ReturnToMedia;
        UpdateMediaReturn();
        await ArchivePage.LoadThreadAsync(entry.Thread);
        // A later jump or a return meanwhile owns the archive now; this one must not move its focus.
        if (request != _mediaJumpRequest || _isClosing || _selectedPage != WorkspacePage.Archive) return;
        await ArchivePage.FocusPostAsync(entry.PostId);
    }

    private void ForgetMediaReturn()
    {
        if (_mediaReturn == null) return;
        _mediaReturn = null;
        ++_mediaJumpRequest;
        UpdateMediaReturn();
    }

    private void UpdateMediaReturn() => _archivePage?.ShowMediaReturn(_mediaReturn != null && !_isClosing
        && _gallery == null && _selectedPage == WorkspacePage.Archive);

    private bool CanReturnToMedia => _mediaReturn != null && _mediaPage != null && !_isClosing && _gallery == null
        && _selectedPage == WorkspacePage.Archive && _archivePage?.IsArchiveCleanupOpen != true && _archivePage?.IsReaderDialogOpen != true;

    private void ReturnToMedia()
    {
        if (!CanReturnToMedia || _mediaReturn is not { } point) return;
        if (_largeReaderOpen) CloseLargeReader(restoreOrigin: false);
        if (!NavigateWorkspace(WorkspacePage.Media)) return; // also ends the return point and any still running jump
        MediaReturnTask = _mediaPage!.ReturnToAsync(point);
    }

    // One Back gesture (Alt+Left, Command-Left on macOS) for three "back" buttons that are never needed at the same time:
    // the previous reading position, the gallery the reader was opened from and the media page. The innermost one wins;
    // a command rebound to another gesture only answers to its own. It is resolved here, before the archive's lists see
    // it: a post list would otherwise keep the arrow. Text fields, combo boxes, sliders, menus and open popups keep the
    // key; so does an unrelated page.
    private static readonly string[] BackCommands = { "reader.history-back", "gallery.return", "media.return" };

    private bool CanRunBack(string id) => id switch
    {
        "reader.history-back" => _archivePage?.CanGoHistoryBack == true,
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
