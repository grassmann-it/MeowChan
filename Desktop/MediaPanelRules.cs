using System;
using System.Collections.Generic;
using Avalonia.Input;

namespace MeowChan.Desktop;

/// <summary>
/// Which tile the Media page's media panel shows when the grid's selection changes (pure). The grid selects several tiles at once; the panel shows one:
/// the tile that one change added (a click, an arrow key, Ctrl+click), else the one it already shows while that is still selected (Ctrl+A or a Shift
/// range does not move it), else the first selected one (the grid's lead). An empty selection keeps what the panel shows, so Ctrl+click on the shown tile
/// does not make the panel flash empty.
/// </summary>
internal static class MediaPanelFollow
{
    /// <summary>
    /// The item the panel shows after a selection change; <paramref name="shown"/> when nothing else applies (it may be null: nothing shown yet).
    /// <paramref name="addedCount"/> and <paramref name="lastAdded"/> describe what the change added; <paramref name="isSelected"/> and
    /// <paramref name="firstSelected"/> ask the grid's selection (no pass over a selection of thousands of tiles).
    /// </summary>
    internal static T? Pick<T>(int addedCount, T? lastAdded, T? shown, Func<T, bool> isSelected, Func<T?> firstSelected) where T : class
    {
        ArgumentNullException.ThrowIfNull(isSelected);
        ArgumentNullException.ThrowIfNull(firstSelected);
        if (addedCount == 1 && lastAdded != null) return lastAdded;
        if (shown != null && isSelected(shown)) return shown;
        return firstSelected() ?? shown;
    }

    /// <summary>The position the panel's arrows lead to from <paramref name="index"/> in a list of <paramref name="count"/> tiles; -1 at either end (no wrap: the arrows are disabled there).</summary>
    internal static int Step(int index, int count, int delta)
    {
        if (count <= 0 || index < 0 || index >= count) return -1;
        int target = index + Math.Sign(delta);
        return target < 0 || target >= count ? -1 : target;
    }

    /// <summary>The counter over the medium: "n / total" (the same form as the reader's), empty for an empty list.</summary>
    internal static string CounterText(int index, int count) => count > 0 && index >= 0 && index < count ? index + 1 + " / " + count : string.Empty;
}

/// <summary>What a key does for the media panel.</summary>
internal enum MediaPanelKeyKind { None, Video, Previous, Next, ReturnToGrid }

/// <summary>The decision of <see cref="MediaPanelKeys.Route"/>.</summary>
internal readonly record struct MediaPanelKeyRoute(MediaPanelKeyKind Kind, ReaderVideoAction Action = ReaderVideoAction.None)
{
    internal static MediaPanelKeyRoute None => new(MediaPanelKeyKind.None);
}

/// <summary>
/// The keys of the media panel (pure). They are the gallery's own video commands (Space, M, L, "," ".", X, C and what the person rebound them to) and the
/// panel's two arrow commands, always read from the bindings through <c>matches</c>; they act on the video the panel shows while the grid or the panel has the
/// focus. Nothing is taken while a text field has the focus (or while the panel is hidden, or has no video for the video keys): Space then stays the
/// grid's selection key and the letters stay letters. Enter is not here: it keeps opening the gallery.
/// </summary>
internal static class MediaPanelKeys
{
    internal static MediaPanelKeyRoute Route(Key key, KeyModifiers modifiers, Func<string, bool> matches, bool panelShown, bool videoOffered,
        bool nativeInput, bool fromPanel, bool canPrevious, bool canNext)
    {
        ArgumentNullException.ThrowIfNull(matches);
        if (!panelShown || nativeInput) return MediaPanelKeyRoute.None;
        if (matches("media.panel.previous") && canPrevious) return new(MediaPanelKeyKind.Previous);
        if (matches("media.panel.next") && canNext) return new(MediaPanelKeyKind.Next);
        if (videoOffered && ReaderVideoKeys.Classify(matches) is var action && action != ReaderVideoAction.None) return new(MediaPanelKeyKind.Video, action);
        // Escape in the panel hands the keyboard back to the grid (the panel has nothing to close with it: × is its own button).
        if (fromPanel && key == Key.Escape && modifiers == KeyModifiers.None) return new(MediaPanelKeyKind.ReturnToGrid);
        return MediaPanelKeyRoute.None;
    }

    /// <summary>Whether a medium can play in the panel: a saved video, with the player enabled for it.</summary>
    internal static bool CanOfferVideo(bool enabled, bool present, bool isVideo) => enabled && present && isVideo;
}
