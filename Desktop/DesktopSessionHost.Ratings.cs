using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Favorites;
using MeowChan.Ratings;

namespace MeowChan.Desktop;

internal sealed partial class DesktopSessionHost
{
    private readonly object _ratingsSync = new();
    private MediaRatingStore? _ratingsStore;
    private IReadOnlyList<MediaRatingEntry>? _ratingEntries;
    private IReadOnlyDictionary<FavoriteAssetKey, int>? _ratingValues;
    private Exception? _ratingLoadError;
    private IReadOnlyDictionary<FavoriteAssetKey, string>? _labelValues;
    private IReadOnlyList<MediaLabelEntry>? _labelEntries;

    private Exception? _ratingReadError;

    internal IReadOnlyList<MediaRatingEntry> GetMediaRatings()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Volatile.Read(ref _ratingEntries)
            ?? throw new InvalidDataException("Ratings are unavailable: " + _ratingReadError?.Message, _ratingReadError);
    }
    internal int? GetMediaRating(FavoriteAssetKey key)
    {
        MediaRatingStore.ValidateKey(key);
        ObjectDisposedException.ThrowIf(_disposed, this);
        IReadOnlyDictionary<FavoriteAssetKey, int>? values = Volatile.Read(ref _ratingValues);
        if (values == null) throw new InvalidDataException("Ratings are unavailable: " + _ratingReadError?.Message, _ratingReadError);
        return values.TryGetValue(key, out int value) ? value : null;
    }
    /// <summary>The colour label (red, yellow, green, blue) of a media identity, or null; throws like <see cref="GetMediaRating"/> when the store is unavailable.</summary>
    internal string? GetMediaLabel(FavoriteAssetKey key)
    {
        MediaRatingStore.ValidateKey(key);
        ObjectDisposedException.ThrowIf(_disposed, this);
        IReadOnlyDictionary<FavoriteAssetKey, string>? values = Volatile.Read(ref _labelValues);
        if (values == null) throw new InvalidDataException("Ratings are unavailable: " + _ratingReadError?.Message, _ratingReadError);
        return values.TryGetValue(key, out string? value) ? value : null;
    }
    internal IReadOnlyList<MediaLabelEntry> GetMediaLabels()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // One snapshot instance per change, so the media page's SameSourcesAs sees "unchanged" for an untouched store.
        return Volatile.Read(ref _labelEntries) ?? throw new InvalidDataException("Ratings are unavailable: " + _ratingReadError?.Message, _ratingReadError);
    }
    internal Task<MediaLabelCommit> SetMediaLabelAsync(FavoriteAssetKey key, string? color, CancellationToken token = default)
        => RunInteractiveAsync(nameof(SetMediaLabelAsync), key.ToString(), () => SetMediaLabelAdmittedCoreAsync(key, color, token));

    private async Task<MediaLabelCommit> SetMediaLabelAdmittedCoreAsync(FavoriteAssetKey key, string? color, CancellationToken token)
    {
        await _commands.WaitAsync(token).ConfigureAwait(false);
        try
        {
            EnsureReady();
            MediaLabelCommit commit;
            lock (_ratingsSync)
            {
                LoadMediaRatingsIfNeeded();
                commit = _ratingsStore!.SetLabel(key, color, token);
                if (commit.Changed) PublishMediaLabels(_ratingsStore.ListLabels());
            }
            if (commit.Changed) SignalChanged();
            return commit;
        }
        finally { _commands.Release(); }
    }
    private void PublishMediaLabels(IEnumerable<MediaLabelEntry> entries)
    {
        var snapshot = Array.AsReadOnly(entries.ToArray());
        Volatile.Write(ref _labelEntries, snapshot);
        Volatile.Write(ref _labelValues, snapshot.ToDictionary(entry => entry.Key, entry => entry.Color));
    }

    internal Task<MediaRatingCommit> SetMediaRatingAsync(FavoriteAssetKey key, int? value, CancellationToken token = default)
        => RunInteractiveAsync(nameof(SetMediaRatingAsync), key.ToString(), () => SetMediaRatingAdmittedCoreAsync(key, value, token));

    private async Task<MediaRatingCommit> SetMediaRatingAdmittedCoreAsync(FavoriteAssetKey key, int? value, CancellationToken token = default)
    {
        await _commands.WaitAsync(token).ConfigureAwait(false);
        try
        {
            EnsureReady();
            MediaRatingCommit commit;
            lock (_ratingsSync)
            {
                LoadMediaRatingsIfNeeded();
                commit = _ratingsStore!.Set(key, value, token);
                if (commit.Changed)
                {
                    var entries = _ratingEntries!.Where(entry => entry.Key != key);
                    if (value.HasValue) entries = entries.Append(new(key, value.Value));
                    PublishMediaRatings(entries.ToArray());
                }
            }
            if (commit.Changed) SignalChanged();
            return commit;
        }
        finally { _commands.Release(); }
    }
    private int ValidateMediaRatingsForDeletion(IReadOnlyCollection<ResourceId> ids)
    {
        lock (_ratingsSync)
        {
            LoadMediaRatingsIfNeeded();
            // Revalidate on disk before destructive work, even when the UI already has a cache.
            return _ratingsStore!.List().Count(entry => ids.Contains(entry.Key.Thread));
        }
    }

    // Called by the deletion owner while _commands is held, only for actually deleted IDs.
    private void CleanupMediaRatings(IReadOnlyCollection<ResourceId> ids)
    {
        lock (_ratingsSync)
        {
            LoadMediaRatingsIfNeeded();
            _ratingsStore!.RemoveThreads(ids);
            PublishMediaRatings(_ratingEntries!.Where(entry => !ids.Contains(entry.Key.Thread)).ToArray());
            PublishMediaLabels(_ratingsStore.ListLabels());
        }
    }
    internal Task RetryMediaRatingCleanupAsync(IReadOnlyCollection<ResourceId> frozenIds, CancellationToken token = default)
    {
        ResourceId[] frozen = frozenIds.ToArray();
        return RunInteractiveAsync(nameof(RetryMediaRatingCleanupAsync), "profile", () => RetryMediaRatingCleanupAdmittedCoreAsync(frozen, token));
    }

    private async Task RetryMediaRatingCleanupAdmittedCoreAsync(IReadOnlyCollection<ResourceId> frozenIds, CancellationToken token = default)
    {
        ResourceId[] ids = frozenIds.ToArray();
        await _commands.WaitAsync(token).ConfigureAwait(false);
        try { EnsureReady(); token.ThrowIfCancellationRequested(); CleanupMediaRatings(ids); SignalChanged(); }
        finally { _commands.Release(); }
    }
    private void LoadMediaRatingsIfNeeded()
    {
        if (_ratingLoadError != null) throw new InvalidOperationException("Ratings are unavailable: " + _ratingLoadError.Message, _ratingLoadError);
        if (_ratingEntries != null) return;
        try
        {
            _ratingsStore = new(_profileLease.ProfileDirectory, _profileLease);
            PublishMediaRatings(_ratingsStore.List());
            PublishMediaLabels(_ratingsStore.ListLabels());
        }
        catch (Exception error) when (error is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { _ratingLoadError = error; throw; }
    }
    private void PublishMediaRatings(IEnumerable<MediaRatingEntry> entries)
    {
        var snapshot = Array.AsReadOnly(entries.ToArray());
        Volatile.Write(ref _ratingEntries, snapshot);
        Volatile.Write(ref _ratingValues, snapshot.ToDictionary(entry => entry.Key, entry => entry.Value));
    }
}
