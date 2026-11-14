using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan.Desktop;

// PT3 (PLAN-POWER-TAGGEN-2026-11-14 §1.4, §1.6, §1.7; decision E1: the list stays a page, the walk happens in the gallery): the tagging
// mode's model without UI. A walk is the gallery list opened over the open entries of the tagging queue; every entry of the list knows
// its queue entry. Enter finishes the shown medium and goes to the next open one, Space skips it, Backspace takes back the newest
// decision and returns to its medium. The queue's own part of a decision is written by the host (DecideTagQueueAsync, which also moves
// the resume point); favorite, rating and tag steps keep their own undo stacks (GalleryDecisionSession, QuickTagSession), and one undo
// key takes back whichever step is newest (TaggingUndo). The viewer (GalleryViewer.TaggingMode.cs) shows it; the window
// (MainWindow.TagQueue.cs) opens it.

/// <summary>What the walk needs from the host: one decision with its undo, and the last committed queue. Calls arrive one at a time.</summary>
internal interface ITaggingQueueWriter
{
    /// <summary>Sets the state of the entry and moves the resume point behind it; null when the entry is no longer in the queue.</summary>
    Task<TagQueueDecision?> DecideAsync(TagQueueKey key, TagQueueState state, CancellationToken token);
    /// <summary>
    /// Writes the reverse of exactly this decision (one the writer returned from <see cref="DecideAsync"/>) and only then forgets it; null
    /// without a write when the writer no longer holds it or its entry left the queue. A refused write keeps it for another try.
    /// </summary>
    Task<TagQueueDecision?> UndoAsync(TagQueueDecision decision, CancellationToken token);
    /// <summary>The last committed queue, or null while it is unknown or unreadable.</summary>
    TagQueueSnapshot? Current { get; }
}

/// <summary>The session host's queue commands for a walk: one decision (state and resume point in one write) and its undo.</summary>
internal sealed class HostTaggingQueueWriter(DesktopSessionHost host) : ITaggingQueueWriter
{
    public Task<TagQueueDecision?> DecideAsync(TagQueueKey key, TagQueueState state, CancellationToken token) =>
        host.DecideTagQueueAsync(key, state, advance: true, token);

    public Task<TagQueueDecision?> UndoAsync(TagQueueDecision decision, CancellationToken token) => host.UndoTagQueueDecisionAsync(decision, token);

    public TagQueueSnapshot? Current
    {
        get
        {
            try { return host.GetTagQueue(); }
            catch (Exception error) when (error is InvalidDataException or ObjectDisposedException) { return null; }
        }
    }
}

/// <summary>One medium of the walk: the gallery entry that shows it, its queue entry and its file name.</summary>
internal sealed record TaggingWalkItem(GalleryEntryKey Entry, TagQueueKey Key, string Name);

internal enum TaggingDecisionOutcome { Decided, Busy, NotInWalk }

/// <summary>
/// What a decision did at once: the medium the walk goes to next (null when every medium of the walk is decided) and the write, which
/// completes with the result of the store. The walk shows the new state before the write ends, so the next medium can follow at once.
/// </summary>
internal sealed record TaggingDecisionResult(TaggingDecisionOutcome Outcome, int Index, int? Next, Task<TaggingWriteResult> Write)
{
    internal static TaggingDecisionResult Refused(TaggingDecisionOutcome outcome, int index) =>
        new(outcome, index, null, Task.FromResult(TaggingWriteResult.None));
}

internal enum TaggingWriteKind { None, Saved, Missing, Failed }

/// <summary>The end of a write: saved, the entry was no longer in the queue, or refused with the localized reason.</summary>
internal sealed record TaggingWriteResult(TaggingWriteKind Kind, string? Message = null)
{
    internal static readonly TaggingWriteResult None = new(TaggingWriteKind.None);
}

/// <summary>An undone queue decision: the medium it belonged to (to return to) and the state it has again.</summary>
internal sealed record TaggingUndoResult(int Index, string Name, TagQueueState State);

