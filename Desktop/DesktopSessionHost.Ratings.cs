using System;
using System.Collections.Generic;
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

    internal IReadOnlyList<MediaRatingEntry> GetMediaRatings()
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (_ratingsSync) { LoadMediaRatingsIfNeeded(); return _ratingEntries!; }
        }
    }
    internal int? GetMediaRating(FavoriteAssetKey key)
    {
        MediaRatingStore.ValidateKey(key);
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (_ratingsSync) { LoadMediaRatingsIfNeeded(); return _ratingValues!.TryGetValue(key, out int value) ? value : null; }
        }
    }
    internal async Task<MediaRatingCommit> SetMediaRatingAsync(FavoriteAssetKey key, int? value, CancellationToken token = default)
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
        }
    }
    internal async Task RetryMediaRatingCleanupAsync(IReadOnlyCollection<ResourceId> frozenIds, CancellationToken token = default)
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
        }
        catch (Exception error) when (error is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { _ratingLoadError = error; throw; }
    }
    private void PublishMediaRatings(IEnumerable<MediaRatingEntry> entries)
    {
        _ratingEntries = Array.AsReadOnly(entries.ToArray());
        _ratingValues = _ratingEntries.ToDictionary(entry => entry.Key, entry => entry.Value);
    }
}
