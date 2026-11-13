using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
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
        Name = name, HorizontalAlignment = HorizontalAlignment.Left,
        // The expandable post context is docked on the right. Keep the passive
        // persisted-state badge in the opposite, fixed safe area so opening the
        // context can never paint it underneath an opaque panel.
        VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12),
        Padding = new Thickness(7, 3),
        Background = new SolidColorBrush(Color.FromArgb(210, 24, 26, 31)),
        IsHitTestVisible = false, Focusable = false, IsVisible = false, ZIndex = 16,
        Child = new TextBlock { FontSize = 16, Foreground = Brushes.White, IsHitTestVisible = false }
    }.BadgeCorners();

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
            var text = (TextBlock)badge.Child!;
            text.Text = (_favoriteSelected ? "★" : "☆") + (_favoritePending ? " …" : "");
            text.Foreground = _favoriteSelected ? Brushes.Gold : Brushes.White;
            AutomationProperties.SetName(badge, label);
            AutomationProperties.SetName(text, label);
        }
        // Never imply "not saved" while a new selection's persisted status is unknown.
        bool visible = _entry != null && _favoriteKnown && !_terminalRetirement;
        _favoriteIndicator.IsVisible = visible && !_nativeOverlayActive;
        _nativeFavoriteIndicator.IsVisible = visible && _nativeOverlayActive;
        // Row 0 is deliberately outside the native video in transparency fallback mode.
        Grid.SetRow(_favoriteIndicator, 0);
    }

    private bool CanRequestFavorite => _entry != null && !_terminalRetirement
        && !_viewerInteractionStopped && _interactionAvailable && !_navigationPending
        && !_sessionCancellation.IsCancellationRequested;

    private void RequestFavorite(bool selected)
    {
        if (CanRequestFavorite && _entry is { } entry) FavoriteSetRequested?.Invoke(entry, selected);
    }
}
