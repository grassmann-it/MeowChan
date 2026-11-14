using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.WorkViews;
using MeowChan.Gallery;

namespace MeowChan.Desktop;

// V3: the filmstrip at the top of the gallery (thumbnails of the media before and after the shown one). Its band starts below the
// top row of the way back and the star (TopRowClearance), so it never covers either of them.
//   * Overlay mode ("Hide controls after"): the strip comes with the controls and, on its own, when the pointer touches the top edge
//     (a 24 dip zone like the bottom edge's); it hides with the controls and never while the pointer or the keyboard is on it.
//   * Docked mode ("Don't hide controls"): a permanent band above the picture; the picture starts below it.
//   * The work view decides whether it is there by default (Barebones: no, Simple and Advanced: yes); the user's own choice (the
//     setting, the L key, the ⋯ row) wins in every view, see FilmstripPolicyVisible.
internal sealed partial class GalleryViewer
{
    /// <summary>Height of the top edge zone that shows a hidden filmstrip (dip; the same width as the bottom and right edge zones).</summary>
    internal const double FilmstripEdgeBand = 24;
    /// <summary>Below this viewer height the thumbnails never exceed the smallest size, so the picture keeps its room (a design constant).</summary>
    internal const double FilmstripCompactHeight = 560;
    /// <summary>Below this viewer height the filmstrip stays away altogether (a design constant).</summary>
    internal const double FilmstripMinimumHeight = 420;

    private readonly GalleryFilmstrip _filmstrip = new();
    private readonly ToggleButton _filmstripToggle = new() { Name = "GalleryFilmstripToggle" };
    // The old profile setting (GalleryFilmstrip): the View set's filmstrip choice where that has none. The layout choice is the owner (GL4).
    private bool _filmstripSetting = true;
    private int _filmstripHeight = Config.GalleryFilmstripHeightDefault, _filmstripPreload = Config.GalleryFilmstripPreloadDefault;
    private bool _filmstripPeek, _filmstripTopHot, _pointerInFilmstrip, _filmstripOverlayStyled, _filmstripStyleKnown;
    private long _lastFilmstripActivity = Stopwatch.GetTimestamp();

    internal GalleryFilmstrip Filmstrip => _filmstrip;
    /// <summary>Whether the filmstrip is on the screen now (tests and smokes).</summary>
    internal bool IsFilmstripVisible => _filmstrip.IsVisible;
    internal bool IsFilmstripPeeking => _filmstripPeek;
    internal ToggleButton FilmstripToggleButton => _filmstripToggle;

    private void InitializeFilmstrip()
    {
        _filmstrip.ZIndex = 16; // Above the picture and the edge hints, below the way back (17), the post panel (20) and the help (30).
        _filmstrip.HorizontalAlignment = HorizontalAlignment.Stretch;
        _filmstrip.VerticalAlignment = VerticalAlignment.Top;
        _filmstrip.Margin = new Thickness(12, TopRowClearance, 12, 0);
        _filmstrip.IsVisible = false;
        WorkViewElement.SetId(_filmstrip, "gallery.filmstrip"); Mark(_filmstrip);
        WorkViewElement.SetId(_filmstripToggle, "gallery.menu.filmstrip-toggle"); Mark(_filmstripToggle);
        GalleryControlTransfer.Track(_filmstrip, () => !_terminalRetirement);
        _visualRoot.Children.Add(_filmstrip);
        // The menu row sits with the two overlay choices, before "Full screen".
        AddOverflowItem(_filmstripToggle, "gallery.filmstrip-toggle", check: true, keepOpen: true,
            label: () => CommandCatalog.Label("gallery.filmstrip-toggle"));
        _overflowPanel.Children.Remove(_filmstripToggle);
        int anchor = _overflowPanel.Children.IndexOf(_postOverlayToggle);
        _overflowPanel.Children.Insert(anchor >= 0 ? anchor + 1 : _overflowPanel.Children.Count, _filmstripToggle);
        CommandBindings.BindExisting(_filmstripToggle, "gallery.filmstrip-toggle");
        _filmstripToggle.Click += (_, _) => ToggleFilmstrip();
        _filmstrip.TileActivated += OnFilmstripTileActivated;
        _filmstrip.ExitRequested += () => { FocusGallerySurface(); _keyboardInteraction = false; };
        _compactPosition.PropertyChanged += (_, e) =>
        { if (e.Property == TextBlock.TextProperty) RefreshFilmstripPosition(); };
        _contextPanel.SizeChanged += (_, _) => UpdateFilmstripPlacement();
        ActualThemeVariantChanged += (_, _) => { _filmstripStyleKnown = false; UpdateFilmstripPlacement(); };
        RefreshFilmstripToggle();
    }

