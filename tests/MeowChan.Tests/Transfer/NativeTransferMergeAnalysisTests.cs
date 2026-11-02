using System.Collections.Immutable;
using System.Globalization;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Storage;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

public sealed class NativeTransferMergeAnalysisTests
{
    private static readonly ResourceId Id = new("4chan", "b", "7001");
    private static readonly DateTimeOffset At = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly string Hash = new('a', 64);
    private static TransferPost Post(string id, long ordinal = 7) => new(
        new CapturedPost(id, ordinal, PostFieldPresence.All, At, "subject", "author", "body", false,
            ImmutableArray<CapturedAttachment>.Empty), []);
    private static TransferThread Thread(params TransferPost[] posts) => new(Id, null, "synthetic", ThreadSourceState.NotFound,
        At, At, 7, null, posts.ToImmutableArray());
    private static TransferAttachment Media()
    {
        CapturedAttachment metadata = new(0, "111", "original", "https://i.4cdn.org/b/111.jpg", "synthetic.jpg",
            ExpectedSize: 3, ExpectedMd5: "kAFQmDzST7DWlj99KOF/cg==")
        {
            ExpectedSha1 = "qZk+NkcGgWq6PiVxeFDCbJzQ2J0=", ExpectedSha256 = Hash,
            ProviderSizeHint = new("3", "B", MediaSizePrecision.Unspecified)
        };
        MediaProviderMetadata provider = new(metadata.MediaId!, metadata.Variant, metadata.Url!, metadata.OriginalFileName,
            metadata.ExpectedSize, metadata.ExpectedMd5, metadata.ExpectedSha256)
        { ExpectedSha1 = metadata.ExpectedSha1, ProviderSizeHint = metadata.ProviderSizeHint };
        metadata = metadata with { AttachmentRevisionKey = MediaRevisionKeys.CreateAttachmentKey(Id, "15", 0, provider) };
        return new(metadata, TransferMediaRole.Original, TransferContentStatus.Contained, Hash,
            new("strict", At, false, false));
    }
    private static TransferPost WithMedia(TransferPost post, params TransferAttachment[] media) => post with
    {
        Metadata = post.Metadata with { Attachments = media.Select(a => a.Metadata).ToImmutableArray() },
        Attachments = media.ToImmutableArray(), AttachmentsWereNull = false
    };
    private static TransferMergeAnalysisResult Analyze(TransferPost target, TransferPost incoming)
        => NativeTransferMergeAnalysis.Analyze(Thread(target), Thread(incoming));
    private static void Conflict(TransferPost target, TransferPost incoming, TransferPostDifference expected)
    {
        TransferMergeAnalysisResult result = Analyze(target, incoming);
        Assert.Equal(TransferMergeAnalysisError.None, result.Error);
        TransferMergePostAnalysis post = Assert.Single(result.Posts);
        Assert.Equal(TransferMergeDisposition.Conflict, post.Disposition);
        Assert.Equal(expected, post.Differences);
    }

    [Theory]
    [InlineData(ThreadSourceState.NotFound)]
    [InlineData(ThreadSourceState.Archived)]
    public void OverlapOneToTwentyAndFifteenToThirtyPreservesTerminalAndExclusiveTarget(ThreadSourceState terminal)
    {
        TransferPost[] IncomingRange() => Enumerable.Range(1, 20).Select(i => Post(i.ToString(CultureInfo.InvariantCulture), i)).ToArray();
        TransferThread target = Thread(Enumerable.Range(15, 16).Select(i => Post(i.ToString(CultureInfo.InvariantCulture), i)).ToArray())
            with { SourceState = terminal };
        TransferThread incoming = Thread(IncomingRange()) with { SourceState = ThreadSourceState.Active, CaptureRevision = 9000 };
        TransferMergeAnalysisResult result = NativeTransferMergeAnalysis.Analyze(target, incoming);
        Assert.Equal(TransferMergeAnalysisError.None, result.Error);
        Assert.Equal(14, result.Posts.Count(p => p.Disposition == TransferMergeDisposition.New));
        Assert.Equal(6, result.Posts.Count(p => p.Disposition == TransferMergeDisposition.Identical));
        Assert.Equal(10, result.Posts.Count(p => p.Disposition == TransferMergeDisposition.TargetOnly));
        Assert.DoesNotContain(result.Posts, p => p.Disposition == TransferMergeDisposition.Conflict);
        Assert.Equal(30, result.Posts.Length);
        Assert.True(Enumerable.Range(1, 30).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToHashSet()
            .SetEquals(result.Posts.Select(p => p.PostId)));
        Assert.Equal(terminal, target.SourceState);
        Assert.Equal(7, target.CaptureRevision);
        Assert.Equal(16, target.Posts.Length);
        Assert.Equal(20, incoming.Posts.Length);
    }

