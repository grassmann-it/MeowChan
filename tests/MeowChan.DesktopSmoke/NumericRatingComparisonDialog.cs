using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MeowChan.Favorites;
using MeowChan.Desktop;

namespace MeowChan.DesktopSmoke;

/// <summary>Test-only labelled numeric alternative. Same exact-key host callback as production; no alternate store.</summary>
internal sealed class NumericRatingComparisonDialog : Window
{
    private readonly ComboBox _choice;
    private readonly Button _clear, _close;
    private readonly Button _retry = new() { Name = "NumericRatingRetry", Content = MediaRatingText.Get("Retry", "Wiederholen"), IsVisible = false };
    private readonly TextBlock _error = new() { Name = "NumericRatingError", TextWrapping = TextWrapping.Wrap };
    private readonly Func<int?, Task> _commit;
    private int? _confirmed, _attempt;
    private bool _busy;
    internal NumericRatingComparisonDialog(FavoriteAssetKey key, int? initial, Func<int?, Task> commit)
    {
        _confirmed = initial; _commit = commit;
        Title = MediaRatingText.Get("Test-only numeric rating comparison", "Numerische Bewertung – Testvergleich"); Width = 520; Height = 330;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _choice = new ComboBox { Name = "NumericRatingEditor", ItemsSource = Enumerable.Range(0, 6).Select(value => MediaRatingText.Value(value == 0 ? null : value)).ToArray(), SelectedIndex = initial ?? 0, MinWidth = 180 };
        AutomationProperties.SetName(_choice, MediaRatingText.Get("Media rating numeric choice", "Medienbewertung als Zahl"));
        AutomationProperties.SetHelpText(_choice, MediaRatingText.Get("Arrows choose; Enter saves. Escape closes without saving a keyboard choice.", "Pfeiltasten wählen; Enter speichert. Escape schließt ohne Übernahme einer Tastaturauswahl."));
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Assertive);
        _clear = new Button { Name = "NumericRatingClear", Content = MediaRatingText.Get("Clear rating", "Bewertung entfernen"), IsEnabled = initial.HasValue };
        _close = new Button { Name = "NumericRatingClose", Content = MediaRatingText.Get("Close", "Schließen") };
        _clear.Click += async (_, _) => await SaveAsync(null);
        _retry.Click += async (_, _) => await SaveAsync(_attempt);
        _close.Click += (_, _) => Close();
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(_clear); buttons.Children.Add(_retry); buttons.Children.Add(_close);
        StackPanel body = new() { Margin = new Thickness(18), Spacing = 12 };
        body.Children.Add(new TextBlock { Text = MediaRatingText.Get("Media rating (test-only numeric alternative)", "Medienbewertung (numerische Testalternative)") });
        body.Children.Add(new TextBlock { Text = MediaRatingText.Identity(key), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(_choice); body.Children.Add(new TextBlock { Text = MediaRatingText.Get("Arrows choose • Enter saves • Escape cancels draft", "Pfeile wählen • Enter speichert • Escape verwirft Auswahl") });
        body.Children.Add(_error); body.Children.Add(buttons); Content = new ScrollViewer { Content = body };
        // Capture before ComboBox's own key processing: exact same draft/commit tasks as Rating.
        _choice.AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (_busy) { e.Handled = true; return; }
            if (e.Key is Key.Left or Key.Down or Key.Right or Key.Up)
            { e.Handled = true; _choice.SelectedIndex = Math.Clamp(_choice.SelectedIndex + (e.Key is Key.Left or Key.Down ? -1 : 1), 1, 5); }
            else if (e.Key is Key.Enter or Key.Space)
            { e.Handled = true; if (_choice.SelectedIndex >= 1) await SaveAsync(_choice.SelectedIndex); }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; if (!_busy) Close(); } };
        Closing += (_, e) => e.Cancel = _busy;
        Opened += (_, _) => _choice.Focus();
    }
    private async Task SaveAsync(int? value)
    {
        if (_busy) return;
        _busy = true; _attempt = value; _error.Text = string.Empty; _retry.IsVisible = false;
        _choice.IsEnabled = _clear.IsEnabled = _close.IsEnabled = false;
        try { await _commit(value); _confirmed = value; }
        catch (Exception error) { _error.Text = error.Message; _retry.IsVisible = true; }
        finally
        {
            _busy = false; _choice.SelectedIndex = _confirmed ?? 0;
            _choice.IsEnabled = _close.IsEnabled = true; _clear.IsEnabled = _confirmed.HasValue;
        }
    }
}
