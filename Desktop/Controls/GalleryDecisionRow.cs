using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// The favorite and rating row on top of the gallery tag panel (PT6, decision E2): "★ Favorite" and five rating stars with a remove button,
/// and under them one live line ("Rating: 4 of 5 · Favorite"). The row shows a <see cref="GalleryDecisionView"/> and raises requests; the
/// viewer writes. The five stars are a group of toggle buttons of which exactly the rated one is checked (the radio role: a screen
/// reader names "Rating: 4 of 5" and its state); the filled stars up to the rating carry the sight. A click never takes the focus out of the
/// tag field: when the field had it before the pointer went down, it gets it back, so the keys keep typing tags.
/// </summary>
internal sealed class GalleryDecisionRow : StackPanel
{
    private readonly ToggleButton _favorite = new() { Classes = { "media-control" } };
    private readonly ToggleButton[] _stars = new ToggleButton[5];
    private readonly Button _clear = new();
    private readonly StackPanel _group = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly TextBlock _summary = new() { Classes = { "workspace-help" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBlock _favoriteLabel = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _favoriteContent;
    private GalleryDecisionView _view = GalleryDecisionView.Unavailable;
    private bool _restoreFocus;
    private bool _attached;
    private string? _keyContext;

    internal GalleryDecisionRow(string name)
    {
        Name = name;
        Spacing = 4;
        _favorite.Name = name + "Favorite"; _clear.Name = name + "RatingClear"; _group.Name = name + "Rating"; _summary.Name = name + "Summary";
        _favorite.IsThreeState = true;
        _favoriteContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { FavoriteStar.Create(false, 16), _favoriteLabel } };
        _favorite.Content = _favoriteContent;
        WrapPanel line = new() { ItemSpacing = 12, LineSpacing = 4, Name = name + "Line" };
        line.Children.Add(_favorite);
        for (int index = 0; index < _stars.Length; index++)
        {
            int number = index + 1;
            ToggleButton star = _stars[index] = new ToggleButton { Name = name + "Rating" + number, Classes = { WorkspaceIcons.SquareClass, "subtle" } };
            star.Click += (_, _) => OnStar(number);
            _group.Children.Add(star);
        }
        _clear.Classes.Add(WorkspaceIcons.SquareClass);
        _clear.Classes.Add("subtle");
        _clear.Click += OnClear;
        _group.Children.Add(_clear);
        line.Children.Add(_group);
        Children.Add(line);
        Children.Add(_summary);
        _favorite.Click += OnFavorite;
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, (_, _) => _restoreFocus = false, RoutingStrategies.Bubble, handledEventsToo: true);
        AttachedToVisualTree += (_, _) => { if (_attached) return; _attached = true; Texts.Changed += OnLanguageChanged; Localize(); };
        DetachedFromVisualTree += (_, _) => { if (!_attached) return; _attached = false; Texts.Changed -= OnLanguageChanged; };
        Localize();
        Show(GalleryDecisionView.Unavailable);
    }

    internal ToggleButton FavoriteButton => _favorite;
    /// <summary>False hides the favourite button (a popover whose host has no favourites call for its targets); the stars stay.</summary>
    internal bool FavoriteVisible { get => _favorite.IsVisible; set => _favorite.IsVisible = value; }
    internal IReadOnlyList<ToggleButton> StarButtons => _stars;
    internal Button ClearButton => _clear;
    internal TextBlock Summary => _summary;
    internal GalleryDecisionView View => _view;
    internal Control RatingGroup => _group;

    /// <summary>The star that takes the focus: the checked one (the current rating), star 1 while the medium is unrated.</summary>
    internal ToggleButton FocusStar => _stars.FirstOrDefault(star => star.IsChecked == true) ?? _stars[0];

    /// <summary>Puts the keyboard focus on <see cref="FocusStar"/> (as a Tab would); false when the row cannot take it (disabled or not shown).</summary>
    internal bool FocusRating()
    {
        ToggleButton star = FocusStar;
        return star.IsEffectivelyEnabled && star.IsEffectivelyVisible && star.Focus(NavigationMethod.Tab);
    }

    /// <summary>True while the keyboard focus is on one of the stars or the remove button.</summary>
    internal bool IsRatingFocused => _stars.Any(star => star.IsFocused) || _clear.IsFocused;

