using System;
using Avalonia.Input;

namespace MeowChan.Desktop;

/// <summary>
/// Retains ownership of Escape while a native playback ComboBox is open and
/// for a short grace period after it closes. Avalonia's macOS backend can
/// dismiss the popup before the key reaches the owning window; gallery routing
/// must not turn that same key into a close/stop command.
/// </summary>
internal sealed class PlaybackMenuEscapeGuard
{
    internal const long GraceMilliseconds = 500;

    private readonly Func<long> _nowMilliseconds;
    private bool _open;
    private long _expiresAt;

    internal PlaybackMenuEscapeGuard(Func<long>? nowMilliseconds = null) =>
        _nowMilliseconds = nowMilliseconds ?? (() => Environment.TickCount64);

    internal void Open()
    {
        _open = true;
        _expiresAt = 0;
    }

    internal void Close()
    {
        _open = false;
        long now = _nowMilliseconds();
        _expiresAt = now > long.MaxValue - GraceMilliseconds ? long.MaxValue : now + GraceMilliseconds;
    }

    /// <summary>
    /// Called by the gallery tunnel handler. While the popup is open, leave the
    /// event unhandled so the ComboBox can dismiss itself. After dismissal,
    /// consume a delayed Escape before gallery routing sees it.
    /// </summary>
    internal bool BypassGalleryRouting(KeyEventArgs e)
    {
        if (!IsEscape(e)) return false;
        if (_open) return true;
        if (!WithinGracePeriod()) return false;
        e.Handled = true;
        _expiresAt = 0;
        return true;
    }

    internal bool Consume(KeyEventArgs e)
    {
        if (!IsEscape(e)) return false;
        if (!_open && !WithinGracePeriod()) return false;
        _open = false;
        _expiresAt = 0;
        e.Handled = true;
        return true;
    }

    private bool WithinGracePeriod()
    {
        if (_expiresAt == 0) return false;
        if (_nowMilliseconds() > _expiresAt)
        {
            _expiresAt = 0;
            return false;
        }
        return true;
    }

    private static bool IsEscape(KeyEventArgs e) =>
        e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None;
}
