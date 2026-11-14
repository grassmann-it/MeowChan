using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Desktop.Localization;
using MeowChan.Tags;

namespace MeowChan.Desktop;

/// <summary>
/// Why the quick tag surface offers a suggestion (PLAN-SCHNELL-TAGGEN §2.2). The strongest contribution names the chip; the
/// text is shown in its tooltip and accessible name so the ranking stays explainable.
/// </summary>
internal enum QuickTagReason { Frequent, Recent, Thread, Neighbour, SameFile, FileName, Title, SourceTag }

/// <summary>
/// One ranked suggestion of the quick tag strip: an own tag (<see cref="Id"/>) or a name that choosing creates as an own tag
/// (a provider source tag or a title marker; <see cref="Id"/> null). <see cref="Score"/> is the context score of the ranking,
/// <see cref="Detail"/> an optional value of the reason text (the board of a <see cref="QuickTagReason.SameFile"/> copy).
/// The context ranking (TP0, <c>RankedTagSuggestion</c>) maps onto this record one to one: tag, score, strongest reason, uses.
/// </summary>
internal sealed record QuickTagSuggestion(PrivateTagName Name, TagId? Id, double Score, QuickTagReason Reason, int Uses, string? Detail = null)
{
    /// <summary>True when choosing it creates an own tag of this name instead of assigning an existing one.</summary>
    internal bool CreatesTag => Id == null;
}

/// <summary>What the strip asks for: the shown tags, tags it must never offer (already carried) and how many chips fit.</summary>
internal sealed record QuickTagSuggestionRequest(PrivateTagStoreSnapshot Snapshot, IReadOnlySet<TagId> Excluded, int Limit);

/// <summary>
/// Supplies the suggestion strip for an empty field, best first. Offline only and cheap per call (the surface asks after every
/// change and medium switch). The context source of TP0 implements it; <see cref="FrequentQuickTagSuggestions"/> is the default.
/// </summary>
internal interface IQuickTagSuggestionSource
{
    IReadOnlyList<QuickTagSuggestion> Suggest(QuickTagSuggestionRequest request);
}

/// <summary>
/// The suggestion source without context: the most used own tags (Q4 of the plan), scored min(3, log2(uses + 1)). Used until a
/// host passes the context source, and wherever no context exists.
/// </summary>
internal sealed class FrequentQuickTagSuggestions : IQuickTagSuggestionSource
{
    internal static readonly FrequentQuickTagSuggestions Instance = new();

    public IReadOnlyList<QuickTagSuggestion> Suggest(QuickTagSuggestionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return TagSuggestionIndex.For(request.Snapshot).MostUsed(request.Excluded, request.Limit)
            .Where(item => item.Id != null)
            .Select(item => new QuickTagSuggestion(item.Name, item.Id, Math.Min(3, Math.Log2(item.Uses + 1)), QuickTagReason.Frequent, item.Uses))
            .ToArray();
    }
}

/// <summary>A suggestion source from a function, for hosts that compute the context themselves and for tests.</summary>
internal sealed class DelegateQuickTagSuggestions(Func<QuickTagSuggestionRequest, IReadOnlyList<QuickTagSuggestion>> suggest) : IQuickTagSuggestionSource
{
    private readonly Func<QuickTagSuggestionRequest, IReadOnlyList<QuickTagSuggestion>> _suggest = suggest ?? throw new ArgumentNullException(nameof(suggest));
    public IReadOnlyList<QuickTagSuggestion> Suggest(QuickTagSuggestionRequest request) => _suggest(request);
}

/// <summary>
/// Limits of the quick tag surface. Each one becomes a setting in TP5 (<c>QuickTagUndoDepth</c>, <c>QuickTagSuggestionCount</c>);
/// until then the defaults apply. Out-of-range values are clamped, never refused.
/// </summary>
internal sealed record QuickTagOptions
{
    internal const int DefaultUndoDepth = 20, MinimumUndoDepth = 1, MaximumUndoDepth = 100;
    internal const int DefaultSuggestionCount = 9, MinimumSuggestionCount = 3, MaximumSuggestionCount = 18;
    /// <summary>Suggestions 1–9 get a number and the Alt/⌘ shortcut; more chips are shown without one.</summary>
    internal const int NumberedSuggestions = 9;
    internal static readonly QuickTagOptions Default = new();

    internal int UndoDepth { get; init; } = DefaultUndoDepth;
    internal int SuggestionCount { get; init; } = DefaultSuggestionCount;

    internal QuickTagOptions Normalized() => this with
    {
        UndoDepth = Math.Clamp(UndoDepth, MinimumUndoDepth, MaximumUndoDepth),
        SuggestionCount = Math.Clamp(SuggestionCount, MinimumSuggestionCount, MaximumSuggestionCount)
    };
}

