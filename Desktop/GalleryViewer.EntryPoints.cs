using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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

    // ---- the Menu key and Shift+F10 (sweep S2 F4, 16.11.2026) ----
    // A focused control with a menu of its own gets the key first, as its right-click does: a chip of the tag panel (add to / remove from
    // the stamp), the slideshow toggle (its settings flyout), the speed button (its speed choices). Only otherwise the gallery routes the key
    // to the layout menu (a dock tool) or the media menu (the picture, the filmstrip, the video bar). The key the gallery handled has a
    // key-up that Avalonia may answer with a positionless ContextRequested of its own on the focused control (a second menu over the first,
    // or the media menu reopened): that one key-up is consumed.

    // The key of the context key-down the gallery handled; its key-up is consumed once. Any other key-down in the viewer ends it.
    private Key? _contextKeyUpPending;

    /// <summary>The focused control (or its ancestor below the viewer) that owns a menu of its own for the Menu key and Shift+F10: one with
    /// an own ContextMenu (tag chips, the speed button), or the slideshow toggle (its settings flyout). The gallery's media and layout menus,
    /// attached to their origin only while open, are not a control's own. Null when the gallery's own routing applies.</summary>
    internal Control? ContextKeyOwner(object? source)
    {
        if (source is not Visual visual || _terminalRetirement) return null;
        foreach (Visual node in visual.GetSelfAndVisualAncestors())
        {
            if (ReferenceEquals(node, this) || ReferenceEquals(node, _nativeVideoOverlay)) break;
            if (node is not Control control) continue;
            if (ReferenceEquals(control, _slideshowToggle)) return control;
            if (control.ContextMenu is { } menu && !ReferenceEquals(menu, _mediaMenuPresenter.Menu) && !ReferenceEquals(menu, LayoutContextMenu))
                return control;
        }
        return null;
    }

    // The owner's menu opens through the same request a right-click raises (positionless: under the control), so the menu composes as it
    // does there (the ContextMenu's Opening fills the stamp entries and the checked speed). True when something opened it; a request the owner
    // declines (the toggle under "More") goes on to the dock's and the viewer's handlers, as the right-click's does.
    private bool OpenOwnContextMenu(Control owner)
    {
        ContextRequestedEventArgs request = new() { RoutedEvent = ContextRequestedEvent, Source = owner };
        owner.RaiseEvent(request);
        return request.Handled;
    }

    // The gallery opened a menu for this key-down: the key is handled and its key-up will be consumed.
    private void ConsumeContextKey(KeyEventArgs e)
    {
        e.Handled = true;
        _contextKeyUpPending = e.Key;
    }

    // A new key-down ends the wait for the key-up of an earlier context key (a modifier, the Shift of Shift+F10, does not).
    private void ForgetContextKeyUp(KeyEventArgs e)
    {
        if (e.Key is not (Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin))
            _contextKeyUpPending = null;
    }

    // The key-up of the context key the gallery handled: handled here, so Avalonia raises no second, positionless request for it.
    private void OnContextKeyUp(object? sender, KeyEventArgs e)
    {
        if (_contextKeyUpPending is not { } key || e.Key != key) return;
        _contextKeyUpPending = null;
        e.Handled = true;
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
