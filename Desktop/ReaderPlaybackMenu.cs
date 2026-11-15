using System.Collections.Generic;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

/// <summary>
/// The playback group on top of the media menu of a video in the reader (a post's inline video and the media sidebar's video): play or pause,
/// mute or unmute, repeat on or off. They are the gallery's own commands (the shortcuts shown come from the bindings); the labels say what the
/// entry does now. Mute and repeat need a started player. The rest of the menu is the common media menu, built by the same builder as for the
/// reader attachment, so both surfaces list the same entries. The gallery's media menu shows the same group on top for a video and adds the
/// post bookmark entry of the reader's menu for a medium that belongs to a post (<see cref="GalleryExtras"/>).
/// </summary>
internal static class ReaderPlaybackMenu
{
    internal readonly record struct Entry(string CommandId, ReaderVideoAction Action, string Label, bool Available);

    internal static IReadOnlyList<Entry> Entries(bool live, bool playing, bool muted, bool loop) => Entries(live, playing, muted, loop, true, live, live);

    /// <summary>The same group where the surface knows separately whether play, mute and repeat can act now (the gallery's video).</summary>
    internal static IReadOnlyList<Entry> Entries(bool live, bool playing, bool muted, bool loop, bool canPlay, bool canMute, bool canLoop) => new[]
    {
        new Entry("video.play-pause", ReaderVideoAction.PlayPause, Texts.T(playing ? "command.video.pause" : "command.video.play"), canPlay),
        new Entry("video.mute", ReaderVideoAction.Mute, Texts.T(muted ? "viewer.video.unmute" : "viewer.video.mute"), live && canMute),
        new Entry("video.loop", ReaderVideoAction.Loop, Texts.T(loop ? "gallery.video.loopOff" : "gallery.video.loopOn"), live && canLoop)
    };

    /// <summary>
    /// What the gallery's media menu adds to the common section: the playback group for a shown video and the post bookmark entry for a
    /// medium that belongs to a post. "Open in gallery" stays out on purpose (the medium is already in the gallery).
    /// </summary>
    internal static class GalleryExtras
    {
        internal const string PostBookmarkCommandId = "post-bookmarks.edit-post";

        internal static IReadOnlyList<Entry> Playback(bool isVideoShown, bool transportUsable, bool playing, bool muted, bool loop, bool hasAudio, bool loopChangeable)
            => isVideoShown ? Entries(transportUsable, playing, muted, loop, transportUsable, hasAudio, loopChangeable) : System.Array.Empty<Entry>();

        internal static bool OffersPostBookmark(bool hasPost) => hasPost;

        /// <summary>The command ids of the extra entries in menu order: the playback group on top, the post bookmark entry below the common section.</summary>
        internal static IReadOnlyList<string> CommandIds(IReadOnlyList<Entry> playback, bool offersPostBookmark)
        {
            List<string> ids = new();
            foreach (Entry entry in playback) ids.Add(entry.CommandId);
            if (offersPostBookmark) ids.Add(PostBookmarkCommandId);
            return ids;
        }
    }

    /// <summary>The playback group as menu items for a reader or Media-page video slot.</summary>
    internal static List<Control>? MenuItems(ReaderInlineVideoSlot? slot, MediaMenuSession session, Func<Func<Task>, Task> run)
    {
        if (slot is not { IsOffered: true }) return null;
        List<Control> items = new();
        foreach (Entry entry in Entries(slot.IsLive, slot.IsPlaying, slot.Player?.Muted == true, slot.Player?.LoopEnabled == true))
        {
            ReaderVideoAction action = entry.Action;
            items.Add(MediaMenuBuilder.ContextCommandItem(session, entry.CommandId, () => slot.ExecuteAsync(action), () => entry.Available, run,
                MediaMenuReasons.Unavailable, entry.Label));
        }
        return items;
    }
}
