using System;
using System.Collections.Generic;

namespace MeowChan.Tags;

/// <summary>
/// One decision of a walk through the queue: the entry, its state before and after, and where the cursor stood before and after. Undoing
/// it writes exactly the reverse. <paramref name="CursorBeforeKey"/> names the entry the cursor stood on before (null when it stood behind
/// the last entry), so an undo finds the same resume point again after entries before it were removed, moved or dropped. Favorite,
/// rating and tag changes made during a walk are separate entries of the tagging mode (PT3); the queue's own part of a decision is this one.
/// </summary>
internal sealed record TagQueueDecision(TagQueueKey Key, TagQueueState Before, TagQueueState After, int CursorBefore, int CursorAfter,
    TagQueueKey? CursorBeforeKey = null)
{
    /// <summary>
    /// Where the undo puts the cursor in <paramref name="current"/>: on the entry it stood on before, wherever that entry is now; else
    /// (it stood behind the last entry, or its entry left the queue) at the old position, clamped to the queue.
    /// </summary>
    internal TagQueueCursorTarget CursorTargetIn(TagQueueSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return CursorBeforeKey is { } anchor && current.Contains(anchor) ? TagQueueCursorTarget.Relative(anchor, 0)
            : TagQueueCursorTarget.At(Math.Clamp(CursorBefore, 0, current.Count));
    }
}

/// <summary>
/// The undo history of one walk: the last decisions, newest last, at most <see cref="Depth"/> of them (the oldest fall out). It lives in
/// memory only; what survives a restart is the state and the cursor in the queue file, which is what "continue" resumes from. The depth is
/// the setting <c>TagQueueUndoDepth</c>; lowering it drops the oldest decisions at once. Not thread-safe: the owner calls it from its own
/// serialized command lane.
/// </summary>
internal sealed class TagQueueDecisionLog
{
    internal const int DefaultDepth = 20, MinimumDepth = 1, MaximumDepth = 100;
    private readonly List<TagQueueDecision> _entries = new();
    private int _depth;

    internal TagQueueDecisionLog(int depth = DefaultDepth) => Depth = depth;

    internal int Depth
    {
        get => _depth;
        set
        {
            _depth = value is >= MinimumDepth and <= MaximumDepth ? value : throw new ArgumentOutOfRangeException(nameof(value), "The depth is outside its supported range.");
            Trim();
        }
    }

    internal int Count => _entries.Count;
    internal TagQueueDecision? Last => _entries.Count == 0 ? null : _entries[^1];

    internal void Push(TagQueueDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        _entries.Add(decision);
        Trim();
    }

    /// <summary>Takes the newest decision off the log, or null when there is none.</summary>
    internal TagQueueDecision? Pop()
    {
        if (_entries.Count == 0) return null;
        TagQueueDecision last = _entries[^1];
        _entries.RemoveAt(_entries.Count - 1);
        return last;
    }

    /// <summary>
    /// The decision an undo takes back: the newest one, or with <paramref name="expected"/> exactly that one (the newest equal entry). Null
    /// when there is none or <paramref name="expected"/> is not in the log any more (its entry left the queue, or it fell out). Nothing is
    /// removed: the caller removes the decision with <see cref="Remove"/> after its reverse was written.
    /// </summary>
    internal TagQueueDecision? Find(TagQueueDecision? expected = null)
    {
        if (expected == null) return Last;
        for (int i = _entries.Count - 1; i >= 0; i--)
            if (_entries[i].Equals(expected)) return _entries[i];
        return null;
    }

    /// <summary>Removes this decision (the newest occurrence of exactly this instance); false when it is not in the log.</summary>
    internal bool Remove(TagQueueDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            if (!ReferenceEquals(_entries[i], decision)) continue;
            _entries.RemoveAt(i);
            return true;
        }
        return false;
    }

    internal void Clear() => _entries.Clear();

    /// <summary>Forgets the decisions of entries that are no longer in the queue (they cannot be undone any more).</summary>
    internal void Forget(IReadOnlyCollection<TagQueueKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        HashSet<TagQueueKey> removed = keys is HashSet<TagQueueKey> set ? set : new(keys);
        _entries.RemoveAll(decision => removed.Contains(decision.Key));
    }

    /// <summary>Forgets the decisions whose entries <paramref name="snapshot"/> no longer holds (removed, cleared or dropped as old history).</summary>
    internal void ForgetMissing(TagQueueSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _entries.RemoveAll(decision => !snapshot.Contains(decision.Key));
    }

    private void Trim()
    {
        if (_entries.Count > _depth) _entries.RemoveRange(0, _entries.Count - _depth);
    }
}
