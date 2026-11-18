using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using MeowChan.Archive;
using MeowChan.Interfaces;
using Serilog;

namespace MeowChan.Desktop;

// The reader's shared foundation (plan P0, section 3): one projection "thread -> shown order" that every page,
// navigation and reading-mark computation works against. P0's "Only posts with media", P2's post filters and P4's
// conversation view only contribute verdicts or an order; placeholders, indentation and revealing belong to P0.

/// <summary>How a view shows one post. A higher value wins when several classifiers judge the same post.</summary>
internal enum ReaderPostVisibility { Shown = 0, Stub = 1, Hidden = 2 }

/// <summary>Marks combined by OR. <see cref="Revealed"/> is set only by the projection (an explicit jump or Show).</summary>
[Flags] internal enum ReaderPostMarks { None = 0, Highlight = 1, Revealed = 2 }

internal readonly record struct ReaderPostVerdict(ReaderPostVisibility Visibility, ReaderPostMarks Marks, string? Reason);

/// <summary>Judges posts of one thread (P0 "Only posts with media", P2 post filters).</summary>
internal interface IReaderPostClassifier
{
    /// <summary>UI thread, cheap; false skips the classifier.</summary>
    bool IsActive(ResourceId thread);
    bool UsesQuoteGraph { get; }
    /// <summary>Changes whenever the inputs (rules, switches) change; part of the projection's cache key.</summary>
    long Revision { get; }
    /// <summary>Background thread, pure, cancellable. <paramref name="verdicts"/> is prefilled with Shown and has one
    /// slot per thread index (thread.PostCount).</summary>
    void Classify(ArchivedThreadView thread, ReaderQuoteGraph? graph, ReaderPostVerdict[] verdicts, CancellationToken token);
}

/// <summary>One post in a custom order. ParentIndex is a thread index, -1 for the root level.</summary>
internal readonly record struct ReaderOrderEntry(int ThreadIndex, int Depth, int ParentIndex);

/// <summary>A custom order of the thread (P4's conversation view; at most one is registered).</summary>
internal interface IReaderPostOrdering
{
    bool IsActive(ResourceId thread);
    /// <summary>Includes the depth limit.</summary>
    long Revision { get; }
    /// <summary>Every thread index exactly once; anything else falls back to chronological order (logged).</summary>
    ReaderOrderEntry[] Order(ArchivedThreadView thread, ReaderQuoteGraph graph, CancellationToken token);
}

/// <summary>One shown post: its thread index, the post, its indentation, its shown parent and the combined verdict.</summary>
internal readonly record struct ReaderPostEntry(int ThreadIndex, CapturedPost Post, int Depth, int ParentIndex, ReaderPostVerdict Verdict);

/// <summary>
/// An immutable projection of one thread read onto the shown order. <see cref="Identity"/> is today's view (all posts,
/// chronological) and allocates nothing per post. Positions are indices into the shown order; thread indices stay those
/// of <see cref="ArchivedThreadView"/>. A projection stays valid for every read of the same thread revision.
/// </summary>
internal sealed class ReaderPostProjection
{
    private static readonly IReadOnlySet<string> NoneRevealed = new HashSet<string>(StringComparer.Ordinal);
    private readonly IReadOnlyList<CapturedPost> _posts;
    private readonly ReaderPostEntry[]? _entries;     // null for the identity
    private readonly int[]? _positionOf;               // thread index -> position, -1 = hidden
    private readonly ReaderOrderEntry[]? _order;       // validated order, including hidden posts
    private readonly ReaderPostVerdict[]? _verdicts;   // combined classifier verdicts before revealing
    private readonly HashSet<string> _revealed;

