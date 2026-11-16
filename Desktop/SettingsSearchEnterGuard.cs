using System;
using Avalonia.Controls;
using Avalonia.Input;

namespace MeowChan.Desktop;

/// <summary>
/// Enter in the settings search picks a hit and moves focus into the setting. A held Enter keeps sending key-down messages
/// (key repeat); they must neither pick again nor reach the focused setting (Avalonia clicks buttons, check boxes and switches on
/// Enter). Avalonia does not flag repeats, so a key-down of Enter while Enter is still down (no key-up seen) is a repeat. Only the
/// first key-down of a press acts; after a pick, the repeats of that press are swallowed until Enter is released.
/// </summary>
internal sealed class SettingsSearchEnterGuard
{
    private bool _enterDown;
    private bool _armed;

    /// <summary>True while the repeats of a press that picked a hit are being swallowed.</summary>
    internal bool IsArmed => _armed;

    /// <summary>A pick made by Enter: the rest of this press is swallowed.</summary>
    internal void Arm() => _armed = true;

    /// <summary>
    /// A key-down inside the settings (seen before any control handles it); true when it must be swallowed. A fresh Enter press
    /// ends an earlier guard; any other key means Enter is not being held for repeating any more.
    /// </summary>
    internal bool SwallowKeyDown(Key key)
    {
        if (key != Key.Enter) { _enterDown = false; _armed = false; return false; }
        bool repeat = _enterDown;
        _enterDown = true;
        if (!repeat) { _armed = false; return false; }
        return _armed;
    }

    /// <summary>Releasing Enter ends the press and the guard.</summary>
    internal void KeyUp(Key key)
    {
        if (key == Key.Enter) Reset();
    }

    /// <summary>The key-up can be missed (window deactivated, editor removed): forget the press.</summary>
    internal void Reset()
    {
        _enterDown = false;
        _armed = false;
    }

    private Window? _window;

    /// <summary>
    /// Follows the window the settings sit in: Alt-Tab while Enter is held sends the key-up to another window, so a deactivation ends
    /// the press like a key-up would (otherwise the next Enter in the settings would be swallowed once). Null stops following.
    /// </summary>
    internal void Follow(Window? window)
    {
        if (ReferenceEquals(_window, window)) return;
        if (_window != null) _window.Deactivated -= OnWindowDeactivated;
        _window = window;
        if (_window != null) _window.Deactivated += OnWindowDeactivated;
    }

    internal void OnWindowDeactivated(object? sender, EventArgs e) => Reset();
}
