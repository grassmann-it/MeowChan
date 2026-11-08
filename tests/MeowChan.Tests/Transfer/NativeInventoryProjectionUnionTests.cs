using System.Collections.Immutable;
using System.Security.Cryptography;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Storage;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

// Pure descriptor union tests. Paths are labels only: no files, stage, SQL or profile.
// In-memory snapshots are NOT evidence of manifest admission or byte verification.
public sealed class NativeInventoryProjectionUnionTests
{
    private static readonly ResourceId Resource = new("4chan", "b", "404");
    private static readonly DateTimeOffset At = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly MediaActualIntegrity Actual = Measure([1, 2, 3]);
    private static readonly MediaProviderMetadata Provider = new("111", "original",
        "https://i.4cdn.org/b/111.webm", "original.webm", Actual.Length, Actual.Md5, Actual.Sha256);
    private static readonly string Key = MediaRevisionKeys.CreateAttachmentKey(Resource, "404", 0, Provider);
    private static readonly CapturedAttachment Metadata = new(0, Provider.MediaId, Provider.Variant,
        Provider.Url, Provider.OriginalFileName, ExpectedSize: Provider.ExpectedSize, ExpectedMd5: Provider.ExpectedMd5)
        { ExpectedSha256 = Provider.ExpectedSha256, AttachmentRevisionKey = Key };

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void EqualEvidenceWithDifferentLocalOriginsCoalescesButKeepsBothPhysicalDerivedOrigins(bool reverse)
    {
        var firstEvidence = Evidence(1);
        var secondEvidence = Evidence(2);
        Assert.NotEqual(firstEvidence.Origin, secondEvidence.Origin);
        var firstDerived = Derived(firstEvidence);
        var secondDerived = Derived(secondEvidence);
        var first = Snapshot("root-a", firstEvidence, firstDerived);
        var second = Snapshot("root-b", secondEvidence, secondDerived);
        var plan = Create(Thread(), reverse ? [second, first] : [first, second]);

        var member = Assert.Single(plan.Members);
        Assert.False(member.Retained);
        Assert.Empty(member.Originals);
        Assert.Equal(Metadata, member.Attachment.Metadata);
        Assert.Equal(Version(Actual), member.Attachment.OriginalByteVersion);
        Assert.Equal(TransferContentStatus.Unavailable, member.Attachment.Status);
        Assert.Null(member.Attachment.PayloadSha256);
        Assert.Null(member.Attachment.History);
        var descriptor = Assert.Single(member.Attachment.DerivedRepresentations);
        Assert.Equal(Key, descriptor.SourceRevisionKey);
        Assert.Equal(Version(Actual), descriptor.SourceByteVersion);
        Assert.Equal(TransferContentStatus.Unavailable, descriptor.Status);
        Assert.Null(descriptor.PayloadSha256);
        var origins = member.Derived[TransferDerivedRepresentationKind.Preview];
        Assert.Equal(2, origins.Length);
        Assert.Contains(origins, o => ReferenceEquals(o.Manifest, first) && o.Derived == firstDerived && o.Original == null);
        Assert.Contains(origins, o => ReferenceEquals(o.Manifest, second) && o.Derived == secondDerived && o.Original == null);
        Assert.Single(Assert.Single(plan.Thread.Posts).Attachments);
        Assert.Empty(plan.Thread.RetainedBindings);
        Assert.Equal(firstEvidence, Assert.Single(first.OriginalEvidence));
        Assert.Equal(secondEvidence, Assert.Single(second.OriginalEvidence));
    }

