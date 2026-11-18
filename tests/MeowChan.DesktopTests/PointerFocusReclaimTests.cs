using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using MeowChan.Desktop.Controls;

namespace MeowChan.DesktopTests;

/// <summary>
/// The one rule of <see cref="PointerFocusReclaim"/> on real controls and the real routed pointer events (no window: the focus the rule asks for is a seam,
/// the step it defers is captured and run by the test, as the UI thread would run it after the click). A pointer release on a Button gives the keys back to
/// the owner; keyboard activation raises no pointer event and so keeps the focus where it is; text inputs, the timeline, exempt subtrees and a focus that
/// moved on purpose are never taken. The same rule serves the gallery's chrome and transport and the reader's video bar (the window-level proof is in the
/// smoke runs of the gallery and the reader).
/// </summary>
[Collection("Avalonia global state")]
public sealed class PointerFocusReclaimTests
{
    private sealed class IconButton : Button
    {
        protected override Type StyleKeyOverride => typeof(Button);

        internal void AddIcon(Visual icon) => VisualChildren.Add(icon);
    }

    private sealed class Rig
    {
        internal readonly StackPanel Scope = new();
        internal readonly List<Action> Deferred = new();
        internal int Reclaims;
        internal IInputElement? Focused;
        internal readonly PointerFocusReclaim Reclaim;

        internal Rig() => Reclaim = PointerFocusReclaim.Attach(Scope, () => Reclaims++, Deferred.Add, () => Focused);

        internal T Add<T>(T control) where T : Control { Scope.Children.Add(control); return control; }

        /// <summary>Runs what the release deferred (the posted step after the click) and reports how often the owner got the keys.</summary>
        internal int Settle()
        {
            Action[] steps = Deferred.ToArray();
            Deferred.Clear();
            foreach (Action step in steps) step();
            return Reclaims;
        }
    }

    private static void Release(Control target, Visual root, PointerType type = PointerType.Mouse, MouseButton button = MouseButton.Left)
    {
        using Avalonia.Input.Pointer pointer = new(Avalonia.Input.Pointer.GetNextFreeId(), type, true);
        PointerUpdateKind kind = button == MouseButton.Left ? PointerUpdateKind.LeftButtonReleased
            : button == MouseButton.Right ? PointerUpdateKind.RightButtonReleased : PointerUpdateKind.MiddleButtonReleased;
        target.RaiseEvent(new PointerReleasedEventArgs(target, pointer, root, new Point(2, 2), 1,
            new PointerPointProperties(RawInputModifiers.None, kind), KeyModifiers.None, button));
    }

    [Theory]
    [InlineData(PointerType.Mouse)]
    [InlineData(PointerType.Pen)]
    [InlineData(PointerType.Touch)]
    public void AnyPointerActivationOfAButtonGivesTheKeysBackAfterItsOwnHandling(PointerType type)
    {
        Rig rig = new();
        Button button = rig.Add(new Button { Name = "GalleryNext" });
        Release(button, rig.Scope, type);
        Assert.Equal(0, rig.Reclaims); // nothing happens inside the release itself: the owner is asked after the control handled it
        Assert.Single(rig.Deferred);
        Assert.Equal(1, rig.Settle());
    }

    [Fact]
    public void AToggleLikeTheFavoriteBadgeAndAHitOnTheContentOfAButtonCountAsTheButton()
    {
        Rig rig = new();
        ToggleButton badge = rig.Add(new ToggleButton { Name = "GalleryFavoriteIndicator" });
        Border icon = new();
        // Without a window no template is applied, so the icon is made a visual child by hand, as the button's template would.
        rig.Add(new IconButton { Name = "GalleryFit" }).AddIcon(icon);
        Release(badge, rig.Scope);
        Assert.Equal(1, rig.Settle());
        Release(icon, rig.Scope); // the click lands on the icon inside the button
        Assert.Equal(2, rig.Settle());
    }