    [Theory]
    [InlineData("source")] [InlineData("board")] [InlineData("thread")]
    public void DifferentResourceNeverProducesCandidates(string component)
    {
        ResourceId other = component switch
        {
            "source" => Id with { Source = "4Chan" }, "board" => Id with { Board = "B" },
            _ => Id with { Thread = "07001" }
        };
        TransferMergeAnalysisResult result = NativeTransferMergeAnalysis.Analyze(Thread(Post("15")), Thread(Post("16")) with { Id = other });
        Assert.Equal(TransferMergeAnalysisError.DifferentResource, result.Error);
        Assert.Empty(result.Posts); Assert.Empty(result.RetainedBindings);
    }

    [Fact]
    public void OpaqueOrdinalIdentityDoesNotNormalizeParseOrMergeByBytes()
    {
        string[] ids = ["01", "1", "A", "a", "é", "e\u0301", "999999999999999999999999999999999999", "opaque/post"];
        TransferPost[] posts = ids.Select(id => WithMedia(Post(id, long.MaxValue), Media() with
            { Metadata = Media().Metadata with { AttachmentRevisionKey = null }, History = null })).ToArray();
        TransferMergeAnalysisResult result = NativeTransferMergeAnalysis.Analyze(Thread(), Thread(posts));
        Assert.Equal(TransferMergeAnalysisError.None, result.Error);
        Assert.Equal(ids, result.Posts.Select(p => p.PostId)); // input order, not a default numeric/source sort
        Assert.All(result.Posts, p => Assert.Equal(TransferMergeDisposition.New, p.Disposition));
    }

    [Fact]
    public void EqualOverlapUsesValuesNotCapturedListOrRecordReferences()
    {
        TransferAttachment a = Media();
        TransferAttachment b = Media() with
        {
            Metadata = Media().Metadata with { ProviderSizeHint = new("3", "B", MediaSizePrecision.Unspecified) },
            History = new("strict", At, false, false)
        };
        TransferMergeAnalysisResult result = Analyze(WithMedia(Post("15"), a), WithMedia(Post("15"), b));
        Assert.Equal(TransferMergeDisposition.Identical, Assert.Single(result.Posts).Disposition);
        Assert.Equal(TransferPostDifference.None, result.Posts[0].Differences);
    }

    [Theory]
    [InlineData("ordinal", (int)TransferPostDifference.Ordinal)]
    [InlineData("posted", (int)TransferPostDifference.PostedAt)]
    [InlineData("subject", (int)TransferPostDifference.Subject)]
    [InlineData("author", (int)TransferPostDifference.Author)]
    [InlineData("html", (int)TransferPostDifference.Html)]
    [InlineData("deleted", (int)TransferPostDifference.IsDeleted)]
    public void EveryScalarDifferenceIsExplicit(string field, int expected)
    {
        TransferPost post = Post("15");
        CapturedPost changed = field switch
        {
            "ordinal" => post.Metadata with { Ordinal = long.MaxValue },
            "posted" => post.Metadata with { PostedAt = At.AddTicks(1) },
            "subject" => post.Metadata with { Subject = "" }, "author" => post.Metadata with { Author = null },
            "html" => post.Metadata with { Html = "BODY" }, _ => post.Metadata with { IsDeleted = true }
        };
        Conflict(post, post with { Metadata = changed }, (TransferPostDifference)expected);
    }

