using System;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Persistence;

namespace MeowChan.Desktop;

internal sealed partial class DesktopSessionHost
{
    // Lazy initialization confines corrupt/unsupported optional data to this feature.
    private PostBookmarkStore? _postBookmarks;
    private PostBookmarkStore GetPostBookmarksForWrite(ProfileAdmissionToken admission) =>
        _postBookmarks ??= new(_profileLease.ProfileDirectory, _profileLease, admission);

    internal Task<PostBookmarkSnapshot> ReadPostBookmarksAsync(CancellationToken token = default)
        => RunInteractiveAsync(nameof(ReadPostBookmarksAsync), "profile", () => ReadPostBookmarksAdmittedCoreAsync(token), captureAfter: false);

    private Task<PostBookmarkSnapshot> ReadPostBookmarksAdmittedCoreAsync(CancellationToken token = default) => Task.Run(async () =>
    {
        await _commands.WaitAsync(token).ConfigureAwait(false);
        try { EnsureReady(); return PostBookmarkStore.ReadOnly(_profileLease.ProfileDirectory, _profileLease); }
        finally { _commands.Release(); }
    }, token);

    internal Task<PostBookmarkSnapshot> SavePostBookmarkAsync(PostBookmarkKey key, string note,
        long expectedRevision, bool expectedExists, CancellationToken token = default)
        => RunInteractiveWithAdmissionAsync(nameof(SavePostBookmarkAsync), key.ToString(), admission => SavePostBookmarkAdmittedCoreAsync(key, note, expectedRevision, expectedExists, admission, token));

    private Task<PostBookmarkSnapshot> SavePostBookmarkAdmittedCoreAsync(PostBookmarkKey key, string note,
        long expectedRevision, bool expectedExists, ProfileAdmissionToken admission, CancellationToken token = default)
    {
        // Immutable inputs captured before entering the owner queue. No UI file writer.
        ArgumentNullException.ThrowIfNull(key);
        string frozen = PostBookmarkStore.NormalizeNote(note);
        return Task.Run(async () =>
        {
            await _commands.WaitAsync(token).ConfigureAwait(false);
            try
            {
                EnsureReady();
                PostBookmarkSnapshot committed = GetPostBookmarksForWrite(admission).UpsertAdmitted(key, frozen, expectedRevision, expectedExists, admission, token);
                SignalChanged();
                return committed;
            }
            finally { _commands.Release(); }
        }, token);
    }

    internal Task<PostBookmarkSnapshot> RemovePostBookmarkAsync(PostBookmarkKey key,
        long expectedRevision, CancellationToken token = default)
        => RunInteractiveWithAdmissionAsync(nameof(RemovePostBookmarkAsync), key.ToString(), admission => RemovePostBookmarkAdmittedCoreAsync(key, expectedRevision, admission, token));

    private Task<PostBookmarkSnapshot> RemovePostBookmarkAdmittedCoreAsync(PostBookmarkKey key,
        long expectedRevision, ProfileAdmissionToken admission, CancellationToken token = default) => Task.Run(async () =>
    {
        await _commands.WaitAsync(token).ConfigureAwait(false);
        try
        {
            EnsureReady();
            PostBookmarkSnapshot committed = GetPostBookmarksForWrite(admission).RemoveAdmitted(key, expectedRevision, admission, token);
            SignalChanged();
            return committed;
        }
        finally { _commands.Release(); }
    }, token);
}
