using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Favorites;
using MeowChan.Interfaces;

namespace MeowChan.Desktop;

internal sealed record ArchivePermanentDeletionPlan(ArchiveDeletionPlan Catalog, ArchiveFilesPlan Files, int RatingCount = 0);

internal sealed record ArchivePermanentDeletionResult(IReadOnlyList<ResourceId> DeletedThreads,
    IReadOnlyList<string> Errors, ArchiveFilesResult? Files, IReadOnlyList<ResourceId>? RatingCleanupThreads = null);

internal sealed partial class DesktopSessionHost
{
    internal async Task<ArchivePermanentDeletionPlan> PrepareArchiveDeletionAsync(
        IReadOnlyCollection<ResourceId> ids, CancellationToken token = default)
    {
        await _commands.WaitAsync(token).ConfigureAwait(false);
        try
        {
            EnsureReady();
            ArchiveDeletionPlan catalog = await _content.Catalog.PrepareDeletionAsync(ids, token).ConfigureAwait(false);
            int ratingCount = ValidateMediaRatingsForDeletion(catalog.Threads.Select(thread => thread.Summary.Id).ToArray());
            ArchiveFilesPlan files = await ArchiveDeletionFiles.PrepareAsync(
                catalog.Threads.SelectMany(thread => thread.MediaDirectories), ProfileDirectory, token)
                .ConfigureAwait(false);
            return new ArchivePermanentDeletionPlan(catalog, files, ratingCount);
        }
        finally { _commands.Release(); }
    }

