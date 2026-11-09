using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace MeowChan.Transfer;

/// <summary>
/// The privacy scope requested by a receiving host. PublicOnly is the safe
/// default; ExplicitPrivateMetadata is never inferred from package presence.
/// </summary>
internal enum NativePrivateConsentScope
{
    PublicOnly = 0,
    ExplicitPrivateMetadata = 1,
}

/// <summary>
/// Content-free provenance for a validated transfer package. It contains only
/// stable package descriptors and hashes, never private notes or store values.
/// </summary>
internal sealed record NativePrivateConsentProvenance(
    string PackageId,
    int EnvelopeVersion,
    string ContentSchema,
    string ManifestSha256,
    long CatalogLength,
    string CatalogSha256,
    long PrivateMetadataLength,
    string? PrivateMetadataSha256)
{
    internal static NativePrivateConsentProvenance FromPackage(NativeTransferPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        NativeTransferEnvelope envelope = package.Envelope
            ?? throw Invalid("The transfer package has no envelope.");
        TransferBlobDescriptor catalog = envelope.Catalog
            ?? throw Invalid("The transfer package has no catalog descriptor.");
        TransferBlobDescriptor? privateMetadata = envelope.PrivateMetadata;
        return new(envelope.PackageId, envelope.EnvelopeVersion, envelope.ContentSchema,
            envelope.Fingerprint, catalog.Length, catalog.Sha256,
            privateMetadata?.Length ?? 0, privateMetadata?.Sha256);
    }

    private static InvalidDataException Invalid(string message) => new(message);
}

/// <summary>
/// A bounded, inert decision for a future host. Creating a decision validates
/// the already inspected package and exact provenance, but performs no store
/// read/write, consent persistence, extraction, or import/merge operation.
/// </summary>
internal sealed class NativePrivateConsentDecision
{
    private const int MaximumPackageIdBytes = 128;
    private const int MaximumSchemaBytes = 64;
    private const int MaximumHashBytes = 64;
    private const long MaximumPackageBytes = 2L * 1024 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private NativePrivateConsentDecision(NativePrivateConsentScope scope,
        NativePrivateMetadataSelection selection, NativePrivateConsentProvenance provenance,
        bool packageHasPrivateMetadata, string decisionIdentity, string requestIdentity)
    {
        Scope = scope;
        Selection = selection;
        Provenance = provenance;
        PackageHasPrivateMetadata = packageHasPrivateMetadata;
        DecisionIdentity = decisionIdentity;
        RequestIdentity = requestIdentity;
    }

    internal NativePrivateConsentScope Scope { get; }
    internal NativePrivateMetadataSelection Selection { get; }
    internal NativePrivateConsentProvenance Provenance { get; }
    internal bool PackageHasPrivateMetadata { get; }
    internal string DecisionIdentity { get; }
    internal string RequestIdentity { get; }

    internal bool IsPublicOnly => Scope == NativePrivateConsentScope.PublicOnly;
    internal bool HasExplicitPrivateConsent => Scope == NativePrivateConsentScope.ExplicitPrivateMetadata;

