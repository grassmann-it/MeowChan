using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.FolderSources;
using MeowChan.Gallery;

namespace MeowChan;

// PR2/PR3 (read-only folder sources): the window's side. New files and new digests reach the media page as an index rebuild
// (at most every MediaRebuildInterval while hashing runs), and the Sources page refreshes its folder list.
public partial class MainWindow
{
    /// <summary>Fixed: at most this often does folder hashing rebuild the shown media index.</summary>
    internal static readonly TimeSpan FolderMediaRebuildInterval = TimeSpan.FromSeconds(15);
    private DateTime _folderMediaRebuiltAt;
    private bool _folderMediaRebuildQueued;

    private void InitializeFolderSources()
    {
        _session.FolderSourcesChanged += OnFolderSourcesChanged;
        Closed += (_, _) => _session.FolderSourcesChanged -= OnFolderSourcesChanged;
        // Background hashing pauses while the person uses the window (keys, clicks, scrolling) and resumes when they are idle.
        AddHandler(KeyDownEvent, (_, _) => _session.NoteFolderSourceActivity(), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerPressedEvent, (_, _) => _session.NoteFolderSourceActivity(), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerWheelChangedEvent, (_, _) => _session.NoteFolderSourceActivity(), RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private FolderSourcePanelServices CreateFolderSourceServices() => new(
        _session.ListFolderSources,
        () => _session.FolderSourceActivity,
        _session.FolderSourcesBlocked,
        new FolderSourceDialogServices(_session.CheckFolderSource,
            (folder, name, subfolders) => _session.AddFolderSourceAsync(folder, name, subfolders),
            (root, name) => _session.RenameFolderSourceAsync(root, name)),
        async root => await _session.RescanFolderSourceAsync(root),
        root => _session.RemoveFolderSourceAsync(root),
        () => _session.FolderHashingPaused,
        paused => _session.FolderHashingPaused = paused);

    private void OnFolderSourcesChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (_isClosing) return;
        _sourcesPage?.RefreshFolderSources();
        QueueFolderMediaRebuild();
    });

    private void QueueFolderMediaRebuild()
    {
        if (_isClosing || _mediaPage is not { } page || _selectedPage != WorkspacePage.Media || _folderMediaRebuildQueued) return;
        TimeSpan wait = FolderMediaRebuildInterval - (DateTime.UtcNow - _folderMediaRebuiltAt);
        if (wait <= TimeSpan.Zero && page.BuildTask.IsCompleted)
        {
            _folderMediaRebuiltAt = DateTime.UtcNow;
            _ = page.RebuildAsync();
            return;
        }
        _folderMediaRebuildQueued = true;
        DispatcherTimer.RunOnce(() =>
        {
            _folderMediaRebuildQueued = false;
            QueueFolderMediaRebuild();
        }, wait > TimeSpan.Zero ? wait : TimeSpan.FromSeconds(1));
    }

    // "Tag together" on folder files: the unhashed ones are hashed first (a user action, any size); the rest keep their entry.
    private async Task<IReadOnlyDictionary<MediaLibraryEntry, MediaLibraryEntry>> HashFolderFilesForActionAsync(IReadOnlyList<MediaLibraryEntry> entries,
        CancellationToken token)
    {
        Dictionary<MediaLibraryEntry, MediaLibraryEntry> hashed = new();
        foreach (MediaLibraryEntry entry in entries)
        {
            token.ThrowIfCancellationRequested();
            if (entry.FolderItemId is not { } id || entry.Sha256 != null || entry.Presence != MediaLibraryPresence.Present
                || !FolderSourceIdentity.TryParseId(id, out Guid item)) continue;
            try
            {
                FolderSourceHashResult result = await _session.HashFolderItemAsync(item, FolderSourceHashPriority.UserAction, token);
                if (result is { Outcome: FolderSourceHashOutcome.Hashed or FolderSourceHashOutcome.AlreadyHashed, Item: { Sha256: { } sha } current }
                    && current.Length == entry.Length)
                    hashed[entry] = entry with { Sha256 = sha };
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ObjectDisposedException
                or Microsoft.Data.Sqlite.SqliteException) { /* This file stays untaggable for now. */ }
        }
        return hashed;
    }
}