    [Fact]
    public void MissingPresenceIsNotNullOrEmptyAndPartialPostIsNotSynthesized()
    {
        TransferPost target = Post("15") with { Metadata = Post("15").Metadata with
            { Fields = PostFieldPresence.All & ~PostFieldPresence.Html, Html = null, Author = "" } };
        TransferPost incoming = Post("15");
        Conflict(target, incoming, TransferPostDifference.Presence | TransferPostDifference.Html | TransferPostDifference.Author);
        TransferPost explicitNull = incoming with { Metadata = incoming.Metadata with { Html = null } };
        Conflict(target with { Metadata = target.Metadata with { Author = "author" } }, explicitNull, TransferPostDifference.Presence);
        Conflict(explicitNull, explicitNull with { Metadata = explicitNull.Metadata with { Html = "" } }, TransferPostDifference.Html);
        Assert.Null(target.Metadata.Html); Assert.Equal("", target.Metadata.Author);
    }

    [Fact]
    public void AttachmentNullEmptyAndMissingPresenceRemainDistinct()
    {
        TransferPost empty = Post("15");
        TransferPost observedNull = empty with { Metadata = empty.Metadata with { Attachments = null }, AttachmentsWereNull = true };
        Conflict(empty, observedNull, TransferPostDifference.Attachments);
        TransferPost missing = empty with { Metadata = empty.Metadata with { Fields = PostFieldPresence.All & ~PostFieldPresence.Attachments } };
        Conflict(empty, missing, TransferPostDifference.Presence);
    }

    [Theory]
    [InlineData("position")] [InlineData("mediaId")] [InlineData("variant")] [InlineData("url")]
    [InlineData("name")] [InlineData("size")] [InlineData("md5")] [InlineData("sha1")] [InlineData("sha256")]
    [InlineData("hint-null")] [InlineData("hint-value")] [InlineData("hint-unit")] [InlineData("hint-precision")]
    [InlineData("deleted")] [InlineData("revision-null")] [InlineData("revision")] [InlineData("role")]
    [InlineData("payload")] [InlineData("history-kind")] [InlineData("history-time")]
    [InlineData("history-size")] [InlineData("history-md5")] [InlineData("history-null")]
    public void AllAttachmentValuesAreConservativeNotHashAliases(string field)
    {
        TransferAttachment a = Media();
        CapturedAttachment m = a.Metadata;
        TransferAttachment b = field switch
        {
            "position" => a with { Metadata = m with { Position = 1 } },
            "mediaId" => a with { Metadata = m with { MediaId = "112" } },
            "variant" => a with { Metadata = m with { Variant = "preview" } },
            "url" => a with { Metadata = m with { Url = "https://i.4cdn.org/b/112.jpg" } },
            "name" => a with { Metadata = m with { OriginalFileName = null } },
            "size" => a with { Metadata = m with { ExpectedSize = null } },
            "md5" => a with { Metadata = m with { ExpectedMd5 = m.ExpectedMd5 + "\n" } },
            "sha1" => a with { Metadata = m with { ExpectedSha1 = null } },
            "sha256" => a with { Metadata = m with { ExpectedSha256 = Hash.ToUpperInvariant() } },
            "hint-null" => a with { Metadata = m with { ProviderSizeHint = null } },
            "hint-value" => a with { Metadata = m with { ProviderSizeHint = m.ProviderSizeHint! with { RawValue = "3.0" } } },
            "hint-unit" => a with { Metadata = m with { ProviderSizeHint = m.ProviderSizeHint! with { UnitLabel = "b" } } },
            "hint-precision" => a with { Metadata = m with { ProviderSizeHint = m.ProviderSizeHint! with { Precision = MediaSizePrecision.Rounded } } },
            "deleted" => a with { Metadata = m with { IsDeleted = true } },
            "revision-null" => a with { Metadata = m with { AttachmentRevisionKey = null } },
            "revision" => a with { Metadata = m with { AttachmentRevisionKey = "att-v1:" + Hash } },
            "role" => a with { Role = TransferMediaRole.Preview },
            "payload" => a with { PayloadSha256 = new string('b', 64) },
            "history-kind" => a with { History = a.History! with { Kind = "provider-mismatch" } },
            "history-time" => a with { History = a.History! with { AcceptedAtUtc = At.AddTicks(1) } },
            "history-size" => a with { History = a.History! with { SizeMismatch = true } },
            "history-md5" => a with { History = a.History! with { Md5Mismatch = true } },
            _ => a with { History = null }
        };
        // This layer intentionally checks comparison shape, not GP10 hash/revision authenticity.
        Conflict(WithMedia(Post("15"), a), WithMedia(Post("15"), b), TransferPostDifference.Attachments);
    }

