using System.Diagnostics;
using MeowChan;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

// Conversation view (plan P4 part A): the tree rule as a pure function over post count and quote references.
public sealed class ReaderConversationTreeTests
{
    private static readonly int[] None = [];

    private static ReaderQuoteTargets Targets(params int[][] perPost) => index => index < perPost.Length ? perPost[index] : None;

    private static int[] Parents(int op, params int[][] perPost) => ReaderConversationTree.Parents(perPost.Length, op, Targets(perPost));

    private static ReaderConversationEntry[] Order(int op, int depth, params int[][] perPost)
        => ReaderConversationTree.Order(perPost.Length, op, Targets(perPost), depth);

    private static void AssertEveryIndexOnce(ReaderConversationEntry[] order, int count)
    {
        Assert.Equal(count, order.Length);
        Assert.Equal(Enumerable.Range(0, count), order.Select(entry => entry.ThreadIndex).Order());
    }

    [Fact]
    public void DepthLimitHasDocumentedBoundsAndClamps()
    {
        Assert.Equal(6, ReaderConversationTree.DepthDefault);
        Assert.Equal(1, ReaderConversationTree.DepthMinimum);
        Assert.Equal(16, ReaderConversationTree.DepthMaximum);
        Assert.Equal(1, ReaderConversationTree.ClampDepth(int.MinValue));
        Assert.Equal(1, ReaderConversationTree.ClampDepth(0));
        Assert.Equal(6, ReaderConversationTree.ClampDepth(6));
        Assert.Equal(16, ReaderConversationTree.ClampDepth(16));
        Assert.Equal(16, ReaderConversationTree.ClampDepth(17));
        Assert.Equal(16, ReaderConversationTree.ClampDepth(int.MaxValue));
    }

