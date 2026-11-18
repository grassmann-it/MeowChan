using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan;

// A folder file that changed on disk after it was tagged keeps its tags with the previous version (a tag belongs to the exact
// version of a file). Two places say so: a line under the selection bar for a single selected file, and the details of the tile
// ("All occurrences…"), which also offers to copy the tags to the new version (asked once, undoable).
public sealed partial class MediaPage
{
    /// <summary>The host side of the changed-folder-file hint. Null: the page says nothing about earlier versions.</summary>
    internal FolderVersionServices? FolderVersions { get; set; }

    private int _folderNoteStamp;
    private bool _folderNoteShown;

    private Control? FolderVersionNoticeOf(MediaLibraryEntry entry, Action rowChanged) =>
        FolderVersions is { } services && entry.FolderItemId != null && entry.Presence == MediaLibraryPresence.Present
            ? new FolderVersionNoticeControl(entry, services, () => { RefreshUserData(); rowChanged(); })
            : null;

    private void ClearFolderVersionNote()
    {
        _folderNoteStamp++;
        if (!_folderNoteShown) return;
        _folderNoteShown = false;
        ShowSelectionNote(null);
    }

    // The line shows only for exactly one selected tile and only while nothing else has a message there. The lookup waits for the
    // catalog and runs in the background; a selection that moved on meanwhile is ignored.
    private void UpdateFolderVersionNote(int selected)
    {
        if (FolderVersions is not { } services || selected != 1 || _selectionBusy || _lifetime.IsCancellationRequested) { ClearFolderVersionNote(); return; }
        int index = _grid.Selection.SelectedIndexes.FirstOrDefault(-1);
        if (index < 0 || index >= _tiles.Count) { ClearFolderVersionNote(); return; }
        MediaLibraryTile tile = _tiles[index];
        MediaLibraryEntry[] folderFiles = tile.Occurrences.Append(tile.Entry).Distinct()
            .Where(entry => entry.FolderItemId != null && entry.Presence == MediaLibraryPresence.Present).ToArray();
        if (folderFiles.Length == 0) { ClearFolderVersionNote(); return; }
        int stamp = ++_folderNoteStamp;
        _ = ShowFolderVersionNoteAsync(services, folderFiles, stamp);
    }

    private async Task ShowFolderVersionNoteAsync(FolderVersionServices services, MediaLibraryEntry[] files, int stamp)
    {
        bool found;
        try { found = await Task.Run(() => files.Any(entry => services.Find(entry) != null), _lifetime.Token); }
        catch (OperationCanceledException) { return; }
        if (stamp != _folderNoteStamp || _lifetime.IsCancellationRequested || _selectionBusy) return;
        if (!found) { ClearFolderVersionNote(); return; }
        // Another message (export, hashing) keeps the line; this hint never replaces it.
        if (_selectionNote.IsVisible && !_folderNoteShown) return;
        _folderNoteShown = true;
        ShowSelectionNote(Texts.T("folderSource.version.short"));
    }
}