    private ReaderPostProjection(ArchivedThreadView thread, ProjectionInputs inputs, HashSet<string> revealed,
        ReaderPostEntry[]? entries, int[]? positionOf, ReaderOrderEntry[]? order, ReaderPostVerdict[]? verdicts,
        int hiddenCount, int stubCount, bool orderFellBack)
    {
        Thread = thread;
        _posts = thread.Posts;
        Inputs = inputs;
        _revealed = revealed;
        _entries = entries;
        _positionOf = positionOf;
        _order = order;
        _verdicts = verdicts;
        HiddenCount = hiddenCount;
        StubCount = stubCount;
        OrderFellBack = orderFellBack;
        Key = ComputeKey(inputs, revealed);
    }

    /// <summary>Today's view: every post, chronological, nothing hidden. Synchronous and without per-post allocation.</summary>
    internal static ReaderPostProjection Identity(ArchivedThreadView thread)
    {
        ArgumentNullException.ThrowIfNull(thread);
        return new(thread, ProjectionInputs.For(thread, Array.Empty<IReaderPostClassifier>(), null),
            new HashSet<string>(StringComparer.Ordinal), null, null, null, null, 0, 0, false);
    }

    /// <summary>
    /// Builds a projection on a background thread. The classifiers and the ordering are the active ones (the caller asks
    /// <c>IsActive</c> on the UI thread). Steps: quote graph if needed, order or chronological, verdicts combined (highest
    /// visibility, marks by OR, reason of the strongest), revealed posts become Shown with Revealed, Hidden posts are
    /// removed and their deeper successors move up one level.
    /// </summary>
    internal static ReaderPostProjection Build(ArchivedThreadView thread, ReaderQuoteGraph? graph,
        IReadOnlyList<IReaderPostClassifier> classifiers, IReaderPostOrdering? ordering,
        IReadOnlySet<string> revealed, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(classifiers);
        ArgumentNullException.ThrowIfNull(revealed);
        if (classifiers.Count == 0 && ordering == null) return Identity(thread);
        int count = thread.PostCount;
        bool needsGraph = ordering != null || classifiers.Any(classifier => classifier.UsesQuoteGraph);
        if (needsGraph && (graph == null || !graph.Matches(thread))) graph = ReaderQuoteGraph.Build(thread, token);
        if (!needsGraph) graph = null;
        token.ThrowIfCancellationRequested();

        ReaderOrderEntry[] order;
        bool fellBack = false;
        if (ordering != null)
        {
            ReaderOrderEntry[]? proposed = ordering.Order(thread, graph!, token);
            token.ThrowIfCancellationRequested();
            if (IsValidOrder(proposed, count)) order = (ReaderOrderEntry[])proposed!.Clone();
            else
            {
                Log.Warning("Reader post ordering {Ordering} returned an invalid order for {Thread} ({Count} posts); showing the posts chronologically.",
                    ordering.GetType().Name, thread.Summary.Id, count);
                order = Chronological(count);
                fellBack = true;
            }
        }
        else order = Chronological(count);

        ReaderPostVerdict[] combined = new ReaderPostVerdict[count];
        if (classifiers.Count > 0)
        {
            ReaderPostVerdict[] scratch = new ReaderPostVerdict[count];
            foreach (IReaderPostClassifier classifier in classifiers)
            {
                token.ThrowIfCancellationRequested();
                Array.Clear(scratch);
                classifier.Classify(thread, graph, scratch, token);
                for (int index = 0; index < count; index++) combined[index] = Combine(combined[index], scratch[index]);
            }
        }
        token.ThrowIfCancellationRequested();
        return Materialize(thread, ProjectionInputs.For(thread, classifiers, ordering), order, combined,
            new HashSet<string>(revealed, StringComparer.Ordinal), fellBack);
    }

    /// <summary>The same classification with another set of revealed posts (steps 4 and 5 only; synchronous).</summary>
    internal ReaderPostProjection WithRevealed(IReadOnlySet<string> revealed)
    {
        ArgumentNullException.ThrowIfNull(revealed);
        if (IsIdentity || _revealed.SetEquals(revealed)) return this;
        return Materialize(Thread, Inputs, _order!, _verdicts!, new HashSet<string>(revealed, StringComparer.Ordinal), OrderFellBack);
    }

