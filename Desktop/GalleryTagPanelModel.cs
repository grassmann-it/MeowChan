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
    /// <summary>The list the gallery walks (read on the UI thread only; the panel copies what it needs).</summary>
    IReadOnlyList<GalleryEntry> Entries { get; }
    GalleryTagPanelPosition? Position { get; }
    /// <summary>Tags used in this session, most recent first; the context ranking reads it.</summary>
    TagRecentList Recent { get; }
    QuickTagOptions Options { get; }
    TagContextOptions ContextOptions { get; }
    BooruCatalogSnapshot? ReadSourceTags();
    /// <summary>Thread tags of <paramref name="thread"/> (the section Ctrl+T opens).</summary>
    GalleryThreadTagModel OpenThreadTags(ResourceId thread);
    /// <summary>Opens the full tag dialog for the entry ("Advanced…"); the panel reloads afterwards.</summary>
    void OpenAdvanced(GalleryEntry entry);
}

/// <summary>What the surface shows for one medium: the frozen target, the file name, the counter text and, for a medium that cannot be tagged, the reason.</summary>
internal sealed record GalleryTagPanelFrame(IQuickTagTarget Target, string Label, string? Counter, string? UnavailableReason);

/// <summary>The commands the gallery tag panel handles and how a command maps onto a key action of the surface.</summary>
internal static class GalleryTagPanelCommands
{
    /// <summary>Handled here. Marking (Shift+Page Down/Up) is the batch package's; those ids stay unwired.</summary>
    internal static IReadOnlyList<string> Handled { get; } = Array.AsReadOnly(
        new[] { QuickTagCommands.Next, QuickTagCommands.Previous, QuickTagCommands.CommitNext, QuickTagCommands.ThreadSection, QuickTagCommands.Undo }
            .Concat(Enumerable.Range(1, QuickTagCommands.SuggestionCount).Select(QuickTagCommands.Suggestion)).ToArray());

    /// <summary>The surface action of a handled command; null for any other id.</summary>
    internal static QuickTagKeyRoute? RouteOf(string commandId)
    {
        switch (commandId)
        {
            case QuickTagCommands.Next: return new(QuickTagKeyAction.Next);
            case QuickTagCommands.Previous: return new(QuickTagKeyAction.Previous);
            case QuickTagCommands.CommitNext: return new(QuickTagKeyAction.CommitAndNext);
            case QuickTagCommands.ThreadSection: return new(QuickTagKeyAction.ThreadSection);
            case QuickTagCommands.Undo: return new(QuickTagKeyAction.Undo);
        }
        for (int number = 1; number <= QuickTagCommands.SuggestionCount; number++)
            if (commandId == QuickTagCommands.Suggestion(number)) return new(QuickTagKeyAction.ToggleSuggestion, number - 1);
        return null;
    }

    /// <summary>How far a navigation request moves the gallery: +1 / -1; 0 for marking, which the batch package wires.</summary>
    internal static int Delta(QuickTagNavigation navigation) => navigation switch
    {
        QuickTagNavigation.Next => 1,
        QuickTagNavigation.Previous => -1,
        _ => 0
    };

    /// <summary>"Medium 4 of 27" for the counter chip and the live region; null without a position.</summary>
    internal static string? CounterText(GalleryTagPanelPosition? position) => position is not { } value ? null
        : Texts.T(value.More ? "tags.quick.gallery.positionMore" : "tags.quick.gallery.position",
            value.Number.ToString(Texts.Culture), value.Count.ToString(Texts.Culture));

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
/// Disposing it (the panel closed) cancels every background step.
/// </summary>
internal sealed class GalleryTagPanelController : IDisposable
{
    private readonly IGalleryTagPanelOwner _owner;
    private GalleryTagNeighbourSubjects? _neighbours;
    private CancellationTokenSource? _fill;
    private bool _disposed;

    private GalleryTagPanelController(IGalleryTagPanelOwner owner, GalleryEntry entry)
    {
        _owner = owner;
        Entry = entry;
        Suggestions = new GalleryTagPanelSuggestions(owner.ContextOptions) { Recent = () => owner.Recent.Snapshot() };
        IQuickTagTarget target = Resolve(entry, out string? reason);
        UnavailableReason = reason;
        Session = new QuickTagSession(target, owner.Options.UndoDepth) { Label = GalleryTagPanelCommands.LabelOf(entry) };
        Session.Applied += OnApplied;
        Suggestions.Context = Context(entry);
    }

    /// <summary>Creates the controller for the entry the panel opens on and starts warming the suggestion index for the current snapshot.</summary>
    internal static GalleryTagPanelController Create(IGalleryTagPanelOwner owner, GalleryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(entry);
        GalleryTagPanelController controller = new(owner, entry);
        // Off the UI thread, before the first strip is asked for: the surface then finds the index warm or answers from the cold path.
        if (controller.Session.Snapshot is { } snapshot) controller.Suggestions.Warm(snapshot);
        return controller;
    }

    internal QuickTagSession Session { get; }
    internal GalleryTagPanelSuggestions Suggestions { get; }
    internal GalleryEntry Entry { get; private set; }
    internal string? UnavailableReason { get; private set; }

    private IQuickTagTarget Resolve(GalleryEntry entry, out string? reason)
    {
        IQuickTagTarget? target = _owner.OpenTarget(entry, out reason);
        return target ?? new GalleryTagUnavailableTarget(reason);
    }

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
        string label = GalleryTagPanelCommands.LabelOf(entry);
        Session.Label = label;
        Suggestions.Context = Context(entry);
        return new GalleryTagPanelFrame(target, label, GalleryTagPanelCommands.CounterText(_owner.Position), reason);
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

    private void OnApplied(TagDialogChange change)
    {
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
