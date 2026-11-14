using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan.Desktop;

internal sealed partial class GalleryViewer
{
    // Separate visuals per native root: never transfer a badge between layout managers.
    private readonly Border _favoriteIndicator = CreateFavoriteIndicator("GalleryFavoriteIndicator");
    private readonly Border _nativeFavoriteIndicator = CreateFavoriteIndicator("GalleryNativeFavoriteIndicator");
    private bool _favoriteKnown;
    private bool _favoritePending;
    internal event Action<GalleryEntry, bool>? FavoriteSetRequested;

    internal static Border CreateFavoriteIndicator(string name) => new Border
    {
        Name = name, HorizontalAlignment = HorizontalAlignment.Right,
        // The top right edge belongs to the way back; the passive persisted-state badge sits directly left of it
        // (UpdateFavoriteIndicators), so neither is painted over by the post panel, which starts below this row.
        VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12),
        Padding = new Thickness(7, 3),
        Background = new SolidColorBrush(Color.FromArgb(210, 24, 26, 31)),
        IsHitTestVisible = false, Focusable = false, IsVisible = false, ZIndex = 16,
        // A drawn star (outline: not saved, filled gold: saved) and, while a write is pending, a trailing "…" (P6: no font glyphs).
        Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, IsHitTestVisible = false,
            Children = { WorkspaceIcons.Create(WorkspaceIcons.Star, inheritForeground: false), new TextBlock { Name = "GalleryFavoritePending", Text = "…",
                Foreground = Brushes.White, IsVisible = false, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false }.SubtitleText(),
                // A Border, a StackPanel and a Path have no automation peer: the saved / saving state is also a hidden text (content view), so a screen reader reads it.
                new TextBlock { Name = "GalleryFavoriteStateText", Classes = { "visually-hidden" } } } }
    }.BadgeCorners();

    /// <summary>The state a favourite badge shows: saved (filled star) and pending (trailing "…"). Tests and the refresh read it back.</summary>
    internal static (bool Saved, bool Pending) FavoriteBadgeState(Border badge)
    {
        StackPanel panel = (StackPanel)badge.Child!;
        return (WorkspaceIcons.KeyOf(panel.Children[0]) == WorkspaceIcons.StarFilled, panel.Children[1].IsVisible);
    }

    private static void ShowFavoriteBadgeState(Border badge, bool saved, bool pending, string label)
    {
        StackPanel panel = (StackPanel)badge.Child!;
        Avalonia.Controls.Shapes.Path star = (Avalonia.Controls.Shapes.Path)panel.Children[0];
        star.Tag = saved ? WorkspaceIcons.StarFilled : WorkspaceIcons.Star;
        IBrush ink = saved ? Brushes.Gold : Brushes.White;
        star.Stroke = ink;
        star.Fill = saved ? ink : Brushes.Transparent;
        panel.Children[1].IsVisible = pending;
        AutomationProperties.SetName(badge, label);
        AutomationProperties.SetName(panel, label);
        if (panel.Children.Count > 2 && panel.Children[2] is TextBlock stateText) { stateText.Text = label; AutomationProperties.SetAccessibilityView(stateText, AccessibilityView.Content); }
    }

    private void InitializeFavoriteIndicators()
    {
        _visualRoot.Children.Add(_favoriteIndicator);
        _nativeVideoOverlay.Children.Add(_nativeFavoriteIndicator);
        UpdateFavoriteIndicators();
    }

    private void UpdateFavoriteIndicators()
    {
        string state = Texts.T(_favoriteSelected ? "viewer.favorite.saved" : "viewer.favorite.unsaved");
        string label = _favoritePending
            ? state + " · " + Texts.T("gallery.saving") : state;
        foreach (Border badge in new[] { _favoriteIndicator, _nativeFavoriteIndicator })
        {
            ShowFavoriteBadgeState(badge, _favoriteSelected, _favoritePending, label);
        }
        // Never imply "not saved" while a new selection's persisted status is unknown.
        bool visible = _entry != null && _favoriteKnown && !_terminalRetirement;
        _favoriteIndicator.IsVisible = visible && !_nativeOverlayActive;
        _nativeFavoriteIndicator.IsVisible = visible && _nativeOverlayActive;
        // Tom's gallery rule: the way back stands alone at the top right edge and the star sits directly left of it. The post
        // panel and the help start below this row (TopRowClearance), so nothing covers either of them.
        Thickness beside = new(12, 12, StarRightMargin, 12);
        _favoriteIndicator.Margin = beside;
        _nativeFavoriteIndicator.Margin = beside;
        // Row 0 is deliberately outside the native video in transparency fallback mode.
        Grid.SetRow(_favoriteIndicator, 0);
    }

    /// <summary>Gap between the star and the way back, in dip.</summary>
    internal const double StarGap = 8;

    // The way back is as wide as its label; before its first layout a typical arrow-and-label button stands in.
    private double StarRightMargin => 12 + (_close != null && _close.Bounds.Width > 0 ? _close.Bounds.Width : 44) + StarGap;

    private bool CanRequestFavorite => _entry != null && !_terminalRetirement
        && !_viewerInteractionStopped && _interactionAvailable && !_navigationPending
        && !_sessionCancellation.IsCancellationRequested;

    private void RequestFavorite(bool selected)
    {
        if (CanRequestFavorite && _entry is { } entry) FavoriteSetRequested?.Invoke(entry, selected);
    }
}
