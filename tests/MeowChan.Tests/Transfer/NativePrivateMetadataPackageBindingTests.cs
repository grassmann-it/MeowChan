using System.Collections.Immutable;
using MeowChan.Archive;
using MeowChan.Favorites;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

// Binding-only tests: these are passive catalog/projection objects, not packages or store fixtures.
public sealed class NativePrivateMetadataPackageBindingTests
{
    private static readonly ResourceId Thread = new("synthetic-source", "synthetic-board", "100");
    private static readonly ResourceId Other = Thread with { Thread = "200" };
    private static readonly NativePrivateMetadataSelection All = new(true, true, true);
    private const string CurrentHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string RetainedHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Secret = "SYNTHETIC-PRIVATE-SENTINEL";
    private static readonly DateTimeOffset Date = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    private static NativePrivateMetadataProjection Projection(
        NativePrivateMetadataSelection? selection = null,
        ImmutableArray<ResourceId>? threads = null,
        ImmutableArray<ResourceId>? threadFavorites = null,
        ImmutableArray<FavoriteAssetKey>? media = null,
        ImmutableArray<PostBookmark>? bookmarks = null)
    {
        NativePrivateMetadataSelection chosen = selection ?? All;
        return new(threads ?? [Thread], chosen,
            threadFavorites ?? (chosen.ThreadFavorites ? [Thread] : []),
            media ?? (chosen.MediaFavorites ? [new(Thread, "current", "original", CurrentHash)] : []),
            bookmarks ?? (chosen.PostBookmarksAndNotes ? [new(new(Thread, "post-1"), Secret)] : []));
    }

    private static TransferAttachment Attachment(string media = "current", string hash = CurrentHash,
        string variant = "original") => new(new CapturedAttachment(0, media, variant),
            TransferMediaRole.Original, TransferContentStatus.Contained, hash, null);

    private static TransferThread PublicThread(ResourceId id, ImmutableArray<TransferAttachment> attachments,
        ImmutableArray<TransferRetainedBinding> retained = default)
    {
        var post = new TransferPost(new CapturedPost("post-1", 1, PostFieldPresence.All,
            Attachments: attachments.Select(a => a.Metadata).ToArray()), attachments);
        return new(id, null, "synthetic", ThreadSourceState.NotFound, Date, Date, 1, true, [post])
        { RetainedBindings = retained.IsDefault ? [] : retained };
    }

    private static NativeTransferCatalog Catalog() => new(Date, [PublicThread(Thread, [Attachment()])]);
    private static NativeTransferCatalog WithAttachment(TransferAttachment attachment) =>
        new(Date, [PublicThread(Thread, [attachment])]);
    private static NativeTransferCatalog RetainedCatalog(TransferAttachment? attachment = null) =>
        new(Date, [PublicThread(Thread, [], [new("old-post", attachment ?? Attachment("retained", RetainedHash))])]);

