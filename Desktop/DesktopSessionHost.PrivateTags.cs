using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Persistence;
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
