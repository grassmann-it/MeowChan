using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan;

// The tagging queue in the shell: "Add to tagging queue" from the gallery, and the walk-through that opens the gallery over the
// open entries of the queue, starting where the last walk stopped, in the tagging mode (PT3: decision strip, tag panel, one undo).
// Nothing here assigns a tag, favorite or rating by itself.
public partial class MainWindow
{
    /// <summary>How many open entries one walk opens at once (the setting <c>TagQueueWalkWindow</c>); the next walk continues behind them.</summary>
    internal int TagQueueWalkWindow => TaggingSettings.WalkWindow(_config);

    private MenuItem? _tagQueueMenu;
    private System.IDisposable? _tagQueueRegistration;
    private bool _tagQueueOpening;

    private bool CanOpenTagQueue => !_isClosing && !_tagQueueOpening && _session.State == DesktopSessionState.Ready && !_largeReaderOpen && _gallery == null;

    private void InitializeTagQueue()
    {
        _tagQueueMenu = CommandBindings.CreateMenuItem("media.queue.open", () => _ = OpenTagQueueGalleryAsync(), () => CanOpenTagQueue);
        int anchor = _postBookmarksMenu is null ? -1 : WorkspaceMenuItem.Items.IndexOf(_postBookmarksMenu);
        if (anchor < 0) anchor = WorkspaceMenuItem.Items.IndexOf(MenuPagesToolsSeparator);
        WorkspaceMenuItem.Items.Insert(anchor + 1, _tagQueueMenu);
        Closed += (_, _) => _tagQueueRegistration?.Dispose();
    }

    private void RegisterTagQueueCommands() =>
        _tagQueueRegistration = _shortcutRouter!.Register(new CommandRegistration("media.queue.open", this,
            () => !_isClosing && !_largeReaderOpen && _gallery == null, () => CanOpenTagQueue, () => _ = OpenTagQueueGalleryAsync()));

    private void RefreshTagQueueLabels()
    {
        if (_tagQueueMenu != null) { CommandBindings.Apply(_tagQueueMenu, "media.queue.open"); CommandBindings.Refresh(_tagQueueMenu); }
    }

    private async Task AddGalleryEntryToTagQueueAsync(GalleryEntry entry)
    {
        if (_isClosing) return;
        string message = await TagQueueActions.AddAsync(_session, new[] { entry });
        if (!_isClosing) ShowNotification(Texts.T("queue.title"), message, NotificationType.Information, operation: "media.queue.add");
    }

    /// <summary>
    /// Opens the gallery over the open entries of the queue, beginning at the entry "continue" resumes from. An entry whose file is gone
    /// is not shown but stays in the queue. When nothing can be walked a sentence says why.
    /// </summary>
    internal async Task OpenTagQueueGalleryAsync()
    {
        if (!CanOpenTagQueue) return;
        _tagQueueOpening = true;
        try
        {
            TagQueueSnapshot queue;
            try { queue = await _session.LoadTagQueueAsync(); }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
            {
                if (!_isClosing) ShowNotification(Texts.T("queue.title"), TagQueueActions.DescribeFailure(error), NotificationType.Warning, operation: "media.queue.open");
                return;
            }
            if (_isClosing || _gallery != null) return;
            if (queue.ResumeIndex() is not { } resume)
            {
                ShowNotification(Texts.T("queue.title"), Texts.T(queue.Count == 0 ? "queue.empty" : "queue.allDone"), NotificationType.Information, operation: "media.queue.open");
                return;
            }
            // From the resume point to the end, then the open entries before it, so a wrapped walk still reaches everything.
            TagQueueItem[] walk = queue.Items.Skip(resume).Concat(queue.Items.Take(resume))
                .Where(item => item.State == TagQueueState.Open).Take(TagQueueWalkWindow).ToArray();
            MediaLibraryEntry[] indexed = walk.Select(item => item.ToLibraryEntry()).ToArray();
            IReadOnlyList<GalleryEntry> entries = await MediaLibraryGallery.ResolveAsync(indexed, _session.OpenArchiveReaderAsync,
                Storage.MediaAssetReader.OpenAsync, readBooruCatalog: _session.ReadBooruCatalog, readImportCatalog: _session.ReadManualImportCatalog);
            if (_isClosing || _gallery != null) return;
            GalleryEntry? first = entries.FirstOrDefault(entry => MediaMenuIdentity.Matches(indexed[0], entry))
                ?? entries.FirstOrDefault();
            if (first == null)
            {
                ShowNotification(Texts.T("queue.title"), Texts.T("queue.unavailable"), NotificationType.Warning, operation: "media.queue.open");
                return;
            }
            await OpenTaggingWalkAsync(queue, walk, entries, first.EntryKey);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            if (!_isClosing) ShowNotification(Texts.T("queue.title"), Texts.T("media.galleryFailed"), NotificationType.Warning, operation: "media.queue.open");
        }
        finally { _tagQueueOpening = false; }
    }

