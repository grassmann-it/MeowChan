using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan.Desktop;

// PT6 (PLAN-POWER-TAGGEN-2026-11-14, decision E2): favorite and rating decided in the gallery tag panel, next to the tags. This file is the
// model without UI: what the owner knows about a medium, the aggregate over the panel's targets (the shown medium, or every marked medium),
// the session that writes through the owner and keeps an undo stack, and the sentences the status line says. The row (Controls/
// GalleryDecisionRow.cs) shows it, the viewer (GalleryViewer.TagPanel.cs) wires keys and clicks, the window (MainWindow.GalleryTags.cs)
// writes through the same stores as the toolbar star and the rating dialog.

/// <summary>Favorite flag and rating of one medium as the owner reads them now (a pending favorite write counts as its target value).</summary>
internal readonly record struct GalleryDecisionState(bool Favorite, int? Rating);

/// <summary>How many of the targets carry something: none, only some or all.</summary>
internal enum GalleryDecisionCoverage { None, Some, All }

/// <summary>
/// What the row shows for the panel's targets. A single target is plain (<see cref="Favorite"/> is None or All, the rating is its value);
/// several targets (the marked media) show a common value, or "differs" when the media disagree.
/// </summary>
internal sealed record GalleryDecisionView(int Count, GalleryDecisionCoverage Favorite, int? Rating, bool RatingMixed, bool Available)
{
    internal static readonly GalleryDecisionView Unavailable = new(0, GalleryDecisionCoverage.None, null, false, false);

    /// <summary>The state the favorite button shows and the next click sets: everything but "all" becomes a favorite.</summary>
    internal bool NextFavorite => Favorite != GalleryDecisionCoverage.All;
}

/// <summary>What the owner (the window) does for the panel: reads and writes with the same stores as the toolbar star and the rating dialog.</summary>
internal interface IGalleryDecisionOwner
{
    /// <summary>The state of the medium, or null when favorites or ratings of it cannot be read or changed now.</summary>
    GalleryDecisionState? Read(GalleryEntry entry);
    /// <summary>Saves the favorite flag; a failure is the owner's to report, the session checks the result by reading again.</summary>
    Task SetFavoriteAsync(GalleryEntry entry, bool selected, CancellationToken token);
    /// <summary>Saves the rating (null removes it); a failure is the owner's to report, the session checks the result by reading again.</summary>
    Task SetRatingAsync(GalleryEntry entry, int? value, CancellationToken token);
}

internal enum GalleryDecisionKind { Rating, Favorite }

/// <summary>One medium of an undoable step: what it was before and what the step wrote.</summary>
internal sealed record GalleryDecisionChange(GalleryEntry Entry, GalleryDecisionState Before, GalleryDecisionState After);

/// <summary>An undoable decision: the media it changed with their old values, a stamp that orders it among the tag steps, and its kind.</summary>
internal sealed record GalleryDecisionStep(GalleryDecisionKind Kind, IReadOnlyList<GalleryDecisionChange> Changes, long Stamp);

internal enum GalleryDecisionStatusKind { None, RatingSaved, RatingRemoved, FavoriteSaved, FavoriteRemoved, UndoneRating, UndoneFavorite, Failed, Unavailable }

/// <summary>The status line after a decision: what happened, for how many media, and how many could not be changed.</summary>
internal sealed record GalleryDecisionStatus(GalleryDecisionStatusKind Kind, int? Value = null, int Files = 1, int Skipped = 0)
{
    internal static readonly GalleryDecisionStatus None = new(GalleryDecisionStatusKind.None);

