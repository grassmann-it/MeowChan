using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Storage;

namespace MeowChan.Gallery;

/// <summary>Whether a media occurrence has a locally saved file.</summary>
internal enum MediaLibraryPresence { Present, Missing }

/// <summary>
/// One media occurrence for the archive-wide media page: a captured attachment of a post, or a saved
/// file not assigned to any post. Derived data only; it carries no tags, favorites or ratings and is
/// never authority for writing, deleting or verifying anything. <see cref="LocalSlot"/> is the manifest's requested
/// file name when the gallery build found this entry locally taggable (a loose view of a file with no bound view),
/// otherwise null; it lets the one tag resolver name the local occurrence locator without reopening the manifest.
/// </summary>
internal sealed record MediaLibraryEntry(
    ResourceId Thread, string ThreadTitle, long ThreadRevision,
    DateTimeOffset FirstSeenAt, DateTimeOffset? LastCapturedAt,
    string? PostId, long? PostOrdinal, DateTimeOffset? PostedAt, int? Position, string? AttachmentRevisionKey,
    string? Directory, string? AssetId, string? Sha256, long? Length,
    string? MediaId, string Variant, string? Name, SavedMediaKind Kind,
    MediaLibraryPresence Presence, bool Unassigned, bool Historical, string? LocalSlot = null);

/// <summary>What one thread's entries were derived from; equal state means the entries can be reused.</summary>
internal sealed record MediaLibraryThreadState(ResourceId Thread, long Revision,
    IReadOnlyList<MediaLibraryManifestState> Manifests, bool Complete);

/// <summary>A media directory and the manifest generation it had when read (null: missing or unreadable).</summary>
internal sealed record MediaLibraryManifestState(string Directory, string? Generation);

internal sealed record MediaLibraryProgress(int ThreadsDone, int ThreadsTotal, int Reused);

/// <summary>An immutable, complete or partial index over all archived threads.</summary>
internal sealed class MediaLibrarySnapshot
{
    internal MediaLibrarySnapshot(IEnumerable<MediaLibraryEntry> entries, IEnumerable<MediaLibraryThreadState> threads,
        IEnumerable<string> warnings, bool complete)
    {
        Entries = Array.AsReadOnly(entries.ToArray());
        Threads = Array.AsReadOnly(threads.ToArray());
        Warnings = Array.AsReadOnly(warnings.ToArray());
        Complete = complete;
    }

    internal static MediaLibrarySnapshot Empty { get; } = new(Array.Empty<MediaLibraryEntry>(),
        Array.Empty<MediaLibraryThreadState>(), Array.Empty<string>(), true);

    internal IReadOnlyList<MediaLibraryEntry> Entries { get; }
    internal IReadOnlyList<MediaLibraryThreadState> Threads { get; }
    /// <summary>Per-thread problems. A thread with a problem keeps whatever could be read and is marked incomplete.</summary>
    internal IReadOnlyList<string> Warnings { get; }
    /// <summary>False when the build was cancelled or a thread could not be read completely.</summary>
    internal bool Complete { get; }
}

/// <summary>
/// Builds the media index from the catalog and the per-thread media manifests, read-only. A thread is
/// reused from the previous index when its catalog revision, its media directories and every manifest
/// generation are unchanged. Otherwise it is read again through the same selection the gallery uses,
/// so the page and the gallery agree about what a thread contains.
/// </summary>
internal static class MediaLibraryIndexBuilder
{
    internal const int ThreadPageSize = 200;