    internal ArchivedThreadView Thread { get; }
    internal bool IsIdentity => _entries == null;
    /// <summary>A custom order (P4) is applied; the reply counter is then suppressed (SuppressReplyToggle).</summary>
    internal bool IsOrdered => Inputs.Ordering != null && !OrderFellBack;
    /// <summary>Thread revision, classifier and ordering revisions and the revealed posts.</summary>
    internal long Key { get; }
    internal ProjectionInputs Inputs { get; }
    internal int Count => _entries?.Length ?? _posts.Count;
    /// <summary>Posts removed from this view (not counting placeholders).</summary>
    internal int HiddenCount { get; }
    /// <summary>Posts shown as a placeholder.</summary>
    internal int StubCount { get; }
    /// <summary>The registered ordering returned an invalid order and the view fell back to chronological order.</summary>
    internal bool OrderFellBack { get; }
    internal IReadOnlySet<string> Revealed => _revealed;

    internal ReaderPostEntry this[int position]
    {
        get
        {
            if (_entries != null) return _entries[position];
            if ((uint)position >= (uint)_posts.Count) throw new ArgumentOutOfRangeException(nameof(position));
            return new ReaderPostEntry(position, _posts[position], 0, -1, default);
        }
    }

    /// <summary>
    /// The page containing a position in the shown order. Retained offsets (history, settings changes) may belong
    /// to an older page size or a longer projection; always realign them before rendering or paging again.
    /// </summary>
    internal int PageOffset(int position, int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        return Count == 0 ? 0 : Math.Clamp(position, 0, Count - 1) / pageSize * pageSize;
    }

    internal IReadOnlyList<ReaderPostEntry> Page(int offset, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        int length = offset >= Count ? 0 : Math.Min(count, Count - offset);
        ReaderPostEntry[] page = new ReaderPostEntry[length];
        if (_entries != null) Array.Copy(_entries, offset, page, 0, length);
        else for (int index = 0; index < length; index++) page[index] = new(offset + index, _posts[offset + index], 0, -1, default);
        return Array.AsReadOnly(page);
    }

    /// <summary>The shown position of a post; -1 when this view hides it or it was not captured.</summary>
    internal int PositionOf(string postId)
    {
        int index = Thread.FindPostIndex(postId);
        if (index < 0) return -1;
        return _positionOf == null ? index : _positionOf[index];
    }

    /// <summary>The post itself when shown, otherwise the next shown post by thread index, otherwise the one before;
    /// -1 for an uncaptured post or an empty view.</summary>
    internal int NearestPosition(string postId)
    {
        int index = Thread.FindPostIndex(postId);
        if (index < 0) return -1;
        if (_positionOf == null) return index;
        if (_positionOf[index] >= 0) return _positionOf[index];
        for (int next = index + 1; next < _positionOf.Length; next++) if (_positionOf[next] >= 0) return _positionOf[next];
        for (int previous = index - 1; previous >= 0; previous--) if (_positionOf[previous] >= 0) return _positionOf[previous];
        return -1;
    }

    /// <summary>The post was captured but this view removes it.</summary>
    internal bool IsHidden(string postId)
    {
        if (_positionOf == null) return false;
        int index = Thread.FindPostIndex(postId);
        return index >= 0 && _positionOf[index] < 0;
    }

    /// <summary>Hidden or shown only as a placeholder: an explicit jump reveals such a post.</summary>
    internal bool IsConcealed(string postId)
    {
        if (_positionOf == null) return false;
        int index = Thread.FindPostIndex(postId);
        if (index < 0) return false;
        int position = _positionOf[index];
        return position < 0 || _entries![position].Verdict.Visibility == ReaderPostVisibility.Stub;
    }

