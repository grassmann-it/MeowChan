using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;

namespace MeowChan.Desktop;

internal sealed class ArchiveCleanupExecutionPlan
{
    private int _used;
    internal ArchiveCleanupExecutionPlan(ArchiveCleanupPreview preview, IReadOnlyList<ResourceId> selected,
        IReadOnlyList<ArchiveCleanupPreviewRow> rows,
        IReadOnlyDictionary<ResourceId, IReadOnlyList<ArchiveCleanupDirectoryFiles>> files,
        IReadOnlyList<string> reasons)
    {
        Preview = preview; SelectedIds = selected; Rows = rows; Files = files; Reasons = reasons;
        PostCount = rows.Sum(row => row.PostCount);
        AssetCount = rows.Sum(row => row.Media.SavedAssetCount ?? 0);
        OwnedBytes = files.Values.SelectMany(value => value).SelectMany(directory => directory.Files).Sum(file => file.Length);
    }
    internal ArchiveCleanupPreview Preview { get; }
    internal IReadOnlyList<ArchiveCleanupPreviewRow> Rows { get; }
    internal IReadOnlyDictionary<ResourceId, IReadOnlyList<ArchiveCleanupDirectoryFiles>> Files { get; }
    public IReadOnlyList<ResourceId> SelectedIds { get; }
    public IReadOnlyList<string> Reasons { get; }
    public bool CanExecute => SelectedIds.Count > 0 && Reasons.Count == 0 && Rows.Count == SelectedIds.Count;
    public int PostCount { get; }
    public int AssetCount { get; }
    public long OwnedBytes { get; }
    internal bool Consume() => Interlocked.Exchange(ref _used, 1) == 0;
}

internal sealed record ArchiveCleanupExecutionResult(IReadOnlyList<ResourceId> DeletedThreads,
    IReadOnlyList<ResourceId> RemainingThreads, bool RequiresNewPreview, bool Canceled,
    IReadOnlyList<string> Errors, int DeletedFileCount, long DeletedBytes);

