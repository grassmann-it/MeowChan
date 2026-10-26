using System;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Layout;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

internal sealed class ArchiveFavoriteButton : ToggleButton
{
    private Func<bool>? _available;
    private Func<bool>? _marked;
    private Func<Task>? _toggle;
    private bool _running;
    private Func<string?>? _errorSource;
    private string? _error;

    internal ArchiveFavoriteButton()
    {
        Name = "ArchiveMediaFavorite";
        Padding = new Avalonia.Thickness(6);
        CommandBindings.Apply(this, "favorites.toggle", iconOnly: true);
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Assertive);
        Click += async (_, e) =>
        {
            e.Handled = true;
            if (_running || _available?.Invoke() != true || _toggle == null) return;
            _error = null;
            IsChecked = _marked?.Invoke() == true;
            _running = true; IsEnabled = false;
            try { await _toggle(); }
            catch (Exception error) { _error = error.Message; }
            finally { _running = false; Refresh(); }
        };
    }

    internal void Configure(Func<bool>? available, Func<bool>? marked, Func<Task>? toggle, Func<string?>? error = null)
    { _available = available; _marked = marked; _toggle = toggle; _errorSource = error; Refresh(); }

    // Availability also owns the callbacks' lifetime: a queued layout or toggle completion
    // may refresh a control after its page has ended and released the session.
    internal static (bool Available, bool Marked, string? Error) ReadState(
        Func<bool>? available, Func<bool>? marked, Func<string?>? error)
    {
        if (available?.Invoke() != true) return (false, false, null);
        return (true, marked?.Invoke() == true, error?.Invoke());
    }

    internal void Refresh()
    {
        var state = ReadState(_available, _marked, _errorSource);
        bool marked = state.Marked;
        if (!state.Available || _errorSource != null) _error = state.Error;
        IsEnabled = !_running && state.Available;
        IsChecked = marked;
        Control icon = CommandBindings.Icon("favorites.toggle", state: marked ? "marked" : "unmarked");
        Content = _error == null ? icon : new StackPanel
        {
            Spacing = 4, MaxWidth = 220,
            Children = { icon, new TextBlock { Text = _error, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Left } }
        };
        if (marked && _error == null) Foreground = Brushes.Gold;
        else ClearValue(ForegroundProperty);
        string label = Texts.T(marked ? "viewer.favorite.remove" : "viewer.favorite.add");
        string description = _error == null ? (IsEnabled ? label : Texts.T("archive.notSaved")) : label + ": " + _error;
        ToolTip.SetTip(this, description);
        AutomationProperties.SetName(this, description);
        AutomationProperties.SetHelpText(this, description);
    }
}
