using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Favorites;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Tests;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

// Live-authority boundary tests. The request/plan values are synthetic, while
// the profile lease, owner registry and exclusive token are real test objects.
public sealed class NativePrivateApplyCapabilityTests
{
    private static readonly ResourceId Thread = new("4chan", "b", "9101");
    private static readonly Guid PackageId = Guid.Parse("c2fd6540-1bb7-4fa5-a4f6-70b3b3d2f306");
    private static readonly DateTimeOffset At = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Media = Encoding.UTF8.GetBytes("capability-test-original");
    private const string Note = "capability-test-private-note";

    [Fact]
    public async Task OwnerIssuedPlanAndExclusiveTokenCreateNonSerializableCapability()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        using MemoryStream bytes = await WritePackageAsync();
        NativeTransferStageResult staged = await NativeTransferImportStage.CreateAsync(bytes, directory.Path);
        NativeTransferImportStage stage = Assert.IsType<NativeTransferImportStage>(staged.Stage);
        await using (stage)
        {
            NativePrivateMetadataSelection selection = new(true, true, true);
            NativePrivateConsentDecision consent = catalog.IssueNativePrivateConsentDecision(
                stage, NativePrivateConsentScope.ExplicitPrivateMetadata, selection);
            NativeMediaImportPreparation prepared = await catalog.PrepareNativeMediaImportAsync(stage, consent);
            NativeMediaImportPlan plan = Assert.IsType<NativeMediaImportPlan>(prepared.Plan);
            NativePrivateMetadataProjection incoming = Assert.IsType<NativePrivateMetadataProjection>(
                plan.SelectedPrivateMetadata);
            NativePrivateMetadataComparisonResult comparison = NativePrivateMetadataComparison.Analyze(
                incoming, plan.Package.Catalog, incoming, selection);

            if (lease.Admission.Phase == ProfileAdmissionPhase.Starting)
                lease.Admission.CompleteStartup();
            using ProfileAdmissionToken admission = await lease.Admission.EnterExclusiveAsync();
            using NativePrivateApplyCapability capability = catalog.IssueNativePrivateApplyCapability(
                lease, admission, plan, comparison, NoChangeFences(selection));

            Assert.Equal(stage.PackageLength, capability.Request.Binding.PackageLength);
            Assert.Equal(plan.OperationId, capability.OperationId);
            Assert.Equal(plan.OperationId, capability.Request.OperationId);
            Assert.Equal(capability.Request.ContentPlanSha256, capability.ContentPlanSha256);
            Assert.Equal(capability.Request.RequestSha256, capability.RequestSha256);
            Assert.Equal(selection, capability.Selection);
            Assert.NotEqual(Guid.Empty, capability.ProfileIncarnation);
            Assert.Equal(64, capability.TargetFenceSha256.Length);
            Assert.Equal(capability.Request.Binding.TargetFenceSha256, capability.TargetFenceSha256);
            Assert.Equal(42, capability.RunAdmitted(request =>
            {
                Assert.Same(capability.Request, request);
                return 42;
            }));

            Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(capability));
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<NativePrivateApplyCapability>("{}"));
            Assert.Throws<InvalidOperationException>(() => catalog.IssueNativePrivateApplyCapability(
                lease, admission, plan, comparison, NoChangeFences(selection)));
        }
    }

    [Fact]
    public async Task CapabilityRejectsOrdinaryAdmissionAndUnissuedPlan()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        using MemoryStream bytes = await WritePackageAsync();
        NativeTransferStageResult staged = await NativeTransferImportStage.CreateAsync(bytes, directory.Path);
        NativeTransferImportStage stage = Assert.IsType<NativeTransferImportStage>(staged.Stage);
        await using (stage)
        {
            NativePrivateMetadataSelection selection = new(true, true, true);
            NativePrivateConsentDecision consent = catalog.IssueNativePrivateConsentDecision(
                stage, NativePrivateConsentScope.ExplicitPrivateMetadata, selection);
            NativeMediaImportPlan plan = Assert.IsType<NativeMediaImportPlan>(
                (await catalog.PrepareNativeMediaImportAsync(stage, consent)).Plan);
            NativePrivateMetadataProjection incoming = Assert.IsType<NativePrivateMetadataProjection>(
                plan.SelectedPrivateMetadata);
            NativePrivateMetadataComparisonResult comparison = NativePrivateMetadataComparison.Analyze(
                incoming, plan.Package.Catalog, incoming, selection);

            lease.Admission.CompleteStartup();
            using (ProfileAdmissionToken ordinary = lease.Admission.EnterOrdinaryWriter())
                Assert.Throws<InvalidOperationException>(() => catalog.IssueNativePrivateApplyCapability(
                    lease, ordinary, plan, comparison, NoChangeFences(selection)));

            using TempDirectory foreignDirectory = new();
            using ProfileLease foreignLease = ProfileLease.Acquire(foreignDirectory.Path);
            foreignLease.Admission.CompleteStartup();
            using ProfileAdmissionToken foreignAdmission = await foreignLease.Admission.EnterExclusiveAsync();
            Assert.Throws<InvalidOperationException>(() => catalog.IssueNativePrivateApplyCapability(
                foreignLease, foreignAdmission, plan, comparison, NoChangeFences(selection)));

            NativeMediaImportPlan unissued = new(catalog, stage, plan.Package, plan.Instance,
                plan.Generation, plan.OperationId, plan.RequestSha256, plan.OperationsParent,
                plan.Attachments, plan.PrivateConsent);
            using ProfileAdmissionToken admission = await lease.Admission.EnterExclusiveAsync();
            Assert.Throws<InvalidOperationException>(() => catalog.IssueNativePrivateApplyCapability(
                lease, admission, unissued, comparison, NoChangeFences(selection)));
        }
    }

    [Fact]
    public async Task CapabilityBindsConsentSelectionAndReleasesExclusiveTokenOnDispose()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        using MemoryStream bytes = await WritePackageAsync();
        NativeTransferStageResult staged = await NativeTransferImportStage.CreateAsync(bytes, directory.Path);
        NativeTransferImportStage stage = Assert.IsType<NativeTransferImportStage>(staged.Stage);
        await using (stage)
        {
            NativePrivateMetadataSelection all = new(true, true, true);
            NativePrivateConsentDecision consent = catalog.IssueNativePrivateConsentDecision(
                stage, NativePrivateConsentScope.ExplicitPrivateMetadata, all);
            NativeMediaImportPlan plan = Assert.IsType<NativeMediaImportPlan>(
                (await catalog.PrepareNativeMediaImportAsync(stage, consent)).Plan);
            NativePrivateMetadataProjection incoming = Assert.IsType<NativePrivateMetadataProjection>(
                plan.SelectedPrivateMetadata);
            NativePrivateMetadataSelection threadOnly = new(ThreadFavorites: true);
            NativePrivateMetadataComparisonResult mismatch = NativePrivateMetadataComparison.Analyze(
                incoming, plan.Package.Catalog, incoming, threadOnly);

            lease.Admission.CompleteStartup();
            using ProfileAdmissionToken admission = await lease.Admission.EnterExclusiveAsync();
            NativePrivateApplyCapability capability = catalog.IssueNativePrivateApplyCapability(
                lease, admission, plan,
                NativePrivateMetadataComparison.Analyze(incoming, plan.Package.Catalog, incoming, all),
                NoChangeFences(all));
            Assert.Throws<InvalidDataException>(() => catalog.IssueNativePrivateApplyCapability(
                lease, admission, plan, mismatch, NoChangeFences(threadOnly)));

            capability.Dispose();
            Assert.Throws<ObjectDisposedException>(() => capability.RunAdmitted(_ => true));
            using ProfileAdmissionToken replacement = await lease.Admission.EnterExclusiveAsync();
            Assert.Equal(ProfileAdmissionKind.Exclusive, replacement.Kind);
        }
    }

    [Fact]
    public async Task ProfileIncarnationIsStablePerLeaseAndDifferentAcrossLeases()
    {
        await using CapabilityContext first = await CapabilityContext.CreateAsync();
        await using CapabilityContext second = await CapabilityContext.CreateAsync();

        Assert.Equal(first.Capability.ProfileIncarnation, first.Capability.ProfileIncarnation);
        Assert.NotEqual(first.Capability.ProfileIncarnation, second.Capability.ProfileIncarnation);
    }

    private sealed class CapabilityContext : IAsyncDisposable
    {
        private CapabilityContext(TempDirectory directory, ProfileLease lease, ArchiveCatalog catalog,
            NativeTransferImportStage stage, NativePrivateApplyCapability capability)
        {
            Directory = directory; Lease = lease; Catalog = catalog; Stage = stage; Capability = capability;
        }

        private TempDirectory Directory { get; }
        private ProfileLease Lease { get; }
        private ArchiveCatalog Catalog { get; }
        private NativeTransferImportStage Stage { get; }
        internal NativePrivateApplyCapability Capability { get; }

        internal static async Task<CapabilityContext> CreateAsync()
        {
            TempDirectory directory = new();
            ProfileLease? lease = null;
            ArchiveCatalog? catalog = null;
            NativeTransferImportStage? stage = null;
            NativePrivateApplyCapability? capability = null;
            try
            {
                lease = ProfileLease.Acquire(directory.Path);
                catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
                using MemoryStream bytes = await WritePackageAsync();
                NativeTransferStageResult staged = await NativeTransferImportStage.CreateAsync(bytes, directory.Path);
                stage = Assert.IsType<NativeTransferImportStage>(staged.Stage);
                NativePrivateMetadataSelection selection = new(true, true, true);
                NativePrivateConsentDecision consent = catalog.IssueNativePrivateConsentDecision(
                    stage, NativePrivateConsentScope.ExplicitPrivateMetadata, selection);
                NativeMediaImportPlan plan = Assert.IsType<NativeMediaImportPlan>(
                    (await catalog.PrepareNativeMediaImportAsync(stage, consent)).Plan);
                NativePrivateMetadataProjection incoming = Assert.IsType<NativePrivateMetadataProjection>(
                    plan.SelectedPrivateMetadata);
                NativePrivateMetadataComparisonResult comparison = NativePrivateMetadataComparison.Analyze(
                    incoming, plan.Package.Catalog, incoming, selection);
                lease.Admission.CompleteStartup();
                ProfileAdmissionToken admission = await lease.Admission.EnterExclusiveAsync();
                try
                {
                    capability = catalog.IssueNativePrivateApplyCapability(
                        lease, admission, plan, comparison, NoChangeFences(selection));
                }
                catch
                {
                    admission.Dispose();
                    throw;
                }
                return new(directory, lease, catalog, stage, capability);
            }
            catch
            {
                if (capability is not null) capability.Dispose();
                if (stage is not null) await stage.DisposeAsync();
                if (catalog is not null) await catalog.DisposeAsync();
                lease?.Dispose();
                directory.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Capability.Dispose();
            await Stage.DisposeAsync();
            await Catalog.DisposeAsync();
            Lease.Dispose();
            Directory.Dispose();
        }
    }

    private static async Task<MemoryStream> WritePackageAsync()
    {
        NativeTransferCatalog catalog = new(At, [new(Thread,
            "https://boards.4chan.org/b/thread/9101", "synthetic capability thread",
            ThreadSourceState.NotFound, At, At, 1, true, [new(
                new CapturedPost("1", 1, PostFieldPresence.All, At, "", null,
                    "synthetic public post", false, [new CapturedAttachment(0, "9101001",
                        "original", "https://i.4cdn.org/b/9101001.jpg", "synthetic.jpg")]),
                [new(new CapturedAttachment(0, "9101001", "original",
                    "https://i.4cdn.org/b/9101001.jpg", "synthetic.jpg"),
                    TransferMediaRole.Original, TransferContentStatus.Contained,
                    Hash(Media), null)])])]);
        NativePrivateMetadataProjection privateMetadata = new([Thread],
            new(true, true, true), [Thread],
            [new(Thread, "9101001", "original", Hash(Media))],
            [new(new(Thread, "1"), Note)]);
        MemoryStream output = new();
        TransferPayloadSource source = new(new(Media.Length, Hash(Media)),
            _ => Task.FromResult<Stream>(new MemoryStream(Media, writable: false)));
        TransferWriteResult result = await NativeTransferPackageWriter.WriteAsync(output, catalog,
            [source], PackageId, privateMetadata: privateMetadata);
        Assert.True(result.IsComplete, result.Error.ToString());
        output.Position = 0;
        return output;
    }

    private static ImmutableArray<NativePrivateApplyStoreFence> NoChangeFences(
        NativePrivateMetadataSelection selection)
    {
        NativePrivateApplyStoreImage image = new(true, 2,
            Guid.Parse("c4bb0f28-9348-4fef-a5a2-4fbd1db1c9a3"), 4, 100, Hash('a'));
        NativePrivateApplyStoreImage missing = new(false, 0, null, 0, 0, null);
        ImmutableArray<NativePrivateApplyStoreFence>.Builder result =
            ImmutableArray.CreateBuilder<NativePrivateApplyStoreFence>();
        if (selection.ThreadFavorites)
            result.Add(new(NativePrivateApplyStoreKind.ThreadFavorites, image, image, missing));
        if (selection.MediaFavorites)
            result.Add(new(NativePrivateApplyStoreKind.MediaFavorites, image, image, missing));
        if (selection.PostBookmarksAndNotes)
            result.Add(new(NativePrivateApplyStoreKind.PostBookmarksAndNotes, image, image));
        return result.ToImmutable();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Hash(char value) => new(value, 64);
}
