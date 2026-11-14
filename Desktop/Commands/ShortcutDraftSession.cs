using System;
using System.Collections.Generic;
using System.Linq;

namespace MeowChan.Desktop.Commands;

/// <summary>Why a <see cref="ShortcutDraftSession"/> changed.</summary>
public enum ShortcutDraftChange
{
    /// <summary>An explicit user edit (set, clear, restore, reset, single keys, undo).</summary>
    Edited,
    /// <summary>The draft was replaced from outside (load, discard, commit); recordings in progress are void.</summary>
    Reloaded,
    /// <summary>A save finished while the draft had newer edits: only the baseline moved, the draft and recordings are untouched.</summary>
    Rebased
}

/// <summary>What happened to a recorded gesture offered to <see cref="ShortcutDraftSession.TryAdd"/>.</summary>
public enum ShortcutAddResult { Applied, Invalid, Limit, ReadOnly }

/// <summary>
/// The one shortcut draft of a settings surface (modals plan KS1): the Settings card and the shortcuts window edit the same
/// session, so they never disagree. It never persists or publishes; <see cref="ShortcutBindingsCommit"/> does that. Every
/// explicit edit can be undone in order until the next load.
/// </summary>
public sealed class ShortcutDraftSession
{
    /// <summary>Gestures per action. The profile schema rejects more (EffectiveShortcutBindings), so this is a fixed ceiling, not a setting.</summary>
    public const int GestureLimit = 8;

    /// <summary>
    /// Edits that can be undone in a row (the oldest are dropped). Fixed ceiling, not a setting: it only bounds the memory of the
    /// history, since every step holds a whole section.
    /// </summary>
    public const int UndoDepth = 200;

    // The newest step last; a list because the oldest step falls off at the ceiling.
    private readonly List<ShortcutBindingsSection> _undo = new();
    private ShortcutBindingsSection _baseline = new();
    private ShortcutBindingsSection _draft = new();
    private EffectiveShortcutBindings? _snapshot;
    private ShortcutPlatform? _snapshotPlatform;

    public ShortcutDraftSession() { }
    public ShortcutDraftSession(ShortcutBindingsSection committed) => Load(committed);

    /// <summary>The draft as it is now. Replaced (never mutated) by every edit.</summary>
    public ShortcutBindingsSection Draft => _draft;
    /// <summary>The section the draft was loaded from: the committed profile state the change count compares against.</summary>
    public ShortcutBindingsSection Baseline => _baseline;
    /// <summary>An explicit edit happened since the last load.</summary>
    public bool IsDirty { get; private set; }
    public bool IsEditable => _draft.IsEditable;
    public bool CanUndo => _undo.Count > 0;
    /// <summary>Steps <see cref="Undo"/> can take back now (at most <see cref="UndoDepth"/>).</summary>
    public int UndoCount => _undo.Count;
    public event EventHandler<ShortcutDraftChange>? Changed;

    /// <summary>The validated view of the draft for <paramref name="platform"/> (cached until the next change).</summary>
    public EffectiveShortcutBindings Snapshot(ShortcutPlatform? platform = null)
    {
        ShortcutPlatform os = platform ?? EffectiveShortcutBindings.CurrentPlatform;
        if (_snapshot == null || _snapshotPlatform != os) { _snapshot = EffectiveShortcutBindings.Build(_draft, os); _snapshotPlatform = os; }
        return _snapshot;
    }

    public IReadOnlyList<ShortcutIssue> Issues => Snapshot().Issues;
    public IReadOnlyList<ShortcutIssue> BlockingIssues => Issues.Where(issue => issue.BlocksSave).ToArray();
    /// <summary>True when Save would be refused (a conflict or an invalid saved gesture).</summary>
    public bool HasBlockingIssues => Issues.Any(issue => issue.BlocksSave);

    /// <summary>The gestures the draft asks for (before conflicts are removed), as the editor shows them.</summary>
    public IReadOnlyList<ShortcutGesture> Requested(string id) =>
        Snapshot().RequestedGestures.TryGetValue(id, out var gestures) ? gestures : Array.Empty<ShortcutGesture>();

    /// <summary>Replaces the draft and the baseline; clears dirty state and the undo history.</summary>
    public void Load(ShortcutBindingsSection committed)
    {
        ArgumentNullException.ThrowIfNull(committed);
        _baseline = committed.Clone();
        _draft = committed.Clone();
        _undo.Clear();
        IsDirty = false;
        Invalidate();
        Changed?.Invoke(this, ShortcutDraftChange.Reloaded);
    }

    /// <summary>The draft has been committed as is: it becomes the baseline, nothing is pending any more.</summary>
    public void MarkCommitted() => Load(_draft);

