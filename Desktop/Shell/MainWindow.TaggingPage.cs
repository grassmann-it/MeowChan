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
// numbering. The page is built when it is first opened. "Start tagging" goes through OpenTagQueueGalleryAsync, which starts the queue
// walk in PT3's tagging mode.
public partial class MainWindow
{
    private TaggingPage? _taggingPage;
    private int _taggingPageVisit;

    internal Func<string, CancellationToken, Task<Storage.MediaAssetSnapshot>> TaggingGalleryOpenManifest { get; set; } =
        Storage.MediaAssetReader.OpenAsync;

    /// <summary>The tagging page once it has been opened; null before.</summary>
    internal TaggingPage? TaggingPage => _taggingPage;

    private bool CanShowTaggingPage => !_isClosing && _session.State == DesktopSessionState.Ready && !_largeReaderOpen && _gallery == null;

    private void InitializeTaggingPage()
    {
        // The More entry follows the old gating of the ⋯ entry: no tagging page while a reader or the gallery covers the workspace.
        if (ToolsNavigationButton.Flyout is { } moreFlyout)
            moreFlyout.Opened += (_, _) => SetMoreMenuItemEnabled("nav.tagging", TaggingNavigationButton.IsEnabled && CanShowTaggingPage);
        _workViews.Changed += (_, view) => _taggingPage?.ApplyWorkView(view);
        // The page's key (navigation.tagging) is a page chord of the shell key handler (OnShellKeyDown, ShellPageCommand), as every other main page:
        // it closes the gallery or the large reader and builds the page on its first opening (ShowTaggingPage).
        Closed += (_, _) => _taggingPage?.End();
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
        _taggingPage = new TaggingPage(_session)
        {
            StartTagging = OpenTagQueueGalleryAsync, AddRequested = AddToTaggingQueueFromPage, Stamp = QuickTagStampSet,
            // TG1: the same tag services, ranking and stamp as the media page's selection, and the passive profile data for the tag counts.
            TagServices = new(_session.GetPublishedPrivateTagSnapshot,
                (name, subjects, expectedRevision, token) => _session.DefineAndSetMediaSubjectTagsAsync(name, subjects, expectedRevision, token),
                (tag, subjects, assigned, expectedRevision, token) => _session.SetMediaSubjectTagsAsync(tag, subjects, assigned, expectedRevision, token)),
            TagPopoverOptions = TagPopoverDefaults,
            // R ("Rate and tag…"): the stars of the selected entries ride on top of the tag field, as on the media page.
            DecisionForEntries = DecisionForMediaEntries,
            ReadBooruCatalog = _session.ReadBooruCatalog,
            ReadUserData = ReadMediaLibraryUserData,
            ReadIndexEntries = () => _mediaPage?.Snapshot?.Entries,
            CompactDensity = () => _config.CompactDensity,
            OpenGallery = OpenTaggingEntriesGalleryAsync,
            ShowInThread = ShowTaggingEntryInThreadAsync,
            ShowInMedia = ShowTaggingEntryInMediaAsync,
            ShowInFolder = ShowTaggingEntryInFolderAsync,
            CopyPath = CopyTaggingEntryPathAsync,
            SaveViewMode = mode => _ = _session.SaveViewPreferencesAsync(config => config.TaggingPageView = mode.ToString()),
            SavePreviewColumn = visible => _ = _session.SaveViewPreferencesAsync(config => config.TaggingPreviewColumn = visible),
            ReadMediaTileAppearance = () =>
            {
                MediaPageLayout layout = CurrentMediaLayout();
                return MediaTileMetrics.For(layout.TileSize, layout.Caption);
            },
            ReadMediaTileSize = () => CurrentMediaLayout().TileSize,
            ChooseMediaTileSize = ChooseSharedTileSize
        };
        _taggingPage.RestoreViewMode(TaggingViewModes.Parse(_session.Config.TaggingPageView));
        _taggingPage.RestorePreviewColumn(_session.Config.TaggingPreviewColumn);
        _taggingPage.ApplyWorkView(_workViews.Current);
        TaggingPageHost.Content = _taggingPage;
    }

    // The tagging grid and the media page show one tile size, the media page's setting (inside MediaLayout). The media page, once it exists, holds the
    // live layout (a change is shown there before the saved settings confirm it); before that the size chosen on the tagging grid stands in for the
    // settings that are still being written.
    private MediaTileSize? _tileSizeChosenInTagging;