/// <summary>
/// What the quick tag surface writes to: one exact media file (<see cref="MediaTagSession"/>) or the chosen occurrences of a
/// selection (<see cref="MediaSelectionTagSession"/>). Both sessions keep their frozen subjects, one-write semantics and errors;
/// this only gives them one shape. Create with <see cref="QuickTagTarget.For(MediaTagSession)"/>.
/// </summary>
internal interface IQuickTagTarget
{
    /// <summary>Null while the tag index is unavailable; then nothing can be changed.</summary>
    PrivateTagStoreSnapshot? Snapshot { get; }
    bool Busy { get; }
    string? MessageKey { get; }
    TagDialogChange? LastChange { get; }
    /// <summary>True for a selection: chips are tri-state and texts count files.</summary>
    bool IsSelection { get; }
    TagCoverage Coverage();
    Task AddAsync(string name, CancellationToken token);
    Task AddExistingAsync(TagId tag, CancellationToken token);
    Task RemoveAsync(TagId tag, CancellationToken token);
    /// <summary>Writes the reverse of <paramref name="change"/> for exactly its subjects (any earlier step, any medium).</summary>
    Task RevertAsync(TagDialogChange change, CancellationToken token);
    void Reload();
    event Action? Updated;
}

internal static class QuickTagTarget
{
    internal static IQuickTagTarget For(MediaTagSession session) => new Single(session ?? throw new ArgumentNullException(nameof(session)));
    internal static IQuickTagTarget For(MediaSelectionTagSession session) => new Selection(session ?? throw new ArgumentNullException(nameof(session)));

    private sealed class Single(MediaTagSession session) : IQuickTagTarget
    {
        public PrivateTagStoreSnapshot? Snapshot => session.Snapshot;
        public bool Busy => session.Busy;
        public string? MessageKey => session.MessageKey;
        public TagDialogChange? LastChange => session.LastChange;
        public bool IsSelection => false;
        public TagCoverage Coverage() => session.Coverage();
        public Task AddAsync(string name, CancellationToken token) => session.AddAsync(name, token);
        public Task AddExistingAsync(TagId tag, CancellationToken token) => session.AddExistingAsync(tag, token);
        public Task RemoveAsync(TagId tag, CancellationToken token) => session.RemoveAsync(tag, token);
        public Task RevertAsync(TagDialogChange change, CancellationToken token) => session.RevertAsync(change, token);
        public void Reload() => session.Reload();
        public event Action? Updated { add => session.Updated += value; remove => session.Updated -= value; }
    }

    private sealed class Selection(MediaSelectionTagSession session) : IQuickTagTarget
    {
        public PrivateTagStoreSnapshot? Snapshot => session.Snapshot;
        public bool Busy => session.Busy;
        public string? MessageKey => session.MessageKey;
        public TagDialogChange? LastChange => session.LastChange;
        public bool IsSelection => true;
        public TagCoverage Coverage() => session.Coverage();
        public Task AddAsync(string name, CancellationToken token) => session.AddAsync(name, token);
        public Task AddExistingAsync(TagId tag, CancellationToken token) => session.AddExistingAsync(tag, token);
        public Task RemoveAsync(TagId tag, CancellationToken token) => session.RemoveAsync(tag, token);
        public Task RevertAsync(TagDialogChange change, CancellationToken token) => session.RevertAsync(change, token);
        public void Reload() => session.Reload();
        public event Action? Updated { add => session.Updated += value; remove => session.Updated -= value; }
    }
}

/// <summary>What the status line of the surface says after the last attempt.</summary>
internal enum QuickTagStatusKind { None, Added, Removed, UndoneAdd, UndoneRemove, Error, Unavailable }

/// <summary>
/// The status line: the last change (polite), its undo, or a refused write (assertive, kept until the next successful change).
/// </summary>
internal sealed record QuickTagStatus(QuickTagStatusKind Kind, string Name = "", int Files = 0, string? MessageKey = null, string? Medium = null)
{
    internal static readonly QuickTagStatus None = new(QuickTagStatusKind.None);
    internal bool IsError => Kind is QuickTagStatusKind.Error or QuickTagStatusKind.Unavailable;