    /// <summary>Applies the profile settings (called with the other gallery preferences): shown, thumbnail height, thumbnails kept ahead.</summary>
    internal void ConfigureFilmstrip(bool enabled, int height, int preload)
    {
        _filmstripSetting = enabled;
        _filmstripHeight = GalleryFilmstripPlan.ClampHeight(height);
        _filmstripPreload = GalleryFilmstripPlan.ClampPreload(preload);
        UpdateFilmstripPlacement();
    }

    // The strip follows the list the shell owns: the entries (the same list that grows as further threads load) and the shown one.
    private void FilmstripShown(GalleryEntry entry, IReadOnlyList<GalleryEntry> list)
    {
        int index = IndexInList(list, entry);
        // Further threads are still to be resolved when the next arrow is live at the last loaded medium (the shell's own rule).
        bool more = index >= 0 && index == list.Count - 1 && _next.IsEnabled;
        _filmstrip.SetEntries(list, list.Count, more);
        _filmstrip.SetCurrent(index);
        UpdateFilmstripPlacement();
    }

    private static int IndexInList(IReadOnlyList<GalleryEntry> list, GalleryEntry entry)
    {
        for (int i = 0; i < list.Count; i++) if (ReferenceEquals(list[i], entry)) return i;
        GalleryEntryKey key = entry.EntryKey;
        for (int i = 0; i < list.Count; i++) if (list[i].EntryKey == key) return i;
        return -1;
    }

    // QW1: the position stands once, between the arrows; the strip repeats it only while the arrows are away (the controls are hidden
    // and the strip peeks in at the top edge).
    private void RefreshFilmstripPosition() =>
        _filmstrip.SetPositionText(GalleryChromePlan.FilmstripPosition(_compactPosition.Text, OverlayControlsEffective && _overlayHidden));

    // ---- visibility ----

    // The user turned the filmstrip on in a view that leaves it away: it stays (R6, the choice beats the view). Saved in the layout choice (GL4).
    private bool FilmstripForced => EffectiveLayoutChoice(ShownChromeMode).Filmstrip == true
        && !WorkViewPolicy.OnSurface(WorkViewPolicy.Resolve("gallery.filmstrip", _workView));

    private bool FilmstripPolicyVisible =>
        WorkViewPolicy.OnSurface(WorkViewPolicy.Resolve("gallery.filmstrip", _workView, nonDefaultState: FilmstripForced));

    /// <summary>The user-visible choice: setting on and the view (or the user's own choice) shows it. Transient reasons (no media yet, tools hidden) are not part of it.</summary>
    internal bool FilmstripWanted => EffectiveLayoutChoice(ShownChromeMode).Filmstrip != false && FilmstripPolicyVisible;

    private bool FilmstripFits => Bounds.Height <= 0 || Bounds.Height >= FilmstripMinimumHeight;

    // G1/GL0: the chrome plan decides: the view's default, the person's choice (the setting, L, later GL4's layout choice), the state.
    private bool FilmstripEffective => BuildChromePlan().Filmstrip && _filmstrip.Count > 0 && !_terminalRetirement
        && !_viewerInteractionStopped && FilmstripFits;