    private MediaPageLayout CurrentMediaLayout()
    {
        MediaPageLayout saved = _mediaPage?.PageLayout ?? MediaPageLayout.Parse(_session.Config.MediaLayout, _session.Config.MediaBooruMode);
        return _mediaPage == null && _tileSizeChosenInTagging is { } size ? saved with { TileSize = size } : saved;
    }

    // Ctrl+wheel and Ctrl+Plus / Minus / 0 over the tagging grid: the media page's tile size changes (it shows it at once when it exists) and is
    // saved into the media layout; the rest of that layout stays as it is.
    private bool ChooseSharedTileSize(MediaTileSize size)
    {
        if (_isClosing) return false;
        _tileSizeChosenInTagging = size;
        _mediaPage?.ApplySavedTileSize(size);
        _ = _session.SaveViewPreferencesAsync(config =>
            config.MediaLayout = (MediaPageLayout.Parse(config.MediaLayout, config.MediaBooruMode) with { TileSize = size }).Format());
        return true;
    }

    // Called by NavigateWorkspace after the page became the shown one.
    private void ActivateTaggingPage() => _ = _taggingPage?.ActivateAsync();

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
        TaggingPage? page = _taggingPage;
        int visit = _taggingPageVisit;
        bool Current() => _selectedPage == WorkspacePage.Tagging && visit == _taggingPageVisit && TaggingEntryCurrent();
        if (page == null || !Current() || start < 0 || start >= items.Count) return;
        MediaLibraryEntry chosen = items[start].ToLibraryEntry();
        // From the chosen entry on (then the ones before it), at most the walk window: a long list opens as quickly as the queue walk does.
        MediaLibraryEntry[] indexed = items.Skip(start).Concat(items.Take(start)).Take(TagQueueWalkWindow)
            .Select(item => item.ToLibraryEntry()).ToArray();
        try
        {
            System.Collections.Generic.IReadOnlyList<GalleryEntry> entries = await MediaLibraryGallery.ResolveAsync(indexed, _session.OpenArchiveReaderAsync,
                TaggingGalleryOpenManifest, readBooruCatalog: _session.ReadBooruCatalog, readImportCatalog: _session.ReadManualImportCatalog);
            if (!Current()) return;
            // Older queues lack a binding revision. The resolver still pins the saved asset and its post occurrence.
            GalleryEntry? first = entries.FirstOrDefault(entry => MediaMenuIdentity.Matches(chosen.AttachmentRevisionKey == null
                ? chosen with { AttachmentRevisionKey = entry.AttachmentResolution?.Binding?.Reference.RevisionKey }
                : chosen, entry));
            if (first == null || !File.Exists(first.Asset.Path))
            {
                ShowNotification(Texts.T("notification.error"), Texts.T("tagging.page.openGallery.missing"), NotificationType.Warning, operation: "tagging.menu");
                return;
            }
            await OpenMediaGalleryAsync(entries, first.EntryKey, page, stillRequested: Current);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            if (Current()) ShowNotification(Texts.T("notification.error"), Texts.T("media.galleryFailed"), NotificationType.Error, operation: "tagging.menu");
        }
    }

    // The post in its thread (the archive reader), as the Media page's "Show in thread". F5 (16.11.2026): with its own way back, "Back to
    // Tagging" (the reader's header and Alt+Left, MainWindow.MediaReturn.cs), which selects this row on the Tagging page again.
    private async Task ShowTaggingEntryInThreadAsync(TagQueueItem item)
    {
        if (_isClosing || item.PostId == null) return;
        ForgetMediaReturn();
        int request = ++_mediaJumpRequest;
        // F1: a thread the archive does not hold leaves the Tagging page and the archive's loaded thread as they are.
        if (!await ArchiveHoldsJumpTargetAsync(item.Thread, JumpStillRequested(request))) return;
        if (!NavigateWorkspace(WorkspacePage.Archive, activateArchive: false)) return;
        _mediaReturn = new TaggingPageReturn(item.Key);
        ArchivePage.MediaReturn = ReturnToMedia;
        UpdateMediaReturn();
        await ShowThreadPostInLargeReaderAsync(item.Thread, item.PostId, request);
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
