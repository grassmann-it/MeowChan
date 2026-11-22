using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Persistence;
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
    MediaLibraryPresence Presence, bool Unassigned, bool Historical, string? LocalSlot = null)
{
    /// <summary>
    /// The Danbooru post id when this is a downloaded provider medium (see <see cref="BooruMediaIdentity"/>), else null.
    /// Additive projection field: such an entry has no chan post, no bound version and no local tag slot, and carries no
    /// provider claims itself; the claims are read from the Booru catalog when a view needs them.
    /// </summary>
    internal string? BooruPostId { get; init; }

    /// <summary>
    /// The import item id (32 hex digits) when this is a manually imported file (see <see cref="Import.ManualImportIdentity"/>),
    /// else null. Additive projection field: such an entry has no chan post and no bound version; it is a loose file whose
    /// <see cref="LocalSlot"/> names its tag locator, and its batch is the borrowed thread identity.
    /// </summary>
    internal string? ImportItemId { get; init; }

    /// <summary>
    /// The item id (32 hex digits) when this is a file of a read-only folder source (see <see cref="FolderSources.FolderSourceIdentity"/>),
    /// else null. Additive projection field: such an entry is a loose file in its virtual media folder; until its bytes are hashed
    /// it is present without <see cref="Sha256"/> (shown, never written to). Never saved in the index cache: the shadow catalog
    /// is cheap to project again.
    /// </summary>
    internal string? FolderItemId { get; init; }

    /// <summary>True for an entry that comes from a local catalog (Danbooru, manual import, folder source) instead of an archived thread.</summary>
    internal bool IsCatalogMedium => BooruPostId != null || ImportItemId != null || FolderItemId != null;
}

/// <summary>What one thread's entries were derived from; equal state means the entries can be reused.</summary>
internal sealed record MediaLibraryThreadState(ResourceId Thread, long Revision,
    IReadOnlyList<MediaLibraryManifestState> Manifests, bool Complete);

/// <summary>A media directory and the manifest generation it had when read (null: missing or unreadable).</summary>
internal sealed record MediaLibraryManifestState(string Directory, string? Generation)
{
    /// <summary>
    /// The identity of the manifest file whose bytes had <see cref="Generation"/> (see <see cref="MediaManifestRead"/>), or
    /// null when it could not be pinned. A later build that sees the same identity knows the generation without reading
    /// and hashing the manifest again. Part of the value: equal states also agree about what can be skipped.
    /// </summary>
    internal RegularProfileFileIdentity? Identity { get; init; }
}

internal sealed record MediaLibraryProgress(int ThreadsDone, int ThreadsTotal, int Reused);

/// <summary>An immutable, complete or partial index over all archived threads.</summary>
internal sealed class MediaLibrarySnapshot
{
    internal MediaLibrarySnapshot(IEnumerable<MediaLibraryEntry> entries, IEnumerable<MediaLibraryThreadState> threads,
        IEnumerable<string> warnings, bool complete, IEnumerable<MediaLibraryManifestState>? providerManifests = null)
    {
        Entries = Array.AsReadOnly(entries.ToArray());
        Threads = Array.AsReadOnly(threads.ToArray());
        Warnings = Array.AsReadOnly(warnings.ToArray());
        Complete = complete;
        ProviderManifests = Array.AsReadOnly(providerManifests?.ToArray() ?? Array.Empty<MediaLibraryManifestState>());
    }

    internal static MediaLibrarySnapshot Empty { get; } = new(Array.Empty<MediaLibraryEntry>(),
        Array.Empty<MediaLibraryThreadState>(), Array.Empty<string>(), true);

    internal IReadOnlyList<MediaLibraryEntry> Entries { get; }
    internal IReadOnlyList<MediaLibraryThreadState> Threads { get; }
    /// <summary>Per-thread problems. A thread with a problem keeps whatever could be read and is marked incomplete.</summary>
    internal IReadOnlyList<string> Warnings { get; }
    /// <summary>False when the build was cancelled or a thread could not be read completely.</summary>
    internal bool Complete { get; }
    /// <summary>
    /// The manifests the downloaded provider entries and the manually imported batches were derived from (one per media
    /// folder, with identity, stored in the same list), so the next build can keep an entry whose manifest file did not change. Not shown anywhere; empty when unknown.
    /// </summary>
    internal IReadOnlyList<MediaLibraryManifestState> ProviderManifests { get; }

