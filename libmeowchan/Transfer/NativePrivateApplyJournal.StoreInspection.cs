using System;
using System.Collections.Immutable;
using MeowChan.Favorites;
using MeowChan.Persistence;

namespace MeowChan.Transfer;

internal sealed partial class NativePrivateApplyJournal
{
    // Receipt first, constructor-free and read-only, including after a process
    // restart. The journal request, not caller-supplied fences, selects the stores.
    // This does not resume Apply, append observations, retire markers or assert
    // profile/catalog/lifecycle/consent authority. Each result is a disk sample.
    internal NativePrivateApplyStoreInspection InspectStores(Guid operationId)
        => _lease.RunExclusive(() =>
        {
            NativePrivateApplyJournalInspection journal = InspectCore(operationId);
            if (journal.HasTerminalReceipt)
                return new NativePrivateApplyStoreInspection(journal, []);
            LoadedOperation loaded = CheckSnapshot(journal.Snapshot);
            var stores = ImmutableArray.CreateBuilder<NativePrivateApplyStoreInspectionRow>();
            foreach (var fence in loaded.Request.StoreFences)
            {
                PrivateStoreBatchInspection result = fence.Kind switch
                {
                    NativePrivateApplyStoreKind.ThreadFavorites => ThreadFavoriteStore.InspectRecoveryFence(
                        _lease, fence, operationId, loaded.Request.ContentPlanSha256),
                    NativePrivateApplyStoreKind.MediaFavorites => FavoriteStore.InspectRecoveryFence(
                        _lease, fence, operationId, loaded.Request.ContentPlanSha256),
                    NativePrivateApplyStoreKind.PostBookmarksAndNotes => PostBookmarkStore.InspectRecoveryFence(
                        _lease, fence, operationId, loaded.Request.ContentPlanSha256),
                    _ => throw Invalid()
                };
                stores.Add(new(fence.Kind, result));
            }
            // The same lease serializes cooperating writers during this short
            // synchronous sample. Revalidate the journal identity/head too; an
            // outside actor changing the journal cannot leave a current result.
            _ = CheckSnapshot(journal.Snapshot);
            return new NativePrivateApplyStoreInspection(journal, stores.ToImmutable());
        });
}

// These facts deliberately do not expose the request payload, target values or
// image hashes. A valid historical receipt has no current-store rows at all.
internal sealed record NativePrivateApplyStoreInspectionRow(
    NativePrivateApplyStoreKind StoreKind, PrivateStoreBatchInspection Inspection);

internal sealed class NativePrivateApplyStoreInspection(
    NativePrivateApplyJournalInspection journal,
    ImmutableArray<NativePrivateApplyStoreInspectionRow> stores)
{
    internal NativePrivateApplyJournalInspection Journal { get; } = journal;
    internal ImmutableArray<NativePrivateApplyStoreInspectionRow> Stores { get; } = stores;
}