/// <summary>
/// The walk through the open entries of the tagging queue in the gallery: the list, the state of every entry (the store's, or the one a
/// running write is about to save), the next open medium, the progress, and the undo stack of the queue decisions of this walk (newest
/// last, as deep as the host's decision log). Writes run one after the other, in the order they were made, and are never cancelled. Not
/// thread-safe: the UI thread calls it.
/// </summary>
internal sealed class TaggingWalk : IDisposable
{
    private readonly ITaggingQueueWriter _writer;
    private readonly IReadOnlyList<TaggingWalkItem> _items;
    private readonly Dictionary<GalleryEntryKey, int> _byEntry = new();
    private readonly Dictionary<TagQueueKey, TagQueueState> _pending = new();
    private readonly List<Step> _steps = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TagQueueSnapshot _snapshot;
    private int _running;
    private bool _disposed;

    // The decision the host returned: the undo names it, so the walk and the host's history always take back the same entry.
    private sealed record Step(int Index, TagQueueKey Key, long Stamp, TagQueueDecision Decision);

    internal TaggingWalk(IReadOnlyList<TaggingWalkItem> items, int unavailable, TagQueueSnapshot snapshot, ITaggingQueueWriter writer,
        int depth = TagQueueDecisionLog.DefaultDepth)
    {
        _items = items ?? throw new ArgumentNullException(nameof(items));
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        Unavailable = Math.Max(0, unavailable);
        Depth = Math.Clamp(depth, TagQueueDecisionLog.MinimumDepth, TagQueueDecisionLog.MaximumDepth);
        for (int i = 0; i < items.Count; i++) _byEntry.TryAdd(items[i].Entry, i);
    }

    /// <summary>
    /// Pairs the resolved gallery list with the queue entries it was resolved from. The resolver keeps the order and leaves out what it
    /// cannot find, so each entry belongs to the next queue entry with the same thread, folder, asset, post and position; queue entries
    /// without a gallery entry are counted as unavailable (they stay open in the queue, nothing is removed).
    /// </summary>
    internal static (IReadOnlyList<TaggingWalkItem> Items, int Unavailable) Pair(IReadOnlyList<TagQueueItem> queued, IReadOnlyList<GalleryEntry> resolved)
    {
        ArgumentNullException.ThrowIfNull(queued);
        ArgumentNullException.ThrowIfNull(resolved);
        List<TaggingWalkItem> items = new(resolved.Count);
        int next = 0, unavailable = 0;
        foreach (TagQueueItem item in queued)
        {
            if (next < resolved.Count && Matches(item, resolved[next]))
            {
                GalleryEntry entry = resolved[next++];
                items.Add(new TaggingWalkItem(entry.EntryKey, item.Key, item.Name.Length > 0 ? item.Name : GalleryTagPanelCommands.LabelOf(entry)));
            }
            else unavailable++;
        }
        return (items, unavailable);
    }

    /// <summary>The identity the gallery resolver uses: thread, media folder, asset, post and attachment position.</summary>
    internal static bool Matches(TagQueueItem item, GalleryEntry entry) =>
        item.Thread == entry.Key.Thread && item.Directory == entry.Manifest.Directory && item.AssetId == entry.Asset.AssetId
        && item.PostId == entry.Post?.PostId && item.Position == entry.Attachment?.Position;

    internal IReadOnlyList<TaggingWalkItem> Items => _items;
    internal int Count => _items.Count;
    /// <summary>Open queue entries of this walk whose file could not be shown; they stay open in the queue.</summary>
    internal int Unavailable { get; }
    internal int Depth { get; }
    /// <summary>The queue as the last write (or the opening) left it.</summary>
    internal TagQueueSnapshot Snapshot => _snapshot;
    /// <summary>Queue decisions of this walk that can be undone (see <see cref="DropGoneSteps"/> for entries that left the queue).</summary>
    internal int Steps => _steps.Count;
    /// <summary>The stamp of the queue decision undone next (shared order with tag and favorite/rating steps); 0 without one.</summary>
    internal long NewestStamp => _steps.Count == 0 ? 0 : _steps[^1].Stamp;
    /// <summary>A write or an undo is running.</summary>
    internal bool Busy => Volatile.Read(ref _running) > 0;

    /// <summary>Raised after every change of a state, the stack or the busy flag (on the UI thread).</summary>
    internal event Action? Changed;

    internal int IndexOf(GalleryEntryKey entry) => entry != null && _byEntry.TryGetValue(entry, out int index) ? index : -1;