    [Fact]
    public void NoPostsAndASinglePost()
    {
        Assert.Empty(ReaderConversationTree.Order(0, 0, Targets(), 6));
        Assert.Empty(ReaderConversationTree.Order(0, -1, Targets(), 6));
        Assert.Equal([new ReaderConversationEntry(0, 0, -1)], ReaderConversationTree.Order(1, 0, Targets(), 6));
        Assert.Equal([new ReaderConversationEntry(0, 0, -1)], ReaderConversationTree.Order(1, -1, Targets([0]), 6));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReaderConversationTree.Order(-1, 0, Targets(), 6));
        Assert.Throws<ArgumentNullException>(() => ReaderConversationTree.Order(1, 0, null!, 6));
    }

    [Fact]
    public void PostsWithoutQuotesStayOnTheRootLevelInThreadOrder()
    {
        ReaderConversationEntry[] order = Order(0, 6, None, None, None, None);
        Assert.Equal(
        [
            new ReaderConversationEntry(0, 0, -1), new ReaderConversationEntry(1, 0, -1),
            new ReaderConversationEntry(2, 0, -1), new ReaderConversationEntry(3, 0, -1),
        ], order);
    }

    [Fact]
    public void ASingleLineOfRepliesNestsOneLevelPerPost()
    {
        // 0 OP, 1 quotes OP, 2 quotes 1, 3 quotes 2, 4 quotes 3.
        ReaderConversationEntry[] order = Order(0, 6, None, [0], [1], [2], [3]);
        Assert.Equal(
        [
            new ReaderConversationEntry(0, 0, -1), new ReaderConversationEntry(1, 0, -1), new ReaderConversationEntry(2, 1, 1),
            new ReaderConversationEntry(3, 2, 2), new ReaderConversationEntry(4, 3, 3),
        ], order);
    }

    [Fact]
    public void QuotingTheOpeningPostDoesNotCreateAParent()
    {
        Assert.Equal([-1, -1, -1, -1], Parents(0, None, [0], [0], [0]));
        // The opening post is ignored next to a real parent: 2 quotes OP and 1 and still nests under 1.
        Assert.Equal([-1, -1, 1], Parents(0, None, None, [0, 1]));
        // The opening post itself never gets a parent, whatever it quotes.
        Assert.Equal([-1, -1], Parents(0, [1, 0], None));
    }

    [Fact]
    public void WithoutAKnownOpeningPostEveryEarlierPostCanBeAParent()
    {
        Assert.Equal([-1, 0, 1], Parents(-1, None, [0], [1]));
        Assert.Equal([-1, 0, 1], Parents(99, None, [0], [1])); // Out of range: no opening post either.
    }

    [Fact]
    public void AnOpeningPostThatIsNotFirstIsStillExcluded()
    {
        // Partial capture: the opening post sits at index 1 (no earlier post is its parent, nobody nests under it).
        Assert.Equal([-1, -1, -1, 0], Parents(1, None, [0], [1], [0, 1]));
    }

    [Fact]
    public void QuotesOfLaterPostsAreIgnored()
    {
        // 1 quotes the later posts 2 and 3: root. 2 quotes 3 (later): root. 3 quotes 1 (earlier): child of 1.
        Assert.Equal([-1, -1, -1, 1], Parents(0, None, [2, 3], [3], [1]));
        // A forward quote beside a backward one does not matter: 2 quotes 1 and 3, parent is 1.
        Assert.Equal([-1, -1, 1, -1], Parents(0, None, None, [3, 1], None));
    }

    [Fact]
    public void SelfReferencesDuplicatesAndOutOfRangeValuesAreIgnored()
    {
        Assert.Equal([-1, -1, 1], Parents(0, None, [1, -5, 77, int.MaxValue, int.MinValue], [1, 1, 1, 1, 77, -1]));
        Assert.Equal([-1, -1, -1], Parents(0, None, [-1], [-3, 12]));
    }

    [Fact]
    public void UnsortedTargetsGiveTheSameResultAsSorted()
    {
        // 1, 2 (child of 1), 3 quotes 2 and 1: 1 is on 2's line -> parent 2.
        int[][] sorted = [None, None, [1], [1, 2]];
        int[][] shuffled = [None, None, [1], [2, 1, 2, 1]];
        Assert.Equal([-1, -1, 1, 2], Parents(0, sorted));
        Assert.Equal(Parents(0, sorted), Parents(0, shuffled));
    }

    [Fact]
    public void ThePostBecomesAChildOfTheMostRecentQuotedPostWhenAllOthersAreItsAncestors()
    {
        // 1 root, 2 under 1, 3 under 2, 4 quotes 1, 2 and 3: everything lies on 3's line -> parent 3.
        int[] parents = Parents(0, None, None, [1], [2], [1, 2, 3]);
        Assert.Equal([-1, -1, 1, 2, 3], parents);
        // 4 quotes only 1 (the root of the line): parent 1, not deeper.
        Assert.Equal([-1, -1, 1, 2, 1], Parents(0, None, None, [1], [2], [1]));
        // 4 quotes 3 and 1 (skipping 2): still on the line.
        Assert.Equal([-1, -1, 1, 2, 3], Parents(0, None, None, [1], [2], [3, 1]));
    }

    [Fact]
    public void QuotingTwoUnrelatedBranchesKeepsThePostAtTheRootLevel()
    {
        // 1 and 2 are independent roots (both quote only the OP); 3 quotes both.
        Assert.Equal([-1, -1, -1, -1], Parents(0, None, [0], [0], [1, 2]));
        // Sibling branches under one parent: 2 and 3 are children of 1; 4 quotes 2 and 3 -> root, not under 3.
        Assert.Equal([-1, -1, 1, 1, -1], Parents(0, None, None, [1], [1], [2, 3]));
        // The older of two quoted posts is an ancestor only if on the line: 4 quotes 3 and 2 where 2 is 3's sibling.
        Assert.Equal([-1, -1, 1, 1, -1], Parents(0, None, None, [1], [1], [3, 2]));
    }

    [Fact]
    public void ACousinOnAnotherBranchOfTheSameTreeDoesNotCount()
    {
        // 1 root; 2 and 3 under 1; 4 under 2; 5 quotes 3 and 4: 3 is not an ancestor of 4 -> root.
        Assert.Equal([-1, -1, 1, 1, 2, -1], Parents(0, None, None, [1], [1], [2], [3, 4]));
        // 5 quotes 4 and 1: 1 is an ancestor of 4 -> child of 4.
        Assert.Equal([-1, -1, 1, 1, 2, 4], Parents(0, None, None, [1], [1], [2], [4, 1]));
    }

    [Fact]
    public void CyclesInTheReferencesCannotLoopOrNest()
    {
        // 1 and 2 quote each other: only the backward quote counts, so 2 is a child of 1 and 1 stays a root.
        ReaderConversationEntry[] order = Order(0, 6, None, [2], [1]);
        Assert.Equal(
        [
            new ReaderConversationEntry(0, 0, -1), new ReaderConversationEntry(1, 0, -1), new ReaderConversationEntry(2, 1, 1),
        ], order);
        // A long cycle 1->2->3->4->1 collapses to a straight line (the closing quote points forward).
        int[] cycle = Parents(0, None, [4], [1], [2], [3]);
        Assert.Equal([-1, -1, 1, 2, 3], cycle);
        // Everybody quotes everybody: a single line in thread order.
        int[][] all = Enumerable.Range(0, 6).Select(_ => Enumerable.Range(0, 6).ToArray()).ToArray();
        Assert.Equal([-1, -1, 1, 2, 3, 4], ReaderConversationTree.Parents(6, 0, Targets(all)));
    }

    [Fact]
    public void DepthFirstOrderOfAMixedTree()
    {
        //  index: parent
        //  0 OP root
        //  1 root
        //  2 -> 1
        //  3 -> 2
        //  4 -> 1
        //  5 root
        //  6 -> 5
        //  7 -> 1
        ReaderConversationEntry[] order = Order(0, 6, None, None, [1], [2], [1], None, [5], [1]);
        Assert.Equal(
        [
            new ReaderConversationEntry(0, 0, -1),
            new ReaderConversationEntry(1, 0, -1),
            new ReaderConversationEntry(2, 1, 1),
            new ReaderConversationEntry(3, 2, 2),
            new ReaderConversationEntry(4, 1, 1),
            new ReaderConversationEntry(7, 1, 1),
            new ReaderConversationEntry(5, 0, -1),
            new ReaderConversationEntry(6, 1, 5),
        ], order);
    }

    [Fact]
    public void DepthIsCappedButTheParentStaysReal()
    {
        int[][] chain = [None, None, [1], [2], [3], [4], [5]]; // 1 root, 2..6 each under the previous one.
        ReaderConversationEntry[] capped = Order(0, 2, chain);
        Assert.Equal([0, 0, 1, 2, 2, 2, 2], capped.Select(entry => entry.Depth).ToArray());
        Assert.Equal([-1, -1, 1, 2, 3, 4, 5], capped.Select(entry => entry.ParentIndex).ToArray());
        Assert.Equal(Enumerable.Range(0, 7), capped.Select(entry => entry.ThreadIndex));
        // Below the limit nothing changes.
        Assert.Equal([0, 0, 1, 2, 3, 4, 5], Order(0, 16, chain).Select(entry => entry.Depth).ToArray());
        // A limit of 1 means one indentation level.
        Assert.Equal([0, 0, 1, 1, 1, 1, 1], Order(0, 1, chain).Select(entry => entry.Depth).ToArray());
    }

    [Theory]
    [InlineData(int.MinValue, 1)]
    [InlineData(0, 1)]
    [InlineData(-4, 1)]
    [InlineData(17, 16)]
    [InlineData(int.MaxValue, 16)]
    public void OutOfRangeDepthLimitsClampToTheBounds(int requested, int effective)
    {
        int[][] chain = Enumerable.Range(0, 40).Select(index => index < 2 ? None : new[] { index - 1 }).ToArray();
        int max = Order(0, requested, chain).Max(entry => entry.Depth);
        Assert.Equal(effective, max);
    }

    [Fact]
    public void ADeepLineKeepsAllItsDeeperPostsOnTheLastLevelInOrder()
    {
        int[][] chain = Enumerable.Range(0, 30).Select(index => index < 2 ? None : new[] { index - 1 }).ToArray();
        ReaderConversationEntry[] order = Order(0, 6, chain);
        for (int position = 0; position < order.Length; position++)
        {
            Assert.Equal(position, order[position].ThreadIndex);
            Assert.Equal(Math.Min(Math.Max(0, position - 1), 6), order[position].Depth);
        }
    }

    [Fact]
    public void ACancelledTokenStopsTheWork()
    {
        using CancellationTokenSource source = new();
        source.Cancel();
        int[][] chain = Enumerable.Range(0, 1000).Select(index => index == 0 ? None : new[] { index - 1 }).ToArray();
        Assert.Throws<OperationCanceledException>(() => ReaderConversationTree.Order(chain.Length, 0, Targets(chain), 6, source.Token));
        Assert.Throws<OperationCanceledException>(() => ReaderConversationTree.Parents(chain.Length, 0, Targets(chain), source.Token));
    }

    // The straightforward quadratic statement of the rule, used as the oracle for random graphs.
    private static int[] NaiveParents(int count, int op, int[][] targets)
    {
        int[] parent = new int[count];
        for (int index = 0; index < count; index++)
        {
            parent[index] = -1;
            if (index == op) continue;
            HashSet<int> earlier = targets[index].Where(t => t >= 0 && t < index && t != op).ToHashSet();
            if (earlier.Count == 0) continue;
            int candidate = earlier.Max();
            HashSet<int> line = new();
            for (int node = candidate; node >= 0; node = parent[node]) line.Add(node);
            if (earlier.All(line.Contains)) parent[index] = candidate;
        }
        return parent;
    }

    [Theory]
    [InlineData(1, 40, 3)]
    [InlineData(2, 200, 2)]
    [InlineData(3, 200, 6)]
    [InlineData(4, 600, 1)]
    [InlineData(5, 600, 4)]
    [InlineData(6, 1500, 3)]
    public void RandomGraphsAgreeWithTheNaiveRuleAndYieldAValidOrder(int seed, int count, int maxQuotes)
    {
        Random random = new(seed);
        int[][] targets = new int[count][];
        for (int index = 0; index < count; index++)
        {
            int quotes = random.Next(0, maxQuotes + 1);
            targets[index] = Enumerable.Range(0, quotes).Select(_ =>
            {
                int kind = random.Next(10);
                if (kind == 0) return random.Next(-3, count + 3);                          // Anything, including later and invalid.
                if (kind < 5) return Math.Max(0, index - 1 - random.Next(0, 4));           // Near predecessors: deep lines.
                return random.Next(0, Math.Max(1, index));                                  // Anywhere earlier.
            }).ToArray();
        }
        int op = seed % 2 == 0 ? 0 : -1;
        int[] expected = NaiveParents(count, op, targets);
        Assert.Equal(expected, ReaderConversationTree.Parents(count, op, Targets(targets)));

        ReaderConversationEntry[] order = ReaderConversationTree.Order(count, op, Targets(targets), 6);
        AssertEveryIndexOnce(order, count);
        int[] position = new int[count];
        for (int at = 0; at < order.Length; at++) position[order[at].ThreadIndex] = at;
        int[] trueDepth = new int[count];
        for (int index = 0; index < count; index++) trueDepth[index] = expected[index] < 0 ? 0 : trueDepth[expected[index]] + 1;
        foreach (ReaderConversationEntry entry in order)
        {
            Assert.Equal(expected[entry.ThreadIndex], entry.ParentIndex);
            Assert.Equal(Math.Min(trueDepth[entry.ThreadIndex], 6), entry.Depth);
            // A parent always comes first, and a subtree is one contiguous run right after its parent.
            if (entry.ParentIndex >= 0) Assert.True(position[entry.ParentIndex] < position[entry.ThreadIndex]);
        }
        // Subtree contiguity: every post sits after its parent with only descendants of that parent in between.
        for (int at = 1; at < order.Length; at++)
        {
            int current = order[at].ThreadIndex;
            int previous = order[at - 1].ThreadIndex;
            // The previous shown post is the parent, a descendant of the parent's earlier child, or (root level) anything.
            if (expected[current] < 0) continue;
            int node = previous;
            while (node >= 0 && node != expected[current]) node = expected[node];
            Assert.Equal(expected[current], node);
        }
    }

    [Fact]
    public void DeadAndCrossThreadReferencesFromARealThreadLeaveThePostAtTheRoot()
    {
        // The quote parser's output is what the reader's quote graph feeds in; dead and other-thread links are no targets.
        static string Link(string target, string? path = null) => "<a href=\"" + (path ?? "") + "#p" + target + "\" class=\"quotelink\">&gt;&gt;" + target + "</a> ";
        (string Id, string Html)[] source =
        [
            ("100", "opening post"),
            ("101", Link("100")),
            ("102", Link("101") + Link("999")),                            // 999 is not captured: dead.
            ("103", Link("201", "/g/thread/200")),                         // Another thread only.
            ("104", Link("103") + Link("201", "/g/thread/200") + Link("102")), // Cross-thread beside 103 and 102 (unrelated lines).
            ("105", Link("102") + Link("101") + Link("100")),
        ];
        ResourceId id = new("4chan", "g", "100");
        ArchivedThreadView view = new(
            new ArchiveThreadSummary(id, "https://boards.4chan.org/g/thread/100", "", "", ThreadSourceState.Archived, true,
                DateTimeOffset.UnixEpoch, null, source.Length, 3),
            source.Select((post, index) => new CapturedPost(post.Id, index, PostFieldPresence.All, Html: post.Html)).ToArray(),
            decimalPostOrder: true);
        int[][] targets = new int[view.PostCount][];
        for (int index = 0; index < targets.Length; index++)
        {
            List<int> found = [];
            foreach (ArchiveQuote quote in ArchiveQuoteParser.Parse(id, view.Summary.Url, view.Posts[index]))
                if (quote.Target is { } target && target.Thread == id && view.FindPostIndex(target.PostId) is var at and >= 0) found.Add(at);
            targets[index] = found.ToArray();
        }
        // 103 quotes only another thread; 104 quotes 103 and 102 (103 is not on 102's line) -> root.
        int[] parents = ReaderConversationTree.Parents(view.PostCount, 0, Targets(targets));
        Assert.Equal([-1, -1, 1, -1, -1, 2], parents);
    }

    [Fact]
    public void ALargeThreadWithAMixedShapeIsOrderedQuickly()
    {
        const int count = 10_000;
        Random random = new(42);
        int[][] targets = new int[count][];
        for (int index = 0; index < count; index++)
        {
            targets[index] = index < 2 ? None : random.Next(4) switch
            {
                0 => None,
                1 => [index - 1],
                2 => [random.Next(1, index), random.Next(1, index)],
                _ => [index - 1, Math.Max(1, index - 1 - random.Next(0, 20)), 0],
            };
        }
        ReaderQuoteTargets accessor = Targets(targets);
        ReaderConversationTree.Order(count, 0, accessor, 6); // Warm up the JIT.
        Stopwatch watch = Stopwatch.StartNew();
        ReaderConversationEntry[] order = ReaderConversationTree.Order(count, 0, accessor, 6);
        watch.Stop();
        AssertEveryIndexOnce(order, count);
        Assert.True(watch.ElapsedMilliseconds < 200, "10 000 posts took " + watch.ElapsedMilliseconds + " ms");
    }

    [Fact]
    public void WorstCaseShapesStayLinear()
    {
        // One endless line where every post also quotes a spread of its own ancestors, and a flat thread quoting the OP.
        const int count = 20_000;
        int[][] line = new int[count][];
        for (int index = 0; index < count; index++)
            line[index] = index < 2 ? None : [index - 1, 1, index / 2, index / 3 + 1];
        Stopwatch watch = Stopwatch.StartNew();
        ReaderConversationEntry[] ordered = ReaderConversationTree.Order(count, 0, Targets(line), ReaderConversationTree.DepthMaximum);
        watch.Stop();
        AssertEveryIndexOnce(ordered, count);
        Assert.Equal(count - 2, ordered.Count(entry => entry.ParentIndex >= 1)); // Everything but the OP and post 1 hangs on the line.
        Assert.Equal(ReaderConversationTree.DepthMaximum, ordered.Max(entry => entry.Depth));
        Assert.True(watch.ElapsedMilliseconds < 1000, "A 20 000 post line took " + watch.ElapsedMilliseconds + " ms");

        int[][] flat = Enumerable.Range(0, count).Select(index => index == 0 ? None : new[] { 0 }).ToArray();
        ReaderConversationEntry[] flatOrder = ReaderConversationTree.Order(count, 0, Targets(flat), 6);
        Assert.All(flatOrder, entry => Assert.Equal(0, entry.Depth));
        Assert.Equal(Enumerable.Range(0, count), flatOrder.Select(entry => entry.ThreadIndex));
    }

    [Fact]
    public void AVeryLongLineDoesNotOverflowTheStack()
    {
        const int count = 200_000;
        ReaderQuoteTargets line = index => index < 2 ? None : new[] { index - 1 };
        ReaderConversationEntry[] order = ReaderConversationTree.Order(count, 0, line, 6);
        Assert.Equal(count, order.Length);
        Assert.Equal(6, order[^1].Depth);
        Assert.Equal(count - 2, order[^1].ParentIndex);
    }
}