    [Theory]
    [InlineData((int)TransferContentStatus.Omitted)] [InlineData((int)TransferContentStatus.Unknown)]
    [InlineData((int)TransferContentStatus.Unavailable)] [InlineData((int)TransferContentStatus.Failed)]
    [InlineData((int)TransferContentStatus.Cancelled)]
    public void SkippedMediaNeverDowngradesOrRemovesTarget(int value)
    {
        TransferContentStatus status = (TransferContentStatus)value;
        TransferAttachment original = Media();
        TransferPost target = WithMedia(Post("15"), original);
        TransferPost incoming = WithMedia(Post("15"), original with { Status = status, PayloadSha256 = null, History = null });
        Conflict(target, incoming, TransferPostDifference.Attachments);
        Assert.Same(original, target.Attachments[0]); Assert.Equal(Hash, target.Attachments[0].PayloadSha256);
        Conflict(target, Post("15"), TransferPostDifference.Attachments);
        // The reverse (potential missing-byte fill) is deliberately not a synthesized safe post.
        Conflict(incoming, target, TransferPostDifference.Attachments);
    }

    [Fact]
    public void AttachmentPositionsRatherThanArrayOrderDefineExactSlots()
    {
        TransferAttachment a = Media();
        TransferAttachment b = a with { Metadata = a.Metadata with { Position = 1, AttachmentRevisionKey = null }, History = null };
        TransferMergeAnalysisResult result = Analyze(WithMedia(Post("15"), a, b), WithMedia(Post("15"), b, a));
        Assert.Equal(TransferMergeDisposition.Identical, Assert.Single(result.Posts).Disposition);
    }

    [Fact]
    public void ConflictingOverlapDoesNotHideOtherNewOrExclusivePosts()
    {
        TransferPost conflict = Post("15") with { Metadata = Post("15").Metadata with { Html = "conflicting" } };
        TransferThread target = Thread(Post("15"), Post("30"));
        TransferThread incoming = Thread(conflict, Post("1"));
        TransferMergeAnalysisResult result = NativeTransferMergeAnalysis.Analyze(target, incoming);
        Assert.Equal(new[] { TransferMergeDisposition.TargetOnly, TransferMergeDisposition.Conflict, TransferMergeDisposition.New },
            result.Posts.Select(p => p.Disposition));
        Assert.Equal("body", target.Posts[0].Metadata.Html);
    }

