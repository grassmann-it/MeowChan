using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MeowChan.Storage;

namespace MeowChan.Transfer;

// Read-only inspection contract for the next transfer/recovery lane. These DTOs deliberately
// contain identities and bounded scalar observations only. They do not carry paths, payloads,
// private values, exception text, leases, or commands that could authorize Apply/Resume.
internal static class NativeMediaTransferInspectionContract
{
    internal const int MaximumResourceCountV1 = 1;
    internal const int MaximumResourceCountV2 = 256;
    internal const int MaximumPageItems = 32;
    internal const int MaximumInspectionEntries = 32;
    internal const int MaximumPhaseSequence = 1_000_000;
    internal const int MaximumIdentityTextLength = 256;
    internal const int MaximumCatalogInstanceLength = 128;
    internal const int Sha256Length = 64;

    internal static ResourceId ValidateResource(ResourceId resource, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(resource.Source) || string.IsNullOrWhiteSpace(resource.Board)
            || string.IsNullOrWhiteSpace(resource.Thread)
            || resource.Source.Length > MaximumIdentityTextLength
            || resource.Board.Length > MaximumIdentityTextLength
            || resource.Thread.Length > MaximumIdentityTextLength
            || HasControl(resource.Source) || HasControl(resource.Board) || HasControl(resource.Thread))
            throw new ArgumentException("The resource identity is invalid or exceeds the inspection bound.", parameterName);
        return resource;
    }

    internal static string NormalizeSha256(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != Sha256Length
            || value.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("The identity hash must be a bounded SHA-256 value.", parameterName);
        return value.ToLowerInvariant();
    }

    internal static string ValidateCatalogInstance(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumCatalogInstanceLength || HasControl(value))
            throw new ArgumentException("The catalog instance identity is invalid or exceeds the inspection bound.", nameof(value));
        return value;
    }

    internal static string ResourceKey(ResourceId resource)
        => Frame("resource", resource.Source, resource.Board, resource.Thread!);

    internal static string Frame(params string[] parts)
    {
        StringBuilder framed = new();
        foreach (string part in parts)
        {
            if (part is null) throw new ArgumentNullException(nameof(parts));
            framed.Append(part.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(part);
        }
        return framed.ToString();
    }

    internal static string ResourceSetDigestV1(ResourceId resource)
        => HashFrame("resource-set-v1", ResourceKey(resource));

    internal static string ResourceSetDigestV2(IEnumerable<ResourceId> resources)
        => HashFrame(new[] { "resource-set-v2" }.Concat(resources.Select(ResourceKey)).ToArray());

    internal static string OperationKey(int version, Guid operationId, string requestSha256,
        string packageFingerprint, string catalogInstance, long catalogGeneration, string resourceSetDigest)
        => HashFrame($"operation-v{version}", operationId.ToString("D"), requestSha256, packageFingerprint,
            catalogInstance, catalogGeneration.ToString(CultureInfo.InvariantCulture), resourceSetDigest);

    internal static string ReceiptKey(int version, string operationKey)
        => HashFrame($"receipt-v{version}", operationKey);

    internal static string PhaseKey(int version, Guid operationId, NativeMediaTransferInspectionPhase phase, int sequence)
        => HashFrame($"phase-v{version}", operationId.ToString("D"), phase.ToString(),
            sequence.ToString(CultureInfo.InvariantCulture));

    internal static string RecoveryKey(int version, Guid operationId, NativeMediaTransferRecoveryDisposition disposition,
        NativeMediaTransferRecoveryReason reason)
        => HashFrame($"recovery-v{version}", operationId.ToString("D"), disposition.ToString(), reason.ToString());

    internal static string HashFrame(params string[] parts)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Frame(parts)))).ToLowerInvariant();

    internal static void ValidatePage<T>(ImmutableArray<T> items, Guid? nextOperationId)
    {
        if (items.IsDefault || items.Length > MaximumPageItems)
            throw new ArgumentException("The inspection page is missing or exceeds its bound.", nameof(items));
        if (nextOperationId is Guid cursor && cursor == Guid.Empty)
            throw new ArgumentException("The inspection continuation identity is invalid.", nameof(nextOperationId));
    }

    private static bool HasControl(string value) => value.Any(char.IsControl);
}

// v1 is intentionally the historical one-resource shape. It cannot be constructed from a v2
// resource set, and it remains content-free even when a caller has a full package available.
internal sealed record NativeMediaTransferResourceIdentityV1
{
    internal NativeMediaTransferResourceIdentityV1(ResourceId resource)
    {
        Resource = NativeMediaTransferInspectionContract.ValidateResource(resource, nameof(resource));
    }

    internal ResourceId Resource { get; }
    internal string StableKey => NativeMediaTransferInspectionContract.ResourceKey(Resource);
}

internal sealed record NativeMediaTransferResourceSetIdentityV1
{
    internal NativeMediaTransferResourceSetIdentityV1(NativeMediaTransferResourceIdentityV1 resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        Resource = resource;
        Digest = NativeMediaTransferInspectionContract.ResourceSetDigestV1(resource.Resource);
    }

    internal NativeMediaTransferResourceIdentityV1 Resource { get; }
    internal string Digest { get; }
    internal int Count => NativeMediaTransferInspectionContract.MaximumResourceCountV1;
}

