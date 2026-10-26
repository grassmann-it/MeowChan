using System;
using System.Collections.Generic;
using System.Collections.Frozen;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Favorites;
using MeowChan.Gallery;
using MeowChan.Storage;

namespace MeowChan.Desktop;

internal sealed record FavoriteResolutionSnapshot(IReadOnlyList<FavoriteExportAsset> Available,
    IReadOnlyList<FavoriteEntry> Missing);

internal sealed partial class DesktopSessionHost
{
    private readonly object _favoritesSync = new();
    private FavoriteStore? _favoritesStore;
    private IReadOnlyList<FavoriteEntry>? _favoriteEntries;
    private HashSet<FavoriteAssetKey>? _favoriteKeys;

    private Exception? _favoriteReadError;
    private sealed record PassiveFavoriteIndex(IReadOnlyList<FavoriteEntry> Entries, FrozenSet<FavoriteAssetKey> Keys);
    private PassiveFavoriteIndex? _passiveFavorites;
    private void PublishFavoriteIndex()
    {
        IReadOnlyList<FavoriteEntry> entries = _favoriteEntries!;
        Volatile.Write(ref _passiveFavorites, new(entries, entries.Select(entry => entry.Key).ToFrozenSet()));
    }

    /// <summary>Passive immutable index. Loading and writes belong to the background owner.</summary>
    internal IReadOnlyList<FavoriteEntry> GetFavorites()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IReadOnlyList<FavoriteEntry>? entries = Volatile.Read(ref _passiveFavorites)?.Entries;
        return entries ?? throw new InvalidDataException("Favorites are unavailable: " + _favoriteReadError?.Message, _favoriteReadError);
    }

    internal bool IsFavorite(GalleryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        FavoriteAssetKey key = FavoriteAssetKey.From(entry.Key.Thread, entry.Asset);
        ObjectDisposedException.ThrowIf(_disposed, this);
        PassiveFavoriteIndex? index = Volatile.Read(ref _passiveFavorites);
        if (index == null) { GetFavorites(); return false; }
        return index.Keys.Contains(key);
    }

    internal Task<bool> SetFavoriteAsync(GalleryEntry entry, bool favorite,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return SetFavoriteAsync(FavoriteStore.From(entry.Key.Thread, entry.Asset), favorite, token);
    }

    // Removing a missing original must not require resolving or touching its file.
    internal Task<bool> SetFavoriteAsync(FavoriteEntry candidate, bool favorite,
        CancellationToken token = default)
        => RunInteractiveAsync(nameof(SetFavoriteAsync), candidate.Key.ToString(), () => SetFavoriteAdmittedCoreAsync(candidate, favorite, token));

    private async Task<bool> SetFavoriteAdmittedCoreAsync(FavoriteEntry candidate, bool favorite,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        await _commands.WaitAsync(token).ConfigureAwait(false);
        try
        {
            EnsureReady();
            bool changed;
            lock (_favoritesSync)
            {
                LoadFavoritesIfNeeded();
                changed = _favoritesStore!.SetFavorite(candidate, favorite, token);
                if (changed)
                {
                    FavoriteEntry[] next = favorite
                        ? _favoriteEntries!.Append(candidate).ToArray()
                        : _favoriteEntries!.Where(item => item.Key != candidate.Key).ToArray();
                    _favoriteEntries = Array.AsReadOnly(next);
                    if (favorite) _favoriteKeys!.Add(candidate.Key);
                    else _favoriteKeys!.Remove(candidate.Key);
                    PublishFavoriteIndex();
                }
            }
            if (changed) SignalChanged();
            return changed;
        }
        finally { _commands.Release(); }
    }

    /// <summary>
    /// Freezes the requested favorite identities, then resolves them through recorded local
    /// media directories and the current download layout. Only matching originals are verified;
    /// missing and corrupt files remain favorites but are reported unavailable. No provider or
    /// download operation is invoked.
    /// </summary>
    internal Task<FavoriteResolutionSnapshot> ResolveFavoriteAssetsAsync(
        IReadOnlyList<FavoriteEntry>? scope = null, CancellationToken token = default)
    {
        FavoriteEntry[]? frozen = scope?.ToArray();
        return RunInteractiveAsync(nameof(ResolveFavoriteAssetsAsync), "profile", () => ResolveFavoriteAssetsAdmittedCoreAsync(frozen, token), captureAfter: false);
    }

    private async Task<FavoriteResolutionSnapshot> ResolveFavoriteAssetsAdmittedCoreAsync(
        IReadOnlyList<FavoriteEntry>? scope = null, CancellationToken token = default)
    {
        FavoriteEntry[] frozen = (scope ?? GetFavorites()).DistinctBy(entry => entry.Key).ToArray();
        if (frozen.Length == 0)
            return new FavoriteResolutionSnapshot(Array.Empty<FavoriteExportAsset>(), Array.Empty<FavoriteEntry>());

        Dictionary<FavoriteAssetKey, FavoriteExportAsset> found = new();
        ArchiveReader? reader = null;
        try
        {
            try { reader = await OpenArchiveReaderAsync(token).ConfigureAwait(false); }
            catch (FileNotFoundException) { /* A profile may retain favorites without an archive catalog. */ }

            foreach (IGrouping<ResourceId, FavoriteEntry> group in frozen.GroupBy(entry => entry.Key.Thread))
            {
                token.ThrowIfCancellationRequested();
                HashSet<FavoriteAssetKey> wanted = group.Select(entry => entry.Key).ToHashSet();
                List<string> directories = new();
                if (reader != null)
                {
                    IReadOnlyList<string> archived = await reader.GetMediaDirectoriesAsync(group.Key, token)
                        .ConfigureAwait(false);
                    directories.AddRange(archived);
                }
                try
                {
                    directories.Add(StorageLayout.GetThreadDirectory(Config.DownloadPath,
                        group.Key.Source, group.Key.Board, group.Key.Thread!));
                }
                catch (ArgumentException) { /* Older provider identities may not use storage-safe segments. */ }

                foreach (string directory in directories.Distinct(StringComparer.Ordinal)
                    .OrderBy(path => path, StringComparer.Ordinal))
                {
                    if (wanted.All(found.ContainsKey)) break;
                    token.ThrowIfCancellationRequested();
                    MediaAssetSnapshot manifest;
                    try { manifest = await MediaAssetReader.OpenAsync(directory, token).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                    { continue; }
                    if (manifest.Status != MediaManifestStatus.Available) continue;
                    for (int offset = 0; offset < manifest.Count; offset += 500)
                    {
                        token.ThrowIfCancellationRequested();
                        foreach (MediaAssetReference asset in manifest.GetPage(offset, 500))
                        {
                            if (asset.Variant != "original") continue;
                            FavoriteAssetKey key = FavoriteAssetKey.From(group.Key, asset);
                            if (!wanted.Contains(key) || found.ContainsKey(key)) continue;
                            FavoriteEntry stored = group.First(entry => entry.Key == key);
                            if (asset.Length != stored.Length) continue;
                            try
                            {
                                await using Stream verified = await manifest.OpenVerifiedReadAsync(asset.AssetId, token)
                                    .ConfigureAwait(false);
                                found.Add(key, FavoriteExportAsset.From(group.Key, manifest, asset));
                            }
                            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                            { /* A later recorded directory may still have the same original. */ }
                        }
                    }
                }
            }
        }
        finally
        {
            if (reader != null) await reader.DisposeAsync().ConfigureAwait(false);
        }
        return new FavoriteResolutionSnapshot(
            Array.AsReadOnly(frozen.Where(entry => found.ContainsKey(entry.Key)).Select(entry => found[entry.Key]).ToArray()),
            Array.AsReadOnly(frozen.Where(entry => !found.ContainsKey(entry.Key)).ToArray()));
    }

    // Called only while holding _favoritesSync. The profile lease is the sole writer for this
    // session, so a loaded identity set remains valid until SetFavoriteAsync publishes a change.
    private void LoadFavoritesIfNeeded()
    {
        if (_favoriteEntries != null) return;
        FavoriteStore store = new(_profileLease.ProfileDirectory, _profileLease);
        FavoriteEntry[] entries = store.List().ToArray();
        _favoritesStore = store;
        _favoriteEntries = Array.AsReadOnly(entries);
        _favoriteKeys = entries.Select(item => item.Key).ToHashSet();
        PublishFavoriteIndex();
    }
}
