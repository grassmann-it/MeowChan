using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Import;
using MeowChan.Storage;
using MeowChan.Tags;

namespace MeowChan.Desktop;

/// <summary>What one finished import did, for the dialog's summary. Tag failures never undo the copied files.</summary>
internal sealed record ManualImportOutcome(ManualImportBatch Batch, bool TagsRequested, bool TagsApplied, PrivateTagStoreError? TagError);

/// <summary>
/// Host commands of the manual import (contract manual-import-v1). The check is read-only and needs no admission. The copy
/// runs as one admitted interactive command (catalog plan, copies, completion); the batch tags follow as the ordinary
/// loose-file tag writes, each revalidated against the import catalog and the batch manifest before it is written.
/// Diagnostics name counts only, never paths or file names.
/// </summary>
internal sealed partial class DesktopSessionHost
{
    private readonly object _manualImportSync = new();
    private ManualImportCatalogStore? _manualImportStore;
    private readonly MediaStore _manualImportMedia = new();

    private ManualImportCatalogStore ManualImportStore
    {
        get
        {
            lock (_manualImportSync)
            {
                _manualImportStore ??= new(_profileLease.ProfileDirectory, _profileLease);
                _manualImportStore.DocumentLimitBytes = (long)Config.ImportCatalogLimitMiB * 1024 * 1024;
                _manualImportStore.MaximumTotalItems = Config.ImportMaxTotalFiles;
                _manualImportStore.MaximumBatchCount = Config.ImportMaxBatches;
                _manualImportStore.MaximumTagsPerBatch = Config.ImportMaxTagsPerBatch;
                return _manualImportStore;
            }
        }
    }

    /// <summary>The download folder that holds <c>meowchan-import/</c>.</summary>
    internal string ManualImportDownloadRoot => Path.GetFullPath(Config.DownloadPath);

    /// <summary>The folder the next batches are written to (shown in the dialog; never stored per source).</summary>
    internal string ManualImportFolder => ManualImportIdentity.ImportRoot(ManualImportDownloadRoot);

    /// <summary>
    /// The import catalog for read-only views (media page, gallery, tag revalidation). Null when it cannot be read; a
    /// damaged or newer catalog is never turned into "no imports".
    /// </summary>
    internal ManualImportCatalogSnapshot? ReadManualImportCatalog()
    {
        if (_disposed) return null;
        try { return ManualImportStore.Read(); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
            or ObjectDisposedException or ManualImportException) { return null; }
    }

    /// <summary>Why imports are blocked right now (damaged or newer catalog), or null when they can run.</summary>
    internal ManualImportError? ManualImportBlocked()
    {
        try { ManualImportStore.Read(); return null; }
        catch (ManualImportException error) { return error.Error; }
        // As in ReadManualImportCatalog above: a directory or link in place of the catalog reports "not a regular file"
        // as InvalidDataException. That is a blocked catalog, not an error for the caller to let escape.
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        { return ManualImportError.CatalogDamaged; }
    }

    /// <summary>Checks a selection read-only with the configured limits; the download folder itself is never imported.</summary>
    internal Task<ManualImportPlan> PlanManualImportAsync(ManualImportRequest request,
        Func<CancellationToken, Task<ManualImportKnownMedia>> knownMedia, IProgress<ManualImportCheckProgress>? progress,
        CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ManualImportLimits limits = Config.ImportLimits;
        string root = ManualImportDownloadRoot;
        return Task.Run(() => ManualImportPlanner.PlanAsync(request, limits, root, knownMedia, progress, token), token);
    }

    /// <summary>Test seam: replaces the volume free-space probe of the import.</summary>
    internal Func<string, long?>? ManualImportFreeSpaceForTests { get; set; }

    /// <summary>
    /// Copies the new files of a checked plan into a fresh batch and then gives the batch tags to the copied files.
    /// Cancellation stops after the current file. Tags are best effort after the copy: a refused tag write leaves the
    /// files imported and the batch marked as "tags not applied".
    /// </summary>
    internal async Task<ManualImportOutcome> RunManualImportAsync(ManualImportPlan plan, ManualImportOptions options,
        IProgress<ManualImportCopyProgress>? progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(options);
        ManualImportCatalogStore store = ManualImportStore;
        ManualImportExecutor executor = new(store, _manualImportMedia, freeSpace: ManualImportFreeSpaceForTests);
        string root = ManualImportDownloadRoot;
        ManualImportOptions effective = options with { ReserveBytes = Math.Max(0, Config.MinimumFreeSpaceGiB) * 1024L * 1024 * 1024 };
        ManualImportResult result = await RunInteractiveWithAdmissionAsync(nameof(RunManualImportAsync), "manual-import",
            admission => Task.Run(() => executor.ExecuteAsync(plan, effective, root, admission, progress, token), CancellationToken.None))
            .ConfigureAwait(false);
        SignalChanged();
        ManualImportBatch batch = result.Batch;
        if (batch.TagsApplied || batch.Tags.Count == 0) return new(batch, batch.Tags.Count > 0, batch.TagsApplied, null);
        IReadOnlyList<MediaTagSubject> subjects = ManualImportExecutor.TagSubjects(batch);
        try
        {
            foreach (string tag in batch.Tags)
                await DefineAndSetMediaSubjectTagsAsync(tag, subjects, null, CancellationToken.None).ConfigureAwait(false);
            batch = await RunInteractiveWithAdmissionAsync("manual-import-tags", ManualImportIdentity.Format(batch.Id),
                admission => Task.Run(() => executor.MarkTagsApplied(batch.Id, admission, CancellationToken.None))).ConfigureAwait(false);
            return new(batch, true, true, null);
        }
        catch (PrivateTagStoreException error) { return new(batch, true, false, error.Error); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
            or ManualImportException or DesktopSessionBusyException or InvalidOperationException)
        {
            _logger.Warning("Manual import tags were not applied: {Error}", error.GetType().Name);
            return new(batch, true, false, null);
        }
    }
}