internal sealed partial class DesktopSessionHost
{
    internal async Task<ArchiveCleanupExecutionPlan> PrepareArchiveCleanupExecutionAsync(ArchiveCleanupPreview preview,
        IReadOnlyCollection<ResourceId> selectedIds, CancellationToken token = default,
        IReadOnlyCollection<ResourceId>? currentOpenThreadIds = null)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(selectedIds);
        ResourceId[] selected = selectedIds.ToArray();
        ResourceId[] currentOpen = (currentOpenThreadIds ?? Array.Empty<ResourceId>()).ToArray();
        List<string> errors = new();
        List<ArchiveCleanupPreviewRow> rows = new();
        Dictionary<ResourceId, IReadOnlyList<ArchiveCleanupDirectoryFiles>> files = new();
        await _commands.WaitAsync(token).ConfigureAwait(false);
        try
        {
            EnsureReady();
            if (!preview.OpenThreadIds.ToHashSet().SetEquals(currentOpen))
                throw new InvalidDataException("Open reader, gallery or export targets changed. Create a new preview.");
            if (preview.ProfileBinding != ArchiveCleanupProfileBinding || !preview.Complete || preview.IsStale || selected.Length == 0 || selected.Distinct().Count() != selected.Length)
                throw new InvalidDataException("Select candidates from a complete, current cleanup preview.");
            foreach (ResourceId id in selected)
            {
                ArchiveCleanupPreviewRow? row = preview.Rows.SingleOrDefault(item => item.Id == id);
                if (row == null || row.Eligibility != ArchiveCleanupEligibility.Candidate)
                    throw new InvalidDataException("The frozen selection contains an unavailable or protected thread.");
                rows.Add(row);
            }
            rows = OrderCleanupRows(rows);
            await using IAsyncDisposable? reservation = await _registry.TryReserveArchiveCleanupAsync(
                CleanupResources(rows), token).ConfigureAwait(false);
            if (reservation == null) throw new InvalidDataException("Work changed. Review the cleanup preview again.");
            ArchiveCleanupPreview fresh = await CollectArchiveCleanupPreviewCoreAsync(preview.OpenThreadIds, token).ConfigureAwait(false);
            RequireCleanupPreview(preview, fresh);
            if (fresh.EvidenceToken != preview.EvidenceToken)
                throw new InvalidDataException("The archive changed. Review the cleanup preview again.");
            foreach (ArchiveCleanupPreviewRow row in rows)
            {
                RequireCleanupRow(row, fresh);
                files.Add(row.Id, await ArchiveCleanupExecutionFiles.PrepareAsync(row, ProfileDirectory, token).ConfigureAwait(false));
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { errors.Add(error.Message); }
        finally { _commands.Release(); }
        return new(preview, Array.AsReadOnly(selected), Array.AsReadOnly(rows.ToArray()), files,
            Array.AsReadOnly(errors.ToArray()));
    }

    internal async Task<ArchiveCleanupExecutionResult> ExecuteArchiveCleanupAsync(ArchiveCleanupExecutionPlan plan,
        CancellationToken token = default, IReadOnlyCollection<ResourceId>? currentOpenThreadIds = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ResourceId[] currentOpen = (currentOpenThreadIds ?? Array.Empty<ResourceId>()).ToArray();
        List<ResourceId> deleted = new();
        List<string> errors = new();
        int deletedFiles = 0;
        long deletedBytes = 0;
        bool canceled = false;
        bool commandHeld = false;
        try
        {
            if (!plan.CanExecute || !plan.Consume())
                throw new InvalidDataException("This cleanup plan is unavailable or already used. Create a new preview.");
            await _commands.WaitAsync(token).ConfigureAwait(false);
            commandHeld = true;
            EnsureReady();
            if (!plan.Preview.OpenThreadIds.ToHashSet().SetEquals(currentOpen))
                throw new InvalidDataException("Open reader, gallery or export targets changed. Create a new preview.");
            if (plan.Preview.ProfileBinding != ArchiveCleanupProfileBinding)
                throw new InvalidDataException("The cleanup plan belongs to another profile session.");
            await using IAsyncDisposable? reservation = await _registry.TryReserveArchiveCleanupAsync(
                CleanupResources(plan.Rows), token).ConfigureAwait(false);
            if (reservation == null) throw new InvalidDataException("Work changed. Review the cleanup preview again.");
            await using IAsyncDisposable leases = await ArchiveCleanupExecutionFiles.AcquireAsync(
                plan.Rows.SelectMany(row => row.Media.Directories), ProfileDirectory, token).ConfigureAwait(false);
            foreach (ArchiveCleanupPreviewRow row in plan.Rows)
            {
                token.ThrowIfCancellationRequested();
                EnsureReady();
                ArchiveCleanupPreview fresh = await CollectArchiveCleanupPreviewCoreAsync(plan.Preview.OpenThreadIds, token, row).ConfigureAwait(false);
                RequireCleanupPreview(plan.Preview, fresh);
                RequireCleanupRow(row, fresh);
                ArchiveDeletionPlan catalog = await _content.Catalog.PrepareDeletionAsync(new[] { row.Id }, token).ConfigureAwait(false);
                if (catalog.Threads.Count != 1 || catalog.SharedMediaDirectories.Count != 0
                    || catalog.Threads[0].Summary != row.Summary)
                    throw new InvalidDataException("Archive identity or directory ownership changed.");
                ArchiveDeletedThread expected = catalog.Threads[0];
                IReadOnlyList<ArchiveCleanupDirectoryFiles> currentFiles = await ArchiveCleanupExecutionFiles.PrepareAsync(
                    row, ProfileDirectory, token).ConfigureAwait(false);
                if (!SameCleanupFiles(plan.Files[row.Id], currentFiles))
                    throw new InvalidDataException("The owned file inventory changed after confirmation.");

                // Protection/work and catalog checks stay fresh before each physical file deletion.
                // The media inventory itself naturally changes after our own first deletion.
                async Task RevalidateGuard()
                {
                    token.ThrowIfCancellationRequested();
                    EnsureReady();
                    ArchiveCleanupGuards guard = await ReadArchiveCleanupGuardTokensAsync(plan.Preview.OpenThreadIds, token).ConfigureAwait(false);
                    if (!guard.Reliable || guard.ProtectionToken != plan.Preview.ProtectionToken || guard.WorkToken != plan.Preview.WorkToken)
                        throw new InvalidDataException("Cleanup protection or work changed. Create a new preview.");
                    ArchiveDeletionPlan now = await _content.Catalog.PrepareDeletionAsync(new[] { row.Id }, token).ConfigureAwait(false);
                    if (!SameCatalogPlan(catalog, now) || now.SharedMediaDirectories.Count != 0)
                        throw new InvalidDataException("Archive ownership changed during cleanup.");
                }
                await ArchiveCleanupExecutionFiles.DeleteAsync(currentFiles, ProfileDirectory, RevalidateGuard,
                    bytes => { deletedFiles++; deletedBytes += bytes; }, token).ConfigureAwait(false);
                if (currentFiles.All(directory => directory.Files.Count == 0))
                {
                    // A zero-file candidate still needs its storage/anchor evidence immediately before loss of catalog data.
                    ArchiveCleanupPreview zeroRecheck = await CollectArchiveCleanupPreviewCoreAsync(plan.Preview.OpenThreadIds, token, row).ConfigureAwait(false);
                    RequireCleanupPreview(plan.Preview, zeroRecheck);
                    RequireCleanupRow(row, zeroRecheck);
                }
                await RevalidateGuard().ConfigureAwait(false);
                ArchiveCleanupExecutionFiles.ValidateCompleted(currentFiles, ProfileDirectory);
                await _content.Catalog.DeleteCleanupThreadAsync(expected, token).ConfigureAwait(false);
                deleted.Add(row.Id);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { canceled = true; }
        catch (Exception error) { errors.Add(error.Message); }
        finally
        {
            if (commandHeld) { SignalChanged(); _commands.Release(); }
        }
        return new(Array.AsReadOnly(deleted.ToArray()), Array.AsReadOnly(plan.SelectedIds.Except(deleted).ToArray()),
            true, canceled, Array.AsReadOnly(errors.ToArray()), deletedFiles, deletedBytes);
    }

    private static List<ArchiveCleanupPreviewRow> OrderCleanupRows(IReadOnlyList<ArchiveCleanupPreviewRow> rows)
    {
        List<ArchiveCleanupPreviewRow> remaining = rows.ToList();
        List<ArchiveCleanupPreviewRow> ordered = new();
        while (remaining.Count != 0)
        {
            HashSet<ResourceId> witnesses = remaining.SelectMany(row => row.Media.Directories)
                .Where(directory => directory.StorageAnchor != null).Select(directory => directory.StorageAnchor!.Thread).ToHashSet();
            ArchiveCleanupPreviewRow? next = remaining.FirstOrDefault(row => !witnesses.Contains(row.Id));
            if (next == null)
                throw new InvalidDataException("Selected threads depend on each other's storage evidence. Review the selection.");
            ordered.Add(next); remaining.Remove(next);
        }
        return ordered;
    }

    private static ArchiveCleanupResource[] CleanupResources(IEnumerable<ArchiveCleanupPreviewRow> rows)
        => rows.Select(row => new ArchiveCleanupResource(row.Id, row.Summary.Url,
            Array.AsReadOnly(row.Media.Directories.SelectMany(directory => directory.StorageAnchor == null
                ? new[] { directory.Directory } : new[] { directory.Directory, directory.StorageAnchor.Directory })
                .Distinct(ArchiveCleanupExecutionFiles.PathComparer).ToArray()))).ToArray();

    private static void RequireCleanupPreview(ArchiveCleanupPreview original, ArchiveCleanupPreview fresh)
    {
        if (!fresh.Complete || fresh.IsStale || fresh.ProfileBinding != original.ProfileBinding
            || fresh.PolicyVersion != original.PolicyVersion || fresh.ProtectionToken != original.ProtectionToken
            || fresh.WorkToken != original.WorkToken)
            throw new InvalidDataException("The cleanup prerequisites changed. Create a new preview.");
    }

    private static void RequireCleanupRow(ArchiveCleanupPreviewRow original, ArchiveCleanupPreview fresh)
    {
        ArchiveCleanupPreviewRow? row = fresh.Rows.SingleOrDefault(item => item.Id == original.Id);
        if (row == null || row.Eligibility != ArchiveCleanupEligibility.Candidate || row.EvidenceToken != original.EvidenceToken
            || row.Media.SavedAssetCount is not (>= 0 and <= ArchiveCleanupPreview.MaximumSavedAssets))
            throw new InvalidDataException("A selected thread changed. Create a new preview.");
    }

    private static bool SameCleanupFiles(IReadOnlyList<ArchiveCleanupDirectoryFiles> expected,
        IReadOnlyList<ArchiveCleanupDirectoryFiles> actual) => expected.Count == actual.Count
        && expected.Zip(actual).All(pair => pair.First.Evidence.Fingerprint == pair.Second.Evidence.Fingerprint
            && pair.First.Files.SequenceEqual(pair.Second.Files));
}
