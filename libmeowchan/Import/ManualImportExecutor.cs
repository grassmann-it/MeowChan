using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;

namespace MeowChan.Import;

// Contract: manual-import-v1.md §6 (order of writes, cancellation, interruption).

/// <summary>What the user confirmed for one batch.</summary>
internal sealed record ManualImportOptions(string BatchName, IReadOnlyList<string> Tags, long ReserveBytes = 0);

/// <summary>Progress of the copy: files finished (copied or failed) of the new files.</summary>
internal sealed record ManualImportCopyProgress(int FilesDone, int FilesTotal, long BytesDone, long BytesTotal);

/// <summary>The written batch and what happened to its files.</summary>
internal sealed record ManualImportResult(ManualImportBatch Batch, ManualImportCatalogSnapshot Catalog)
{
    internal int Copied => Batch.Count(ManualImportItemState.Committed);
    internal int Linked => Batch.Count(ManualImportItemState.Duplicate);
    internal int Failed => Batch.Count(ManualImportItemState.Failed);
}

/// <summary>
/// Copies the new files of a checked plan into a fresh batch folder, in this order: (1) the catalog records the batch as
/// copying with every item planned, before any byte is written; (2) each new file is copied and published in the batch
/// manifest with the SHA-256 measured by the check as its expectation (a changed source fails visibly, the rest goes on);
/// progress is written to the catalog in groups; (3) the catalog records the batch as completed. Source files are only
/// read: never moved, renamed or deleted. Cancellation stops after the current file; finished files stay. A stop between
/// (1) and (3) leaves an interrupted batch that <see cref="FinishInterruptedAsync"/> completes from the manifest.
/// </summary>
internal sealed class ManualImportExecutor
{
    internal const int ProgressFlushFiles = 50;
    internal static readonly TimeSpan ProgressFlushInterval = TimeSpan.FromSeconds(2);

    private readonly ManualImportCatalogStore _catalog;
    private readonly MediaStore _media;
    private readonly TimeProvider _clock;
    private readonly Func<string, long?> _freeSpace;

    /// <param name="catalog">The profile's import catalog.</param>
    /// <param name="media">The media store that publishes the copies.</param>
    /// <param name="clock">Time source of batch timestamps.</param>
    /// <param name="freeSpace">Free bytes of the volume of a folder; null when unknown (then no space check).</param>
    internal ManualImportExecutor(ManualImportCatalogStore catalog, MediaStore media, TimeProvider? clock = null,
        Func<string, long?>? freeSpace = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _media = media ?? throw new ArgumentNullException(nameof(media));
        _clock = clock ?? TimeProvider.System;
        _freeSpace = freeSpace ?? AvailableFreeSpace;
    }

    /// <summary>Test seam: runs before every catalog write after the plan (progress and completion); throwing simulates a stop.</summary>
    internal Action<ManualImportBatch>? BeforeCatalogUpdateForTests { get; set; }

    internal async Task<ManualImportResult> ExecuteAsync(ManualImportPlan plan, ManualImportOptions options, string downloadRoot,
        ProfileAdmissionToken admission, IProgress<ManualImportCopyProgress>? progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadRoot);
        if (plan.Status != ManualImportPlanStatus.Ready) throw new ManualImportException(ManualImportError.InvalidRequest);
        string name = (options.BatchName ?? string.Empty).Trim();
        if (!ManualImportCatalogStore.ValidName(name, ManualImportCatalogStore.MaximumBatchNameLength))
            throw new ManualImportException(ManualImportError.InvalidRequest);
        string[] tags = NormalizeTags(options.Tags, _catalog.MaximumTagsPerBatch);
        ManualImportCandidate[] fresh = plan.New.Where(item => item.Sha256 != null).ToArray();
        ManualImportCandidate[] linked = plan.Items.Where(item => item.Sha256 != null && item.Disposition
            is ManualImportDisposition.AlreadyInLibrary or ManualImportDisposition.DuplicateInSelection).ToArray();
        if (fresh.Length == 0 && linked.Length == 0) throw new ManualImportException(ManualImportError.InvalidRequest);
        token.ThrowIfCancellationRequested();

