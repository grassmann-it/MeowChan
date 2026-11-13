using MeowChan.Archive;

namespace MeowChan.Tests;

// The reader asks for a post's position per scroll event and per quote: one lookup table per immutable read, not a scan.
public sealed class ArchivedThreadViewIndexTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 13, 8, 0, 0, TimeSpan.Zero);

    private static ArchivedThreadView View(IEnumerable<CapturedPost> posts, bool? decimalOrder = true)
    {
        ArchiveThreadSummary summary = new(new ResourceId("4chan", "g", "100"), "https://fixture.invalid/thread", "Synthetic", "/synthetic/root",
            MeowChan.Interfaces.ThreadSourceState.NotFound, false, At, At, 1, 11);
        return new ArchivedThreadView(summary, posts, ["/synthetic/root"], decimalOrder);
    }

    private static CapturedPost Post(int number) => new(number.ToString(), number, PostFieldPresence.All, At, Html: "<p>" + number + "</p>");

    [Fact]
    public void FindPostIndexAgreesWithThePostOrderAndReportsMissingPosts()
    {
        ArchivedThreadView view = View(Enumerable.Range(100, 50).Reverse().Select(Post)); // Stored out of order; the view sorts.
        IReadOnlyList<CapturedPost> posts = view.GetPosts(0, 100);
        for (int index = 0; index < posts.Count; index++) Assert.Equal(index, view.FindPostIndex(posts[index].PostId));
        Assert.Equal(-1, view.FindPostIndex("999"));
        Assert.Equal(-1, view.FindPostIndex(string.Empty));
        Assert.Equal(-1, view.FindPostIndex("0100 ")); // Ordinal, exact.
    }

    [Fact]
    public void ManyLookupsOnALargeThreadStayCheap()
    {
        ArchivedThreadView view = View(Enumerable.Range(1, 20000).Select(Post));
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        long sum = 0;
        for (int round = 0; round < 50; round++)
            for (int number = 1; number <= 20000; number += 7) sum += view.FindPostIndex(number.ToString());
        watch.Stop();
        Assert.True(sum > 0);
        // 50 * ~2900 lookups; a linear scan would be in the billions of comparisons.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), "Lookups took " + watch.Elapsed);
    }

    [Fact]
    public void GetPostsReturnsTheRequestedWindow()
    {
        ArchivedThreadView view = View(Enumerable.Range(1, 120).Select(Post));
        Assert.Equal(50, view.GetPosts(0, 50).Count);
        Assert.Equal("51", view.GetPosts(50, 50)[0].PostId);
        Assert.Equal(20, view.GetPosts(100, 50).Count);
        Assert.Empty(view.GetPosts(120, 50));
        Assert.Empty(view.GetPosts(500, 50));
    }

    [Fact]
    public void ConcurrentFirstLookupsAgree()
    {
        ArchivedThreadView view = View(Enumerable.Range(1, 5000).Select(Post));
        int[] results = new int[8];
        Parallel.For(0, results.Length, worker => results[worker] = view.FindPostIndex("4321"));
        Assert.All(results, result => Assert.Equal(4320, result));
    }
}