    [Theory]
    [InlineData("null-sha1", false)] [InlineData("null-sha1", true)]
    [InlineData("sha1", false)] [InlineData("length", false)]
    [InlineData("md5", false)] [InlineData("sha256", false)]
    public void ConflictingExactSourceVersionsRejectRatherThanUseCompatibleEvidence(string field, bool reverse)
    {
        var first = Evidence(1);
        var other = Measure([4, 5, 6]);
        var changed = field switch
        {
            "null-sha1" => Actual with { Sha1 = null },
            "sha1" => Actual with { Sha1 = other.Sha1 },
            "length" => Actual with { Length = Actual.Length + 1 },
            "md5" => Actual with { Md5 = other.Md5 },
            "sha256" => Actual with { Sha256 = other.Sha256 },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        var second = Evidence(2) with { OriginalByteVersion = changed };
        var a = Snapshot("root-a", first, Derived(first));
        var b = Snapshot("root-b", second, Derived(second));
        Assert.Throws<InvalidDataException>(() => Create(Thread(), reverse ? [b, a] : [a, b]));
    }

    [Fact]
    public void EqualNullableSourceVersionsRemainNullWithoutPromotionFromDerivedBytes()
    {
        var first = Evidence(1) with { OriginalByteVersion = Actual with { Sha1 = null } };
        var second = Evidence(2) with { OriginalByteVersion = first.OriginalByteVersion };
        var member = Assert.Single(Create(Thread(),
            [Snapshot("root-a", first, Derived(first)), Snapshot("root-b", second, Derived(second))]).Members);
        Assert.Null(member.Attachment.OriginalByteVersion!.Sha1);
        Assert.Null(Assert.Single(member.Attachment.DerivedRepresentations).SourceByteVersion.Sha1);
        Assert.All(member.Derived[TransferDerivedRepresentationKind.Preview],
            origin => Assert.NotNull(origin.Derived!.Blob!.Actual.Sha1));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SameParentAndKindWithDifferentDerivedActualRejectsInEitherOrder(bool reverse)
    {
        var first = Evidence(1);
        var second = Evidence(2);
        var a = Snapshot("root-a", first, Derived(first));
        var b = Snapshot("root-b", second, Derived(second, Measure([7, 8, 9])));
        Assert.Throws<InvalidDataException>(() => Create(Thread(), reverse ? [b, a] : [a, b]));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CurrentSqlDeletionWinsOverPassiveEvidenceWithoutRewritingPost(bool deleted)
    {
        var evidence = Evidence(1) with { Metadata = Metadata with { IsDeleted = !deleted } };
        var current = Metadata with { IsDeleted = deleted };
        var input = Thread(current, deleted);
        var plan = Create(input, [Snapshot("root-a", evidence, Derived(evidence))]);
        var post = Assert.Single(plan.Thread.Posts);
        Assert.Same(Assert.Single(input.Posts).Metadata, post.Metadata);
        Assert.Equal(deleted, post.Metadata.IsDeleted);
        Assert.Equal(current, Assert.Single(post.Attachments).Metadata);
        Assert.Equal(current, Assert.Single(plan.Members).Attachment.Metadata);
        Assert.Equal(!deleted, evidence.Metadata.IsDeleted);
        Assert.Empty(plan.Thread.RetainedBindings);
    }

    [Fact]
    public void KeyedHistoricalEvidenceBecomesRetainedWithoutInventingMissingPost()
    {
        var evidence = Evidence(1) with { IncomingStatus = MediaObservedContentStatus.Omitted };
        var input = Thread() with { Posts = [] };
        var plan = Create(input, [Snapshot("root-a", evidence, Derived(evidence))]);
        Assert.Empty(plan.Thread.Posts);
        Assert.Empty(input.Posts);
        var retained = Assert.Single(plan.Thread.RetainedBindings);
        Assert.Equal("404", retained.PostId);
        Assert.Equal(Metadata, retained.Attachment.Metadata);
        Assert.Equal(TransferContentStatus.Omitted, retained.Attachment.Status);
        Assert.Equal(Version(Actual), retained.Attachment.OriginalByteVersion);
        Assert.Single(retained.Attachment.DerivedRepresentations);
        var member = Assert.Single(plan.Members);
        Assert.True(member.Retained);
        Assert.Equal(retained.Attachment, member.Attachment);
        Assert.Empty(member.Originals);
        Assert.Single(member.Derived[TransferDerivedRepresentationKind.Preview]);
    }

    [Fact]
    public void UnkeyedHistoricalEvidenceRejectsInsteadOfInventingRevisionOrPost()
    {
        var evidence = Evidence(1) with
        {
            Occurrence = new(Resource, "404", 0, null),
            Metadata = Metadata with { AttachmentRevisionKey = null },
            OriginalByteVersion = null,
            IncomingStatus = MediaObservedContentStatus.Omitted
        };
        Assert.Throws<InvalidDataException>(() => Create(Thread() with { Posts = [] },
            [Snapshot("root-a", evidence)]));
    }

    [Fact]
    public void DerivedOnlySameShaNeverBecomesAnOriginalResolution()
    {
        var evidence = Evidence(1); // incoming Contained is intentionally not local Original availability
        var derived = Derived(evidence, Actual);
        var snapshot = Snapshot("derived-only", evidence, derived);
        Assert.Equal(Actual.Sha256, derived.Blob!.Actual.Sha256);
        Assert.Null(MediaAttachmentResolver.Resolve(snapshot, Resource, "404", Metadata));
        var member = Assert.Single(Create(Thread(), [snapshot]).Members);
        Assert.Empty(member.Originals);
        Assert.Equal(TransferContentStatus.Unavailable, member.Attachment.Status);
        Assert.Null(member.Attachment.PayloadSha256);
        Assert.Null(member.Attachment.History);
        var origin = Assert.Single(member.Derived[TransferDerivedRepresentationKind.Preview]);
        Assert.Same(snapshot, origin.Manifest);
        Assert.Equal(derived, origin.Derived);
        Assert.Null(origin.Original);
        Assert.Equal(0, snapshot.Count);
        Assert.Empty(snapshot.AttachmentBindings);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ExactBindingsResolvePerSnapshotDespiteDifferentAssetIdentities(bool reverse)
    {
        var a = BoundSnapshot("root-a", 1);
        var b = BoundSnapshot("root-b", 2);
        Assert.NotEqual(Assert.Single(a.GetPage()).AssetId, Assert.Single(b.GetPage()).AssetId);
        Assert.NotNull(MediaAttachmentResolver.Resolve(a, Resource, "404", Metadata));
        Assert.NotNull(MediaAttachmentResolver.Resolve(b, Resource, "404", Metadata));
        // The old aggregate resolver is deliberately stricter than the physical-origin union.
        Assert.Null(MediaAttachmentResolver.Resolve(new[] { a, b }, Resource, "404", Metadata));
        var member = Assert.Single(Create(Thread(), reverse ? [b, a] : [a, b]).Members);
        Assert.Equal(2, member.Originals.Length);
        Assert.Contains(member.Originals, o => ReferenceEquals(o.Manifest, a) && o.Original == Assert.Single(a.GetPage()));
        Assert.Contains(member.Originals, o => ReferenceEquals(o.Manifest, b) && o.Original == Assert.Single(b.GetPage()));
        Assert.All(member.Originals, o => Assert.Null(o.Derived));
        Assert.Equal(TransferContentStatus.Unavailable, member.Attachment.Status);
        Assert.Null(member.Attachment.PayloadSha256); // descriptor plan is not verified output
    }

    [Fact]
    public void DuplicateExactBindingAcrossRootsKeepsEveryPhysicalOriginButOnePortableMember()
    {
        var a = BoundSnapshot("root-a", 1);
        var b = BoundSnapshot("root-b", 1);
        Assert.Equal(Assert.Single(a.AttachmentBindings).Value, Assert.Single(b.AttachmentBindings).Value);
        var plan = Create(Thread(), [a, b]);
        var member = Assert.Single(plan.Members);
        Assert.Single(Assert.Single(plan.Thread.Posts).Attachments);
        Assert.Equal(2, member.Originals.Length);
        Assert.Same(a, member.Originals[0].Manifest);
        Assert.Same(b, member.Originals[1].Manifest);
        Assert.NotEqual(member.Originals[0].Original!.Path, member.Originals[1].Original!.Path);
    }

    [Fact]
    public void SameDerivedShaWithConflictingSecondaryDigestStillRejects()
    {
        var first = Evidence(1);
        var second = Evidence(2);
        var left = Derived(first);
        var right = Derived(second, left.Blob!.Actual with { Sha1 = Measure([9]).Sha1 });
        Assert.Equal(left.Blob.Actual.Sha256, right.Blob!.Actual.Sha256);
        Assert.Throws<InvalidDataException>(() => Create(Thread(),
            [Snapshot("root-a", first, left), Snapshot("root-b", second, right)]));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void BrokenExactBindingInOneSnapshotCannotBorrowAnotherSnapshotsValidAsset(bool reverse)
    {
        var good = BoundSnapshot("root-a", 1);
        var bound = BoundSnapshot("root-b", 2);
        // Deliberately inconsistent snapshot isolates the fail-closed per-origin binding guard.
        var broken = new MediaAssetSnapshot(bound.Directory, MediaManifestStatus.Available, 6, bound.Generation, [],
            attachmentBindings: bound.AttachmentBindings, originalEvidence: bound.OriginalEvidence);
        Assert.Null(MediaAttachmentResolver.Resolve(broken, Resource, "404", Metadata));
        Assert.Throws<InvalidDataException>(() => Create(Thread(), reverse ? [broken, good] : [good, broken]));
    }

    [Fact]
    public void DuplicateCurrentOccurrenceRejectsInsteadOfDeduplicatingSqlAttachments()
    {
        var thread = Thread();
        var post = Assert.Single(thread.Posts);
        var attachment = Assert.Single(post.Attachments);
        thread = thread with { Posts = [post with { Attachments = [attachment, attachment] }] };
        Assert.Throws<InvalidDataException>(() => Create(thread, [BoundSnapshot("root-a", 1)]));
    }

    [Fact]
    public void PreCancelledPureProjectionThrowsWithOriginalToken()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = Assert.Throws<OperationCanceledException>(() => NativeTransferMediaInventoryProjection.Create(
            Thread(), [], cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    private static NativeTransferMediaInventoryProjection.Plan Create(TransferThread thread,
        ImmutableArray<MediaAssetSnapshot> snapshots) => NativeTransferMediaInventoryProjection.Create(thread, snapshots, default);

    private static TransferThread Thread(CapturedAttachment? metadata = null, bool deleted = false)
    {
        metadata ??= Metadata;
        var post = new CapturedPost("404", 17, PostFieldPresence.All, At, "subject", "author",
            "<p>synthetic</p>", deleted, [metadata]);
        return new(Resource, "https://boards.4chan.org/b/thread/404", "synthetic union", ThreadSourceState.NotFound,
            At, At.AddMinutes(1), 12, true,
            [new(post, [new(metadata, TransferMediaRole.Original, TransferContentStatus.Unavailable, null, null)])]);
    }

    private static MediaOriginalEvidence Evidence(int origin) => new(new(Resource, "404", 0, Key), Metadata,
        MediaObservedContentStatus.Contained, Actual,
        new(origin == 1 ? "32b432b4-aaaa-4aaa-baaa-000000000001" : "32b432b4-aaaa-4aaa-baaa-000000000002",
            Measure([(byte)origin]).Sha256, At.AddMinutes(origin)));

    private static MediaDerivedRepresentation Derived(MediaOriginalEvidence evidence, MediaActualIntegrity? actual = null)
    {
        actual ??= Measure([10, 11, 12]);
        return new(evidence.Occurrence, evidence.OriginalByteVersion!, MediaDerivedKind.Preview,
            MediaDerivedStatus.Contained, new(actual.Sha256 + ".derived.bin", actual), evidence.Origin);
    }

    private static MediaAssetSnapshot Snapshot(string directory, MediaOriginalEvidence evidence,
        MediaDerivedRepresentation? derived = null) => new(directory, MediaManifestStatus.Available, 6,
            Measure(System.Text.Encoding.UTF8.GetBytes(directory)).Sha256, [], originalEvidence: [evidence],
            derivedRepresentations: derived == null ? [] : [derived]);

    private static MediaAssetSnapshot BoundSnapshot(string directory, int identity)
    {
        string assetId = Measure([(byte)(identity + 20)]).Sha256;
        var asset = new MediaAssetReference(assetId, Provider.MediaId, "original", "original.bin",
            Path.Combine(directory, "original.bin"), Actual.Length, Actual.Sha256, Provider.Url, Provider.OriginalFileName, true)
            { Md5 = Actual.Md5, Sha1 = Actual.Sha1 };
        var binding = new MediaAttachmentBinding(new(Resource, "404", 0, Key),
            MediaRevisionKeys.CreateAcquisitionKey(Resource, Provider), assetId, Provider, Actual,
            new(MediaAcceptanceKind.Strict, false, false, At));
        MediaIntegrityValidation.ValidateBinding(Key, binding);
        return new(directory, MediaManifestStatus.Available, 6, Measure([(byte)identity]).Sha256, [asset],
            attachmentBindings: new Dictionary<string, MediaAttachmentBinding> { [Key] = binding },
            originalEvidence: [Evidence(identity)]);
    }

    private static TransferOriginalByteVersion Version(MediaActualIntegrity actual)
        => new(actual.Length, actual.Sha256, actual.Md5, actual.Sha1);

    private static MediaActualIntegrity Measure(byte[] bytes) => new(bytes.Length,
        Convert.ToBase64String(MD5.HashData(bytes)), Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant())
        { Sha1 = Convert.ToBase64String(SHA1.HashData(bytes)) };
}
