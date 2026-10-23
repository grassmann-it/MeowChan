using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;

namespace MeowChan.Persistence;

internal sealed record PostBookmarkTarget(ArchivedThreadView View, int PostIndex)
{
    // A missing identity always stays missing; callers cannot accidentally inherit Resume's fallback.
    internal static async Task<PostBookmarkTarget?> ResolveAsync(ArchiveReader reader, PostBookmarkKey key, CancellationToken token)
    {
        key.Validate();
        ArchivedThreadView? view = await reader.ReadThreadAsync(key.Thread, token).ConfigureAwait(false);
        int index = view?.FindPostIndex(key.Post) ?? -1;
        return index < 0 ? null : new(view!, index);
    }
}