        // Finish whatever an earlier stop left half done before counting room for this batch.
        await FinishInterruptedAsync(admission, token).ConfigureAwait(false);
        ManualImportCatalogSnapshot catalog = _catalog.Read(token);
        if (catalog.ItemCount + fresh.Length + linked.Length > _catalog.MaximumTotalItems)
            throw new ManualImportException(ManualImportError.TooManyImportedFiles);
        if (catalog.Batches.Count >= _catalog.MaximumBatchCount) throw new ManualImportException(ManualImportError.TooManyBatches);
        long needed = fresh.Sum(item => item.Length);
        if (needed > 0 && _freeSpace(downloadRoot) is long free && free - Math.Max(0, options.ReserveBytes) < needed)
            throw new ManualImportException(ManualImportError.NotEnoughSpace);

        Guid batchId = Guid.NewGuid();
        string directory = ManualImportIdentity.BatchDirectory(downloadRoot, batchId);
        List<ManualImportItem> items = new();
        Dictionary<Guid, ManualImportCandidate> sources = new();
        foreach (ManualImportCandidate candidate in plan.Items.Where(item => item.Sha256 != null && item.Importable))
        {
            Guid id = Guid.NewGuid();
            bool copy = candidate.Disposition == ManualImportDisposition.New;
            items.Add(new ManualImportItem(id, copy ? ManualImportItemState.Planned : ManualImportItemState.Duplicate,
                candidate.Sha256!, candidate.Length, candidate.Name, candidate.Kind, candidate.Extension));
            if (copy) sources[id] = candidate;
        }
        ManualImportBatch batch = new(batchId, name, Now(), null, ManualImportBatchState.Copying, directory, plan.SourceLabel,
            plan.IncludeOtherFiles, false, tags, tags.Length == 0, items.ToArray());
        catalog = _catalog.PutBatchAdmitted(batch, catalog.Revision, admission, token);

        int done = 0, unflushed = 0;
        long bytesDone = 0;
        bool cancelled = false, stopped = false;
        Stopwatch sinceFlush = Stopwatch.StartNew();
        progress?.Report(new(0, sources.Count, 0, needed));
        for (int index = 0; index < items.Count; index++)
        {
            ManualImportItem item = items[index];
            if (item.State != ManualImportItemState.Planned) continue;
            if (stopped || token.IsCancellationRequested) { cancelled |= token.IsCancellationRequested; break; }
            ManualImportCandidate source = sources[item.Id];
            ManualImportItem outcome;
            try
            {
                outcome = await CopyAsync(item, source, directory, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // The current file was not published: it is left out of the batch like the files after it.
                cancelled = true;
                break;
            }
            if (outcome.Failure == ManualImportFailure.NoSpace) stopped = true; // Every later copy would fail the same way.
            items[index] = outcome;
            done++;
            unflushed++;
            bytesDone += item.Length;
            progress?.Report(new(done, sources.Count, bytesDone, needed));
            if (unflushed >= ProgressFlushFiles || sinceFlush.Elapsed >= ProgressFlushInterval)
            {
                batch = batch with { Items = items.ToArray() };
                BeforeCatalogUpdateForTests?.Invoke(batch);
                catalog = _catalog.PutBatchAdmitted(batch, catalog.Revision, admission, CancellationToken.None);
                unflushed = 0;
                sinceFlush.Restart();
            }
        }
        // Files never started (cancelled or stopped) are no part of the batch; they were never copied.
        ManualImportItem[] final = items.Where(item => item.State != ManualImportItemState.Planned).ToArray();
        batch = batch with
        {
            Items = final, State = ManualImportBatchState.Completed, CompletedAtUtc = Max(Now(), batch.CreatedAtUtc), Cancelled = cancelled,
            TagsApplied = tags.Length == 0 || !final.Any(item => item.State == ManualImportItemState.Committed)
        };
        // The completion is not cancellable: it records what really happened.
        BeforeCatalogUpdateForTests?.Invoke(batch);
        catalog = _catalog.PutBatchAdmitted(batch, catalog.Revision, admission, CancellationToken.None);
        return new ManualImportResult(batch, catalog);
    }

