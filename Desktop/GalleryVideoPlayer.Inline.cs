using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace MeowChan.Desktop;

// The reader's minimal control (plan R10 follow-up): the same player, a different transport. Play or pause, a slim timeline, the time and
// mute or unmute (and, where the owner offers it, "Open in gallery") in one row exactly as wide as the picture; speed, repeat, A–B loop, next
// frame and volume stay in the gallery's bar. The controls are the player's own (same engine, same state, same names), only moved into
// the compact bar: the gallery's bar is not changed.
internal sealed partial class GalleryVideoPlayer
{
    private InlineVideoBarPanel? _minimalBar;

    /// <summary>The user chose mute or unmute with the bar's speaker (true = muted now): the reader keeps the choice for this session.</summary>
    internal event Action<bool>? MuteChosen;

    internal bool HasMinimalTransport => _minimalBar != null;
    internal InlineVideoBarPanel? MinimalBar => _minimalBar;
    internal bool Muted => _muteIntent.Muted;
    /// <summary>Whether the player is meant to play now (the state the play / pause toggle acts on), for the gallery's media menu.</summary>
    internal bool IsPlaybackRequested => !IsDisposed && !_ended && _intent.Current.Mode == VideoPlaybackMode.Playing;
    /// <summary>Whether the medium has usable audio, so mute can act.</summary>
    internal bool HasAudio => !IsDisposed && _audioAvailable;
    /// <summary>Whether repeat can be changed now (not while a running slideshow owns it).</summary>
    internal bool CanToggleLoopNow => !IsDisposed && _loop.IsEnabled;

    private static void Detach(Control control) => (control.Parent as Panel)?.Children.Remove(control);

    /// <summary>
    /// Replaces the full transport (time row, buttons, volume, status line) by the minimal bar. Call once, before the transport is hosted.
    /// <paramref name="open"/> is the optional "Open in gallery" icon button of the owner.
    /// </summary>
    internal void UseMinimalTransport(Control? open = null)
    {
        Avalonia.Threading.Dispatcher.UIThread.VerifyAccess();
        if (_minimalBar != null || IsDisposed) return;
        Detach(_playPause); Detach(_seek); Detach(_time); Detach(_mute);
        _seek.MinWidth = 0;
        _time.MinWidth = 0;
        _time.TextAlignment = Avalonia.Media.TextAlignment.Right;
        _mute.Click += (_, _) => MuteChosen?.Invoke(_muteIntent.Muted);
        _minimalBar = new InlineVideoBarPanel(_playPause, _seek, _time, _mute, open) { Name = "ReaderInlineVideoBar" };
        _transportPanel.Child = _minimalBar;
        RefreshTransportPresentation();
    }

    // The reader's video keys (they use the gallery's own commands): the actions of the keys on a player that has the minimal bar.
    internal void ToggleLoopByKey() { if (!IsDisposed) ToggleLoop(); }
    internal void SeekByStep(bool forward) => SeekRelative(TimeSpan.FromSeconds(forward ? _seekStepSeconds : -_seekStepSeconds));
}