    /// <summary>
    /// <paramref name="saved"/> (the draft as it was handed to the save) was persisted. It becomes the baseline. When the draft is still
    /// that section this is <see cref="MarkCommitted"/>; when either surface edited the draft while the save ran, those edits stay in
    /// the draft (and in the undo history), the session stays dirty and the change count is taken against the saved state.
    /// </summary>
    public void CommitSaved(ShortcutBindingsSection saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        if (saved.Raw.GetRawText() == _draft.Raw.GetRawText()) { Load(saved); return; }
        _baseline = saved.Clone();
        IsDirty = true;
        Changed?.Invoke(this, ShortcutDraftChange.Rebased);
    }

    /// <summary>Back to the baseline (Discard).</summary>
    public void Discard() => Load(_baseline);

    /// <summary>Replaces the gestures of <paramref name="id"/>; null restores its default. A read-only (unknown schema) draft stays as it is.</summary>
    public void Set(string id, IReadOnlyList<ShortcutGesture>? gestures) { if (_draft.IsEditable) Edit(_draft.Set(id, gestures)); }
    public void Clear(string id) => Set(id, Array.Empty<ShortcutGesture>());
    public void RestoreDefault(string id) => Set(id, null);
    public void RemoveGesture(string id, int index)
    {
        IReadOnlyList<ShortcutGesture> gestures = Requested(id);
        if (index < 0 || index >= gestures.Count) throw new ArgumentOutOfRangeException(nameof(index));
        Set(id, gestures.Where((_, n) => n != index).ToArray());
    }
    public void ResetKnown() { if (_draft.IsEditable) Edit(_draft.ResetKnown()); }
    public void SetSingleKeyShortcuts(bool enabled)
    {
        if (!_draft.IsEditable || _draft.SingleKeyShortcuts == enabled) return;
        Edit(_draft.WithSingleKeyShortcuts(enabled));
    }

    /// <summary>
    /// Offers a recorded gesture to <paramref name="id"/>: appended to its gestures, or with <paramref name="replace"/> as its only
    /// one. With <paramref name="takeOver"/> every other action that can run at the same time loses an equivalent gesture,
    /// otherwise the draft keeps the conflict and Save explains it.
    /// </summary>
    public ShortcutAddResult TryAdd(string id, ShortcutGesture gesture, bool takeOver, bool replace = false, ShortcutPlatform? platform = null)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        if (!_draft.IsEditable) return ShortcutAddResult.ReadOnly;
        ShortcutPlatform os = platform ?? EffectiveShortcutBindings.CurrentPlatform;
        if (EffectiveShortcutBindings.InvalidIssueReason(gesture, os) != null) return ShortcutAddResult.Invalid;
        List<ShortcutGesture> list = replace ? new() : Requested(id).ToList();
        if (list.Count >= GestureLimit) return ShortcutAddResult.Limit;
        if (!list.Any(g => EffectiveShortcutBindings.Equivalent(g, gesture, os))) list.Add(gesture);
        ShortcutBindingsSection next = _draft;
        if (takeOver)
            foreach (CommandDefinition command in CommandCatalog.All.Where(c => c.Id != id && EffectiveShortcutBindings.ContextsOverlap(c.Id, id)))
            {
                IReadOnlyList<ShortcutGesture> other = Requested(command.Id);
                ShortcutGesture[] remaining = other.Where(g => !EffectiveShortcutBindings.Equivalent(g, gesture, os)).ToArray();
                if (remaining.Length != other.Count) next = next.Set(command.Id, remaining);
            }
        Edit(next.Set(id, list));
        return ShortcutAddResult.Applied;
    }

    /// <summary>Takes back the last explicit edit; false when there is none.</summary>
    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        _draft = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        IsDirty = true;
        Invalidate();
        Changed?.Invoke(this, ShortcutDraftChange.Edited);
        return true;
    }

    /// <summary>Actions whose saved binding differs from the baseline, plus one for a changed single-key switch.</summary>
    public int ChangeCount
    {
        get
        {
            Dictionary<string, string> before = _baseline.Overrides.ToDictionary(p => p.Name, p => p.Value.GetRawText(), StringComparer.Ordinal);
            Dictionary<string, string> after = _draft.Overrides.ToDictionary(p => p.Name, p => p.Value.GetRawText(), StringComparer.Ordinal);
            int changed = before.Keys.Union(after.Keys, StringComparer.Ordinal)
                .Count(id => !before.TryGetValue(id, out string? a) || !after.TryGetValue(id, out string? b) || a != b);
            return changed + (_baseline.SingleKeyShortcuts != _draft.SingleKeyShortcuts ? 1 : 0);
        }
    }

    /// <summary>Known actions with a saved binding of their own in the draft.</summary>
    public int CustomCount => _draft.Overrides.Count(p => CommandCatalog.TryGet(p.Name, out _));

    private void Edit(ShortcutBindingsSection next)
    {
        if (next.Raw.GetRawText() == _draft.Raw.GetRawText()) return;
        _undo.Add(_draft);
        if (_undo.Count > UndoDepth) _undo.RemoveAt(0);
        _draft = next;
        IsDirty = true;
        Invalidate();
        Changed?.Invoke(this, ShortcutDraftChange.Edited);
    }

    private void Invalidate() { _snapshot = null; _snapshotPlatform = null; }
}
