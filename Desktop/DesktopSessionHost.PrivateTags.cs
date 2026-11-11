using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;

namespace MeowChan.Desktop;

internal enum PrivateTagLoadState { Ready, Unavailable }
// Unavailable never supplies an empty replacement vocabulary; callers must require Ready.
internal sealed record PrivateTagLoadSnapshot(PrivateTagLoadState State,
    PrivateTagStoreSnapshot? Snapshot, string? Error);

/// <summary>
/// Host commands for the profile's own tags (contract: private-tag-store-v1). Every mutation is an
/// admitted interactive command; reads are passive. Names never enter diagnostics targets.
/// </summary>
internal sealed partial class DesktopSessionHost
{
    private readonly object _privateTagsSync = new();
    private PrivateTagStore? _privateTagStore;
    private PrivateTagLoadSnapshot _publishedPrivateTags = new(PrivateTagLoadState.Unavailable, null, "Private tags are loading.");

    /// <summary>Last committed or revalidated view; never touches the disk.</summary>
    internal PrivateTagLoadSnapshot GetPublishedPrivateTagSnapshot() => Volatile.Read(ref _publishedPrivateTags);

    /// <summary>Revalidates the complete on-disk tag index.</summary>
    internal PrivateTagLoadSnapshot GetPrivateTagSnapshot()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_privateTagsSync)
        {
            try
            {
                _privateTagStore ??= new(_profileLease.ProfileDirectory, _profileLease);
                var published = new PrivateTagLoadSnapshot(PrivateTagLoadState.Ready, _privateTagStore.ReadSnapshot(), null);
                Volatile.Write(ref _publishedPrivateTags, published);
                return published;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            {
                var unavailable = new PrivateTagLoadSnapshot(PrivateTagLoadState.Unavailable, null, error.Message);
                Volatile.Write(ref _publishedPrivateTags, unavailable);
                return unavailable;
            }
        }
    }

    /// <summary>Creates a tag or returns the existing tag with the same comparison key.</summary>
    internal Task<PrivateTagCommit> DefinePrivateTagAsync(string name, CancellationToken token = default)
        => RunInteractiveWithAdmissionAsync(nameof(DefinePrivateTagAsync), "definition",
            admission => MutatePrivateTagsAsync(store => store.DefineAdmitted(name, admission, null, token), token));

    internal Task<PrivateTagCommit> RenamePrivateTagAsync(TagId tag, string name, long expectedRevision,
        CancellationToken token = default)
        => RunInteractiveWithAdmissionAsync(nameof(RenamePrivateTagAsync), tag.Value.ToString("N"),
            admission => MutatePrivateTagsAsync(store => store.RenameAdmitted(tag, name, admission, expectedRevision, token), token));

    /// <summary>Idempotent thread membership; media of the thread are not tagged.</summary>
    internal Task<PrivateTagCommit> SetThreadTagAsync(TagId tag, ResourceId thread, bool assigned,
        CancellationToken token = default)
        => RunInteractiveWithAdmissionAsync(nameof(SetThreadTagAsync), tag.Value.ToString("N") + " " + thread,
            admission => MutatePrivateTagsAsync(store => store.SetThreadAdmitted(tag, thread, assigned, admission, null, token), token));

    /// <summary>Idempotent membership of one exact media version.</summary>
    internal Task<PrivateTagCommit> SetMediaTagAsync(TagId tag, PrivateMediaTagTarget target, bool assigned,
        CancellationToken token = default)
        => RunInteractiveWithAdmissionAsync(nameof(SetMediaTagAsync),
            tag.Value.ToString("N") + " " + target?.Reference.RevisionKey + " " + target?.Role,
            admission => MutatePrivateTagsAsync(store => store.SetMediaAdmitted(tag, target, assigned, admission, null, token), token));

    /// <summary>
    /// One tag on several exact media versions as a single admitted write: all or none. With an expected
    /// revision the write is refused (stale) if the tags changed since the caller looked.
    /// </summary>
    internal Task<PrivateTagCommit> SetMediaTagsAsync(TagId tag, IReadOnlyList<PrivateMediaTagTarget> targets, bool assigned,
        long? expectedRevision = null, CancellationToken token = default)
        => RunInteractiveWithAdmissionAsync(nameof(SetMediaTagsAsync), tag.Value.ToString("N") + " " + targets?.Count + " media",
            admission => MutatePrivateTagsAsync(store => store.SetMediaBatchAdmitted(tag, targets!, assigned, admission, expectedRevision, token), token));

    /// <summary>Deletes a tag and all of its assignments, bound to the revision the caller confirmed against.</summary>
    internal Task<PrivateTagCommit> DeletePrivateTagAsync(TagId tag, long expectedRevision,
        CancellationToken token = default)
        => RunInteractiveWithAdmissionAsync(nameof(DeletePrivateTagAsync), tag.Value.ToString("N"),
            admission => MutatePrivateTagsAsync(store => store.DeleteAdmitted(tag, admission, expectedRevision, token), token));

    // Called by the deletion owner while _commands is held, only for actually deleted IDs.
    // The catalog deletions are already committed, so this is not cancelable.
    private void CleanupPrivateTags(IReadOnlyCollection<ResourceId> deleted, ProfileAdmissionToken admission)
    {
        lock (_privateTagsSync)
        {
            _privateTagStore ??= new(_profileLease.ProfileDirectory, _profileLease);
            PrivateTagCommit commit = _privateTagStore.RemoveThreadsAdmitted(deleted, admission);
            Volatile.Write(ref _publishedPrivateTags, new(PrivateTagLoadState.Ready, commit.Snapshot, null));
        }
    }

    /// <summary>
    /// The download queue wrote a media-tag carry-over straight to the tag file (read-modify-write under the
    /// lease, so it cannot overwrite or be overwritten by a host write). Refresh the published view from disk so
    /// the UI shows the carried tags without a restart. A host write from an older view still fails safe: the
    /// revision it expected is stale.
    /// </summary>
    private void OnMediaTagsCarriedOver() => _ = RefreshPublishedPrivateTagsAsync();

    internal Task RefreshPublishedPrivateTagsAsync() => Task.Run(() =>
    {
        try
        {
            if (_disposed) return;
            if (GetPrivateTagSnapshot().State == PrivateTagLoadState.Ready) SignalChanged();
        }
        catch (ObjectDisposedException) { /* Stopped meanwhile. */ }
        catch (Exception error) { _logger.Debug(error, "Could not refresh the private tag view."); }
    });

    internal ProfileLease ProfileLeaseForTests => _profileLease;

    private Task? _mediaTagReconcile;

    /// <summary>The background reconcile started after Ready; tests await it, shutdown drains it.</summary>
    internal Task MediaTagReconcileForTests => _mediaTagReconcile ?? Task.CompletedTask;

    /// <summary>
    /// Starts the one-time background pass that completes media-tag hand-overs a stop between a media commit and
    /// its tag write may have lost. It never delays start (it begins after Ready) and only reads the manifests of
    /// threads that carry media tags.
    /// </summary>
    private void StartMediaTagReconcile()
        => _mediaTagReconcile ??= Task.Run(() => ReconcileMediaTagKeysAsync(_startupCancellation.Token));

    internal async Task ReconcileMediaTagKeysAsync(CancellationToken token)
    {
        try
        {
            using var measurement = MeowChan.Diagnostics.PerformanceDiagnostics.Measure("host.tags.reconcile", epoch: _epoch);
            PrivateTagStoreSnapshot? tags = GetPrivateTagSnapshot().Snapshot;
            if (tags == null || tags.MediaAssignments.Count == 0) return;
            ArchiveReader? reader = null;
            IReadOnlyList<MediaTagKeyHandover> pending;
            try
            {
                try { reader = await OpenArchiveReaderAsync(token).ConfigureAwait(false); }
                catch (FileNotFoundException) { /* A profile may keep tags without an archive catalog. */ }
                pending = await MediaTagKeyCarryOver.FindPendingAsync(tags, async (thread, ct) =>
                {
                    List<string> directories = new();
                    if (reader != null) directories.AddRange(await reader.GetMediaDirectoriesAsync(thread, ct).ConfigureAwait(false));
                    try { directories.Add(StorageLayout.GetThreadDirectory(Config.DownloadPath, thread.Source, thread.Board, thread.Thread!)); }
                    catch (ArgumentException) { /* Older provider identities may not use storage-safe segments. */ }
                    return directories;
                }, token).ConfigureAwait(false);
            }
            finally { if (reader != null) await reader.DisposeAsync().ConfigureAwait(false); }
            if (pending.Count == 0) return;
            await RunInteractiveWithAdmissionAsync("reconcile-media-tags", "profile",
                admission => MutatePrivateTagsAsync(store => store.CarryMediaAssignmentsAdmitted(pending, admission, token), token),
                captureAfter: false).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException
            or DesktopSessionBusyException or InvalidOperationException) { /* Stopping or busy: the next start reconciles. */ }
        catch (Exception error) { _logger.Warning(error, "Could not reconcile private media tags."); }
    }

    private async Task<PrivateTagCommit> MutatePrivateTagsAsync(Func<PrivateTagStore, PrivateTagCommit> change,
        CancellationToken token)
    {
        await _commands.WaitAsync(token).ConfigureAwait(false);
        try
        {
            EnsureReady();
            PrivateTagCommit commit;
            lock (_privateTagsSync)
            {
                _privateTagStore ??= new(_profileLease.ProfileDirectory, _profileLease);
                // A rejected or failed change throws here and leaves the published view untouched.
                commit = change(_privateTagStore);
                Volatile.Write(ref _publishedPrivateTags, new(PrivateTagLoadState.Ready, commit.Snapshot, null));
            }
            if (commit.Changed) SignalChanged();
            return commit;
        }
        finally { _commands.Release(); }
    }
}
