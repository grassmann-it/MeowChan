using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.MediaActions;
using MeowChan.Favorites;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan;

public partial class ArchivePage
{
    private ArchiveAssetRow? _savedMediaSidebarRow;
    private (ArchivedThreadView Thread, IReadOnlyList<ArchiveAssetRow> Assets, Task<ILookup<GalleryAssetKey, GalleryEntry>> Entries)? _savedSidebarTags;
    internal Func<GalleryEntry, GalleryDecodeRequest.Fit, CancellationToken, Task<ReaderMediaSidebarContent.Picture>>? SavedSidebarImageLoaderForTests { get; set; }

    private ArchiveAssetRow? SavedMediaSidebarRow => ArchiveMediaTab.IsSelected && _shownMediaThread == _selectedThread
        && ReferenceEquals(_savedMediaSidebarRow, SelectedMediaRow()) ? _savedMediaSidebarRow : null;

    private ArchiveAssetRow? ActionableSavedMediaSidebarRow => !_ended && _available && _mediaSidebarShown
        && _mediaSidebar is { IsEffectivelyVisible: true } && !_obscuredByGallery && !IsReaderDialogOpen
        && _thread != null && _thread.Summary.Id == _selectedThread ? SavedMediaSidebarRow : null;

    private void FollowSavedMediaSelection()
    {
        if (ArchiveMediaTab.IsSelected) OnMediaSidebarSelectionChanged();
    }

    private static GalleryEntry SavedSidebarEntry(ArchivedThreadView thread, ArchiveAssetRow row) =>
        new(new(thread.Summary.Id, row.Manifest.Directory, row.Asset.AssetId), row.Manifest, row.Asset,
            null, null, !row.Asset.IsCurrent, true) { ThreadUrl = thread.Summary.Url };

    private static string SavedSidebarKey(ArchivedThreadView thread, ArchiveAssetRow row) =>
        $"saved\n{thread.Summary.Id}\n{row.Manifest.Directory}\n{row.Asset.AssetId}\n{row.Asset.Sha256}";

    private void RefreshMediaSidebarDecisions()
    {
        if (ArchiveMediaTab.IsSelected)
        {
            if (!_ended && _mediaSidebarShown && _mediaSidebar is { } sidebar && _thread is { } thread
                && SavedMediaSidebarRow is { } row)
                ConfigureSavedMediaSidebarDecisions(sidebar, thread, row);
        }
        else RefreshPostMediaSidebarDecisions();
    }

    private void ConfigureSavedMediaSidebarDecisions(ReaderMediaSidebar sidebar, ArchivedThreadView thread, ArchiveAssetRow row)
    {
        bool Current() => !_ended && _available && _mediaSidebarShown && ReferenceEquals(_thread, thread)
            && ReferenceEquals(SavedMediaSidebarRow, row);
        FavoriteAssetKey key = FavoriteAssetKey.From(thread.Summary.Id, row.Asset);
        sidebar.ConfigureFavorite(() => Current() && row.FavoriteAvailable?.Invoke() == true, row.IsFavorite,
            () => Current() ? row.ToggleFavoriteAsync?.Invoke() ?? Task.CompletedTask : Task.CompletedTask,
            row.FavoriteError, row.FavoriteHold);
        sidebar.ConfigureRating(() => Current() && !row.RatingUnavailable && _mediaRatingError == null
            && GetMediaRating != null && SetMediaRatingAsync != null && CanChangeMediaRating?.Invoke(key) == true,
            () => row.Rating, value => Current() ? SetMediaRatingAsync?.Invoke(key, value) ?? Task.CompletedTask : Task.CompletedTask);
        OpenActions.Start(() => RefreshSavedMediaSidebarTagsAsync(sidebar, thread, row, Current), "archive.savedMedia.sidebarTags");
    }

    private async Task RefreshSavedMediaSidebarTagsAsync(ReaderMediaSidebar sidebar, ArchivedThreadView thread, ArchiveAssetRow row, Func<bool> current)
    {
        if (_privateTags == null)
        {
            sidebar.SetTags(null, MediaMenuReasons.TagsUnknown);
            return;
        }

        IReadOnlyList<ArchiveAssetRow> assets = _assets;
        if (_savedSidebarTags is not { } lookup || !ReferenceEquals(lookup.Thread, thread) || !ReferenceEquals(lookup.Assets, assets))
        {
            // Apply the gallery's bound/loose identity rules once per immutable inventory, away from the arrow-key route.
            lookup = (thread, assets, Task.Run(() => GallerySelection.Build(thread, assets.Select(asset => asset.Manifest)
                .DistinctBy(manifest => manifest.Directory), allSavedVersions: true, includeOtherMedia: true)
                .Entries.ToLookup(entry => entry.Key), _lifetime.Token));
            _savedSidebarTags = lookup;
        }

        try
        {
            ILookup<GalleryAssetKey, GalleryEntry> entries = await lookup.Entries;
            if (!current() || _privateTags is not { } tags) return;
            GalleryAssetKey key = new(thread.Summary.Id, row.Manifest.Directory, row.Asset.AssetId);
            GalleryEntry[] candidates = entries[key].ToArray();
            MediaTagSubject[] subjects = candidates.Select(MediaTagSubjects.Of).OfType<MediaTagSubject>().Distinct().ToArray();
            MediaTagSubject? subject = subjects.Length == 1 ? subjects[0] : null;
            MediaTagSubjects.Of(candidates.FirstOrDefault(), out GalleryMediaTagUnavailable reason);
            sidebar.SetTags(subject != null ? tags.TagsOf(subject).Select(tag => tag.Name.Display).ToArray() : null,
                subject != null ? null : MediaMenuReasons.Tags(reason, ambiguous: subjects.Length > 1));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (current()) sidebar.SetTags(null, MediaMenuReasons.TagsUnknown);
            Serilog.Log.Debug(error, "Saved media tags could not be shown in the reader sidebar");
        }
    }

