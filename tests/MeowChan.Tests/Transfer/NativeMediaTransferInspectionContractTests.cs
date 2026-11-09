using System;
using System.Collections.Immutable;
using System.Linq;
using MeowChan.Storage;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

// WP0 contract tests only. These DTOs are read-only inspection evidence; no test invokes or
// exposes a mutation/resume path. The tests use synthetic identities and never personal data.
public sealed class NativeMediaTransferInspectionContractTests
{
    private static readonly Guid OperationId = Guid.Parse("638b60cb-3fc8-4a32-b72d-ea0f12345678");
    private static readonly ResourceId Resource = new("synthetic-source", "synthetic-board", "100");
    private static readonly string Hash = new('a', 64);

    [Fact]
    public void V1IdentityIsSingleResourceAndStableAcrossEquivalentConstruction()
    {
        NativeMediaTransferResourceSetIdentityV1 resources = new(new(Resource));
        NativeMediaTransferOperationIdentityV1 first = Operation(resources);
        NativeMediaTransferOperationIdentityV1 second = Operation(new(new(Resource)));

        Assert.Equal(1, first.ContractVersion);
        Assert.Equal(1, resources.Count);
        Assert.Equal(resources.Digest, second.ResourceSet.Digest);
        Assert.Equal(first.StableKey, second.StableKey);
        Assert.Equal(64, first.StableKey.Length);
    }

    [Fact]
    public void V2IdentityUsesAnOrderedResourceSetAndHasDifferentDomainIdentity()
    {
        NativeMediaTransferResourceSetIdentityV2 resources = NativeMediaTransferResourceSetIdentityV2.Create(
            [new("synthetic-source", "synthetic-board", "100"), new("synthetic-source", "synthetic-board", "101")]);
        NativeMediaTransferOperationIdentityV2 operation = new(OperationId, Hash, Hash, "catalog-instance", 7, resources);

        Assert.Equal(2, operation.ContractVersion);
        Assert.Equal(2, resources.Count);
        Assert.Equal(64, resources.Digest.Length);
        Assert.NotEqual(new NativeMediaTransferResourceSetIdentityV1(new(Resource)).Digest, resources.Digest);
        Assert.NotEqual(Operation(new(new(Resource))).StableKey, operation.StableKey);
    }

    [Fact]
    public void V2RejectsUnorderedAndDuplicateResources()
    {
        ResourceId a = new("synthetic-source", "synthetic-board", "100");
        ResourceId b = new("synthetic-source", "synthetic-board", "101");
        Assert.Throws<ArgumentException>(() => new NativeMediaTransferResourceSetIdentityV2(
            [new(b), new(a)]));
        Assert.Throws<ArgumentException>(() => new NativeMediaTransferResourceSetIdentityV2(
            [new(a), new(a)]));
        Assert.Throws<ArgumentException>(() => NativeMediaTransferResourceSetIdentityV2.Create([]));
    }

    [Fact]
    public void ResourceSetAndPageBoundsAreEnforcedBeforeUse()
    {
        ResourceId[] resources = Enumerable.Range(0, 257)
            .Select(index => new ResourceId("synthetic-source", "synthetic-board", index.ToString()))
            .ToArray();
        Assert.Throws<ArgumentException>(() => NativeMediaTransferResourceSetIdentityV2.Create(resources));

        NativeMediaTransferRecoveryInspectionV1 inspection = InspectionV1();
        NativeMediaTransferRecoveryInspectionV1[] tooMany = Enumerable.Repeat(inspection, 33).ToArray();
        Assert.Throws<ArgumentException>(() => new NativeMediaTransferInspectionPageV1(tooMany.ToImmutableArray(), null));
    }

    [Fact]
    public void IdentityHashesAreNormalizedAndMalformedIdentityIsRejected()
    {
        NativeMediaTransferOperationIdentityV1 operation = new(OperationId, Hash.ToUpperInvariant(), Hash.ToUpperInvariant(),
            "catalog-instance", 0, new(new(Resource)));
        Assert.Equal(Hash, operation.RequestSha256);
        Assert.Equal(Hash, operation.PackageFingerprint);
        Assert.Throws<ArgumentException>(() => new NativeMediaTransferOperationIdentityV1(
            OperationId, "not-a-hash", Hash, "catalog-instance", 0, new(new(Resource))));
        Assert.Throws<ArgumentException>(() => new NativeMediaTransferOperationIdentityV1(
            OperationId, Hash, Hash, "catalog\ninstance", 0, new(new(Resource))));
    }