    internal static NativePrivateConsentDecision Create(NativeTransferPackage package,
        NativePrivateConsentProvenance provenance,
        NativePrivateConsentScope scope = NativePrivateConsentScope.PublicOnly,
        NativePrivateMetadataSelection selection = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(provenance);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            ValidatePackage(package, cancellationToken);
            ValidateProvenance(package, provenance);
            NativePrivateMetadataSelection available = package.PrivateMetadata?.Selection ?? default;
            ValidateSelection(scope, selection, package.PrivateMetadata, available);
            cancellationToken.ThrowIfCancellationRequested();

            bool hasPrivate = package.PrivateMetadata is not null;
            string decisionIdentity = Digest("meowchan-private-consent-decision-v1", provenance,
                scope, selection, includeRequestMarker: false, cancellationToken);
            string requestIdentity = Digest("meowchan-private-apply-request-v1", provenance,
                scope, selection, includeRequestMarker: true, cancellationToken);
            return new(scope, selection, provenance, hasPrivate, decisionIdentity, requestIdentity);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException
            or InvalidDataException or OverflowException or DecoderFallbackException)
        {
            throw Invalid();
        }
    }

    private static void ValidatePackage(NativeTransferPackage package, CancellationToken token)
    {
        NativeTransferEnvelope envelope = package.Envelope ?? throw Invalid();
        NativeTransferCatalog catalog = package.Catalog ?? throw Invalid();
        token.ThrowIfCancellationRequested();
        if (!Guid.TryParseExact(envelope.PackageId, "D", out Guid packageId) || packageId == Guid.Empty
            || envelope.ContentSchema is not ("chan-v1" or "chan-v2" or "chan-v3" or "chan-v4")
            || envelope.EnvelopeVersion is < 1 or > 2
            || !LowerHash(envelope.Fingerprint)
            || envelope.Catalog is null || envelope.Catalog.Length <= 0
            || envelope.Catalog.Length > NativeTransferCatalogSerializerCap
            || !LowerHash(envelope.Catalog.Sha256)
            || envelope.CatalogBytes.IsDefault || envelope.CatalogBytes.Length != envelope.Catalog.Length
            || !HashEquals(envelope.CatalogBytes.AsSpan(), envelope.Catalog.Sha256)) throw Invalid();

        byte[] canonicalCatalog = NativeTransferCatalogSerializer.Serialize(catalog, token);
        if (!canonicalCatalog.AsSpan().SequenceEqual(envelope.CatalogBytes.AsSpan())
            || NativeTransferCatalogSerializer.ContentSchema(catalog, token) != envelope.ContentSchema)
            throw Invalid();

        TransferBlobDescriptor? privateDescriptor = envelope.PrivateMetadata;
        NativePrivateMetadataProjection? privateProjection = package.PrivateMetadata;
        if (privateDescriptor is null)
        {
            if (envelope.EnvelopeVersion != 1 || !envelope.PrivateMetadataBytes.IsDefault
                && !envelope.PrivateMetadataBytes.IsEmpty || privateProjection is not null)
                throw Invalid();
            return;
        }

        if (envelope.EnvelopeVersion != 2 || privateProjection is null
            || privateDescriptor.Length <= 0 || privateDescriptor.Length > NativePrivateMetadataCodec.MaximumDocumentBytes
            || !LowerHash(privateDescriptor.Sha256)
            || envelope.PrivateMetadataBytes.IsDefault
            || envelope.PrivateMetadataBytes.Length != privateDescriptor.Length
            || !HashEquals(envelope.PrivateMetadataBytes.AsSpan(), privateDescriptor.Sha256)) throw Invalid();

        NativePrivateMetadataCodec.Validate(privateProjection, token);
        NativePrivateMetadataCatalogBinding.Validate(catalog, privateProjection, token);
        byte[] canonicalPrivate = NativePrivateMetadataCodec.Serialize(privateProjection, token);
        if (!canonicalPrivate.AsSpan().SequenceEqual(envelope.PrivateMetadataBytes.AsSpan())) throw Invalid();
    }

    private static void ValidateProvenance(NativeTransferPackage package, NativePrivateConsentProvenance provenance)
    {
        NativePrivateConsentProvenance expected = NativePrivateConsentProvenance.FromPackage(package);
        ValidateProvenanceShape(provenance);
        if (!string.Equals(provenance.PackageId, expected.PackageId, StringComparison.Ordinal)
            || provenance.EnvelopeVersion != expected.EnvelopeVersion
            || !string.Equals(provenance.ContentSchema, expected.ContentSchema, StringComparison.Ordinal)
            || !string.Equals(provenance.ManifestSha256, expected.ManifestSha256, StringComparison.Ordinal)
            || provenance.CatalogLength != expected.CatalogLength
            || !string.Equals(provenance.CatalogSha256, expected.CatalogSha256, StringComparison.Ordinal)
            || provenance.PrivateMetadataLength != expected.PrivateMetadataLength
            || !string.Equals(provenance.PrivateMetadataSha256, expected.PrivateMetadataSha256, StringComparison.Ordinal))
            throw Invalid();
    }

    private static void ValidateProvenanceShape(NativePrivateConsentProvenance value)
    {
        if (value is null || !Guid.TryParseExact(value.PackageId, "D", out Guid id) || id == Guid.Empty
            || value.PackageId is not { Length: > 0 and <= MaximumPackageIdBytes }
            || value.ContentSchema is not { Length: > 0 and <= MaximumSchemaBytes }
            || value.ContentSchema is not ("chan-v1" or "chan-v2" or "chan-v3" or "chan-v4")
            || value.EnvelopeVersion is < 1 or > 2
            || !LowerHash(value.ManifestSha256) || value.CatalogLength <= 0 || value.CatalogLength > NativeTransferCatalogSerializerCap
            || !LowerHash(value.CatalogSha256) || value.PrivateMetadataLength < 0
            || value.PrivateMetadataLength > NativePrivateMetadataCodec.MaximumDocumentBytes
            || value.PrivateMetadataLength == 0 && value.PrivateMetadataSha256 is not null
            || value.PrivateMetadataLength > 0 && !LowerHash(value.PrivateMetadataSha256)) throw Invalid();
    }

    private static void ValidateSelection(NativePrivateConsentScope scope,
        NativePrivateMetadataSelection selection, NativePrivateMetadataProjection? packagePrivate,
        NativePrivateMetadataSelection available)
    {
        if (!Enum.IsDefined(scope)) throw Invalid();
        bool selected = selection.ThreadFavorites || selection.MediaFavorites || selection.PostBookmarksAndNotes;
        if (scope == NativePrivateConsentScope.PublicOnly)
        {
            // Public-only is intentionally strict: private categories cannot be
            // smuggled into a default/public decision.
            if (selected) throw Invalid();
            return;
        }
        if (packagePrivate is null || !selected
            || selection.ThreadFavorites && !available.ThreadFavorites
            || selection.MediaFavorites && !available.MediaFavorites
            || selection.PostBookmarksAndNotes && !available.PostBookmarksAndNotes) throw Invalid();
    }

    private static string Digest(string domain, NativePrivateConsentProvenance provenance,
        NativePrivateConsentScope scope, NativePrivateMetadataSelection selection,
        bool includeRequestMarker, CancellationToken token)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Number(long value)
        {
            token.ThrowIfCancellationRequested();
            Span<byte> buffer = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(buffer, value);
            hash.AppendData(buffer);
        }
        void Text(string value)
        {
            byte[] bytes = StrictUtf8.GetBytes(value);
            Number(bytes.Length);
            hash.AppendData(bytes);
        }
        Text(domain);
        Text(provenance.PackageId); Number(provenance.EnvelopeVersion); Text(provenance.ContentSchema);
        Text(provenance.ManifestSha256); Number(provenance.CatalogLength); Text(provenance.CatalogSha256);
        Number(provenance.PrivateMetadataLength); Text(provenance.PrivateMetadataSha256 ?? "");
        Number((int)scope); Number(selection.ThreadFavorites ? 1 : 0);
        Number(selection.MediaFavorites ? 1 : 0); Number(selection.PostBookmarksAndNotes ? 1 : 0);
        Number(includeRequestMarker ? 1 : 0);
        token.ThrowIfCancellationRequested();
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static bool LowerHash(string? value) => value is { Length: 64 }
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool HashEquals(ReadOnlySpan<byte> bytes, string hash)
        => string.Equals(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), hash, StringComparison.Ordinal);

    // Keep this slice bounded independently of any caller-supplied package limits.
    private const int NativeTransferCatalogSerializerCap = 16 * 1024 * 1024;
    private static InvalidDataException Invalid() => new("The private consent decision is invalid.");
    private static InvalidDataException Invalid(string message) => new(message);
}