    private bool FilmstripHolds => _pointerInFilmstrip || _keyboardInteraction && _filmstrip.IsKeyboardFocusWithin;

    private bool FilmstripShownNow => FilmstripEffective && (!OverlayControlsEffective || !_overlayHidden || _filmstripPeek);

    // Places the band, sizes it and shows or hides it. Cheap and idempotent: it runs with every placement, reveal and hide.
    private void UpdateFilmstripPlacement()
    {
        if (_terminalRetirement || _filmstrip == null) return; // (null: a viewer built without its constructor, as the overlay tests do)
        bool overlay = OverlayControlsEffective;
        bool show = FilmstripShownNow;
        Panel host = overlay ? OverlayHost : _visualRoot;
        MoveOverlayControl(_filmstrip, host);
        Grid.SetRow(_filmstrip, _nativeOverlayFallback && !_nativeOverlayActive ? 1 : 0); Grid.SetRowSpan(_filmstrip, 1);
        // The band ends before an open post panel (which starts below the same top row) and starts after the page margin.
        double panelWidth = double.IsFinite(_contextPanel.Width) && _contextPanel.Width > 0 ? _contextPanel.Width : _contextPanel.Bounds.Width;
        bool panelDocked = ReferenceEquals(_contextPanel.Parent, _body) && host == _visualRoot;
        bool panelOver = ReferenceEquals(_contextPanel.Parent, host);
        double right = 12 + (_contextOpen && _contextPanel.IsVisible && panelDocked ? panelWidth - 4 : _contextOpen && _contextPanel.IsVisible && panelOver ? panelWidth + 12 : 0);
        _filmstrip.Margin = new Thickness(12, TopRowClearance, right, 0);
        int tile = Bounds.Height > 0 && Bounds.Height < FilmstripCompactHeight
            ? Math.Min(_filmstripHeight, Config.GalleryFilmstripHeightMinimum) : _filmstripHeight;
        _filmstrip.Configure(tile, _filmstripPreload);
        ApplyFilmstripBackground(overlay);
        WorkViewGate.Apply(_filmstrip, _workView, stateVisible: show, nonDefaultState: FilmstripForced);
        // Docked, the picture starts below the band; the overlay floats over it.
        double inset = show && !overlay ? TopRowClearance + GalleryFilmstripPlan.BandHeight(tile) + 2 : 0;
        if (_viewport.Margin.Top != inset) _viewport.Margin = new Thickness(0, inset, 0, 0);
        _filmstrip.Refresh();
        RefreshFilmstripPosition();
        RefreshFilmstripToggle();
    }

    private void ApplyFilmstripBackground(bool overlay)
    {
        if (_filmstripStyleKnown && _filmstripOverlayStyled == overlay) return;
        _filmstripStyleKnown = true; _filmstripOverlayStyled = overlay;
        if (overlay)
        {
            // The overlay tone of the theme (as the other floating bars) at 85 % so the picture shows faintly through; no colour of its own.
            if (this.TryFindResource("Workspace.Overlay", ActualThemeVariant, out object? resource) && resource is ISolidColorBrush brush)
                _filmstrip.SetBackground(new SolidColorBrush(brush.Color, 0.85));
            else _filmstrip[!Border.BackgroundProperty] = new DynamicResourceExtension("Workspace.Overlay");
        }
        else _filmstrip[!Border.BackgroundProperty] = new DynamicResourceExtension("Workspace.Panel");
    }

    private void RefreshFilmstripToggle()
    {
        // The row's check mark follows this when the menu opens (RefreshOverflowItems).
        _filmstripToggle.IsChecked = FilmstripWanted;
    }

    // ---- pointer: the top edge zone and the strip itself ----

