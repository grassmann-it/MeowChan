namespace MeowChan.Desktop;

/// <summary>
/// What the window's state means for a running slideshow (Tom, 14.11.). "Pause when MeowChan loses focus" is a profile choice, off by
/// default: off, the slideshow ignores focus, minimise and the tray (all three are "the window is not the active one"); on, it waits for
/// the window and, when the window is back, shows the current medium for a full new period (RenewAfterHold) instead of what was left.
/// The surface check is not a focus question: the main window disabled by a dialog or the gallery put away for the reader always stops it.
/// </summary>
internal sealed class GallerySlideshowWindowGate
{
    private bool _focusHeld;

    /// <summary>The slideshow waits for the window now because of the setting (diagnostics and tests).</summary>
    internal bool FocusHeld => _focusHeld;

    /// <summary>Whether the window's state lets a slideshow run: the surface must be usable, and with the setting on the window must have the focus.</summary>
    internal static bool Allows(bool pauseOnFocusLoss, bool surfaceUsable, bool windowFocused)
        => surfaceUsable && (!pauseOnFocusLoss || windowFocused);

    /// <summary>
    /// Takes the window's present state. Returns true when a focus wait just ended (the window is back, or the setting was turned off):
    /// the caller then calls <see cref="GallerySlideshowController.RenewAfterHold"/> before it releases the hold.
    /// </summary>
    internal bool Observe(bool pauseOnFocusLoss, bool surfaceUsable, bool windowFocused)
    {
        bool held = pauseOnFocusLoss && surfaceUsable && !windowFocused;
        bool ended = _focusHeld && !held;
        _focusHeld = held;
        return ended;
    }
}
