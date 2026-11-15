using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;
using MeowChan.Desktop.Commands;

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

    /// <summary>
    /// The position the panel's arrows lead to from <paramref name="index"/> in a list of <paramref name="count"/> tiles; -1 at either end and for a
    /// position outside the list (no wrap: the arrows are disabled there). The step itself is the reader sidebar's (ReaderMediaSidebarRules).
    /// </summary>
    internal static int Step(int index, int count, int delta) =>
        index >= 0 && index < count && ReaderMediaSidebarRules.CanStep(index, count, delta) ? ReaderMediaSidebarRules.Step(index, count, delta) : -1;
}

/// <summary>What a key does for the media panel. ReturnToGrid is no longer a decision of <see cref="MediaPanelKeys.Route"/> (Escape is a level of
/// the page, MediaFocusMode.Escape); it stays until the panel's own switch no longer names it.</summary>
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
/// grid's selection key and the letters stay letters. A video key that one of the Media page's own commands also has (a binding the person saved for
/// it) is left to that command: on the Media page the video keys give way instead of conflicting (EffectiveShortcutBindings.ConflictContexts), so the
/// gallery and the reader keep them. Enter is not here: it keeps opening the gallery. Escape is not here either: it is a level of the page
/// (MediaFocusMode.Escape), so the focus mode ends before the panel hands the keyboard back to the grid.
/// </summary>
internal static class MediaPanelKeys
{
    /// <summary>The panel's switch (Primary+Shift+M by default, like the reader's media sidebar): the Layout menu row and the panel's ×.</summary>
    internal const string ToggleCommandId = "media.panel.toggle";

    internal static MediaPanelKeyRoute Route(Key key, KeyModifiers modifiers, Func<string, bool> matches, bool panelShown, bool videoOffered,
        bool nativeInput, bool fromPanel, bool canPrevious, bool canNext)
    {
        ArgumentNullException.ThrowIfNull(matches);
        if (!panelShown || nativeInput) return MediaPanelKeyRoute.None;
        if (matches("media.panel.previous") && canPrevious) return new(MediaPanelKeyKind.Previous);
        if (matches("media.panel.next") && canNext) return new(MediaPanelKeyKind.Next);
        if (videoOffered && ReaderVideoKeys.Classify(matches) is var action && action != ReaderVideoAction.None && !PageClaims(matches))
            return new(MediaPanelKeyKind.Video, action);
        return MediaPanelKeyRoute.None;
    }

    /// <summary>Whether one of the Media page's own commands has the key (the panel's arrows aside, which <see cref="Route"/> asks first).</summary>
    internal static bool PageClaims(Func<string, bool> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);
        foreach (string id in PageCommands.Value)
            if (matches(id)) return true;
        return false;
    }

    // Every command that runs on the Media page and competes there for its gestures: not the video keys themselves (they give way), not the arrows.
    private static readonly Lazy<string[]> PageCommands = new(() => CommandCatalog.All
        .Where(command => command.IsImplemented && !ReaderVideoKeys.IsVideoCommand(command.Id)
            && command.Id is not ("media.panel.previous" or "media.panel.next")
            && EffectiveShortcutBindings.ConflictContexts(command.Id).Contains("media", StringComparer.Ordinal))
        .Select(command => command.Id).ToArray());

    /// <summary>Whether a medium can play in the panel: a saved video, with the player enabled for it.</summary>
    internal static bool CanOfferVideo(bool enabled, bool present, bool isVideo) => enabled && present && isVideo;
}
