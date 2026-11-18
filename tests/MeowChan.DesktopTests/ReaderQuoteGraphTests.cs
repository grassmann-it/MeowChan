using MeowChan;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

// Reader foundation (plan P0): who quotes whom inside one thread read, shared by post filters (P2) and the conversation (P4).
public sealed class ReaderQuoteGraphTests
{
    private static string Quote(string target, string? path = null) =>
        "<a href=\"" + (path ?? "") + "#p" + target + "\" class=\"quotelink\">&gt;&gt;" + target + "</a> ";

    private static ArchivedThreadView View(string url, params (string Id, string Html)[] posts) => new(
        new ArchiveThreadSummary(new ResourceId("4chan", "g", "100"), url, "", "",
            ThreadSourceState.Archived, true, DateTimeOffset.UnixEpoch, null, posts.Length, 3),
        posts.Select((post, index) => new CapturedPost(post.Id, index, PostFieldPresence.All, Html: post.Html)).ToArray(),
        decimalPostOrder: true);

    [Fact]
    public void OnlyCapturedTargetsInTheSameThreadWithoutSelfReferencesOrDuplicates()
    {
        ArchivedThreadView view = View("https://boards.4chan.org/g/thread/100",
            ("100", "opening post"),
            ("101", Quote("100") + Quote("101") + Quote("100") + Quote("999") + Quote("201", "/g/thread/200")),
            ("102", Quote("101") + Quote("100")),
            ("103", "<a href=\"https://example.org/#p100\">&gt;&gt;100</a> external, " + Quote("102")));
        ReaderQuoteGraph graph = ReaderQuoteGraph.Build(view, CancellationToken.None);
        Assert.True(graph.Matches(view));
        Assert.Empty(graph.TargetsOf(0).ToArray());
        Assert.Equal(new[] { 0 }, graph.TargetsOf(1).ToArray());      // Not itself, not twice, not uncaptured, not another thread.
        Assert.Equal(new[] { 0, 1 }, graph.TargetsOf(2).ToArray());   // Sorted by thread index.
        Assert.Equal(new[] { 2 }, graph.TargetsOf(3).ToArray());      // An external page is no target.
        Assert.Equal(new[] { 1, 2 }, graph.RepliesTo(0).ToArray());
        Assert.Equal(new[] { 2 }, graph.RepliesTo(1).ToArray());
        Assert.Equal(new[] { 3 }, graph.RepliesTo(2).ToArray());
        Assert.Empty(graph.RepliesTo(3).ToArray());
        Assert.Equal(4, graph.EdgeCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => graph.TargetsOf(4).ToArray());
    }

    [Fact]
    public void AThreadWithoutAnHttpAddressHasNoReferences()
    {
        ArchivedThreadView view = View("", ("100", "x"), ("101", Quote("100")));
        ReaderQuoteGraph graph = ReaderQuoteGraph.Build(view, CancellationToken.None);
        Assert.Equal(0, graph.EdgeCount);
        Assert.Empty(graph.RepliesTo(0).ToArray());
    }

    [Fact]
    public void BuildingObservesCancellation()
    {
        ArchivedThreadView view = View("https://boards.4chan.org/g/thread/100", ("100", "x"));
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => ReaderQuoteGraph.Build(view, cancelled.Token));
    }
}