internal sealed record NativeMediaTransferOperationIdentityV1
{
    internal NativeMediaTransferOperationIdentityV1(Guid operationId, string requestSha256,
        string packageFingerprint, string catalogInstance, long catalogGeneration,
        NativeMediaTransferResourceSetIdentityV1 resourceSet)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("The operation identity is empty.", nameof(operationId));
        if (catalogGeneration < 0) throw new ArgumentOutOfRangeException(nameof(catalogGeneration));
        ArgumentNullException.ThrowIfNull(resourceSet);
        OperationId = operationId;
        RequestSha256 = NativeMediaTransferInspectionContract.NormalizeSha256(requestSha256, nameof(requestSha256));
        PackageFingerprint = NativeMediaTransferInspectionContract.NormalizeSha256(packageFingerprint, nameof(packageFingerprint));
        CatalogInstance = NativeMediaTransferInspectionContract.ValidateCatalogInstance(catalogInstance);
        CatalogGeneration = catalogGeneration;
        ResourceSet = resourceSet;
        StableKey = NativeMediaTransferInspectionContract.OperationKey(1, operationId, RequestSha256,
            PackageFingerprint, CatalogInstance, catalogGeneration, resourceSet.Digest);
    }

    internal Guid OperationId { get; }
    internal string RequestSha256 { get; }
    internal string PackageFingerprint { get; }
    internal string CatalogInstance { get; }
    internal long CatalogGeneration { get; }
    internal NativeMediaTransferResourceSetIdentityV1 ResourceSet { get; }
    internal string StableKey { get; }
    internal int ContractVersion => 1;
}

// v2 is a separate DTO family. It models an ordered resource set, but this file only describes
// inspection identity; the presence of a v2 DTO never grants multi-thread mutation authority.
internal sealed record NativeMediaTransferResourceIdentityV2
{
    internal NativeMediaTransferResourceIdentityV2(ResourceId resource)
    {
        Resource = NativeMediaTransferInspectionContract.ValidateResource(resource, nameof(resource));
    }

    internal ResourceId Resource { get; }
    internal string StableKey => NativeMediaTransferInspectionContract.ResourceKey(Resource);
}

internal sealed record NativeMediaTransferResourceSetIdentityV2
{
    internal NativeMediaTransferResourceSetIdentityV2(ImmutableArray<NativeMediaTransferResourceIdentityV2> resources)
    {
        if (resources.IsDefault || resources.Length is < 1 or > NativeMediaTransferInspectionContract.MaximumResourceCountV2
            || resources.Any(resource => resource is null))
            throw new ArgumentException("The v2 resource set is empty, null-containing, or exceeds its inspection bound.", nameof(resources));
        for (int index = 1; index < resources.Length; index++)
        {
            if (string.CompareOrdinal(resources[index - 1].StableKey, resources[index].StableKey) >= 0)
                throw new ArgumentException("The v2 resource set must be strictly ordered and duplicate-free.", nameof(resources));
        }
        Resources = resources;
        Digest = NativeMediaTransferInspectionContract.ResourceSetDigestV2(resources.Select(item => item.Resource));
    }

    internal ImmutableArray<NativeMediaTransferResourceIdentityV2> Resources { get; }
    internal string Digest { get; }
    internal int Count => Resources.Length;

    internal static NativeMediaTransferResourceSetIdentityV2 Create(IEnumerable<ResourceId> resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ImmutableArray<NativeMediaTransferResourceIdentityV2>.Builder result = ImmutableArray.CreateBuilder<NativeMediaTransferResourceIdentityV2>();
        foreach (ResourceId resource in resources)
        {
            if (result.Count == NativeMediaTransferInspectionContract.MaximumResourceCountV2)
                throw new ArgumentException("The v2 resource set exceeds its inspection bound.", nameof(resources));
            result.Add(new(resource));
        }
        return new(result.ToImmutable());
    }
}

internal sealed record NativeMediaTransferOperationIdentityV2
{
    internal NativeMediaTransferOperationIdentityV2(Guid operationId, string requestSha256,
        string packageFingerprint, string catalogInstance, long catalogGeneration,
        NativeMediaTransferResourceSetIdentityV2 resourceSet)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("The operation identity is empty.", nameof(operationId));
        if (catalogGeneration < 0) throw new ArgumentOutOfRangeException(nameof(catalogGeneration));
        ArgumentNullException.ThrowIfNull(resourceSet);
        OperationId = operationId;
        RequestSha256 = NativeMediaTransferInspectionContract.NormalizeSha256(requestSha256, nameof(requestSha256));
        PackageFingerprint = NativeMediaTransferInspectionContract.NormalizeSha256(packageFingerprint, nameof(packageFingerprint));
        CatalogInstance = NativeMediaTransferInspectionContract.ValidateCatalogInstance(catalogInstance);
        CatalogGeneration = catalogGeneration;
        ResourceSet = resourceSet;
        StableKey = NativeMediaTransferInspectionContract.OperationKey(2, operationId, RequestSha256,
            PackageFingerprint, CatalogInstance, catalogGeneration, resourceSet.Digest);
    }

    internal Guid OperationId { get; }
    internal string RequestSha256 { get; }
    internal string PackageFingerprint { get; }
    internal string CatalogInstance { get; }
    internal long CatalogGeneration { get; }
    internal NativeMediaTransferResourceSetIdentityV2 ResourceSet { get; }
    internal string StableKey { get; }
    internal int ContractVersion => 2;
}