    /// <summary>
    /// Left and Right (Home and End) move the focus along the stars and the remove button like a radio group; they stop at the ends. Without
    /// this the gallery would read the arrows as "previous / next medium" under a keyboard user who is choosing a rating.
    /// </summary>
    internal bool RouteStarKey(KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Left or Key.Right or Key.Home or Key.End)) return false;
        Control[] order = _stars.Cast<Control>().Append(_clear).Where(control => control.IsEffectivelyEnabled && control.IsEffectivelyVisible).ToArray();
        int at = Array.FindIndex(order, control => control.IsFocused);
        if (at < 0) return false;
        int to = e.Key switch { Key.Left => Math.Max(0, at - 1), Key.Right => Math.Min(order.Length - 1, at + 1), Key.Home => 0, _ => order.Length - 1 };
        if (to != at) order[to].Focus(NavigationMethod.Directional);
        return true;
    }

    /// <summary>
    /// A rating key (Shift+1 to Shift+5, Shift+0): the request a click on that star (or on the remove button) makes. False when the row cannot rate now
    /// (nothing shown, disabled), so the key is not taken.
    /// </summary>
    internal bool RequestRating(int? value)
    {
        if (!_view.Available || value is < 1 or > 5) return false;
        if (value == null && !_view.Rating.HasValue && !_view.RatingMixed) return true; // nothing to remove: the key is the row's, it changes nothing
        Show(_view);
        RatingRequested?.Invoke(value);
        return true;
    }

    /// <summary>
    /// A favourite key (M adds, U removes, F toggles): the request the button makes, as an explicit flag. A key that changes nothing (add on a
    /// favourite) is the row's and does nothing; false when the row has no favourite call or cannot act.
    /// </summary>
    internal bool RequestFavorite(FavoriteKeyAction action)
    {
        if (!_view.Available || !FavoriteVisible || action == FavoriteKeyAction.None) return false;
        bool wanted = action switch { FavoriteKeyAction.Add => true, FavoriteKeyAction.Remove => false, _ => _view.NextFavorite };
        bool all = _view.Favorite == GalleryDecisionCoverage.All, none = _view.Favorite == GalleryDecisionCoverage.None;
        if (action == FavoriteKeyAction.Add && all || action == FavoriteKeyAction.Remove && none) return true;
        Show(_view);
        FavoriteRequested?.Invoke(wanted);
        return true;
    }

    /// <summary>The favorite button was pressed: the flag the targets should get (every target not yet a favorite becomes one).</summary>
    internal event Action<bool>? FavoriteRequested;
    /// <summary>A star (1–5) or the remove button (null) was pressed.</summary>
    internal event Action<int?>? RatingRequested;
    /// <summary>True while the tag field holds the keyboard focus (read when the pointer goes down on the row).</summary>
    internal Func<bool>? FieldFocused { get; set; }
    /// <summary>Gives the focus back to the tag field after a pointer click on the row.</summary>
    internal Action? ReturnFocus { get; set; }

    /// <summary>Shows what the targets carry now.</summary>
    internal void Show(GalleryDecisionView view)
    {
        _view = view ?? throw new ArgumentNullException(nameof(view));
        bool all = view.Favorite == GalleryDecisionCoverage.All;
        _favorite.IsEnabled = view.Available;
        _favorite.IsChecked = view.Favorite == GalleryDecisionCoverage.Some ? null : all;
        _favoriteContent.Children[0] = FavoriteStar.Create(all, 16);
        int rating = view.RatingMixed ? 0 : view.Rating ?? 0;
        for (int index = 0; index < _stars.Length; index++)
        {
            ToggleButton star = _stars[index];
            star.IsEnabled = view.Available;
            star.IsChecked = view.Available && !view.RatingMixed && rating == index + 1;
            star.Content = FavoriteStar.Create(index < rating, 16);
        }
        _clear.IsEnabled = view.Available && (view.Rating.HasValue || view.RatingMixed);
        _summary.Text = GalleryDecisionText.Summary(view);
        AutomationProperties.SetHelpText(_group, _summary.Text);
        AutomationProperties.SetHelpText(_favorite, GalleryDecisionText.Favorite(view));
    }

    /// <summary>
    /// The execution context the row's keys run in ("gallery", "media", "reader"; null: not named), asked for by the tooltips of the stars: under a partial
    /// yield (a saved binding of another command on Shift+3 in one page) a tip never names a rating key that does nothing where the row is. Set it before
    /// the row is shown, or call <see cref="Localize"/> afterwards.
    /// </summary>
    internal string? KeyContext
    {
        get => _keyContext;
        set { if (string.Equals(_keyContext, value, StringComparison.Ordinal)) return; _keyContext = value; Localize(); }
    }

    /// <summary>Names, tooltips (with the gestures as bound now) and the group name, in the current language.</summary>
    internal void Localize()
    {
        _favoriteLabel.Text = Texts.T("gallery.decision.favorite");
        AutomationProperties.SetName(_favorite, _favoriteLabel.Text);
        string favoriteTip = _favoriteLabel.Text + " · " + Texts.T("gallery.decision.favorite.tip");
        ToolTip.SetTip(_favorite, favoriteTip);
        for (int number = 1; number <= _stars.Length; number++)
        {
            string id = "media.rating.set-" + number;
            string label = CommandCatalog.Label(id);
            AutomationProperties.SetName(_stars[number - 1], label);
            ToolTip.SetTip(_stars[number - 1], CommandCatalog.ShortcutTooltipDisplay(id, _keyContext) is { } gesture ? label + " · " + gesture : label);
        }
        string clear = CommandCatalog.Label("media.rating.clear");
        WorkspaceIcons.MakeSquare(_clear, WorkspaceIcons.Close, clear,
            CommandCatalog.ShortcutTooltipDisplay("media.rating.clear", _keyContext) is { } clearGesture ? clear + " · " + clearGesture : clear);
        AutomationProperties.SetName(_group, MediaRatingText.Label);
        // The stars are one group of toggle buttons that act as a radio group: a screen reader names the group "Rating" with the summary as its help.
        AutomationProperties.SetAccessibilityView(_group, AccessibilityView.Content);
        AutomationProperties.SetControlTypeOverride(_group, AutomationControlType.Group);
        AutomationProperties.SetName(this, Texts.T("gallery.decision.name"));
        Show(_view);
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => { if (_attached) Localize(); });

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e) => _restoreFocus = FieldFocused?.Invoke() == true;

    // A toggle button flips itself on click; the shown state is the view's, so it is set again before the request goes out.
    private void OnFavorite(object? sender, RoutedEventArgs e)
    {
        Show(_view);
        FavoriteRequested?.Invoke(_view.NextFavorite);
        GiveFocusBack();
    }

    private void OnStar(int number)
    {
        Show(_view);
        RatingRequested?.Invoke(number);
        GiveFocusBack();
    }

    private void OnClear(object? sender, RoutedEventArgs e)
    {
        RatingRequested?.Invoke(null);
        GiveFocusBack();
    }

    private void GiveFocusBack()
    {
        if (!_restoreFocus) return;
        _restoreFocus = false;
        Dispatcher.UIThread.Post(() => ReturnFocus?.Invoke(), DispatcherPriority.Input);
    }
}
