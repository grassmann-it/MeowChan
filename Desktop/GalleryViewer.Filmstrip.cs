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

// V3, GL1: the filmstrip of the gallery (thumbnails of the media before and after the shown one). Where it sits is the plan's
// FilmstripDock (Tom, GL E1: Bottom by default, Top stays selectable); GalleryFilmstripPlan.Place turns it into a host and margins.
//   * Bottom: the first child of the dock (docked) or of the floating footer (overlay), above the video bar and the tools. It comes and
//     goes with the controls: no peek, no top edge zone. It lies below the way back, the star and the side panel, so it covers none.
//   * Top: below the top row of the way back and the star (TopRowClearance), so it never covers either of them.
//     - Overlay mode ("Hide controls after"): the strip comes with the controls and, on its own, when the pointer touches the top edge
//       (a 24 dip zone like the bottom edge's); it hides with the controls and never while the pointer or the keyboard is on it.
//     - Docked mode ("Don't hide controls"): a permanent band above the picture; the picture starts below it.
//   * The work view decides whether it is there by default (Barebones: no, Simple and Advanced: yes); the user's own choice (the
//     setting, the filmstrip key, the ⋯ row) wins in every view, see FilmstripPolicyVisible.
//   * The tab order follows the picture: the bottom strip is the first stop of the dock, before the toolbar.
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
    private int _filmstripHeight = Config.GalleryFilmstripHeightDefault, _filmstripPreload = Config.GalleryFilmstripPreloadDefault, _filmstripGap = Config.GalleryFilmstripTileGapDefault;
    private bool _filmstripPeek, _filmstripTopHot, _pointerInFilmstrip, _filmstripOverlayStyled, _filmstripStyleKnown;
    // The band is shown as the first row of the docked dock (GL1 bottom, docked): the dock's top margin is the band's (GL2, ApplyDockMargin).
    private bool _filmstripHeadsDock;
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
        MeowChan.Desktop.Theming.ThemeRuntime.FollowApplied(this, () => { _filmstripStyleKnown = false; UpdateFilmstripPlacement(); });
        RefreshFilmstripToggle();
    }

    /// <summary>Applies the profile settings (called with the other gallery preferences): shown, thumbnail height, thumbnails kept ahead.</summary>
    internal void ConfigureFilmstrip(bool enabled, int height, int preload, int gap = Config.GalleryFilmstripTileGapDefault)
    {
        _filmstripSetting = enabled;
        _filmstripHeight = GalleryFilmstripPlan.ClampHeight(height);
        _filmstripPreload = GalleryFilmstripPlan.ClampPreload(preload);
        _filmstripGap = GalleryFilmstripPlan.ClampGap(gap);
        UpdateFilmstripPlacement();
    }

    /// <summary>
    /// Filmstrip flags (decision 123): the choices of Settings, "Filmstrip shows" (favourite, rating, no tags yet). Called with the other gallery
    /// preferences; the tiles in view draw again only when what is shown changed.
    /// </summary>
    internal void ConfigureFilmstripFlags(FilmstripFlagSettings settings) => _filmstrip.ConfigureFlags(settings);

    /// <summary>Gives the strip the owner's view of favourites, ratings and tags (data that is already loaded; asked only for the flags that are shown).</summary>
    internal void SetFilmstripFlagSource(Func<GalleryEntry, FilmstripFlagKinds, FilmstripFlagFacts>? source) => _filmstrip.FlagSource = source;

    /// <summary>A favourite, rating or tag changed: the tiles in view read their flags again.</summary>
    internal void RefreshFilmstripFlags() => _filmstrip.RefreshFlags();

    // The strip follows the list the shell owns: the entries (the same list that grows as further threads load) and the shown one.
    private void FilmstripShown(GalleryEntry entry, IReadOnlyList<GalleryEntry> list)
    {
        int index = IndexInList(list, entry);
        // Further threads are still to be resolved when the next arrow is live at the last loaded medium (the shell's own rule).
        bool more = index >= 0 && index == list.Count - 1 && _next.IsEnabled;
        _filmstrip.SetEntries(list, list.Count, more);
        _filmstrip.SetCurrent(index);
        // The player reports the new length asynchronously. Clear the selected slot first so a reused
        // viewer never leaves the previous medium's duration on a video whose length is not known yet.
        _filmstrip.SetDuration(index, null);
        UpdateFilmstripPlacement();
    }

    private void OnVideoDurationChanged(TimeSpan? duration)
    {
        if (_entry?.Kind != SavedMediaKind.Video) return;
        // LengthChanged is admitted by GalleryVideoPlayer's generation fence. The filmstrip already
        // received the same entry synchronously in ShowAsync(entry, associations, ...).
        _filmstrip.SetDuration(_filmstrip.CurrentIndex, duration);
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
    private bool FilmstripEffective => FilmstripEffectiveFor(BuildChromePlan());

    private bool FilmstripEffectiveFor(GalleryChromePlan plan) => plan.Filmstrip && _filmstrip.Count > 0 && !_terminalRetirement
        && !_viewerInteractionStopped && FilmstripFits;

    private bool FilmstripHolds => _pointerInFilmstrip || _keyboardInteraction && _filmstrip.IsKeyboardFocusWithin;

    private bool FilmstripShownNow => FilmstripShownFor(BuildChromePlan());

    // At the bottom the strip is a part of the footer: it is on the screen exactly when the controls are, a peek never shows it alone.
    private bool FilmstripShownFor(GalleryChromePlan plan) => FilmstripEffectiveFor(plan)
        && (!OverlayControlsEffective || !_overlayHidden || _filmstripPeek && plan.FilmstripPeek);

    // Places the band, sizes it and shows or hides it. Cheap and idempotent: it runs with every placement, reveal and hide.
    private void UpdateFilmstripPlacement()
    {
        if (_terminalRetirement || _filmstrip == null) return; // (null: a viewer built without its constructor, as the overlay tests do)
        bool overlay = OverlayControlsEffective;
        GalleryChromePlan plan = BuildChromePlan();
        // A band that moved to the bottom (or lost its overlay) leaves no peek behind: the bottom strip comes with the controls.
        if (_filmstripPeek && !plan.FilmstripPeek) _filmstripPeek = false;
        bool show = FilmstripShownFor(plan);
        int tile = Bounds.Height > 0 && Bounds.Height < FilmstripCompactHeight
            ? Math.Min(_filmstripHeight, Config.GalleryFilmstripHeightMinimum) : _filmstripHeight;
        // The band ends before an open post panel (which starts below the same top row) and starts after the page margin. Only the top
        // band shares a row with the panel; the bottom band lies below it (docked) or in the footer the panel is lifted over (overlay).
        double panelWidth = double.IsFinite(_contextPanel.Width) && _contextPanel.Width > 0 ? _contextPanel.Width : _contextPanel.Bounds.Width;
        Panel picture = overlay ? OverlayHost : _visualRoot;
        bool panelDocked = ReferenceEquals(_contextPanel.Parent, _body) && picture == _visualRoot;
        bool panelOver = ReferenceEquals(_contextPanel.Parent, picture);
        double panelRight = _contextOpen && _contextPanel.IsVisible && panelDocked ? panelWidth - 4 : _contextOpen && _contextPanel.IsVisible && panelOver ? panelWidth + 12 : 0;
        GalleryFilmstripPlan.Placement place = GalleryFilmstripPlan.Place(plan.FilmstripDock, overlay, show, tile, TopRowClearance, panelRight, _nativeOverlayFallback);
        switch (place.Host)
        {
            case GalleryFilmstripPlan.Host.Dock: MoveOverlayControlAt(_filmstrip, _dockStack, 0); break;
            case GalleryFilmstripPlan.Host.OverlayFooter: MoveOverlayControlAt(_filmstrip, _overlayFooter, 0); break;
            // The top band follows the way back in the tab order (the way back is the first stop), before the picture and the toolbar.
            default: MoveOverlayControlAt(_filmstrip, picture, _close != null && ReferenceEquals(_close.Parent, picture) ? picture.Children.IndexOf(_close) + 1 : -1); break;
        }
        Grid.SetRow(_filmstrip, _nativeOverlayFallback && !_nativeOverlayActive ? 1 : 0); Grid.SetRowSpan(_filmstrip, 1);
        _filmstrip.Margin = new Thickness(place.Left, place.Top, place.Right, place.Bottom);
        // GL1+GL2: a shown band in the dock is its first row; the tools surface below it gives up its own top margin (ApplyDockMargin).
        bool headsDock = show && place.Host == GalleryFilmstripPlan.Host.Dock;
        if (headsDock != _filmstripHeadsDock) { _filmstripHeadsDock = headsDock; ApplyDockMargin(); }
        _filmstrip.SetDock(plan.FilmstripDock == GalleryFilmstripDock.Bottom);
        _filmstrip.Configure(tile, _filmstripPreload, _filmstripGap);
        ApplyFilmstripBackground(overlay);
        WorkViewGate.Apply(_filmstrip, _workView, stateVisible: show, nonDefaultState: FilmstripForced);
        // The top band, docked, pushes the picture down; the overlay floats over it. The bottom band never touches the picture.
        if (_viewport.Margin.Top != place.PictureTopInset) _viewport.Margin = new Thickness(0, place.PictureTopInset, 0, 0);
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
                _filmstripTopHot = inside && point.Y < FilmstripEdgeBand && OverlayControlsEffective && FilmstripPeeksAtTop;
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

    // The top edge zone and the peek exist only for a filmstrip at the top with the controls floating (the plan's FilmstripPeek); a
    // strip at the bottom has no zone of its own: the controls' bottom edge zone brings the whole footer, strip included.
    private bool FilmstripPeeksAtTop => BuildChromePlan() is { FilmstripPeek: true } plan && FilmstripEffectiveFor(plan);

    // The same restraint as the other edge reveals: no drag or press in progress, an active window, no dialog or popup.
    private bool CanPeekFilmstrip => IsGalleryWindowActive && _interactionAvailable && !HasGalleryDialog && !HasGalleryPopup
        && !_dragging && _pressedPointers.Count == 0 && _lastPointer?.Captured == null && !_helpOpen && FilmstripPeeksAtTop;

    // ---- keyboard and commands ----

    // Called from the gallery's key routing after the check that the key is not typed into a text field.
    private bool HandleFilmstripKey(KeyEventArgs e)
    {
        if (CommandBindings.Matches(e, "gallery.filmstrip-toggle")) { ToggleFilmstrip(); e.Handled = true; return true; }
        if (!_filmstrip.HandleKey(e)) return false;
        e.Handled = true;
        return true;
    }

    /// <summary>Shows or hides the filmstrip (the filmstrip key and the ⋯ row); the choice is the shown mode's layout choice, saved per profile and mode (GL4).</summary>
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
