using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan.Desktop;

/// <summary>Where the shown medium sits in the gallery list: "Medium 4 of 27"; <see cref="More"/> when more threads are still to be resolved.</summary>
internal readonly record struct GalleryTagPanelPosition(int Number, int Count, bool More);

/// <summary>What the gallery tag panel needs from the window that owns the gallery: targets, the list, the session-wide recent list,
/// limits and the full dialog. The viewer only shows state and raises requests; this is the one place that touches the tag store.</summary>
internal interface IGalleryTagPanelOwner
{
    /// <summary>A target frozen to exactly this entry (the same resolver as the full dialog), or null with the reason the medium cannot be tagged.</summary>
    IQuickTagTarget? OpenTarget(GalleryEntry entry, out string? unavailableReason);
    /// <summary>
    /// The exact tag subject of an entry (the same resolver as <see cref="OpenTarget"/>), or null with the reason it cannot be
    /// tagged: marking needs it, and a medium without one is refused with that reason.
    /// </summary>
    MediaTagSubject? SubjectOf(GalleryEntry entry, out string? unavailableReason);
    /// <summary>
    /// A target over exactly these subjects (the marked media): one session whose chips are tri-state and whose writes cover all of
    /// them at once. Null with the reason when the tag index is unavailable.
    /// </summary>
    IQuickTagTarget? OpenSelectionTarget(IReadOnlyList<MediaTagSubject> subjects, out string? unavailableReason);
    /// <summary>The list the gallery walks (read on the UI thread only; the panel copies what it needs).</summary>
    IReadOnlyList<GalleryEntry> Entries { get; }
    GalleryTagPanelPosition? Position { get; }
    /// <summary>Tags used in this session, most recent first; the context ranking reads it.</summary>
    TagRecentList Recent { get; }
    QuickTagOptions Options { get; }
    TagContextOptions ContextOptions { get; }
    /// <summary>The profile's one stamp set (PT5), shared with the tag popover; null gives the panel an empty stamp of its own.</summary>
    QuickTagStamp? Stamp => null;
    BooruCatalogSnapshot? ReadSourceTags();
    /// <summary>Thread tags of <paramref name="thread"/> (the section Ctrl+T opens).</summary>
    GalleryThreadTagModel OpenThreadTags(ResourceId thread);
    /// <summary>Opens the full tag dialog for the entry ("Advanced…"); the panel reloads afterwards.</summary>
    void OpenAdvanced(GalleryEntry entry);
}

/// <summary>What a mark request did: the outcome, the medium's file name, how many media are marked now and, for a refusal, why.</summary>
internal sealed record GalleryMarkResult(GalleryMarkOutcome Outcome, string Label, int Count, int Limit, string? Reason = null)
{
    /// <summary>True when the set of marks changed (the target must be made again).</summary>
    internal bool Changed => Outcome is GalleryMarkOutcome.Marked or GalleryMarkOutcome.Unmarked;
}

/// <summary>What the surface shows for one medium: the frozen target, the file name, the counter text and, for a medium that cannot be tagged, the reason.</summary>
internal sealed record GalleryTagPanelFrame(IQuickTagTarget Target, string Label, string? Counter, string? UnavailableReason);

/// <summary>The commands the gallery tag panel handles and how a command maps onto a key action of the surface.</summary>
internal static class GalleryTagPanelCommands
{
    /// <summary>Handled here, and so the ids <see cref="QuickTagCommands"/> counts as wired: paging, commit and next, marking
    /// (Shift+Page Down/Up, TP4), the thread section, undo, the stamp (PT5) and the nine suggestion toggles.</summary>
    internal static IReadOnlyList<string> Handled { get; } = Array.AsReadOnly(
        new[] { QuickTagCommands.Next, QuickTagCommands.Previous, QuickTagCommands.CommitNext, QuickTagCommands.MarkNext, QuickTagCommands.MarkPrevious,
            QuickTagCommands.ThreadSection, QuickTagCommands.Undo, QuickTagCommands.StampApply }
            .Concat(Enumerable.Range(1, QuickTagCommands.SuggestionCount).Select(QuickTagCommands.Suggestion)).ToArray());

