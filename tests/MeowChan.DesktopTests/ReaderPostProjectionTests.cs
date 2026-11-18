using MeowChan;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

// Reader foundation (plan P0): the projection "thread -> shown order" and its contract for P2/P4.
public sealed class ReaderPostProjectionTests
{
    private static readonly ResourceId Thread = new("4chan", "g", "100");

    private static ArchivedThreadView View(int count, Func<int, bool>? media = null, long revision = 7) => new(
        new ArchiveThreadSummary(Thread, "https://boards.4chan.org/g/thread/100", "", "",
            ThreadSourceState.Archived, true, DateTimeOffset.UnixEpoch, null, count, revision),
        Enumerable.Range(0, count).Select(index => new CapturedPost((100 + index).ToString(), index, PostFieldPresence.All,
            Html: "post " + index,
            Attachments: media?.Invoke(index) == true ? new[] { new CapturedAttachment(0, "m" + index, OriginalFileName: index + ".png") } : null))
            .ToArray(),
        decimalPostOrder: true);

    private sealed class Rules(Func<int, ReaderPostVerdict> judge, bool graph = false) : IReaderPostClassifier
    {
        public bool IsActive(ResourceId thread) => true;
        public bool UsesQuoteGraph => graph;
        public long Revision { get; set; } = 1;
        public ReaderQuoteGraph? SeenGraph { get; private set; }
        public void Classify(ArchivedThreadView thread, ReaderQuoteGraph? quotes, ReaderPostVerdict[] verdicts, CancellationToken token)
        {
            SeenGraph = quotes;
            Assert.Equal(thread.PostCount, verdicts.Length);
            Assert.All(verdicts, verdict => Assert.Equal(default, verdict)); // Prefilled with Shown for every classifier.
            for (int index = 0; index < verdicts.Length; index++) verdicts[index] = judge(index);
        }
    }

    private sealed class FixedOrder(Func<int, ReaderOrderEntry[]> order) : IReaderPostOrdering
    {
        public bool IsActive(ResourceId thread) => true;
        public long Revision => 3;
        public ReaderOrderEntry[] Order(ArchivedThreadView thread, ReaderQuoteGraph graph, CancellationToken token) => order(thread.PostCount);
    }

    private static ReaderPostVerdict Hidden(string? reason = null) => new(ReaderPostVisibility.Hidden, ReaderPostMarks.None, reason);
    private static ReaderPostVerdict Stub(string? reason = null) => new(ReaderPostVisibility.Stub, ReaderPostMarks.None, reason);
    private static readonly ReaderPostVerdict Highlight = new(ReaderPostVisibility.Shown, ReaderPostMarks.Highlight, "hl");
    private static readonly IReadOnlySet<string> NoReveal = new HashSet<string>();

    private static ReaderPostProjection Build(ArchivedThreadView view, IReaderPostClassifier[]? classifiers = null,
        IReaderPostOrdering? ordering = null, IReadOnlySet<string>? revealed = null) =>
        ReaderPostProjection.Build(view, null, classifiers ?? Array.Empty<IReaderPostClassifier>(), ordering, revealed ?? NoReveal, CancellationToken.None);

    [Fact]
    public void IdentityIsTodaysChronologicalViewWithTheSamePages()
    {
        ArchivedThreadView view = View(120);
        ReaderPostProjection identity = ReaderPostProjection.Identity(view);
        Assert.True(identity.IsIdentity);
        Assert.False(identity.IsOrdered);
        Assert.Equal(120, identity.Count);
        Assert.Equal(0, identity.HiddenCount);
        foreach (int offset in new[] { 0, 50, 100 })
        {
            IReadOnlyList<ReaderPostEntry> page = identity.Page(offset, 50);
            Assert.Equal(view.GetPosts(offset, 50).Select(post => post.PostId), page.Select(entry => entry.Post.PostId));
            Assert.Equal(Enumerable.Range(offset, page.Count), page.Select(entry => entry.ThreadIndex));
            Assert.All(page, entry => Assert.Equal((0, -1, default(ReaderPostVerdict)), (entry.Depth, entry.ParentIndex, entry.Verdict)));
        }
        Assert.Equal(20, identity.Page(100, 50).Count);
        Assert.Empty(identity.Page(120, 50));
        Assert.Empty(identity.Page(500, 50));
        Assert.Equal(37, identity.PositionOf("137"));
        Assert.Equal(37, identity.NearestPosition("137"));
        Assert.Equal(-1, identity.PositionOf("999"));
        Assert.Equal(-1, identity.NearestPosition("999"));
        Assert.False(identity.IsHidden("137"));
        Assert.False(identity.IsConcealed("137"));
        Assert.Equal("137", identity[37].Post.PostId);
        // No classifier and no ordering: Build is the identity too.
        Assert.True(Build(view).IsIdentity);
        Assert.Equal(ReaderPostProjection.ComputeKey(view, Array.Empty<IReaderPostClassifier>(), null, NoReveal), identity.Key);
    }