    private void RefreshSavedMediaSidebar(ReaderMediaSidebar sidebar)
    {
        ArchiveAssetRow? next = _shownMediaThread == _selectedThread ? SelectedMediaRow() : null;
        if (!ReferenceEquals(_savedMediaSidebarRow, next)) InvalidateSidebarMediaMenu();
        _savedMediaSidebarRow = next;
        CancelSidebarStripLoad();
        sidebar.ClearStrip();
        if (_thread is not { } thread || _savedMediaSidebarRow is not { } row)
        {
            OfferSidebarVideo(null, null);
            sidebar.ShowEmpty(Texts.T("media.panel.empty"));
            ScheduleSidebarAnnouncement(Texts.T("media.panel.empty"));
            return;
        }

        GalleryEntry entry = SavedSidebarEntry(thread, row);
        string key = SavedSidebarKey(thread, row);
        (double Width, double Height) target = MediaSidebarTarget(sidebar);
        ReaderMediaLoadTicket ticket = sidebar.Load.Begin(key, target);
        if (!ticket.UpToDate && !ticket.SameMedium) sidebar.SetImageVerification(entry.Kind == SavedMediaKind.Image ? null : true);
        sidebar.Describe(row.Version, row.Name, ticket.SameMedium ? _sidebarShownDetail : string.Empty, OpenMediaGallery != null, mediumKey: key);
        sidebar.SetSourceFacts($"{entry.Key.Thread.Source} /{entry.Key.Thread.Board}/ · {entry.Key.Thread.Thread}",
            null, null, row.Asset.Length, null, null, hash: row.Asset.Sha256);
        sidebar.SetTags(null, MediaMenuReasons.TagsUnknown);
        ConfigureSavedMediaSidebarDecisions(sidebar, thread, row);
        OfferSavedSidebarVideo(entry, key);
        ScheduleSidebarAnnouncement(row.Name + " · " + row.Version);
        if (ticket.UpToDate)
        {
            if (sidebar.Load.ShownSource is { } source)
                sidebar.LimitPicture(ReaderMediaSidebarRules.MaximumDisplay(source.Width, _sidebarFit),
                    ReaderMediaSidebarRules.MaximumDisplay(source.Height, _sidebarFit));
            ScheduleSidebarVideoAutoplay();
            return;
        }

        CancellationTokenSource loading = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _sidebarLoading = loading;
        MediaSidebarTask = RunMediaSidebarLoadAsync(loading, token =>
        {
            ReaderMediaSidebarImageLoad load = new(sidebar, key, ticket, _sidebarFit, () => IsCurrentSidebarRequest(ticket.Request), token);
            return load.RunAsync(async ct =>
            {
                if (entry.Kind == SavedMediaKind.Image)
                {
                    GalleryDecodeRequest.Fit fit = new(target.Width, target.Height);
                    if (SavedSidebarImageLoaderForTests is not { } decode) return await load.ReadImageAsync(entry, fit, ct);
                    ReaderMediaSidebarContent.Picture picture = await decode(entry, fit, ct);
                    if (IsCurrentSidebarRequest(ticket.Request) && !ct.IsCancellationRequested) sidebar.SetImageVerification(true, entry);
                    return picture;
                }
                if (entry.Kind == SavedMediaKind.Video)
                {
                    int width = (int)target.Width, height = (int)target.Height;
                    Bitmap? poster = await MediaTileThumbnails.LoadPosterAsync(row.Manifest, row.Asset,
                        System.IO.Path.Combine(Config.GetConfigDirectory(), "cache", "video-posters-v1"),
                        width, height, MediaTileThumbnails.PosterKey(row.Asset, width, height), ct, useFailureBackoff: false);
                    if (IsCurrentSidebarRequest(ticket.Request) && !ct.IsCancellationRequested) ScheduleSidebarVideoAutoplay();
                    if (poster != null) return new ReaderMediaSidebarContent.Picture(poster, MediaTileThumbnails.Release, Playable: true);
                }
                return new ReaderMediaSidebarContent.Unavailable("reader.sidebar.unavailable");
            }, picture =>
            {
                _sidebarShownDetail = picture.Source is { } source ? $"{source.Width}×{source.Height}" : string.Empty;
                sidebar.SetDetail(_sidebarShownDetail);
            });
        });
    }

    private void OfferSavedSidebarVideo(GalleryEntry entry, string key)
    {
        bool offered = _inlineVideo.Options.Enabled && entry.Kind == SavedMediaKind.Video;
        OfferSidebarVideo(offered ? key : null, _ => Task.FromResult<GalleryEntry?>(entry),
            entry.Asset.OriginalFileName ?? entry.Asset.RequestedFileName, OpenMediaSidebarMedia);
    }
}