    /// <summary>The surface action of a handled command; null for any other id.</summary>
    internal static QuickTagKeyRoute? RouteOf(string commandId)
    {
        switch (commandId)
        {
            case QuickTagCommands.Next: return new(QuickTagKeyAction.Next);
            case QuickTagCommands.Previous: return new(QuickTagKeyAction.Previous);
            case QuickTagCommands.CommitNext: return new(QuickTagKeyAction.CommitAndNext);
            case QuickTagCommands.MarkNext: return new(QuickTagKeyAction.MarkNext);
            case QuickTagCommands.MarkPrevious: return new(QuickTagKeyAction.UnmarkPrevious);
            case QuickTagCommands.ThreadSection: return new(QuickTagKeyAction.ThreadSection);
            case QuickTagCommands.Undo: return new(QuickTagKeyAction.Undo);
            case QuickTagCommands.StampApply: return new(QuickTagKeyAction.ApplyStamp);
        }
        for (int number = 1; number <= QuickTagCommands.SuggestionCount; number++)
            if (commandId == QuickTagCommands.Suggestion(number)) return new(QuickTagKeyAction.ToggleSuggestion, number - 1);
        return null;
    }

    /// <summary>How far a navigation request moves the gallery: +1 / -1; marking moves like paging (down, then up) and also changes the marks.</summary>
    internal static int Delta(QuickTagNavigation navigation) => navigation switch
    {
        QuickTagNavigation.Next or QuickTagNavigation.MarkNext => 1,
        QuickTagNavigation.Previous or QuickTagNavigation.UnmarkPrevious => -1,
        _ => 0
    };

    /// <summary>"Medium 4 of 27" for the counter chip and the live region; with <paramref name="marked"/> "Medium 4 of 27 · marked".
    /// Null without a position.</summary>
    internal static string? CounterText(GalleryTagPanelPosition? position, bool marked = false)
    {
        if (position is not { } value) return null;
        string text = Texts.T(value.More ? "tags.quick.gallery.positionMore" : "tags.quick.gallery.position",
            value.Number.ToString(Texts.Culture), value.Count.ToString(Texts.Culture));
        return marked ? Texts.T("tags.quick.marked.counter", text) : text;
    }

    /// <summary>What the polite status line says after a mark request; null when nothing changed and nothing needs saying.</summary>
    internal static string? MarkAnnouncement(GalleryMarkResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        string total = result.Count.ToString(Texts.Culture);
        return result.Outcome switch
        {
            GalleryMarkOutcome.Marked => Texts.T("tags.quick.marked.announce.added", result.Label, total),
            GalleryMarkOutcome.Unmarked => Texts.T("tags.quick.marked.announce.removed", result.Label, total),
            GalleryMarkOutcome.AlreadyMarked => Texts.T("tags.quick.marked.announce.already", result.Label, total),
            GalleryMarkOutcome.LimitReached => Texts.T("tags.quick.marked.announce.limit", result.Limit),
            GalleryMarkOutcome.Unavailable => Texts.T("tags.quick.marked.announce.unavailable", result.Label, result.Reason ?? string.Empty),
            _ => null
        };
    }

    internal static string ClearedAnnouncement() => Texts.T("tags.quick.marked.announce.cleared");

    /// <summary>The label of a selection target: "3 media files".</summary>
    internal static string SelectionLabel(int count) => Texts.Count("tags.quick.files", count);

    internal static string LabelOf(GalleryEntry entry) =>
        entry.Asset.OriginalFileName ?? entry.Asset.RequestedFileName ?? Path.GetFileName(entry.Asset.Path);
}

