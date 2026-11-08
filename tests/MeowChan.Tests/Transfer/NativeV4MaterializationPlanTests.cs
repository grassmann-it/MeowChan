using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Storage;
using MeowChan.Testing;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

// W32b plan-only coverage. Every input is a real writer -> validator -> owned Stage;
// the plan is intentionally exercised without MediaStore, SQL, profile leases,
// candidate roots, active registration, payload copies, or decoder work.
public sealed class NativeV4MaterializationPlanTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly ResourceId Resource = new("4chan", "b", "7001");
    private static readonly ResourceId ForeignResource = new("4chan", "b", "7002");
    private static readonly byte[] Original = Encoding.UTF8.GetBytes("synthetic v4 original bytes; no decoder claim");
    private static readonly byte[] Preview = Encoding.UTF8.GetBytes("synthetic legacy preview bytes");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RealV4StageProducesEvidenceAndPayloadWithoutEnrichingNullableSha1(bool sourceSha1Known)
    {
        using Workspace files = new();
        TransferAttachment attachment = OriginalAttachment(Resource, "15", TransferContentStatus.Contained,
            sourceSha1Known: sourceSha1Known);
        await using NativeTransferImportStage stage = await StageAsync(files, Catalog(Thread(Resource, Post("15", attachment))), Original);
        string[] before = Inventory(files.Root);

        NativeMediaMaterializationPlan plan = NativeMediaMaterializationPlan.Create(stage, Resource);

        Assert.Equal(before, Inventory(files.Root));
        Assert.Same(stage, plan.Stage);
        Assert.Same(stage.Package, plan.Package);
        Assert.Equal(Resource, plan.Resource);
        NativeMediaSelectedMember evidence = Assert.Single(plan.FullSelection.Members,
            member => member.Id.Role == NativeMediaMemberRole.OriginalEvidence);
        NativeMediaSelectedMember payload = Assert.Single(plan.FullSelection.Members,
            member => member.Id.Role == NativeMediaMemberRole.OriginalPayload);
        Assert.Equal(attachment.Metadata, evidence.Descriptor.Metadata);
        Assert.Equal(attachment.Metadata, payload.Descriptor.Metadata);
        Assert.False(evidence.ContextOnly);
        Assert.False(payload.ContextOnly);
        Assert.Equal(attachment.OriginalByteVersion, evidence.Id.SourceVersion);
        Assert.Equal(attachment.OriginalByteVersion, payload.Id.SourceVersion);
        Assert.Equal(sourceSha1Known ? Sha1(Original) : null, evidence.Id.SourceVersion!.Sha1);
        Assert.Equal(sourceSha1Known ? Sha1(Original) : null, payload.Id.SourceVersion!.Sha1);
        Assert.Equal(Hash(Original), payload.Descriptor.PayloadSha256);
        Assert.Equal(Hash(Original), payload.Descriptor.MeasuredPayload!.Sha256);
        // The package's measured SHA1 is known even when the sender's nullable source
        // evidence intentionally recorded no SHA1; the plan must not enrich the ID.
        Assert.Equal(Sha1(Original), payload.Descriptor.MeasuredPayload.Sha1);
    }

    [Fact]
    public async Task OmittedUnknownAndUnkeyedOriginalsRemainEvidenceOnlyWithoutFakeKeys()
    {
        using Workspace files = new();
        TransferAttachment anchor = OriginalAttachment(Resource, "10", TransferContentStatus.Contained);
        TransferAttachment omitted = OriginalAttachment(Resource, "11", TransferContentStatus.Omitted);
        TransferAttachment unknown = OriginalAttachment(Resource, "12", TransferContentStatus.Unknown);
        TransferAttachment unkeyed = UnkeyedEvidence("13");
        NativeTransferCatalog catalog = Catalog(Thread(Resource,
            Post("10", anchor), Post("11", omitted), Post("12", unknown), Post("13", unkeyed)));
        await using NativeTransferImportStage stage = await StageAsync(files, catalog, Original);

        NativeMediaMaterializationPlan plan = NativeMediaMaterializationPlan.Create(stage, Resource);

        Assert.Equal(5, plan.FullSelection.Members.Length);
        Assert.Equal(1, plan.FullSelection.Members.Count(member => member.Id.Role == NativeMediaMemberRole.OriginalPayload));
        foreach ((TransferAttachment input, string postId) in new[]
        {
            (omitted, "11"), (unknown, "12"), (unkeyed, "13")
        })
        {
            NativeMediaSelectedMember evidence = Assert.Single(plan.FullSelection.Members,
                member => member.Id.Role == NativeMediaMemberRole.OriginalEvidence
                    && member.Id.Occurrence.PostId == postId);
            Assert.Equal(input.Metadata.AttachmentRevisionKey, evidence.Id.Occurrence.OriginalRevisionKey);
            Assert.Equal(input.OriginalByteVersion, evidence.Id.SourceVersion);
            Assert.DoesNotContain(plan.FullSelection.Members,
                member => member.Id.Role == NativeMediaMemberRole.OriginalPayload
                    && member.Id.Occurrence.PostId == postId);
        }
        Assert.Null(Assert.Single(plan.FullSelection.Members,
            member => member.Id.Occurrence.PostId == "13").Id.Occurrence.OriginalRevisionKey);
        Assert.Null(Assert.Single(plan.FullSelection.Members,
            member => member.Id.Occurrence.PostId == "13").Id.SourceVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedAttachmentKeepsHistoricalPlacementAndDoesNotInventPost(bool sourceSha1Known)
    {
        using Workspace files = new();
        TransferAttachment retained = OriginalAttachment(Resource, "999", TransferContentStatus.Omitted,
            sourceSha1Known: sourceSha1Known);
        TransferThread thread = Thread(Resource) with
        {
            RetainedBindings = [new("999", retained)]
        };
        await using NativeTransferImportStage stage = await StageAsync(files, Catalog(thread));
        string[] before = Inventory(files.Root);

        NativeMediaMaterializationPlan plan = NativeMediaMaterializationPlan.Create(stage, Resource);

        Assert.Equal(before, Inventory(files.Root));
        Assert.Empty(Assert.Single(plan.Package.Catalog.Threads).Posts);
        Assert.All(plan.FullSelection.Members, member => Assert.True(member.Descriptor.Retained));
        NativeMediaSelectedMember evidence = Assert.Single(plan.FullSelection.Members,
            member => member.Id.Role == NativeMediaMemberRole.OriginalEvidence);
        Assert.Equal("999", evidence.Id.Occurrence.PostId);
        Assert.Equal(retained.Metadata.AttachmentRevisionKey, evidence.Id.Occurrence.OriginalRevisionKey);
        Assert.Equal(retained.OriginalByteVersion, evidence.Id.SourceVersion);
        Assert.DoesNotContain(plan.FullSelection.Members,
            member => member.Id.Role == NativeMediaMemberRole.OriginalPayload);
    }

    [Fact]
    public async Task NullAttachmentCollectionIsPreservedWithoutInventingAnyMemberOrWriting()
    {
        using Workspace files = new();
        TransferAttachment anchor = OriginalAttachment(Resource, "15", TransferContentStatus.Contained);
        TransferPost nullCollection = new(
            new CapturedPost("16", 1, PostFieldPresence.Attachments, Attachments: null), [])
        { AttachmentsWereNull = true };
        TransferPost emptyCollection = Post("17");
        NativeTransferCatalog catalog = Catalog(Thread(Resource, Post("15", anchor), nullCollection, emptyCollection));
        await using NativeTransferImportStage stage = await StageAsync(files, catalog, Original);
        string[] before = Inventory(files.Root);

        NativeMediaMaterializationPlan plan = NativeMediaMaterializationPlan.Create(stage, Resource);

        Assert.Equal(before, Inventory(files.Root));
        TransferPost stagedNullCollection = Assert.Single(plan.Package.Catalog.Threads.Single().Posts,
            post => post.Metadata.PostId == "16");
        Assert.Equal(PostFieldPresence.Attachments, stagedNullCollection.Metadata.Fields);
        Assert.True(stagedNullCollection.AttachmentsWereNull);
        Assert.Null(stagedNullCollection.Metadata.Attachments);
        Assert.Empty(stagedNullCollection.Attachments);
        TransferPost stagedEmptyCollection = Assert.Single(plan.Package.Catalog.Threads.Single().Posts,
            post => post.Metadata.PostId == "17");
        Assert.False(stagedEmptyCollection.AttachmentsWereNull);
        Assert.NotNull(stagedEmptyCollection.Metadata.Attachments);
        Assert.Empty(stagedEmptyCollection.Metadata.Attachments);
        Assert.Empty(stagedEmptyCollection.Attachments);
        Assert.Equal(3, plan.Package.Catalog.Threads.Single().Posts.Length);
        Assert.Equal(2, plan.FullSelection.Members.Length);
        Assert.All(plan.FullSelection.Members, member => Assert.Equal("15", member.Id.Occurrence.PostId));
        Assert.Single(plan.FullSelection.Members, member => member.Id.Role == NativeMediaMemberRole.OriginalEvidence);
        Assert.Single(plan.FullSelection.Members, member => member.Id.Role == NativeMediaMemberRole.OriginalPayload);
        Assert.Empty(Directory.GetDirectories(files.Root, "candidate*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DuplicateUnknownAndForeignSelectionsAreRejectedExactly()
    {
        using Workspace files = new();
        TransferAttachment attachment = OriginalAttachment(Resource, "15", TransferContentStatus.Contained);
        await using NativeTransferImportStage stage = await StageAsync(files,
            Catalog(Thread(Resource, Post("15", attachment))), Original);
        NativeMediaMaterializationPlan plan = NativeMediaMaterializationPlan.Create(stage, Resource);
        NativeMediaMemberId payloadId = Assert.Single(plan.FullSelection.Members,
            member => member.Id.Role == NativeMediaMemberRole.OriginalPayload).Id;

        Assert.Throws<InvalidDataException>(() => plan.Select([payloadId, payloadId]));
        Assert.Throws<InvalidDataException>(() => plan.Select([
            payloadId with { Occurrence = payloadId.Occurrence with { PostId = "does-not-exist" } }]));
        Assert.Throws<InvalidDataException>(() => plan.Select([
            payloadId with { Occurrence = payloadId.Occurrence with { Resource = ForeignResource } }]));

        await using NativeTransferImportStage foreignStage = await StageAsync(files,
            Catalog(Thread(ForeignResource, Post("15", OriginalAttachment(ForeignResource, "15",
                TransferContentStatus.Contained)))), Original);
        NativeMediaMaterializationPlan foreignPlan = NativeMediaMaterializationPlan.Create(foreignStage, ForeignResource);
        Assert.Throws<InvalidDataException>(() => plan.ValidateSelection(foreignPlan.FullSelection));
    }

    [Fact]
    public async Task MultiThreadV4IsRejectedBeforeAnyPlanOutputOrWrite()
    {
        using Workspace files = new();
        TransferAttachment first = OriginalAttachment(Resource, "15", TransferContentStatus.Contained);
        TransferAttachment second = OriginalAttachment(ForeignResource, "15", TransferContentStatus.Contained);
        await using NativeTransferImportStage stage = await StageAsync(files,
            Catalog(Thread(Resource, Post("15", first)), Thread(ForeignResource, Post("15", second))), Original);
        string[] before = Inventory(files.Root);

        Assert.Throws<InvalidDataException>(() => NativeMediaMaterializationPlan.Create(stage, Resource));

        Assert.Equal(before, Inventory(files.Root));
        Assert.Empty(Directory.GetDirectories(files.Root, "candidate*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task LegacyPreviewRoleIsRejectedBeforeAnyPlanOutputOrWrite()
    {
        using Workspace files = new();
        TransferAttachment anchor = OriginalAttachment(Resource, "15", TransferContentStatus.Contained);
        TransferAttachment legacyPreview = new(
            new CapturedAttachment(1, "112", "preview", "https://i.4cdn.org/b/112s.jpg", "preview.jpg",
                ExpectedSize: Preview.Length, ExpectedMd5: Md5(Preview)) with
            { ExpectedSha256 = Hash(Preview) },
            TransferMediaRole.Preview, TransferContentStatus.Contained, Hash(Preview), null);
        await using NativeTransferImportStage stage = await StageAsync(files,
            Catalog(Thread(Resource, Post("15", anchor, legacyPreview))), Original, Preview);
        Assert.True(stage.Package.IsChanV4);
        string[] before = Inventory(files.Root);

        Assert.Throws<InvalidDataException>(() => NativeMediaMaterializationPlan.Create(stage, Resource));

        Assert.Equal(before, Inventory(files.Root));
        Assert.Empty(Directory.GetDirectories(files.Root, "candidate*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CanceledCreateDoesNotReadBeyondTheValidatedStageOrWrite()
    {
        using Workspace files = new();
        TransferAttachment attachment = OriginalAttachment(Resource, "15", TransferContentStatus.Contained);
        await using NativeTransferImportStage stage = await StageAsync(files,
            Catalog(Thread(Resource, Post("15", attachment))), Original);
        string[] before = Inventory(files.Root);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            NativeMediaMaterializationPlan.Create(stage, Resource, cancellation.Token));

        Assert.Equal(before, Inventory(files.Root));
    }

    private static TransferAttachment OriginalAttachment(ResourceId resource, string postId,
        TransferContentStatus status, bool sourceSha1Known = true)
    {
        MediaProviderMetadata provider = new("media-" + postId, "original",
            $"https://i.4cdn.org/{resource.Board}/{postId}.webm", "synthetic.webm",
            Original.Length, Md5(Original), Hash(Original))
        {
            ExpectedSha1 = sourceSha1Known ? Sha1(Original) : null
        };
        CapturedAttachment metadata = new(0, provider.MediaId, provider.Variant, provider.Url,
            provider.OriginalFileName, ExpectedSize: provider.ExpectedSize, ExpectedMd5: provider.ExpectedMd5)
        {
            ExpectedSha256 = provider.ExpectedSha256,
            ExpectedSha1 = provider.ExpectedSha1,
            AttachmentRevisionKey = MediaRevisionKeys.CreateAttachmentKey(resource, postId, 0, provider)
        };
        return new(metadata, TransferMediaRole.Original, status,
            status == TransferContentStatus.Contained ? Hash(Original) : null, null)
        {
            OriginalByteVersion = Version(Original, sourceSha1Known)
        };
    }

    private static TransferAttachment UnkeyedEvidence(string postId)
        => new(new CapturedAttachment(0, null, "original", IsDeleted: true),
            TransferMediaRole.Original, TransferContentStatus.Unknown, null, null)
        { OriginalByteVersion = null };

    private static TransferPost Post(string postId, params TransferAttachment[] attachments)
    {
        CapturedAttachment[] metadata = attachments.Select(attachment => attachment.Metadata).ToArray();
        return new(new CapturedPost(postId, 1, PostFieldPresence.All, At, "", "",
            "synthetic post", false, metadata), attachments.ToImmutableArray());
    }

    private static TransferThread Thread(ResourceId resource, params TransferPost[] posts)
        => new(resource, $"https://boards.4chan.org/{resource.Board}/thread/{resource.Thread}",
            "synthetic v4 thread", ThreadSourceState.NotFound, At, At, 1, true, posts.ToImmutableArray())
        { RetainedBindings = [] };

    private static NativeTransferCatalog Catalog(params TransferThread[] threads)
        => new(At, threads.ToImmutableArray());

    private static async Task<NativeTransferImportStage> StageAsync(Workspace files,
        NativeTransferCatalog catalog, params byte[][] payloads)
    {
        using MemoryStream package = new();
        TransferPayloadSource[] sources = payloads.DistinctBy(Hash).Select(bytes =>
            new TransferPayloadSource(new(bytes.Length, Hash(bytes)),
                _ => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false)))).ToArray();
        TransferWriteResult written = await NativeTransferPackageWriter.WriteAsync(package, catalog, sources, Guid.NewGuid());
        Assert.True(written.IsComplete, written.ToString());
        Assert.True(written.Package!.IsChanV4, "The fixture must exercise the chan-v4 plan.");
        package.Position = 0;
        NativeTransferStageResult staged = await NativeTransferImportStage.CreateAsync(package, files.Staging);
        Assert.Equal(NativeTransferStageError.None, staged.Error);
        Assert.Equal(TransferEnvelopeError.None, staged.EnvelopeError);
        Assert.Equal(TransferCatalogError.None, staged.CatalogError);
        Assert.False(staged.StageRetained);
        NativeTransferImportStage stage = Assert.IsType<NativeTransferImportStage>(staged.Stage);
        Assert.True(stage.Package.IsChanV4);
        Assert.Null(stage.Package.PrivateMetadata);
        return stage;
    }

    private static string[] Inventory(string root)
        => Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(root, path) + (Directory.Exists(path)
                ? "/" : ":" + new FileInfo(path).Length.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .ToArray();

    private static TransferOriginalByteVersion Version(byte[] bytes, bool sha1Known)
        => new(bytes.LongLength, Hash(bytes), Md5(bytes), sha1Known ? Sha1(bytes) : null);

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Md5(byte[] bytes) => Convert.ToBase64String(MD5.HashData(bytes));
    private static string Sha1(byte[] bytes) => Convert.ToBase64String(SHA1.HashData(bytes));

    private sealed class Workspace : IDisposable
    {
        internal string Root { get; } = TemporaryWorkspacePaths.CreateDirectory("meowchan-v4-plan-tests-");
        internal string Staging { get; }
        internal Workspace() => Staging = Directory.CreateDirectory(Path.Combine(Root, "staging")).FullName;
        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