    internal string Text()
    {
        string text = Kind switch
        {
            GalleryDecisionStatusKind.RatingSaved => Texts.T("gallery.decision.status.ratingSaved", Value ?? 0),
            GalleryDecisionStatusKind.RatingRemoved => Texts.T("gallery.decision.status.ratingRemoved"),
            GalleryDecisionStatusKind.FavoriteSaved => Texts.T("gallery.decision.status.favoriteSaved"),
            GalleryDecisionStatusKind.FavoriteRemoved => Texts.T("gallery.decision.status.favoriteRemoved"),
            GalleryDecisionStatusKind.UndoneRating => Texts.T("gallery.decision.status.undoneRating"),
            GalleryDecisionStatusKind.UndoneFavorite => Texts.T("gallery.decision.status.undoneFavorite"),
            GalleryDecisionStatusKind.Failed => Texts.T("gallery.decision.status.failed"),
            GalleryDecisionStatusKind.Unavailable => Texts.T("gallery.decision.status.unavailable"),
            _ => string.Empty
        };
        if (text.Length == 0) return text;
        // Counts are numbers in fixed phrases, so no language needs plural forms.
        if (Files > 1 && Kind is not (GalleryDecisionStatusKind.Failed or GalleryDecisionStatusKind.Unavailable))
            text += " " + Texts.T("gallery.decision.status.allMarked", Files.ToString(Texts.Culture));
        if (Skipped > 0) text += " " + Texts.T("gallery.decision.status.skipped", Skipped.ToString(Texts.Culture));
        return text;
    }
}

/// <summary>Texts the row shows for a view; one place so the row and the tests agree.</summary>
internal static class GalleryDecisionText
{
    /// <summary>"Rating: 4 of 5" · "Rating: Unrated" · "Rating: ratings differ".</summary>
    internal static string Rating(GalleryDecisionView view) => MediaRatingText.Label + ": " + (view.RatingMixed
        ? Texts.T("gallery.decision.rating.mixed") : MediaRatingText.Value(view.Rating));

    /// <summary>
    /// The short caption beside the favourite button: the rating alone ("4 of 5" · "Unrated" · "Ratings differ"), because the favourite shows itself;
    /// the unavailable reason when neither can be changed. The full sentence stays <see cref="Summary"/> (the rating group's help text).
    /// </summary>
    internal static string Caption(GalleryDecisionView view) => !view.Available ? Texts.T("gallery.decision.status.unavailable")
        : view.RatingMixed ? Texts.T("gallery.decision.rating.mixed") : MediaRatingText.Value(view.Rating);

    /// <summary>"Favorite" · "Not a favorite" · "Favorite on some".</summary>
    internal static string Favorite(GalleryDecisionView view) => view.Favorite switch
    {
        GalleryDecisionCoverage.All => Texts.T("viewer.favorite.saved"),
        GalleryDecisionCoverage.Some => Texts.T("gallery.decision.favorite.some"),
        _ => Texts.T("viewer.favorite.unsaved")
    };

    /// <summary>The live line under the buttons: rating and favorite; the unavailable reason when neither can be changed.</summary>
    internal static string Summary(GalleryDecisionView view) => view.Available
        ? Rating(view) + " · " + Favorite(view)
        : Texts.T("gallery.decision.status.unavailable");
}

/// <summary>
/// Writes favorite and rating for a list of media through the owner and keeps an undo stack of what it changed. Requests run one after
/// the other (a quick second key waits for the first), each on the list of media it was made for, so paging or marking meanwhile never
/// redirects a write. A step records only the media whose value really changed, found by reading again after the write; undoing it writes
/// the old values back for exactly those media. The stack lives as long as the panel (like the tag session's). No UI.
/// </summary>
internal sealed class GalleryDecisionSession : IDisposable
{
    private static long _stamp;
    private readonly IGalleryDecisionOwner _owner;
    private readonly List<GalleryDecisionStep> _undo = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private int _running;
    private bool _disposed;

