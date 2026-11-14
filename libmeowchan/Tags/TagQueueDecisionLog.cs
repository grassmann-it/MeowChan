using System;
using System.Collections.Generic;

namespace MeowChan.Tags;

/// <summary>
/// One decision of a walk through the queue: the entry, its state before and after, and where the cursor stood before and after. Undoing
/// it writes exactly the reverse. Favorite, rating and tag changes made during a walk are separate entries of the tagging mode (PT3); the
/// queue's own part of a decision is this one.
/// </summary>
internal sealed record TagQueueDecision(TagQueueKey Key, TagQueueState Before, TagQueueState After, int CursorBefore, int CursorAfter);

/// <summary>
/// The undo history of one walk: the last decisions, newest last, at most <see cref="Depth"/> of them (the oldest fall out). It lives in
/// memory only; what survives a restart is the state and the cursor in the queue file, which is what "continue" resumes from. Not
/// thread-safe: the owner calls it from its own serialized command lane.
/// </summary>
internal sealed class TagQueueDecisionLog
{
    internal const int DefaultDepth = 20, MinimumDepth = 1, MaximumDepth = 100;
    private readonly List<TagQueueDecision> _entries = new();

    internal TagQueueDecisionLog(int depth = DefaultDepth) =>
        Depth = depth is >= MinimumDepth and <= MaximumDepth ? depth : throw new ArgumentOutOfRangeException(nameof(depth), "The depth is outside its supported range.");

    internal int Depth { get; }
    internal int Count => _entries.Count;
    internal TagQueueDecision? Last => _entries.Count == 0 ? null : _entries[^1];

    internal void Push(TagQueueDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        _entries.Add(decision);
        if (_entries.Count > Depth) _entries.RemoveRange(0, _entries.Count - Depth);
    }

    /// <summary>Takes the newest decision off the log, or null when there is none.</summary>
    internal TagQueueDecision? Pop()
    {
        if (_entries.Count == 0) return null;
        TagQueueDecision last = _entries[^1];
        _entries.RemoveAt(_entries.Count - 1);
        return last;
    }

    internal void Clear() => _entries.Clear();

    /// <summary>Forgets the decisions of entries that are no longer in the queue (they cannot be undone any more).</summary>
    internal void Forget(IReadOnlyCollection<TagQueueKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        HashSet<TagQueueKey> removed = keys is HashSet<TagQueueKey> set ? set : new(keys);
        _entries.RemoveAll(decision => removed.Contains(decision.Key));
    }
}