    internal string Text => Kind switch
    {
        QuickTagStatusKind.Added => Files > 1 ? Texts.Count("tags.dialog.addedMany", Files, Name, Files) : Texts.T("tags.dialog.added", Name),
        QuickTagStatusKind.Removed => Files > 1 ? Texts.Count("tags.dialog.removedMany", Files, Name, Files) : Texts.T("tags.dialog.removed", Name),
        // An undo that reached a step of another medium (the gallery panel moved on) names that medium.
        QuickTagStatusKind.UndoneAdd => Medium == null ? Texts.T("tags.quick.status.undoneAdd", Name) : Texts.T("tags.quick.status.undoneAddOn", Name, Medium),
        QuickTagStatusKind.UndoneRemove => Medium == null ? Texts.T("tags.quick.status.undoneRemove", Name) : Texts.T("tags.quick.status.undoneRemoveOn", Name, Medium),
        QuickTagStatusKind.Error => Texts.T(MessageKey ?? "tags.error.other"),
        QuickTagStatusKind.Unavailable => Texts.T("tags.unavailable"),
        _ => string.Empty
    };
}

/// <summary>One tag carried by the target: by every file (<see cref="TagToggleState.All"/>) or, in a selection, by some.</summary>
internal sealed record QuickTagAssignment(PrivateTagDefinition Tag, TagToggleState State, int Carriers, int Total);

/// <summary>
/// The model of one quick tag panel or popover: the target it writes to (switchable while the gallery moves on), a multi-step
/// undo stack and the status line. Every change is one write of the wrapped session; an undo writes the exact reverse for the
/// subjects that change touched, so undoing an older step never resets other tags. The stack lives as long as the surface
/// (a medium switch is no reset) and keeps at most <see cref="UndoDepth"/> steps. A tag a change created stays in the
/// vocabulary after its undo. No UI and no storage.
/// </summary>
internal sealed class QuickTagSession : IDisposable
{
    private readonly List<TagDialogChange> _undo = new();
    // Where each step was made (the target and the host's name for it), so an undo can say when it reaches another medium.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<TagDialogChange, StepOrigin> _origins = new();
    private IQuickTagTarget _target;
    private bool _running;
    private bool _disposed;