    private static void Reject(NativeTransferCatalog catalog, NativePrivateMetadataProjection projection)
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            NativePrivateMetadataCatalogBinding.Validate(catalog, projection));
        Assert.Equal("The private metadata does not bind to the public catalog.", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(Secret, error.ToString());
        Assert.DoesNotContain(CurrentHash, error.ToString());
        Assert.DoesNotContain(RetainedHash, error.ToString());
    }

    [Fact]
    public void BindsCurrentContainedAttachmentAndCapturedPostWithoutMutation()
    {
        NativeTransferCatalog catalog = Catalog();
        NativePrivateMetadataProjection projection = Projection();
        byte[] beforePublic = NativeTransferCatalogSerializer.Serialize(catalog);
        byte[] beforePrivate = NativePrivateMetadataCodec.Serialize(projection);
        NativePrivateMetadataCatalogBinding.Validate(catalog, projection);
        Assert.Equal(beforePublic, NativeTransferCatalogSerializer.Serialize(catalog));
        Assert.Equal(beforePrivate, NativePrivateMetadataCodec.Serialize(projection));
        Assert.DoesNotContain(Secret, System.Text.Encoding.UTF8.GetString(beforePublic));
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void EverySelectedEmptyCombinationIsValid(int bits)
    {
        NativePrivateMetadataSelection selection = new((bits & 1) != 0, (bits & 2) != 0, (bits & 4) != 0);
        NativePrivateMetadataCatalogBinding.Validate(Catalog(),
            Projection(selection, threadFavorites: [], media: [], bookmarks: []));
        NativePrivateMetadataCatalogBinding.Validate(new(Date, []), new([], selection, [], [], []));
    }

    [Fact]
    public void DefaultOffIsCodecValidButNotAPrivatePackageComponent()
    {
        NativePrivateMetadataProjection projection = Projection(default(NativePrivateMetadataSelection));
        Assert.True(NativePrivateMetadataCodec.Inspect(NativePrivateMetadataCodec.Serialize(projection)).IsValid);
        Reject(Catalog(), projection);
        Reject(new(Date, []), new([], default, [], [], []));
    }

    [Fact]
    public void ThreadSetOrderIsIrrelevantAndNoFavoriteForEveryThreadIsRequired()
    {
        NativeTransferCatalog catalog = new(Date,
            [PublicThread(Thread, [Attachment()]), PublicThread(Other, [])]);
        NativePrivateMetadataCatalogBinding.Validate(catalog, Projection(threads: [Other, Thread]));
        Reject(catalog, Projection()); // missing public thread
        Reject(Catalog(), Projection(threads: [Other, Thread])); // extra private thread
        Reject(Catalog(), Projection(threads: [Other], threadFavorites: [], media: [], bookmarks: []));
    }

    [Theory]
    [InlineData("source")] [InlineData("board")] [InlineData("thread")]
    [InlineData("leading-zero")] [InlineData("unicode")]
    public void ThreadScopeNeverNormalizesIdentities(string field)
    {
        ResourceId publicId = Thread with { Board = "caf\u00e9" };
        ResourceId privateId = field switch
        {
            "source" => publicId with { Source = "SYNTHETIC-SOURCE" },
            "board" => publicId with { Board = "CAF\u00c9" },
            "thread" => publicId with { Thread = "200" },
            "leading-zero" => publicId with { Thread = "0100" },
            _ => publicId with { Board = "cafe\u0301" }
        };
        Reject(new(Date, [PublicThread(publicId, [])]), Projection(
            threads: [privateId], threadFavorites: [], media: [], bookmarks: []));
    }

    [Fact]
    public void SelectedThreadFavoriteMustBelongToPublicThreads()
    {
        Reject(Catalog(), Projection(threadFavorites: [Other], media: [], bookmarks: []));
    }

    [Theory]
    [InlineData("missing")] [InlineData("POST-1")] [InlineData("01")]
    public void BookmarkRequiresExactCapturedPost(string post)
    {
        Reject(Catalog(), Projection(media: [], bookmarks: [new(new(Thread, post), Secret)]));
    }

    [Fact]
    public void PostInAnotherThreadCannotSatisfyBookmark()
    {
        NativeTransferCatalog catalog = new(Date,
            [PublicThread(Thread, []) with { Posts = [] }, PublicThread(Other, [])]);
        Reject(catalog, Projection(threads: [Thread, Other], media: []));
    }

    [Fact]
    public void RetainedMediaDoesNotInventACapturedPost()
    {
        NativePrivateMetadataProjection projection = Projection(
            media: [new(Thread, "retained", "original", RetainedHash)], bookmarks: []);
        NativePrivateMetadataCatalogBinding.Validate(RetainedCatalog(), projection);
        Reject(RetainedCatalog(), Projection(media: [], bookmarks: [new(new(Thread, "old-post"), Secret)]));
        NativeTransferCatalog retainedOnly = RetainedCatalog() with
        { Threads = [RetainedCatalog().Threads[0] with { Posts = [] }] };
        NativePrivateMetadataCatalogBinding.Validate(retainedOnly, projection);
    }

    [Theory]
    [InlineData("media")] [InlineData("variant")] [InlineData("hash")]
    [InlineData("media-case")] [InlineData("variant-case")] [InlineData("unicode")]
    public void MediaRequiresFullExactTupleForCurrentAndRetained(string mismatch)
    {
        FavoriteAssetKey favorite = new(Thread, "current", "original", CurrentHash);
        favorite = mismatch switch
        {
            "media" => favorite with { MediaIdentifier = "different" },
            "variant" => favorite with { Variant = "preview" },
            "hash" => favorite with { Sha256 = RetainedHash },
            "media-case" => favorite with { MediaIdentifier = "CURRENT" },
            "variant-case" => favorite with { Variant = "Original" },
            _ => favorite with { MediaIdentifier = "cafe\u0301" }
        };
        TransferAttachment attachment = mismatch == "unicode" ? Attachment("caf\u00e9") : Attachment();
        NativePrivateMetadataProjection projection = Projection(media: [favorite], bookmarks: []);
        Reject(WithAttachment(attachment), projection);
        Reject(RetainedCatalog(attachment), projection);
    }

    [Fact]
    public void EqualBytesInAnotherThreadDoNotSatisfyFavorite()
    {
        NativeTransferCatalog catalog = new(Date,
            [PublicThread(Thread, []), PublicThread(Other, [Attachment()])]);
        Reject(catalog, Projection(threads: [Thread, Other], bookmarks: []));
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void NonContainedAttachmentsNeverSatisfyFavoriteEvenWithMatchingRemoteDigest(int status)
    {
        TransferAttachment attachment = Attachment() with
        {
            Status = (TransferContentStatus)status, PayloadSha256 = null,
            Metadata = Attachment().Metadata with { ExpectedSha256 = CurrentHash }
        };
        NativePrivateMetadataProjection projection = Projection(bookmarks: []);
        Reject(WithAttachment(attachment), projection);
        Reject(RetainedCatalog(attachment), projection);
    }

    [Fact]
    public void ExpectedRemoteHashAndUrlCannotSubstituteForPayloadHashOrMediaIdentity()
    {
        TransferAttachment attachment = Attachment() with
        {
            PayloadSha256 = RetainedHash,
            Metadata = Attachment().Metadata with
            { ExpectedSha256 = CurrentHash, Url = "https://example.invalid/current" }
        };
        Reject(WithAttachment(attachment), Projection(bookmarks: []));
        attachment = attachment with { PayloadSha256 = CurrentHash,
            Metadata = attachment.Metadata with { MediaId = "another" } };
        Reject(WithAttachment(attachment), Projection(bookmarks: []));
    }

    [Fact]
    public void MissingMediaIsNotInferredAndRetainedCannotHideUnmatchedRequests()
    {
        Reject(new(Date, [PublicThread(Thread, [])]), Projection(bookmarks: []));
        Reject(RetainedCatalog(), Projection(media:
            [new(Thread, "retained", "original", RetainedHash), new(Thread, "missing", "original", CurrentHash)],
            bookmarks: []));
    }

    [Fact]
    public void CurrentAndRetainedDuplicatesCanSatisfySameAssetWithoutAmbiguityOrRekeying()
    {
        NativeTransferCatalog catalog = new(Date,
            [PublicThread(Thread, [Attachment(), Attachment() with { Metadata = Attachment().Metadata with { Position = 1 } }],
                [new("old-post", Attachment())])]);
        NativePrivateMetadataCatalogBinding.Validate(catalog, Projection());
    }

    [Fact]
    public void OneValidThreadDoesNotHideUnmatchedBookmarksOrMediaInAnotherThread()
    {
        NativeTransferCatalog catalog = new(Date,
            [PublicThread(Thread, [Attachment()]), PublicThread(Other, []) with { Posts = [] }]);
        Reject(catalog, Projection(threads: [Thread, Other], media: [],
            bookmarks: [new(new(Thread, "post-1"), ""), new(new(Other, "post-1"), Secret)]));
        Reject(catalog, Projection(threads: [Thread, Other], bookmarks: [],
            media: [new(Thread, "current", "original", CurrentHash), new(Other, "current", "original", CurrentHash)]));
    }

    [Fact]
    public void MalformedPrivateProjectionHasOnlySanitizedBindingDiagnostic()
    {
        Reject(Catalog(), Projection(media: [new(Thread, Secret, "original", "not-a-hash")], bookmarks: []));
        Reject(Catalog(), Projection(new NativePrivateMetadataSelection(ThreadFavorites: true),
            media: [new(Thread, Secret, "original", CurrentHash)], bookmarks: []));
        Reject(Catalog(), Projection(threads: [Thread, Thread]));
        Reject(Catalog(), null!);
        Reject(null!, Projection());
    }

    [Fact]
    public void CancellationIsPreservedAndDoesNotBecomeADataDiagnostic()
    {
        using CancellationTokenSource source = new();
        source.Cancel();
        OperationCanceledException error = Assert.Throws<OperationCanceledException>(() =>
            NativePrivateMetadataCatalogBinding.Validate(Catalog(), Projection(), source.Token));
        Assert.Equal(source.Token, error.CancellationToken);
    }
}
