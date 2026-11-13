using Avalonia.Controls;
using Avalonia.Input;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Motion;

namespace MeowChan.DesktopTests;

// The behaviour the archive and the media filter surfaces share. Controls without an Avalonia application or a window:
// the keyboard focus return needs a real TopLevel and stays with the native filter-surface smokes.
public sealed class FilterSurfaceControllerTests
{
    private static (FilterSurfaceController Controller, Expander Expander, Border Surface) Make(bool drivesVisibility, Func<bool>? ended = null)
    {
        Expander expander = new();
        Border surface = new() { IsVisible = false };
        return (new FilterSurfaceController(new Border(), expander, surface, ended ?? (() => false), drivesVisibility), expander, surface);
    }

    private static KeyEventArgs Key(Key key, KeyModifiers modifiers = KeyModifiers.None) =>
        new() { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers };

    [Fact]
    public void TheSurfaceFollowsTheOverlayReveal()
    {
        (_, _, Border surface) = Make(drivesVisibility: true);
        Assert.Contains(OverlayReveal.StyleClass, surface.Classes);
    }

    [Fact]
    public void WhenItDrivesVisibilityTheToggleShowsAndHidesTheSurface()
    {
        (FilterSurfaceController controller, Expander expander, Border surface) = Make(drivesVisibility: true);
        Assert.False(controller.IsOpen);
        expander.IsExpanded = true;
        Assert.True(controller.IsOpen);
        Assert.True(surface.IsVisible);
        expander.IsExpanded = false;
        Assert.False(controller.IsOpen);
        Assert.False(surface.IsVisible);
    }

    [Fact]
    public void WhenThePageBindsVisibilityTheControllerLeavesItAlone()
    {
        (_, Expander expander, Border surface) = Make(drivesVisibility: false);
        expander.IsExpanded = true;
        Assert.False(surface.IsVisible);
        surface.IsVisible = true;
        expander.IsExpanded = false;
        Assert.True(surface.IsVisible);
    }

    [Fact]
    public void AnEndedPageIgnoresTheToggle()
    {
        bool ended = false;
        (_, Expander expander, Border surface) = Make(drivesVisibility: true, () => ended);
        ended = true;
        expander.IsExpanded = true;
        Assert.False(surface.IsVisible);
    }

    [Fact]
    public void EscapeClosesTheSurfaceAndMarksTheKeyHandled()
    {
        (_, Expander expander, Border surface) = Make(drivesVisibility: true);
        expander.IsExpanded = true;
        KeyEventArgs escape = Key(Avalonia.Input.Key.Escape);
        surface.RaiseEvent(escape);
        Assert.True(escape.Handled);
        Assert.False(expander.IsExpanded);
        Assert.False(surface.IsVisible);
    }

    [Theory]
    [InlineData(Avalonia.Input.Key.Enter, KeyModifiers.None, false)]
    [InlineData(Avalonia.Input.Key.Escape, KeyModifiers.Shift, false)]
    [InlineData(Avalonia.Input.Key.Escape, KeyModifiers.Control, false)]
    [InlineData(Avalonia.Input.Key.Escape, KeyModifiers.None, true)]
    public void OnlyAPlainUnhandledEscapeCloses(Key key, KeyModifiers modifiers, bool alreadyHandled)
    {
        (_, Expander expander, Border surface) = Make(drivesVisibility: true);
        expander.IsExpanded = true;
        KeyEventArgs args = Key(key, modifiers);
        args.Handled = alreadyHandled;
        surface.RaiseEvent(args);
        Assert.True(expander.IsExpanded);
        Assert.True(surface.IsVisible);
        Assert.Equal(alreadyHandled, args.Handled);
    }

    [Fact]
    public void CloseCollapsesTheExpanderWithOrWithoutFocusRestore()
    {
        (FilterSurfaceController controller, Expander expander, _) = Make(drivesVisibility: true);
        expander.IsExpanded = true;
        controller.Close(restoreFocus: true);
        Assert.False(expander.IsExpanded);
        expander.IsExpanded = true;
        controller.Close(restoreFocus: false);
        Assert.False(expander.IsExpanded);
    }

    [Fact]
    public void FocusWithinIsFalseWithoutAWindow()
    {
        (FilterSurfaceController controller, _, Border surface) = Make(drivesVisibility: true);
        Assert.False(controller.FocusWithin(surface));
        Assert.False(FilterSurfaceController.FocusWithin(surface, surface));
    }
}