    /// <summary>Another read of the same thread revision (a quote jump re-reads the thread) shares this projection.</summary>
    internal bool Matches(ArchivedThreadView? thread) => thread != null && (ReferenceEquals(thread, Thread)
        || thread.Summary.Id == Thread.Summary.Id && thread.Summary.Revision == Thread.Summary.Revision
            && thread.PostCount == Thread.PostCount);

    internal static long ComputeKey(ArchivedThreadView thread, IReadOnlyList<IReaderPostClassifier> classifiers,
        IReaderPostOrdering? ordering, IReadOnlySet<string> revealed) =>
        ComputeKey(ProjectionInputs.For(thread, classifiers, ordering), revealed);

    private static long ComputeKey(ProjectionInputs inputs, IReadOnlySet<string> revealed)
    {
        // Order-independent over the revealed set; the high half carries the inputs, the low half the reveals.
        int reveals = revealed.Count;
        foreach (string id in revealed) reveals = unchecked(reveals + StringComparer.Ordinal.GetHashCode(id) * 31);
        return ((long)inputs.GetHashCode() << 32) | (uint)reveals;
    }

    internal static bool IsValidOrder(ReaderOrderEntry[]? order, int count)
    {
        if (order == null || order.Length != count) return false;
        bool[] seen = new bool[count];
        foreach (ReaderOrderEntry entry in order)
        {
            if ((uint)entry.ThreadIndex >= (uint)count || seen[entry.ThreadIndex] || entry.Depth < 0
                || entry.ParentIndex < -1 || entry.ParentIndex >= count || entry.ParentIndex == entry.ThreadIndex) return false;
            seen[entry.ThreadIndex] = true;
        }
        return true;
    }

    internal static ReaderPostVerdict Combine(ReaderPostVerdict current, ReaderPostVerdict next)
    {
        ReaderPostVisibility visibility = next.Visibility switch
        {
            < ReaderPostVisibility.Shown => ReaderPostVisibility.Shown,
            > ReaderPostVisibility.Hidden => ReaderPostVisibility.Hidden,
            _ => next.Visibility
        };
        ReaderPostMarks marks = current.Marks | (next.Marks & ReaderPostMarks.Highlight); // Revealed is P0's alone.
        string? reason = visibility > current.Visibility ? next.Reason ?? current.Reason
            : visibility == current.Visibility ? current.Reason ?? next.Reason : current.Reason;
        return new(visibility > current.Visibility ? visibility : current.Visibility, marks, reason);
    }

    private static ReaderOrderEntry[] Chronological(int count)
    {
        ReaderOrderEntry[] order = new ReaderOrderEntry[count];
        for (int index = 0; index < count; index++) order[index] = new(index, 0, -1);
        return order;
    }