    internal static async Task<MediaLibrarySnapshot> BuildAsync(
        Func<CancellationToken, Task<ArchiveReader>> openReader,
        Func<string, CancellationToken, Task<MediaAssetSnapshot>> openManifest,
        MediaLibrarySnapshot? previous = null, IProgress<MediaLibraryProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(openReader);
        ArgumentNullException.ThrowIfNull(openManifest);
        Dictionary<ResourceId, MediaLibraryThreadState> priorStates = previous?.Threads
            .Where(state => state.Complete).ToDictionary(state => state.Thread) ?? new();
        ILookup<ResourceId, MediaLibraryEntry> priorEntries = (previous?.Entries ?? Array.Empty<MediaLibraryEntry>())
            .ToLookup(entry => entry.Thread);

        List<MediaLibraryEntry> entries = new();
        List<MediaLibraryThreadState> states = new();
        List<string> warnings = new();
        bool complete = true;
        await using ArchiveReader reader = await openReader(cancellationToken).ConfigureAwait(false);
        List<ArchiveThreadSummary> threads = new();
        for (int offset = 0; ; offset += ThreadPageSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArchiveThreadPage page = await reader.ListThreadsAsync(offset, ThreadPageSize, cancellationToken).ConfigureAwait(false);
            threads.AddRange(page.Items);
            if (page.Items.Count < ThreadPageSize || threads.Count >= page.TotalCount) break;
        }

        int done = 0, reused = 0;
        progress?.Report(new(0, threads.Count, 0));
        foreach (ArchiveThreadSummary summary in threads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                IReadOnlyList<string> directories = await reader.GetMediaDirectoriesAsync(summary.Id, cancellationToken).ConfigureAwait(false);
                List<MediaAssetSnapshot> manifests = new();
                List<MediaLibraryManifestState> manifestStates = new();
                bool threadComplete = true;
                foreach (string directory in directories.Distinct(StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        MediaAssetSnapshot manifest = await openManifest(directory, cancellationToken).ConfigureAwait(false);
                        manifests.Add(manifest);
                        manifestStates.Add(new(directory, manifest.Generation));
                        if (manifest.Status == MediaManifestStatus.Corrupt) { threadComplete = false; warnings.Add(Describe(summary.Id) + ": media manifest is damaged."); }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
                    {
                        // Captured attachments of this directory then appear as missing; never invented as present.
                        manifestStates.Add(new(directory, null));
                        threadComplete = false;
                        warnings.Add(Describe(summary.Id) + ": media folder could not be read.");
                    }
                }
                MediaLibraryThreadState state = new(summary.Id, summary.Revision, manifestStates.AsReadOnly(), threadComplete);
                if (threadComplete && priorStates.TryGetValue(summary.Id, out MediaLibraryThreadState? prior) && SameState(prior, state))
                {
                    entries.AddRange(priorEntries[summary.Id]);
                    reused++;
                }
                else
                {
                    ArchivedThreadView? view = await reader.ReadThreadAsync(summary.Id, cancellationToken).ConfigureAwait(false);
                    if (view == null) { done++; continue; } // Removed between listing and reading.
                    entries.AddRange(EntriesOf(view, manifests));
                }
                if (!threadComplete) complete = false;
                states.Add(state);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
                or Microsoft.Data.Sqlite.SqliteException)
            {
                complete = false;
                warnings.Add(Describe(summary.Id) + ": thread could not be read.");
            }
            done++;
            progress?.Report(new(done, threads.Count, reused));
        }
        return new MediaLibrarySnapshot(entries, states, warnings, complete);
    }

    private static bool SameState(MediaLibraryThreadState prior, MediaLibraryThreadState current) =>
        prior.Revision == current.Revision && prior.Manifests.Count == current.Manifests.Count
        && prior.Manifests.Zip(current.Manifests).All(pair => pair.First == pair.Second && pair.First.Generation != null);

    private static string Describe(ResourceId id) => id.Source + " /" + id.Board + "/ " + id.Thread;

    /// <summary>
    /// The occurrences of one thread: every attachment the gallery would show (current versions, all
    /// media kinds), every saved file without a post, and every captured attachment without a saved file.
    /// </summary>
    internal static IEnumerable<MediaLibraryEntry> EntriesOf(ArchivedThreadView view, IReadOnlyList<MediaAssetSnapshot> manifests)
    {
        ArchiveThreadSummary summary = view.Summary;
        GalleryThreadResult selected = GallerySelection.Build(view, manifests, allSavedVersions: false, includeOtherMedia: true);
        Dictionary<string, CapturedPost> posts = new(StringComparer.Ordinal);
        for (int offset = 0; offset < view.PostCount; offset += 500)
            foreach (CapturedPost post in view.GetPosts(offset, 500)) posts[post.PostId] = post;
        HashSet<(string Post, int Position)> found = new();
        foreach (GalleryEntry entry in selected.Entries)
        {
            CapturedPost? post = entry.Post;
            if (post != null && entry.Attachment != null) found.Add((post.PostId, entry.Attachment.Position));
            yield return new MediaLibraryEntry(summary.Id, summary.Title, summary.Revision, summary.FirstSeenAt, summary.LastCapturedAt,
                post?.PostId, post?.Ordinal, post?.PostedAt, entry.Attachment?.Position, entry.AttachmentResolution?.Binding?.Reference.RevisionKey,
                entry.Manifest.Directory, entry.Asset.AssetId, entry.Asset.Sha256.ToLowerInvariant(), entry.Asset.Length,
                entry.Asset.MediaIdentifier, entry.Asset.Variant, entry.Asset.OriginalFileName ?? Path.GetFileName(entry.Asset.Path),
                entry.Kind, MediaLibraryPresence.Present, entry.Unassigned, entry.Historical,
                entry.LocalTagEligible ? entry.Asset.RequestedFileName : null);
        }
        foreach (CapturedPost post in posts.Values.OrderBy(post => post.Ordinal).ThenBy(post => post.PostId, StringComparer.Ordinal))
            foreach (CapturedAttachment attachment in (post.Attachments ?? Array.Empty<CapturedAttachment>()).OrderBy(item => item.Position))
            {
                if (attachment.IsDeleted || attachment.MediaId == null || found.Contains((post.PostId, attachment.Position))) continue;
                string? name = attachment.OriginalFileName;
                yield return new MediaLibraryEntry(summary.Id, summary.Title, summary.Revision, summary.FirstSeenAt, summary.LastCapturedAt,
                    post.PostId, post.Ordinal, post.PostedAt, attachment.Position, attachment.AttachmentRevisionKey,
                    null, null, null, null, attachment.MediaId, attachment.Variant, name,
                    SavedMediaTypes.Classify(name ?? attachment.Url ?? string.Empty), MediaLibraryPresence.Missing, false, false);
            }
    }
}