    [Theory]
    [InlineData(null, true)] [InlineData(false, true)] [InlineData(true, false)]
    public void SortContractsAreReportedWithoutFallbackOrSorting(bool? targetOrder, bool incomingOrder)
    {
        TransferThread target = Thread(Post("10"), Post("2")) with { DecimalPostOrder = targetOrder };
        TransferThread incoming = Thread(Post("2"), Post("10"), Post("01")) with { DecimalPostOrder = incomingOrder };
        TransferMergeAnalysisResult result = NativeTransferMergeAnalysis.Analyze(target, incoming);
        Assert.Equal(TransferThreadDifference.OrderContract, result.ThreadDifferences);
        Assert.Equal(new[] { "2", "10", "01" }, result.Posts.Select(p => p.PostId));
        Assert.Equal(targetOrder, target.DecimalPostOrder);
    }

    [Fact]
    public void ProvenanceAndLocalCaptureCountersAreNeverGlobalFreshnessOrEquality()
    {
        TransferThread target = Thread(Post("15"));
        TransferThread incoming = target with { SourceState = ThreadSourceState.Active, FirstSeenAtUtc = At.AddYears(-1),
            CapturedAtUtc = null, CaptureRevision = long.MaxValue };
        TransferMergeAnalysisResult result = NativeTransferMergeAnalysis.Analyze(target, incoming);
        Assert.Equal(TransferThreadDifference.None, result.ThreadDifferences);
        Assert.Equal(TransferMergeDisposition.Identical, Assert.Single(result.Posts).Disposition);
        result = NativeTransferMergeAnalysis.Analyze(target, incoming with { Title = "different", PageUrl = "https://boards.4chan.org/b/thread/7001" });
        Assert.Equal(TransferThreadDifference.Title | TransferThreadDifference.PageUrl, result.ThreadDifferences);
        Assert.Equal(TransferMergeDisposition.Identical, Assert.Single(result.Posts).Disposition);
    }

    [Fact]
    public void RetainedBindingsNeverInventPostsAndMissingBindingsNeverDelete()
    {
        TransferRetainedBinding retained = new("historic-not-a-post", Media());
        TransferThread target = Thread(Post("15")) with { RetainedBindings = [retained] };
        TransferMergeAnalysisResult result = NativeTransferMergeAnalysis.Analyze(target, Thread(Post("15")));
        Assert.Equal(TransferMergeDisposition.TargetOnly, Assert.Single(result.RetainedBindings).Disposition);
        Assert.Single(result.Posts); Assert.Single(target.RetainedBindings);
        result = NativeTransferMergeAnalysis.Analyze(Thread(), target);
        Assert.Equal(TransferMergeDisposition.New, Assert.Single(result.RetainedBindings).Disposition);
        Assert.Equal("15", Assert.Single(result.Posts).PostId);
        result = NativeTransferMergeAnalysis.Analyze(target, target with { RetainedBindings = [retained with { Attachment = Media() }] });
        Assert.Equal(TransferMergeDisposition.Identical, Assert.Single(result.RetainedBindings).Disposition);
        result = NativeTransferMergeAnalysis.Analyze(target, target with { RetainedBindings = [retained with
            { Attachment = Media() with { PayloadSha256 = new string('b', 64) } }] });
        Assert.Equal(TransferMergeDisposition.Conflict, Assert.Single(result.RetainedBindings).Disposition);
    }

    [Fact]
    public void RetainedInputCanMatchExactCurrentRevisionButCannotMergeByPayload()
    {
        TransferAttachment media = Media();
        TransferThread target = Thread(WithMedia(Post("15"), media));
        TransferThread incoming = Thread() with { RetainedBindings = [new("15", Media())] };
        TransferMergeAnalysisResult result = NativeTransferMergeAnalysis.Analyze(target, incoming);
        Assert.Equal(TransferMergeDisposition.Identical, Assert.Single(result.RetainedBindings).Disposition);
        Assert.Equal(TransferMergeDisposition.TargetOnly, Assert.Single(result.Posts).Disposition);
        TransferAttachment another = media with { Metadata = media.Metadata with { AttachmentRevisionKey = "att-v2:" + Hash } };
        result = NativeTransferMergeAnalysis.Analyze(target, incoming with { RetainedBindings = [new("15", another)] });
        Assert.Equal(TransferMergeDisposition.New, Assert.Single(result.RetainedBindings).Disposition);
        Assert.Single(target.Posts[0].Attachments);
    }