    [Fact]
    public void PhaseReceiptAndRecoveryIdentitiesAreStableAndOperationBound()
    {
        NativeMediaTransferRecoveryInspectionV1 inspection = InspectionV1();
        NativeMediaTransferReceiptIdentityV1 receipt = Assert.IsType<NativeMediaTransferReceiptIdentityV1>(inspection.Receipt);

        Assert.Equal(OperationId, inspection.Phase.OperationId);
        Assert.Equal(OperationId, inspection.Recovery.OperationId);
        Assert.Equal(OperationId, receipt.Operation.OperationId);
        Assert.Equal(64, inspection.Phase.StableKey.Length);
        Assert.Equal(64, inspection.Recovery.StableKey.Length);
        Assert.Equal(64, receipt.StableKey.Length);
        Assert.True(inspection.HasHistoricalReceipt);
        Assert.Equal(1, inspection.ObservedResourceCount);
    }

    [Fact]
    public void RecoveryRequiredIsAnObservationAndDoesNotBecomeClearImplicitly()
    {
        NativeMediaTransferRecoveryIdentityV1 required = new(OperationId,
            NativeMediaTransferRecoveryDisposition.Required,
            NativeMediaTransferRecoveryReason.MissingReceipt);
        NativeMediaTransferRecoveryIdentityV1 clear = new(OperationId,
            NativeMediaTransferRecoveryDisposition.Clear,
            NativeMediaTransferRecoveryReason.None);

        Assert.NotEqual(required.StableKey, clear.StableKey);
        Assert.Throws<ArgumentException>(() => new NativeMediaTransferRecoveryIdentityV1(OperationId,
            NativeMediaTransferRecoveryDisposition.Clear, NativeMediaTransferRecoveryReason.ReadFailure));
    }

    [Fact]
    public void V1AndV2InspectionPagesRemainDistinctAndContinuationIsOnlyAReadCursor()
    {
        NativeMediaTransferRecoveryInspectionV1 v1 = InspectionV1();
        NativeMediaTransferInspectionPageV1 page = new([v1], OperationId);
        Assert.Single(page.Items);
        Assert.Equal(OperationId, page.NextOperationId);

        NativeMediaTransferResourceSetIdentityV2 resources = NativeMediaTransferResourceSetIdentityV2.Create([Resource]);
        NativeMediaTransferOperationIdentityV2 operation = new(OperationId, Hash, Hash, "catalog-instance", 0, resources);
        NativeMediaTransferRecoveryInspectionV2 v2 = new(operation,
            new(operation.OperationId, NativeMediaTransferInspectionPhase.Prepared, 1),
            new(operation.OperationId, NativeMediaTransferRecoveryDisposition.Clear, NativeMediaTransferRecoveryReason.None),
            null, 0);
        NativeMediaTransferInspectionPageV2 v2Page = new([v2], null);

        Assert.Equal(2, v2Page.Items[0].Operation.ContractVersion);
        Assert.Null(v2Page.NextOperationId);
        Assert.NotEqual(v1.Operation.ContractVersion, v2Page.Items[0].Operation.ContractVersion);
    }

    [Fact]
    public void InspectionSurfaceContainsNoMutationResumeOrPayloadMembers()
    {
        string[] forbiddenFragments = ["Apply", "Resume", "Mutation", "Payload", "Path", "Note", "Diagnostic"];
        Type[] dtoTypes =
        [
            typeof(NativeMediaTransferOperationIdentityV1), typeof(NativeMediaTransferOperationIdentityV2),
            typeof(NativeMediaTransferRecoveryInspectionV1), typeof(NativeMediaTransferRecoveryInspectionV2),
            typeof(NativeMediaTransferInspectionPageV1), typeof(NativeMediaTransferInspectionPageV2)
        ];

        foreach (Type type in dtoTypes)
        foreach (var member in type.GetMembers())
            Assert.DoesNotContain(forbiddenFragments, fragment => member.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    private static NativeMediaTransferOperationIdentityV1 Operation(NativeMediaTransferResourceSetIdentityV1 resources)
        => new(OperationId, Hash, Hash, "catalog-instance", 7, resources);

    private static NativeMediaTransferRecoveryInspectionV1 InspectionV1()
    {
        NativeMediaTransferOperationIdentityV1 operation = Operation(new(new(Resource)));
        return new(operation,
            new(operation.OperationId, NativeMediaTransferInspectionPhase.Prepared, 1),
            new(operation.OperationId, NativeMediaTransferRecoveryDisposition.Clear, NativeMediaTransferRecoveryReason.None),
            new(operation), 0);
    }
}
