using System;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
    private bool _tinted;
    private bool _failed;
    private string? _shownError;

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
        // ★ filled when marked, ☆ outline otherwise: the same pair as the thread list and the gallery badge.
        Content = CommandBindings.Icon("favorites.toggle", state: marked ? "marked" : "unmarked");
        // A failed write keeps the star in place: an error edge plus the reason in the tooltip, which
        // opens once for a new error, instead of a text block that pushes the card layout apart.
        bool failed = _error != null;
        if (failed != _failed)
        {
            _failed = failed;
            if (failed)
            {
                this[!BorderBrushProperty] = new DynamicResourceExtension("Workspace.Error");
                BorderThickness = new Avalonia.Thickness(1.5);
            }
            else { ClearValue(BorderBrushProperty); ClearValue(BorderThicknessProperty); }
        }
        bool tinted = marked && _error == null;
        if (tinted != _tinted)
        {
            _tinted = tinted;
            // Gold only reads on the dark surfaces; the theme key carries a darker amber for Light.
            if (tinted) this[!ForegroundProperty] = new DynamicResourceExtension("Workspace.Favorite");
            else ClearValue(ForegroundProperty);
        }
        string label = Texts.T(marked ? "viewer.favorite.remove" : "viewer.favorite.add");
        string description = _error == null ? (IsEnabled ? label : Texts.T("archive.notSaved")) : label + ": " + _error;
        ToolTip.SetShowOnDisabled(this, true);
        ToolTip.SetTip(this, description);
        AutomationProperties.SetName(this, description);
        AutomationProperties.SetHelpText(this, description);
        if (_error != null && !string.Equals(_error, _shownError, StringComparison.Ordinal))
        {
            _shownError = _error;
            Dispatcher.UIThread.Post(() =>
            {
                if (_error == _shownError && this.IsAttachedToVisualTree() && IsEffectivelyVisible) ToolTip.SetIsOpen(this, true);
            }, DispatcherPriority.Loaded);
        }
        else if (_error == null) _shownError = null;
    }
}
