using MeowChan.Desktop;
using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan;

/// <summary>
/// Local-only bridge between the Danbooru workbench and the media library. The thread identity is part of
/// every lookup so a post number that merely occurs in another file name can never select the wrong tile.
/// </summary>
public sealed partial class MediaPage
{
    /// <summary>Exact saved-post identity used by the host and by the cross-link tests.</summary>
    internal static bool IsSavedBooruEntry(MediaLibraryEntry entry, string postId) =>
        IsSavedBooruEntry(entry, BooruPostKey.FromLegacy(BooruMediaIdentity.Source, postId));

    private static bool IsSavedBooruEntry(MediaLibraryEntry entry, BooruPostKey identity) =>
        entry.Presence == MediaLibraryPresence.Present
        && entry.BooruIdentity == identity
        && entry.AssetId is { Length: > 0 }
        && entry.MediaId == BooruMediaKey.Original(identity).StorageId
        && entry.Variant == "original";

    internal enum BooruOriginalAvailability { Present, Missing, Unavailable }

    // A partial index may prove presence, never absence. Shared by admission and its pure contracts.
    internal static BooruOriginalAvailability SavedBooruOriginalAvailability(MediaLibrarySnapshot? snapshot, string postId)
        => SavedBooruOriginalAvailability(snapshot, BooruPostKey.FromLegacy(BooruMediaIdentity.Source, postId));

    private static BooruOriginalAvailability SavedBooruOriginalAvailability(MediaLibrarySnapshot? snapshot, BooruPostKey identity)
    {
        if (snapshot == null) return BooruOriginalAvailability.Unavailable;
        if (snapshot.Entries.Any(entry => IsSavedBooruEntry(entry, identity))) return BooruOriginalAvailability.Present;
        return snapshot.Complete ? BooruOriginalAvailability.Missing : BooruOriginalAvailability.Unavailable;
    }

    /// <summary>
    /// Explicit reset-navigation to a local Danbooru original: only source and exact post/thread remain active.
    /// No catalog/provider request is made here; the host has already admitted the local post.
    /// </summary>
    internal Task<bool> ShowBooruPostAsync(string postId, Func<bool>? stillRequested = null, Action? cancelNotice = null) =>
        ShowBooruPostAsync(BooruPostKey.FromLegacy(BooruMediaIdentity.Source, postId), stillRequested, cancelNotice);

    internal async Task<bool> ShowBooruPostAsync(BooruPostKey identity, Func<bool>? stillRequested = null, Action? cancelNotice = null)
    {
        string postId = identity.PostId;
        bool Cancelled() { cancelNotice?.Invoke(); return false; }
        bool Failed()
        {
            ShowSelectionNote(Texts.T("booru.crossLinks.navigationFailed", postId));
            throw new InvalidOperationException("The local media index or exact media focus is unavailable.");
        }
        if (stillRequested?.Invoke() == false || _lifetime.IsCancellationRequested || !IsEffectivelyVisible || _building != null) return Cancelled();
        ResourceId thread = BooruMediaIdentity.Thread(identity);
        if (!BooruMediaIdentity.IsBooruThread(thread)) return false;
        BooruOriginalAvailability available = SavedBooruOriginalAvailability(_snapshot, identity);
        if (available == BooruOriginalAvailability.Unavailable) return Failed();
        if (available == BooruOriginalAvailability.Missing)
        {
            ShowSelectionNote(Texts.T("booru.crossLinks.missingLocal", postId));
            return false;
        }

        // This is navigation, not a refinement of the previous query. Like Reset all, it changes only the
        // page's transient controls (no saved user settings). The existing chips describe the entire new
        // context: source + exact thread/post, never a filename substring masquerading as a post identity.
        if (!ApplyBooruNavigationFilters(thread, postId)) return Cancelled();
        object run = new();
        _returnRun = run; // Supersedes any earlier return/focus; own input cancels this run too.
        RenderTagOptions();
        PresentEditedFilters();
        Func<bool> context = CaptureNavigationContext(run, awaitingPresent: true);
        bool Current() => context() && IsEffectivelyVisible && (stillRequested?.Invoke() ?? true);
        try
        {
            await PresentTask.ConfigureAwait(true);
            if (!Current()) return Cancelled();
            ExactEntryFocusResult focused = await FocusExactEntryAsync(entry => IsSavedBooruEntry(entry, identity),
                Texts.T("booru.crossLinks.missingLocal", postId), run, stillRequested).ConfigureAwait(true);
            // Retired requests have no notice authority. A current layout/focus failure is an
            // honest action failure, not cancellation and not proof that the saved original is gone.
            if (!Current() || focused == ExactEntryFocusResult.Cancelled) return Cancelled();
            if (focused == ExactEntryFocusResult.Failed) return Failed();
            return focused == ExactEntryFocusResult.Focused;
        }
        catch (Exception error) when (error is not OutOfMemoryException && !Current())
        {
            return Cancelled();
        }
    }

    // One atomic edit: no intermediate unfiltered presentation and no events with a half-reset query.
    private bool ApplyBooruNavigationFilters(ResourceId thread, string postId)
    {
        bool localizing = _localizing;
        _localizing = true;
        try
        {
            if (_snapshot?.Entries.Any(entry => entry.Thread.Source == thread.Source) != true) return false;
            // Replace the single source of truth first. Rendering any options against the old terms can
            // leave the source set to Danbooru but its board list/summary still tied to the previous source.
            _terms.Clear();
            _terms.Add(MediaFilterTerm.InThread(thread));
            _terms.Add(MediaFilterTerm.Source(thread.Source));
            _threadTitle = Texts.T("booru.media.tile.post", postId);
            _nameFilter.SetTextSilently(string.Empty);
            _tagMode.SelectedIndex = 0;
            ClearQueryHint();
            SyncFilterControls();
            return true;
        }
        finally { _localizing = localizing; }
    }
}
