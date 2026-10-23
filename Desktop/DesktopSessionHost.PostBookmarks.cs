using System;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Persistence;

namespace MeowChan.Desktop;

internal sealed partial class DesktopSessionHost
{
    // Lazy initialization confines corrupt/unsupported optional data to this feature.
    private PostBookmarkStore? _postBookmarks;
    private PostBookmarkStore PostBookmarks => _postBookmarks ??= new(_profileLease.ProfileDirectory, _profileLease);

    internal Task<PostBookmarkSnapshot> ReadPostBookmarksAsync(CancellationToken token = default) => Task.Run(async () =>
    {
        await _commands.WaitAsync(token).ConfigureAwait(false);
        try { EnsureReady(); return PostBookmarks.Read(); }
        finally { _commands.Release(); }
    }, token);

    internal Task<PostBookmarkSnapshot> SavePostBookmarkAsync(PostBookmarkKey key, string note,
        long expectedRevision, bool expectedExists, CancellationToken token = default)
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
                PostBookmarkSnapshot committed = PostBookmarks.Upsert(key, frozen, expectedRevision, expectedExists, token);
                SignalChanged();
                return committed;
            }
            finally { _commands.Release(); }
        }, token);
    }

    internal Task<PostBookmarkSnapshot> RemovePostBookmarkAsync(PostBookmarkKey key,
        long expectedRevision, CancellationToken token = default) => Task.Run(async () =>
    {
        await _commands.WaitAsync(token).ConfigureAwait(false);
        try
        {
            EnsureReady();
            PostBookmarkSnapshot committed = PostBookmarks.Remove(key, expectedRevision, token);
            SignalChanged();
            return committed;
        }
        finally { _commands.Release(); }
    }, token);
}