    internal QuickTagSession(IQuickTagTarget target, int undoDepth = QuickTagOptions.DefaultUndoDepth)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        UndoDepth = Math.Clamp(undoDepth, QuickTagOptions.MinimumUndoDepth, QuickTagOptions.MaximumUndoDepth);
        _target.Updated += OnTargetUpdated;
    }

    internal IQuickTagTarget Target => _target;
    /// <summary>The host's name for the medium of the target (the gallery panel: the file name); set before <see cref="Retarget"/>.
    /// Steps remember it, and an undo of a step made on another target names that medium in the status. Null: never named.</summary>
    internal string? Label { get; set; }
    internal int UndoDepth { get; }
    /// <summary>The undoable steps, oldest first; the last one is undone next.</summary>
    internal IReadOnlyList<TagDialogChange> UndoSteps => _undo;
    internal PrivateTagStoreSnapshot? Snapshot => _target.Snapshot;
    internal bool Busy => _running || _target.Busy;
    internal bool Ready => Snapshot != null && !Busy;
    internal bool CanUndo => _undo.Count > 0 && Ready;
    internal bool IsSelection => _target.IsSelection;
    internal QuickTagStatus Status => Snapshot == null ? new QuickTagStatus(QuickTagStatusKind.Unavailable) : _status;
    private QuickTagStatus _status = QuickTagStatus.None;

    /// <summary>Raised after every state change of the session or its target, on the thread that caused it.</summary>
    internal event Action? Updated;
    /// <summary>Every accepted change (not undos): the recently-used memory of the context ranking listens here.</summary>
    internal event Action<TagDialogChange>? Applied;

    /// <summary>
    /// Writes to another target from now on (the gallery panel follows the shown medium; marking turns it into a selection).
    /// The undo stack and the status stay: each step remembers its own subjects.
    /// </summary>
    internal void Retarget(IQuickTagTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (ReferenceEquals(target, _target)) return;
        _target.Updated -= OnTargetUpdated;
        _target = target;
        if (!_disposed) _target.Updated += OnTargetUpdated;
        Updated?.Invoke();
    }

    internal TagCoverage Coverage() => Snapshot == null ? TagCoverage.Empty : _target.Coverage();

    internal TagToggleState StateOf(TagId tag) => Coverage().StateOf(tag);

    /// <summary>Tags carried by at least one file of the target, in vocabulary order.</summary>
    internal IReadOnlyList<QuickTagAssignment> Assignments()
    {
        if (Snapshot is not { } snapshot) return Array.Empty<QuickTagAssignment>();
        TagCoverage coverage = _target.Coverage();
        return snapshot.Tags.Where(tag => coverage.Carriers.GetValueOrDefault(tag.Id) > 0)
            .Select(tag => new QuickTagAssignment(tag, coverage.StateOf(tag.Id), coverage.Carriers.GetValueOrDefault(tag.Id), coverage.Total))
            .ToArray();
    }

    /// <summary>Tags the strip never offers: every tag that at least one file already carries (those are chips).</summary>
    internal IReadOnlySet<TagId> Carried() => Coverage().Carriers.Where(item => item.Value > 0).Select(item => item.Key).ToHashSet();

    /// <summary>Tags the field does not suggest: those on every file. A tag only some files carry can still be typed to give it to all.</summary>
    internal IReadOnlySet<TagId> OnAll()
    {
        TagCoverage coverage = Coverage();
        return coverage.Carriers.Where(item => coverage.StateOf(item.Key) == TagToggleState.All).Select(item => item.Key).ToHashSet();
    }

    /// <summary>Create-or-get by name and give it to the target (Enter, comma, a "new" suggestion).</summary>
    internal Task AddAsync(string? name, CancellationToken token = default) =>
        string.IsNullOrWhiteSpace(name) ? Task.CompletedTask : RunAsync(target => target.AddAsync(name, token));

    /// <summary>
    /// A chip toggle: a tag on every file is taken off all of them; otherwise (none or some) it is given to all
    /// (the tri-state rule of the selection dialog).
    /// </summary>
    internal Task ToggleAsync(TagId tag, CancellationToken token = default) =>
        StateOf(tag) == TagToggleState.All ? RemoveAsync(tag, token) : RunAsync(target => target.AddExistingAsync(tag, token));

    /// <summary>Takes the tag off every file of the target that carries it.</summary>
    internal Task RemoveAsync(TagId tag, CancellationToken token = default) => RunAsync(target => target.RemoveAsync(tag, token));

    /// <summary>A suggestion chip: an own tag toggles, a name creates the own tag and assigns it.</summary>
    internal Task AcceptAsync(QuickTagSuggestion suggestion, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        return suggestion.Id is { } id && Snapshot?.Find(id) != null ? ToggleAsync(id, token) : AddAsync(suggestion.Name.Display, token);
    }

    /// <summary>
    /// Undoes the newest step for exactly its subjects. A refused undo keeps the step for another try, unless its tag no longer
    /// exists (then there is nothing to restore and the step is dropped).
    /// </summary>
    internal async Task UndoAsync(CancellationToken token = default)
    {
        if (!CanUndo) return;
        TagDialogChange change = _undo[^1];
        IQuickTagTarget target = _target;
        string? medium = _origins.TryGetValue(change, out StepOrigin? origin) && !ReferenceEquals(origin.Target, target)
            && !string.IsNullOrWhiteSpace(origin.Label) ? origin.Label : null;
        _running = true;
        Updated?.Invoke();
        try { await target.RevertAsync(change, token); }
        finally { _running = false; }
        if (target.MessageKey is { } key)
        {
            _status = new QuickTagStatus(QuickTagStatusKind.Error, change.Name, change.Subjects.Count, key);
            if (target.Snapshot is { } snapshot && snapshot.Find(change.Tag) == null) _undo.Remove(change);
        }
        else
        {
            _undo.Remove(change);
            _status = new QuickTagStatus(change.Assigned ? QuickTagStatusKind.UndoneAdd : QuickTagStatusKind.UndoneRemove, change.Name, change.Subjects.Count, Medium: medium);
        }
        Updated?.Invoke();
    }

    private async Task RunAsync(Func<IQuickTagTarget, Task> write)
    {
        if (!Ready) return;
        IQuickTagTarget target = _target;
        string? label = Label;
        TagDialogChange? before = target.LastChange;
        _running = true;
        Updated?.Invoke();
        try { await write(target); }
        finally { _running = false; }
        TagDialogChange? applied = null;
        if (target.MessageKey is { } key) _status = new QuickTagStatus(QuickTagStatusKind.Error, MessageKey: key);
        else if (target.LastChange is { } change && !ReferenceEquals(change, before))
        {
            Push(change);
            _origins.AddOrUpdate(change, new StepOrigin(target, label));
            applied = change;
            _status = new QuickTagStatus(change.Assigned ? QuickTagStatusKind.Added : QuickTagStatusKind.Removed, change.Name, change.Subjects.Count);
        }
        // Nothing changed (the tag was already there): a former error is no longer current.
        else if (_status.IsError) _status = QuickTagStatus.None;
        if (applied != null) Applied?.Invoke(applied);
        Updated?.Invoke();
    }

    private void Push(TagDialogChange change)
    {
        _undo.Add(change);
        if (_undo.Count > UndoDepth) _undo.RemoveRange(0, _undo.Count - UndoDepth);
    }

    private void OnTargetUpdated() { if (!_disposed) Updated?.Invoke(); }

    private sealed record StepOrigin(IQuickTagTarget Target, string? Label);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _target.Updated -= OnTargetUpdated;
        Updated = null;
        Applied = null;
    }
}
