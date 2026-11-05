using MeowChan;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

public sealed class ReaderThreadFindIndexTests
{
    private static ArchivedThreadView View(params CapturedPost[] posts) => new(
        new ArchiveThreadSummary(new ResourceId("synthetic", "b", "404"), "", "", "",
            ThreadSourceState.NotFound, true, DateTimeOffset.UnixEpoch, null, posts.Length, 7),
        posts, decimalPostOrder: false);
    private static CapturedPost Post(string id, long order, string body, string? subject = null) =>
        new(id, order, PostFieldPresence.All, Subject: subject, Html: body);

    [Fact]
    public void Offline404SearchesAllPostsInArchiveOrderNotViewport()
    {
        var view = View(Enumerable.Range(0, 650).Reverse()
            .Select(i => Post("p" + i, i, i == 649 ? "Needle" : "other")).ToArray());
        var result = ReaderThreadFindIndex.Create(view, "needle");
        var hit = Assert.Single(result.Hits);
        Assert.Equal("p649", result.Texts[hit.TextIndex].PostId);
        Assert.Equal(649, result.Texts[hit.TextIndex].PostIndex);
        Assert.Equal(650, result.PostsExamined);
        Assert.Equal(ReaderFindTruncation.None, result.Truncation);
        Assert.True(result.Matches(view.Summary.Id, 7));
        Assert.False(result.Matches(view.Summary.Id, 8));
        Assert.False(result.Matches(view.Summary.Id with { Source = "other" }, 7));
        Assert.False(result.Matches(view.Summary.Id with { Board = "other" }, 7));
        Assert.False(result.Matches(view.Summary.Id with { Thread = "other" }, 7));
    }

    [Fact]
    public void SubjectIsLiteralBodyIsReaderPlaintextAndActiveContentsDoNotMatch()
    {
        var view = View(Post("a", 0, "<script>hidden</script><!--hidden--><style>hidden</style><b>AA</b> aa", "AA"));
        var result = ReaderThreadFindIndex.Create(view, "aa");
        Assert.Equal(3, result.Hits.Count);
        Assert.Equal(ReaderFindField.Subject, result.Texts[result.Hits[0].TextIndex].Field);
        Assert.Equal(ReaderFindField.Body, result.Texts[result.Hits[1].TextIndex].Field);
        Assert.Equal(new[] { 0, 0, 3 }, result.Hits.Select(h => h.Offset));
        Assert.Empty(ReaderThreadFindIndex.Create(view, "hidden").Hits);
        Assert.Empty(ReaderThreadFindIndex.Create(view, "<b>").Hits);
        Assert.Empty(ReaderThreadFindIndex.Create(view, "missing").Hits);
    }

    [Fact]
    public void OffsetsAreUtf16InMaterializedTextWithCrLfAndEntities()
    {
        string html = "<p>😀\r\nÄ &amp; ä</p>";
        var result = ReaderThreadFindIndex.Create(View(Post("a", 0, html)), "ä");
        Assert.Equal(new[] { 3, 7 }, result.Hits.Select(h => h.Offset));
        foreach (var hit in result.Hits)
        {
            string text = result.Texts[hit.TextIndex].Text;
            Assert.Equal(ArchiveText.PlainText(html), text);
            Assert.True(string.Equals("ä", text.Substring(hit.Offset, hit.Length), StringComparison.OrdinalIgnoreCase));
        }
        Assert.Empty(ReaderThreadFindIndex.Create(View(Post("a", 0, "é")), "e\u0301").Hits);
    }

    [Fact]
    public void DuplicateTextRemainsDistinctAndOverlappingMatchesAreStable()
    {
        var result = ReaderThreadFindIndex.Create(View(Post("b", 1, "aaa"), Post("a", 0, "aaa")), "aa");
        Assert.Equal(new[] { "a", "a", "b", "b" }, result.Hits.Select(h => result.Texts[h.TextIndex].PostId));
        Assert.Equal(new[] { 0, 1, 0, 1 }, result.Hits.Select(h => h.Offset));
    }

    [Fact]
    public void BudgetsExposeIncompleteSearchWithoutCuttingHtml()
    {
        var view = View(Post("a", 0, "aaa"), Post("b", 1, "aaa"));
        Assert.True(ReaderThreadFindIndex.Create(view, "aa", new(MaxPosts: 1)).Truncation.HasFlag(ReaderFindTruncation.Posts));
        var hits = ReaderThreadFindIndex.Create(view, "aa", new(MaxHits: 1));
        Assert.Single(hits.Hits);
        Assert.True(hits.Truncation.HasFlag(ReaderFindTruncation.Hits));
        var text = ReaderThreadFindIndex.Create(view, "aa", new(MaxTextBytes: 5));
        Assert.Empty(text.Hits);
        Assert.True(text.Truncation.HasFlag(ReaderFindTruncation.TextBytes));
        Assert.InRange(text.TextBytes, 0L, 5L);
        var exact = ReaderThreadFindIndex.Create(View(Post("a", 0, "aaa")), "aa", new(MaxHits: 2, MaxTextBytes: 12));
        Assert.Equal(ReaderFindTruncation.None, exact.Truncation);
        Assert.Equal(12L, exact.TextBytes);
    }

    [Fact]
    public void CancellationAndInputBoundsDoNotReturnPartialSuccess()
    {
        var view = View(Post("a", 0, "x"));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => ReaderThreadFindIndex.Create(view, "x", cancellationToken: cancelled.Token));
        Assert.Throws<OperationCanceledException>(() => ReaderThreadFindIndex.Create(view, "", cancellationToken: cancelled.Token));
        Assert.Empty(ReaderThreadFindIndex.Create(view, "").Hits);
        Assert.Empty(ReaderThreadFindIndex.Create(view, new string('x', 512)).Hits);
        Assert.Throws<ArgumentOutOfRangeException>(() => ReaderThreadFindIndex.Create(view, new string('x', 513)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReaderThreadFindIndex.Create(view, "x", new(MaxPosts: 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReaderThreadFindIndex.Create(view, "x", new(MaxHits: 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReaderThreadFindIndex.Create(view, "x", new(MaxTextBytes: 0)));
    }

    [Fact]
    public void NavigationIsPureBoundedAndWrapIsExplicit()
    {
        Assert.Equal(-1, ReaderThreadFindIndex.Next(-1, 0));
        Assert.Equal(0, ReaderThreadFindIndex.Next(-1, 3));
        Assert.Equal(2, ReaderThreadFindIndex.Previous(-1, 3));
        Assert.Equal(0, ReaderThreadFindIndex.Next(2, 3));
        Assert.Equal(2, ReaderThreadFindIndex.Previous(0, 3));
        Assert.Equal(2, ReaderThreadFindIndex.Next(2, 3, false));
        Assert.Equal(0, ReaderThreadFindIndex.Previous(0, 3, false));
        Assert.Equal(0, ReaderThreadFindIndex.Next(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReaderThreadFindIndex.Next(3, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReaderThreadFindIndex.Previous(-2, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReaderThreadFindIndex.Next(-1, -1));
    }
}
