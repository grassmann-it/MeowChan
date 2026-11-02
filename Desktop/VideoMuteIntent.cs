namespace MeowChan.Desktop;

/// <summary>
/// One gallery player's audio intent, independent of its replaceable native playback.
/// Startup policy seeds selections until the user chooses mute/unmute (or edits a positive
/// volume). Backend observations and programmatic volume/config updates are not user intent.
/// A new gallery owns a new instance; nothing here is a persisted preference.
/// </summary>
internal sealed class VideoMuteIntent
{
    private readonly object _gate = new();
    private bool _startMuted;
    private bool? _userMuted;
    private bool _muted;

    internal bool Muted { get { lock (_gate) return _muted; } }

    internal void ConfigureStartup(bool startMuted)
    { lock (_gate) _startMuted = startMuted; }

    internal bool BeginMedia()
    { lock (_gate) return _muted = _userMuted ?? _startMuted; }

    internal bool Toggle()
    {
        lock (_gate)
        {
            _muted = !_muted;
            _userMuted = _muted;
            return _muted;
        }
    }

    internal void OnVolumeChanged(int volume, bool userInitiated)
    {
        // Retain the existing positive-user-volume unmute behavior. In particular a
        // late AudioReady replay of that volume must not undo a *later* manual mute.
        if (!userInitiated || volume <= 0) return;
        lock (_gate) { _userMuted = false; _muted = false; }
    }
}