    /// <summary>
    /// Whether two indexes hold the same entries in the same order and the same derivation states (warnings are not
    /// persisted and do not count). Decides whether the cache file needs to be written again.
    /// </summary>
    internal static bool SameContent(MediaLibrarySnapshot? left, MediaLibrarySnapshot? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left == null || right == null || left.Entries.Count != right.Entries.Count || left.Threads.Count != right.Threads.Count
            || left.ProviderManifests.Count != right.ProviderManifests.Count) return false;
        for (int index = 0; index < left.Entries.Count; index++)
            if (!ReferenceEquals(left.Entries[index], right.Entries[index]) && left.Entries[index] != right.Entries[index]) return false;
        for (int index = 0; index < left.Threads.Count; index++)
            if (!SameState(left.Threads[index], right.Threads[index])) return false;
        return SameManifests(left.ProviderManifests, right.ProviderManifests);
    }

    private static bool SameState(MediaLibraryThreadState left, MediaLibraryThreadState right) =>
        ReferenceEquals(left, right) || left.Thread == right.Thread && left.Revision == right.Revision && left.Complete == right.Complete
            && SameManifests(left.Manifests, right.Manifests);

    private static bool SameManifests(IReadOnlyList<MediaLibraryManifestState> left, IReadOnlyList<MediaLibraryManifestState> right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left.Count != right.Count) return false;
        for (int index = 0; index < left.Count; index++)
            if (left[index] != right[index]) return false;
        return true;
    }
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

    /// <summary>
    /// Builds with a plain manifest opener: every manifest is read and hashed, as the builder always did; no file
    /// identity is recorded or used.
    /// </summary>
    internal static Task<MediaLibrarySnapshot> BuildAsync(
        Func<CancellationToken, Task<ArchiveReader>> openReader,
        Func<string, CancellationToken, Task<MediaAssetSnapshot>> openManifest,
        MediaLibrarySnapshot? previous = null, IProgress<MediaLibraryProgress>? progress = null,
        CancellationToken cancellationToken = default, Func<BooruCatalogSnapshot?>? readBooruCatalog = null,
        Func<Import.ManualImportCatalogSnapshot?>? readImportCatalog = null, Func<FolderSources.FolderSourceCatalogSnapshot?>? readFolderSources = null)
    {
        ArgumentNullException.ThrowIfNull(openManifest);
        return BuildAsync(openReader, new MediaManifestCache(openManifest, identity: false), previous, progress, cancellationToken, readBooruCatalog,
            readImportCatalog, readFolderSources);
    }

    /// <summary>
    /// Builds through <paramref name="manifests"/>. When it pins manifest file identities, a thread whose catalog revision,
    /// media folders and every manifest file identity equal the previous complete state is reused without opening any
    /// manifest; the result is the same as reading them, because equal identity means equal bytes and so equal generation.
    /// </summary>
    internal static async Task<MediaLibrarySnapshot> BuildAsync(
        Func<CancellationToken, Task<ArchiveReader>> openReader,
        MediaManifestCache manifests,
        MediaLibrarySnapshot? previous = null, IProgress<MediaLibraryProgress>? progress = null,
        CancellationToken cancellationToken = default, Func<BooruCatalogSnapshot?>? readBooruCatalog = null,
        Func<Import.ManualImportCatalogSnapshot?>? readImportCatalog = null, Func<FolderSources.FolderSourceCatalogSnapshot?>? readFolderSources = null)
    {
        ArgumentNullException.ThrowIfNull(openReader);
        ArgumentNullException.ThrowIfNull(manifests);
        Dictionary<ResourceId, MediaLibraryThreadState> priorStates = previous?.Threads
            .Where(state => state.Complete).ToDictionary(state => state.Thread) ?? new();
        ILookup<ResourceId, MediaLibraryEntry> priorEntries = (previous?.Entries ?? Array.Empty<MediaLibraryEntry>())
            .ToLookup(entry => entry.Thread);

        List<MediaLibraryEntry> entries = new();
        List<MediaLibraryThreadState> states = new();
        List<string> warnings = new();
        bool complete = true;
        // A profile whose archive catalog does not exist yet can still hold downloaded provider media (Danbooru does not
        // create archived threads), so a missing catalog only counts when there is nothing else to show.
        ArchiveReader? reader = null;
        FileNotFoundException? noArchive = null;
        try { reader = await openReader(cancellationToken).ConfigureAwait(false); }
        catch (FileNotFoundException error) when (readBooruCatalog != null || readImportCatalog != null || readFolderSources != null) { noArchive = error; }
        await using ArchiveReader? readerGuard = reader;
        List<ArchiveThreadSummary> threads = new();
        for (int offset = 0; reader != null; offset += ThreadPageSize)
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
                string[] directories = (await reader!.GetMediaDirectoriesAsync(summary.Id, cancellationToken).ConfigureAwait(false))
                    .Distinct(StringComparer.Ordinal).ToArray();
                priorStates.TryGetValue(summary.Id, out MediaLibraryThreadState? prior);
                if (prior != null && prior.Revision == summary.Revision && Unchanged(prior, directories, manifests, cancellationToken))
                {
                    // Same catalog revision, same folders, every manifest file untouched since it was read: nothing to open.
                    entries.AddRange(priorEntries[summary.Id]);
                    states.Add(prior);
                    reused++;
                    done++;
                    progress?.Report(new(done, threads.Count, reused));
                    continue;
                }
                List<MediaAssetSnapshot> threadManifests = new();
                List<MediaLibraryManifestState> manifestStates = new();
                bool threadComplete = true;
                foreach (string directory in directories)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        MediaManifestRead read = await manifests.ReadAsync(directory, cancellationToken).ConfigureAwait(false);
                        MediaAssetSnapshot manifest = read.Snapshot;
                        threadManifests.Add(manifest);
                        manifestStates.Add(new(directory, manifest.Generation) { Identity = read.Identity });
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
                if (threadComplete && prior != null && SameState(prior, state))
                {
                    entries.AddRange(priorEntries[summary.Id]);
                    reused++;
                }
                else
                {
                    ArchivedThreadView? view = await reader!.ReadThreadAsync(summary.Id, cancellationToken).ConfigureAwait(false);
                    if (view == null) { done++; continue; } // Removed between listing and reading.
                    entries.AddRange(EntriesOf(view, threadManifests));
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
        // Downloaded provider posts are always derived again from the local catalog (cheap, no thread revision to reuse).
        // An unreadable catalog keeps what the previous index showed instead of making the local media vanish.
        IReadOnlyList<MediaLibraryManifestState> providerManifests = Array.Empty<MediaLibraryManifestState>();
        if (readBooruCatalog != null)
        {
            BooruCatalogSnapshot? booru = null;
            try { booru = readBooruCatalog(); }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ObjectDisposedException) { }
            if (booru == null)
            {
                entries.AddRange((previous?.Entries ?? Array.Empty<MediaLibraryEntry>()).Where(entry => entry.BooruPostId != null));
                providerManifests = previous?.ProviderManifests ?? providerManifests;
                complete = false;
                warnings.Add("Danbooru: the local catalog could not be read.");
            }
            else
            {
                BooruMediaProjection.Result projected = await BooruMediaProjection.EntriesAsync(booru, manifests, previous, cancellationToken).ConfigureAwait(false);
                entries.AddRange(projected.Entries);
                providerManifests = projected.Manifests;
                int unreadable = projected.Unreadable;
                if (unreadable > 0) { complete = false; warnings.Add("Danbooru: " + unreadable + " media folder(s) could not be read."); }
            }
        }
        // Imported files are derived again from the import catalog each time, like Danbooru media; an unreadable catalog
        // keeps the imports the previous index showed (the index is then incomplete).
        if (readImportCatalog != null)
        {
            Import.ManualImportCatalogSnapshot? imports = null;
            try { imports = readImportCatalog(); }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ObjectDisposedException
                or Import.ManualImportException) { }
            if (imports == null)
            {
                entries.AddRange((previous?.Entries ?? Array.Empty<MediaLibraryEntry>()).Where(entry => entry.ImportItemId != null));
                complete = false;
                warnings.Add("Imports: the import catalog could not be read.");
            }
            else
            {
                ManualImportMediaProjection.Result imported = await ManualImportMediaProjection.EntriesAsync(imports, manifests, previous, cancellationToken).ConfigureAwait(false);
                entries.AddRange(imported.Entries);
                // The manifest states of the batches ride along with the provider manifests (the same list in the cache file);
                // a folder is named once, so a state kept from the previous index never doubles one that was just read.
                HashSet<string> importFolders = imported.Manifests.Select(state => state.Directory).ToHashSet(StringComparer.Ordinal);
                providerManifests = providerManifests.Where(state => !importFolders.Contains(state.Directory)).Concat(imported.Manifests).ToArray();
                if (imported.Unreadable > 0) { complete = false; warnings.Add("Imports: " + imported.Unreadable + " media folder(s) could not be read."); }
            }
        }
        // Folder source files are projected again from the shadow catalog each time (no file is opened); an unreadable catalog
        // keeps the folder files the previous index showed (the index is then incomplete).
        if (readFolderSources != null)
        {
            FolderSources.FolderSourceCatalogSnapshot? folders = null;
            try { folders = readFolderSources(); }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ObjectDisposedException
                or Microsoft.Data.Sqlite.SqliteException) { }
            if (folders == null)
            {
                entries.AddRange((previous?.Entries ?? Array.Empty<MediaLibraryEntry>()).Where(entry => entry.FolderItemId != null));
                complete = false;
                warnings.Add("Folders: the folder source catalog could not be read.");
            }
            else entries.AddRange(FolderSourceProjection.EntriesOf(folders, cancellationToken));
        }
        if (noArchive != null && entries.Count == 0) throw noArchive;
        return new MediaLibrarySnapshot(entries, states, warnings, complete, providerManifests);
    }

    // Reuse by generation: the identity is bookkeeping about how the generation was learned, not part of what was read.
    private static bool SameState(MediaLibraryThreadState prior, MediaLibraryThreadState current) =>
        prior.Revision == current.Revision && prior.Manifests.Count == current.Manifests.Count
        && prior.Manifests.Zip(current.Manifests).All(pair => pair.First.Directory == pair.Second.Directory
            && pair.First.Generation == pair.Second.Generation && pair.First.Generation != null);

    // True only when every folder is the same as before, in the same order, and each manifest file still has the identity
    // it had while the recorded generation was read. Anything unknown (no identity, a custom opener) means "read it".
    private static bool Unchanged(MediaLibraryThreadState prior, IReadOnlyList<string> directories, MediaManifestCache manifests,
        CancellationToken cancellationToken)
    {
        if (!manifests.UsesIdentity || prior.Manifests.Count != directories.Count) return false;
        for (int index = 0; index < directories.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MediaLibraryManifestState state = prior.Manifests[index];
            if (!string.Equals(state.Directory, directories[index], StringComparison.Ordinal) || state.Generation == null
                || state.Identity is not { } identity || manifests.Probe(directories[index]) != identity) return false;
        }
        return true;
    }

    private static string Describe(ResourceId id) => id.Source + " /" + id.Board + "/ " + id.Thread;

    /// <summary>
    /// The index entry of one gallery occurrence of a saved file. The thread's own media tab uses it to hand its selection to the
    /// same tag dialog and export plan as the media page, so both describe an occurrence with exactly the same facts.
    /// </summary>
    internal static MediaLibraryEntry EntryOf(ArchiveThreadSummary summary, GalleryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(entry);
        CapturedPost? post = entry.Post;
        return new MediaLibraryEntry(summary.Id, summary.Title, summary.Revision, summary.FirstSeenAt, summary.LastCapturedAt,
            post?.PostId, post?.Ordinal, post?.PostedAt, entry.Attachment?.Position, entry.AttachmentResolution?.Binding?.Reference.RevisionKey,
            entry.Manifest.Directory, entry.Asset.AssetId, entry.Asset.Sha256.ToLowerInvariant(), entry.Asset.Length,
            entry.Asset.MediaIdentifier, entry.Asset.Variant, entry.Asset.OriginalFileName ?? Path.GetFileName(entry.Asset.Path),
            entry.Kind, MediaLibraryPresence.Present, entry.Unassigned, entry.Historical,
            entry.LocalTagEligible ? entry.Asset.RequestedFileName : null);
    }

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
            yield return EntryOf(summary, entry);
        }
        foreach (CapturedPost post in posts.Values.OrderBy(post => post.Ordinal).ThenBy(post => post.PostId, StringComparer.Ordinal))
            foreach (CapturedAttachment attachment in (post.Attachments ?? Array.Empty<CapturedAttachment>()).OrderBy(item => item.Position))
            {
                if (!ThreadMediaPositions.Includes(attachment) || found.Contains((post.PostId, attachment.Position))) continue;
                string? name = attachment.OriginalFileName;
                yield return new MediaLibraryEntry(summary.Id, summary.Title, summary.Revision, summary.FirstSeenAt, summary.LastCapturedAt,
                    post.PostId, post.Ordinal, post.PostedAt, attachment.Position, attachment.AttachmentRevisionKey,
                    null, null, null, null, attachment.MediaId, attachment.Variant, name,
                    SavedMediaTypes.Classify(name ?? attachment.Url ?? string.Empty), MediaLibraryPresence.Missing, false, false);
            }
    }
}