    internal async Task<ArchivePermanentDeletionResult> DeleteArchivePermanentlyAsync(
        ArchivePermanentDeletionPlan plan, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        await _commands.WaitAsync(token).ConfigureAwait(false);
        try
        {
            EnsureReady();
            ValidateMediaRatingsForDeletion(plan.Catalog.Threads.Select(thread => thread.Summary.Id).ToArray());
            List<string> errors = new();
            IReadOnlyList<ResourceId>? ratingCleanupThreads = null;
            List<ResourceId> deleted = new();
            HashSet<string> evictedPosters = new(StringComparer.Ordinal);
            ArchiveFilesResult? files = null;
            bool wasPaused = _registry.IsPaused;
            if (!wasPaused) await _registry.SetPausedAsync(true).ConfigureAwait(false);
            try
            {
                await _registry.DrainBackgroundTasksAsync(token).ConfigureAwait(false);
                // A preview can be left open while capture changes its paths or ownership.
                ArchiveDeletionPlan fresh = await _content.Catalog.PrepareDeletionAsync(
                    plan.Catalog.Threads.Select(thread => thread.Summary.Id).ToArray(), token).ConfigureAwait(false);
                if (!SameCatalogPlan(plan.Catalog, fresh))
                    return new ArchivePermanentDeletionResult(Array.Empty<ResourceId>(),
                        new[] { "The archive changed after confirmation. Review the deletion again." }, null);

                HashSet<string> shared = fresh.SharedMediaDirectories.Select(DeletionPath).ToHashSet(DeletionPathComparer);
                ArchiveDeletedThread[] eligible = fresh.Threads.Where(thread =>
                    !thread.MediaDirectories.Any(directory => shared.Contains(DeletionPath(directory)))).ToArray();
                foreach (ArchiveDeletedThread thread in fresh.Threads.Except(eligible))
                    errors.Add($"{thread.Summary.Title}: A media directory is also owned by another archived thread.");

                List<ArchiveDeletedThread> quiesced = new();
                foreach (ArchiveDeletedThread thread in eligible)
                {
                    token.ThrowIfCancellationRequested();
                    ResourceId id = thread.Summary.Id;
                    try
                    {
                        await _registry.SuppressArchivedThreadAsync(id, thread.Summary.Url, token).ConfigureAwait(false);
                        IThread[] active = _registry.GetView().Threads.Keys.Where(watch => Matches(watch, id)).ToArray();
                        foreach (IThread watch in active)
                        {
                            await _registry.RemoveThreadAsync(watch).ConfigureAwait(false);
                            lock (_projection)
                            {
                                if (_threads.Remove(watch, out WatchIdentity? identity))
                                    _threadHandles.Remove(identity.Id);
                            }
                        }
                        quiesced.Add(thread);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    { errors.Add($"{thread.Summary.Title}: Could not stop its watch: {exception.Message}"); }
                }

                HashSet<string> unquiescedDirectories = eligible.Except(quiesced)
                    .SelectMany(thread => thread.MediaDirectories).Select(DeletionPath).ToHashSet(DeletionPathComparer);
                while (true)
                {
                    ArchiveDeletedThread[] newlyBlocked = quiesced.Where(thread =>
                        thread.MediaDirectories.Any(directory => unquiescedDirectories.Contains(DeletionPath(directory)))).ToArray();
                    if (newlyBlocked.Length == 0) break;
                    foreach (ArchiveDeletedThread thread in newlyBlocked)
                    {
                        quiesced.Remove(thread);
                        unquiescedDirectories.UnionWith(thread.MediaDirectories.Select(DeletionPath));
                        errors.Add($"{thread.Summary.Title}: Its media directory is also owned by a thread whose watch could not be stopped.");
                    }
                }

                string[] eligibleDirectories = quiesced.SelectMany(thread => thread.MediaDirectories)
                    .Select(DeletionPath).Distinct(DeletionPathComparer).ToArray();
                ArchiveFilesPlan eligibleFiles = new(plan.Files.ProfileDirectory,
                    Array.AsReadOnly(plan.Files.Directories.Where(directory =>
                        eligibleDirectories.Contains(DeletionPath(directory.Directory), DeletionPathComparer)).ToArray()));
                files = await ArchiveDeletionFiles.DeleteAsync(eligibleFiles, token).ConfigureAwait(false);
                Dictionary<string, ArchiveDirectoryResult> fileResults = files.Directories.ToDictionary(
                    directory => DeletionPath(directory.Directory), DeletionPathComparer);
                const string canceledAfterCommit = "Archive deletion was canceled. Remaining catalog entries were not deleted.";
                foreach (ArchiveDeletedThread thread in quiesced)
                {
                    if (token.IsCancellationRequested)
                    {
                        if (deleted.Count == 0) token.ThrowIfCancellationRequested();
                        errors.Add(canceledAfterCommit);
                        break;
                    }
                    string[] failed = thread.MediaDirectories.Where(directory =>
                        !fileResults.TryGetValue(DeletionPath(directory), out ArchiveDirectoryResult? result) || result.Error != null)
                        .ToArray();
                    if (failed.Length > 0)
                    {
                        foreach (string directory in failed)
                            errors.Add($"{thread.Summary.Title}: {directory}: " +
                                (fileResults.TryGetValue(DeletionPath(directory), out ArchiveDirectoryResult? result)
                                    ? result.Error : "Media directory was not in the confirmed plan."));
                        continue;
                    }
                    try
                    {
                        IReadOnlyList<ArchiveDeletedThread> removed = await _content.Catalog.DeleteThreadsAsync(
                            new[] { thread.Summary.Id }, token).ConfigureAwait(false);
                        if (removed.Count == 1)
                        {
                            deleted.Add(thread.Summary.Id);
                            foreach (string hash in plan.Files.Directories.Where(directory =>
                                thread.MediaDirectories.Any(path => DeletionPathComparer.Equals(DeletionPath(path), DeletionPath(directory.Directory))))
                                .SelectMany(directory => directory.AssetSha256).Distinct(StringComparer.Ordinal))
                            {
                                if (!evictedPosters.Add(hash)) continue;
                                try { EvictVideoPoster(hash); }
                                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                    or InvalidDataException)
                                { errors.Add($"{thread.Summary.Title}: Could not remove cached video poster: {exception.Message}"); }
                            }
                        }
                        else errors.Add($"{thread.Summary.Title}: The catalog record changed before removal.");
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested && deleted.Count > 0)
                    {
                        errors.Add(canceledAfterCommit);
                        break;
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    { errors.Add($"{thread.Summary.Title}: {exception.Message}"); }
                }
                if (deleted.Count > 0)
                {
                    try { CleanupMediaRatings(deleted); }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                        ratingCleanupThreads = Array.AsReadOnly(deleted.ToArray());
                        errors.Add(MediaRatingText.Get("The archive was deleted, but ratings were not cleaned up. Reimporting the same media identities may restore their ratings. ",
                            "Das Archiv wurde gelöscht, aber Bewertungen wurden nicht bereinigt. Beim erneuten Import derselben Medienidentitäten können deren Bewertungen wieder erscheinen. ")
                            + string.Join(", ", deleted.Select(id => id.Source + "/" + id.Board + "/" + id.Thread)) + ": " + exception.Message);
                    }
                    try
                    {
                        lock (_favoritesSync)
                        {
                            LoadFavoritesIfNeeded();
                            // These catalog deletions are already committed. Finish their
                            // metadata cleanup and return the actual result despite cancellation.
                            _favoritesStore!.RemoveThreads(deleted);
                            FavoriteEntry[] retained = _favoriteEntries!.Where(entry =>
                                !deleted.Contains(entry.Key.Thread)).ToArray();
                            _favoriteEntries = Array.AsReadOnly(retained);
                            _favoriteKeys = retained.Select(entry => entry.Key).ToHashSet();
                        }
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    { errors.Add($"The archive was deleted, but favorites could not be updated: {exception.Message}"); }
                }
                SignalChanged();
                return new ArchivePermanentDeletionResult(Array.AsReadOnly(deleted.ToArray()),
                    Array.AsReadOnly(errors.ToArray()), files, ratingCleanupThreads);
            }
            finally
            {
                if (!wasPaused) await _registry.SetPausedAsync(false).ConfigureAwait(false);
                SignalChanged();
            }
        }
        finally { _commands.Release(); }
    }

    private static readonly StringComparer DeletionPathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string DeletionPath(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { return path; } // The file plan rejects invalid paths before touching data.
    }

    private static bool Matches(IThread watch, ResourceId id) => watch.Board.ImageBoard.Name == id.Source
        && watch.Board.BoardCode == id.Board && watch.ThreadId == id.Thread;

    private void EvictVideoPoster(string sha256)
    {
        if (sha256.Length != 64 || sha256.Any(character => !char.IsAsciiHexDigit(character) || char.IsLetter(character) && !char.IsLower(character)))
            throw new InvalidDataException("The media hash is invalid for poster cleanup.");
        string cacheParent = Path.Combine(ProfileDirectory, "cache");
        string cacheDirectory = Path.Combine(cacheParent, "video-posters-v1");
        foreach (string directory in new[] { ProfileDirectory, cacheParent, cacheDirectory })
            if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The video poster cache path is a symbolic link or reparse point.");
        if (!Directory.Exists(cacheDirectory)) return;
        string poster = Path.Combine(cacheDirectory, sha256 + ".png");
        if (!File.Exists(poster)) return;
        if ((File.GetAttributes(poster) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The video poster cache entry is a symbolic link or reparse point.");
        File.Delete(poster);
    }

    private static bool SameCatalogPlan(ArchiveDeletionPlan previous, ArchiveDeletionPlan current)
    {
        if (!previous.SharedMediaDirectories.Order(StringComparer.Ordinal).SequenceEqual(
            current.SharedMediaDirectories.Order(StringComparer.Ordinal))) return false;
        if (previous.Threads.Count != current.Threads.Count) return false;
        Dictionary<ResourceId, ArchiveDeletedThread> byId = current.Threads.ToDictionary(item => item.Summary.Id);
        return previous.Threads.All(item => byId.TryGetValue(item.Summary.Id, out ArchiveDeletedThread? fresh)
            && item.Summary == fresh.Summary
            && item.MediaDirectories.Order(StringComparer.Ordinal).SequenceEqual(
                fresh.MediaDirectories.Order(StringComparer.Ordinal)));
    }
}