/// <summary>
/// A medium that cannot be tagged (no bound version, tag index unavailable): the panel stays open on it with a disabled field and
/// the reason, instead of keeping the previous medium's target, which would write to the wrong file.
/// </summary>
internal sealed class GalleryTagUnavailableTarget : IQuickTagTarget
{
    internal GalleryTagUnavailableTarget(string? reason) => Reason = reason;
    internal string? Reason { get; }
    public PrivateTagStoreSnapshot? Snapshot => null;
    public bool Busy => false;
    public string? MessageKey => null;
    public TagDialogChange? LastChange => null;
    public bool Undone => false;
    public bool IsSelection => false;
    public TagCoverage Coverage() => TagCoverage.Empty;
    public Task AddAsync(string name, CancellationToken token) => Task.CompletedTask;
    public Task AddExistingAsync(TagId tag, CancellationToken token) => Task.CompletedTask;
    public Task RemoveAsync(TagId tag, CancellationToken token) => Task.CompletedTask;
    public Task RevertAsync(TagDialogChange change, CancellationToken token) => Task.CompletedTask;
    public void Reload() { }
    public event Action? Updated { add { } remove { } }
}

/// <summary>
/// The panel's model without UI: the quick tag session over a target per shown medium, the context suggestions with their
/// off-thread warm-up, the neighbour list, and the session-wide recent list fed by every accepted change. A medium switch is
/// <see cref="Frame"/>: a new frozen target (the undo stack stays), the file name and counter, and a new context.
/// With marks (E4, <see cref="Marks"/>) the target is one selection over the marked media instead, for every shown medium; it is made
/// again only when the marks change, and every step keeps the exact subjects it wrote, so an undo does not follow later marks.
/// Disposing it (the panel closed) cancels every background step; the marks belong to the gallery and stay.
/// </summary>
internal sealed class GalleryTagPanelController : IDisposable
{
    private readonly IGalleryTagPanelOwner _owner;
    private IQuickTagTarget? _selection;
    private int _selectionRevision = -1;
    private GalleryTagNeighbourSubjects? _neighbours;
    private CancellationTokenSource? _fill;
    private bool _disposed;
    // When each tag step was made, on the order shared with the decision steps (one undo key serves both stacks, newest first).
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<TagDialogChange, System.Runtime.CompilerServices.StrongBox<long>> _tagStamps = new();

    private GalleryTagPanelController(IGalleryTagPanelOwner owner, GalleryEntry entry, GalleryMarks marks)
    {
        _owner = owner;
        Entry = entry;
        Marks = marks;
        Suggestions = new GalleryTagPanelSuggestions(owner.ContextOptions) { Recent = () => owner.Recent.Snapshot() };
        IQuickTagTarget target = Resolve(entry, out string? reason);
        UnavailableReason = reason;
        Session = new QuickTagSession(target, owner.Options.UndoDepth, owner.Stamp)
        { Label = marks.Count > 0 ? GalleryTagPanelCommands.SelectionLabel(marks.Count) : GalleryTagPanelCommands.LabelOf(entry) };
        Session.Applied += OnApplied;
        Suggestions.Context = Context(entry);
        // Favorite and rating (PT6): only when the owner can read and write them; the panel then shows its row.
        if (owner is IGalleryDecisionOwner decisions)
        {
            Decisions = new GalleryDecisionSession(decisions, owner.Options.UndoDepth);
            ExtraUndo = new DecisionUndo(this);
        }
    }

    /// <summary>
    /// Creates the controller for the entry the panel opens on and starts warming the suggestion index for the current snapshot.
    /// <paramref name="marks"/> is the gallery's mark set (kept across panel openings); null starts an empty one.
    /// </summary>
    internal static GalleryTagPanelController Create(IGalleryTagPanelOwner owner, GalleryEntry entry, GalleryMarks? marks = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(entry);
        GalleryTagPanelController controller = new(owner, entry, marks ?? new GalleryMarks(owner.Options.MarkedMediaLimit));
        // Off the UI thread, before the first strip is asked for: the surface then finds the index warm or answers from the cold path.
        if (controller.Session.Snapshot is { } snapshot) controller.Suggestions.Warm(snapshot);
        return controller;
    }