    [Theory]
    [InlineData(50, 20, 40)]    // History retained the old 50-post offset; Prev must reach 20, then 0, not 30 and 10.
    [InlineData(100, 35, 70)]   // Non-step values are valid saved settings too.
    [InlineData(50, 200, 0)]
    [InlineData(100, 50, 100)]  // Existing default pages are unchanged.
    [InlineData(int.MaxValue, 20, 100)]
    [InlineData(int.MinValue, 20, 0)]
    public void RetainedOffsetsAreAlignedAndClampedToTheCurrentReaderPage(int position, int pageSize, int expected)
    {
        ReaderPostProjection projection = ReaderPostProjection.Identity(View(120));
        int offset = projection.PageOffset(position, pageSize);
        Assert.Equal(expected, offset);
        Assert.NotEmpty(projection.Page(offset, pageSize));
        Assert.Equal(0, offset % pageSize);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(35)]
    [InlineData(50)]
    [InlineData(200)]
    public void ConfiguredPagesCoverTheShownOrderExactlyOnceIncludingConversationAndHiddenPosts(int pageSize)
    {
        ReaderPostProjection projection = Build(View(231),
            new IReaderPostClassifier[] { new Rules(index => index % 3 == 0 ? Hidden() : default) },
            new FixedOrder(count => Enumerable.Range(0, count).Reverse()
                .Select(index => new ReaderOrderEntry(index, 0, -1)).ToArray()));
        Assert.True(projection.IsOrdered);
        Assert.Equal(154, projection.Count);
        List<string> visited = new();
        for (int offset = 0; offset < projection.Count; offset += pageSize)
        {
            Assert.Equal(offset, projection.PageOffset(offset, pageSize));
            IReadOnlyList<ReaderPostEntry> page = projection.Page(offset, pageSize);
            foreach (ReaderPostEntry entry in page)
                Assert.Equal(offset, projection.PageOffset(projection.PositionOf(entry.Post.PostId), pageSize));
            visited.AddRange(page.Select(entry => entry.Post.PostId));
        }
        Assert.Equal(Enumerable.Range(0, projection.Count).Select(position => projection[position].Post.PostId), visited);
        Assert.Equal(visited.Count, visited.Distinct().Count());
    }

