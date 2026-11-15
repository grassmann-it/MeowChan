using System;
using System.Collections.Generic;
using Avalonia.Input;

namespace MeowChan.Desktop;

/// <summary>One medium of the selected post as the keys see it (pure facts, no controls).</summary>
/// <param name="IsVideo">The file is a video.</param>
/// <param name="Available">Not deleted, and (once the manifest is known) its saved file is there; anything else is skipped silently.</param>
/// <param name="LiveInPost">A player of this medium lives in its post.</param>
/// <param name="PlayingInPost">That player is playing now.</param>
internal readonly record struct ReaderPostMedium(bool IsVideo, bool Available, bool LiveInPost = false, bool PlayingInPost = false);

/// <summary>Where the player of the chosen medium lives (none: it has none yet).</summary>
internal enum ReaderMediumPlace { None, Post, Sidebar }

/// <summary>The medium Space and Enter act on (<see cref="ReaderMediaActivation.Choose"/>).</summary>
internal readonly record struct ReaderMediumChoice(int Index, ReaderMediumPlace Live);

/// <summary>What Space or Enter does to the chosen medium.</summary>
internal enum ReaderMediumOutcome { None, ToggleInPost, ToggleInSidebar, PlayInPost, ExpandInPost, ShowInSidebar, OpenGallery }

/// <summary>
/// Space and Enter on the selected post's medium (pure). One resolver picks the medium for both keys and one rule says what happens to it:
/// a video that already has a player toggles it (in the sidebar or in its post); anything else does what a click on it does by the choice "Clicking a
/// medium". Space is play / pause with that as its fallback, Enter is that; so for a given medium both keys do the same.
/// </summary>
internal static class ReaderMediaActivation
{
    /// <summary>
    /// The medium, in this order: (1) a live player of the post, a playing one before a paused one (the sidebar's before an in-post one when both wait);
    /// (2) the sidebar's current medium while the sidebar is shown; (3) the medium whose tile has the focus; (4) the first available one by position.
    /// A medium that is not available is never chosen. Null: nothing to act on.
    /// </summary>
    internal static ReaderMediumChoice? Choose(IReadOnlyList<ReaderPostMedium> media, int? sidebarIndex, bool sidebarLive, bool sidebarPlaying, int? focusedIndex)
    {
        ArgumentNullException.ThrowIfNull(media);
        bool Usable(int? index) => index is { } i && i >= 0 && i < media.Count && media[i].Available;
        // 1. A live player: playing first.
        if (sidebarLive && Usable(sidebarIndex) && sidebarPlaying) return new(sidebarIndex!.Value, ReaderMediumPlace.Sidebar);
        for (int i = 0; i < media.Count; i++)
            if (media[i] is { Available: true, LiveInPost: true, PlayingInPost: true }) return new(i, ReaderMediumPlace.Post);
        if (sidebarLive && Usable(sidebarIndex)) return new(sidebarIndex!.Value, ReaderMediumPlace.Sidebar);
        for (int i = 0; i < media.Count; i++)
            if (media[i] is { Available: true, LiveInPost: true }) return new(i, ReaderMediumPlace.Post);
        // 2. What the sidebar shows, 3. what has the focus, 4. the first one.
        if (Usable(sidebarIndex)) return new(sidebarIndex!.Value, ReaderMediumPlace.None);
        if (Usable(focusedIndex)) return new(focusedIndex!.Value, ReaderMediumPlace.None);
        for (int i = 0; i < media.Count; i++)
            if (media[i].Available) return new(i, ReaderMediumPlace.None);
        return null;
    }

    /// <summary>
    /// What happens to <paramref name="medium"/>. <paramref name="sidebarShown"/>: the sidebar is open (and wide enough); <paramref name="sidebarShowsIt"/>: it shows exactly
    /// this medium now; <paramref name="canPlayInPost"/> and <paramref name="canExpand"/>: a video can play in its post, an image can expand there;
    /// <paramref name="galleryWired"/>: the gallery can be opened.
    /// </summary>
    internal static ReaderMediumOutcome Decide(ReaderMediumChoice choice, ReaderPostMedium medium, ReaderInlineVideoOptions options, bool sidebarShown,
        bool sidebarShowsIt, bool canPlayInPost, bool canExpand, bool galleryWired)
    {
        if (!medium.Available) return ReaderMediumOutcome.None;
        // A video with a player toggles it, where it lives.
        if (choice.Live == ReaderMediumPlace.Sidebar) return ReaderMediumOutcome.ToggleInSidebar;
        if (choice.Live == ReaderMediumPlace.Post) return ReaderMediumOutcome.ToggleInPost;
        ReaderMediaClickRules.Outcome click = ReaderMediaClickRules.Click(options, medium.IsVideo && canPlayInPost, canExpand, sidebarShown);
        switch (click)
        {
            case ReaderMediaClickRules.Outcome.PlayInPost: return ReaderMediumOutcome.PlayInPost;
            case ReaderMediaClickRules.Outcome.ExpandInPost: return ReaderMediumOutcome.ExpandInPost;
            case ReaderMediaClickRules.Outcome.ShowInSidebar:
                // A sidebar that already shows the image: a second activation opens the gallery, as a click on the sidebar's picture does.
                if (sidebarShowsIt && !medium.IsVideo) return galleryWired ? ReaderMediumOutcome.OpenGallery : ReaderMediumOutcome.None;
                return ReaderMediumOutcome.ShowInSidebar;
            default: return galleryWired ? ReaderMediumOutcome.OpenGallery : ReaderMediumOutcome.None;
        }
    }

    /// <summary>
    /// A held key sends repeats: while the key is still down (or a gallery opening is under way) a repeat does nothing, so a held Space never flickers play and
    /// pause. The key's release (the reader observes it) lets the next press act.
    /// </summary>
    internal static bool IsRepeat(ISet<Key> heldKeys, Key key, bool activationInFlight)
    {
        ArgumentNullException.ThrowIfNull(heldKeys);
        return activationInFlight || heldKeys.Contains(key);
    }

    /// <summary>
    /// Whether the key is Space or Enter as a medium key: Space with no modifier whose gesture no saved reader or shell command has (the video keys give way
    /// to those too), or the gesture of the command "Open medium of the post" (Enter by default). A modifier never activates a medium on Space.
    /// </summary>
    internal static bool IsMediaKey(Key key, KeyModifiers modifiers, bool matchesActivateCommand, bool claimedByOtherCommand) =>
        !claimedByOtherCommand && (matchesActivateCommand || key == Key.Space && modifiers == KeyModifiers.None);
}