    internal QuickTagSession Session { get; }
    /// <summary>Favorite and rating of the panel's targets with their own undo stack; null when the owner offers neither.</summary>
    internal GalleryDecisionSession? Decisions { get; }
    /// <summary>The stamp of the newest tag step (the order shared with favorite, rating and queue steps); 0 without one.</summary>
    internal long NewestTagStamp
    {
        get
        {
            IReadOnlyList<TagDialogChange> tags = Session.UndoSteps;
            return tags.Count > 0 && _tagStamps.TryGetValue(tags[^1], out System.Runtime.CompilerServices.StrongBox<long>? box) ? box.Value : 0;
        }
    }
    /// <summary>The decision stack as the second undo stack of the surface (shares its undo key and button); null without decisions.</summary>
    internal IQuickTagExtraUndo? ExtraUndo { get; }
    internal GalleryTagPanelSuggestions Suggestions { get; }
    internal GalleryEntry Entry { get; private set; }
    internal string? UnavailableReason { get; private set; }
    /// <summary>The media marked for one tag change. While it has any, the session writes to them all, whichever medium is shown.</summary>
    internal GalleryMarks Marks { get; }

    private IQuickTagTarget Resolve(GalleryEntry entry, out string? reason)
    {
        if (Marks.Count > 0) return Selection(out reason);
        IQuickTagTarget? target = _owner.OpenTarget(entry, out reason);
        return target ?? new GalleryTagUnavailableTarget(reason);
    }

    // One target per marks revision: paging through the gallery does not make it again (only reloads its snapshot).
    private IQuickTagTarget Selection(out string? reason)
    {
        reason = null;
        if (_selection != null && _selectionRevision == Marks.Revision)
        {
            _selection.Reload();
            return _selection;
        }
        _selection = null;
        IQuickTagTarget? target = _owner.OpenSelectionTarget(Marks.Snapshot(), out reason);
        if (target == null) return new GalleryTagUnavailableTarget(reason);
        _selectionRevision = Marks.Revision;
        return _selection = target;
    }

    /// <summary>The medium the gallery shows now: the owner's position is current as soon as a move is requested, before the picture changes.</summary>
    internal GalleryEntry ShownEntry() => EntryAt(_owner.Position is { } position ? position.Number - 1 : -1) ?? Entry;

    /// <summary>The medium before the shown one, or null at the start of the list.</summary>
    internal GalleryEntry? PreviousEntry() => EntryAt(_owner.Position is { } position ? position.Number - 2 : -1);

    private GalleryEntry? EntryAt(int index)
    {
        IReadOnlyList<GalleryEntry> entries = _owner.Entries;
        return index >= 0 && index < entries.Count ? entries[index] : null;
    }

    /// <summary>True when the entry's own tag subject is marked.</summary>
    internal bool IsMarked(GalleryEntry entry) =>
        Marks.Count > 0 && _owner.SubjectOf(entry, out _) is { } subject && Marks.IsMarked(subject);

    /// <summary>Marks the medium (Shift+Page Down). A medium that cannot be tagged is refused with the reason; marking is never a guess.</summary>
    internal GalleryMarkResult Mark(GalleryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Marks.Limit = _owner.Options.MarkedMediaLimit; // the setting may have changed since the panel opened
        string label = GalleryTagPanelCommands.LabelOf(entry);
        if (_owner.SubjectOf(entry, out string? reason) is not { } subject)
            return new GalleryMarkResult(GalleryMarkOutcome.Unavailable, label, Marks.Count, Marks.Limit, reason);
        return new GalleryMarkResult(Marks.Mark(subject, entry), label, Marks.Count, Marks.Limit);
    }

    /// <summary>Takes the mark off the medium (Shift+Page Up on the one it goes back to).</summary>
    internal GalleryMarkResult Unmark(GalleryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string label = GalleryTagPanelCommands.LabelOf(entry);
        GalleryMarkOutcome outcome = _owner.SubjectOf(entry, out _) is { } subject ? Marks.Unmark(subject) : GalleryMarkOutcome.NotMarked;
        return new GalleryMarkResult(outcome, label, Marks.Count, Marks.Limit);
    }

    /// <summary>Removes every mark; the panel then writes to the shown medium again. Returns how many there were.</summary>
    internal int ClearMarks() => Marks.Clear();

    /// <summary>The counter chip's text for <paramref name="entry"/>: position, with "marked" when this medium is.</summary>
    internal string? CounterFor(GalleryEntry entry) => GalleryTagPanelCommands.CounterText(_owner.Position, IsMarked(entry));