    /// <summary>The state of the medium: what a running write is saving, else the store's; null when it left the queue.</summary>
    internal TagQueueState? StateOf(int index)
    {
        if (index < 0 || index >= _items.Count) return null;
        TagQueueKey key = _items[index].Key;
        if (_pending.TryGetValue(key, out TagQueueState pending)) return pending;
        int position = _snapshot.IndexOf(key);
        return position < 0 ? null : _snapshot.Items[position].State;
    }

    /// <summary>The next open medium after <paramref name="index"/>, else the first open one before it; null when none is open.</summary>
    internal int? NextOpen(int index)
    {
        for (int i = Math.Max(-1, index) + 1; i < _items.Count; i++)
            if (StateOf(i) == TagQueueState.Open) return i;
        for (int i = 0; i < Math.Min(index, _items.Count); i++)
            if (StateOf(i) == TagQueueState.Open) return i;
        return null;
    }

    /// <summary>The medium's place in the whole queue (1-based) and the queue's length; null when it left the queue.</summary>
    internal (int Number, int Count)? QueuePosition(int index)
    {
        if (index < 0 || index >= _items.Count) return null;
        int position = _snapshot.IndexOf(_items[index].Key);
        return position < 0 ? null : (position + 1, _snapshot.Count);
    }

    /// <summary>Done, skipped and open entries of the whole queue, with the states a running write is saving.</summary>
    internal (int Done, int Skipped, int Open) Progress()
    {
        int done = _snapshot.Done, skipped = _snapshot.Skipped, open = _snapshot.Open;
        foreach ((TagQueueKey key, TagQueueState state) in _pending)
        {
            int position = _snapshot.IndexOf(key);
            if (position < 0) continue;
            Count(_snapshot.Items[position].State, -1);
            Count(state, 1);
        }
        return (done, skipped, open);

        void Count(TagQueueState state, int delta)
        {
            switch (state) { case TagQueueState.Done: done += delta; break; case TagQueueState.Skipped: skipped += delta; break; default: open += delta; break; }
        }
    }

    /// <summary>Open entries of the queue that are not part of this walk (beyond its window, or unavailable).</summary>
    internal int OpenOutsideWalk()
    {
        HashSet<TagQueueKey> walk = _items.Select(item => item.Key).ToHashSet();
        return _snapshot.Items.Count(item => item.State == TagQueueState.Open && !walk.Contains(item.Key));
    }

    /// <summary>
    /// Finishes (<see cref="TagQueueState.Done"/>) or skips the medium: the new state counts at once, the next open medium is chosen, and
    /// the write follows in order. Refused while an undo runs (its medium is about to be shown again).
    /// </summary>
    internal TaggingDecisionResult Decide(int index, TagQueueState state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (index < 0 || index >= _items.Count || StateOf(index) == null) return TaggingDecisionResult.Refused(TaggingDecisionOutcome.NotInWalk, index);
        if (_undoing) return TaggingDecisionResult.Refused(TaggingDecisionOutcome.Busy, index);
        TaggingWalkItem item = _items[index];
        _pending[item.Key] = state;
        long stamp = GalleryDecisionSession.NextStamp();
        int? next = NextOpen(index);
        Changed?.Invoke();
        return new TaggingDecisionResult(TaggingDecisionOutcome.Decided, index, next, WriteAsync(index, item, state, stamp));
    }

    private bool _undoing;

