using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MeowChan.Transfer;

internal sealed record NativeTransferPackage(NativeTransferEnvelope Envelope, NativeTransferCatalog Catalog)
{
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
        TransferEnvelopeInspection framing = await NativeTransferEnvelopeValidator.InspectAsync(source, cancellationToken, limits).ConfigureAwait(false);
        if (!framing.IsEnvelopeValid) return new(null, framing.Error, TransferCatalogError.None);
        NativeTransferEnvelope envelope = framing.Envelope!;
        TransferCatalogInspection content = NativeTransferCatalogValidator.Inspect(envelope.CatalogBytes.AsMemory(), envelope, cancellationToken);
        if (!content.IsCatalogValid) return new(null, TransferEnvelopeError.None, content.Error);
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