    /// <summary>
    /// Moves the panel to <paramref name="entry"/>: a fresh target frozen to exactly that entry, the session's medium name and the
    /// suggestion context. The caller hands the result to <see cref="QuickTagSurface.Retarget"/> (the label is set first so a step
    /// made now remembers its medium).
    /// </summary>
    internal GalleryTagPanelFrame Frame(GalleryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Entry = entry;
        IQuickTagTarget target = Resolve(entry, out string? reason);
        UnavailableReason = reason;
        string label = Marks.Count > 0 ? GalleryTagPanelCommands.SelectionLabel(Marks.Count) : GalleryTagPanelCommands.LabelOf(entry);
        Session.Label = label;
        Suggestions.Context = Context(entry);
        return new GalleryTagPanelFrame(target, label, CounterFor(entry), reason);
    }

    /// <summary>What is known about the medium: thread, bytes, file name, subject, neighbours of the list, session-recent tags.</summary>
    internal TagContext Context(GalleryEntry entry)
    {
        GalleryTagPanelPosition? position = _owner.Position;
        GalleryTagNeighbourSubjects neighbours = Neighbours();
        int index = position is { } value && value.Number >= 1 && value.Number <= neighbours.Count ? value.Number - 1 : -1;
        return new TagContext
        {
            Thread = entry.Key.Thread,
            Sha256 = entry.Asset.Sha256,
            FileName = GalleryTagPanelCommands.LabelOf(entry),
            TitleTexts = new[] { entry.Post?.Subject },
            Neighbours = index < 0 ? TagNeighbourhood.None : neighbours.Around(index),
            Recent = _owner.Recent.Snapshot()
        };
    }

    // The list is copied and mapped to subjects on a worker; a change of its length (more threads resolved) maps it again.
    private GalleryTagNeighbourSubjects Neighbours()
    {
        IReadOnlyList<GalleryEntry> entries = _owner.Entries;
        if (_neighbours != null && _neighbours.Count == entries.Count) return _neighbours;
        _fill?.Cancel();
        GalleryTagNeighbourSubjects neighbours = _neighbours = new GalleryTagNeighbourSubjects(entries);
        CancellationTokenSource fill = _fill = new CancellationTokenSource();
        CancellationToken token = fill.Token;
        _ = Task.Run(() => { if (neighbours.Fill(token) && !token.IsCancellationRequested) Suggestions.NotifyReady(); });
        return neighbours;
    }

    /// <summary>
    /// The media a favorite or rating change writes to: every marked medium while there are marks (as the tags do), otherwise the one the
    /// panel is on. Read when the request is made, so a later move or mark never redirects it.
    /// </summary>
    internal IReadOnlyList<GalleryEntry> DecisionTargets()
    {
        IReadOnlyList<GalleryEntry> marked = Marks.Entries();
        return Marks.Count > 0 && marked.Count > 0 ? marked : new[] { Entry };
    }

    /// <summary>What the row shows now for <see cref="DecisionTargets"/>; null without decisions.</summary>
    internal GalleryDecisionView? DecisionView() => Decisions?.View(DecisionTargets());

    private void OnApplied(TagDialogChange change)
    {
        _tagStamps.AddOrUpdate(change, new System.Runtime.CompilerServices.StrongBox<long>(GalleryDecisionSession.NextStamp()));
        // Giving a tag is a use; taking one off is not.
        if (change.Assigned) _owner.Recent.Touch(change.Tag);
    }

    internal void OpenAdvanced() => _owner.OpenAdvanced(Entry);

