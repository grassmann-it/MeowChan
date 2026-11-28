using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Storage;

namespace MeowChan;

// Details of a tile of equal bytes: all occurrences with their own tags. Grouping is a query-time step;
// tags, favorites and ratings stay with each occurrence and are read per occurrence here.
public sealed partial class MediaPage
{
    private MediaOccurrencesWindow? _occurrences;

    internal Window? OccurrencesWindow => _occurrences;

    /// <summary>Opens (or replaces) the list of every occurrence of the tile's bytes.</summary>
    internal void ShowOccurrences(MediaLibraryTile tile)
    {
        ArgumentNullException.ThrowIfNull(tile);
        if (_lifetime.IsCancellationRequested) return;
        _occurrences?.Close();
        // Findings describe the tile at opening time. Source tags are subject to the current local rules.
        BooruCatalogSnapshot? catalog = tile.Occurrences.Any(entry => entry.BooruPostId != null) ? SafeReadBooruCatalog() : null;
        MediaOccurrencesWindow window = new(tile, OccurrenceTags, entry => entry.PostId != null && ShowInThread != null,
            entry => OpenActions.Start(() => ShowOccurrenceInThreadAsync(entry), "media.occurrences.thread"), entry => ProviderFindings(catalog, entry),
            FolderVersionNoticeOf, SourceTagsReader(tile));
        _occurrences = window;
        window.Closed += (_, _) => { if (ReferenceEquals(_occurrences, window)) _occurrences = null; };
        if (_owner != null) window.Show(_owner); else window.Show();
    }

    private BooruCatalogSnapshot? SafeReadBooruCatalog()
    {
        try { return ReadBooruCatalog?.Invoke(); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ObjectDisposedException) { return null; }
    }

    private Func<CancellationToken, Task<Dictionary<MediaLibraryEntry, IReadOnlyList<BooruPostClaimTag>>>>? SourceTagsReader(MediaLibraryTile tile)
    {
        if (MediaLibraryGrouping.ByteKey(tile.Entry) is not { } bytes) return null;
        MediaLibraryEntry[] matching = tile.Occurrences.Where(entry => MediaLibraryGrouping.ByteKey(entry) == bytes).ToArray();
        MediaLibraryEntry[] chan = matching.Where(entry => !entry.IsCatalogMedium && entry.Kind == SavedMediaKind.Image).ToArray();
        MediaLibraryEntry[] booru = matching.Where(entry => entry.BooruIdentity != null).ToArray();
        if (chan.Length == 0 || booru.Length == 0) return null;

        MediaManifestCache manifests = CreateManifestCache();
        VerifiedFileMemo verified = new();
        return token => Task.Run(async () =>
        {
            Dictionary<MediaLibraryEntry, IReadOnlyList<BooruPostClaimTag>> tags = new();
            BooruCatalogSnapshot? catalog = SafeReadBooruCatalog();
            if (catalog == null) return tags;

            manifests.Invalidate();
            foreach (MediaLibraryEntry entry in chan)
            {
                if (await VerifyChanOccurrenceAsync(entry, manifests, verified, token).ConfigureAwait(false))
                    return await ReadBooruOccurrenceTagsAsync(booru, catalog, manifests, verified, token).ConfigureAwait(false);
            }
            return tags;
        }, token);
    }

