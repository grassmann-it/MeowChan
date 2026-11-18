using System;

namespace MeowChan.Desktop.Theming;

/// <summary>
/// TH5: "Try in window" of the colour dialog, free of any control so it can be tested. While it is on, every draft the gate accepts is put
/// on the live window through <c>applyLive</c> (<see cref="ThemeRuntime.ApplyOverrides"/>, the same call a saved change makes, so nothing new
/// is invented and nothing is written to the profile). A draft the gate refuses is never shown: the window keeps the last accepted one, so a
/// colour dragged through weak values does not flicker the window back to its saved look and a refused set never reaches the runtime.
/// Switching it off, and <see cref="Close"/> (Cancel, Escape, ×, the owner closing), put the saved colours back; after Apply the saved
/// colours <em>are</em> the draft, so closing then changes nothing.
/// <para>
/// M4: every apply recolours the whole application (a new dictionary, every <c>DynamicResource</c> resolved again), and a slider drag raises dozens
/// of changes a second. With a <c>schedule</c> the applies are coalesced: the first change applies at once, the ones that follow within
/// <see cref="CoalesceMilliseconds"/> of an apply collapse into one apply of the newest draft when that time has passed (so at most one apply per
/// interval, and the last draft always arrives). Switching on and off and closing never wait. Without a <c>schedule</c> every change applies at once.
/// </para>
/// </summary>
internal sealed class ThemeTrialSession
{
    private readonly ThemeConfiguratorModel _model;
    private readonly Action<ThemeColorOverrides> _applyLive;
    private readonly Func<Action, Action>? _schedule;
    private Action? _cancelTimer;
    private bool _on, _shown, _pending;

    /// <summary>The shortest time between two applies while drafts keep changing (M4; a timing, not a size limit).</summary>
    internal const int CoalesceMilliseconds = 120;

    /// <param name="schedule">Runs the callback once, <see cref="CoalesceMilliseconds"/> from now, on the UI thread, and returns what cancels it; null: no coalescing.</param>
    internal ThemeTrialSession(ThemeConfiguratorModel model, Action<ThemeColorOverrides> applyLive, Func<Action, Action>? schedule = null)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _applyLive = applyLive ?? throw new ArgumentNullException(nameof(applyLive));
        _schedule = schedule;
    }

    /// <summary>A change waits for the end of the current interval (tests and smokes).</summary>
    internal bool HasPending => _pending;

    /// <summary>The switch is on.</summary>
    internal bool IsOn => _on;

    /// <summary>The window currently shows a draft rather than the saved colours.</summary>
    internal bool IsShowing => _shown;

    /// <summary>The switch: on shows the draft now (when the gate accepts it), off puts the saved colours back.</summary>
    internal void SetOn(bool on)
    {
        if (_on == on) return;
        _on = on;
        if (on) Show(); else Restore();
    }

    /// <summary>The draft changed: with the switch on, the window follows it.</summary>
    internal void DraftChanged()
    {
        if (!_on) return;
        if (_cancelTimer != null) _pending = true; // inside the interval of the last apply: the newest draft is applied when it ends
        else Show();
    }

    /// <summary>The dialog is closing (however): the saved colours go back on the window if a draft was shown.</summary>
    internal void Close()
    {
        _on = false;
        Restore();
    }

    private void Show()
    {
        if (_model.Block != null) return; // the gate refuses it: the window keeps what it shows
        _applyLive(_model.Draft);
        _shown = !_model.Draft.Equals(_model.Saved);
        StartInterval();
    }

    private void StartInterval()
    {
        if (_schedule == null) return;
        _cancelTimer?.Invoke();
        _cancelTimer = _schedule(OnIntervalEnd);
    }

    private void OnIntervalEnd()
    {
        _cancelTimer = null;
        if (!_on || !_pending) { _pending = false; return; }
        _pending = false;
        Show(); // starts the next interval when it applied; a refused newest draft leaves the window as it is and the interval over
    }

    private void Restore()
    {
        // Switching off and closing never wait for an interval: what is waiting is dropped, the saved colours go back at once.
        _cancelTimer?.Invoke();
        _cancelTimer = null;
        _pending = false;
        if (!_shown) return;
        _shown = false;
        _applyLive(_model.Saved);
    }
}
