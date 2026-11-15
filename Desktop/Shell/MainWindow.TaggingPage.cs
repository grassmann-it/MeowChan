using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.MediaActions;
using MeowChan.Gallery;
using MeowChan.Tags;

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
        _taggingPage = new TaggingPage(_session)
        {
            StartTagging = StartTaggingFromPageAsync, AddRequested = AddToTaggingQueueFromPage, Stamp = QuickTagStampSet,
            // TG1: the same tag services, ranking and stamp as the media page's selection, and the passive profile data for the tag counts.
            TagServices = new(_session.GetPublishedPrivateTagSnapshot,
                (name, subjects, expectedRevision, token) => _session.DefineAndSetMediaSubjectTagsAsync(name, subjects, expectedRevision, token),
                (tag, subjects, assigned, expectedRevision, token) => _session.SetMediaSubjectTagsAsync(tag, subjects, assigned, expectedRevision, token)),
            TagPopoverOptions = TagPopoverDefaults,
            ReadBooruCatalog = _session.ReadBooruCatalog,
            ReadUserData = ReadMediaLibraryUserData,
            CompactDensity = () => _config.CompactDensity,
            OpenGallery = OpenTaggingEntriesGalleryAsync,
            ShowInThread = ShowTaggingEntryInThreadAsync,
            ShowInMedia = ShowTaggingEntryInMediaAsync,
            ShowInFolder = ShowTaggingEntryInFolderAsync,
            CopyPath = CopyTaggingEntryPathAsync,
            SaveStampBlock = state => _ = _session.SaveViewPreferencesAsync(config => config.TaggingStampBlock = state.ToString()),
            SaveViewMode = mode => _ = _session.SaveViewPreferencesAsync(config => config.TaggingPageView = mode.ToString()),
            SavePreviewColumn = visible => _ = _session.SaveViewPreferencesAsync(config => config.TaggingPreviewColumn = visible),
            ReadMediaTileAppearance = () => MediaTileMetrics.For(
                MediaPageLayout.Parse(_session.Config.MediaLayout, _session.Config.MediaBooruMode).TileSize,
                MediaPageLayout.Parse(_session.Config.MediaLayout, _session.Config.MediaBooruMode).Caption)
        };
        _taggingPage.RestoreViewMode(TaggingViewModes.Parse(_session.Config.TaggingPageView));
        _taggingPage.RestorePreviewColumn(_session.Config.TaggingPreviewColumn);
        _taggingPage.RestoreStampBlock(TaggingStampBlocks.Parse(_session.Config.TaggingStampBlock));
        _taggingPage.ApplyWorkView(_workViews.Current);
        TaggingPageHost.Content = _taggingPage;
    }

    // Called by NavigateWorkspace after the page became the shown one.
    private void ActivateTaggingPage() => _ = _taggingPage?.ActivateAsync();

    /// <summary>
    /// The seam of the tagging mode: the page has already moved the queue's cursor to where the walk starts. The queue walk opens the
    /// gallery in PT3's tagging mode (decision strip, keys, resume point; MainWindow.TagQueue.cs, StartTaggingMode).
    /// </summary>
    private Task StartTaggingFromPageAsync() => OpenTagQueueGalleryAsync(_taggingPage?.SelectedGroupId, _taggingPage?.UngroupedGroupSelected == true);

    private void AddToTaggingQueueFromPage()
    {
        if (_isClosing || !NavigateWorkspace(WorkspacePage.Media)) return;
        ShowNotification(Texts.T("queue.title"), Texts.T("tagging.page.add.how"), NotificationType.Information, operation: "tagging.add");
    }

    // ---- the commands of the page that leave it (TG1): each resolves the queue entry to its saved file like the media page's menu ----

    private bool TaggingEntryCurrent() => !_isClosing && _taggingPage is { IsEffectivelyVisible: true } && _gallery == null;

    private async Task<GalleryEntry?> ResolveTaggingEntryAsync(TagQueueItem item)
    {
        GalleryEntry? entry = await MediaMenuIdentity.ResolveAsync(item.ToLibraryEntry(), _session.OpenArchiveReaderAsync,
            Storage.MediaAssetReader.OpenAsync, TaggingEntryCurrent, CancellationToken.None, _session.ReadBooruCatalog, _session.ReadManualImportCatalog);
        if (entry == null && TaggingEntryCurrent())
            ShowNotification(Texts.T("notification.error"), Texts.T("media.galleryFailed"), NotificationType.Warning, operation: "tagging.menu");
        return entry;
    }

    /// <summary>"Open in gallery": the gallery over the given entries (the page picks the selection or the shown list), beginning at <paramref name="start"/>.</summary>
    private async Task OpenTaggingEntriesGalleryAsync(System.Collections.Generic.IReadOnlyList<TagQueueItem> items, int start)
    {
        if (!TaggingEntryCurrent() || start < 0 || start >= items.Count) return;
        // From the chosen entry on (then the ones before it), at most the walk window: a long list opens as quickly as the queue walk does.
        TagQueueItem[] window = items.Skip(start).Concat(items.Take(start)).Take(_session.Config.TagQueueWalkWindow).ToArray();
        MediaLibraryEntry[] indexed = window.Select(item => item.ToLibraryEntry()).ToArray();
        try
        {
            System.Collections.Generic.IReadOnlyList<GalleryEntry> entries = await MediaLibraryGallery.ResolveAsync(indexed, _session.OpenArchiveReaderAsync,
                Storage.MediaAssetReader.OpenAsync, readBooruCatalog: _session.ReadBooruCatalog, readImportCatalog: _session.ReadManualImportCatalog);
            if (!TaggingEntryCurrent()) return;
            GalleryEntry? first = entries.FirstOrDefault(entry => MediaMenuIdentity.Matches(indexed[0], entry)) ?? entries.FirstOrDefault();
            if (first == null)
            {
                ShowNotification(Texts.T("notification.error"), Texts.T("media.galleryFailed"), NotificationType.Warning, operation: "tagging.menu");
                return;
            }
            await OpenMediaGalleryAsync(entries, first.EntryKey, _taggingPage, stillRequested: () => !_isClosing && _gallery == null);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            if (TaggingEntryCurrent()) ShowNotification(Texts.T("notification.error"), Texts.T("media.galleryFailed"), NotificationType.Error, operation: "tagging.menu");
        }
    }

    // The post in its thread (the archive reader); the media page's return point does not apply, the way back is the page navigation.
    private async Task ShowTaggingEntryInThreadAsync(TagQueueItem item)
    {
        if (_isClosing || item.PostId == null) return;
        ForgetMediaReturn();
        int request = ++_mediaJumpRequest;
        if (!NavigateWorkspace(WorkspacePage.Archive, activateArchive: false)) return;
        await ArchivePage.LoadThreadAsync(item.Thread);
        if (request != _mediaJumpRequest || _isClosing || _selectedPage != WorkspacePage.Archive) return;
        await ArchivePage.FocusPostAsync(item.PostId);
    }

    // The media page with the entry's tile selected; a filter that hides it is cleared by the page's own exact-focus rule or says so.
    private async Task ShowTaggingEntryInMediaAsync(TagQueueItem item)
    {
        if (_isClosing || !NavigateWorkspace(WorkspacePage.Media) || _mediaPage is not { } page) return;
        await page.ActivateAsync();
        await page.FocusExactEntryAsync(entry => TagQueueItem.From(entry, DateTimeOffset.UtcNow)?.Key == item.Key,
            Texts.T("tagging.page.showInMedia.missing"));
    }

    private async Task ShowTaggingEntryInFolderAsync(TagQueueItem item)
    {
        if (await ResolveTaggingEntryAsync(item) is { } entry && Path.GetDirectoryName(entry.Asset.Path) is string directory) OpenDirectory(directory);
    }

    private async Task CopyTaggingEntryPathAsync(TagQueueItem item)
    {
        if (await ResolveTaggingEntryAsync(item) is { } entry) await CopyMediaPathAsync(entry);
    }
}
