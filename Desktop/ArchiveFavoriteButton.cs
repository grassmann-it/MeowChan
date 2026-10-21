using System;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

internal sealed class ArchiveFavoriteButton : ToggleButton
{
    private Func<bool>? _available;
    private Func<bool>? _marked;
    private Func<Task>? _toggle;
    private bool _running;

    internal ArchiveFavoriteButton()
    {
        Name = "ArchiveMediaFavorite";
        Padding = new Avalonia.Thickness(6);
        CommandBindings.Apply(this, "favorites.toggle", iconOnly: true);
        Click += async (_, e) =>
        {
            e.Handled = true;
            if (_running || _available?.Invoke() != true || _toggle == null) return;
            _running = true; IsEnabled = false;
            try { await _toggle(); }
            catch (Exception error) { ToolTip.SetTip(this, error.Message); }
            finally { _running = false; Refresh(); }
        };
    }

    internal void Configure(Func<bool>? available, Func<bool>? marked, Func<Task>? toggle)
    { _available = available; _marked = marked; _toggle = toggle; Refresh(); }

    internal void Refresh()
    {
        bool marked = _marked?.Invoke() == true;
        IsEnabled = !_running && _available?.Invoke() == true;
        IsChecked = marked;
        Content = CommandBindings.Icon("favorites.toggle", state: marked ? "marked" : "unmarked");
        Foreground = marked ? Brushes.Gold : null;
        string label = Texts.T(marked ? "viewer.favorite.remove" : "viewer.favorite.add");
        ToolTip.SetTip(this, IsEnabled ? label : Texts.T("archive.notSaved"));
        AutomationProperties.SetName(this, label);
        AutomationProperties.SetHelpText(this, IsEnabled ? label : Texts.T("archive.notSaved"));
    }
}
