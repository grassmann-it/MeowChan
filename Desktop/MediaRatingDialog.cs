using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;
using MeowChan.Favorites;

namespace MeowChan.Desktop;

/// <summary>One immutable key per dialog; pointer commits and keyboard confirmation share the host executor.</summary>
internal sealed class MediaRatingDialog : Window
{
    private readonly Ursa.Controls.Rating _rating;
    private readonly TextBlock _value = new() { Name = "MediaRatingValue" };
    private readonly TextBlock _error = new() { Name = "MediaRatingError", TextWrapping = TextWrapping.Wrap };
    private readonly Button _retry = new() { Name = "MediaRatingRetry", IsVisible = false };
    private readonly Button _clear;
    private readonly Button _close;
    private readonly Func<int?, Task> _commit;
    private bool _busy;
    private int? _confirmed;
    private int? _attempt;
    internal MediaRatingDialog(FavoriteAssetKey key, int? value, Func<int?, Task> commit)
    {
        _commit = commit; _confirmed = value;
        Name = "MediaRatingDialog"; Title = MediaRatingText.Label;
        Width = 520; Height = 330; MinWidth = 320; MinHeight = 280;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _rating = new() { Name = "MediaRatingEditor", Count = 5, AllowHalf = false, AllowClear = false,
            DefaultValue = value ?? 0, Value = value ?? 0, Focusable = true, Size = 28 };
        AutomationProperties.SetName(_rating, MediaRatingText.Label);
        CommandBindings.BindShortcutHelp(_rating,
            new[] { "media.rating.set-1", "media.rating.set-2", "media.rating.set-3", "media.rating.set-4", "media.rating.set-5", "media.rating.clear" },
            () => MediaRatingText.Get("Arrow keys choose; Enter saves. Escape closes without saving a keyboard choice.", "Pfeiltasten wählen; Enter speichert. Escape schließt ohne Übernahme einer Tastaturauswahl."));
        _value.Text = MediaRatingText.Value(value);
        AutomationProperties.SetName(_rating, MediaRatingText.Label + ": " + _value.Text);
        _error.Classes.Add("error"); AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Assertive);
        _clear = CommandBindings.CreateButton("media.rating.clear", async () => await SaveAsync(null), () => !_busy && _confirmed.HasValue);
        _clear.Name = "MediaRatingClear";
        _close = new() { Name = "MediaRatingClose", Content = MediaRatingText.Get("Close", "Schließen") };
        _close.Click += (_, _) => Close();
        _retry.Content = MediaRatingText.Get("Retry", "Wiederholen");
        _retry.Click += async (_, _) => await SaveAsync(_attempt);
        StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(_clear); actions.Children.Add(_retry); actions.Children.Add(_close);
        StackPanel body = new() { Spacing = 10, Margin = new Thickness(18) };
        body.Children.Add(new TextBlock { Text = MediaRatingText.Label });
        body.Children.Add(new SelectableTextBlock { Text = MediaRatingText.Identity(key), TextWrapping = TextWrapping.Wrap, FontSize = 11 });
        body.Children.Add(_rating); body.Children.Add(_value);
        body.Children.Add(new TextBlock { Text = MediaRatingText.Get("Click to save • Arrows choose, Enter saves", "Klick speichert • Pfeile wählen, Enter speichert"), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(_error); body.Children.Add(actions); Content = new ScrollViewer { Content = body };
        _rating.AddHandler(PointerReleasedEvent, async (_, e) =>
        {
            if (_busy) return;
            if (e.InitialPressMouseButton != MouseButton.Left || e.Source is not Visual visual
                || !visual.GetSelfAndVisualAncestors().OfType<Ursa.Controls.RatingCharacter>().Any())
            { _rating.Value = _confirmed ?? 0; return; }
            if (_rating.Value is < 1 or > 5 || _rating.Value != Math.Truncate(_rating.Value)) return;
            e.Handled = true; await SaveAsync((int)_rating.Value);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        _rating.KeyDown += async (_, e) =>
        {
            if (_busy) { e.Handled = true; return; }
            if (e.Key is Key.Left or Key.Down or Key.Right or Key.Up)
            {
                _rating.Value = Math.Clamp(_rating.Value + (e.Key is Key.Left or Key.Down ? -1 : 1), 1, 5);
                _value.Text = MediaRatingText.Value((int)_rating.Value) + MediaRatingText.Get(" (Enter to save)", " (Enter zum Speichern)");
                AutomationProperties.SetName(_rating, MediaRatingText.Label + ": " + _value.Text); e.Handled = true;
            }
            else if (e.Key is Key.Enter or Key.Space)
            { e.Handled = true; if (_rating.Value >= 1) await SaveAsync((int)_rating.Value); }
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; if (!_busy) Close(); } };
        CommandRouter router = new(this);
        for (int number = 1; number <= 5; number++)
        {
            int selected = number;
            router.Register(new("media.rating.set-" + number, this, () => IsVisible, () => !_busy, () => _ = SaveAsync(selected), Priority: 300));
        }
        router.Register(new("media.rating.clear", this, () => IsVisible, () => !_busy && _confirmed.HasValue, () => _ = SaveAsync(null), Priority: 300));
        KeyDown += (_, e) => router.Route(e);
        Closed += (_, _) => router.Dispose();
        Closing += (_, e) => e.Cancel = _busy;
        Opened += (_, _) => { _rating.Value = _confirmed ?? 0; _rating.Focus(); };
    }
    private async Task SaveAsync(int? value)
    {
        if (_busy) return;
        _attempt = value; _busy = true; _error.Text = string.Empty; _retry.IsVisible = false;
        _rating.IsEnabled = _clear.IsEnabled = _close.IsEnabled = false;
        try { await _commit(value); _confirmed = value; }
        catch (Exception error) { _error.Text = value.HasValue ? MediaRatingText.SaveError(error) : MediaRatingText.RemoveError(error); _retry.IsVisible = true; }
        finally
        {
            _busy = false; _rating.DefaultValue = _confirmed ?? 0; _rating.Value = _confirmed ?? 0; _value.Text = MediaRatingText.Value(_confirmed);
            AutomationProperties.SetName(_rating, MediaRatingText.Label + ": " + _value.Text);
            _rating.IsEnabled = _close.IsEnabled = true; CommandBindings.Refresh(_clear);
        }
    }
}
