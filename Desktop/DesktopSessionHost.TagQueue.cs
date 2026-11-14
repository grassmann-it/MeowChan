using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Persistence;
using MeowChan.Tags;

namespace MeowChan.Desktop;

/// <summary>
/// Host commands of the tagging queue (<c>tag-queue.json</c>). Every change is an admitted interactive command that writes the file
/// before it completes, off the UI thread, so a finished command is durable and the session's stop waits for the ones in flight;
/// reads are passive and return the last committed view. The queue never assigns a tag, favorite or rating by itself.
/// </summary>
internal sealed partial class DesktopSessionHost
{
    private readonly object _tagQueueSync = new();
    private TagQueueStore? _tagQueueStore;
    private TagQueueSnapshot? _tagQueueSnapshot;
    private Exception? _tagQueueError;
    // Access only while holding _commands: the undo history of the current walk (memory only; the file keeps state and cursor). Its depth
    // follows the setting TagQueueUndoDepth; every committed queue forgets the decisions of entries that left it.
    private readonly TagQueueDecisionLog _tagQueueDecisions = new();
    private long _tagQueueCommands;

    /// <summary>
    /// The last committed or loaded queue, or throws <see cref="InvalidDataException"/> while it is unreadable. Null until
    /// <see cref="LoadTagQueueAsync"/> or a change has run once; callers show an empty queue then. Never touches the disk.
    /// </summary>
    internal TagQueueSnapshot? GetTagQueue()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_tagQueueSync)
        {
            if (_tagQueueError != null) throw new InvalidDataException("The tagging queue is unavailable: " + _tagQueueError.Message, _tagQueueError);
            return _tagQueueSnapshot;
        }
    }

    /// <summary>The error that makes the queue unreadable, or null. It is cleared by a successful load or by clearing the queue.</summary>
    internal Exception? TagQueueError { get { lock (_tagQueueSync) return _tagQueueError; } }

    /// <summary>How many steps the current walk can undo.</summary>
    internal int TagQueueUndoDepth => _tagQueueDecisions.Count;

    /// <summary>Reads the file and publishes it (the entry point of a page that shows the queue). An unreadable file is reported, not emptied.</summary>
    internal Task<TagQueueSnapshot> LoadTagQueueAsync(CancellationToken token = default)
        => TagQueueCommandAsync(nameof(LoadTagQueueAsync), store => store.Read(), token, signal: false);

    /// <summary>
    /// Adds media to the end of the queue as open entries. Entries that are already in the queue or do not fit under the entry limit are
    /// counted in the result, never added and never an error.
    /// </summary>
    internal Task<TagQueueAddResult> EnqueueTagQueueAsync(IReadOnlyList<TagQueueItem> items, CancellationToken token = default)
    {
        TagQueueItem[] frozen = items.ToArray();
        return TagQueueCommandAsync(nameof(EnqueueTagQueueAsync), store => store.Add(frozen, token), token, result => result.Snapshot, result => result.Changed);
    }

    internal Task<TagQueueSnapshot> RemoveFromTagQueueAsync(IReadOnlyCollection<TagQueueKey> keys, CancellationToken token = default)
    {
        TagQueueKey[] frozen = keys.ToArray();
        // The decisions of the removed entries leave the walk's history with the commit (ForgetMissing), so an undo never reaches past them.
        return TagQueueCommandAsync(nameof(RemoveFromTagQueueAsync), store => store.Remove(frozen, token), token);
    }

    /// <summary>Marks entries open, finished or skipped without touching the undo history of the walk.</summary>
    internal Task<TagQueueSnapshot> SetTagQueueStatesAsync(IReadOnlyList<KeyValuePair<TagQueueKey, TagQueueState>> changes, CancellationToken token = default)
    {
        KeyValuePair<TagQueueKey, TagQueueState>[] frozen = changes.ToArray();
        return TagQueueCommandAsync(nameof(SetTagQueueStatesAsync), store => store.SetStates(frozen, DateTimeOffset.UtcNow, null, token), token);
    }

    /// <summary>
    /// One decision of the walk: sets the state of the entry and, when <paramref name="advance"/>, moves the cursor behind it, in one
    /// write. The decision is pushed on the walk's undo history. Returns null when the entry is no longer in the queue.
    /// </summary>
    internal Task<TagQueueDecision?> DecideTagQueueAsync(TagQueueKey key, TagQueueState state, bool advance = true, CancellationToken token = default)
        => TagQueueDecisionAsync(nameof(DecideTagQueueAsync), store =>
        {
            TagQueueSnapshot before = store.Read();
            int index = before.IndexOf(key);
            if (index < 0) return null;
            TagQueueSnapshot after = store.SetStates([new(key, state)], DateTimeOffset.UtcNow,
                advance ? TagQueueCursorTarget.Relative(key, 1) : TagQueueCursorTarget.Relative(key, 0), token);
            TagQueueKey? cursorKey = before.Cursor < before.Count ? before.Items[before.Cursor].Key : null;
            return (after, new TagQueueDecision(key, before.Items[index].State, state, before.Cursor, after.Cursor, cursorKey));
        }, token, decision =>
        {
            _tagQueueDecisions.Depth = TaggingSettings.UndoDepth(Config);
            _tagQueueDecisions.Push(decision);
        });

    /// <summary>
    /// Writes the reverse of a decision of the walk's history (state and cursor, in one write) and only then takes it off the history: the
    /// newest one, or with <paramref name="expected"/> exactly that decision (the walk names its own step, so the two stacks never take back
    /// different entries). Returns null without writing when there is nothing to undo, <paramref name="expected"/> is no longer in the
    /// history, or its entry has left the queue meanwhile (that decision is dropped). A refused write keeps the decision for another try.
    /// The cursor returns to the entry it stood on before, wherever that entry stands now.
    /// </summary>
    internal Task<TagQueueDecision?> UndoTagQueueDecisionAsync(TagQueueDecision? expected = null, CancellationToken token = default)
        => TagQueueDecisionAsync(nameof(UndoTagQueueDecisionAsync), store =>
        {
            if (_tagQueueDecisions.Find(expected) is not { } decision) return null;
            TagQueueSnapshot current = store.Read();
            if (!current.Contains(decision.Key))
            {
                _tagQueueDecisions.Remove(decision);
                return null;
            }
            TagQueueSnapshot after = store.SetStates([new(decision.Key, decision.Before)], DateTimeOffset.UtcNow,
                decision.CursorTargetIn(current), token);
            return (after, decision);
        }, token, decision => _tagQueueDecisions.Remove(decision));

    internal Task<TagQueueSnapshot> SetTagQueueCursorAsync(int cursor, CancellationToken token = default)
        => TagQueueCommandAsync(nameof(SetTagQueueCursorAsync), store => store.SetCursor(cursor, token), token);

    internal Task<TagQueueSnapshot> MoveInTagQueueAsync(TagQueueKey key, int index, CancellationToken token = default)
        => TagQueueCommandAsync(nameof(MoveInTagQueueAsync), store => store.Move(key, index, token), token);

    /// <summary>Removes every entry, or only the finished ones. An unreadable file is preserved next to its place and replaced by an empty queue.</summary>
    internal Task<TagQueueSnapshot> ClearTagQueueAsync(bool finishedOnly = false, CancellationToken token = default)
        => TagQueueCommandAsync(nameof(ClearTagQueueAsync), store => store.Clear(finishedOnly, token), token);

    /// <summary>Replaces the stamp set (tag names; at most the fixed ceiling, the user limit applies in the surface).</summary>
    internal Task<TagQueueSnapshot> SetTagQueueStampAsync(IReadOnlyList<string> names, CancellationToken token = default)
    {
        string[] frozen = names.ToArray();
        return TagQueueCommandAsync(nameof(SetTagQueueStampAsync), store => store.SetStamp(frozen, token), token);
    }

    private Task<TagQueueSnapshot> TagQueueCommandAsync(string action, Func<TagQueueStore, TagQueueSnapshot> operation, CancellationToken token,
        Action<TagQueueSnapshot>? onCommitted = null, bool signal = true)
        => TagQueueCommandAsync(action, operation, token, snapshot => snapshot, snapshot => signal, onCommitted == null ? null : (_, snapshot) => onCommitted(snapshot));

    private Task<TResult> TagQueueCommandAsync<TResult>(string action, Func<TagQueueStore, TResult> operation, CancellationToken token,
        Func<TResult, TagQueueSnapshot> snapshotOf, Func<TResult, bool> changed, Action<TResult, TagQueueSnapshot>? onCommitted = null)
        => RunInteractiveAsync(action, "queue-" + Interlocked.Increment(ref _tagQueueCommands), async () =>
        {
            await _commands.WaitAsync(token).ConfigureAwait(false);
            try
            {
                EnsureReady();
                TResult result;
                try { result = await Task.Run(() => operation(TagQueue), token).ConfigureAwait(false); }
                catch (Exception error) when (IsTagQueueReadError(error)) { SetTagQueueError(error); throw; }
                TagQueueSnapshot snapshot = snapshotOf(result);
                Publish(snapshot);
                _tagQueueDecisions.ForgetMissing(snapshot);
                onCommitted?.Invoke(result, snapshot);
                if (changed(result)) SignalChanged();
                return result;
            }
            finally { _commands.Release(); }
        });

    // `committed` runs after the write succeeded and was published, still inside the command lane (push or remove the decision there).
    private Task<TagQueueDecision?> TagQueueDecisionAsync(string action,
        Func<TagQueueStore, (TagQueueSnapshot Snapshot, TagQueueDecision Decision)?> operation, CancellationToken token, Action<TagQueueDecision> committed)
        => RunInteractiveAsync<TagQueueDecision?>(action, "queue-" + Interlocked.Increment(ref _tagQueueCommands), async () =>
        {
            await _commands.WaitAsync(token).ConfigureAwait(false);
            try
            {
                EnsureReady();
                (TagQueueSnapshot Snapshot, TagQueueDecision Decision)? result;
                try { result = await Task.Run(() => operation(TagQueue), token).ConfigureAwait(false); }
                catch (Exception error) when (IsTagQueueReadError(error)) { SetTagQueueError(error); throw; }
                if (result is not { } done) return null;
                Publish(done.Snapshot);
                committed(done.Decision);
                _tagQueueDecisions.ForgetMissing(done.Snapshot);
                SignalChanged();
                return done.Decision;
            }
            finally { _commands.Release(); }
        });

    // The store is created on first use, under _commands or the sync lock; every operation runs inside the lease's own exclusion.
    private TagQueueStore TagQueue
    {
        get
        {
            lock (_tagQueueSync) return _tagQueueStore ??= new TagQueueStore(_profileLease.ProfileDirectory, _profileLease);
        }
    }

    private void Publish(TagQueueSnapshot snapshot)
    {
        lock (_tagQueueSync) { _tagQueueSnapshot = snapshot; _tagQueueError = null; }
    }

    private void SetTagQueueError(Exception error)
    {
        lock (_tagQueueSync) _tagQueueError = error;
    }

    // A refused write (limit, newer file, blocked backup) leaves the published view as it was; only an unreadable file is an error state.
    private static bool IsTagQueueReadError(Exception error) => error is InvalidDataException && !PrivateStoreLimit.TryDescribe(error, out _, out _)
        || error is TagQueueReadException;
}