    // ---- the tagging mode (PT3) --------------------------------------------------------------------------------------

    // The walk of the open gallery, or null when the gallery shows anything else.
    private TaggingWalk? _galleryTagging;
    // G1: the walk of a gallery the person switched to View: kept (with its undo steps) so Tag continues it on the same list.
    private TaggingWalk? _galleryTaggingParked;
    // The list the walk was started over; Tag resumes only while the gallery still shows exactly this list.
    private IReadOnlyList<GalleryEntry>? _galleryTaggingList;

    /// <summary>The walk of the open gallery (tests and smokes); null outside the tagging mode.</summary>
    internal TaggingWalk? GalleryTagging => _galleryTagging;

    /// <summary>
    /// Opens the gallery in the tagging mode over <paramref name="entries"/>, the resolved media of the queue entries <paramref name="walk"/>
    /// in the same order (the menu's walk-through, and the smoke over its own fixture). The walk pairs every shown medium with its queue
    /// entry; the open ones the resolver could not find stay open in the queue and are counted in the first status line.
    /// </summary>
    internal async Task OpenTaggingWalkAsync(TagQueueSnapshot queue, IReadOnlyList<TagQueueItem> walk, IReadOnlyList<GalleryEntry> entries,
        GalleryEntryKey start)
    {
        (IReadOnlyList<TaggingWalkItem> items, int unavailable) = TaggingWalk.Pair(walk, entries);
        TaggingWalk tagging = new(items, unavailable, queue, new HostTaggingQueueWriter(_session), TaggingSettings.UndoDepth(_config));
        await OpenMediaGalleryAsync(entries, start, stillRequested: () => !_isClosing && _gallery == null);
        StartGalleryTagging(tagging, entries);
    }

    // Starts the mode on the gallery that now shows exactly the walk's list; otherwise (closed meanwhile, another list) the walk ends unused.
    private void StartGalleryTagging(TaggingWalk walk, IReadOnlyList<GalleryEntry> entries)
    {
        if (_isClosing || _gallery == null || _galleryEntries.Count != entries.Count
            || !_galleryEntries.Select(entry => entry.EntryKey).SequenceEqual(entries.Select(entry => entry.EntryKey)))
        {
            walk.Dispose();
            return;
        }
        EndGalleryTagging(persist: false);
        _galleryTagging = walk;
        _galleryTaggingList = entries;
        _gallery.SetTaggingAvailable(true); // the switch's Tag half can come back to this walk after View
        _gallery.StartTaggingMode(walk, _galleryTagPanelOwner ??= new GalleryTagPanelOwner(this));
        UpdateGalleryPosition();
    }

    /// <summary>
    /// G1: the gallery's mode switch ("View | Tag", Shift+T). View ends the tagging mode on the same list and keeps the walk (its undo steps
    /// and the resume point, which moves to the shown medium as on close); Tag continues that walk. A list that is not a walk of the tagging
    /// queue cannot be tagged here: the viewer says why and never asks.
    /// </summary>
    private void SwitchGalleryTagging(bool tag)
    {
        if (_isClosing || _gallery == null) return;
        if (!tag)
        {
            if (_galleryTagging is not { } walk) return;
            PersistTaggingResume(walk);
            _galleryTagging = null;
            _gallery.EndTaggingMode();
            _galleryTaggingParked = walk;
            UpdateGalleryPosition();
            return;
        }
        if (_galleryTagging != null || _galleryTaggingParked is not { } parked || _galleryTaggingList is not { } list) return;
        _galleryTaggingParked = null;
        StartGalleryTagging(parked, list); // disposes the walk and leaves View when the list changed meanwhile
        if (_galleryTagging == null) _gallery?.SetTaggingAvailable(false);
    }

