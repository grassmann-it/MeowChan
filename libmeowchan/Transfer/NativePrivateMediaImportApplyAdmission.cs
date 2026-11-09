using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;

namespace MeowChan.Transfer;

/// <summary>
/// Bridges an owner-issued native media plan to the existing selected-only
/// private request contract. This is deliberately an admission/preparation
/// seam: the journal claim and store batches remain the owning apply executor.
/// </summary>
internal static class NativePrivateMediaImportApplyAdmission
{
    internal static NativePrivateApplyRequest CreateRequest(
        NativeMediaImportPlan plan,
        NativePrivateMetadataComparisonResult comparison,
        NativePrivateApplyRequestBinding binding,
        ImmutableArray<NativePrivateApplyStoreFence> storeFences,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();
        // Reject a missing/default decision before inspecting caller-supplied
        // comparison or store data. PublicOnly plans never cross this boundary.
        if (!plan.PrivateConsent.HasExplicitPrivateConsent
            || plan.SelectedPrivateMetadata is not { } selected)
            throw new InvalidDataException("An explicit owner-issued private consent selection is required.");
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentNullException.ThrowIfNull(binding);
        if (comparison.Selection != plan.PrivateConsent.Selection
            || !SameProjection(selected, comparison.SelectedIncoming))
            throw new InvalidDataException("An explicit owner-issued private consent selection is required.");

        // NativePrivateApplyRequest performs the bounded row/fence validation and
        // canonical identity construction. No journal/store write occurs here.
        return NativePrivateApplyRequest.Create(plan.OperationId, comparison, binding, storeFences, cancellationToken);
    }

    private static bool SameProjection(NativePrivateMetadataProjection expected,
        NativePrivateMetadataProjection actual)
    {
        ArgumentNullException.ThrowIfNull(actual);
        byte[] expectedBytes = NativePrivateMetadataCodec.Serialize(expected);
        byte[] actualBytes = NativePrivateMetadataCodec.Serialize(actual);
        return expectedBytes.AsSpan().SequenceEqual(actualBytes);
    }
}
