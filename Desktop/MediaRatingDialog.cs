using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Threading;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Favorites;

namespace MeowChan.Desktop;

/// <summary>One immutable key per dialog; pointer commits and keyboard confirmation share the host executor.</summary>
internal sealed class MediaRatingDialog : Window
{
    /// <summary>The dialog's size step (read by the footer budget test).</summary>
    internal const DialogSize DialogStep = DialogSize.M;
    private readonly Ursa.Controls.Rating _rating;
    private readonly TextBlock _value = new() { Name = "MediaRatingValue" };
    private readonly TextBlock _confirmedValue = new() { Name = "MediaRatingConfirmed", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _error = new() { Name = "MediaRatingError", TextWrapping = TextWrapping.Wrap };
    private readonly Button _retry = new() { Name = "MediaRatingRetry", IsVisible = false };
    private readonly Button _clear;
    private readonly Button _close;
    private readonly Func<int?, Task> _commit;
    private readonly FavoriteAssetKey _key;
    private readonly SelectableTextBlock _identity;
    private readonly TextBlock _help = new() { TextWrapping = TextWrapping.Wrap };
    private bool _closed;
    private bool _keyboardChoice;
    private Func<string>? _errorLabel;
    private string? _errorDetails;
    private bool _busy;
    private int? _confirmed;
    private int? _attempt;
    internal MediaRatingDialog(FavoriteAssetKey key, int? value, Func<int?, Task> commit)
    {
        _commit = commit; _key = key; _confirmed = value;
        Name = "MediaRatingDialog"; Title = MediaRatingText.LocalizedLabel;
        // Modals plan MD1: a modal form (its error state with a wrapped footer needs the form step), fitted to the screen;
        // the title stands in the title bar only.
        DialogWindow.Configure(this, DialogStep, modal: true);
        _rating = new() { Name = "MediaRatingEditor", Count = 5, AllowHalf = false, AllowClear = false,
            DefaultValue = value ?? 0, Value = value ?? 0, Focusable = true, Size = 28 };
        AutomationProperties.SetName(_rating, MediaRatingText.LocalizedLabel);
        _value.Text = MediaRatingText.LocalizedValue(value);
        _confirmedValue.Text = ConfirmedText();
        _confirmedValue.Classes.Add("workspace-meta");
        AutomationProperties.SetName(_rating, MediaRatingText.AccessibleValue(_value.Text ?? string.Empty));
        _error.Classes.Add("workspace-error"); AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Assertive);
        _clear = CommandBindings.CreateButton("media.rating.clear", async () => await SaveAsync(null), () => !_busy && _confirmed.HasValue);
        _clear.Name = "MediaRatingClear";
        _close = new() { Name = "MediaRatingClose", Content = MediaRatingText.Catalog("media.rating.close") };
        _close.Click += (_, _) => Close();
        _retry.Content = MediaRatingText.Catalog("media.rating.retry");
        _retry.Click += async (_, _) => await SaveAsync(_attempt);
        _clear.Classes.Add("danger");
        Border actions = DialogFooter.Build(_close, new Control[] { _clear, _retry });
        _identity = new() { Text = MediaRatingText.LocalizedIdentity(key), TextWrapping = TextWrapping.Wrap };
        _identity.Classes.Add("workspace-meta");
        // UX103 P7: the save/remove error in the local strip on top, the asset identity and editor
        // scroll, and the actions stay in the fixed footer (Close right-most).
        StackPanel body = new() { Spacing = 12, Margin = new Thickness(16, 0, 16, 16) };
        body.Children.Add(_identity); body.Children.Add(_confirmedValue);
        body.Children.Add(_rating); body.Children.Add(_value);
        _help.Classes.Add("workspace-help"); body.Children.Add(_help);
        DialogWindow.Frame(this, DialogFrame.Build(header: null, body, actions, DialogFrame.ErrorStrip(_error, maxHeight: 72)), modal: true);
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
                _keyboardChoice = true;
                RefreshValueText(); e.Handled = true;
            }
            else if (e.Key is Key.Enter or Key.Space)
            { e.Handled = true; if (_rating.Value >= 1) await SaveAsync((int)_rating.Value); }
        };
        DialogEscape.Attach(this, isBusy: () => _busy);
        CommandRouter router = new(this);
        for (int number = 1; number <= 5; number++)
        {
            int selected = number;
            router.Register(new("media.rating.set-" + number, this, () => IsVisible, () => !_busy, () => _ = SaveAsync(selected), Priority: 300));
        }
        router.Register(new("media.rating.clear", this, () => IsVisible, () => !_busy && _confirmed.HasValue, () => _ = SaveAsync(null), Priority: 300));
        KeyDown += (_, e) => router.Route(e);
        Closed += (_, _) => { Volatile.Write(ref _closed, true); Texts.Changed -= OnLanguageChanged; router.Dispose(); };
        Texts.Changed += OnLanguageChanged;
        ApplyTexts();
        Closing += (_, e) => e.Cancel = _busy;
        Opened += (_, _) => { _rating.Value = _confirmed ?? 0; _rating.Focus(); };
    }
    private void OnLanguageChanged(object? sender, EventArgs args)
    {
        if (Volatile.Read(ref _closed)) return;
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(ApplyTexts); return; }
        ApplyTexts();
    }

    private void ApplyTexts()
    {
        // Also guards callbacks queued before Closed; never reset editor or operation state here.
        if (Volatile.Read(ref _closed)) return;
        Title = MediaRatingText.LocalizedLabel;
        _identity.Text = MediaRatingText.LocalizedIdentity(_key);
        _help.Text = MediaRatingText.Catalog("media.rating.help");
        _confirmedValue.Text = ConfirmedText();
        RefreshValueText();
        CommandBindings.Apply(_clear, "media.rating.clear");
        _close.Content = MediaRatingText.Catalog("media.rating.close");
        _retry.Content = MediaRatingText.Catalog("media.rating.retry");
        CommandBindings.BindShortcutHelp(_rating,
            new[] { "media.rating.set-1", "media.rating.set-2", "media.rating.set-3", "media.rating.set-4", "media.rating.set-5", "media.rating.clear" },
            () => MediaRatingText.Catalog("media.rating.keyboardHelp"));
        RefreshErrorText();
    }

    private void RefreshValueText()
    {
        _value.Text = _keyboardChoice
            ? MediaRatingText.KeyboardValue((int)_rating.Value)
            : MediaRatingText.LocalizedValue(_confirmed);
        AutomationProperties.SetName(_rating, MediaRatingText.AccessibleValue(_value.Text ?? string.Empty));
    }

    private void RefreshErrorText() =>
        _error.Text = _errorLabel == null ? string.Empty : MediaRatingText.FormatError(_errorLabel(), _errorDetails);

    private string ConfirmedText() => MediaRatingText.LocalizedSaved(_confirmed);

    private async Task SaveAsync(int? value)
    {
        if (_busy) return;
        _attempt = value; _busy = true; _errorLabel = null; _errorDetails = null; RefreshErrorText(); _retry.IsVisible = false;
        _rating.IsEnabled = _clear.IsEnabled = _close.IsEnabled = false;
        try { await _commit(value); _confirmed = value; }
        catch (Exception error)
        {
            // Snapshot provider details once; only our operation label is translated later.
            _errorDetails = error.Message;
            _errorLabel = value.HasValue
                ? () => MediaRatingText.Catalog("media.rating.error.save")
                : () => MediaRatingText.Catalog("media.rating.error.remove");
            RefreshErrorText(); _retry.IsVisible = true;
        }
        finally
        {
            _confirmedValue.Text = ConfirmedText();
            _busy = false; _keyboardChoice = false; _rating.DefaultValue = _confirmed ?? 0; _rating.Value = _confirmed ?? 0; _value.Text = MediaRatingText.LocalizedValue(_confirmed);
            AutomationProperties.SetName(_rating, MediaRatingText.AccessibleValue(_value.Text ?? string.Empty));
            _rating.IsEnabled = _close.IsEnabled = true; CommandBindings.Refresh(_clear);
        }
    }
}
