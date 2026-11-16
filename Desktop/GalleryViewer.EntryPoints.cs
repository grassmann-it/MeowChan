using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using MeowChan.Desktop.MediaActions;
using MeowChan.Gallery;

namespace MeowChan.Desktop;

// The entry point audit of the gallery (Tom, 16.11.2026: "we must check ALL functions: shortcuts, context menus, right-clicks"). Nothing the
// gallery offers may do nothing in silence:
//   * a key whose command cannot run in this state says why, in the words the menu entry or the disabled button already uses (the shell shows
//     MenuExplanationRequested as a short notice), instead of being consumed without an effect;
//   * a menu row is enabled only while its command can act (save frame, rotate), so a click never lands on an early return;
//   * the commands the shortcut editor offers for the gallery have a handler here ("Back to image").
internal sealed partial class GalleryViewer
{
    /// <summary>A command cannot run now: the reason, as the menu entry or the disabled button would show it (the shell shows it as a notice).</summary>
    private void ExplainBlocked(string? reason)
    {
        if (_terminalRetirement || string.IsNullOrWhiteSpace(reason)) return;
        MenuExplanationRequested?.Invoke(reason);
    }

    // D (or a rebound slideshow key) while the slideshow cannot start: the slideshow's own status says why (post panel open, help, menu, loading).
    private void ExplainBlockedPlaybackKey(string id)
    {
        if (id != "gallery.slideshow-toggle" || _slideshow == null || CanExecutePlaybackAction(id) || _viewerInteractionStopped) return;
        SlideshowStatus status = CurrentSlideshowStatus;
        string reason = status is SlideshowStatus.Ready or SlideshowStatus.Running or SlideshowStatus.Paused
            ? MediaMenuReasons.Unavailable : SlideshowStatusText(status);
        MenuExplanationRequested?.Invoke(string.IsNullOrWhiteSpace(reason) ? MediaMenuReasons.Unavailable : reason);
    }

    // Ctrl+Shift+T: the tag panel, or why the shown medium has none (the reason the Tags row and segment show).
    private void RequestTagsByKey()
    {
        if (!_tagPanelOpen && _entry != null && !(_tagNames != null && CanRequestFavorite))
        {
            ExplainBlocked(_tagsUnavailableReason ?? MediaMenuReasons.TagsUnknown);
            return;
        }
        RequestTags();
    }

    // "Back to image" (gallery.context-close, unbound by default): the side panel's ×, by key. The focus returns to the picture.
    private void CloseContextByCommand()
    {
        if (!_contextOpen || _terminalRetirement) return;
        bool hadFocus = _contextPanel.IsKeyboardFocusWithin;
        SetContextOpen(false);
        if (hadFocus || TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() == null) FocusGallerySurface();
    }

    /// <summary>Whether the Menu key or Shift+F10 from <paramref name="source"/> opens the layout menu: a control of the dock (tools, info line,
    /// tagging strip), not the filmstrip (its tiles have the media menu) and not the video bar (the media menu carries its playback group).</summary>
    internal bool IsLayoutMenuKeySource(object? source)
    {
        if (source is not Visual visual || _terminalRetirement) return false;
        Visual[] chain = visual.GetSelfAndVisualAncestors().ToArray();
        if (chain.Contains(_filmstrip) || _video?.TransportControl is { } transport && chain.Contains(transport)) return false;
        return chain.Contains(_chrome) || chain.Contains(_infoOverlay);
    }

    /// <summary>A frame can be saved now: the shown video is ready and its transport usable (the "⋯" row, S and the player menu agree).</summary>
    private bool CanRequestSaveFrame => CanUseVideoTransport && _videoReadyRequest == _request && _entry != null;

    // The rows of "⋯" whose command depends on the moment, not only on the medium: enabled exactly while a click can act.
    private void RefreshFrameCommands()
    {
        if (_saveFrame != null) _saveFrame.IsEnabled = !_terminalRetirement && CanRequestSaveFrame;
        // Windows: a playing video that cannot reopen at its position cannot turn (the player's menu says the same with CanRotate).
        if (_rotate != null && _fit != null)
            _rotate.IsEnabled = _fit.IsEnabled && !(_entry?.Kind == SavedMediaKind.Video && _video is { TurnsNow: false });
    }
}