    internal GalleryDecisionSession(IGalleryDecisionOwner owner, int undoDepth = QuickTagOptions.DefaultUndoDepth)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        UndoDepth = Math.Clamp(undoDepth, QuickTagOptions.MinimumUndoDepth, QuickTagOptions.MaximumUndoDepth);
    }

    /// <summary>The next number of the shared order of undoable steps (tag steps and decision steps compare by it).</summary>
    internal static long NextStamp() => Interlocked.Increment(ref _stamp);

    internal int UndoDepth { get; }
    /// <summary>The undoable steps, oldest first; the last one is undone next.</summary>
    internal IReadOnlyList<GalleryDecisionStep> UndoSteps => _undo;
    internal bool Busy => Volatile.Read(ref _running) > 0;
    internal bool CanUndo => _undo.Count > 0 && !Busy;
    /// <summary>The stamp of the step that is undone next; 0 without steps.</summary>
    internal long NewestStamp => _undo.Count > 0 ? _undo[^1].Stamp : 0;
    internal GalleryDecisionStatus Status { get; private set; } = GalleryDecisionStatus.None;

    /// <summary>Raised after every change of the stack or the busy state, on the thread that caused it.</summary>
    internal event Action? Updated;

    /// <summary>The aggregate of the targets the owner can read; Unavailable when it can read none.</summary>
    internal GalleryDecisionView View(IReadOnlyList<GalleryEntry> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        List<GalleryDecisionState> states = new(targets.Count);
        foreach (GalleryEntry entry in targets)
            if (Read(entry) is { } state) states.Add(state);
        if (states.Count == 0) return GalleryDecisionView.Unavailable;
        int favorites = states.Count(state => state.Favorite);
        GalleryDecisionCoverage coverage = favorites == 0 ? GalleryDecisionCoverage.None
            : favorites == states.Count ? GalleryDecisionCoverage.All : GalleryDecisionCoverage.Some;
        bool mixed = states.Select(state => state.Rating).Distinct().Count() > 1;
        return new GalleryDecisionView(states.Count, coverage, mixed ? null : states[0].Rating, mixed, true);
    }

    private GalleryDecisionState? Read(GalleryEntry entry)
    {
        try { return _owner.Read(entry); }
        catch (Exception error) when (error is InvalidOperationException or System.IO.IOException or System.IO.InvalidDataException or ObjectDisposedException or ArgumentException)
        { return null; }
    }

    /// <summary>Rates every target (null removes the rating). One undoable step for the media that changed.</summary>
    internal Task<GalleryDecisionStatus> SetRatingAsync(IReadOnlyList<GalleryEntry> targets, int? value, CancellationToken token = default)
    {
        if (value is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(value));
        return Run(targets, GalleryDecisionKind.Rating, state => state with { Rating = value },
            (entry, ct) => _owner.SetRatingAsync(entry, value, ct), token,
            value.HasValue ? new GalleryDecisionStatus(GalleryDecisionStatusKind.RatingSaved, value) : new GalleryDecisionStatus(GalleryDecisionStatusKind.RatingRemoved));
    }

    /// <summary>Sets the favorite flag of every target. One undoable step for the media that changed.</summary>
    internal Task<GalleryDecisionStatus> SetFavoriteAsync(IReadOnlyList<GalleryEntry> targets, bool selected, CancellationToken token = default) =>
        Run(targets, GalleryDecisionKind.Favorite, state => state with { Favorite = selected },
            (entry, ct) => _owner.SetFavoriteAsync(entry, selected, ct), token,
            new GalleryDecisionStatus(selected ? GalleryDecisionStatusKind.FavoriteSaved : GalleryDecisionStatusKind.FavoriteRemoved));

    /// <summary>The favorite button: when every target is a favorite it takes the flag off all of them, otherwise it gives it to all.</summary>
    internal Task<GalleryDecisionStatus> ToggleFavoriteAsync(IReadOnlyList<GalleryEntry> targets, CancellationToken token = default) =>
        SetFavoriteAsync(targets, View(targets).NextFavorite, token);

    private async Task<GalleryDecisionStatus> Run(IReadOnlyList<GalleryEntry> targets, GalleryDecisionKind kind,
        Func<GalleryDecisionState, GalleryDecisionState> desired, Func<GalleryEntry, CancellationToken, Task> write, CancellationToken token,
        GalleryDecisionStatus success)
    {
        ArgumentNullException.ThrowIfNull(targets);
        GalleryEntry[] frozen = targets.ToArray(); // the list this request was made for
        if (_disposed || frozen.Length == 0) return GalleryDecisionStatus.None;
        BeginRun();
        try
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            await _gate.WaitAsync(linked.Token).ConfigureAwait(true);
            try
            {
                List<GalleryDecisionChange> changes = new();
                int readable = 0, unchanged = 0, failed = 0;
                foreach (GalleryEntry entry in frozen)
                {
                    if (linked.IsCancellationRequested) break;
                    if (Read(entry) is not { } before) continue;
                    readable++;
                    GalleryDecisionState wanted = desired(before);
                    if (wanted == before) { unchanged++; continue; }
                    try { await write(entry, linked.Token).ConfigureAwait(true); }
                    catch (OperationCanceledException) { break; }
                    catch (Exception error) when (error is not OutOfMemoryException) { /* the owner reports it; the read below decides */ }
                    GalleryDecisionState? after = Read(entry);
                    if (after == wanted) changes.Add(new GalleryDecisionChange(entry, before, wanted));
                    else failed++;
                }
                if (changes.Count > 0) Push(new GalleryDecisionStep(kind, changes, NextStamp()));
                Status = readable == 0 ? new GalleryDecisionStatus(GalleryDecisionStatusKind.Unavailable)
                    : failed > 0 ? new GalleryDecisionStatus(GalleryDecisionStatusKind.Failed, Files: frozen.Length, Skipped: failed)
                    : success with { Files = readable };
                return Status;
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) { return GalleryDecisionStatus.None; }
        finally { EndRun(); }
    }

    /// <summary>
    /// Undoes the newest step: the old values go back for exactly the media it changed. A medium that refuses keeps no step of its own;
    /// when every medium of the step refuses, the step stays for another try.
    /// </summary>
    internal async Task<GalleryDecisionStatus> UndoAsync(CancellationToken token = default)
    {
        if (_disposed || _undo.Count == 0 || Busy) return GalleryDecisionStatus.None;
        GalleryDecisionStep step = _undo[^1];
        BeginRun();
        try
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            await _gate.WaitAsync(linked.Token).ConfigureAwait(true);
            try
            {
                int restored = 0, failed = 0;
                foreach (GalleryDecisionChange change in step.Changes)
                {
                    if (linked.IsCancellationRequested) break;
                    GalleryDecisionState? now = Read(change.Entry);
                    if (now is null) { failed++; continue; }
                    GalleryDecisionState back = step.Kind == GalleryDecisionKind.Rating ? now.Value with { Rating = change.Before.Rating }
                        : now.Value with { Favorite = change.Before.Favorite };
                    if (back == now) { restored++; continue; }
                    try
                    {
                        if (step.Kind == GalleryDecisionKind.Rating) await _owner.SetRatingAsync(change.Entry, change.Before.Rating, linked.Token).ConfigureAwait(true);
                        else await _owner.SetFavoriteAsync(change.Entry, change.Before.Favorite, linked.Token).ConfigureAwait(true);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception error) when (error is not OutOfMemoryException) { }
                    if (Read(change.Entry) == back) restored++; else failed++;
                }
                if (restored > 0 || failed == 0) _undo.Remove(step);
                Status = failed > 0
                    ? new GalleryDecisionStatus(GalleryDecisionStatusKind.Failed, Files: step.Changes.Count, Skipped: failed)
                    : new GalleryDecisionStatus(step.Kind == GalleryDecisionKind.Rating ? GalleryDecisionStatusKind.UndoneRating : GalleryDecisionStatusKind.UndoneFavorite, Files: restored);
                return Status;
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) { return GalleryDecisionStatus.None; }
        finally { EndRun(); }
    }

    private void Push(GalleryDecisionStep step)
    {
        _undo.Add(step);
        if (_undo.Count > UndoDepth) _undo.RemoveRange(0, _undo.Count - UndoDepth);
    }

    private void BeginRun() { Interlocked.Increment(ref _running); Updated?.Invoke(); }
    private void EndRun() { Interlocked.Decrement(ref _running); if (!_disposed) Updated?.Invoke(); }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _lifetime.Cancel(); } catch (ObjectDisposedException) { }
        Updated = null;
    }
}

/// <summary>
/// A second undo stack that shares the panel's undo key and button: the surface asks it whether its newest step is newer than the newest
/// tag step, and undoes that one first. The text it returns goes to the status line.
/// </summary>
internal interface IQuickTagExtraUndo
{
    /// <summary>Steps that can be undone.</summary>
    int Steps { get; }
    /// <summary>An undo can start now (steps exist and no write is running).</summary>
    bool CanUndo { get; }
    /// <summary>The newest step is newer than the newest tag step.</summary>
    bool IsNewest { get; }
    /// <summary>Undoes the newest step; returns the sentence for the status line.</summary>
    Task<string?> UndoAsync();
    /// <summary>Raised after every change of the stack.</summary>
    event Action? Changed;
}
