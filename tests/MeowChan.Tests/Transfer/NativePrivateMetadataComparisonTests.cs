using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reflection;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MeowChan.Archive;
using MeowChan.Favorites;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

// Pure comparison tests.  All identities, notes and payloads in this file are synthetic.
public sealed class NativePrivateMetadataComparisonTests
{
    private static readonly DateTimeOffset When = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly ResourceId Thread = new("synthetic-source", "synthetic-board", "100");
    private static readonly ResourceId Other = Thread with { Thread = "200" };
    private const string Post = "p-1";
    private const string Media = "m-1";
    private const string Note = "incoming private note";
    private const string OtherNote = "old target note";
    private static readonly NativePrivateMetadataSelection All = new(true, true, true);
    private static readonly string MediaHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("synthetic-media"))).ToLowerInvariant();

    private static NativePrivateMetadataProjection Projection(
        ResourceId? thread = null, NativePrivateMetadataSelection? selection = null,
        ImmutableArray<ResourceId>? threadFavorites = null,
        ImmutableArray<FavoriteAssetKey>? mediaFavorites = null,
        ImmutableArray<PostBookmark>? bookmarks = null,
        ImmutableArray<FavoriteEntry>? mediaEntries = null)
    {
        thread ??= Thread;
        NativePrivateMetadataSelection chosen = selection ?? All;
        return new([thread], chosen,
            threadFavorites ?? (chosen.ThreadFavorites ? [thread] : []),
            mediaFavorites ?? (chosen.MediaFavorites ? [new(thread, Media, "original", MediaHash)] : []),
            bookmarks ?? (chosen.PostBookmarksAndNotes ? [new(new(thread, Post), Note)] : []),
            mediaEntries);
    }

    private static TransferAttachment Attachment(string id = Media, string? hash = null,
        TransferContentStatus status = TransferContentStatus.Contained, string variant = "original") =>
        new(new CapturedAttachment(0, id, variant, $"https://i.4cdn.org/b/1.jpg", "synthetic.jpg"),
            TransferMediaRole.Original, status, status == TransferContentStatus.Contained ? hash ?? MediaHash : null, null);

    private static NativeTransferCatalog RawCatalog(bool includePost = true, bool includeMedia = true,
        bool deleted = false, bool retained = false, ResourceId? id = null)
    {
        id ??= Thread;
        TransferAttachment[] attachments = includeMedia ? [Attachment()] : [];
        TransferPost[] posts = includePost
            ? [new(new CapturedPost(Post, 1, PostFieldPresence.All, When, "", null, "synthetic", deleted,
                attachments.Select(a => a.Metadata).ToImmutableArray()), attachments.ToImmutableArray())]
            : [];
        TransferThread thread = new(id, null, "synthetic", ThreadSourceState.NotFound,
            When, When, 1, true, posts.ToImmutableArray());
        if (retained)
        {
            string retainedId = "retained";
            string retainedUrl = $"https://i.4cdn.org/b/2.jpg";
            MediaProviderMetadata provider = new(retainedId, "original", retainedUrl, "synthetic.jpg", null, null, null);
            CapturedAttachment metadata = new(0, retainedId, "original", retainedUrl, "synthetic.jpg")
            { AttachmentRevisionKey = MediaRevisionKeys.CreateAttachmentKey(id, Post, 0, provider) };
            TransferAttachment retainedAttachment = new(metadata, TransferMediaRole.Original,
                TransferContentStatus.Contained, MediaHash, null);
            thread = thread with { RetainedBindings = [new(Post, retainedAttachment)] };
        }
        return new(When, [thread]);
    }

    // Exercise the same public descriptor path as package tests, without creating a package.
    private static NativeTransferCatalog ValidCatalog(NativeTransferCatalog catalog)
    {
        // Validate descriptor structure only, as Analyze does; do not invent measured bytes.
        TransferAttachment Descriptor(TransferAttachment a) => a.Status == TransferContentStatus.Contained
            ? a with { Status = TransferContentStatus.Unavailable, PayloadSha256 = null, History = null } : a;
        NativeTransferCatalog descriptors = catalog with
        {
            Threads = catalog.Threads.Select(t => t with
            {
                Posts = t.Posts.Select(p => p with
                { Attachments = p.Attachments.Select(Descriptor).ToImmutableArray() }).ToImmutableArray(),
                RetainedBindings = t.RetainedBindings.Select(r => r with
                { Attachment = Descriptor(r.Attachment) }).ToImmutableArray()
            }).ToImmutableArray()
        };
        byte[] bytes = NativeTransferCatalogSerializer.Serialize(descriptors);
        NativeTransferEnvelope envelope = new("synthetic-comparison", NativeTransferCatalogSerializer.ContentSchema(descriptors),
            "synthetic", new(bytes.Length, Hash(bytes)), []) { CatalogBytes = bytes.ToImmutableArray() };
        TransferCatalogInspection inspected = NativeTransferCatalogValidator.Inspect(bytes, envelope);
        Assert.True(inspected.IsCatalogValid, inspected.Error.ToString());
        _ = NativeTransferCatalogSerializer.Serialize(catalog);
        return catalog;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static NativePrivateMetadataComparisonResult Compare(
        NativePrivateMetadataProjection incoming, NativeTransferCatalog? catalog = null,
        NativePrivateMetadataProjection? target = null, NativePrivateMetadataSelection selection = default) =>
        NativePrivateMetadataComparison.Analyze(incoming, ValidCatalog(catalog ?? RawCatalog()), target ?? Projection(), selection);

    [Fact]
    public void MediaComparisonCarriesCompleteFavoriteEntryWithoutUsingTargetData()
    {
        FavoriteAssetKey key = new(Thread, Media, "original", MediaHash);
        FavoriteEntry entry = new(key, 987654, "incoming-original.jpg");
        NativePrivateMetadataProjection incoming = Projection(mediaFavorites: [key],
            mediaEntries: [entry]);
        NativePrivateMetadataComparisonResult result = Compare(incoming, RawCatalog(),
            Projection(mediaFavorites: [], bookmarks: []), new(MediaFavorites: true));

        NativePrivateMediaFavoriteComparison row = Assert.Single(result.MediaFavorites);
        Assert.Equal(key, row.Target);
        Assert.Equal(entry, row.IncomingEntry);
    }

    [Fact]
    public void DefaultOffIsIndependentAndEmptySelectionsProduceNoRows()
    {
        NativePrivateMetadataProjection incoming = new([Thread], default, [], [], []);
        NativePrivateMetadataComparisonResult result = Compare(incoming, RawCatalog(),
            new([Thread], default, [], [], []));
        Assert.Equal(default, result.Selection);
        Assert.Empty(result.SelectedIncoming.ThreadFavorites);
        Assert.Empty(result.ThreadFavorites);
        Assert.Empty(result.MediaFavorites);
        Assert.Empty(result.PostBookmarksAndNotes);

        NativePrivateMetadataProjection selectedEmpty = new([Thread], All, [], [], []);
        result = Compare(selectedEmpty, RawCatalog(), new([Thread], All, [], [], []), All);
        Assert.Equal(All, result.Selection);
        Assert.Empty(result.SelectedIncoming.ThreadFavorites);
        Assert.Empty(result.ThreadFavorites);
    }

    [Fact]
    public void AllCategoriesProduceAddIdenticalAndConflictRows()
    {
        NativePrivateMetadataProjection incoming = Projection(threadFavorites: [Thread],
            mediaFavorites: [new(Thread, Media, "original", MediaHash)],
            bookmarks: [new(new(Thread, Post), Note)]);
        NativePrivateMetadataProjection target = Projection(threadFavorites: [],
            mediaFavorites: [], bookmarks: [new(new(Thread, Post), OtherNote)]);
        NativePrivateMetadataComparisonResult result = Compare(incoming, RawCatalog(), target, All);
        Assert.Equal(NativePrivateMetadataDisposition.Add, Assert.Single(result.ThreadFavorites).Disposition);
        Assert.Equal(NativePrivateMetadataDisposition.Add, Assert.Single(result.MediaFavorites).Disposition);
        Assert.Equal(NativePrivateMetadataDisposition.Conflict, Assert.Single(result.PostBookmarksAndNotes).Disposition);
        Assert.Equal(Note, result.PostBookmarksAndNotes[0].IncomingNote);

        target = Projection(threadFavorites: [Thread], mediaFavorites: [new(Thread, Media, "original", MediaHash)],
            bookmarks: [new(new(Thread, Post), Note)]);
        result = Compare(incoming, RawCatalog(), target, All);
        Assert.All(result.ThreadFavorites, r => Assert.Equal(NativePrivateMetadataDisposition.Identical, r.Disposition));
        Assert.All(result.MediaFavorites, r => Assert.Equal(NativePrivateMetadataDisposition.Identical, r.Disposition));
        Assert.All(result.PostBookmarksAndNotes, r => Assert.Equal(NativePrivateMetadataDisposition.Identical, r.Disposition));
    }

    [Fact]
    public void AlreadyNormalizedCanonicalNotesCompareEqualAndEmptyConflicts()
    {
        NativePrivateMetadataProjection incoming = Projection(bookmarks: [new(new(Thread, Post), "a\n b")]);
        NativePrivateMetadataProjection target = Projection(bookmarks: [new(new(Thread, Post), PostBookmarkStore.NormalizeNote("a\r\n b"))]);
        NativePrivateMetadataComparisonResult result = Compare(incoming, RawCatalog(), target, new(PostBookmarksAndNotes: true));
        Assert.Equal(NativePrivateMetadataDisposition.Identical, Assert.Single(result.PostBookmarksAndNotes).Disposition);

        result = Compare(Projection(bookmarks: [new(new(Thread, Post), "")]), RawCatalog(),
            Projection(bookmarks: [new(new(Thread, Post), "x")]), new(PostBookmarksAndNotes: true));
        Assert.Equal(NativePrivateMetadataDisposition.Conflict, Assert.Single(result.PostBookmarksAndNotes).Disposition);
    }

    [Fact]
    public void MissingPublicTargetsWinOverOrphanLocalMarksAndDeletedPostsArePresent()
    {
        NativePrivateMetadataComparisonResult result = Compare(Projection(), RawCatalog(includePost: false, includeMedia: false),
            Projection(), All);
        Assert.All(result.ThreadFavorites, r => Assert.Equal(NativePrivateMetadataDisposition.Identical, r.Disposition));
        Assert.Equal(NativePrivateMetadataDisposition.MissingTarget, Assert.Single(result.MediaFavorites).Disposition);
        Assert.Equal(NativePrivateMetadataDisposition.MissingTarget, Assert.Single(result.PostBookmarksAndNotes).Disposition);

        result = Compare(Projection(), RawCatalog(deleted: true), Projection(), All);
        Assert.DoesNotContain(result.PostBookmarksAndNotes, r => r.Disposition == NativePrivateMetadataDisposition.MissingTarget);
    }

    [Fact]
    public void MediaRequiresExactCurrentOrRetainedFullKeyAndContainedPayload()
    {
        NativePrivateMetadataComparisonResult result = Compare(Projection(), RawCatalog(),
            Projection(mediaFavorites: [], bookmarks: []), new(MediaFavorites: true));
        Assert.Equal(NativePrivateMetadataDisposition.Add, Assert.Single(result.MediaFavorites).Disposition);

        foreach (FavoriteAssetKey wrong in new FavoriteAssetKey[] {
            new(Thread, Media, "preview", MediaHash), new(Thread, Media, "original", new string('a', 64)),
            new(Thread, "other", "original", MediaHash), new(Thread, "M-1", "original", MediaHash),
            new(Thread, Media, "Original", MediaHash) })
        {
            result = Compare(Projection(mediaFavorites: [wrong]), RawCatalog(), Projection(mediaFavorites: [], bookmarks: []),
                new(MediaFavorites: true));
            Assert.Equal(NativePrivateMetadataDisposition.MissingTarget, Assert.Single(result.MediaFavorites).Disposition);
        }

        result = Compare(Projection(), RawCatalog(includeMedia: false, retained: true), Projection(mediaFavorites: [], bookmarks: []),
            new(MediaFavorites: true));
        Assert.Equal(NativePrivateMetadataDisposition.MissingTarget, Assert.Single(result.MediaFavorites).Disposition);
    }

    [Fact]
    public void SelectionFiltersIncomingAndDoesNotRetainTargetOnlyRows()
    {
        NativePrivateMetadataProjection incoming = Projection();
        NativePrivateMetadataProjection target = Projection(threadFavorites: [], mediaFavorites: [],
            bookmarks: [new(new(Thread, "target-only"), OtherNote)]);
        NativePrivateMetadataComparisonResult result = Compare(incoming, RawCatalog(), target,
            new(ThreadFavorites: true));
        Assert.True(result.Selection.ThreadFavorites);
        Assert.False(result.Selection.MediaFavorites);
        Assert.False(result.Selection.PostBookmarksAndNotes);
        Assert.Single(result.SelectedIncoming.ThreadFavorites);
        Assert.Empty(result.SelectedIncoming.MediaFavorites);
        Assert.Empty(result.SelectedIncoming.PostBookmarksAndNotes);
        Assert.Empty(result.MediaFavorites);
        Assert.Empty(result.PostBookmarksAndNotes);
    }

    [Fact]
    public void RequestingAnUnofferedCategoryFailsClosed()
    {
        NativePrivateMetadataProjection incoming = new([Thread], new(ThreadFavorites: true), [Thread], [], []);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            NativePrivateMetadataComparison.Analyze(incoming, ValidCatalog(RawCatalog()),
                new([Thread], All, [Thread], [new(Thread, Media, "original", MediaHash)],
                    [new(new(Thread, Post), Note)]), new(MediaFavorites: true)));
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void EveryReceivingCombinationFiltersRowsAndSelectedIncoming(int bits)
    {
        NativePrivateMetadataSelection selection = new((bits & 1) != 0, (bits & 2) != 0, (bits & 4) != 0);
        NativePrivateMetadataProjection incoming = Projection();
        NativePrivateMetadataProjection target = Projection(threadFavorites: [], mediaFavorites: [], bookmarks: []);
        byte[] beforeIncoming = NativePrivateMetadataCodec.Serialize(incoming);
        byte[] beforeTarget = NativePrivateMetadataCodec.Serialize(target);
        NativePrivateMetadataComparisonResult result = Compare(incoming, target: target, selection: selection);
        Assert.Equal(selection, result.Selection);
        Assert.Equal(selection, result.SelectedIncoming.Selection);
        Assert.Equal(selection.ThreadFavorites ? 1 : 0, result.ThreadFavorites.Length);
        Assert.Equal(selection.MediaFavorites ? 1 : 0, result.MediaFavorites.Length);
        Assert.Equal(selection.PostBookmarksAndNotes ? 1 : 0, result.PostBookmarksAndNotes.Length);
        Assert.Equal(selection.ThreadFavorites ? 1 : 0, result.SelectedIncoming.ThreadFavorites.Length);
        Assert.Equal(selection.MediaFavorites ? 1 : 0, result.SelectedIncoming.MediaFavorites.Length);
        Assert.Equal(selection.PostBookmarksAndNotes ? 1 : 0, result.SelectedIncoming.PostBookmarksAndNotes.Length);
        Assert.All(result.ThreadFavorites, r => Assert.Equal(NativePrivateMetadataDisposition.Add, r.Disposition));
        Assert.All(result.MediaFavorites, r => Assert.Equal(NativePrivateMetadataDisposition.Add, r.Disposition));
        Assert.All(result.PostBookmarksAndNotes, r => Assert.Equal(NativePrivateMetadataDisposition.Add, r.Disposition));
        Assert.Equal(beforeIncoming, NativePrivateMetadataCodec.Serialize(incoming));
        Assert.Equal(beforeTarget, NativePrivateMetadataCodec.Serialize(target));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void SelectedEmptyNeverMeansUnmarkOrDeleteTargetOnlyValues(int bits)
    {
        NativePrivateMetadataSelection selection = new((bits & 1) != 0, (bits & 2) != 0, (bits & 4) != 0);
        NativePrivateMetadataProjection empty = new([Thread], selection, [], [], []);
        NativePrivateMetadataProjection target = Projection();
        byte[] before = NativePrivateMetadataCodec.Serialize(target);
        NativePrivateMetadataComparisonResult result = Compare(empty, target: target, selection: selection);
        Assert.Equal(selection, result.Selection);
        Assert.Equal(selection, result.SelectedIncoming.Selection);
        Assert.Empty(result.ThreadFavorites);
        Assert.Empty(result.MediaFavorites);
        Assert.Empty(result.PostBookmarksAndNotes);
        Assert.Equal(before, NativePrivateMetadataCodec.Serialize(target));
    }

    [Theory]
    [InlineData("", "", 1)]
    [InlineData("", "old", 2)]
    [InlineData("incoming", "", 2)]
    [InlineData("a\n b", "a\n b", 1)]
    [InlineData(" a", "a", 2)]
    [InlineData("caf\u00e9", "cafe\u0301", 2)]
    public void NoteComparisonDoesNotTrimOrUnicodeNormalize(string incoming, string target,
        int expected)
    {
        NativePrivateMetadataComparisonResult result = Compare(
            Projection(bookmarks: [new(new(Thread, Post), incoming)]),
            target: Projection(bookmarks: [new(new(Thread, Post), target)]),
            selection: new(PostBookmarksAndNotes: true));
        var row = Assert.Single(result.PostBookmarksAndNotes);
        Assert.Equal((NativePrivateMetadataDisposition)expected, row.Disposition);
        Assert.Equal(new PostBookmarkKey(Thread, Post), row.Target);
        Assert.Equal(incoming, row.IncomingNote);
        Assert.Equal(incoming, Assert.Single(result.SelectedIncoming.PostBookmarksAndNotes).Note);
    }

    [Fact]
    public void MissingThreadOverridesEveryOrphanMark()
    {
        NativePrivateMetadataComparisonResult result = Compare(Projection(), new(When, []), Projection(), All);
        Assert.Equal(Thread, Assert.Single(result.ThreadFavorites).Target);
        Assert.Equal(NativePrivateMetadataDisposition.MissingTarget, result.ThreadFavorites[0].Disposition);
        Assert.Equal(NativePrivateMetadataDisposition.MissingTarget, Assert.Single(result.MediaFavorites).Disposition);
        Assert.Equal(NativePrivateMetadataDisposition.MissingTarget, Assert.Single(result.PostBookmarksAndNotes).Disposition);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RetainedAssetMatchesExactlyWithoutInventingACapturedPost(bool locallyMarked)
    {
        FavoriteAssetKey key = new(Thread, "retained", "original", MediaHash);
        NativePrivateMetadataProjection incoming = Projection(mediaFavorites: [key]);
        NativePrivateMetadataProjection target = Projection(mediaFavorites: locallyMarked ? [key] : [], bookmarks: []);
        NativePrivateMetadataComparisonResult result = Compare(incoming,
            RawCatalog(includePost: false, includeMedia: false, retained: true), target, All);
        Assert.Equal(key, Assert.Single(result.MediaFavorites).Target);
        Assert.Equal(locallyMarked ? NativePrivateMetadataDisposition.Identical : NativePrivateMetadataDisposition.Add,
            result.MediaFavorites[0].Disposition);
        Assert.Equal(NativePrivateMetadataDisposition.MissingTarget, Assert.Single(result.PostBookmarksAndNotes).Disposition);
    }

    [Theory]
    [InlineData("preview-role")] [InlineData("unavailable")] [InlineData("omitted")]
    [InlineData("unknown")] [InlineData("failed")] [InlineData("cancelled")]
    public void PreviewAndNonContainedClaimsCannotSatisfyAnOriginalFavorite(string kind)
    {
        TransferAttachment attachment = kind == "preview-role"
            ? Attachment() with { Role = TransferMediaRole.Preview }
            : Attachment(status: kind switch
            {
                "unavailable" => TransferContentStatus.Unavailable,
                "omitted" => TransferContentStatus.Omitted,
                "unknown" => TransferContentStatus.Unknown,
                "failed" => TransferContentStatus.Failed,
                _ => TransferContentStatus.Cancelled
            });
        // Even the matching remote hash is not a contained original asset.
        attachment = attachment with { Metadata = attachment.Metadata with { ExpectedSha256 = MediaHash } };
        NativeTransferCatalog catalog = RawCatalog();
        TransferPost post = catalog.Threads[0].Posts[0] with
        {
            Metadata = catalog.Threads[0].Posts[0].Metadata with { Attachments = [attachment.Metadata] },
            Attachments = [attachment]
        };
        catalog = catalog with { Threads = [catalog.Threads[0] with { Posts = [post] }] };
        NativePrivateMetadataComparisonResult result = Compare(Projection(), catalog, Projection(), new(MediaFavorites: true));
        Assert.Equal(NativePrivateMetadataDisposition.MissingTarget, Assert.Single(result.MediaFavorites).Disposition);
    }

    [Theory]
    [InlineData("source")] [InlineData("board")] [InlineData("thread")]
    [InlineData("leading-zero")] [InlineData("unicode")]
    public void MatchingPostAndHashInAnotherIdentityNeverSatisfyRequestedTargets(string mismatch)
    {
        ResourceId changed = mismatch switch
        {
            "source" => Thread with { Source = "SYNTHETIC-SOURCE" },
            "board" => Thread with { Board = "SYNTHETIC-BOARD" },
            "thread" => Other,
            "leading-zero" => Thread with { Thread = "0100" },
            _ => Thread with { Board = "cafe\u0301" }
        };
        ResourceId requested = mismatch == "unicode" ? Thread with { Board = "caf\u00e9" } : Thread;
        NativePrivateMetadataProjection incoming = Projection(requested);
        NativePrivateMetadataProjection target = Projection(requested);
        // Public targets remain a subset of exactly the same private scope.
        incoming = new([requested, changed], All, incoming.ThreadFavorites, incoming.MediaFavorites, incoming.PostBookmarksAndNotes);
        target = new([changed, requested], All, target.ThreadFavorites, target.MediaFavorites, target.PostBookmarksAndNotes);
        NativePrivateMetadataComparisonResult result = Compare(incoming, RawCatalog(id: changed), target, All);
        Assert.Equal(NativePrivateMetadataDisposition.MissingTarget, Assert.Single(result.ThreadFavorites).Disposition);
        Assert.Equal(NativePrivateMetadataDisposition.MissingTarget, Assert.Single(result.MediaFavorites).Disposition);
        Assert.Equal(NativePrivateMetadataDisposition.MissingTarget, Assert.Single(result.PostBookmarksAndNotes).Disposition);
    }

    [Theory]
    [InlineData("P-1")] [InlineData("p-01")] [InlineData("missing")]
    public void BookmarkPostKeyIsExactEvenWhenLocalOrphanExists(string post)
    {
        NativePrivateMetadataProjection projection = Projection(bookmarks: [new(new(Thread, post), Note)]);
        NativePrivateMetadataComparisonResult result = Compare(projection, target: projection,
            selection: new(PostBookmarksAndNotes: true));
        Assert.Equal(NativePrivateMetadataDisposition.MissingTarget, Assert.Single(result.PostBookmarksAndNotes).Disposition);
    }

    [Theory]
    [InlineData(false, "media-case")] [InlineData(true, "media-case")]
    [InlineData(false, "variant-case")] [InlineData(true, "variant-case")]
    [InlineData(false, "hash")] [InlineData(true, "hash")]
    [InlineData(false, "unicode")] [InlineData(true, "unicode")]
    public void CurrentAndRetainedKeysNeverUseCaseUnicodeOrHashOnlyInference(bool retained, string mismatch)
    {
        NativeTransferCatalog catalog = RawCatalog(includeMedia: !retained, retained: retained);
        string mediaId = retained ? "retained" : Media;
        FavoriteAssetKey key = new(Thread, mediaId, "original", MediaHash);
        if (mismatch == "unicode")
        {
            // Use a valid captured descriptor with a composed media identity.
            // Retained revisions are rebuilt from provider metadata, not copied after re-keying.
            TransferAttachment original = retained ? catalog.Threads[0].RetainedBindings[0].Attachment
                : catalog.Threads[0].Posts[0].Attachments[0];
            CapturedAttachment metadata = original.Metadata with { MediaId = "caf\u00e9" };
            if (retained)
            {
                MediaProviderMetadata provider = new(metadata.MediaId!, metadata.Variant, metadata.Url!,
                    metadata.OriginalFileName, metadata.ExpectedSize, metadata.ExpectedMd5, metadata.ExpectedSha256);
                metadata = metadata with { AttachmentRevisionKey = MediaRevisionKeys.CreateAttachmentKey(Thread, Post, 0, provider) };
                catalog = catalog with { Threads = [catalog.Threads[0] with
                    { RetainedBindings = [new(Post, original with { Metadata = metadata })] }] };
            }
            else
            {
                TransferPost post = catalog.Threads[0].Posts[0];
                post = post with { Metadata = post.Metadata with { Attachments = [metadata] },
                    Attachments = [original with { Metadata = metadata }] };
                catalog = catalog with { Threads = [catalog.Threads[0] with { Posts = [post] }] };
            }
            key = key with { MediaIdentifier = "cafe\u0301" };
        }
        else key = mismatch switch
        {
            "media-case" => key with { MediaIdentifier = mediaId.ToUpperInvariant() },
            "variant-case" => key with { Variant = "Original" },
            _ => key with { Sha256 = new string('b', 64) }
        };
        NativePrivateMetadataProjection incoming = Projection(mediaFavorites: [key]);
        NativePrivateMetadataComparisonResult result = Compare(incoming, catalog, incoming, new(MediaFavorites: true));
        Assert.Equal(key, Assert.Single(result.MediaFavorites).Target);
        Assert.Equal(NativePrivateMetadataDisposition.MissingTarget, result.MediaFavorites[0].Disposition);
    }

    [Fact]
    public void ResultGraphContainsNeitherOldTargetNotesNorUnselectedIncomingValues()
    {
        NativePrivateMetadataProjection incoming = Projection(bookmarks: [new(new(Thread, Post), "unselected incoming sentinel")]);
        NativePrivateMetadataProjection target = Projection(bookmarks: [new(new(Thread, Post), OtherNote)]);
        NativePrivateMetadataComparisonResult unselected = Compare(incoming, target: target, selection: new(ThreadFavorites: true));
        AssertGraphDoesNotContain(unselected, OtherNote, "unselected incoming sentinel");
        NativePrivateMetadataComparisonResult conflict = Compare(Projection(), target: target, selection: All);
        Assert.Equal(Note, Assert.Single(conflict.PostBookmarksAndNotes).IncomingNote);
        AssertGraphDoesNotContain(conflict, OtherNote);
    }

    private static void AssertGraphDoesNotContain(object root, params string[] forbidden)
    {
        HashSet<object> visited = new(ReferenceEqualityComparer.Instance);
        Visit(root);
        void Visit(object? value)
        {
            if (value is null || !visited.Add(value)) return;
            if (value is string text)
            {
                foreach (string secret in forbidden) Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
                return;
            }
            Type type = value.GetType();
            if (type.IsPrimitive || type.IsEnum) return;
            if (value is IEnumerable values)
            {
                foreach (object? entry in values) Visit(entry);
                return;
            }
            // Includes private backing fields: retaining the whole target projection is a leak too.
            if (type.Namespace?.StartsWith("MeowChan", StringComparison.Ordinal) != true) return;
            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                Visit(field.GetValue(value));
        }
    }

    [Fact]
    public void ToStringIsOpaqueAndDoesNotExposePrivateNotesOrIdentities()
    {
        NativePrivateMetadataComparisonResult result = Compare(Projection(), RawCatalog(), Projection(), All);
        string text = result.ToString()!;
        Assert.DoesNotContain(Note, text, StringComparison.Ordinal);
        Assert.DoesNotContain(OtherNote, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Thread.Source, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Thread.Board, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Thread.Thread!, text, StringComparison.Ordinal);
        Assert.DoesNotContain(MediaHash, text, StringComparison.Ordinal);
        Assert.True(result.GetType().IsSealed);
        foreach (object row in result.ThreadFavorites.Cast<object>().Concat(result.MediaFavorites.Cast<object>())
            .Concat(result.PostBookmarksAndNotes.Cast<object>()))
        {
            Assert.True(row.GetType().IsSealed);
            string diagnostic = row.ToString()!;
            foreach (string secret in new[] { Note, OtherNote, Thread.Source, Thread.Board, MediaHash })
                Assert.DoesNotContain(secret, diagnostic, StringComparison.Ordinal);
        }
    }
}
