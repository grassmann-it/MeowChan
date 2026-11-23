using System.Threading;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

public sealed class ReaderConversationOrderingTests
{
    private static readonly ResourceId Thread = new("4chan", "g", "700");

    [Fact]
    public void AdapterUsesTheOpeningPostAndClampsTheSessionDepth()
    {
        ReaderViewState state = new() { Conversation = true };
        ReaderConversationOrdering ordering = new(state, int.MaxValue);
        Assert.Equal(ReaderConversationTree.DepthMaximum, ordering.Depth);
        Assert.Equal(ReaderConversationTree.DepthMaximum, ordering.Revision);
        ordering.Depth = int.MinValue;
        Assert.Equal(ReaderConversationTree.DepthMinimum, ordering.Depth);
    }

    [Fact]
    public void AdapterMapsThePureTreeToTheProjectionOrder()
    {
        ArchivedThreadView view = SyntheticThread();
        ReaderQuoteGraph graph = ReaderQuoteGraph.Build(view, CancellationToken.None);
        ReaderConversationOrdering ordering = new(new ReaderViewState { Conversation = true }, 6);

        ReaderOrderEntry[] result = ordering.Order(view, graph, CancellationToken.None);

        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6 }, result.Select(entry => entry.ThreadIndex));
        Assert.Equal(new[] { 0, 0, 1, 2, 1, 2, 0 }, result.Select(entry => entry.Depth));
        Assert.Equal(new[] { -1, -1, 1, 2, 1, 4, -1 }, result.Select(entry => entry.ParentIndex));
        Assert.Throws<ArgumentException>(() => ordering.Order(view, ReaderQuoteGraph.Build(OtherThread(), CancellationToken.None), CancellationToken.None));
    }

    [Fact]
    public void ParsedQuotesReorderPostsWithoutConfusingThreadIndicesAndPositions()
    {
        ArchivedThreadView view = BoundaryThread();
        ReaderConversationOrdering ordering = new(new ReaderViewState { Conversation = true });
        ReaderQuoteGraph graph = ReaderQuoteGraph.Build(view, CancellationToken.None);

        ReaderOrderEntry[] result = ordering.Order(view, graph, CancellationToken.None);

        // 701 <-> 703 is a quote cycle; only the backward edge can become a parent.
        // Duplicate/self/dead links and cross-thread/board links to a captured ID cannot create extra parents.
        Assert.Equal(new[] { 0, 1, 3, 4, 7, 2, 6, 5, 8 }, result.Select(entry => entry.ThreadIndex));
        Assert.Equal(new[] { 0, 0, 1, 2, 1, 0, 1, 0, 0 }, result.Select(entry => entry.Depth));
        Assert.Equal(new[] { -1, -1, 1, 3, 1, -1, 2, -1, -1 }, result.Select(entry => entry.ParentIndex));
        Assert.Equal(new[] { 1 }, graph.TargetsOf(3).ToArray());
        Assert.Empty(graph.TargetsOf(8).ToArray());
        Assert.Equal(Enumerable.Range(0, view.PostCount), result.Select(entry => entry.ThreadIndex).Order());
    }

    [Fact]
    public void MissingOpeningPostDoesNotTurnTheFirstRetainedDeletedPostIntoTheOpeningPost()
    {
        // Deleted but retained is not the same as absent: 701 remains a valid parent; 799 does not.
        ArchivedThreadView view = MakeThread(
        [
            new("701", 0, PostFieldPresence.All, Html: Quote("703"), IsDeleted: true),
            new("702", 1, PostFieldPresence.All),
            new("703", 2, PostFieldPresence.All, Html: Quote("701") + Quote("799")),
            new("704", 3, PostFieldPresence.All, Html: Quote("701"))
        ]);
        ReaderConversationOrdering ordering = new(new ReaderViewState { Conversation = true });

        ReaderOrderEntry[] result = ordering.Order(view, ReaderQuoteGraph.Build(view, CancellationToken.None), CancellationToken.None);

        Assert.Equal(-1, view.FindPostIndex("700"));
        Assert.True(view.Posts[0].IsDeleted);
        Assert.Equal(new[] { 0, 2, 3, 1 }, result.Select(entry => entry.ThreadIndex));
        Assert.Equal(new[] { -1, 0, 0, -1 }, result.Select(entry => entry.ParentIndex));
        Assert.Equal(new[] { 0, 1, 1, 0 }, result.Select(entry => entry.Depth));
    }

    [Theory]
    [InlineData(true, 1, -1)]
    [InlineData(false, 2, 1)]
    public void FilteredParentsKeepTheRealAdapterOrderAndRevealByPostId(
        bool hideParent, int expectedDepth, int expectedParent)
    {
        ReaderPostVisibility visibility = hideParent ? ReaderPostVisibility.Hidden : ReaderPostVisibility.Stub;
        ArchivedThreadView view = BoundaryThread();
        ReaderConversationOrdering ordering = new(new ReaderViewState { Conversation = true });
        IReaderPostClassifier[] classifiers = [new BoundaryClassifier(visibility)];
        ReaderPostProjection projection = ReaderPostProjection.Build(view, null, classifiers, ordering,
            new HashSet<string>(), CancellationToken.None);

        Assert.False(projection.OrderFellBack);
        Assert.Equal(visibility == ReaderPostVisibility.Hidden ? 1 : 0, projection.HiddenCount);
        // A stub parent adds its own stub to the always-stubbed child 703; a hidden parent leaves only 703.
        Assert.Equal(visibility == ReaderPostVisibility.Stub ? 2 : 1, projection.StubCount);
        // 703 stays a stub parent; 704 is not a child of the previous displayed row by accident.
        ReaderPostEntry descendant = projection[projection.PositionOf("704")];
        Assert.Equal((expectedDepth, 3), (descendant.Depth, descendant.ParentIndex));
        ReaderPostEntry stub = projection[projection.PositionOf("703")];
        Assert.Equal((expectedDepth - 1, expectedParent), (stub.Depth, stub.ParentIndex));
        Assert.Equal(ReaderPostVisibility.Stub, stub.Verdict.Visibility);
        Assert.Equal(ReaderPostMarks.Highlight, projection[projection.PositionOf("707")].Verdict.Marks);
        Assert.Equal(projection.PositionOf(hideParent ? "702" : "701"), projection.NearestPosition("701"));
        Assert.Equal(hideParent ? -1 : 1, projection.PositionOf("701"));
        Assert.Equal(new[] { "700", "701", "703", "704", "707", "702", "706", "705", "708" }
            .Where(id => !hideParent || id != "701"), Entries(projection).Select(entry => entry.Post.PostId));
        ReaderPostProjection chronological = ReaderPostProjection.Build(view, null, classifiers, null,
            new HashSet<string>(), CancellationToken.None);
        Assert.NotEqual(chronological.PositionOf("707"), projection.PositionOf("707"));
        Assert.Equal("707", chronological[chronological.PositionOf("707")].Post.PostId);
        Assert.Equal(ReaderPostMarks.Highlight, chronological[chronological.PositionOf("707")].Verdict.Marks);

        ReaderPostProjection revealed = projection.WithRevealed(new HashSet<string> { "701" });
        Assert.Equal(new[] { "700", "701", "703", "704", "707", "702", "706", "705", "708" },
            Entries(revealed).Select(entry => entry.Post.PostId));
        Assert.Equal((2, 3), (revealed[revealed.PositionOf("704")].Depth, revealed[revealed.PositionOf("704")].ParentIndex));
        Assert.Equal(ReaderPostMarks.Revealed, revealed[revealed.PositionOf("701")].Verdict.Marks);
        // Revealing is a new projection, not a mutation of the previous view or the captured posts.
        Assert.True(projection.IsConcealed("701"));
        Assert.Equal(ReaderPostMarks.Highlight, revealed[revealed.PositionOf("707")].Verdict.Marks);
        Assert.Same(view.Posts[7], chronological[chronological.PositionOf("707")].Post);
        Assert.Same(view.Posts[7], projection[projection.PositionOf("707")].Post);
        Assert.Same(view.Posts[7], revealed[revealed.PositionOf("707")].Post);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(16)]
    public void CappedConversationOrderRoundTripsPostIdentityAcrossSessionViewChanges(int depth)
    {
        const int count = 603; // Two interleaved reply chains across a large thread.
        ArchivedThreadView view = MakeThread(Enumerable.Range(0, count).Select(index =>
            new CapturedPost((700 + index).ToString(), index, PostFieldPresence.All,
                Html: index < 3 ? "" : Quote((700 + index - 2).ToString()))).ToArray());
        ReaderViewState state = new();
        ReaderConversationOrdering ordering = new(state, depth);
        Assert.False(ordering.IsActive(Thread));
        ReaderPostProjection chronological = ReaderPostProjection.Identity(view);
        state.Conversation = true;
        Assert.True(ordering.IsActive(Thread));
        ReaderPostProjection conversation = ReaderPostProjection.Build(view, null, [], ordering,
            new HashSet<string>(), CancellationToken.None);
        int[] expected = new[] { 0 }.Concat(Enumerable.Range(1, count - 1).Where(index => index % 2 == 1))
            .Concat(Enumerable.Range(1, count - 1).Where(index => index % 2 == 0)).ToArray();

        Assert.False(conversation.OrderFellBack);
        Assert.Equal(expected, Entries(conversation).Select(entry => entry.ThreadIndex));
        Assert.NotEqual(chronological.PositionOf("702"), conversation.PositionOf("702"));
        for (int position = 0; position < count; position++)
        {
            ReaderPostEntry entry = conversation[position];
            Assert.Equal(position, conversation.PositionOf(entry.Post.PostId));
            Assert.Equal(entry.ThreadIndex, chronological.PositionOf(entry.Post.PostId));
            Assert.Equal(entry.ThreadIndex, view.FindPostIndex(entry.Post.PostId));
            Assert.Equal(entry.ThreadIndex < 3 ? -1 : entry.ThreadIndex - 2, entry.ParentIndex);
            Assert.Equal(Math.Min(depth, Math.Max(0, (entry.ThreadIndex - 1) / 2)), entry.Depth);
        }
        state.Conversation = false;
        Assert.False(ordering.IsActive(Thread));
        ReaderPostProjection restored = ReaderPostProjection.Build(view, null, [], ordering.IsActive(Thread) ? ordering : null,
            new HashSet<string>(), CancellationToken.None);
        Assert.True(restored.IsIdentity);
        Assert.False(new ReaderViewState().Conversation); // New in-memory sessions start with the switch off.
        Assert.Equal(Enumerable.Range(0, count), Entries(restored).Select(entry => entry.ThreadIndex));
        Assert.Equal(chronological.PositionOf("702"), restored.PositionOf("702"));
    }

    private static IEnumerable<ReaderPostEntry> Entries(ReaderPostProjection projection) =>
        Enumerable.Range(0, projection.Count).Select(index => projection[index]);

    private sealed class BoundaryClassifier(ReaderPostVisibility parentVisibility) : IReaderPostClassifier
    {
        public bool IsActive(ResourceId thread) => true;
        public bool UsesQuoteGraph => false;
        public long Revision => 1;
        public void Classify(ArchivedThreadView thread, ReaderQuoteGraph? graph, ReaderPostVerdict[] verdicts, CancellationToken token)
        {
            verdicts[1] = new(parentVisibility, ReaderPostMarks.None, "parent rule");
            verdicts[3] = new(ReaderPostVisibility.Stub, ReaderPostMarks.None, "child rule");
            verdicts[7] = new(ReaderPostVisibility.Shown, ReaderPostMarks.Highlight, "marked post");
        }
    }

    private static string Quote(string target, string path = "") =>
        "<a href=\"" + path + "#p" + target + "\" class=\"quotelink\">&gt;&gt;" + target + "</a>";

    private static ArchivedThreadView BoundaryThread() => MakeThread(new[]
    {
        "", Quote("703"), "",
        Quote("701") + Quote("701") + Quote("703") + Quote("999") + Quote("702", "/g/thread/900") + Quote("702", "/x/thread/700"),
        Quote("703") + Quote("701") + Quote("700"), Quote("702") + Quote("704"), Quote("702"), Quote("701"),
        Quote("702", "/g/thread/900")
    }.Select((html, index) => new CapturedPost((700 + index).ToString(), index, PostFieldPresence.All, Html: html)).ToArray());

    private static ArchivedThreadView MakeThread(CapturedPost[] posts) => new(
        new ArchiveThreadSummary(Thread, "https://boards.4chan.org/g/thread/700", "synthetic", "",
            ThreadSourceState.Archived, true, DateTimeOffset.UnixEpoch, null, posts.Length, 7),
        posts, decimalPostOrder: true);

    [Theory]
    [InlineData(900, 1, 20)]
    [InlineData(900, 6, 120)]
    [InlineData(900, 16, 320)]
    [InlineData(1440, 16, 320)]
    public void IndentationStaysInsideTheReadableColumn(int width, int depth, double expected)
    {
        Assert.Equal(expected, ReaderNavigationLayout.ConversationIndent(depth, width));
        Assert.InRange(ReaderNavigationLayout.ConversationIndent(depth, width), 0, width * 0.4);
    }

    private static ArchivedThreadView SyntheticThread() => Make(
        ["700", "701", "702", "703", "704", "705", "706"],
        ["", "", "701", "702", "701", "704", "702 704"]);

    private static ArchivedThreadView OtherThread() => Make(["800"], [""]);

    private static ArchivedThreadView Make(string[] ids, string[] quotes)
    {
        CapturedPost[] posts = ids.Select((id, index) => new CapturedPost(id, index, PostFieldPresence.All,
            Html: "<p>" + string.Join(" ", quotes[index].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(target =>
                "<a href=\"#p" + target + "\" class=\"quotelink\">&gt;&gt;" + target + "</a>")) + "</p>")).ToArray();
        ResourceId id = ids[0] == "700" ? Thread : new("4chan", "g", "800");
        return new ArchivedThreadView(new ArchiveThreadSummary(id, "https://boards.4chan.org/g/thread/" + id.Thread,
            "synthetic", "", ThreadSourceState.Archived, true, DateTimeOffset.UnixEpoch, null, posts.Length, 0), posts,
            decimalPostOrder: true);
    }
}