    private static async Task<bool> VerifyChanOccurrenceAsync(MediaLibraryEntry entry, MediaManifestCache manifests, VerifiedFileMemo verified,
        CancellationToken token)
    {
        if (entry.Directory == null || entry.AssetId == null) return false;
        try
        {
            MediaAssetSnapshot manifest = await manifests.OpenAsync(entry.Directory, token).ConfigureAwait(false);
            if (manifest.Status != MediaManifestStatus.Available || MediaTileThumbnails.Matching(manifest, entry) is not { } asset) return false;
            await using Stream file = await manifest.OpenSessionVerifiedReadAsync(asset.AssetId, verified, token).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (OccurrenceReadFailed(error)) { return false; }
    }

    private async Task<Dictionary<MediaLibraryEntry, IReadOnlyList<BooruPostClaimTag>>> ReadBooruOccurrenceTagsAsync(
        IEnumerable<MediaLibraryEntry> entries, BooruCatalogSnapshot catalog, MediaManifestCache manifests, VerifiedFileMemo verified,
        CancellationToken token)
    {
        Dictionary<MediaLibraryEntry, BooruPostClaim> available = new();
        foreach (MediaLibraryEntry entry in entries)
        {
            BooruPostClaim? post = catalog.TryGetPost(entry.BooruIdentity.GetValueOrDefault())?.Post;
            if (post is not { IsDeleted: false, IsBanned: false, Tags.Count: > 0 } || !post.Fields.HasFlag(BooruPostClaimPresence.Tags)) continue;
            try
            {
                GalleryEntry? saved = await BooruMediaGallery.ResolveAsync(entry, catalog, manifests.OpenAsync, token).ConfigureAwait(false);
                if (saved == null) continue;
                await using Stream file = await saved.Manifest.OpenSessionVerifiedReadAsync(saved.Asset.AssetId, verified, token).ConfigureAwait(false);
                available[entry] = post;
            }
            catch (Exception error) when (OccurrenceReadFailed(error)) { }
        }

        if (available.Count == 0) return new();
        // Read the rules after file validation, so a change while local IO is pending also applies to copy.
        try
        {
            BooruContentProfileSnapshot? profile = ReadContentProfile?.Invoke();
            BooruContentFilter? filter = profile?.State == BooruContentProfileState.Ready ? profile.Filter : MediaContentBoundary.RatingFilter(profile);
            return available.Where(pair => filter?.Evaluate(pair.Value).IsAllowed ?? true)
                .ToDictionary(pair => pair.Key, pair => pair.Value.Tags);
        }
        catch (Exception error) when (OccurrenceReadFailed(error)) { return new(); }
    }

    private static bool OccurrenceReadFailed(Exception error) =>
        error is IOException or InvalidDataException or UnauthorizedAccessException or ObjectDisposedException;

    // "Findings: Found by a search; Found by a watcher ×2". Several claims on one saved file are listed, never merged away.
    private static string? ProviderFindings(BooruCatalogSnapshot? catalog, MediaLibraryEntry entry)
    {
        if (catalog == null || entry.BooruIdentity is not { } identity || BooruMediaProjection.Find(catalog, identity) is not { } details) return null;
        string[] kinds = details.Findings.GroupBy(finding => finding.Kind).OrderBy(group => group.Key)
            .Select(group => BooruMessages.Finding(group.Key) + (group.Count() > 1 ? " ×" + group.Count() : string.Empty)).ToArray();
        return kinds.Length == 0 ? null : Texts.T("booru.media.occurrence.findings", string.Join("; ", kinds));
    }

    private async Task ShowOccurrenceInThreadAsync(MediaLibraryEntry entry)
    {
        if (entry.PostId == null || ShowInThread == null) return;
        // Close the originating window before the reader takes focus. Closing it after
        // navigation can restore its old focus over the newly focused reader (and an
        // awaited jump must never close a newer occurrences window).
        _occurrences?.Close();
        await ShowInThread(entry);
    }

    // The occurrence's own media tags and the tags of its thread, as the filter sees them.
    private string OccurrenceTags(MediaLibraryEntry entry)
    {
        try
        {
            string[] names = _userData.TagsOf(entry).Select(TagLabel).Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
            return names.Length == 0 ? Texts.T("media.occurrence.noTags") : Texts.T("media.occurrence.tags", string.Join(", ", names));
        }
        catch (MediaLibraryFilterException) { return Texts.T("media.occurrence.tagsUnavailable"); }
    }
}