    [Fact]
    public void EmptyViewsAlwaysStartAtZeroAndInvalidPageSizesAreRejected()
    {
        foreach (ReaderPostProjection projection in new[]
        {
            ReaderPostProjection.Identity(View(0)),
            Build(View(80), new IReaderPostClassifier[] { new Rules(_ => Hidden()) })
        })
        {
            Assert.Equal(0, projection.PageOffset(50, 20));
            Assert.Empty(projection.Page(projection.PageOffset(50, 20), 20));
            Assert.Throws<ArgumentOutOfRangeException>(() => projection.PageOffset(0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => projection.PageOffset(0, -1));
        }
    }

    [Fact]
    public void VerdictsCombineByPrecedenceMarksByOrAndTheStrongestReason()
    {
        ArchivedThreadView view = View(6);
        Rules first = new(index => index switch { 1 => Stub("first"), 2 => Hidden(), 3 => Highlight, 4 => Stub("weak"), _ => default });
        Rules second = new(index => index switch
        {
            1 => Highlight,
            2 => Stub("second"),
            3 => new ReaderPostVerdict(ReaderPostVisibility.Shown, ReaderPostMarks.Revealed, null), // Revealed is P0's alone.
            4 => Hidden("strong"),
            _ => default
        });
        ReaderPostProjection projection = Build(view, new IReaderPostClassifier[] { first, second });
        Assert.False(projection.IsIdentity);
        Assert.Equal(new[] { "100", "101", "103", "105" }, Enumerable.Range(0, projection.Count).Select(position => projection[position].Post.PostId));
        ReaderPostEntry stub = projection[projection.PositionOf("101")];
        Assert.Equal(new ReaderPostVerdict(ReaderPostVisibility.Stub, ReaderPostMarks.Highlight, "first"), stub.Verdict);
        Assert.Equal(new ReaderPostVerdict(ReaderPostVisibility.Shown, ReaderPostMarks.Highlight, "hl"), projection[projection.PositionOf("103")].Verdict);
        Assert.Equal(2, projection.HiddenCount);
        Assert.Equal(1, projection.StubCount);
        Assert.True(projection.IsHidden("102") && projection.IsHidden("104"));
        Assert.True(projection.IsConcealed("101") && !projection.IsHidden("101"));
        // The stronger verdict brings its reason; an equal one keeps the first reason; a weaker one adds none.
        Assert.Equal("strong", ReaderPostProjection.Combine(Stub("weak"), Hidden("strong")).Reason);
        Assert.Equal("a", ReaderPostProjection.Combine(Stub("a"), Stub("b")).Reason);
        Assert.Equal("a", ReaderPostProjection.Combine(Hidden("a"), Highlight).Reason);
        Assert.Equal(ReaderPostVisibility.Hidden, ReaderPostProjection.Combine(default, new((ReaderPostVisibility)9, 0, null)).Visibility);
    }

    [Fact]
    public void HidingAParentLiftsItsSubtreeByOneLevelAndReparentsIt()
    {
        // 0 root; 1 root; 2 child of 1; 3 child of 2; 4 child of 1; 5 root.
        ReaderOrderEntry[] Tree(int _) => new ReaderOrderEntry[] { new(0, 0, -1), new(1, 0, -1), new(2, 1, 1), new(3, 2, 2), new(4, 1, 1), new(5, 0, -1) };
        ArchivedThreadView view = View(6);
        ReaderPostProjection plain = Build(view, ordering: new FixedOrder(Tree));
        Assert.True(plain.IsOrdered);
        Assert.Equal(new[] { 0, 0, 1, 2, 1, 0 }, Enumerable.Range(0, 6).Select(position => plain[position].Depth));

        ReaderPostProjection one = Build(view, new IReaderPostClassifier[] { new Rules(index => index == 2 ? Hidden() : default) }, new FixedOrder(Tree));
        Assert.Equal(new[] { "100", "101", "103", "104", "105" }, Enumerable.Range(0, one.Count).Select(position => one[position].Post.PostId));
        ReaderPostEntry lifted = one[one.PositionOf("103")];
        Assert.Equal((1, 1), (lifted.Depth, lifted.ParentIndex)); // Under 101 now, one level up.
        Assert.Equal((1, 1), (one[one.PositionOf("104")].Depth, one[one.PositionOf("104")].ParentIndex)); // A sibling is untouched.

        ReaderPostProjection two = Build(view, new IReaderPostClassifier[] { new Rules(index => index is 1 or 2 ? Hidden() : default) }, new FixedOrder(Tree));
        Assert.Equal((0, -1), (two[two.PositionOf("103")].Depth, two[two.PositionOf("103")].ParentIndex));
        Assert.Equal((0, -1), (two[two.PositionOf("104")].Depth, two[two.PositionOf("104")].ParentIndex));

        // A placeholder parent stays a node: its children keep their level.
        ReaderPostProjection stub = Build(view, new IReaderPostClassifier[] { new Rules(index => index == 2 ? Stub() : default) }, new FixedOrder(Tree));
        Assert.Equal((2, 2), (stub[stub.PositionOf("103")].Depth, stub[stub.PositionOf("103")].ParentIndex));
    }

    [Fact]
    public void RevealingShowsTheFullPostWithItsMarkAndNearestPositionFindsTheNextShownPost()
    {
        ArchivedThreadView view = View(10, index => index % 3 == 0);
        ReaderMediaOnlyClassifier mediaOnly = new(new ReaderViewState { MediaOnly = true });
        ReaderPostProjection projection = Build(view, new IReaderPostClassifier[] { mediaOnly });
        Assert.Equal(new[] { "100", "103", "106", "109" }, Enumerable.Range(0, projection.Count).Select(position => projection[position].Post.PostId));
        Assert.Equal(6, projection.HiddenCount);
        Assert.Equal(-1, projection.PositionOf("104"));
        Assert.Equal(projection.PositionOf("106"), projection.NearestPosition("104")); // The next shown by thread index.
        Assert.Equal(projection.PositionOf("103"), projection.NearestPosition("103"));
        ReaderPostProjection last = Build(View(10, index => index < 3), new IReaderPostClassifier[] { mediaOnly });
        Assert.Equal(last.PositionOf("102"), last.NearestPosition("108")); // Nothing after: the one before.

        HashSet<string> revealed = new() { "104" };
        ReaderPostProjection shown = projection.WithRevealed(revealed);
        int position = shown.PositionOf("104");
        Assert.Equal(2, position); // Back at its chronological place.
        Assert.Equal(ReaderPostMarks.Revealed, shown[position].Verdict.Marks);
        Assert.Equal(ReaderPostVisibility.Shown, shown[position].Verdict.Visibility);
        Assert.Equal(5, shown.HiddenCount);
        Assert.False(shown.IsConcealed("104"));
        Assert.NotEqual(projection.Key, shown.Key);
        Assert.Equal(shown.Key, Build(view, new IReaderPostClassifier[] { mediaOnly }, revealed: revealed).Key);
        Assert.Same(shown, shown.WithRevealed(new HashSet<string> { "104" }));
        // Revealing a post the view already shows changes nothing.
        Assert.Equal(projection.Count, projection.WithRevealed(new HashSet<string> { "103" }).Count);
        Assert.Equal(projection.Count, projection.WithRevealed(new HashSet<string>()).Count);
    }

    [Fact]
    public void AnInvalidOrderFallsBackToChronological()
    {
        ArchivedThreadView view = View(4);
        foreach (Func<int, ReaderOrderEntry[]> broken in new Func<int, ReaderOrderEntry[]>[]
        {
            count => new ReaderOrderEntry[] { new(0, 0, -1), new(0, 0, -1), new(2, 0, -1), new(3, 0, -1) }, // Twice.
            count => new ReaderOrderEntry[] { new(0, 0, -1), new(1, 0, -1), new(2, 0, -1) },                // Missing one.
            count => new ReaderOrderEntry[] { new(0, 0, -1), new(1, -1, -1), new(2, 0, -1), new(3, 0, -1) }, // Negative depth.
            count => new ReaderOrderEntry[] { new(0, 0, -1), new(1, 1, 1), new(2, 0, -1), new(3, 0, -1) },  // Own parent.
            count => new ReaderOrderEntry[] { new(0, 0, -1), new(1, 0, -1), new(2, 0, -1), new(9, 0, -1) }, // Out of range.
            count => null!,
        })
        {
            ReaderPostProjection projection = Build(view, ordering: new FixedOrder(broken));
            Assert.True(projection.OrderFellBack);
            Assert.False(projection.IsOrdered); // Chronological fallback must keep the inline reply toggles usable.
            Assert.Equal(new[] { 0, 1, 2, 3 }, Enumerable.Range(0, 4).Select(position => projection[position].ThreadIndex));
            Assert.All(Enumerable.Range(0, 4), position => Assert.Equal(0, projection[position].Depth));
        }
        ReaderPostProjection reversed = Build(view, ordering: new FixedOrder(count =>
            Enumerable.Range(0, count).Reverse().Select(index => new ReaderOrderEntry(index, 0, -1)).ToArray()));
        Assert.False(reversed.OrderFellBack);
        Assert.True(reversed.IsOrdered);
        Assert.Equal(new[] { 3, 2, 1, 0 }, Enumerable.Range(0, 4).Select(position => reversed[position].ThreadIndex));
    }

    [Fact]
    public void PagesAreCutFromTheShownOrder()
    {
        ArchivedThreadView view = View(120, index => index % 2 == 0);
        ReaderPostProjection projection = Build(view, new IReaderPostClassifier[] { new ReaderMediaOnlyClassifier(new() { MediaOnly = true }) });
        Assert.Equal(60, projection.Count);
        Assert.Equal(Enumerable.Range(0, 50).Select(index => index * 2), projection.Page(0, 50).Select(entry => entry.ThreadIndex));
        Assert.Equal(Enumerable.Range(50, 10).Select(index => index * 2), projection.Page(50, 50).Select(entry => entry.ThreadIndex));
        Assert.Empty(projection.Page(60, 50));
        Assert.Equal(3, projection.Page(57, 50).Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => projection.Page(-1, 50));
        Assert.Equal(55, projection.PositionOf("210"));
        Assert.Equal(55, projection.NearestPosition("209"));
    }

    [Fact]
    public void OnlyPostsWithMediaKeepsTheOpeningPostAndIsOffByDefault()
    {
        ReaderViewState state = new();
        ReaderMediaOnlyClassifier classifier = new(state);
        Assert.False(classifier.IsActive(Thread));
        int changes = 0;
        state.Changed += (_, _) => changes++;
        state.MediaOnly = true;
        state.MediaOnly = true;
        Assert.Equal(1, changes);
        Assert.True(classifier.IsActive(Thread));
        ArchivedThreadView view = View(5, index => index == 3);
        ReaderPostProjection projection = Build(view, new IReaderPostClassifier[] { classifier });
        // 100 is the 4chan opening post (the thread number): it stays without media.
        Assert.Equal(new[] { "100", "103" }, Enumerable.Range(0, projection.Count).Select(position => projection[position].Post.PostId));
        Assert.False(state.HideMedia || state.Conversation);
    }

    [Fact]
    public void TheKeyFollowsRevisionsAndAnotherReadOfTheSameRevisionMatches()
    {
        ArchivedThreadView view = View(8, index => index % 2 == 0);
        Rules rules = new(index => index == 1 ? Hidden() : default);
        long before = ReaderPostProjection.ComputeKey(view, new IReaderPostClassifier[] { rules }, null, NoReveal);
        ReaderPostProjection projection = Build(view, new IReaderPostClassifier[] { rules });
        Assert.Equal(before, projection.Key);
        rules.Revision = 2;
        Assert.NotEqual(before, ReaderPostProjection.ComputeKey(view, new IReaderPostClassifier[] { rules }, null, NoReveal));
        Assert.False(projection.Inputs.Equals(ReaderPostProjection.ProjectionInputs.For(view, new IReaderPostClassifier[] { rules }, null)));
        Assert.True(projection.Matches(View(8, index => index % 2 == 0)));
        Assert.False(projection.Matches(View(8, index => index % 2 == 0, revision: 8)));
        Assert.False(projection.Matches(View(9)));
        Assert.False(projection.Matches(null));
    }

    [Fact]
    public void TheQuoteGraphIsBuiltOnlyForAConsumerThatAsksForIt()
    {
        ArchivedThreadView view = View(3);
        Rules plain = new(_ => default);
        Build(view, new IReaderPostClassifier[] { plain });
        Assert.Null(plain.SeenGraph);
        Rules quoting = new(_ => default, graph: true);
        Build(view, new IReaderPostClassifier[] { quoting });
        Assert.NotNull(quoting.SeenGraph);
        Assert.True(quoting.SeenGraph!.Matches(view));
    }

    [Fact]
    public void IndentationIsALocalMarginWithLevelAndWidthLimits()
    {
        Assert.Equal(new Avalonia.Thickness(8, 4), ArchivePage.ReaderPostIndentMargin(0, 1000));
        Assert.Equal(new Avalonia.Thickness(28, 4, 8, 4), ArchivePage.ReaderPostIndentMargin(1, 1000));
        Assert.Equal(new Avalonia.Thickness(8 + 16 * 20, 4, 8, 4), ArchivePage.ReaderPostIndentMargin(40, 5000)); // 16 levels at most.
        Assert.Equal(new Avalonia.Thickness(8 + 160, 4, 8, 4), ArchivePage.ReaderPostIndentMargin(16, 400));      // 40 % of the width.
    }

    [Fact]
    public void RowsDefaultToTodaysViewAndHiddenMediaReadsAsTextOnly()
    {
        ArchiveAttachmentRow attachment = new(new CapturedAttachment(0, "m", OriginalFileName: "a.png"));
        ArchivePostRow plain = new("1", "", "text", new[] { attachment });
        Assert.False(plain.IsTextOnly || plain.IsStub || plain.IsRevealed || plain.IsHighlighted || plain.MediaHidden || plain.SuppressReplyToggle);
        Assert.True(plain.ShowsMedia);
        Assert.False(plain.HasMediaHiddenLine);
        Assert.Equal(0, plain.Depth);
        Assert.Equal(string.Empty, plain.DepthLabel);
        ArchivePostRow hidden = plain with { MediaHidden = true };
        Assert.True(hidden.IsTextOnly && hidden.HasMediaHiddenLine && !hidden.ShowsMedia);
        ArchivePostRow threaded = new("2", "", "text", Array.Empty<ArchiveAttachmentRow>()) { SuppressReplyToggle = true, Depth = 2, ParentId = "1" };
        threaded.SetBacklinkCount(3);
        Assert.False(threaded.HasReplyToggle);
        Assert.NotEmpty(threaded.DepthLabel);
        ArchivePostRow counted = new("3", "", "text", Array.Empty<ArchiveAttachmentRow>());
        counted.SetBacklinkCount(3);
        Assert.True(counted.HasReplyToggle);
    }
}
