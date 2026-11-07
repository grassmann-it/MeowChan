using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MeowChan.Transfer;

internal sealed record NativeTransferPackage(NativeTransferEnvelope Envelope, NativeTransferCatalog Catalog)
{
    // Inspect the admitted wire schema, not serializer downlevel selection. Even empty v4
    // input must be rejected by original-only materializers until their explicit handoff.
    internal bool IsChanV4 => Envelope.ContentSchema == "chan-v4";
    internal NativePrivateMetadataProjection? PrivateMetadata { get; init; }
}
internal sealed record TransferPackageInspection(NativeTransferPackage? Package,
    TransferEnvelopeError EnvelopeError, TransferCatalogError CatalogError,
    NativePrivateMetadataCodecError PrivateMetadataError = NativePrivateMetadataCodecError.None)
{
    internal bool IsPackageValid => Package != null && EnvelopeError == TransferEnvelopeError.None && CatalogError == TransferCatalogError.None
        && PrivateMetadataError == NativePrivateMetadataCodecError.None;
}
/// <summary>Passive bounded inspection only; no extraction, store admission or stable handle lease.</summary>
internal static class NativeTransferPackageValidator
{
    internal static async Task<TransferPackageInspection> InspectAsync(Stream source, CancellationToken cancellationToken = default,
        TransferEnvelopeLimits? limits = null)
    {
        NativeTransferDerivedPayloadValidator derivedPayloads = new();
        TransferEnvelopeInspection framing = await NativeTransferEnvelopeValidator.InspectAsync(source, cancellationToken, limits,
            derivedPayloads).ConfigureAwait(false);
        if (!framing.IsEnvelopeValid)
        {
            // A failed position restore/read still wins; only the explicit preflight stop
            // is translated to its catalog error. Cancellation remains an exception.
            if (framing.Error == TransferEnvelopeError.InvalidReference && derivedPayloads.CatalogPreflightError != TransferCatalogError.None)
                return new(null, TransferEnvelopeError.None, derivedPayloads.CatalogPreflightError);
            return new(null, framing.Error, TransferCatalogError.None);
        }
        NativeTransferEnvelope envelope = framing.Envelope!;
        TransferCatalogInspection content = NativeTransferCatalogValidator.Inspect(envelope.CatalogBytes.AsMemory(), envelope, cancellationToken);
        if (!content.IsCatalogValid) return new(null, TransferEnvelopeError.None, content.Error);
        if (envelope.ContentSchema == "chan-v4" && !derivedPayloads.Validate(content.Catalog!, cancellationToken))
            return new(null, TransferEnvelopeError.None, TransferCatalogError.InvalidDerivedPayload);
        NativePrivateMetadataProjection? projection = null;
        if (envelope.PrivateMetadata != null)
        {
            NativePrivateMetadataInspection privateContent = NativePrivateMetadataCodec.Inspect(
                envelope.PrivateMetadataBytes.AsMemory(), cancellationToken);
            if (!privateContent.IsValid)
                return new(null, TransferEnvelopeError.None, TransferCatalogError.None, privateContent.Error);
            projection = privateContent.Projection!;
            if (!projection.Selection.ThreadFavorites && !projection.Selection.MediaFavorites
                && !projection.Selection.PostBookmarksAndNotes)
                return new(null, TransferEnvelopeError.None, TransferCatalogError.None, NativePrivateMetadataCodecError.InvalidPresence);
            try { NativePrivateMetadataCatalogBinding.Validate(content.Catalog!, projection, cancellationToken); }
            catch (InvalidDataException)
            { return new(null, TransferEnvelopeError.None, TransferCatalogError.None, NativePrivateMetadataCodecError.OutOfScope); }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(new(envelope, content.Catalog!) { PrivateMetadata = projection },
            TransferEnvelopeError.None, TransferCatalogError.None);
    }
}
