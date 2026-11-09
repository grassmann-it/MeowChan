using System.Collections.Immutable;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

public sealed class NativeTransferMultiThreadAdmissionTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 9, 9, 0, 0, TimeSpan.Zero);
    private static readonly string PackageFingerprint = new('a', 64);
    private static readonly ResourceId First = new("4chan", "g", "100");
    private static readonly ResourceId Second = new("7chan", "b", "200");

    [Fact]
    public void AdmissionCanonicalizesSelectionOrderAndCarriesPerThreadFences()
    {
        NativeTransferPackage package = Package(Thread(First), Thread(Second));
        NativeTransferAggregateAdmissionResult first = NativeTransferAggregateAdmission.Admit(package, [Second, First]);
        NativeTransferAggregateAdmissionResult reversed = NativeTransferAggregateAdmission.Admit(package, [First, Second]);

        Assert.True(first.IsAdmitted, first.Error.ToString());
        Assert.True(reversed.IsAdmitted, reversed.Error.ToString());
        NativeTransferAggregateAdmissionSnapshot a = first.Admission!;
        NativeTransferAggregateAdmissionSnapshot b = reversed.Admission!;
        Assert.Equal(a.Identity, b.Identity);
        Assert.Equal(a.ReplayKey, b.ReplayKey);
        Assert.Equal(new[] { First, Second }, a.Threads.Select(entry => entry.Resource));
        Assert.All(a.Threads, entry =>
        {
            Assert.Equal(entry.Resource, entry.Fence.Resource);
            Assert.Equal(64, entry.Fence.ContentSha256.Length);
            Assert.Equal(64, entry.ReplayKey.Length);
            Assert.True(entry.PostCount > 0);
        });
    }

    [Fact]
    public void AdmissionRejectsPrivateMetadataBeforePlanning()
    {
        NativeTransferPackage package = Package(Thread(First)) with
        {
            Envelope = Package(Thread(First)).Envelope with { PrivateMetadata = new(0, new string('b', 64)) }
        };

        NativeTransferAggregateAdmissionResult result = NativeTransferAggregateAdmission.Admit(package, [First]);

        Assert.False(result.IsAdmitted);
        Assert.Equal(NativeTransferAggregateAdmissionError.PrivateMetadataUnsupported, result.Error);
        Assert.Null(result.Admission);
    }

    [Fact]
    public void AdmissionRejectsRootlessAndDuplicatePackageIdentities()
    {
        NativeTransferPackage rootless = Package(Thread(new ResourceId("4chan", "g")));
        NativeTransferAggregateAdmissionResult rootlessResult = NativeTransferAggregateAdmission.Admit(rootless, [new("4chan", "g")]);
        Assert.Equal(NativeTransferAggregateAdmissionError.RootlessThread, rootlessResult.Error);

        NativeTransferPackage duplicate = Package(Thread(First), Thread(First));
        NativeTransferAggregateAdmissionResult duplicateResult = NativeTransferAggregateAdmission.Admit(duplicate, [First]);
        Assert.Equal(NativeTransferAggregateAdmissionError.DuplicateThreadIdentity, duplicateResult.Error);
    }

    [Fact]
    public void AdmissionRejectsDuplicateOrUnknownSelectionIdentities()
    {
        NativeTransferPackage package = Package(Thread(First), Thread(Second));

        Assert.Equal(NativeTransferAggregateAdmissionError.DuplicateSelectionIdentity,
            NativeTransferAggregateAdmission.Admit(package, [First, First]).Error);
        Assert.Equal(NativeTransferAggregateAdmissionError.SelectionNotInPackage,
            NativeTransferAggregateAdmission.Admit(package, [new("4chan", "g", "999")]).Error);
        Assert.Equal(NativeTransferAggregateAdmissionError.InvalidSelectionIdentity,
            NativeTransferAggregateAdmission.Admit(package, [new("", "g", "100")]).Error);
    }

    [Fact]
    public void PlannerCarriesTargetFencesAndIsDeterministic()
    {
        NativeTransferPackage package = Package(Thread(First), Thread(Second));
        NativeTransferAggregateAdmissionSnapshot admission = NativeTransferAggregateAdmission.Admit(package, [Second, First]).Admission!;
        string targetFence = new('c', 64);

        NativeTransferAggregateMergePlanResult result = NativeTransferAggregateMergePlanner.Plan(admission,
            [new(Second, targetFence), new(First, null)]);
        NativeTransferAggregateMergePlanResult reversed = NativeTransferAggregateMergePlanner.Plan(admission,
            [new(First, null), new(Second, targetFence)]);

        Assert.True(result.IsPlanned, result.Error.ToString());
        Assert.Equal(result.Plan!.Identity, reversed.Plan!.Identity);
        Assert.Equal(result.Plan.RequestSha256, reversed.Plan.RequestSha256);
        Assert.Equal(result.Plan.ReplayKey, reversed.Plan.ReplayKey);
        Assert.True(result.Plan.Threads.SequenceEqual(reversed.Plan.Threads));
        NativeTransferAggregateMergePlan plan = result.Plan;
        Assert.Equal(new[] { First, Second }, plan.Threads.Select(thread => thread.Resource));
        Assert.Null(plan.Threads[0].ExpectedTargetFenceSha256);
        Assert.Equal(targetFence, plan.Threads[1].ExpectedTargetFenceSha256);
        Assert.All(plan.Threads, thread =>
        {
            Assert.Equal(64, thread.RequestSha256.Length);
            Assert.Equal(64, thread.ReplayKey.Length);
            Assert.Equal(thread.IncomingFence.Resource, thread.Resource);
        });
        Assert.NotEqual(admission.ReplayKey, plan.ReplayKey);
    }

    [Fact]
    public void PlannerRejectsDuplicateUnknownRootlessAndMalformedTargetFences()
    {
        NativeTransferPackage package = Package(Thread(First), Thread(Second));
        NativeTransferAggregateAdmissionSnapshot admission = NativeTransferAggregateAdmission.Admit(package, [First, Second]).Admission!;

        Assert.Equal(NativeTransferAggregateMergePlanError.DuplicateTargetFence,
            NativeTransferAggregateMergePlanner.Plan(admission, [new(First, null), new(First, null)]).Error);
        Assert.Equal(NativeTransferAggregateMergePlanError.UnknownTarget,
            NativeTransferAggregateMergePlanner.Plan(admission, [new(new("4chan", "g", "999"), null)]).Error);
        Assert.Equal(NativeTransferAggregateMergePlanError.RootlessTarget,
            NativeTransferAggregateMergePlanner.Plan(admission, [new(new("4chan", "g"), null)]).Error);
        Assert.Equal(NativeTransferAggregateMergePlanError.InvalidTargetFence,
            NativeTransferAggregateMergePlanner.Plan(admission, [new(First, "not-a-sha256")]).Error);
    }

    [Fact]
    public void AggregateLimitsApplyToSelectedResourcesOnly()
    {
        NativeTransferPackage package = Package(Thread(First), Thread(Second));
        NativeTransferAggregateAdmissionResult result = NativeTransferAggregateAdmission.Admit(package, [First],
            new NativeTransferAggregateLimits(MaxThreads: 1, MaxPosts: 1, MaxAttachments: 0));
        Assert.True(result.IsAdmitted, result.Error.ToString());

        Assert.Equal(NativeTransferAggregateAdmissionError.PostLimitExceeded,
            NativeTransferAggregateAdmission.Admit(package, [First], new(MaxThreads: 1, MaxPosts: 0, MaxAttachments: 1)).Error);
    }

    private static NativeTransferPackage Package(params TransferThread[] threads)
    {
        NativeTransferCatalog catalog = new(At, threads.ToImmutableArray());
        byte[] catalogBytes;
        try { catalogBytes = NativeTransferCatalogSerializer.Serialize(catalog); }
        catch (InvalidDataException) { catalogBytes = []; }
        NativeTransferEnvelope envelope = new("synthetic-package", "chan-v1", PackageFingerprint,
            new(catalogBytes.Length, Hash(catalogBytes)), ImmutableArray<TransferBlobDescriptor>.Empty)
        { CatalogBytes = ImmutableArray.CreateRange(catalogBytes) };
        return new(envelope, catalog);
    }

    private static TransferThread Thread(ResourceId id) => new(id, null, "synthetic " + id.Thread,
        ThreadSourceState.NotFound, At, At, 1, true,
        [new(new CapturedPost(id.Thread!, 0, PostFieldPresence.All,
            At, "subject", "author", "<p>offline</p>", false, ImmutableArray<CapturedAttachment>.Empty),
            ImmutableArray<TransferAttachment>.Empty)]);

    private static string Hash(byte[] bytes)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
}
