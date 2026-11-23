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
using Avalonia.Media;
using Avalonia.Threading;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Controls;

internal sealed class GalleryDecisionRow : StackPanel
{
    private readonly ToggleButton _favorite = new() { Classes = { "rating-favorite" }, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ToggleButton[] _stars = new ToggleButton[5];
    private readonly Button _clear = new();
    /// <summary>Edge of a star glyph in dip.</summary>
    private const int FavoriteGlyphSize = 16;
    private readonly int _starGlyphSize;
    private readonly bool _popover;
    private readonly StackPanel _bar = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _unavailable = new() { Classes = { "workspace-meta" }, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _error = new() { Classes = { "workspace-error" }, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    // The second row shares the first row's star and clear-button columns.
    private readonly Grid _group = new()
    {
        ColumnDefinitions = new ColumnDefinitions("Auto,8,*"),
        RowDefinitions = new RowDefinitions("32,8,32"),
        HorizontalAlignment = HorizontalAlignment.Left
    };
    private readonly RatingCaption _summary = new()
    {
        Classes = { "workspace-meta", "tnum" }, LineHeight = 16, MaxLines = 2, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly TextBlock _favoriteLabel = new() { Classes = { "panel-action-label" }, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid _favoriteContent;
    private bool? _favoriteAvailable;
    private GalleryDecisionView _view = GalleryDecisionView.Unavailable;
    // The star the pointer is on (1 to 5): the stars up to it show as set. Never a state of the row, never a request.
    private int? _preview;
    private bool _restoreFocus;
    private bool _attached;
    private string? _keyContext;

    internal GalleryDecisionRow(string name, bool compact = false)
    {
        Name = name;
        _starGlyphSize = compact ? 20 : 24;
        _popover = !compact;
        Spacing = 4;
        _group.Children.Add(_bar);
        _group.ColumnDefinitions[2].MinWidth = WorkspaceMetrics.InputHeight;
        _favorite.Name = name + "Favorite"; _clear.Name = name + "RatingClear"; _group.Name = name + "Rating"; _summary.Name = name + "Summary";
        _favorite.IsThreeState = true;
        _favoriteContent = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8,
            Children = { FavoriteStar.Create(false, FavoriteGlyphSize), _favoriteLabel } };
        Grid.SetColumn(_favoriteLabel, 1);
        _favorite.Content = _favoriteContent;
        for (int index = 0; index < _stars.Length; index++)
        {
            int number = index + 1;
            ToggleButton star = _stars[index] = new ToggleButton { Name = name + "Rating" + number, Classes = { "rating-star" } };
            star.Click += (_, _) => OnStar(number);
            TransientHover.Attach(star, over => { if (over) Preview(number); else if (_preview == number) Preview(null); });
            _bar.Children.Add(star);
        }
        _clear.Width = _clear.Height = WorkspaceMetrics.InputHeight;
        _clear.Classes.Add(WorkspaceIcons.SquareClass);
        _clear.Classes.Add("media-control");
        _clear.Classes.Add("rating-clear");
        _clear.Click += OnClear;
        _clear.VerticalAlignment = VerticalAlignment.Center;
        _clear.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumn(_clear, 2);
        Grid.SetRow(_favorite, 2);
        Grid.SetRow(_summary, 2);
        Grid.SetColumn(_summary, 2);
        _group.Children.Add(_clear);
        _group.Children.Add(_favorite);
        _group.Children.Add(_summary);
        Children.Add(_group);
        Children.Add(_unavailable);
        Children.Add(_error);
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Assertive);
        _favorite.Click += OnFavorite;
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, (_, _) => _restoreFocus = false, RoutingStrategies.Bubble, handledEventsToo: true);
        AttachedToVisualTree += (_, _) => { if (_attached) return; _attached = true; Texts.Changed += OnLanguageChanged; Localize(); };
        DetachedFromVisualTree += (_, _) => { _preview = null; if (!_attached) return; _attached = false; Texts.Changed -= OnLanguageChanged; };
        Localize();
        Show(GalleryDecisionView.Unavailable);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = !_popover ? WorkspaceMetrics.RatingStarMinWidth
            : availableSize.Width < 336 ? WorkspaceMetrics.InputHeight : WorkspaceMetrics.RatingPopoverStarSize;
        double height = Math.Max(WorkspaceMetrics.InputHeight, width);
        _favorite.MaxWidth = width * _stars.Length;
        _group.ColumnDefinitions[0].MinWidth = _favorite.MaxWidth;
        // A narrow 240-DIP panel may wrap single ratings. At the normal minimum, only mixed ratings wrap.
        _summary.SetLineLimit(_view.RatingMixed || availableSize.Width < (_popover ? 256 : 246) ? 2 : 1);
        _group.RowDefinitions[0].Height = new GridLength(height);
        _bar.Margin = new Thickness((_starGlyphSize - width) / 2, 0, 0, 0);
        foreach (ToggleButton star in _stars)
        {
            star.Width = width;
            star.Height = height;
        }
        return base.MeasureOverride(availableSize);
    }

    internal ToggleButton FavoriteButton => _favorite;
    /// <summary>False hides the favourite button (a popover whose host has no favourites call for its targets); the stars stay.</summary>
    internal bool FavoriteVisible { get => _favorite.IsVisible; set => _favorite.IsVisible = value; }
    internal IReadOnlyList<ToggleButton> StarButtons => _stars;
    internal Button ClearButton => _clear;
    internal RatingCaption Summary => _summary;
    internal GalleryDecisionView View => _view;
    internal Control RatingGroup => _group;

    internal void SetError(string? message)
    {
        _error.Text = message;
        _error.IsVisible = !string.IsNullOrEmpty(message);
    }

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
        Show(_view, _favoriteAvailable);
        RatingRequested?.Invoke(value);
        return true;
    }

    /// <summary>
    /// A favourite key (M adds, U removes, F toggles): the request the button makes, as an explicit flag. A key that changes nothing (add on a
    /// favourite) is the row's and does nothing; false when the row has no favourite call or cannot act.
    /// </summary>
    internal bool RequestFavorite(FavoriteKeyAction action)
    {
        if (!_favorite.IsEnabled || !FavoriteVisible || action == FavoriteKeyAction.None) return false;
        bool wanted = action switch { FavoriteKeyAction.Add => true, FavoriteKeyAction.Remove => false, _ => _view.NextFavorite };
        bool all = _view.Favorite == GalleryDecisionCoverage.All, none = _view.Favorite == GalleryDecisionCoverage.None;
        if (action == FavoriteKeyAction.Add && all || action == FavoriteKeyAction.Remove && none) return true;
        Show(_view, _favoriteAvailable);
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
    internal void Show(GalleryDecisionView view, bool? favoriteAvailable = null)
    {
        _favoriteAvailable = favoriteAvailable;
        _view = view ?? throw new ArgumentNullException(nameof(view));
        bool all = view.Favorite == GalleryDecisionCoverage.All;
        _favorite.IsEnabled = favoriteAvailable ?? view.Available;
        _favorite.IsChecked = view.Favorite == GalleryDecisionCoverage.Some ? null : all;
        _favoriteContent.Children[0] = FavoriteStar.Create(all, FavoriteGlyphSize, mixed: view.Favorite == GalleryDecisionCoverage.Some);
        _favoriteLabel.Text = Texts.T("gallery.decision.favorite");
        ToolTip.SetTip(_favorite, view.Favorite == GalleryDecisionCoverage.Some ? GalleryDecisionText.Favorite(view)
            : _favoriteLabel.Text + " · " + Texts.T("gallery.decision.favorite.tip"));
        int rating = view.RatingMixed ? 0 : view.Rating ?? 0;
        // Roving tab stop: the stars are one stop of the Tab order (the checked one, star 1 while unrated); the others and the remove button are
        // reached with Left, Right, Home and End. A star that has the focus keeps it when the stop moves.
        int stopIndex = view.Available && !view.RatingMixed && rating > 0 ? rating - 1 : 0;
        for (int index = 0; index < _stars.Length; index++)
        {
            ToggleButton star = _stars[index];
            star.IsEnabled = view.Available;
            star.IsChecked = view.Available && !view.RatingMixed && rating == index + 1;
            star.IsTabStop = index == stopIndex;
        }
        _clear.IsTabStop = false;
        _clear.IsEnabled = view.Available && (view.Rating.HasValue || view.RatingMixed);
        Paint();
        _summary.IsVisible = view.Available;
        _summary.Text = view.Available ? GalleryDecisionText.Caption(view) : null;
        _unavailable.IsVisible = !view.Available;
        _unavailable.Text = Texts.T("gallery.decision.status.unavailable");
        // The group's help is the full sentence (rating and favourite); the caption below the clear button shows the rating alone.
        AutomationProperties.SetHelpText(_group, ShortcutHelp.Append(GalleryDecisionText.Summary(view),
            ShortcutHelp.Commands(_keyContext ?? "gallery", Enumerable.Range(1, 5).Select(number => "media.rating.set-" + number)
                .Append("media.rating.clear").ToArray())));
        AutomationProperties.SetHelpText(_favorite, GalleryDecisionText.Favorite(view));
    }

    /// <summary>
    /// The star the pointer is on (1 to 5), or null when it left: the stars up to it show as set, in the text colour, the rest as outlines. Only the
    /// picture changes; <see cref="StarButtons"/> keep their checked state and no <see cref="RatingRequested"/> is raised. Ignored while the row cannot rate.
    /// </summary>
    internal void Preview(int? number)
    {
        if (number is < 1 or > 5 || !_view.Available) number = null;
        if (_preview == number) return;
        _preview = number;
        Paint();
    }

    /// <summary>The star the pointer previews now (null: none); what <see cref="Paint"/> shows instead of the rating.</summary>
    internal int? PreviewedStar => _preview;

    // The glyphs: the stars up to the rating (or up to the previewed star) filled. A rated star is the favourite colour; a previewed one follows the
    // text colour (tinted: false) so a preview is never taken for the saved rating.
    private void Paint()
    {
        int rating = _view.RatingMixed ? 0 : _view.Rating ?? 0;
        bool previewing = _preview.HasValue;
        int shown = previewing ? _preview!.Value : rating;
        for (int index = 0; index < _stars.Length; index++)
        {
            ToggleButton star = _stars[index];
            star.Content = FavoriteStar.Create(index < shown, _starGlyphSize, tinted: !previewing);
            star.Classes.Set("rating-preview", previewing && index < shown);
        }
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
        string favorite = Texts.T("gallery.decision.favorite");
        AutomationProperties.SetName(_favorite, favorite);
        for (int number = 1; number <= _stars.Length; number++)
        {
            string id = "media.rating.set-" + number;
            string label = CommandCatalog.Label(id);
            AutomationProperties.SetName(_stars[number - 1], label);
            string tip = CommandCatalog.ShortcutTooltipDisplay(id, _keyContext) is { } gesture ? label + " · " + gesture : label;
            ToolTip.SetTip(_stars[number - 1], tip);
            AutomationProperties.SetHelpText(_stars[number - 1], tip);
        }
        string clear = CommandCatalog.Label("media.rating.clear");
        WorkspaceIcons.MakeSquare(_clear, WorkspaceIcons.StarClear, clear,
            CommandCatalog.ShortcutTooltipDisplay("media.rating.clear", _keyContext) is { } clearGesture ? clear + " · " + clearGesture : clear, subtle: false);
        AutomationProperties.SetName(_group, MediaRatingText.Label);
        // The stars are one group of toggle buttons that act as a radio group: a screen reader names the group "Rating" with the summary as its help.
        AutomationProperties.SetAccessibilityView(_group, AccessibilityView.Content);
        AutomationProperties.SetControlTypeOverride(_group, AutomationControlType.Group);
        AutomationProperties.SetName(this, Texts.T("gallery.decision.name"));
        Show(_view, _favoriteAvailable);
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => { if (_attached) Localize(); });

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e) => _restoreFocus = FieldFocused?.Invoke() == true;

    // A toggle button flips itself on click; the shown state is the view's, so it is set again before the request goes out.
    private void OnFavorite(object? sender, RoutedEventArgs e)
    {
        Show(_view, _favoriteAvailable);
        FavoriteRequested?.Invoke(_view.NextFavorite);
        GiveFocusBack();
    }

    private void OnStar(int number)
    {
        Show(_view, _favoriteAvailable);
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
