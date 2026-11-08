using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Storage;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

// Real writer -> owned stage -> immutable plan/selection -> inactive Journal2 candidate.
// Fresh MediaStore inspection is not SQL registration, active import or restart publication.
public sealed class NativeV4InactiveCandidateTests
{
    private static readonly ResourceId Resource = new("4chan", "b", "7001");
    private static readonly DateTimeOffset At = new(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid Operation = Guid.Parse("37cfd0be-27be-40c1-88b9-f1e971cb08b6");
    private static readonly byte[] Original = Encoding.UTF8.GetBytes("synthetic original bytes, not playable media");
    private static readonly byte[] Preview = Encoding.UTF8.GetBytes("synthetic preview bytes");

    [Theory]
    [InlineData((int)TransferContentStatus.Omitted)]
    [InlineData((int)TransferContentStatus.Unknown)]
    [InlineData((int)TransferContentStatus.Unavailable)]
    [InlineData((int)TransferContentStatus.Failed)]
    [InlineData((int)TransferContentStatus.Cancelled)]
    public async Task EvidenceOnlyPreservesEveryLegalOriginalAbsenceWithoutManufacturingBytes(int statusValue)
    {
        using Workspace files = new();
        TransferAttachment attachment = Attachment("15", (TransferContentStatus)statusValue);
        await using NativeTransferImportStage stage = await Stage(files, Catalog(attachment));
        NativeMediaMaterializationPlan plan = NativeMediaMaterializationPlan.Create(stage, Resource);
        Assert.Equal(NativeMediaMemberRole.OriginalEvidence, Assert.Single(plan.FullSelection.Members).Id.Role);
        PassiveMediaImportCandidate candidate = await Materialize(files, plan, plan.FullSelection);
        await stage.DisposeAsync();

        MediaAssetSnapshot snapshot = await Inspect(candidate, plan.FullSelection);
        AssertNoOriginal(snapshot, attachment, "15");
        MediaOriginalEvidence evidence = Assert.Single(snapshot.OriginalEvidence);
        Assert.Equal((MediaObservedContentStatus)statusValue, evidence.IncomingStatus);
        Assert.Equal(attachment.Metadata, evidence.Metadata);
        Assert.Equal(Version(Original).Sha256, evidence.OriginalByteVersion!.Sha256);
        Assert.Empty(snapshot.DerivedRepresentations);
        Assert.Empty(Directory.GetFiles(candidate.Directory, "*.bin"));
    }

    [Fact]
    public async Task ContainedEvidenceSelectedWithoutPayloadRemainsEvidenceOnly()
    {
        using Workspace files = new();
        TransferAttachment attachment = Attachment("15", TransferContentStatus.Contained);
        await using NativeTransferImportStage stage = await Stage(files, Catalog(attachment), Original);
        NativeMediaMaterializationPlan plan = NativeMediaMaterializationPlan.Create(stage, Resource);
        NativeMediaMemberId evidenceId = Assert.Single(plan.FullSelection.Members,
            m => m.Id.Role == NativeMediaMemberRole.OriginalEvidence).Id;
        NativeMediaCandidateSelection selection = plan.Select([evidenceId]);
        Assert.False(Assert.Single(selection.Members).ContextOnly);
        PassiveMediaImportCandidate candidate = await Materialize(files, plan, selection);
        await stage.DisposeAsync();

        MediaAssetSnapshot snapshot = await Inspect(candidate, selection);
        AssertNoOriginal(snapshot, attachment, "15");
        Assert.Equal(MediaObservedContentStatus.Contained, Assert.Single(snapshot.OriginalEvidence).IncomingStatus);
        Assert.Empty(Directory.GetFiles(candidate.Directory, "*.bin"));
    }

    [Fact]
    public async Task DerivedOnlySafeSubsetIncludesContextButNeverCopiesContainedOriginal()
    {
        using Workspace files = new();
        TransferAttachment attachment = WithDerived(Attachment("15", TransferContentStatus.Contained),
            TransferDerivedRepresentationKind.Preview, TransferContentStatus.Contained, Preview);
        await using NativeTransferImportStage stage = await Stage(files, Catalog(attachment), Original, Preview);
        NativeMediaMaterializationPlan plan = NativeMediaMaterializationPlan.Create(stage, Resource);
        NativeMediaMemberId previewId = Assert.Single(plan.FullSelection.Members,
            m => m.Id.Role == NativeMediaMemberRole.Preview).Id;
        NativeMediaCandidateSelection safe = plan.Select([previewId]);
        plan.ValidateSelection(safe);
        Assert.Equal(2, safe.Members.Length);
        Assert.True(Assert.Single(safe.Members, m => m.Id.Role == NativeMediaMemberRole.OriginalEvidence).ContextOnly);
        Assert.False(Assert.Single(safe.Members, m => m.Id.Role == NativeMediaMemberRole.Preview).ContextOnly);
        Assert.DoesNotContain(safe.Members, m => m.Id.Role == NativeMediaMemberRole.OriginalPayload);
        Assert.NotEqual(plan.FullSelection.CanonicalJson, safe.CanonicalJson);
        PassiveMediaImportCandidate candidate = await Materialize(files, plan, safe);
        await stage.DisposeAsync();

        MediaAssetSnapshot snapshot = await Inspect(candidate, safe);
        AssertNoOriginal(snapshot, attachment, "15");
        Assert.Equal(MediaObservedContentStatus.Contained, Assert.Single(snapshot.OriginalEvidence).IncomingStatus);
        MediaDerivedRepresentation derived = Assert.Single(snapshot.DerivedRepresentations);
        Assert.Equal(MediaDerivedKind.Preview, derived.Kind);
        await AssertDerivedBytes(snapshot, derived, Preview);
        Assert.Equal(Hash(Preview) + ".derived.bin", Path.GetFileName(Assert.Single(Directory.GetFiles(candidate.Directory, "*.bin"))));
    }

    [Theory]
    [InlineData((int)TransferContentStatus.Omitted, false)]
    [InlineData((int)TransferContentStatus.Unavailable, false)]
    [InlineData((int)TransferContentStatus.Omitted, true)]
    [InlineData((int)TransferContentStatus.Unavailable, true)]
    public async Task NoncontainedDerivedStatusesPreserveObservationWithoutPayload(int statusValue, bool poster)
    {
        using Workspace files = new();
        TransferContentStatus status = (TransferContentStatus)statusValue;
        TransferAttachment attachment = WithDerived(Attachment("15", TransferContentStatus.Omitted),
            poster ? TransferDerivedRepresentationKind.VideoPosterPng : TransferDerivedRepresentationKind.Preview,
            status, Preview);
        await using NativeTransferImportStage stage = await Stage(files, Catalog(attachment));
        NativeMediaMaterializationPlan plan = NativeMediaMaterializationPlan.Create(stage, Resource);
        PassiveMediaImportCandidate candidate = await Materialize(files, plan, plan.FullSelection);
        await stage.DisposeAsync();

        MediaAssetSnapshot snapshot = await Inspect(candidate, plan.FullSelection);
        AssertNoOriginal(snapshot, attachment, "15");
        MediaDerivedRepresentation derived = Assert.Single(snapshot.DerivedRepresentations);
        Assert.Equal(poster ? MediaDerivedKind.VideoPosterPng : MediaDerivedKind.Preview, derived.Kind);
        Assert.Equal(status == TransferContentStatus.Omitted ? MediaDerivedStatus.Omitted : MediaDerivedStatus.Unavailable, derived.IncomingStatus);
        Assert.Null(derived.Blob);
        Assert.Empty(Directory.GetFiles(candidate.Directory, "*.bin"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedOnlyHasZeroCapturedPostsAndPreservesNullableHistoricalSha1(bool missingSha1)
    {
        using Workspace files = new();
        TransferAttachment attachment = Attachment("999", TransferContentStatus.Omitted);
        if (missingSha1) attachment = attachment with { OriginalByteVersion = attachment.OriginalByteVersion! with { Sha1 = null } };
        attachment = WithDerived(attachment, TransferDerivedRepresentationKind.Preview, TransferContentStatus.Contained, Preview);
        await using NativeTransferImportStage stage = await Stage(files, Catalog(attachment, retained: true), Preview);
        Assert.Empty(Assert.Single(stage.Package.Catalog.Threads).Posts);
        NativeMediaMaterializationPlan plan = NativeMediaMaterializationPlan.Create(stage, Resource);
        Assert.All(plan.FullSelection.Members, m => Assert.True(m.Descriptor.Retained));
        PassiveMediaImportCandidate candidate = await Materialize(files, plan, plan.FullSelection);
        await stage.DisposeAsync();

        MediaAssetSnapshot snapshot = await Inspect(candidate, plan.FullSelection);
        AssertNoOriginal(snapshot, attachment, "999");
        MediaOriginalEvidence evidence = Assert.Single(snapshot.OriginalEvidence);
        MediaDerivedRepresentation derived = Assert.Single(snapshot.DerivedRepresentations);
        Assert.Equal("999", evidence.Occurrence.PostId);
        Assert.Equal(evidence.Occurrence, derived.Parent);
        Assert.Equal(attachment.OriginalByteVersion!.Sha1, evidence.OriginalByteVersion!.Sha1);
        Assert.Equal(attachment.OriginalByteVersion.Sha1, derived.SourceVersion.Sha1);
        Assert.Equal(Sha1(Preview), derived.Blob!.Actual.Sha1);
        await AssertDerivedBytes(snapshot, derived, Preview);
    }

    [Fact]
    public async Task SameHashOriginalAndDerivedUseSeparatePhysicalNamesAndRoles()
    {
        using Workspace files = new();
        TransferAttachment attachment = WithDerived(Attachment("15", TransferContentStatus.Contained),
            TransferDerivedRepresentationKind.Preview, TransferContentStatus.Contained, Original);
        await using NativeTransferImportStage stage = await Stage(files, Catalog(attachment), Original);
        Assert.Single(stage.Package.Envelope.Payloads);
        NativeMediaMaterializationPlan plan = NativeMediaMaterializationPlan.Create(stage, Resource);
        Assert.Equal(3, plan.FullSelection.Members.Length);
        PassiveMediaImportCandidate candidate = await Materialize(files, plan, plan.FullSelection);
        await stage.DisposeAsync();

        MediaAssetSnapshot snapshot = await Inspect(candidate, plan.FullSelection);
        MediaAttachmentBinding binding = Assert.Single(snapshot.AttachmentBindings).Value;
        MediaAssetReference asset = Assert.IsType<MediaAssetReference>(snapshot.FindById(binding.AssetId));
        MediaDerivedRepresentation derived = Assert.Single(snapshot.DerivedRepresentations);
        Assert.Equal(Hash(Original) + ".original.bin", Path.GetFileName(asset.Path));
        Assert.Equal(Hash(Original) + ".derived.bin", derived.Blob!.RelativeFileName);
        Assert.NotEqual(Path.GetFileName(asset.Path), derived.Blob.RelativeFileName);
        Assert.Equal(2, Directory.GetFiles(candidate.Directory, "*.bin").Length);
        Assert.Equal(1, snapshot.Count);
        ResolvedMediaAttachment resolved = Assert.IsType<ResolvedMediaAttachment>(
            MediaAttachmentResolver.Resolve(snapshot, Resource, "15", attachment.Metadata));
        Assert.Equal(binding, resolved.Binding);
        Assert.Equal(MediaAcceptanceKind.PassiveImport, binding.Acceptance.Kind);
        await using Stream original = await snapshot.OpenVerifiedReadAsync(asset.AssetId);
        using MemoryStream bytes = new();
        await original.CopyToAsync(bytes);
        Assert.Equal(Original, bytes.ToArray());
        await AssertDerivedBytes(snapshot, derived, Original);
    }

    [Fact]
    public async Task ContainedPngPosterSurvivesStageDisposalAndFreshInspectionWithoutOriginal()
    {
        using Workspace files = new();
        byte[] png = Poster();
        TransferAttachment attachment = WithDerived(Attachment("15", TransferContentStatus.Unavailable),
            TransferDerivedRepresentationKind.VideoPosterPng, TransferContentStatus.Contained, png);
        await using NativeTransferImportStage stage = await Stage(files, Catalog(attachment), png);
        NativeMediaMaterializationPlan plan = NativeMediaMaterializationPlan.Create(stage, Resource);
        PassiveMediaImportCandidate candidate = await Materialize(files, plan, plan.FullSelection);
        await stage.DisposeAsync();

        MediaAssetSnapshot snapshot = await Inspect(candidate, plan.FullSelection);
        AssertNoOriginal(snapshot, attachment, "15");
        MediaDerivedRepresentation derived = Assert.Single(snapshot.DerivedRepresentations);
        Assert.Equal(MediaDerivedKind.VideoPosterPng, derived.Kind);
        await AssertDerivedBytes(snapshot, derived, png);
    }

    private static Task<PassiveMediaImportCandidate> Materialize(Workspace files,
        NativeMediaMaterializationPlan plan, NativeMediaCandidateSelection selection)
        => new MediaStore().MaterializePassiveImportCandidateAsync(plan, selection, files.Candidates, Operation, At);

    private static async Task<MediaAssetSnapshot> Inspect(PassiveMediaImportCandidate candidate,
        NativeMediaCandidateSelection selection)
    {
        Assert.Equal(2, candidate.PlanVersion);
        Assert.Equal(Operation, candidate.OperationId);
        Assert.Equal(Resource, candidate.Resource);
        Assert.Equal(At, candidate.ImportedAtUtc);
        Assert.Equal(selection.CanonicalJson, candidate.Selection!.CanonicalJson);
        Assert.Equal(selection.Sha256, candidate.Selection.Sha256);
        Assert.False(string.IsNullOrEmpty(candidate.InventorySha256));
        Dictionary<string, string> before = Files(candidate.Directory);
        for (int i = 0; i < 2; i++)
        {
            PassiveMediaCandidateInspection inspection = await new MediaStore().InspectPassiveImportCandidateAsync(candidate.Directory);
            Assert.Equal(PassiveMediaCandidateStatus.Ready, inspection.Status);
            PassiveMediaImportCandidate restored = Assert.IsType<PassiveMediaImportCandidate>(inspection.Candidate);
            Assert.Equal(2, restored.PlanVersion);
            Assert.Equal(candidate.OperationId, restored.OperationId);
            Assert.Equal(candidate.PackageFingerprint, restored.PackageFingerprint);
            Assert.Equal(candidate.Resource, restored.Resource);
            Assert.Equal(candidate.ImportedAtUtc, restored.ImportedAtUtc);
            Assert.Equal(candidate.ManifestGeneration, restored.ManifestGeneration);
            Assert.Equal(selection.CanonicalJson, restored.Selection!.CanonicalJson);
            Assert.Equal(selection.Sha256, restored.Selection.Sha256);
            Assert.Equal(candidate.InventorySha256, restored.InventorySha256);
            Assert.Equal(candidate.Bindings.ToArray(), restored.Bindings.ToArray());
            Assert.Equal(candidate.OriginalEvidence.ToArray(), restored.OriginalEvidence.ToArray());
            Assert.Equal(candidate.DerivedRepresentations.ToArray(), restored.DerivedRepresentations.ToArray());
            Assert.Equal(before.OrderBy(p => p.Key), Files(candidate.Directory).OrderBy(p => p.Key));
        }
        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(candidate.Directory);
        Assert.Equal(MediaManifestStatus.Available, snapshot.Status);
        Assert.Equal(6, snapshot.ManifestVersion);
        Assert.Equal(candidate.ManifestGeneration, snapshot.Generation);
        Assert.Equal(candidate.OriginalEvidence.ToArray(), snapshot.OriginalEvidence.ToArray());
        Assert.Equal(candidate.DerivedRepresentations.ToArray(), snapshot.DerivedRepresentations.ToArray());
        Assert.All(snapshot.OriginalEvidence, e => Assert.Equal(
            new MediaPassiveObservation(Operation.ToString("D"), candidate.PackageFingerprint, At), e.Origin));
        Assert.All(snapshot.DerivedRepresentations, d => Assert.Equal(
            new MediaPassiveObservation(Operation.ToString("D"), candidate.PackageFingerprint, At), d.Origin));
        Assert.Empty(Directory.GetFiles(candidate.Directory, "*.part"));
        return snapshot;
    }

    private static void AssertNoOriginal(MediaAssetSnapshot snapshot, TransferAttachment attachment, string post)
    {
        Assert.Equal(0, snapshot.Count);
        Assert.Empty(snapshot.GetPage());
        Assert.Empty(snapshot.AttachmentBindings);
        Assert.Null(snapshot.FindAttachmentBinding(attachment.Metadata.AttachmentRevisionKey!));
        Assert.Null(MediaAttachmentResolver.Resolve(snapshot, Resource, post, attachment.Metadata));
        Assert.Empty(Directory.GetFiles(snapshot.Directory, "*.original.bin"));
    }

    private static async Task AssertDerivedBytes(MediaAssetSnapshot snapshot, MediaDerivedRepresentation derived, byte[] expected)
    {
        await using Stream input = await snapshot.OpenVerifiedDerivedReadAsync(derived);
        using MemoryStream bytes = new();
        await input.CopyToAsync(bytes);
        Assert.Equal(expected, bytes.ToArray());
        Assert.Equal(Hash(expected), derived.Blob!.Actual.Sha256);
        Assert.Equal(Md5(expected), derived.Blob.Actual.Md5);
        Assert.Equal(Sha1(expected), derived.Blob.Actual.Sha1);
        Assert.Equal(expected.LongLength, derived.Blob.Actual.Length);
    }

    private static TransferAttachment Attachment(string post, TransferContentStatus status)
    {
        MediaProviderMetadata provider = new("111", "original", "https://i.4cdn.org/b/111.webm", "synthetic.webm",
            Original.Length, Md5(Original), Hash(Original));
        CapturedAttachment metadata = new(0, provider.MediaId, provider.Variant, provider.Url,
            provider.OriginalFileName, ExpectedSize: provider.ExpectedSize, ExpectedMd5: provider.ExpectedMd5)
        {
            ExpectedSha256 = provider.ExpectedSha256,
            AttachmentRevisionKey = MediaRevisionKeys.CreateAttachmentKey(Resource, post, 0, provider)
        };
        return new(metadata, TransferMediaRole.Original, status,
            status == TransferContentStatus.Contained ? Hash(Original) : null, null)
        { OriginalByteVersion = Version(Original) };
    }

    private static TransferAttachment WithDerived(TransferAttachment parent, TransferDerivedRepresentationKind kind,
        TransferContentStatus status, byte[] bytes) => parent with
    {
        DerivedRepresentations = [new(kind, parent.Metadata.AttachmentRevisionKey!, parent.OriginalByteVersion!,
            status, status == TransferContentStatus.Contained ? Hash(bytes) : null)]
    };

    private static NativeTransferCatalog Catalog(TransferAttachment attachment, bool retained = false)
    {
        TransferPost post = new(new("15", 0, PostFieldPresence.All, At, "", "", "synthetic", false,
            [attachment.Metadata]), [attachment]);
        TransferThread thread = new(Resource, null, "inactive candidate", ThreadSourceState.NotFound, At, At, 1, true,
            retained ? ImmutableArray<TransferPost>.Empty : [post]);
        if (retained) thread = thread with { RetainedBindings = [new("999", attachment)] };
        return new(At, [thread]);
    }

    private static async Task<NativeTransferImportStage> Stage(Workspace files, NativeTransferCatalog catalog, params byte[][] payloads)
    {
        using MemoryStream package = new();
        TransferPayloadSource[] sources = payloads.DistinctBy(Hash).Select(bytes => new TransferPayloadSource(
            new(bytes.Length, Hash(bytes)), _ => Task.FromResult<Stream>(new MemoryStream(bytes, false)))).ToArray();
        TransferWriteResult written = await NativeTransferPackageWriter.WriteAsync(package, catalog, sources,
            Guid.Parse("7e274989-53cb-4609-9a13-c5e16970b053"));
        Assert.True(written.IsComplete, written.ToString());
        package.Position = 0;
        NativeTransferStageResult admitted = await NativeTransferImportStage.CreateAsync(package, files.Staging);
        Assert.Equal(NativeTransferStageError.None, admitted.Error);
        NativeTransferImportStage stage = Assert.IsType<NativeTransferImportStage>(admitted.Stage);
        Assert.True(stage.Package.IsChanV4);
        Assert.Null(stage.Package.Envelope.PrivateMetadata);
        return stage;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Md5(byte[] bytes) => Convert.ToBase64String(MD5.HashData(bytes));
    private static string Sha1(byte[] bytes) => Convert.ToBase64String(SHA1.HashData(bytes));
    private static TransferOriginalByteVersion Version(byte[] bytes) => new(bytes.LongLength, Hash(bytes), Md5(bytes), Sha1(bytes));
    private static Dictionary<string, string> Files(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(p => Path.GetRelativePath(root, p), p => Hash(File.ReadAllBytes(p)), StringComparer.Ordinal);

    // Bounded structural PNG, as in existing v4 preview tests; not a video/decoder claim.
    private static byte[] Poster()
    {
        byte[] header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, 1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 1);
        header[8] = 8;
        byte[] Chunk(string type, byte[] data)
        {
            byte[] result = new byte[data.Length + 12];
            BinaryPrimitives.WriteUInt32BigEndian(result, (uint)data.Length);
            Encoding.ASCII.GetBytes(type).CopyTo(result, 4);
            data.CopyTo(result, 8);
            uint crc = uint.MaxValue;
            foreach (byte value in result.AsSpan(4, data.Length + 4))
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0u);
            }
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(result.Length - 4), ~crc);
            return result;
        }
        return [137, 80, 78, 71, 13, 10, 26, 10, .. Chunk("IHDR", header),
            .. Chunk("IDAT", [0x78, 0x9c, 0x63, 0x60, 0x00, 0x00, 0x00, 0x02, 0x00, 0x01]), .. Chunk("IEND", [])];
    }

    private sealed class Workspace : IDisposable
    {
        private readonly string root = Directory.CreateTempSubdirectory("meowchan-v4-inactive-tests-").FullName;
        internal string Candidates { get; }
        internal string Staging { get; }
        internal Workspace()
        {
            Candidates = Directory.CreateDirectory(Path.Combine(root, "candidates")).FullName;
            Staging = Directory.CreateDirectory(Path.Combine(root, "staging")).FullName;
        }
        public void Dispose() => Directory.Delete(root, recursive: true);
    }
}