    internal GalleryThreadTagModel OpenThreadTags() => _owner.OpenThreadTags(Entry.Key.Thread);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _fill?.Cancel(); } catch (ObjectDisposedException) { }
        Suggestions.Dispose();
        Session.Applied -= OnApplied;
        Session.Dispose();
        Decisions?.Dispose();
    }

    private sealed class DecisionUndo(GalleryTagPanelController owner) : IQuickTagExtraUndo
    {
        public int Steps => owner.Decisions!.UndoSteps.Count;
        public bool CanUndo => owner.Decisions!.CanUndo;
        public bool IsNewest
        {
            get
            {
                GalleryDecisionSession decisions = owner.Decisions!;
                if (decisions.UndoSteps.Count == 0) return false;
                IReadOnlyList<TagDialogChange> tags = owner.Session.UndoSteps;
                if (tags.Count == 0) return true;
                long tag = owner._tagStamps.TryGetValue(tags[^1], out System.Runtime.CompilerServices.StrongBox<long>? box) ? box.Value : 0;
                return decisions.NewestStamp > tag;
            }
        }
        public async Task<string?> UndoAsync()
        {
            GalleryDecisionStatus status = await owner.Decisions!.UndoAsync();
            return status.Kind == GalleryDecisionStatusKind.None ? null : status.Text();
        }
        public event Action? Changed { add => owner.Decisions!.Updated += value; remove => owner.Decisions!.Updated -= value; }
    }
}

/// <summary>
/// The thread tags of the shown medium's thread (the panel's Ctrl+T section): list, add by name (create-or-get, then give to the thread),
/// remove. Same store functions and error texts as the archive page's tag bar; no UI and no storage.
/// </summary>
internal sealed class GalleryThreadTagModel
{
    private readonly Func<PrivateTagLoadSnapshot> _load;
    private readonly Func<string, CancellationToken, Task<PrivateTagCommit>> _define;
    private readonly Func<TagId, ResourceId, bool, CancellationToken, Task<PrivateTagCommit>> _set;

    internal GalleryThreadTagModel(ResourceId thread, Func<PrivateTagLoadSnapshot> load,
        Func<string, CancellationToken, Task<PrivateTagCommit>> define,
        Func<TagId, ResourceId, bool, CancellationToken, Task<PrivateTagCommit>> set)
    {
        Thread = thread ?? throw new ArgumentNullException(nameof(thread));
        _load = load ?? throw new ArgumentNullException(nameof(load));
        _define = define ?? throw new ArgumentNullException(nameof(define));
        _set = set ?? throw new ArgumentNullException(nameof(set));
        Reload();
    }

    internal ResourceId Thread { get; }
    internal PrivateTagStoreSnapshot? Snapshot { get; private set; }
    /// <summary>Tags the thread itself carries (media of the thread are not tagged by them).</summary>
    internal IReadOnlyList<PrivateTagDefinition> Assigned { get; private set; } = Array.Empty<PrivateTagDefinition>();
    internal bool Busy { get; private set; }
    internal string? MessageKey { get; private set; }
    internal event Action? Updated;

    internal void Reload()
    {
        PrivateTagLoadSnapshot loaded = _load();
        Adopt(loaded.State == PrivateTagLoadState.Ready ? loaded.Snapshot : null);
    }

    private void Adopt(PrivateTagStoreSnapshot? snapshot)
    {
        if (snapshot != null && Snapshot != null && snapshot.Revision < Snapshot.Revision) return;
        Snapshot = snapshot;
        Assigned = snapshot == null ? Array.Empty<PrivateTagDefinition>() : snapshot.TagsOf(Thread);
    }

    internal IReadOnlySet<TagId> AssignedIds() => Assigned.Select(tag => tag.Id).ToHashSet();

    internal Task AddAsync(string? name, CancellationToken token = default) => string.IsNullOrWhiteSpace(name) ? Task.CompletedTask : RunAsync(async () =>
    {
        PrivateTagCommit defined = await _define(name, token);
        Adopt(defined.Snapshot);
        Adopt((await _set(defined.Tag, Thread, true, token)).Snapshot);
    });

    internal Task RemoveAsync(TagId tag, CancellationToken token = default) => RunAsync(async () =>
        Adopt((await _set(tag, Thread, false, token)).Snapshot));

    private async Task RunAsync(Func<Task> operation)
    {
        if (Busy || Snapshot == null) return;
        Busy = true;
        MessageKey = null;
        Updated?.Invoke();
        try { await operation(); }
        catch (OperationCanceledException) { }
        catch (PrivateTagStoreException error)
        {
            MessageKey = MediaTagSession.MessageFor(error.Error);
            Reload();
        }
        catch (Exception)
        {
            MessageKey = "tags.error.other";
            Reload();
        }
        finally
        {
            Busy = false;
            Updated?.Invoke();
        }
    }
}
