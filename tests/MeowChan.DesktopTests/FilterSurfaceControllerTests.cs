using Avalonia.Controls;
using Avalonia.Input;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Motion;

namespace MeowChan.DesktopTests;

// The behaviour the archive and the media filter surfaces share. Controls without an Avalonia application or a window:
// the keyboard focus return needs a real TopLevel and stays with the native filter-surface smokes.
public sealed class FilterSurfaceControllerTests
{
    private static (FilterSurfaceController Controller, FilterToggleButton Toggle, Border Surface) Make(bool drivesVisibility, Func<bool>? ended = null, bool overlay = true)
    {
        FilterToggleButton toggle = new();
        Border surface = new() { IsVisible = false };
        return (new FilterSurfaceController(new Border(), toggle, surface, ended ?? (() => false), drivesVisibility, overlay), toggle, surface);
    }

    private static KeyEventArgs Key(Key key, KeyModifiers modifiers = KeyModifiers.None) =>
        new() { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers };

    [Fact]
    public void AnOverlayFollowsTheOverlayRevealAndAnInlineSectionDoesNot()
    {
        (_, _, Border overlay) = Make(drivesVisibility: true);
        Assert.Contains(OverlayReveal.StyleClass, overlay.Classes);
        (_, _, Border inline) = Make(drivesVisibility: true, overlay: false);
        Assert.DoesNotContain(OverlayReveal.StyleClass, inline.Classes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WhenItDrivesVisibilityTheToggleShowsAndHidesTheSurface(bool overlay)
    {
        (FilterSurfaceController controller, FilterToggleButton toggle, Border surface) = Make(drivesVisibility: true, overlay: overlay);
        Assert.False(controller.IsOpen);
        toggle.IsChecked = true;
        Assert.True(controller.IsOpen);
        Assert.True(surface.IsVisible);
        toggle.IsChecked = false;
        Assert.False(controller.IsOpen);
        Assert.False(surface.IsVisible);
    }

    [Fact]
    public void TheToggleIsTheControllersOwnButtonAndStartsClosed()
    {
        (FilterSurfaceController controller, FilterToggleButton toggle, _) = Make(drivesVisibility: true);
        Assert.Same(toggle, controller.Toggle);
        Assert.False(controller.IsOpen);
        Assert.NotEqual(true, toggle.IsChecked);
    }

    [Fact]
    public void WhenThePageBindsVisibilityTheControllerLeavesItAlone()
    {
        (_, FilterToggleButton toggle, Border surface) = Make(drivesVisibility: false);
        toggle.IsChecked = true;
        Assert.False(surface.IsVisible);
        surface.IsVisible = true;
        toggle.IsChecked = false;
        Assert.True(surface.IsVisible);
    }

    [Fact]
    public void AnEndedPageIgnoresTheToggle()
    {
        bool ended = false;
        (_, FilterToggleButton toggle, Border surface) = Make(drivesVisibility: true, () => ended);
        ended = true;
        toggle.IsChecked = true;
        Assert.False(surface.IsVisible);
    }

    [Fact]
    public void EscapeClosesTheSurfaceAndMarksTheKeyHandled()
    {
        (_, FilterToggleButton toggle, Border surface) = Make(drivesVisibility: true);
        toggle.IsChecked = true;
        KeyEventArgs escape = Key(Avalonia.Input.Key.Escape);
        surface.RaiseEvent(escape);
        Assert.True(escape.Handled);
        Assert.NotEqual(true, toggle.IsChecked);
        Assert.False(surface.IsVisible);
    }

    [Theory]
    [InlineData(Avalonia.Input.Key.Enter, KeyModifiers.None, false)]
    [InlineData(Avalonia.Input.Key.Escape, KeyModifiers.Shift, false)]
    [InlineData(Avalonia.Input.Key.Escape, KeyModifiers.Control, false)]
    [InlineData(Avalonia.Input.Key.Escape, KeyModifiers.None, true)]
    public void OnlyAPlainUnhandledEscapeCloses(Key key, KeyModifiers modifiers, bool alreadyHandled)
    {
        (_, FilterToggleButton toggle, Border surface) = Make(drivesVisibility: true);
        toggle.IsChecked = true;
        KeyEventArgs args = Key(key, modifiers);
        args.Handled = alreadyHandled;
        surface.RaiseEvent(args);
        Assert.True(toggle.IsChecked);
        Assert.True(surface.IsVisible);
        Assert.Equal(alreadyHandled, args.Handled);
    }

    [Fact]
    public void CloseUnchecksTheToggleWithOrWithoutFocusRestore()
    {
        (FilterSurfaceController controller, FilterToggleButton toggle, _) = Make(drivesVisibility: true);
        toggle.IsChecked = true;
        controller.Close(restoreFocus: true);
        Assert.NotEqual(true, toggle.IsChecked);
        toggle.IsChecked = true;
        controller.Close(restoreFocus: false);
        Assert.NotEqual(true, toggle.IsChecked);
    }

    [Fact]
    public void FocusWithinIsFalseWithoutAWindow()
    {
        (FilterSurfaceController controller, _, Border surface) = Make(drivesVisibility: true);
        Assert.False(controller.FocusWithin(surface));
        Assert.False(FilterSurfaceController.FocusWithin(surface, surface));
    }
}
