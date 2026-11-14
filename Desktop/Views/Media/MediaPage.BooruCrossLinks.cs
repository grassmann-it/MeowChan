using MeowChan.Desktop;
using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using MeowChan.Archive;
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
        entry.Presence == MediaLibraryPresence.Present
        && entry.BooruPostId == postId
        && entry.Thread == BooruMediaIdentity.Thread(postId)
        && entry.AssetId is { Length: > 0 }
        && entry.MediaId == BooruMediaIdentity.MediaIdentifier(postId)
        && entry.Variant == "original";

    /// <summary>
    /// Explicit reset-navigation to a local Danbooru original: only source and exact post/thread remain active.
    /// No catalog/provider request is made here; the host has already admitted the local post.
    /// </summary>
    internal async Task<bool> ShowBooruPostAsync(string postId, Func<bool>? stillRequested = null)
    {
        if (stillRequested?.Invoke() == false || _lifetime.IsCancellationRequested || !IsEffectivelyVisible || _building != null) return false;
        ResourceId thread = BooruMediaIdentity.Thread(postId);
        if (!BooruMediaIdentity.IsBooruThread(thread)) return false;
        if (_snapshot == null || !_snapshot.Entries.Any(entry => IsSavedBooruEntry(entry, postId)))
        {
            ShowSelectionNote(Texts.T("booru.crossLinks.missingLocal", postId));
            return false;
        }

        // This is navigation, not a refinement of the previous query. Like Reset all, it changes only the
        // page's transient controls (no saved user settings). The existing chips describe the entire new
        // context: source + exact thread/post, never a filename substring masquerading as a post identity.
        if (!ApplyBooruNavigationFilters(thread, postId)) return false;
        object run = new();
        _returnRun = run; // Supersedes any earlier return/focus; own input cancels this run too.
        RenderTagOptions();
        PresentEditedFilters();
        Func<bool> context = CaptureNavigationContext(run, awaitingPresent: true);
        bool Current() => context() && IsEffectivelyVisible && (stillRequested?.Invoke() ?? true);
        await PresentTask.ConfigureAwait(true);
        if (!Current()) return false;
        bool focused = await FocusExactEntryAsync(entry => IsSavedBooruEntry(entry, postId),
            Texts.T("booru.crossLinks.missingLocal", postId), run, stillRequested).ConfigureAwait(true);
        return Current() && focused;
    }

    // One atomic edit: no intermediate unfiltered presentation and no events with a half-reset query.
    private bool ApplyBooruNavigationFilters(ResourceId thread, string postId)
    {
        bool localizing = _localizing;
        _localizing = true;
        try
        {
            if (_snapshot?.Entries.Any(entry => entry.Thread.Source == BooruMediaIdentity.Source) != true) return false;
            // Replace the single source of truth first. Rendering any options against the old terms can
            // leave the source set to Danbooru but its board list/summary still tied to the previous source.
            _terms.Clear();
            _terms.Add(MediaFilterTerm.InThread(thread));
            _terms.Add(MediaFilterTerm.Source(BooruMediaIdentity.Source));
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
