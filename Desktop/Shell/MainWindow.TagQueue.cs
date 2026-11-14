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
// open entries of the queue, starting where the last walk stopped. Nothing here assigns a tag, favorite or rating.
public partial class MainWindow
{
    /// <summary>How many open entries one walk opens at once; the next walk continues behind them.</summary>
    internal const int TagQueueWalkWindow = 200;

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
            await OpenMediaGalleryAsync(entries, first.EntryKey, stillRequested: () => !_isClosing && _gallery == null);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            if (!_isClosing) ShowNotification(Texts.T("queue.title"), Texts.T("media.galleryFailed"), NotificationType.Warning, operation: "media.queue.open");
        }
        finally { _tagQueueOpening = false; }
    }
}