    private async Task<TaggingWriteResult> WriteAsync(int index, TaggingWalkItem item, TagQueueState state, long stamp)
    {
        Begin();
        try
        {
            await _gate.WaitAsync().ConfigureAwait(true);
            try
            {
                TagQueueDecision? decision;
                try { decision = await _writer.DecideAsync(item.Key, state, CancellationToken.None).ConfigureAwait(true); }
                catch (OperationCanceledException) { return TaggingWriteResult.None; }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
                {
                    Settle(item.Key, state);
                    return new TaggingWriteResult(TaggingWriteKind.Failed, TagQueueActions.DescribeFailure(error));
                }
                Settle(item.Key, state);
                if (decision == null) return new TaggingWriteResult(TaggingWriteKind.Missing);
                _steps.Add(new Step(index, item.Key, stamp, decision));
                if (_steps.Count > Depth) _steps.RemoveRange(0, _steps.Count - Depth);
                return new TaggingWriteResult(TaggingWriteKind.Saved);
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) { return TaggingWriteResult.None; }
        finally { End(); }
    }

    // The write ended: the store's view replaces the state it was saving (a later decision of the same entry keeps its own). The new
    // view comes first, so a reader in between never sees neither the pending nor the saved state.
    private void Settle(TagQueueKey key, TagQueueState state)
    {
        Refresh();
        if (_pending.TryGetValue(key, out TagQueueState pending) && pending == state) _pending.Remove(key);
    }

    /// <summary>
    /// Takes back the newest queue decision of this walk after every running write: the entry gets its old state and the resume point its
    /// old place. Steps whose entries left the queue meanwhile (removed or cleared on the tagging page) are dropped first, so the undo
    /// takes back the newest decision that still exists. Null when there is nothing to undo, or the host no longer holds the decision
    /// (that step is dropped). A refused write keeps the step and the host's decision, so the next try takes back the same entry.
    /// </summary>
    internal async Task<TaggingUndoResult?> UndoAsync()
    {
        if (_disposed || _undoing) return null;
        _undoing = true;
        Begin();
        try
        {
            await _gate.WaitAsync().ConfigureAwait(true);
            try
            {
                DropGoneSteps();
                if (_steps.Count == 0) return null;
                Step step = _steps[^1];
                // The step leaves only after the host answered: a refused write keeps it (and the host keeps its decision).
                TagQueueDecision? decision = await _writer.UndoAsync(step.Decision, CancellationToken.None).ConfigureAwait(true);
                _steps.Remove(step);
                Refresh();
                if (decision == null || decision.Key != step.Key) return null;
                return new TaggingUndoResult(step.Index, _items[step.Index].Name, decision.Before);
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) { return null; }
        finally { _undoing = false; End(); }
    }

    /// <summary>
    /// Drops the steps whose entries the last committed queue no longer holds (removed, cleared, dropped as old history): they cannot be
    /// undone, and they must not count as the newest step of the shared undo key. Keeps every step while the queue is unknown.
    /// </summary>
    internal void DropGoneSteps()
    {
        Refresh();
        if (_steps.RemoveAll(step => !_snapshot.Contains(step.Key)) > 0 && !_disposed) Changed?.Invoke();
    }

    private void Refresh()
    {
        try { if (_writer.Current is { } current) _snapshot = current; }
        catch (Exception error) when (error is IOException or InvalidDataException or ObjectDisposedException) { /* the last view stays */ }
    }

    private void Begin() { Interlocked.Increment(ref _running); Changed?.Invoke(); }
    private void End() { Interlocked.Decrement(ref _running); if (!_disposed) Changed?.Invoke(); }

    /// <summary>
    /// Ends the walk for its viewer. Writes already made still finish (a decision is never lost because the gallery closed right after
    /// it); the session's stop waits for them like for any admitted command.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Changed = null;
    }
}

/// <summary>
/// The undo key and button of the tag panel in the tagging mode: one stack over the queue decisions of the walk, the favorite and rating
/// steps of the panel and (through the surface) its tag steps. Whatever is newest is taken back first; an undone queue decision also
/// returns to its medium. Replaces the panel's own favorite/rating undo while the mode runs.
/// </summary>
internal sealed class TaggingUndo : IQuickTagExtraUndo, IDisposable
{
    private readonly TaggingWalk _walk;
    private readonly GalleryDecisionSession? _decisions;
    private readonly Func<long> _newestTag;
    private readonly Action<TaggingUndoResult> _returnTo;
    private Action? _changed;

    internal TaggingUndo(TaggingWalk walk, GalleryDecisionSession? decisions, Func<long> newestTag, Action<TaggingUndoResult> returnTo)
    {
        _walk = walk ?? throw new ArgumentNullException(nameof(walk));
        _decisions = decisions;
        _newestTag = newestTag ?? throw new ArgumentNullException(nameof(newestTag));
        _returnTo = returnTo ?? throw new ArgumentNullException(nameof(returnTo));
        _walk.Changed += OnChanged;
        if (_decisions != null) _decisions.Updated += OnChanged;
    }

    private long DecisionStamp => _decisions?.NewestStamp ?? 0;
    /// <summary>The newest of the two stacks here is a queue decision (not a favorite or rating step).</summary>
    internal bool QueueIsNewest => _walk.Steps > 0 && _walk.NewestStamp > DecisionStamp;

