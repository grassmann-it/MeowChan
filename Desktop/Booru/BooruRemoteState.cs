using System;

namespace MeowChan.Desktop.Booru;

/// <summary>
/// The one state a remote hit shows on its tile, its card and in the viewer. It is derived, never stored: the same
/// row snapshot gives the same state everywhere, so a tile and the viewer cannot disagree.
/// </summary>
internal enum BooruRemoteItemState
{
    /// <summary>Not local; the thumbnail (loading, ready, unavailable or none) is all there is to see. Also the state of a post whose
    /// sample is the original (<see cref="BooruSampleAvailability.OriginalOnly"/>): nothing larger is shown until it is downloaded.</summary>
    RemotePreview,
    /// <summary>Not local; the viewer holds the provider sample bitmap.</summary>
    SampleLoaded,
    /// <summary>The provider offers no downloadable original (none, withheld, deleted or banned) and nothing is local.</summary>
    Unavailable,
    /// <summary>Handed to the download queue, waiting or transferring (the two are not told apart: nothing observes a running job).</summary>
    Queued,
    /// <summary>The original is local, linked to the library.</summary>
    Downloaded,
    /// <summary>Linked in the catalog, but the media page reports the file as missing.</summary>
    LocalMissing,
    /// <summary>The last hand-over or download failed or the provider could not be reached; an explicit retry is offered.</summary>
    Failed
}

/// <summary>The pure derivation of <see cref="BooruRemoteItemState"/> from a result row and what the viewer knows.</summary>
internal static class BooruRemoteItem
{
    /// <summary>
    /// <paramref name="row"/> carries the post's access, the row's load state (which already folds in the queue's local
    /// state) and the preview. <paramref name="sampleLoaded"/> is true while the viewer holds the sample bitmap of this post.
    /// <paramref name="localMissing"/> is the media page's report that a linked file is gone; it only matters for a downloaded post.
    /// Order of precedence: local file, queue, failure, withdrawn original, sample, preview.
    /// </summary>
    internal static BooruRemoteItemState StateOf(BooruResultRowView row, bool sampleLoaded = false, bool localMissing = false)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Load switch
        {
            BooruRowLoad.AlreadyLoaded => localMissing ? BooruRemoteItemState.LocalMissing : BooruRemoteItemState.Downloaded,
            BooruRowLoad.Handing or BooruRowLoad.Queued or BooruRowLoad.AlreadyQueued => BooruRemoteItemState.Queued,
            BooruRowLoad.Failed or BooruRowLoad.Offline => BooruRemoteItemState.Failed,
            BooruRowLoad.NoOriginal or BooruRowLoad.Restricted => BooruRemoteItemState.Unavailable,
            _ when row.Post.OriginalAccess != BooruOriginalAccess.Available => BooruRemoteItemState.Unavailable,
            _ => sampleLoaded ? BooruRemoteItemState.SampleLoaded : BooruRemoteItemState.RemotePreview
        };
    }
}