    [Fact]
    public void NewCurrentPostIsNotSafeWhenItsRevisionContradictsRetainedTargetBytes()
    {
        TransferAttachment media = Media();
        TransferThread target = Thread() with { RetainedBindings = [new("15", media)] };
        TransferThread incoming = Thread(WithMedia(Post("15"), media with { PayloadSha256 = new string('b', 64) }));
        TransferMergeAnalysisResult result = NativeTransferMergeAnalysis.Analyze(target, incoming);
        Assert.Equal(TransferMergeAnalysisError.None, result.Error);
        Assert.Equal(TransferMergeDisposition.Conflict, Assert.Single(result.Posts).Disposition);
        Assert.Equal(TransferPostDifference.Attachments, result.Posts[0].Differences);
        Assert.Equal(TransferMergeDisposition.Conflict, Assert.Single(result.RetainedBindings).Disposition);
        Assert.Equal(Hash, target.RetainedBindings[0].Attachment.PayloadSha256);
        result = NativeTransferMergeAnalysis.Analyze(target, Thread(WithMedia(Post("15"), Media())));
        Assert.Equal(TransferMergeDisposition.New, Assert.Single(result.Posts).Disposition);
        Assert.Equal(TransferMergeDisposition.Identical, Assert.Single(result.RetainedBindings).Disposition);
    }

    [Fact]
    public void CallerOwnedListsStayUnmodifiedAndResultsRetainNoMutableMetadata()
    {
        List<CapturedAttachment> list = [Media().Metadata];
        TransferPost post = WithMedia(Post("15"), Media());
        post = post with { Metadata = post.Metadata with { Attachments = list } };
        TransferThread target = Thread(post);
        TransferMergeAnalysisResult result = NativeTransferMergeAnalysis.Analyze(target, Thread(WithMedia(Post("15"), Media())));
        Assert.Same(list, target.Posts[0].Metadata.Attachments); Assert.Single(list);
        list.Clear(); // later caller mutation cannot change the identity-only report
        Assert.Equal(TransferMergeDisposition.Identical, Assert.Single(result.Posts).Disposition);
    }

    [Fact]
    public void RepeatAnalysisIsDeterministicAndDoesNotPretendToCommit()
    {
        TransferThread target = Thread(Post("15")); TransferThread incoming = Thread(Post("15"), Post("16"));
        TransferMergeAnalysisResult first = NativeTransferMergeAnalysis.Analyze(target, incoming);
        TransferMergeAnalysisResult second = NativeTransferMergeAnalysis.Analyze(target, incoming);
        Assert.Equal(first.Posts.ToArray(), second.Posts.ToArray()); Assert.Single(target.Posts);
        Assert.Equal(TransferMergeDisposition.New, second.Posts[1].Disposition);
    }

    [Fact]
    public void DuplicatePostSlotOrRevisionAndInconsistentProjectionRejectWithoutPartialCandidates()
    {
        TransferPost post = WithMedia(Post("15"), Media());
        TransferThread[] invalid =
        [
            Thread(Post("15"), Post("15")),
            Thread(WithMedia(Post("15"), Media(), Media() with { Metadata = Media().Metadata with { AttachmentRevisionKey = null } })),
            Thread(post) with { RetainedBindings = [new("15", Media())] },
            Thread(post with { Metadata = post.Metadata with { Attachments = [] } }),
            Thread(post with { AttachmentsWereNull = true }),
            Thread(Post("15") with { Metadata = Post("15").Metadata with { Fields = PostFieldPresence.None } }),
            Thread(post with { Attachments = default }),
            Thread() with { Posts = default }, Thread() with { RetainedBindings = default },
            Thread(WithMedia(Post("15"), Media() with { Metadata = Media().Metadata with { PlannedPath = "/SECRET/local-only" } }))
        ];
        foreach (TransferThread input in invalid)
        {
            TransferMergeAnalysisResult result = NativeTransferMergeAnalysis.Analyze(Thread(), input);
            Assert.Equal(TransferMergeAnalysisError.InvalidInput, result.Error);
            Assert.Empty(result.Posts); Assert.Empty(result.RetainedBindings);
            Assert.DoesNotContain("SECRET", result.ToString());
        }
    }

