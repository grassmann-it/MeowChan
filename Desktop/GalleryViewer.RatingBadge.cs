using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Styling;
using MeowChan.Desktop.Controls;

namespace MeowChan.Desktop;

internal sealed partial class GalleryViewer
{
    // Tom, 25.11.2026: beside the way back and the favourite heart, the shown medium's rating as five stars. The same row as the Post segment's
    // (GalleryDecisionRow in its sidebar form: the stars never take the focus, no favourite and no remove button of its own), on the dark scrim of
    // the heart. One visual per native root, like the heart (never a control moved between layout managers).
    private Border? _ratingIndicator;
    private Border? _nativeRatingIndicator;
    private GalleryDecisionRow? _ratingIndicatorRow;
    private GalleryDecisionRow? _nativeRatingIndicatorRow;

    /// <summary>The rating badge beside the heart in the managed root (tests read it by its name "GalleryRatingIndicator").</summary>
    internal Border? RatingIndicator => _ratingIndicator;
    /// <summary>The five stars of the managed root's rating badge.</summary>
    internal GalleryDecisionRow? RatingIndicatorRow => _ratingIndicatorRow;
    /// <summary>The rating badge of the native video overlay.</summary>
    internal Border? NativeRatingIndicator => _nativeRatingIndicator;

    /// <summary>Inner padding of the rating badge, in dip (DesignLint: a multiple of 4).</summary>
    internal const double RatingBadgePadding = 4;

    /// <summary>The badge's width: five star cells and its padding (the row's cells are <see cref="WorkspaceMetrics.RatingStarMinWidth"/> wide).</summary>
    internal const double RatingBadgeWidth = 5 * WorkspaceMetrics.RatingStarMinWidth + 2 * RatingBadgePadding;

    internal static Border CreateRatingIndicator(string name, out GalleryDecisionRow row)
    {
        row = new GalleryDecisionRow(name + "Row", compact: true, favorite: false, sidebar: true) { KeyContext = "gallery" };
        row.ClearButton.IsVisible = false;
        row.ClearGap = 0;
        row.VerticalAlignment = VerticalAlignment.Center;
        Border badge = new()
        {
            Name = name, Classes = { "gallery-rating-badge" },
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Padding = new Thickness(RatingBadgePadding, 0), Margin = new Thickness(12), ZIndex = 16, IsVisible = false, Focusable = false,
            // The stars' own theme resources (muted outline, text-coloured hover, the favourite tint) read on the dark scrim in either theme: the
            // badge is a dark surface like the heart's, so its content resolves the Dark dictionary.
            Child = new ThemeVariantScope { RequestedThemeVariant = ThemeVariant.Dark, Child = row }
        };
        badge[!Border.BackgroundProperty] = new DynamicResourceExtension("Workspace.MediaScrim");
        return badge.ControlSized();
    }

    private void InitializeRatingIndicators()
    {
        _ratingIndicator = CreateRatingIndicator("GalleryRatingIndicator", out GalleryDecisionRow row);
        _nativeRatingIndicator = CreateRatingIndicator("GalleryNativeRatingIndicator", out GalleryDecisionRow native);
        _ratingIndicatorRow = row;
        _nativeRatingIndicatorRow = native;
        _visualRoot.Children.Add(_ratingIndicator);
        _nativeVideoOverlay.Children.Add(_nativeRatingIndicator);
        foreach ((Border badge, GalleryDecisionRow stars) in new[] { (_ratingIndicator, row), (_nativeRatingIndicator, native) })
        {
            stars.RatingRequested += OnRatingBadgeRequested;
            // A click never leaves the focus on the badge (its stars cannot take it); the keys go back to the picture as after any chrome click.
            stars.FieldFocused = () => true;
            stars.ReturnFocus = FocusGallerySurface;
            badge.PointerEntered += (_, _) => UpdateCornerChrome();
            badge.PointerExited += (_, _) => UpdateCornerChrome();
        }
        _favoriteIndicator.SizeChanged += (_, _) => UpdateRatingIndicators();
        UpdateRatingIndicators();
    }

    // A star of the badge: the rating of exactly the shown medium (never the marked media of the tag panel), the write the rating keys make
    // without the panel. The row has already turned a click on the set star into "remove", as every star row does.
    private void OnRatingBadgeRequested(int? value)
    {
        if (_entry is not { } entry || _ratingPending || !_ratingAvailable || !CanRequestFavorite) { UpdateRatingIndicators(); return; }
        FlashFavoriteBadge();
        RatingSetRequested?.Invoke(entry, value);
    }

    private bool RatingBadgeKnown => _entry != null && !_terminalRetirement && _favoriteKnown && (_ratingAvailable || _ratingPending);

    // The badge stands right of the heart while the whole top row (way back, heart, stars) fits in the share of the width the labelled way back
    // may take (GalleryBackLabel.MaximumShare); a narrower viewer shows the heart alone and keeps the label of the way back.
    private double RatingBadgeLeft => StarLeftMargin + (_favoriteIndicator.Bounds.Width > 0 ? _favoriteIndicator.Bounds.Width : GalleryBackLabel.StarWidth) + StarGap;

    private bool RatingBadgeFits => Bounds.Width <= 0 || RatingBadgeLeft + RatingBadgeWidth + GalleryBackLabel.EdgeMargin <= Bounds.Width * GalleryBackLabel.MaximumShare;

    private void UpdateRatingIndicators()
    {
        if (_ratingIndicator == null || _nativeRatingIndicator == null) return;
        GalleryDecisionView view = PostDecisionView();
        bool visible = _favoriteIndicator.IsVisible || _nativeFavoriteIndicator.IsVisible;
        visible = visible && RatingBadgeKnown && RatingBadgeFits;
        Thickness beside = new(RatingBadgeLeft, 12, 12, 12);
        foreach ((Border badge, GalleryDecisionRow? row, bool native) in new[] { (_ratingIndicator, _ratingIndicatorRow, false), (_nativeRatingIndicator, _nativeRatingIndicatorRow, true) })
        {
            row?.Show(view);
            badge.Margin = beside;
            badge.IsVisible = visible && native == _nativeOverlayActive;
        }
        Grid.SetRow(_ratingIndicator, 0);
    }

    /// <summary>The shown rating badge follows the heart: shown, hidden and hit-testable with it; never focusable.</summary>
    private void ApplyRatingCornerChrome(GalleryCornerChrome chrome)
    {
        foreach (Border? badge in new[] { _ratingIndicator, _nativeRatingIndicator })
        {
            if (badge == null) continue;
            badge.Opacity = chrome.Opacity;
            badge.IsHitTestVisible = chrome.HitTestVisible;
        }
    }

    private bool RatingBadgePointerOver => _ratingIndicator?.IsPointerOver == true || _nativeRatingIndicator?.IsPointerOver == true;

    private double RatingBadgeShownWidth => _ratingIndicator?.IsVisible == true || _nativeRatingIndicator?.IsVisible == true
        ? StarGap + (_ratingIndicator!.Bounds.Width > 0 ? _ratingIndicator.Bounds.Width : RatingBadgeWidth) : 0;
}