    [Fact]
    public void KeyboardActivationKeepsTheFocusOnTheControl()
    {
        Rig rig = new();
        Button button = rig.Add(new Button { Name = "GalleryVideoMute" });
        // Tab to the button, then Space and Enter: key events and the click they cause are no pointer release, so the rule is never asked.
        button.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space, Source = button });
        button.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.Space, Source = button });
        button.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = button });
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Empty(rig.Deferred);
        Assert.Equal(0, rig.Settle());
    }

    [Fact]
    public void ARightClickOnlyMattersForAMenuAndAMiddleClickNever()
    {
        Rig rig = new();
        Button button = rig.Add(new Button());
        Release(button, rig.Scope, button: MouseButton.Right);
        Assert.Equal(0, rig.Settle()); // no menu opened by it: nothing to wait for and nothing to give back
        Release(button, rig.Scope, button: MouseButton.Middle);
        Assert.Empty(rig.Deferred);
    }

    [Fact]
    public void TextInputsTheTimelineAndThePictureAreNeverATarget()
    {
        Rig rig = new();
        Control[] targets =
        {
            rig.Add(new TextBox { Name = "GalleryTagInput" }), rig.Add(new NumericUpDown()), rig.Add(new ComboBox()),
            rig.Add(new Slider { Name = "GalleryVideoSeek" }), // the timeline keeps its focus (its arrows seek)
            rig.Add(new Border())                              // the picture: a plain click there has its own focus rule
        };
        foreach (Control target in targets)
        {
            Assert.Null(PointerFocusReclaim.Resolve(rig.Scope, target));
            Release(target, rig.Scope);
        }
        Assert.Empty(rig.Deferred);
        Assert.Equal(0, rig.Settle());
    }

    [Fact]
    public void OnlyASliderTheOwnerNamesGivesTheFocusBack()
    {
        Rig rig = new();
        Slider volume = rig.Add(new Slider { Name = "GalleryVideoVolume" });
        PointerFocusReclaim.SetReclaims(volume, true);
        Release(volume, rig.Scope);
        Assert.Equal(1, rig.Settle());
        Release(rig.Add(new Slider { Name = "GalleryVideoSeek" }), rig.Scope);
        Assert.Equal(1, rig.Settle()); // still one: the timeline gave nothing back
    }

    [Fact]
    public void AnExemptSubtreeAndAControlOutsideTheScopeAreLeftAlone()
    {
        Rig rig = new();
        Border region = rig.Add(new Border());
        Button inside = new() { Name = "ReaderInlineVideoOpen" };
        region.Child = inside;
        PointerFocusReclaim.SetExempt(region, true); // a whole region (the filmstrip, the tagging strip, the edge handles)
        Release(inside, rig.Scope);
        Assert.Equal(0, rig.Settle());
        PointerFocusReclaim.SetExempt(region, false);
        PointerFocusReclaim.SetExempt(inside, true); // a single button (Open in gallery)
        Release(inside, rig.Scope);
        Assert.Equal(0, rig.Settle());
        PointerFocusReclaim.SetExempt(inside, false);
        Release(inside, rig.Scope);
        Assert.Equal(1, rig.Settle());
        // A control that is not below the scope (the content of a popup) is not this surface's.
        Assert.Null(PointerFocusReclaim.Resolve(rig.Scope, new Button()));
    }

    [Fact]
    public void TheFocusOnlyGoesBackWhenItStillRestsOnTheClickedControlOrNowhere()
    {
        Rig rig = new();
        Button clicked = rig.Add(new Button { Name = "GalleryToggleContext" });
        TextBox field = rig.Add(new TextBox { Name = "GalleryTagInput" });
        Button other = rig.Add(new Button { Name = "GalleryPrevious" });

        rig.Focused = clicked; // the press focused the button
        Release(clicked, rig.Scope);
        Assert.Equal(1, rig.Settle());

        rig.Focused = null; // the button hid itself (the play button of the reader) or the medium changed
        Release(clicked, rig.Scope);
        Assert.Equal(2, rig.Settle());

        rig.Focused = field; // its click moved the focus into a text field on purpose
        Release(clicked, rig.Scope);
        Assert.Equal(2, rig.Settle());

        rig.Focused = other; // or onto another control
        Release(clicked, rig.Scope);
        Assert.Equal(2, rig.Settle());
    }

    [Fact]
    public void ADisposedWatcherSeesNothing()
    {
        Rig rig = new();
        Button button = rig.Add(new Button());
        rig.Reclaim.Dispose();
        Release(button, rig.Scope);
        Assert.Empty(rig.Deferred);
    }

    [Fact]
    public void DisposingAlsoCancelsAReclaimAlreadyQueuedByTheRelease()
    {
        Rig rig = new();
        Release(rig.Add(new Button()), rig.Scope);
        Assert.Single(rig.Deferred);
        rig.Reclaim.Dispose();
        rig.Reclaim.Dispose(); // terminal cleanup can be repeated
        Assert.Equal(0, rig.Settle());
    }

    [Fact]
    public void AHandledReleaseStillCountsBecauseAButtonHandlesItsOwn()
    {
        Rig rig = new();
        Button button = rig.Add(new Button());
        button.AddHandler(InputElement.PointerReleasedEvent, (_, e) => e.Handled = true, RoutingStrategies.Bubble);
        Release(button, rig.Scope);
        Assert.Equal(1, rig.Settle());
    }
}
