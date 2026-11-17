using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.MediaActions;
using MeowChan.Gallery;
using MeowChan.Storage;

namespace MeowChan.DesktopTests;

/// <summary>
/// The entry point audit of the gallery viewer (16.11.2026, Tom: "shown but silently does nothing"). The real viewer is built without a window
/// and the real key goes to it through the gallery's key policy (<see cref="GalleryViewer.RouteKeyForTests"/>), so each test fails on the
/// handler as it was: a key whose command had no handler, a key consumed in silence, a row enabled while its click can only return.
/// </summary>
[Collection("Gallery viewer construction")]
public sealed class GalleryEntryPointAuditTests
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

    private static void WithViewer(Action<GalleryViewer> test) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
        test(new GalleryViewer());
    });

    private static KeyEventArgs Key(GalleryViewer viewer, Key key, KeyModifiers modifiers = KeyModifiers.None) =>
        new() { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers, Source = viewer };

    private static T Get<T>(GalleryViewer viewer, string field) => (T)typeof(GalleryViewer).GetField(field, Fields)!.GetValue(viewer)!;
    private static void Set(GalleryViewer viewer, string field, object? value) => typeof(GalleryViewer).GetField(field, Fields)!.SetValue(viewer, value);

    // A shown image without a post (a loose file): T, R and the tag key have nothing they can open for it.
    private static GalleryEntry LooseImage()
    {
        var asset = new MediaAssetReference("asset", "image", "original", "loose.png", "/fixture/loose.png", 7,
            new string('a', 64), "https://fixture.invalid/loose.png", "loose.png", true);
        var manifest = new MediaAssetSnapshot("fixture", MediaManifestStatus.Available, 1, "generation", [asset]);
        return new(new(new("fixture", "b", "42"), manifest.Directory, asset.AssetId), manifest, asset, null, null, false, true);
    }

    private static List<string> Explanations(GalleryViewer viewer)
    {
        List<string> said = new();
        viewer.MenuExplanationRequested += said.Add;
        return said;
    }

    // MISSING HANDLER: "Back to image" is offered for the gallery in the shortcut editor; a key bound to it did nothing.
    [Fact]
    public void AKeyBoundToBackToImageClosesThePostPanel() => WithViewer(viewer =>
    {
        ShortcutService.Publish(new ShortcutBindingsSection().Set("gallery.context-close", [ShortcutGesture.Parse("F9")]));
        try
        {
            viewer.Configure(contextOpen: true, showShortcutHelp: false, videoAutoplay: false, videoStartMuted: false, videoVolume: 50);
            Assert.True(Get<bool>(viewer, "_contextOpen"));
            KeyEventArgs press = Key(viewer, Avalonia.Input.Key.F9);
            viewer.RouteKeyForTests(press);
            Assert.True(press.Handled);
            Assert.False(Get<bool>(viewer, "_contextOpen"));
        }
        finally { ShortcutService.Publish(new ShortcutBindingsSection()); }
    });

    // SILENT NO-OP: D while the slideshow cannot start was consumed without a word; now the slideshow's own status says why.
    [Fact]
    public void TheSlideshowKeyThatCannotStartSaysWhy() => WithViewer(viewer =>
    {
        List<string> said = Explanations(viewer);
        KeyEventArgs press = Key(viewer, Avalonia.Input.Key.D);
        viewer.RouteKeyForTests(press);
        Assert.True(press.Handled);
        Assert.False(viewer.CanExecutePlaybackAction("gallery.slideshow-toggle"));
        Assert.False(string.IsNullOrWhiteSpace(Assert.Single(said)));
    });

    // SILENT NO-OP: T on a medium without a post; the menu entry is disabled with "no post", the key now says the same.
    [Fact]
    public void ShowInThreadOnAMediumWithoutAPostSaysSoAndOpensNothing() => WithViewer(viewer =>
    {
        List<string> said = Explanations(viewer);
        int opened = 0;
        viewer.ReadPostRequested += _ => opened++;
        Set(viewer, "_entry", LooseImage());
        KeyEventArgs press = Key(viewer, Avalonia.Input.Key.T);
        viewer.RouteKeyForTests(press);
        Assert.True(press.Handled);
        Assert.Equal(0, opened);
        Assert.Equal(MediaMenuReasons.NoPost, Assert.Single(said));
    });

    // SILENT NO-OP: the tag key while the medium's tags are unknown (the Tags row is disabled with that reason).
    [Fact]
    public void TheTagKeyWithoutTagsSaysWhy() => WithViewer(viewer =>
    {
        List<string> said = Explanations(viewer);
        Set(viewer, "_entry", LooseImage());
        // Primary is Command on macOS, Control on Windows/Linux; do not change the production shortcut to fit this fixture.
        KeyModifiers primary = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        KeyModifiers other = OperatingSystem.IsMacOS() ? KeyModifiers.Control : KeyModifiers.Meta;
        KeyEventArgs wrong = Key(viewer, Avalonia.Input.Key.T, other | KeyModifiers.Shift);
        viewer.RouteKeyForTests(wrong);
        Assert.False(wrong.Handled);
        Assert.Empty(said);

        KeyEventArgs press = Key(viewer, Avalonia.Input.Key.T, primary | KeyModifiers.Shift);
        viewer.RouteKeyForTests(press);
        Assert.True(press.Handled);
        Assert.Equal(MediaMenuReasons.TagsUnknown, Assert.Single(said));
    });

    [Theory]
    [InlineData(ShortcutPlatform.Windows, KeyModifiers.Control)]
    [InlineData(ShortcutPlatform.Linux, KeyModifiers.Control)]
    [InlineData(ShortcutPlatform.MacOS, KeyModifiers.Meta)]
    public void TheTagShortcutAndItsHelpUseThePlatformsPrimaryModifier(ShortcutPlatform platform, KeyModifiers primary)
    {
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(platform: platform);
        ShortcutGesture gesture = Assert.Single(bindings.For("media.tags.edit"));
        Assert.Equal("Primary+Shift+T", string.Join("+", gesture.Modifiers.Append(gesture.Key)));
        KeyGesture resolved = gesture.Resolve(platform);
        Assert.Equal(Avalonia.Input.Key.T, resolved.Key);
        Assert.Equal(primary | KeyModifiers.Shift, resolved.KeyModifiers);
        Assert.True(bindings.Matches(new KeyEventArgs { Key = resolved.Key, KeyModifiers = resolved.KeyModifiers }, "media.tags.edit", "gallery"));
        KeyModifiers other = primary == KeyModifiers.Meta ? KeyModifiers.Control : KeyModifiers.Meta;
        Assert.False(bindings.Matches(new KeyEventArgs { Key = resolved.Key, KeyModifiers = other | KeyModifiers.Shift }, "media.tags.edit", "gallery"));
        Assert.Equal(gesture.Display(platform), bindings.Display("media.tags.edit"));
        Assert.Equal(bindings.Display("media.tags.edit"), Assert.Single(CommandCatalog.TooltipGestures(
            "media.tags.edit", platform == ShortcutPlatform.MacOS, "gallery", bindings)));
    }

    // SILENT NO-OP: the "Rating: –" segment of the info line (and R) with neither stars nor tags available.
    [Fact]
    public void TheRatingSegmentWithNothingToOpenSaysWhy() => WithViewer(viewer =>
    {
        List<string> said = Explanations(viewer);
        Set(viewer, "_entry", LooseImage());
        Assert.False(viewer.CanRateAndTag);
        Get<Button>(viewer, "_ratingStatusButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(MediaMenuReasons.Unavailable, Assert.Single(said));
    });

    // Enabled row whose click returns early: "Save frame as image" while no video frame is ready.
    [Fact]
    public void TheSaveFrameRowIsEnabledOnlyWhileAFrameCanBeSaved() => WithViewer(viewer =>
    {
        Set(viewer, "_entry", LooseImage());
        Button saveFrame = Get<Button>(viewer, "_saveFrame");
        saveFrame.IsEnabled = true; // the state the row had for a video that was still loading or had failed
        typeof(GalleryViewer).GetMethod("RefreshOverflowItems", Fields)!.Invoke(viewer, null);
        Assert.False(saveFrame.IsEnabled);
    });

    // WRONG TARGET: the Menu key with the focus on a tool of the dock opened the media menu; the dock's own menu is the layout menu (GL3).
    [Fact]
    public void TheMenuKeyOnADockToolOpensTheLayoutMenuAndStaysTheMediaMenuElsewhere() => WithViewer(viewer =>
    {
        Assert.True(viewer.IsLayoutMenuKeySource(Get<Button>(viewer, "_fit")));
        Assert.True(viewer.IsLayoutMenuKeySource(viewer.OverflowButton));
        Assert.False(viewer.IsLayoutMenuKeySource(viewer));            // the picture: the media menu
        Assert.False(viewer.IsLayoutMenuKeySource(viewer.Filmstrip));  // a thumbnail: the shown medium's media menu
        Assert.False(viewer.IsLayoutMenuKeySource(null));

        // Sweep S2 F4: a control with a menu of its own gets the key before the layout and the media menu. The slideshow toggle is a dock tool
        // (IsLayoutMenuKeySource) but its own flyout comes first; chips and the speed button own a ContextMenu; the gallery's tools do not.
        ToggleButton toggle = Get<ToggleButton>(viewer, "_slideshowToggle");
        Assert.Same(toggle, viewer.ContextKeyOwner(toggle)); // its own flyout wins whether or not the windowless viewer counts it as a dock tool
        Button chip = new() { Name = "GalleryTagPanelAssignedChip", ContextMenu = new ContextMenu() };
        Assert.Same(chip, viewer.ContextKeyOwner(chip));
        Button speed = new() { Name = "GalleryVideoSpeed", ContextMenu = new ContextMenu() };
        Assert.Same(speed, viewer.ContextKeyOwner(speed));
        Assert.Null(viewer.ContextKeyOwner(Get<Button>(viewer, "_fit")));
        Assert.Null(viewer.ContextKeyOwner(viewer.OverflowButton));
        Assert.Null(viewer.ContextKeyOwner(viewer));
        Assert.Null(viewer.ContextKeyOwner(viewer.Filmstrip));
        Assert.Null(viewer.ContextKeyOwner(null));
    });

    // WRONG TARGET (sweep S2 F4): the Menu key and Shift+F10 on a tag chip, the slideshow toggle or the speed button opened the gallery's media or
    // layout menu instead of the control's own (the stamp menu, the slideshow settings, the speed choices). The key now raises the control's own
    // request (the one its right-click raises) exactly once, opens neither gallery menu, and its key-up is consumed once so Avalonia's
    // positionless request on the key-up cannot open a second menu over the first.
    [Fact]
    public void TheMenuKeyOnAControlWithItsOwnMenuOpensThatMenuOnce() => WithViewer(viewer =>
    {
        Set(viewer, "_entry", LooseImage()); // with a medium shown the old routing opened the media menu for these keys
        ToggleButton toggle = Get<ToggleButton>(viewer, "_slideshowToggle");
        Button chip = new() { Name = "GalleryTagPanelAssignedChip", ContextMenu = new ContextMenu() };
        Button speed = new() { Name = "GalleryVideoSpeed", ContextMenu = new ContextMenu() };
        MethodInfo keyUp = typeof(GalleryViewer).GetMethod("OnContextKeyUp", Fields)!;
        foreach (Control owner in new Control[] { chip, toggle, speed })
            foreach (var (key, modifiers) in new[] { (Avalonia.Input.Key.Apps, KeyModifiers.None), (Avalonia.Input.Key.F10, KeyModifiers.Shift) })
            {
                int requests = 0;
                EventHandler<ContextRequestedEventArgs> answer = (_, e) => { requests++; Assert.False(e.TryGetPosition(owner, out Avalonia.Point _)); e.Handled = true; };
                owner.AddHandler(Control.ContextRequestedEvent, answer, RoutingStrategies.Tunnel);
                try
                {
                    KeyEventArgs press = new() { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers, Source = owner };
                    viewer.RouteKeyForTests(press);
                    Assert.True(press.Handled);
                    Assert.Equal(1, requests);
                    Assert.Null(viewer.MediaMenu);
                    Assert.Null(viewer.LayoutContextMenu);

                    KeyEventArgs release = new() { RoutedEvent = InputElement.KeyUpEvent, Key = key, KeyModifiers = modifiers, Source = owner };
                    keyUp.Invoke(viewer, [owner, release]);
                    Assert.True(release.Handled); // no second, positionless request from Avalonia's key-up path
                    KeyEventArgs later = new() { RoutedEvent = InputElement.KeyUpEvent, Key = key, Source = owner };
                    keyUp.Invoke(viewer, [owner, later]);
                    Assert.False(later.Handled); // consumed once, not every later key-up of that key
                }
                finally { owner.RemoveHandler(Control.ContextRequestedEvent, answer); }
            }
    });

    // A key-up is consumed only for the context key the gallery handled itself: another key-down in between ends the wait, so a later Menu key
    // that a text field or another control answers keeps its key-up.
    [Fact]
    public void AnotherKeyEndsTheWaitForTheContextKeyUp() => WithViewer(viewer =>
    {
        Button chip = new() { Name = "GalleryTagPanelAssignedChip", ContextMenu = new ContextMenu() };
        chip.AddHandler(Control.ContextRequestedEvent, (_, e) => e.Handled = true, RoutingStrategies.Tunnel);
        MethodInfo keyUp = typeof(GalleryViewer).GetMethod("OnContextKeyUp", Fields)!;
        viewer.RouteKeyForTests(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Apps, Source = chip });
        viewer.RouteKeyForTests(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Avalonia.Input.Key.LeftShift, Source = chip });
        KeyEventArgs afterModifier = new() { RoutedEvent = InputElement.KeyUpEvent, Key = Avalonia.Input.Key.Apps, Source = chip };
        keyUp.Invoke(viewer, [chip, afterModifier]);
        Assert.True(afterModifier.Handled); // a modifier (the Shift of Shift+F10) does not end it

        viewer.RouteKeyForTests(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Apps, Source = chip });
        viewer.RouteKeyForTests(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Avalonia.Input.Key.F13, Source = chip });
        KeyEventArgs afterKey = new() { RoutedEvent = InputElement.KeyUpEvent, Key = Avalonia.Input.Key.Apps, Source = chip };
        keyUp.Invoke(viewer, [chip, afterKey]);
        Assert.False(afterKey.Handled);
    });

    // The real owners are what the rule sees: the stamp menu sits on the chip as its ContextMenu, the speed choices on the speed button, and the
    // toggle's request opens the slideshow flyout; the gallery routes through the owner before the layout and the media menu.
    [Fact]
    public void TheRealChipSpeedAndToggleCarryTheirOwnMenus()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory != null && !File.Exists(Path.Combine(directory, "MeowChan.csproj"))) directory = Path.GetDirectoryName(directory);
        string Source(string path) => File.ReadAllText(Path.Combine(directory!, path));
        Assert.Contains("chip.ContextMenu = menu;", Source("Desktop/Controls/QuickTagSurface.cs"));
        Assert.Contains("_speed.ContextMenu = _speedMenu;", Source("Desktop/GalleryVideoPlayer.Transport.cs"));
        Assert.Contains("_slideshowToggle.ContextRequested +=", Source("Desktop/GalleryViewer.SlideshowMenu.cs"));
        string viewer = Source("Desktop/GalleryViewer.cs");
        int own = viewer.IndexOf("ContextKeyOwner(e.Source)", StringComparison.Ordinal);
        int layout = viewer.IndexOf("IsLayoutMenuKeySource(e.Source) && ShowLayoutContextMenu()", StringComparison.Ordinal);
        Assert.True(own > 0 && layout > own);
        Assert.Contains("AddHandler(KeyUpEvent, OnContextKeyUp, RoutingStrategies.Tunnel, handledEventsToo: true)", viewer);
        Assert.Contains("_nativeVideoOverlay.AddHandler(KeyUpEvent, OnContextKeyUp, RoutingStrategies.Tunnel, handledEventsToo: true)", viewer);
    }

    [Fact]
    public void MouseTransportActionsReturnShortcutOwnershipToTheGallery()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory != null && !File.Exists(Path.Combine(directory, "MeowChan.csproj"))) directory = Path.GetDirectoryName(directory);
        string player = File.ReadAllText(Path.Combine(directory!, "Desktop", "GalleryVideoPlayer.cs"));
        string viewer = File.ReadAllText(Path.Combine(directory!, "Desktop", "GalleryViewer.cs"));
        Assert.Contains("MouseTransportActionCompleted", player);
        Assert.Contains("e.Pointer.Type == PointerType.Mouse", player);
        Assert.Contains("DispatcherPriority.Input", player);
        Assert.Contains("_video.MouseTransportActionCompleted += FocusGallerySurface", viewer);
        // Mouse mute/loop clicks must not leave Enter on the toggle; keyboard Tab/Enter remains native.
        Assert.Contains("Keyboard activation retain the control's native focus", player);
    }

    // Quote chips of the post panel present "Show quoted post" like the reader's quote buttons, so a key bound to it opens the focused chip.
    [Fact]
    public void QuoteChipsCarryTheShowQuotedPostCommand()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory != null && !File.Exists(Path.Combine(directory, "MeowChan.csproj"))) directory = Path.GetDirectoryName(directory);
        string viewer = File.ReadAllText(Path.Combine(directory!, "Desktop", "GalleryViewer.cs"));
        Assert.Contains("CommandBindings.BindExisting(chip, \"reader.open-quote\")", viewer);
        Assert.Contains("Matches(e, \"gallery.context-close\")", viewer);
    }
}