    private static ReaderPostProjection Materialize(ArchivedThreadView thread, ProjectionInputs inputs, ReaderOrderEntry[] order,
        ReaderPostVerdict[] verdicts, HashSet<string> revealed, bool fellBack)
    {
        IReadOnlyList<CapturedPost> posts = thread.Posts;
        int count = order.Length;
        ReaderPostVerdict[] effective = (ReaderPostVerdict[])verdicts.Clone();
        foreach (string id in revealed)
        {
            int index = thread.FindPostIndex(id);
            if (index >= 0 && effective[index].Visibility != ReaderPostVisibility.Shown)
                effective[index] = effective[index] with
                {
                    Visibility = ReaderPostVisibility.Shown, Marks = effective[index].Marks | ReaderPostMarks.Revealed
                };
        }
        int[] parentOf = new int[count];
        foreach (ReaderOrderEntry entry in order) parentOf[entry.ThreadIndex] = entry.ParentIndex;
        int[] positionOf = new int[count];
        Array.Fill(positionOf, -1);
        List<ReaderPostEntry> entries = new(count);
        // Ancestors by original depth; a hidden ancestor lifts its subtree by one level.
        int[] stackDepth = new int[Math.Max(1, count)];
        bool[] stackHidden = new bool[Math.Max(1, count)];
        int stack = 0, hiddenAncestors = 0, hidden = 0, stubs = 0;
        foreach (ReaderOrderEntry entry in order)
        {
            int depth = Math.Max(0, entry.Depth);
            while (stack > 0 && stackDepth[stack - 1] >= depth)
            {
                stack--;
                if (stackHidden[stack]) hiddenAncestors--;
            }
            ReaderPostVerdict verdict = effective[entry.ThreadIndex];
            bool isHidden = verdict.Visibility == ReaderPostVisibility.Hidden;
            int lift = hiddenAncestors;
            stackDepth[stack] = depth;
            stackHidden[stack] = isHidden;
            stack++;
            if (isHidden) { hiddenAncestors++; hidden++; continue; }
            if (verdict.Visibility == ReaderPostVisibility.Stub) stubs++;
            int parent = entry.ParentIndex;
            for (int guard = 0; parent >= 0 && effective[parent].Visibility == ReaderPostVisibility.Hidden && guard < count; guard++)
                parent = parentOf[parent];
            if (parent >= 0 && effective[parent].Visibility == ReaderPostVisibility.Hidden) parent = -1;
            positionOf[entry.ThreadIndex] = entries.Count;
            entries.Add(new ReaderPostEntry(entry.ThreadIndex, posts[entry.ThreadIndex], Math.Max(0, depth - lift), parent, verdict));
        }
        return new ReaderPostProjection(thread, inputs, revealed, entries.ToArray(), positionOf, order, verdicts,
            hidden, stubs, fellBack);
    }

    /// <summary>What a projection was built from, compared structurally (never by hash alone).</summary>
    internal sealed class ProjectionInputs : IEquatable<ProjectionInputs>
    {
        private ProjectionInputs(ResourceId thread, long revision, int count, IReaderPostClassifier[] classifiers,
            long[] revisions, IReaderPostOrdering? ordering, long orderingRevision)
        {
            Thread = thread; Revision = revision; PostCount = count; Classifiers = classifiers;
            ClassifierRevisions = revisions; Ordering = ordering; OrderingRevision = orderingRevision;
        }
        internal ResourceId Thread { get; }
        internal long Revision { get; }
        internal int PostCount { get; }
        internal IReaderPostClassifier[] Classifiers { get; }
        internal long[] ClassifierRevisions { get; }
        internal IReaderPostOrdering? Ordering { get; }
        internal long OrderingRevision { get; }

        internal static ProjectionInputs For(ArchivedThreadView thread, IReadOnlyList<IReaderPostClassifier> classifiers,
            IReaderPostOrdering? ordering) => new(thread.Summary.Id, thread.Summary.Revision, thread.PostCount,
                classifiers.ToArray(), classifiers.Select(classifier => classifier.Revision).ToArray(), ordering, ordering?.Revision ?? 0);

        public bool Equals(ProjectionInputs? other) => other != null && Thread == other.Thread && Revision == other.Revision
            && PostCount == other.PostCount && ReferenceEquals(Ordering, other.Ordering) && OrderingRevision == other.OrderingRevision
            && Classifiers.Length == other.Classifiers.Length
            && Classifiers.Zip(other.Classifiers).All(pair => ReferenceEquals(pair.First, pair.Second))
            && ClassifierRevisions.AsSpan().SequenceEqual(other.ClassifierRevisions);
        public override bool Equals(object? obj) => Equals(obj as ProjectionInputs);
        public override int GetHashCode()
        {
            HashCode hash = new();
            hash.Add(Thread); hash.Add(Revision); hash.Add(PostCount);
            hash.Add(Ordering == null ? 0 : RuntimeHelpers.GetHashCode(Ordering)); hash.Add(OrderingRevision);
            for (int index = 0; index < Classifiers.Length; index++)
            {
                hash.Add(RuntimeHelpers.GetHashCode(Classifiers[index]));
                hash.Add(ClassifierRevisions[index]);
            }
            return hash.ToHashCode();
        }
    }
}