    [Fact]
    public void CountsTextAndRetainedWorkAreBoundedBeforeReturningAnyCandidates()
    {
        TransferThread post = Thread(Post("15"));
        TransferMergeAnalysisResult result = NativeTransferMergeAnalysis.Analyze(post, post, new(MaxPosts: 1));
        Assert.Equal(TransferMergeAnalysisError.BudgetExceeded, result.Error); Assert.Empty(result.Posts);
        result = NativeTransferMergeAnalysis.Analyze(Thread(), Thread() with { RetainedBindings = [new("15", Media())] }, new(MaxAttachments: 0));
        Assert.Equal(TransferMergeAnalysisError.BudgetExceeded, result.Error);
        result = NativeTransferMergeAnalysis.Analyze(Thread(), post, new(MaxTextCharacters: 1));
        Assert.Equal(TransferMergeAnalysisError.BudgetExceeded, result.Error);
        result = NativeTransferMergeAnalysis.Analyze(Thread(), Thread(Post("15") with
            { Metadata = Post("15").Metadata with { Html = new string('x', 1_048_577) } }));
        Assert.Equal(TransferMergeAnalysisError.BudgetExceeded, result.Error);
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeTransferMergeAnalysis.Analyze(post, post, new(MaxPosts: 200_001)));
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeTransferMergeAnalysis.Analyze(post, post, new(MaxAttachments: -1)));
    }

    [Fact]
    public void ExactLowerBudgetsSucceedIncludingBothSnapshotsAndRetainedBindings()
    {
        TransferThread input = Thread(WithMedia(Post("15"), Media()));
        TransferMergeAnalysisResult result = NativeTransferMergeAnalysis.Analyze(input, input, new(MaxPosts: 2, MaxAttachments: 2));
        Assert.Equal(TransferMergeAnalysisError.None, result.Error);
        Assert.Equal(TransferMergeDisposition.Identical, Assert.Single(result.Posts).Disposition);
        result = NativeTransferMergeAnalysis.Analyze(input, input, new(MaxPosts: 2, MaxAttachments: 1));
        Assert.Equal(TransferMergeAnalysisError.BudgetExceeded, result.Error);
    }

    [Fact]
    public void CancellationDuringComparisonShapeReadPropagatesWithoutPartialSuccess()
    {
        using CancellationTokenSource cancellation = new();
        TransferPost post = WithMedia(Post("15"), Media());
        post = post with { Metadata = post.Metadata with
            { Attachments = new CancelOnReadAttachmentList(Media().Metadata, cancellation) } };
        OperationCanceledException error = Assert.Throws<OperationCanceledException>(() =>
            NativeTransferMergeAnalysis.Analyze(Thread(post), Thread(), cancellationToken: cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    private sealed class CancelOnReadAttachmentList(CapturedAttachment attachment, CancellationTokenSource cancellation)
        : IReadOnlyList<CapturedAttachment>
    {
        public int Count => 1;
        public CapturedAttachment this[int index]
        {
            get { cancellation.Cancel(); return index == 0 ? attachment : throw new IndexOutOfRangeException(); }
        }
        public IEnumerator<CapturedAttachment> GetEnumerator() { yield return this[0]; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void PreCancelledTokenNeverProducesAnAnalysis()
    {
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        OperationCanceledException error = Assert.Throws<OperationCanceledException>(() =>
            NativeTransferMergeAnalysis.Analyze(Thread(), Thread(), cancellationToken: cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }
}
