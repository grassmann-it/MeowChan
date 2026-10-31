using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class GalleryOverlayActivityTests
{
    private static GalleryOverlayActivity Idle => new(Overlay: true, AutoHide: true, Available: true,
        ActiveWindow: true, Focused: false, Captured: false, Popup: false, Reading: false, Notice: false);

    [Theory]
    [InlineData(0, false)]
    [InlineData(499, false)]
    [InlineData(500, true)]
    [InlineData(60000, true)]
    public void OnlyAnIdleOverlayMayHide(int milliseconds, bool expected) =>
        Assert.Equal(expected, Idle.CanHide(TimeSpan.FromMilliseconds(milliseconds)));

    [Theory]
    [InlineData("docked")]
    [InlineData("auto-hide-off")]
    [InlineData("covered")]
    [InlineData("inactive-window")]
    [InlineData("control-focus")]
    [InlineData("pointer-capture")]
    [InlineData("menu-or-dialog")]
    [InlineData("post-or-help-reading")]
    public void InteractionPreventsHidingEvenAfterLongIdle(string condition)
    {
        var state = condition switch
        {
            "docked" => Idle with { Overlay = false },
            "auto-hide-off" => Idle with { AutoHide = false },
            "covered" => Idle with { Available = false },
            "inactive-window" => Idle with { ActiveWindow = false },
            "control-focus" => Idle with { Focused = true },
            "pointer-capture" => Idle with { Captured = true },
            "menu-or-dialog" => Idle with { Popup = true },
            "post-or-help-reading" => Idle with { Reading = true },
            _ => throw new ArgumentOutOfRangeException(nameof(condition))
        };
        Assert.False(state.CanHide(TimeSpan.FromHours(1)));
    }

    [Fact]
    public void HoverProtectsOnlyItsPanelAndMouseFocusAloneIsNotKeyboardOwnership()
    {
        Assert.False((Idle with { Hovered = true }).CanHide(TimeSpan.FromSeconds(9)));
        Assert.True(Idle.CanHide(TimeSpan.FromSeconds(1))); // caller maps pointer focus to Focused=false
        Assert.False((Idle with { Focused = true }).CanHide(TimeSpan.FromSeconds(9)));
    }

    [Theory]
    [InlineData(400, 300, false, false)]
    [InlineData(400, 599, true, false)]
    [InlineData(799, 200, false, true)]
    [InlineData(799, 599, true, false)]
    [InlineData(-1, 200, false, false)]
    [InlineData(800, 200, false, false)]
    public void OnlyBottomAndRightStripsRevealPanels(double x, double y, bool controls, bool post)
    {
        var hit = GalleryEdgeHit.At(new Avalonia.Point(x, y), new Avalonia.Size(800, 600));
        Assert.Equal(controls, hit.Controls);
        Assert.Equal(post, hit.Post);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void PlacementAndMediaFocusAreIndependent(bool mediaFocus, bool overlayControls)
    {
        var prefs = new GalleryPlaybackPreferences(MediaFocus: mediaFocus, OverlayControls: overlayControls,
            OverlayPostContext: true);
        var placement = GalleryOverlayPlacement.Resolve(prefs, nativeVideo: false, nativeOverlayActive: false, fallback: false);
        Assert.Equal(overlayControls, placement.Controls);
        Assert.True(placement.PostContext);
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    [InlineData(false, false, false, true)]
    public void NativeFallbackChangesEffectivePlacementNotSavedWishes(bool native, bool overlayActive, bool fallback, bool expected)
    {
        var prefs = new GalleryPlaybackPreferences(OverlayControls: true, OverlayPostContext: true);
        var placement = GalleryOverlayPlacement.Resolve(prefs, native, overlayActive, fallback);
        Assert.Equal(expected, placement.Controls);
        Assert.Equal(expected, placement.PostContext);
        Assert.True(prefs.OverlayControls);
        Assert.True(prefs.OverlayPostContext);
    }

    [Theory]
    [InlineData(799, 550, true, false)] // volume/loop above the bottom 24px band
    [InlineData(799, 599, true, false)] // bottom-right transport corner
    [InlineData(799, 599, false, true)] // post owns bottom strip when controls are collapsed
    [InlineData(799, 200, false, true)]
    public void VisiblePanelOwnsItsWholeRectangleBeforeAnyHotspot(double x, double y, bool inControls, bool inPost)
    {
        var raw = GalleryEdgeHit.At(new Point(x, y), new Size(800, 600));
        Assert.True(raw.Controls || raw.Post);
        Assert.Equal(default, raw.WithoutPanelOverlap(inControls, inPost, inIndicator: false));
    }

    [Fact]
    public void IndicatorDoesNotDisappearUnderThePointerBeforeClickOrTouchActivation()
    {
        var bottom = GalleryEdgeHit.At(new Point(400, 590), new Size(800, 600));
        var right = GalleryEdgeHit.At(new Point(790, 300), new Size(800, 600));
        Assert.Equal(default, bottom.WithoutPanelOverlap(false, false, inIndicator: true));
        Assert.Equal(default, right.WithoutPanelOverlap(false, false, inIndicator: true));
        Assert.Equal(bottom, bottom.WithoutPanelOverlap(false, false, false));
        Assert.Equal(right, right.WithoutPanelOverlap(false, false, false));
    }

    [Theory]
    [InlineData(double.NaN, 600, 24)]
    [InlineData(800, double.PositiveInfinity, 24)]
    [InlineData(800, 600, double.NaN)]
    [InlineData(800, 600, -1)]
    [InlineData(0, 600, 24)]
    public void InvalidLayoutCannotActivateAnEdge(double width, double height, double band) =>
        Assert.Equal(default, GalleryEdgeHit.At(new Point(799, 599), new Size(width, height), band));

    [Theory]
    [InlineData(Key.Left)]
    [InlineData(Key.Right)]
    [InlineData(Key.Up)]
    [InlineData(Key.Down)]
    [InlineData(Key.Space)]
    [InlineData(Key.Enter)]
    [InlineData(Key.Escape)]
    [InlineData(Key.F1)]
    [InlineData(Key.F11)]
    [InlineData(Key.M)]
    [InlineData(Key.L)]
    [InlineData(Key.F9)]
    public void HiddenOverlayDoesNotGateFirstOrRepeatedCommandOrEscape(Key key)
    {
        // Exercise the actual entry hook without constructing a window/player.
        // Command observation must not touch any UI: only Tab requests a reveal.
        var viewer = (GalleryViewer)RuntimeHelpers.GetUninitializedObject(typeof(GalleryViewer));
        var hidden = typeof(GalleryViewer).GetField("_overlayHidden", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var reveal = typeof(GalleryViewer).GetMethod("RevealForKeyboard", BindingFlags.NonPublic | BindingFlags.Instance)!;
        hidden.SetValue(viewer, true);
        foreach (var modifiers in new[] { KeyModifiers.None, KeyModifiers.Control | KeyModifiers.Shift })
        {
            for (int press = 0; press < 3; press++)
            {
                var e = new KeyEventArgs { Key = key, KeyModifiers = modifiers };
                Assert.False((bool)reveal.Invoke(viewer, new object[] { e })!);
                Assert.False(e.Handled);
                Assert.True((bool)hidden.GetValue(viewer)!);
            }
        }
    }

    [Theory]
    [InlineData(KeyModifiers.None)]
    [InlineData(KeyModifiers.Shift)]
    public void TabRequestsAccessWithoutClaimingTheKey(KeyModifiers modifiers)
    {
        var e = new KeyEventArgs { Key = Key.Tab, KeyModifiers = modifiers };
        Assert.True(GalleryEdgeKeyboard.ShouldReveal(e));
        Assert.False(e.Handled);
        e.Handled = true;
        Assert.False(GalleryEdgeKeyboard.ShouldReveal(e));
        Assert.True(e.Handled);
    }

    [Theory]
    [InlineData(false, "GalleryControlsEdgeIndicator", HorizontalAlignment.Center, VerticalAlignment.Bottom)]
    [InlineData(true, "GalleryPostEdgeIndicator", HorizontalAlignment.Right, VerticalAlignment.Center)]
    public void EdgeIndicatorsHaveStableNamesAccessibleLabelsAndFocusableTouchTargets(bool post, string name,
        HorizontalAlignment horizontal, VerticalAlignment vertical)
    {
        var indicator = GalleryViewer.CreateEdgeIndicator(post);
        Assert.Equal(name, indicator.Name);
        Assert.True(indicator.Focusable);
        Assert.True(indicator.IsHitTestVisible);
        Assert.False(indicator.IsVisible); // enabled only for an effectively collapsed overlay panel
        Assert.Equal(44, indicator.Width);
        Assert.Equal(44, indicator.Height);
        Assert.Equal(horizontal, indicator.HorizontalAlignment);
        Assert.Equal(vertical, indicator.VerticalAlignment);
        Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(indicator)));
        Assert.Equal(AutomationProperties.GetName(indicator), AutomationProperties.GetHelpText(indicator));
    }

    [Theory]
    [InlineData(false, false, false, false, true)]
    [InlineData(true, false, false, false, false)]
    [InlineData(false, true, false, false, false)]
    [InlineData(false, false, true, false, false)]
    [InlineData(false, false, false, true, false)]
    [InlineData(true, true, true, true, false)]
    public void HoverCannotInterruptCaptureTextSelectionKeyboardPanelFocusOrHelp(bool captured,
        bool selectedText, bool keyboardPanelFocus, bool helpOpen, bool expected) =>
        Assert.Equal(expected, GalleryEdgeHover.CanReveal(captured, selectedText, keyboardPanelFocus, helpOpen));

    [Fact]
    public void HiddenOverlayObservationLeavesNativeInputAndHandledKeysUntouched()
    {
        var viewer = (GalleryViewer)RuntimeHelpers.GetUninitializedObject(typeof(GalleryViewer));
        SetField(viewer, "_overlayHidden", true);
        foreach (var source in new Control[] { new Slider(), new TextBox(), new SelectableTextBlock(), new Button() })
        foreach (Key key in new[] { Key.Left, Key.Right, Key.Space, Key.Enter, Key.Escape, Key.F9 })
        foreach (bool handled in new[] { false, true })
        {
            var e = new KeyEventArgs { Source = source, Key = key, Handled = handled };
            Assert.False((bool)Invoke(viewer, "RevealForKeyboard", e)!);
            Assert.Equal(handled, e.Handled);
            Assert.Same(source, e.Source);
            Assert.True((bool)GetField(viewer, "_overlayHidden")!);
        }
    }

    // Actual indicator reparenting requires the UI dispatcher. Its identity,
    // idempotence and no-footer-row assertions live in the registered native
    // GalleryControlTransferChecks rather than invoking UI work on xUnit threads.

    [Fact]
    public void StoppedViewerDetachesIndicatorsFromBothRootsAndNeverRefocusesOrReattachesThem()
    {
        var viewer = (GalleryViewer)RuntimeHelpers.GetUninitializedObject(typeof(GalleryViewer));
        var controls = GalleryViewer.CreateEdgeIndicator(false);
        var post = GalleryViewer.CreateEdgeIndicator(true);
        var managed = new Grid(); var nativeOverlay = new Grid();
        controls.IsVisible = post.IsVisible = true;
        managed.Children.Add(controls); nativeOverlay.Children.Add(post);
        SetField(viewer, "_controlsEdgeIndicator", controls);
        SetField(viewer, "_postEdgeIndicator", post);
        SetField(viewer, "_viewerInteractionStopped", true);
        SetField(viewer, "_pointerOnControlsIndicator", true);
        SetField(viewer, "_pointerOnPostIndicator", true);
        Invoke(viewer, "UpdateEdgeIndicators");
        Invoke(viewer, "UpdateEdgeIndicators");
        Assert.Null(controls.Parent); Assert.Null(post.Parent);
        Assert.Empty(managed.Children); Assert.Empty(nativeOverlay.Children);
        Assert.False(controls.IsVisible); Assert.False(post.IsVisible);
        Assert.False((bool)GetField(viewer, "_pointerOnControlsIndicator")!);
        Assert.False((bool)GetField(viewer, "_pointerOnPostIndicator")!);
    }

    [Theory]
    [InlineData(KeyModifiers.Control)]
    [InlineData(KeyModifiers.Alt)]
    [InlineData(KeyModifiers.Meta)]
    [InlineData(KeyModifiers.Control | KeyModifiers.Shift)]
    public void ModifiedTabCommandsAreNotMistakenForFocusTraversal(KeyModifiers modifiers)
    {
        var e = new KeyEventArgs { Key = Key.Tab, KeyModifiers = modifiers };
        Assert.False(GalleryEdgeKeyboard.ShouldReveal(e));
        Assert.False(e.Handled);
    }

    [Fact]
    public void PointerTextSelectionIsProtectedInPostAndEditableControlsButHiddenSelectionIsNotAPin()
    {
        var reading = new SelectableTextBlock { Text = "post context", SelectionStart = 0, SelectionEnd = 4 };
        var editing = new TextBox { Text = "10", SelectionStart = 0, SelectionEnd = 2 };
        Assert.True(GalleryEdgeHover.HasSelection(reading));
        Assert.True(GalleryEdgeHover.HasSelection(editing));
        var panel = new StackPanel { Children = { reading, editing } };
        Assert.True(GalleryEdgeHover.HasSelection(panel));
        reading.SelectionEnd = 0; editing.SelectionEnd = 0;
        Assert.False(GalleryEdgeHover.HasSelection(panel));
        reading.SelectionEnd = 4; reading.IsVisible = false;
        Assert.False(GalleryEdgeHover.HasSelection(panel));
        editing.SelectionEnd = 2; panel.IsVisible = false;
        Assert.False(GalleryEdgeHover.HasSelection(panel));
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(699, true)]
    [InlineData(700, false)]
    [InlineData(60000, false)]
    public void MotionHintsExpireWithoutHoverOrPollingRenewal(int milliseconds, bool expected)
    {
        Assert.False(GalleryEdgeMotion.IsVisible(null));
        var age = TimeSpan.FromMilliseconds(milliseconds);
        for (int poll = 0; poll < 3; poll++)
            Assert.Equal(expected, GalleryEdgeMotion.IsVisible(age));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndicatorTemplateIsOnlyATranslucentChevronEvenWhenHoveredOrPressed(bool post)
    {
        var indicator = GalleryViewer.CreateEdgeIndicator(post);
        indicator.ApplyTemplate();
        Assert.Equal(new Thickness(0), indicator.BorderThickness);
        Assert.Null(indicator.FocusAdorner);
        Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(indicator.Background).Color);
        Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(indicator.BorderBrush).Color);
        // The complete custom template contains no theme presenter/border and no
        // state setters that could restore a hover or pressed box.
        Assert.Empty(indicator.Styles);
        var glyph = Assert.Single(indicator.GetVisualChildren().OfType<TextBlock>());
        Assert.Empty(glyph.GetVisualChildren());
        Assert.Equal(post ? "‹" : "⌃", glyph.Text);
        byte alpha = Assert.IsAssignableFrom<ISolidColorBrush>(glyph.Foreground).Color.A;
        Assert.InRange(alpha, (byte)1, (byte)127);
        Assert.Equal(0, glyph.Opacity); // no initial/static hint
        Assert.True(indicator.IsHitTestVisible); // invisible artwork is not a lost touch target
    }

    [Fact]
    public void KeyboardFocusShowsGlyphWithoutMouseMotionAndLossRestoresExpiryPolicy()
    {
        Assert.True(GalleryEdgeMotion.IsVisible(null, keyboardFocused: true));
        Assert.True(GalleryEdgeMotion.IsVisible(TimeSpan.FromSeconds(10), keyboardFocused: true));
        Assert.False(GalleryEdgeMotion.IsVisible(null, keyboardFocused: false));
        Assert.False(GalleryEdgeMotion.IsVisible(TimeSpan.FromSeconds(10), keyboardFocused: false));
    }

    [Fact]
    public void OneShotExpiryClearsEvenFreshMotionAndKeepsTransparentTargets()
    {
        var viewer = (GalleryViewer)RuntimeHelpers.GetUninitializedObject(typeof(GalleryViewer));
        var controls = GalleryViewer.CreateEdgeIndicator(false);
        var post = GalleryViewer.CreateEdgeIndicator(true);
        SetField(viewer, "_controlsEdgeIndicator", controls);
        SetField(viewer, "_postEdgeIndicator", post);
        SetField(viewer, "_lastIndicatorMotion", System.Diagnostics.Stopwatch.GetTimestamp());
        Invoke(viewer, "UpdateEdgeIndicatorArtwork");
        Invoke(viewer, "OnIndicatorExpiry", null!, EventArgs.Empty);
        Assert.Null(GetField(viewer, "_lastIndicatorMotion"));
        foreach (Button indicator in new[] { controls, post })
        {
            Assert.Equal(0, Assert.Single(indicator.GetVisualChildren().OfType<TextBlock>()).Opacity);
            Assert.True(indicator.IsHitTestVisible);
            Assert.Equal(44, indicator.Width);
            Assert.Equal(44, indicator.Height);
        }
    }

    private static object? Invoke(GalleryViewer viewer, string name, params object[] args) =>
        typeof(GalleryViewer).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(viewer, args);
    private static object? GetField(GalleryViewer viewer, string name) =>
        typeof(GalleryViewer).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(viewer);
    private static void SetField(GalleryViewer viewer, string name, object value) =>
        typeof(GalleryViewer).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(viewer, value);

}
