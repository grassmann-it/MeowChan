using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;

namespace MeowChan.DesktopTests;

// BeginCapture is application-wide. Do not let this fixture's capture lease
// interfere with other shortcut tests; no profile or committed binding is changed.
[CollectionDefinition("Media shortcut routing", DisableParallelization = true)]
public sealed class MediaShortcutRoutingCollection;

[Collection("Media shortcut routing")]
public sealed class MediaShortcutRoutingTests
{
    [Fact]
    public void OnlyActualUnfocusedWindowInputMayUseGalleryFallback()
    {
        // No native window is opened: this predicate reads identity only.
        Window window = (Window)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Window));
        Window other = (Window)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Window));
        var e = Press(window, Key.Right);
        Assert.True(GalleryViewer.IsUnfocusedWindowKey(e, window, null));
        Assert.False(GalleryViewer.IsUnfocusedWindowKey(e, other, null));
        Assert.False(GalleryViewer.IsUnfocusedWindowKey(e, window, new Slider()));
        e.Source = new Slider();
        Assert.False(GalleryViewer.IsUnfocusedWindowKey(e, window, null));
        e.Source = window; e.Handled = true;
        Assert.False(GalleryViewer.IsUnfocusedWindowKey(e, window, null));
    }

    [Theory]
    [InlineData(Key.Left)]
    [InlineData(Key.Right)]
    [InlineData(Key.F7)]
    [InlineData(Key.F8)]
    public void UnfocusedHostFallbackRetainsEffectiveNavigationButNeverStealsFocusedInput(Key key)
    {
        Window host = (Window)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Window));
        var press = Press(host, key);
        Assert.True(GalleryViewer.IsUnfocusedWindowKey(press, host, null));
        foreach (IInputElement focused in new IInputElement[] { new TextBox(), new ComboBox(), new Slider(), new Button() })
            Assert.False(GalleryViewer.IsUnfocusedWindowKey(press, host, focused));
        press.Handled = true;
        Assert.False(GalleryViewer.IsUnfocusedWindowKey(press, host, null));
    }

    private static KeyEventArgs Press(object source, Key key, KeyModifiers modifiers = KeyModifiers.None) =>
        new() { Source = source, Key = key, KeyModifiers = modifiers, RoutedEvent = InputElement.KeyDownEvent };

    [Theory]
    [InlineData(Key.Left)]
    [InlineData(Key.Right)]
    [InlineData(Key.Up)]
    [InlineData(Key.Down)]
    [InlineData(Key.Home)]
    [InlineData(Key.End)]
    [InlineData(Key.PageUp)]
    [InlineData(Key.PageDown)]
    public void UnrelatedSliderNavigationRemainsLocalEvenWhenAGalleryBindingMatches(Key key)
    {
        var slider = new Slider { Minimum = 0, Maximum = 100, Value = 50 };
        var bindings = EffectiveShortcutBindings.Build(new ShortcutBindingsSection()
            .Set("gallery.previous", [])
            // This fixture remaps navigation; remove the new same-context volume
            // defaults so conflict rejection does not masquerade as a slider guard.
            .Set("video.volume-up", [])
            .Set("video.volume-down", [])
            .Set("gallery.next", [ShortcutGesture.Parse(key.ToString())]));
        var e = Press(slider, key);
        Assert.True(bindings.Matches(e, "gallery.next"));
        Assert.True(CommandRouter.IsNativeInput(e.Source, e.Key));
        Assert.False(GalleryViewer.IsGalleryShortcutInput(e));
        Assert.False(e.Handled); // Leave the event for the slider, do not consume it.
    }

    [Theory]
    [InlineData(Key.Left, 49)]
    [InlineData(Key.Right, 51)]
    [InlineData(Key.Up, 51)]
    [InlineData(Key.Down, 49)]
    [InlineData(Key.Home, 0)]
    [InlineData(Key.End, 100)]
    [InlineData(Key.PageUp, 60)]
    [InlineData(Key.PageDown, 40)]
    public void UnrelatedAvaloniaSliderStillHandlesItsReservedValueKeys(Key key, double expected)
    {
        var slider = new SliderHost(new Border()) { Minimum = 0, Maximum = 100, Value = 50,
            SmallChange = 1, LargeChange = 10 };
        var e = Press(slider, key);
        slider.ApplyLocalKey(e);
        Assert.True(e.Handled);
        Assert.Equal(expected, slider.Value);
    }

    [Fact]
    public void AvaloniaSliderDoesNotOwnTheCustomNonArrowGesture()
    {
        var slider = new SliderHost(new Border()) { Value = 50 };
        var e = Press(slider, Key.F9, KeyModifiers.Control | KeyModifiers.Shift);
        slider.ApplyLocalKey(e);
        Assert.False(e.Handled);
        Assert.True(GalleryViewer.IsGalleryShortcutInput(e));
        Assert.Equal(50, slider.Value);
    }

    [Fact]
    public void DefaultPreviousNextStayNativeOnUnrelatedSliders()
    {
        var slider = new Slider();
        foreach (var (key, id) in new[] { (Key.Left, "gallery.previous"), (Key.Right, "gallery.next") })
        {
            var e = Press(slider, key);
            Assert.True(EffectiveShortcutBindings.Build().Matches(e, id));
            Assert.False(CommandBindings.Matches(e, id));
            Assert.False(GalleryViewer.IsGalleryShortcutInput(e));
        }
    }

    [Theory]
    [InlineData("gallery.previous")]
    [InlineData("gallery.next")]
    [InlineData("video.play-pause")]
    [InlineData("video.mute")]
    [InlineData("video.loop")]
    [InlineData("gallery.context-toggle")]
    public void EffectiveNonArrowBindingWorksWithSliderFocusWithoutMovingFocus(string id)
    {
        var slider = new Slider { Value = 50 };
        var e = Press(slider, Key.F9, KeyModifiers.Control | KeyModifiers.Shift);
        var bindings = EffectiveShortcutBindings.Build(new ShortcutBindingsSection()
            .Set(id, [ShortcutGesture.Parse("Control+Shift+F9")]), ShortcutPlatform.Linux);
        Assert.Empty(bindings.Issues);
        Assert.True(bindings.Matches(e, id));
        Assert.True(GalleryViewer.IsGalleryShortcutInput(e));
        Assert.Equal(50, slider.Value);
        Assert.Same(slider, e.Source);
        Assert.False(e.Handled); // Eligibility/matching alone never executes an action.
    }

    [Fact]
    public void SliderUnusedSpaceMayReachDefaultPlaybackWhileButtonSpaceStaysNative()
    {
        var e = Press(new Slider(), Key.Space);
        Assert.True(GalleryViewer.IsGalleryShortcutInput(e));
        Assert.True(CommandBindings.Matches(e, "video.play-pause"));
        Assert.False(GalleryViewer.IsGalleryShortcutInput(Press(new Button(), Key.Space)));
    }

    [Fact]
    public void ClearedAndReplacedBindingsAreNotRecoveredFromCatalogDefaults()
    {
        var e = Press(new Slider(), Key.F9, KeyModifiers.Control | KeyModifiers.Shift);
        var section = new ShortcutBindingsSection().Set("gallery.next", [ShortcutGesture.Parse("Control+Shift+F9")]);
        Assert.True(EffectiveShortcutBindings.Build(section).Matches(e, "gallery.next"));
        Assert.False(EffectiveShortcutBindings.Build(section.Set("gallery.next", [])).Matches(e, "gallery.next"));
        Assert.False(EffectiveShortcutBindings.Build(section).Matches(Press(new Border(), Key.Right), "gallery.next"));
    }

    [Theory]
    [InlineData(Key.Space)]
    [InlineData(Key.Enter)]
    public void ButtonActivationIsNotGalleryPlaybackOrNavigation(Key key)
    {
        var child = new Border();
        var button = new ButtonHost(child);
        var e = Press(child, key);
        Assert.True(CommandRouter.IsNativeInput(e.Source, key));
        Assert.False(GalleryViewer.IsGalleryShortcutInput(e));
        Assert.False(CommandBindings.Matches(e, "video.play-pause"));
        Assert.False(e.Handled);
    }

    [Fact]
    public void ButtonDoesNotBlockAnUnrelatedCustomPlaybackGesture()
    {
        var button = new Button();
        var e = Press(button, Key.F9, KeyModifiers.Control | KeyModifiers.Shift);
        var bindings = EffectiveShortcutBindings.Build(new ShortcutBindingsSection()
            .Set("video.play-pause", [ShortcutGesture.Parse("Control+Shift+F9")]));
        Assert.True(bindings.Matches(e, "video.play-pause"));
        Assert.True(GalleryViewer.IsGalleryShortcutInput(e));
    }

    [Fact]
    public void SliderDescendantsUseTheSameKeySpecificPolicy()
    {
        var child = new Border();
        var slider = new SliderHost(child);
        Assert.True(CommandRouter.IsNativeInput(child, Key.Left));
        Assert.False(CommandRouter.IsNativeInput(child, Key.F9));
        Assert.True(GalleryViewer.IsGalleryShortcutInput(Press(child, Key.F9)));
        GC.KeepAlive(slider);
    }

    [Fact]
    public void TextAndSelectionSurfacesDoNotBecomeGalleryShortcuts()
    {
        Control[] controls = [new TextBox(), new ComboBox(), new NumericUpDown(), new ScrollBar(),
            new MenuItem(), new SelectableTextBlock(), new TabControl(), new DataGrid()];
        foreach (var control in controls)
            Assert.False(GalleryViewer.IsGalleryShortcutInput(Press(control, Key.F9, KeyModifiers.Control | KeyModifiers.Shift)));
    }

    [Fact]
    public void HandledEventsAndShortcutCaptureNeverBecomeASecondGalleryInvocation()
    {
        var e = Press(new Slider(), Key.F9);
        e.Handled = true;
        Assert.False(GalleryViewer.IsGalleryShortcutInput(e));
        using var router = new CommandRouter(new Border());
        Assert.False(router.Route(e));
        e.Handled = false;
        using (ShortcutService.BeginCapture())
        {
            Assert.False(GalleryViewer.IsGalleryShortcutInput(e));
            Assert.False(router.Route(e));
        }
        Assert.True(GalleryViewer.IsGalleryShortcutInput(e));
    }

    [Fact]
    public void OpenDropdownAndMenuBlockTheOwningContextEvenForOtherEventSources()
    {
        var combo = new ComboBox();
        var menu = new MenuItem();
        var root = new StackPanel { Children = { combo, menu, new Slider() } };
        Assert.False(CommandRouter.HasOpenPopup(root));
        combo.IsDropDownOpen = true;
        Assert.True(CommandRouter.HasOpenPopup(root));
        combo.IsDropDownOpen = false;
        menu.IsSubMenuOpen = true;
        Assert.True(CommandRouter.HasOpenPopup(root));
        menu.IsSubMenuOpen = false;
        Assert.False(CommandRouter.HasOpenPopup(root));
    }

    [Fact]
    public void DetachedOwnersAndUnknownSourcesNeverEstablishAnActiveWindowContext()
    {
        var owner = new Border();
        foreach (object? source in new object?[] { owner, new Border(), null, new object() })
            Assert.False(CommandRouter.IsRoutingContextActive(owner, source));
        using var router = new CommandRouter(owner);
        int executed = 0;
        using var registration = router.Register(new("gallery.next", owner, () => true, () => true, () => executed++));
        Assert.False(router.Route(Press(owner, Key.Right)));
        Assert.Equal(0, executed);
    }

    private sealed class ButtonHost : Button
    {
        public ButtonHost(Control child) => VisualChildren.Add(child);
    }

    private sealed class SliderHost : Slider
    {
        public SliderHost(Control child) => VisualChildren.Add(child);
        public void ApplyLocalKey(KeyEventArgs e) => base.OnKeyDown(e);
    }
}
