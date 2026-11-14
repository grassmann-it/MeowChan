using System;

namespace MeowChan.Desktop.Theming;

/// <summary>
/// TH5: "Try in window" of the colour dialog, free of any control so it can be tested. While it is on, every draft the gate accepts is put
/// on the live window through <c>applyLive</c> (<see cref="ThemeRuntime.ApplyOverrides"/>, the same call a saved change makes, so nothing new
/// is invented and nothing is written to the profile). A draft the gate refuses is never shown: the window keeps the last accepted one, so a
/// colour dragged through weak values does not flicker the window back to its saved look and a refused set never reaches the runtime.
/// Switching it off, and <see cref="Close"/> (Cancel, Escape, ×, the owner closing), put the saved colours back; after Apply the saved
/// colours <em>are</em> the draft, so closing then changes nothing.
/// </summary>
internal sealed class ThemeTrialSession
{
    private readonly ThemeConfiguratorModel _model;
    private readonly Action<ThemeColorOverrides> _applyLive;
    private bool _on, _shown;

    internal ThemeTrialSession(ThemeConfiguratorModel model, Action<ThemeColorOverrides> applyLive)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _applyLive = applyLive ?? throw new ArgumentNullException(nameof(applyLive));
    }

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
        if (_on) Show();
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
    }

    private void Restore()
    {
        if (!_shown) return;
        _shown = false;
        _applyLive(_model.Saved);
    }
}
