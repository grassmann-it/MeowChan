using System;

namespace MeowChan.Desktop;

/// <summary>What a click on a medium of a reader post does: show it in the media sidebar, open it in the post, or open the gallery.</summary>
internal enum ReaderMediaClickMode { Sidebar, Post, Gallery }

/// <summary>
/// The one choice "Clicking a medium" (reader quick settings and Settings › Reading and media). It replaces the old on/off setting "Play video in
/// the reader": its value lives on as the choice "Open in the post" (an old saved <c>ReaderInlineVideo</c> of true is read as that choice).
/// Window-free so the routing is testable: <see cref="Resolve"/> says where a click goes given whether the sidebar is on screen.
/// A double click and the gallery buttons always open the gallery, whatever the choice is.
/// </summary>
internal static class ReaderMediaClickRules
{
    internal const string SidebarKey = "Sidebar", PostKey = "Post", GalleryKey = "Gallery";
    internal static readonly string[] Keys = { SidebarKey, PostKey, GalleryKey };

    /// <summary>The canonical key of a stored value, or an empty text for none or an unknown one (not chosen yet).</summary>
    internal static string Normalize(string? stored)
    {
        string text = stored?.Trim() ?? string.Empty;
        foreach (string key in Keys)
            if (string.Equals(text, key, StringComparison.OrdinalIgnoreCase)) return key;
        return string.Empty;
    }

    internal static string ToKey(ReaderMediaClickMode mode) => mode switch
    {
        ReaderMediaClickMode.Post => PostKey,
        ReaderMediaClickMode.Gallery => GalleryKey,
        _ => SidebarKey
    };

    internal static ReaderMediaClickMode FromKey(string? key) => Normalize(key) switch
    {
        PostKey => ReaderMediaClickMode.Post,
        GalleryKey => ReaderMediaClickMode.Gallery,
        _ => ReaderMediaClickMode.Sidebar
    };

    /// <summary>
    /// The effective choice: the stored one, else (an old config without it) "Open in the post" when the old inline video setting was on,
    /// else the sidebar.
    /// </summary>
    internal static ReaderMediaClickMode Effective(string? stored, bool legacyInlineVideo)
    {
        string key = Normalize(stored);
        return key.Length > 0 ? FromKey(key) : legacyInlineVideo ? ReaderMediaClickMode.Post : ReaderMediaClickMode.Sidebar;
    }

    internal enum Outcome { PlayInPost, ExpandInPost, ShowInSidebar, OpenGallery }

    /// <summary>
    /// What a click on a medium does. In the post a video that can play there plays, an image that can expand expands (anything else opens the
    /// gallery); with the sidebar choice the sidebar shows it, or the gallery opens while the sidebar is closed; with the gallery choice the gallery opens.
    /// </summary>
    internal static Outcome Click(ReaderInlineVideoOptions options, bool videoPlaysInPost, bool imageExpands, bool sidebarShown) => options.Mode switch
    {
        ReaderMediaClickMode.Post => videoPlaysInPost ? Outcome.PlayInPost : imageExpands ? Outcome.ExpandInPost : Outcome.OpenGallery,
        ReaderMediaClickMode.Sidebar => sidebarShown ? Outcome.ShowInSidebar : Outcome.OpenGallery,
        _ => Outcome.OpenGallery
    };

    /// <summary>Where a click goes: "Sidebar" falls back to the gallery while the sidebar is closed (or too narrow to show).</summary>
    internal static ReaderMediaClickMode Resolve(ReaderMediaClickMode mode, bool sidebarShown) =>
        mode == ReaderMediaClickMode.Sidebar && !sidebarShown ? ReaderMediaClickMode.Gallery : mode;
}