    // Called with the other hover geometry: where is the pointer relative to the top edge and the band, and has a peek run out.
    private void RefreshFilmstripHover()
    {
        bool was = _pointerInFilmstrip || _filmstripTopHot;
        _pointerInFilmstrip = _filmstripTopHot = false;
        if (_lastPointerScreen is PixelPoint pixel && !_viewerInteractionStopped)
        {
            Control surface = _nativeOverlayActive ? _nativeVideoOverlay : _body;
            if (surface.IsAttachedToVisualTree())
            {
                Point point = surface.PointToClient(pixel);
                bool inside = new Rect(surface.Bounds.Size).Contains(point);
                _filmstripTopHot = inside && point.Y < FilmstripEdgeBand && OverlayControlsEffective && FilmstripEffective;
                _pointerInFilmstrip = _filmstrip.IsVisible && _filmstrip.IsAttachedToVisualTree()
                    && new Rect(_filmstrip.Bounds.Size).Contains(_filmstrip.PointToClient(pixel));
            }
        }
        long now = Stopwatch.GetTimestamp();
        if (_pointerInFilmstrip || _filmstripTopHot || was) _lastFilmstripActivity = now;
        bool changed = false;
        if (_filmstripTopHot && !_filmstripPeek && CanPeekFilmstrip) { _filmstripPeek = true; changed = true; }
        else if (_filmstripPeek && !_pointerInFilmstrip && !_filmstripTopHot && !FilmstripHolds
            && Stopwatch.GetElapsedTime(_lastFilmstripActivity) >= TimeSpan.FromMilliseconds(_controlsIdleHideMs)) { _filmstripPeek = false; changed = true; }
        if (changed) { UpdateFilmstripPlacement(); UpdateViewerInteractionTimer(); }
    }

    // The same restraint as the other edge reveals: no drag or press in progress, an active window, no dialog or popup.
    private bool CanPeekFilmstrip => IsGalleryWindowActive && _interactionAvailable && !HasGalleryDialog && !HasGalleryPopup
        && !_dragging && _pressedPointers.Count == 0 && _lastPointer?.Captured == null && !_helpOpen;

    // ---- keyboard and commands ----

    // Called from the gallery's key routing after the check that the key is not typed into a text field.
    private bool HandleFilmstripKey(KeyEventArgs e)
    {
        if (CommandBindings.Matches(e, "gallery.filmstrip-toggle")) { ToggleFilmstrip(); e.Handled = true; return true; }
        if (!_filmstrip.HandleKey(e)) return false;
        e.Handled = true;
        return true;
    }

    /// <summary>Shows or hides the filmstrip (the L key and the ⋯ row); the choice is the shown mode's layout choice, saved per profile and mode (GL4).</summary>
    internal void ToggleFilmstrip()
    {
        if (_terminalRetirement) return;
        bool show = !FilmstripWanted;
        _filmstripPeek = false;
        if (_filmstrip.IsKeyboardFocusWithin && !show) FocusGallerySurface();
        GalleryChromeMode mode = ShownChromeMode;
        ChangeLayoutChoice(mode, LayoutChoice(mode) with { Filmstrip = show });
        // In overlay mode the strip needs the controls up to be seen at all.
        if (show && OverlayControlsEffective) RevealOverlayControls();
        UpdateFilmstripPlacement();
    }

    // A click or Home/End on a tile asks the shell for the same relative move the arrows use, so the list, the preload and the
    // favourite state keep one owner. The "more" tile (one past the last entry) loads the next thread.
    private void OnFilmstripTileActivated(int index)
    {
        if (_terminalRetirement || _viewerInteractionStopped || !_interactionAvailable || _filmstrip.CurrentIndex < 0) return;
        int delta = index - _filmstrip.CurrentIndex;
        if (delta == 0) { FocusGallerySurface(); return; }
        if (!(delta < 0 ? _previous.IsEnabled : _next.IsEnabled)) return;
        _navigationPending = true; // No old-media timer while the shell resolves the entry.
        MoveRequested?.Invoke(delta);
    }

    private void RetireFilmstrip()
    {
        GalleryControlTransfer.Cancel(_filmstrip);
        _filmstripPeek = false;
        _filmstrip.IsVisible = false;
        _filmstrip.Retire();
    }
}