    private async Task<ManualImportItem> CopyAsync(ManualImportItem item, ManualImportCandidate source, string directory, CancellationToken token)
    {
        FileStream stream;
        try
        {
            stream = ManualImportPlanner.OpenSource(source.SourcePath);
        }
        // Replaced by a link, folder, pipe or device since the check: changed, and nothing was opened blocking.
        catch (InvalidDataException)
        { return item with { State = ManualImportItemState.Failed, Failure = ManualImportFailure.Changed }; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        { return item with { State = ManualImportItemState.Failed, Failure = ManualImportFailure.Unreadable }; }
        await using (stream)
        {
            try
            {
                MediaCommitResult committed = await _media.CommitLocalFileAsync(directory, item.RequestedFileName, item.MediaIdentifier,
                    item.Name, stream, item.Sha256, item.Length, token).ConfigureAwait(false);
                return item with { State = ManualImportItemState.Committed, AssetId = committed.AssetId };
            }
            catch (InvalidDataException) { return item with { State = ManualImportItemState.Failed, Failure = ManualImportFailure.Changed }; }
            catch (IOException error) when (IsDiskFull(error))
            { return item with { State = ManualImportItemState.Failed, Failure = ManualImportFailure.NoSpace }; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A read failure of the source and a write failure of the folder look alike; the source is the likelier one
                // only when the folder is still writable, which a later file shows. Keep the honest generic code.
                return item with { State = ManualImportItemState.Failed, Failure = ManualImportFailure.Store };
            }
        }
    }

    /// <summary>
    /// Completes batches a stop left in <see cref="ManualImportBatchState.Copying"/>: a planned item whose file the batch
    /// manifest records with exactly its id, length and SHA-256 becomes committed; any other planned item failed as
    /// interrupted (nothing of it is published). Never copies, deletes or guesses from file names. Idempotent.
    /// </summary>
    internal async Task<int> FinishInterruptedAsync(ProfileAdmissionToken admission, CancellationToken token)
    {
        ManualImportCatalogSnapshot catalog = _catalog.Read(token);
        int finished = 0;
        foreach (ManualImportBatch interrupted in catalog.Batches.Where(batch => batch.State == ManualImportBatchState.Copying).ToArray())
        {
            token.ThrowIfCancellationRequested();
            MediaAssetSnapshot? manifest = null;
            try { manifest = await MediaAssetReader.OpenAsync(interrupted.Directory, token).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
            // A damaged or unreadable manifest hides what was published: leave the batch for a later attempt.
            if (manifest == null || manifest.Status == MediaManifestStatus.Corrupt) continue;
            MediaAssetReference[] assets = AllAssets(manifest);
            ManualImportItem[] items = interrupted.Items.Select(item =>
            {
                if (item.State != ManualImportItemState.Planned) return item;
                MediaAssetReference? asset = manifest.Status == MediaManifestStatus.Available
                    ? assets.FirstOrDefault(candidate => candidate.MediaIdentifier == item.MediaIdentifier
                        && candidate.RequestedFileName == item.RequestedFileName && candidate.Variant == ManualImportIdentity.Variant
                        && candidate.Length == item.Length && string.Equals(candidate.Sha256, item.Sha256, StringComparison.Ordinal))
                    : null;
                return asset != null ? item with { State = ManualImportItemState.Committed, AssetId = asset.AssetId }
                    : item with { State = ManualImportItemState.Failed, Failure = ManualImportFailure.Interrupted };
            }).ToArray();
            ManualImportBatch completed = interrupted with
            {
                Items = items, State = ManualImportBatchState.Completed, CompletedAtUtc = Max(Now(), interrupted.CreatedAtUtc),
                TagsApplied = interrupted.TagsApplied || !items.Any(item => item.State == ManualImportItemState.Committed)
            };
            catalog = _catalog.PutBatchAdmitted(completed, catalog.Revision, admission, CancellationToken.None);
            finished++;
        }
        return finished;
    }

    internal static MediaAssetReference[] AllAssets(MediaAssetSnapshot manifest)
    {
        List<MediaAssetReference> assets = new(manifest.Count);
        for (int offset = 0; offset < manifest.Count; offset += 500) assets.AddRange(manifest.GetPage(offset, 500));
        return assets.ToArray();
    }

    /// <summary>Records that the batch tags were written to the tag store (after the tag write succeeded).</summary>
    internal ManualImportBatch MarkTagsApplied(Guid batchId, ProfileAdmissionToken admission, CancellationToken token)
    {
        ManualImportCatalogSnapshot catalog = _catalog.Read(token);
        ManualImportBatch batch = catalog.Find(batchId) ?? throw new ManualImportException(ManualImportError.Conflict);
        if (batch.TagsApplied) return batch;
        ManualImportBatch next = batch with { TagsApplied = true };
        _catalog.PutBatchAdmitted(next, catalog.Revision, admission, token);
        return next;
    }

    /// <summary>
    /// The loose-file tag subjects of the copied files of a batch: the exact manifest records the import published.
    /// The tag store registers them as local occurrences; the host revalidates them against the catalog before the write.
    /// </summary>
    internal static IReadOnlyList<MediaTagSubject> TagSubjects(ManualImportBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        List<MediaTagSubject> subjects = new();
        foreach (ManualImportItem item in batch.Items.Where(item => item.State == ManualImportItemState.Committed))
            if (LocalMediaObservation.TryCreate(batch.Thread, batch.Directory, item.RequestedFileName, item.AssetId, item.Length, item.Sha256,
                out LocalMediaObservation? observation))
                subjects.Add(new MediaTagSubject.Local(observation!, null));
        return subjects;
    }

    internal static string[] NormalizeTags(IReadOnlyList<string>? tags, int maximumTags = ManualImportCatalogStore.DefaultMaximumTags)
    {
        List<string> names = new();
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (string raw in tags ?? Array.Empty<string>())
        {
            if (!PrivateTagName.TryCreate(raw, out PrivateTagName? name, out _) || name == null)
                throw new ManualImportException(ManualImportError.InvalidRequest);
            if (keys.Add(name.Key)) names.Add(name.Display);
        }
        if (names.Count > maximumTags) throw new ManualImportException(ManualImportError.InvalidRequest);
        return names.ToArray();
    }

    private DateTimeOffset Now()
    {
        DateTimeOffset now = _clock.GetUtcNow().ToUniversalTime();
        // The catalog keeps round-trip text; drop sub-tick noise and keep UTC.
        return new DateTimeOffset(now.UtcDateTime, TimeSpan.Zero);
    }

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left >= right ? left : right;

    private static bool IsDiskFull(IOException error) =>
        error.HResult is unchecked((int)0x80070070) or unchecked((int)0x80070027) // Windows ERROR_DISK_FULL, ERROR_HANDLE_DISK_FULL
        || (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) && error.HResult == 28; // ENOSPC

    private static long? AvailableFreeSpace(string folder)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(folder));
            // The deepest existing ancestor decides the volume (the download folder may not exist yet).
            string probe = Path.GetFullPath(folder);
            while (!Directory.Exists(probe) && Path.GetDirectoryName(probe) is { } parent && parent != probe) probe = parent;
            return root == null ? null : new DriveInfo(probe).AvailableFreeSpace;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
}
