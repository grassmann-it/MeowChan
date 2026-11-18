using System;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;

namespace MeowChan.Booru;

/// <summary>
/// Hands watcher findings to the existing B4 path (<see cref="BooruOriginalQueue"/>), so a watcher never
/// owns a second queue. B4 provides the dedupe: one runtime owner and one queue job per
/// (danbooru, ProviderPostId, original), idempotent findings, catalog link only after verified commit.
/// <para>
/// One watcher-specific rule is added here: a post whose original the user explicitly cancelled is not
/// re-queued because another (or the same, replayed) watcher finds it again. The claim and the watcher
/// finding are still stored, so provenance stays complete; a retry stays an explicit user action.
/// </para>
/// </summary>
internal sealed class DanbooruWatcherQueueAdmission : IDanbooruWatcherAdmissionPort
{
    private readonly BooruOriginalQueue _originals;
    private readonly BooruCatalogStore _catalog;
    private readonly TimeProvider _time;

    internal DanbooruWatcherQueueAdmission(BooruOriginalQueue originals, BooruCatalogStore catalog,
        TimeProvider? timeProvider = null)
    {
        _originals = originals ?? throw new ArgumentNullException(nameof(originals));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<DanbooruWatcherAdmissionResult> AdmitAsync(BooruPostClaim post, string watcherId, string query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(post);
        cancellationToken.ThrowIfCancellationRequested();
        BooruPostRecord? existing = _catalog.TryGetPost(post.SourceId, post.PostId);
        if (existing is { OriginalIntent: BooruOriginalIntent.Cancelled })
        {
            // Claim and watcher finding in one write; the stored cancellation is left as it is. This is the
            // admission's direct catalog boundary (the normal path is wrapped by BooruOriginalQueue).
            try
            {
                _catalog.RecordObservation(post, _time.GetUtcNow(), BooruFindingKind.Watcher, watcherId, query,
                    cancellationToken: cancellationToken);
            }
            catch (Exception exception) when (BooruCatalogPersistenceException.IsStoreWriteFailure(exception))
            {
                throw new BooruCatalogPersistenceException(exception);
            }
            return DanbooruWatcherAdmissionResult.KeptCancelled;
        }

        BooruOriginalAdmission admission = await _originals.AdmitAsync(post, BooruFindingKind.Watcher, watcherId, query,
            cancellationToken).ConfigureAwait(false);
        return admission.Status switch
        {
            BooruOriginalAdmissionStatus.Queued => DanbooruWatcherAdmissionResult.Queued,
            BooruOriginalAdmissionStatus.AlreadyQueued => DanbooruWatcherAdmissionResult.AlreadyQueued,
            BooruOriginalAdmissionStatus.AlreadyLinked => DanbooruWatcherAdmissionResult.AlreadyLinked,
            BooruOriginalAdmissionStatus.Unavailable => DanbooruWatcherAdmissionResult.Unavailable,
            _ => DanbooruWatcherAdmissionResult.NotAdmitted
        };
    }
}
