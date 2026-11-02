using System.Collections.Immutable;
using MeowChan.Archive;
using MeowChan.Favorites;
using MeowChan.Persistence;
using MeowChan.Interfaces;
using MeowChan.Storage;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

public sealed class NativePrivateMetadataComparisonNegativeTests
{
    private static readonly ResourceId Thread = new("synthetic", "board", "100");
    private static readonly NativePrivateMetadataSelection All = new(true, true, true);
    private const string Secret = "PRIVATE-SYNTHETIC-NOTE";
    private static readonly DateTimeOffset Date = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private static NativePrivateMetadataProjection Projection(NativePrivateMetadataSelection? selection = null,
        ImmutableArray<ResourceId>? scope = null, ImmutableArray<ResourceId>? favorites = null,
        ImmutableArray<FavoriteAssetKey>? media = null, ImmutableArray<PostBookmark>? notes = null) =>
        new(scope ?? [Thread], selection ?? All, favorites ?? [], media ?? [], notes ?? []);

    private static TransferThread PublicThread(ResourceId? id = null) =>
        new(id ?? Thread, null, "synthetic", ThreadSourceState.NotFound, Date, Date, 1, true, []);
    private static NativeTransferCatalog Catalog() => new(Date, [PublicThread()]);

    private static void Reject(NativePrivateMetadataProjection incoming,
        NativeTransferCatalog? catalog = null, NativePrivateMetadataProjection? target = null,
        NativePrivateMetadataSelection? selection = null)
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            NativePrivateMetadataComparison.Analyze(incoming, catalog ?? Catalog(), target ?? Projection(), selection ?? All));
        Assert.Equal("The private metadata comparison input is invalid.", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(Secret, error.ToString());
        Assert.DoesNotContain(Thread.Source, error.ToString());
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void EachRequestedCategoryMustHaveBeenOfferedBySender(int category)
    {
        NativePrivateMetadataSelection offered = new(category != 0, category != 1, category != 2);
        Reject(Projection(offered));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void EachRequestedCategoryMustHaveATargetReadSnapshot(int category)
    {
        NativePrivateMetadataSelection offered = new(category != 0, category != 1, category != 2);
        Reject(Projection(), target: Projection(offered));
    }

    [Fact]
    public void TargetPrivateScopeMustBeExactlyTheIncomingScope()
    {
        Reject(Projection(), target: Projection(scope: []));
        Reject(Projection(), target: Projection(scope: [Thread, Thread with { Thread = "200" }]));
        Reject(Projection(), target: Projection(scope: [Thread with { Board = "BOARD" }]));
    }

    [Fact]
    public void PublicTargetMayBeMissingButCannotContainOutOfScopeOrDuplicateThreads()
    {
        NativePrivateMetadataComparisonResult missing = NativePrivateMetadataComparison.Analyze(
            Projection(favorites: [Thread]), new(Date, []), Projection(), All);
        Assert.Equal(NativePrivateMetadataDisposition.MissingTarget, Assert.Single(missing.ThreadFavorites).Disposition);
        Reject(Projection(), new(Date, [PublicThread(Thread with { Thread = "200" })]));
        Reject(Projection(), new(Date, [PublicThread(), PublicThread()]));
    }

    [Theory]
    [InlineData("incoming")] [InlineData("target")]
    public void InvalidPrivateValuesFailWithoutEchoingAnyPrivateText(string side)
    {
        NativePrivateMetadataProjection bad = Projection(notes: [new(new(Thread, "post"), Secret + '\0')]);
        if (side == "incoming") Reject(bad);
        else Reject(Projection(), target: bad);
    }

    [Fact]
    public void UnselectedValuesAndDefaultCollectionsCannotBeSmuggledThrough()
    {
        Reject(Projection(default(NativePrivateMetadataSelection), notes: [new(new(Thread, "post"), Secret)]), selection: default(NativePrivateMetadataSelection));
        Reject(new(default, All, [], [], []));
        Reject(new([Thread], All, default, [], []));
        Reject(new([Thread], All, [], default, []));
        Reject(new([Thread], All, [], [], default));
    }

    [Fact]
    public void DuplicatePrivateIdentitiesAreRejectedInsteadOfDictionaryOverwrite()
    {
        Reject(Projection(favorites: [Thread, Thread]));
        Reject(Projection(scope: [Thread, Thread]));
        Reject(Projection(notes: [new(new(Thread, "1"), ""), new(new(Thread, "1"), Secret)]));
        FavoriteAssetKey key = new(Thread, "media", "original", new string('a', 64));
        Reject(Projection(media: [key, key]));
    }

    [Theory]
    [InlineData("\r")] [InlineData("\r\n")]
    public void NoncanonicalNotesAreNotSilentlyNormalizedDuringComparison(string newline)
    {
        Reject(Projection(notes: [new(new(Thread, "1"), Secret + newline)]));
    }

    [Fact]
    public void PublicPostIdentityAndSemanticValidationAreNotBypassed()
    {
        var post = new TransferPost(new CapturedPost("p", 1, PostFieldPresence.All, Attachments: []), []);
        Reject(Projection(), new(Date, [PublicThread() with { Posts = [post, post] }]));
        Reject(Projection(), new(Date, [PublicThread() with { PageUrl = "https://user:secret@invalid.example/thread/100" }]));
        Reject(Projection(), new(Date, [PublicThread() with
        {
            Posts = [post with { Metadata = post.Metadata with { Html = "<script>" + Secret + "</script>" } }]
        }]));
    }

    [Fact]
    public void MaximumThreadScopeAndOneOverUseExistingCodecBound()
    {
        ImmutableArray<ResourceId> scope = Enumerable.Range(0, NativePrivateMetadataCodec.MaximumThreads)
            .Select(i => Thread with { Thread = i.ToString(System.Globalization.CultureInfo.InvariantCulture) }).ToImmutableArray();
        NativePrivateMetadataProjection accepted = Projection(scope: scope);
        NativePrivateMetadataComparisonResult result = NativePrivateMetadataComparison.Analyze(accepted, new(Date, []), accepted);
        Assert.Equal(scope, result.SelectedIncoming.Threads);
        Reject(Projection(scope: scope.Add(Thread with { Thread = "limit-plus-one" })));
    }

    [Fact]
    public void NoteScalarBoundaryUsesCodecRatherThanUtf16Clipping()
    {
        string atLimit = string.Concat(Enumerable.Repeat("😺", PostBookmarkStore.MaximumNoteCodepoints));
        var note = new PostBookmark(new(Thread, "post"), atLimit);
        NativePrivateMetadataProjection incoming = Projection(notes: [note]);
        NativePrivateMetadataComparisonResult result = NativePrivateMetadataComparison.Analyze(incoming, Catalog(), Projection(), All);
        Assert.Equal(atLimit, Assert.Single(result.PostBookmarksAndNotes).IncomingNote);
        Reject(Projection(notes: [note with { Note = atLimit + "!" }]));
    }

    [Fact]
    public void PublicGraphIsBoundedBeforeAnyComparisonIndices()
    {
        NativeTransferCatalog tooMany = new(Date, Enumerable.Repeat(PublicThread(), 1001).ToImmutableArray());
        Reject(Projection(), tooMany);
        Reject(Projection(), new(Date, default));
        Reject(Projection(), new(Date, [PublicThread() with { Posts = default }]));
        Reject(Projection(), new(Date, [PublicThread() with { RetainedBindings = default }]));
    }

    [Fact]
    public void CancellationIsNotReportedAsInvalidInputOrPartialSuccess()
    {
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        OperationCanceledException error = Assert.ThrowsAny<OperationCanceledException>(() =>
            NativePrivateMetadataComparison.Analyze(Projection(), Catalog(), Projection(), All, cancelled.Token));
        Assert.Equal(cancelled.Token, error.CancellationToken);
    }

    public static IEnumerable<object?[]> InvalidContainedDescriptors()
    {
        foreach (bool retained in new[] { false, true })
            foreach (bool selected in new[] { false, true })
                foreach (string? hash in new[] { null, "", "too-short", new string('A', 64), new string('g', 64) })
                    yield return new object?[] { retained, selected, hash };
    }

    private static NativeTransferCatalog ContainedCatalog(bool retained, string? hash)
    {
        CapturedAttachment metadata = new(0, "media", "original", "https://i.4cdn.org/b/1.jpg");
        if (retained)
        {
            MediaProviderMetadata provider = new("media", "original", metadata.Url!, null, null, null, null);
            metadata = metadata with { AttachmentRevisionKey = MediaRevisionKeys.CreateAttachmentKey(Thread, "post", 0, provider) };
        }
        TransferAttachment attachment = new(metadata, TransferMediaRole.Original, TransferContentStatus.Contained, hash, null);
        TransferThread thread = PublicThread();
        if (retained) thread = thread with { RetainedBindings = [new("post", attachment)] };
        else thread = thread with { Posts = [new(new CapturedPost("post", 1, PostFieldPresence.All,
            Attachments: [metadata]), [attachment])] };
        return new(Date, [thread]);
    }

    [Theory]
    [MemberData(nameof(InvalidContainedDescriptors))]
    public void OriginalDescriptorSyntaxIsCheckedBeforeErasureRegardlessOfSelection(bool retained, bool selected, string? hash)
    {
        Reject(Projection(), ContainedCatalog(retained, hash), selection: new(MediaFavorites: selected));
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void ValidContainedDescriptorIsStillAcceptedWithoutFabricatedMeasurements(bool retained, bool selected)
    {
        string hash = new('a', 64);
        FavoriteAssetKey key = new(Thread, "media", "original", hash);
        NativePrivateMetadataComparisonResult result = NativePrivateMetadataComparison.Analyze(
            Projection(media: [key]), ContainedCatalog(retained, hash), Projection(), new(MediaFavorites: selected));
        if (selected) Assert.Equal(NativePrivateMetadataDisposition.Add, Assert.Single(result.MediaFavorites).Disposition);
        else Assert.Empty(result.MediaFavorites);
    }

    [Fact]
    public void ResultDoesNotKeepTargetProjectionOrOldTargetNotes()
    {
        var post = new TransferPost(new CapturedPost("post", 1, PostFieldPresence.All, Attachments: []), []);
        NativePrivateMetadataProjection incoming = Projection(notes: [new(new(Thread, "post"), "incoming")]);
        NativePrivateMetadataProjection target = Projection(notes: [new(new(Thread, "post"), Secret)]);
        NativePrivateMetadataComparisonResult result = NativePrivateMetadataComparison.Analyze(incoming,
            new(Date, [PublicThread() with { Posts = [post] }]), target, All);
        Assert.Equal("incoming", result.PostBookmarksAndNotes[0].IncomingNote);
        Assert.DoesNotContain(Secret, result.ToString());
        Assert.DoesNotContain(Secret, result.PostBookmarksAndNotes[0].ToString());
        Assert.DoesNotContain(typeof(NativePrivateMetadataComparisonResult).GetFields(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
            field => ReferenceEquals(field.GetValue(result), target));
    }
}