    public int Steps => _walk.Steps + (_decisions?.UndoSteps.Count ?? 0);
    public bool CanUndo => QueueIsNewest ? !_walk.Busy : _decisions?.CanUndo == true;
    public bool IsNewest => Steps > 0 && Math.Max(_walk.NewestStamp, DecisionStamp) > _newestTag();

    public async Task<string?> UndoAsync()
    {
        // A queue step whose entry left the queue on the tagging page is neither newest nor undoable: drop it before choosing the stack.
        if (!_walk.Busy) _walk.DropGoneSteps();
        if (QueueIsNewest)
        {
            TaggingUndoResult? undone;
            try { undone = await _walk.UndoAsync(); }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
            { return Texts.T("tagging.status.failed", TagQueueActions.DescribeFailure(error)); }
            if (undone == null) return Texts.T("tagging.status.undoGone");
            _returnTo(undone);
            return TaggingModeText.Undone(undone);
        }
        if (_decisions == null) return null;
        GalleryDecisionStatus status = await _decisions.UndoAsync();
        return status.Kind == GalleryDecisionStatusKind.None ? null : status.Text();
    }

    public event Action? Changed { add => _changed += value; remove => _changed -= value; }

    private void OnChanged() => _changed?.Invoke();

    public void Dispose()
    {
        _walk.Changed -= OnChanged;
        if (_decisions != null) _decisions.Updated -= OnChanged;
        _changed = null;
    }
}

/// <summary>The sentences of the tagging mode; one place so the strip, the panel and the tests agree.</summary>
internal static class TaggingModeText
{
    /// <summary>"Queue 12 of 200" for the gallery header; null when the medium left the queue.</summary>
    internal static string? Position(TaggingWalk walk, int index) => walk.QueuePosition(index) is { } position
        ? Texts.T("tagging.position", position.Number.ToString(Texts.Culture), position.Count.ToString(Texts.Culture)) : null;

    /// <summary>"Done: 5 · Skipped: 1 · Open: 194" (numbers in fixed phrases: no plural forms needed).</summary>
    internal static string Progress(TaggingWalk walk)
    {
        (int done, int skipped, int open) = walk.Progress();
        return Texts.T("tagging.progress", done.ToString(Texts.Culture), skipped.ToString(Texts.Culture), open.ToString(Texts.Culture));
    }

    /// <summary>What the status line says after a decision: what happened, and the next medium or that the walk is through.</summary>
    internal static string Decided(TaggingWalk walk, TaggingDecisionResult result, TagQueueState state)
    {
        string name = walk.Items[result.Index].Name;
        bool done = state == TagQueueState.Done;
        if (result.Next is { } next)
            return Texts.T(done ? "tagging.status.done" : "tagging.status.skipped", name, walk.Items[next].Name);
        string text = Texts.T(done ? "tagging.status.doneLast" : "tagging.status.skippedLast", name);
        int outside = walk.OpenOutsideWalk();
        return outside > 0 ? text + " " + Texts.T("tagging.status.moreOpen", outside.ToString(Texts.Culture)) : text;
    }

    internal static string Undone(TaggingUndoResult undone) => Texts.T(undone.State switch
    {
        TagQueueState.Done => "tagging.status.undone.done",
        TagQueueState.Skipped => "tagging.status.undone.skipped",
        _ => "tagging.status.undone.open"
    }, undone.Name);

    /// <summary>The effective key of a command as the person sees it ("Enter", "↩"); the command's name when it has none.</summary>
    internal static string Key(string id) => Commands.CommandCatalog.ShortcutDisplay(id) ?? Commands.CommandCatalog.Label(id);

    /// <summary>The note under the keys in F1: paging without a decision, the Esc stages and the shared undo.</summary>
    internal static string HelpNote() => Texts.T("tagging.help.note", Key("gallery.next"), Key("gallery.previous"), Key(Commands.QuickTagCommands.Undo));

    /// <summary>The first line of a walk: how to decide, and the media that could not be shown.</summary>
    internal static string Start(TaggingWalk walk)
    {
        string text = Texts.T("tagging.status.start", Key("tags.queue.done-next"), Key("tags.queue.skip"), Key("tags.queue.back"));
        return walk.Unavailable > 0 ? text + " " + Texts.T("tagging.status.unavailable", walk.Unavailable.ToString(Texts.Culture)) : text;
    }
}
