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