    /// <summary>
    /// Ends the mode with its gallery. The resume point moves to the shown medium when it is still open, so "Walk through tagging queue"
    /// continues there after a restart; a decided medium needs no move (the next walk starts at the next open entry anyway).
    /// Returns the queue entry of the medium that was shown last (open or decided), or null outside the mode: the way back to the
    /// Tagging page marks that row (QW6).
    /// </summary>
    private TagQueueKey? EndGalleryTagging(bool persist)
    {
        // A walk parked by the mode switch (View) ends with its gallery like the running one.
        TaggingWalk? walk = _galleryTagging ?? _galleryTaggingParked;
        bool running = _galleryTagging != null;
        _galleryTaggingParked = null;
        _galleryTaggingList = null;
        if (walk == null) return null;
        _galleryTagging = null;
        TagQueueKey? lastShown = ShownTaggingKey(walk);
        if (running) _gallery?.EndTaggingMode();
        _gallery?.SetTaggingAvailable(false);
        walk.Dispose();
        if (persist) PersistTaggingResume(walk);
        return lastShown;
    }

    // The queue entry of the medium shown now, or null when it is not part of the walk.
    private TagQueueKey? ShownTaggingKey(TaggingWalk walk)
    {
        GalleryEntry? shown = _galleryIndex >= 0 && _galleryIndex < _galleryEntries.Count ? _galleryEntries[_galleryIndex] : null;
        int index = shown == null ? -1 : walk.IndexOf(shown.EntryKey);
        return index < 0 ? null : walk.Items[index].Key;
    }

    // The resume point moves to the shown medium when it is still open, so the next walk continues there.
    private void PersistTaggingResume(TaggingWalk walk)
    {
        GalleryEntry? shown = _galleryIndex >= 0 && _galleryIndex < _galleryEntries.Count ? _galleryEntries[_galleryIndex] : null;
        int index = shown == null ? -1 : walk.IndexOf(shown.EntryKey);
        if (_isClosing || index < 0 || walk.StateOf(index) != TagQueueState.Open) return;
        int position = walk.Snapshot.IndexOf(walk.Items[index].Key);
        if (position < 0 || position == walk.Snapshot.Cursor) return;
        _ = PersistTagQueueCursorAsync(position);
    }

    /// <summary>
    /// QW6: the way back of a gallery that was opened over the Tagging page ("Back to Tagging page", Esc): the page is the shown one
    /// again (it still is unless the person navigated away meanwhile) with the row of the medium shown last selected and in view.
    /// </summary>
    private void ReturnToTaggingPage(TagQueueKey? lastShown)
    {
        if (_isClosing || _gallery != null) return;
        if (_selectedPage != WorkspacePage.Tagging && !ShowTaggingPage()) return;
        if (_taggingPage == null) return;
        if (lastShown is { } key) _taggingPage.SelectKey(key);
        else _taggingPage.FocusContent();
    }

    private async Task PersistTagQueueCursorAsync(int position)
    {
        try { await _session.SetTagQueueCursorAsync(position); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            // The resume point is a convenience: the next walk then starts at the first open entry after the old one.
            if (!_isClosing) ShowNotification(Texts.T("queue.title"), TagQueueActions.DescribeFailure(error), NotificationType.Warning, operation: "media.queue.cursor");
        }
    }

    /// <summary>"Queue 12 of 200" for the gallery header while the tagging mode shows a medium of its walk; null otherwise.</summary>
    private string? TaggingPositionText(GalleryEntry entry) =>
        _galleryTagging is { } walk && walk.IndexOf(entry.EntryKey) is var index and >= 0 ? TaggingModeText.Position(walk, index) : null;
}
