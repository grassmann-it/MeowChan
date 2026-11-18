using System;
using System.Threading;

namespace MeowChan.Desktop;

/// <summary>
/// The quoted targets of one post by thread index (earlier or later posts, unsorted, duplicates and out-of-range values
/// tolerated). The reader foundation's <c>ReaderQuoteGraph.TargetsOf</c> fits this shape directly.
/// </summary>
internal delegate ReadOnlySpan<int> ReaderQuoteTargets(int threadIndex);

/// <summary>
/// One post in the conversation order. <see cref="Depth"/> is the indentation level already capped to the depth limit;
/// <see cref="ParentIndex"/> is the real reply parent (thread index, -1 for the root level) and is not affected by the cap.
/// Same shape as the foundation's order entry so the view can map it one to one.
/// </summary>
internal readonly record struct ReaderConversationEntry(int ThreadIndex, int Depth, int ParentIndex);

/// <summary>
/// The conversation view's tree rule (as in 4chan X threading), as a pure function over the post count and the quote
/// references. A post becomes a child of the most recent earlier post it quotes (the opening post never counts as a
/// parent), but only when every earlier non-opening post it quotes lies on that parent's line of ancestors. A post that
/// quotes two unrelated branches stays at the root level. Quotes of the opening post, of itself and of later posts are
/// ignored. The result is a depth-first order with children in thread order: every thread index exactly once.
/// </summary>
internal static class ReaderConversationTree
{
    /// <summary>Indentation levels below the root level (default).</summary>
    internal const int DepthDefault = 6;
    /// <summary>Smallest accepted depth limit.</summary>
    internal const int DepthMinimum = 1;
    /// <summary>Largest accepted depth limit (screen width bounds what is readable).</summary>
    internal const int DepthMaximum = 16;

    /// <summary>Clamps a depth limit to <see cref="DepthMinimum"/>..<see cref="DepthMaximum"/>.</summary>
    internal static int ClampDepth(int depth) => Math.Clamp(depth, DepthMinimum, DepthMaximum);

    /// <summary>
    /// Orders <paramref name="postCount"/> posts as a conversation. <paramref name="openingPostIndex"/> is the thread index
    /// of the opening post, or any out-of-range value (such as -1) when there is none. Deeper posts than
    /// <paramref name="maxDepth"/> (clamped to the documented bounds) stay on the last level. Linear in posts plus quote
    /// references, with a logarithmic ancestor check; no recursion, so a chain of any length is fine.
    /// </summary>
    internal static ReaderConversationEntry[] Order(int postCount, int openingPostIndex, ReaderQuoteTargets targetsOf, int maxDepth,
        CancellationToken token = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(postCount);
        ArgumentNullException.ThrowIfNull(targetsOf);
        int[] parent = Parents(postCount, openingPostIndex, targetsOf, out int[] depth, token);
        int limit = ClampDepth(maxDepth);

        // Children as linked lists in thread order; slot postCount is the virtual root holding the root-level posts.
        int virtualRoot = postCount;
        int[] firstChild = new int[postCount + 1], nextSibling = new int[postCount], lastChild = new int[postCount + 1];
        Array.Fill(firstChild, -1);
        Array.Fill(lastChild, -1);
        for (int index = 0; index < postCount; index++)
        {
            int owner = parent[index] < 0 ? virtualRoot : parent[index];
            nextSibling[index] = -1;
            if (lastChild[owner] < 0) firstChild[owner] = index; else nextSibling[lastChild[owner]] = index;
            lastChild[owner] = index;
        }

        // Preorder without a stack: descend, else go to the next sibling, else climb until one exists.
        ReaderConversationEntry[] result = new ReaderConversationEntry[postCount];
        int node = firstChild[virtualRoot];
        for (int position = 0; position < postCount; position++)
        {
            if ((position & 255) == 0) token.ThrowIfCancellationRequested();
            result[position] = new ReaderConversationEntry(node, Math.Min(depth[node], limit), parent[node]);
            if (firstChild[node] >= 0) { node = firstChild[node]; continue; }
            while (node >= 0 && nextSibling[node] < 0) node = parent[node];
            if (node >= 0) node = nextSibling[node];
        }
        return result;
    }

    /// <summary>
    /// The reply parent of every post (thread index, -1 = root level) under the rule above, uncapped. Exposed for callers
    /// and tests that need the tree without the order.
    /// </summary>
    internal static int[] Parents(int postCount, int openingPostIndex, ReaderQuoteTargets targetsOf, CancellationToken token = default)
        => Parents(postCount, openingPostIndex, targetsOf, out _, token);

    private static int[] Parents(int postCount, int openingPostIndex, ReaderQuoteTargets targetsOf, out int[] depth, CancellationToken token)
    {
        int[] parent = new int[postCount];
        depth = new int[postCount];
        // Skew-binary jump pointers: the ancestor at a given depth is reached in O(log n) steps with O(1) memory per post.
        int[] jump = new int[postCount];
        for (int index = 0; index < postCount; index++)
        {
            if ((index & 255) == 0) token.ThrowIfCancellationRequested();
            int candidate = -1;
            ReadOnlySpan<int> targets = index == openingPostIndex ? default : targetsOf(index);
            foreach (int target in targets)
                if (target > candidate && target < index && target != openingPostIndex) candidate = target;
            if (candidate >= 0)
            {
                foreach (int target in targets)
                {
                    if (target < 0 || target >= index || target == openingPostIndex || target == candidate) continue;
                    if (!IsAncestor(target, candidate, parent, depth, jump)) { candidate = -1; break; }
                }
            }
            parent[index] = candidate;
            if (candidate < 0) { depth[index] = 0; jump[index] = index; continue; }
            depth[index] = depth[candidate] + 1;
            int up = jump[candidate];
            jump[index] = depth[candidate] - depth[up] == depth[up] - depth[jump[up]] ? jump[up] : candidate;
        }
        return parent;
    }

    /// <summary>Whether <paramref name="ancestor"/> is on the line from <paramref name="node"/> up to its root (inclusive).</summary>
    private static bool IsAncestor(int ancestor, int node, int[] parent, int[] depth, int[] jump)
    {
        int wanted = depth[ancestor];
        if (wanted > depth[node]) return false;
        while (depth[node] > wanted) node = depth[jump[node]] >= wanted ? jump[node] : parent[node];
        return node == ancestor;
    }
}
