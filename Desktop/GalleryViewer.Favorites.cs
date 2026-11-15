using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Primitives;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan.Desktop;

internal sealed partial class GalleryViewer
{
    // Separate visuals per native root: never transfer a badge between layout managers.
    private readonly ToggleButton _favoriteIndicator = CreateFavoriteIndicator("GalleryFavoriteIndicator");
    private readonly ToggleButton _nativeFavoriteIndicator = CreateFavoriteIndicator("GalleryNativeFavoriteIndicator");
    private bool _favoriteKnown;
    private bool _favoritePending;
    internal event Action<GalleryEntry, bool>? FavoriteSetRequested;

    /// <summary>
    /// The favourite badge beside the way back (Tom's decision of 15.11.): a toggle on the picture that shows the persisted state and, clicked,
    /// toggles the favourite like the toolbar star. Its look (dark scrim, hover, pressed and focus-visible edge) is the <c>gallery-favorite-badge</c>
    /// style of WorkspaceStyles.axaml.
    /// </summary>
    internal static ToggleButton CreateFavoriteIndicator(string name) => new ToggleButton
    {
        Name = name, HorizontalAlignment = HorizontalAlignment.Left,
        // The top left edge belongs to the way back; the badge sits directly right of it as a pair (UpdateFavoriteIndicators), so neither
        // is painted over by the post panel, which starts below this row where it reaches it.
        VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12),
        // As high as the way back beside it (Workspace.Size.Control, see ControlSized) and a square for the star alone; the content is centred.
        Classes = { "gallery-favorite-badge" },
        HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
        Focusable = true, IsVisible = false, ZIndex = 16,
        // A drawn star (outline: not saved, filled gold: saved) and, while a write is pending, a trailing "…" (P6: no font glyphs).
        Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Children = { WorkspaceIcons.Create(WorkspaceIcons.Star, inheritForeground: false), new TextBlock { Name = "GalleryFavoritePending", Text = "…",
                Foreground = Brushes.White, IsVisible = false, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false }.SubtitleText() } }
    }.ControlSized();

    /// <summary>The state a favourite badge shows: saved (filled star) and pending (trailing "…"). Tests and the refresh read it back.</summary>
    internal static (bool Saved, bool Pending) FavoriteBadgeState(ToggleButton badge)
    {
        StackPanel panel = (StackPanel)badge.Content!;
        return (WorkspaceIcons.KeyOf(panel.Children[0]) == WorkspaceIcons.StarFilled, panel.Children[1].IsVisible);
    }

    // The toggle's checked state is the saved state (a screen reader reads "Favorite, on / off"); the name adds " · saving" while a write is
    // pending. The enabled state stays on while pending so the focus is not lost under a keyboard user; the click is ignored then.
    private static void ShowFavoriteBadgeState(ToggleButton badge, bool saved, bool pending, string label, string tip)
    {
        StackPanel panel = (StackPanel)badge.Content!;
        Avalonia.Controls.Shapes.Path star = (Avalonia.Controls.Shapes.Path)panel.Children[0];
        star.Tag = saved ? WorkspaceIcons.StarFilled : WorkspaceIcons.Star;
        IBrush ink = saved ? Brushes.Gold : Brushes.White;
        star.Stroke = ink;
        star.Fill = saved ? ink : Brushes.Transparent;
        panel.Children[1].IsVisible = pending;
        badge.IsChecked = saved;
        AutomationProperties.SetName(badge, label);
        AutomationProperties.SetHelpText(badge, tip);
        ToolTip.SetTip(badge, tip);
    }

    private void InitializeFavoriteIndicators()
    {
        _visualRoot.Children.Add(_favoriteIndicator);
        _nativeVideoOverlay.Children.Add(_nativeFavoriteIndicator);
        foreach (ToggleButton badge in new[] { _favoriteIndicator, _nativeFavoriteIndicator })
        {
            ToggleButton clicked = badge;
            badge.Click += (_, _) => OnFavoriteBadgeClick(clicked);
            // The badge hides with the way back and returns with it under the pointer or the focus, or alone after a change (UpdateCornerChrome).
            badge.PointerEntered += (_, _) => UpdateCornerChrome();
            badge.PointerExited += (_, _) => UpdateCornerChrome();
            badge.GotFocus += (_, _) => UpdateCornerChrome();
            badge.LostFocus += (_, _) => UpdateCornerChrome();
        }
        UpdateFavoriteIndicators();
    }

    private void UpdateFavoriteIndicators()
    {
        // The name is the thing ("Favorite"), the toggle state says saved or not; a pending write adds " · saving".
        string label = Texts.T("gallery.decision.favorite");
        if (_favoritePending) label += " · " + Texts.T("gallery.saving");
        // The keys of every favourite star (F toggles, M adds, U removes: FavoriteKeys.Tooltip).
        string tip = FavoriteKeys.Tooltip(_favoriteSelected);
        foreach (ToggleButton badge in new[] { _favoriteIndicator, _nativeFavoriteIndicator })
        {
            ShowFavoriteBadgeState(badge, _favoriteSelected, _favoritePending, label, tip);
        }
        // Never imply "not saved" while a new selection's persisted status is unknown.
        bool visible = _entry != null && _favoriteKnown && !_terminalRetirement;
        _favoriteIndicator.IsVisible = visible && !_nativeOverlayActive;
        _nativeFavoriteIndicator.IsVisible = visible && _nativeOverlayActive;
        // Tom's rule of 14.11.: the way back stands first at the top left edge and the star sits directly right of it. The post
        // panel starts below this row where it reaches it (PostPanelTopInset) and the help below it (TopRowClearance), so nothing
        // covers either of them.
        Thickness beside = new(StarLeftMargin, 12, 12, 12);
        _favoriteIndicator.Margin = beside;
        _nativeFavoriteIndicator.Margin = beside;
        // Row 0 is deliberately outside the native video in transparency fallback mode.
        Grid.SetRow(_favoriteIndicator, 0);
        PlaceBadgeAfterBack(_favoriteIndicator);
        PlaceBadgeAfterBack(_nativeFavoriteIndicator);
    }

    // Tab order: the badge comes right after "Back to …". The way back is appended to its host whenever the overlay placement runs, so the
    // badge is moved behind it in place (Move keeps the focus; a removal would drop it).
    private void PlaceBadgeAfterBack(ToggleButton badge)
    {
        if (_close?.Parent is not Panel host || !ReferenceEquals(host, badge.Parent)) return;
        int at = host.Children.IndexOf(badge), back = host.Children.IndexOf(_close);
        if (at < 0 || back < 0 || at == back + 1) return;
        host.Children.Move(at, at < back ? back : back + 1);
    }

    // A click on the badge toggles like the toolbar star (the keyboard's F keeps its confirmation of a removal). The shown state is the viewer's,
    // so it is set again before the request goes out; a pending write ignores the click and keeps the focus where it is.
    private void OnFavoriteBadgeClick(ToggleButton badge)
    {
        badge.IsChecked = _favoriteSelected;
        if (_favoritePending) return;
        ToggleFavorite();
    }

    /// <summary>Gap between the star and the way back, in dip.</summary>
    internal const double StarGap = 8;

    // The way back is as wide as its label; before its first layout a typical arrow-and-label button stands in.
    private double StarLeftMargin => 12 + (_close != null && _close.Bounds.Width > 0 ? _close.Bounds.Width : 44) + StarGap;

    private bool CanRequestFavorite => _entry != null && !_terminalRetirement
        && !_viewerInteractionStopped && _interactionAvailable && !_navigationPending
        && !_sessionCancellation.IsCancellationRequested;

    private void RequestFavorite(bool selected)
    {
        if (!CanRequestFavorite || _entry is not { } entry) return;
        FlashFavoriteBadge(); // M and U show the badge at once, also when the key changes nothing
        FavoriteSetRequested?.Invoke(entry, selected);
    }
}
