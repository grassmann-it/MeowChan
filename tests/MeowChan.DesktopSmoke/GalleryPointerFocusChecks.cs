using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;

namespace MeowChan.DesktopSmoke;

/// <summary>
/// One rule for chrome over keyed surfaces (<c>PointerFocusReclaim</c>): after a mouse, pen or touch activation of a gallery or video-bar control the keys
/// are the surface's again, so the next Space plays or pauses (and never presses the clicked button once more: the favourite star un-favourited, mute
/// toggled); keyboard activation keeps the focus on the control. The pointer is raised through the routed press and release of the real controls, with the
/// focus a Button takes when it is pressed (a synthetic route does not move it); the focus and the key are then read and sent through the window's own
/// focus manager and the focused element, as a person's keyboard would reach them.
/// </summary>
internal static class GalleryPointerFocusChecks
{
    internal static async Task FlushAsync() => await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

    internal static IInputElement? Focused(TopLevel window) => window.FocusManager?.GetFocusedElement();

    /// <summary>A pointer press and release on <paramref name="target"/>, with the focus a pressed Button takes; the reclaim's own deferred step has run when this returns.</summary>
    internal static async Task ClickAsync(Window window, Control target, PointerType type = PointerType.Mouse)
    {
        Point center = CenterIn(window, target);
        // A Button raises Click on the release only where the window's hit test finds it under the pointer, and the window hit-tests the
        // renderer's last frame: a control that was just shown or moved (the docked toolbar after ConfigurePlayback) is clickable once a frame
        // has it, as for a person's mouse.
        await Program.WaitUntilAsync(() =>
            {
                window.UpdateLayout();
                center = CenterIn(window, target);
                return window.InputHitTest(center) is Visual hit && (ReferenceEquals(hit, target) || target.IsVisualAncestorOf(hit));
            },
            () => "The pointer does not reach " + target.Name + ": the window hits " + Describe(window.InputHitTest(center)) + " at " + center
                + " in a client of " + window.ClientSize + "; target bounds " + target.Bounds + ", viewer bounds " + (window.Content as Control)?.Bounds);
        Require(target.Focus(NavigationMethod.Pointer), "The pressed control did not take the focus: " + target.Name);
        using Avalonia.Input.Pointer pointer = new(Avalonia.Input.Pointer.GetNextFreeId(), type, true);
        target.RaiseEvent(new PointerPressedEventArgs(target, pointer, window, center, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));
        target.RaiseEvent(new PointerReleasedEventArgs(target, pointer, window, center, 1,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
        pointer.Capture(null);
        await FlushAsync();
    }

    /// <summary>Only the release of a pointer on <paramref name="target"/> (a slider is not moved by it), with the focus its press would have given it.</summary>
    internal static async Task ReleaseAsync(Window window, Control target)
    {
        Point center = CenterIn(window, target);
        Require(target.Focus(NavigationMethod.Pointer), "The pressed control did not take the focus: " + target.Name);
        using Avalonia.Input.Pointer pointer = new(Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, true);
        target.RaiseEvent(new PointerReleasedEventArgs(target, pointer, window, center, 1,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
        await FlushAsync();
    }

    /// <summary>Space, down and up, on the element that has the focus now: the key's real source.</summary>
    internal static async Task SpaceAsync(TopLevel window)
    {
        IInputElement focus = Focused(window) ?? throw new InvalidOperationException("Nothing has the focus for Space.");
        focus.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space, Source = focus });
        focus.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.Space, Source = focus });
        await FlushAsync();
    }

    private static Point CenterIn(Visual root, Control target) =>
        target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), root)
        ?? throw new InvalidOperationException("The control has no position in the window: " + target.Name);

    /// <summary>The favourite star and badge of the gallery, the way the review found them: a mouse click, then Space.</summary>
    internal static async Task CheckFavoriteControlsAsync(MainWindow window, GalleryViewer viewer, Func<bool> saved, Func<Task> settled)
    {
        viewer.ConfigurePlayback(new GalleryPlaybackPreferences { OverlayControls = false, AutoHideControls = false, MediaFocus = false });
        ToggleButton star = Named<ToggleButton>(viewer, "GalleryFavorite");
        await Program.WaitUntilAsync(() => star.IsEffectivelyVisible && star.IsEnabled && star.Focusable);
        viewer.FocusGallerySurface();
        bool start = saved();
        int clicks = 0;
        void Count(object? _, RoutedEventArgs __) => clicks++;
        star.Click += Count;
        try
        {
            await ClickAsync(window, star);
            await settled();
            Require(clicks == 1 && saved() != start, $"A mouse click on the toolbar star did not toggle the favourite once: clicks {clicks}, saved {start} -> {saved()}, hit {Describe(window.InputHitTest(CenterIn(window, star)))}, pressed {star.IsPressed}.");
            Require(ReferenceEquals(Focused(window), viewer), "A mouse click on the toolbar star left the keys on the star: " + Describe(Focused(window)));
            await SpaceAsync(window);
            await settled();
            Require(clicks == 1 && saved() != start, "Space after the mouse click pressed the star again (the favourite was toggled back).");

            ToggleButton badge = Named<ToggleButton>(viewer, "GalleryFavoriteIndicator");
            if (badge.IsEffectivelyVisible && badge.IsEnabled && badge.Focusable && badge.IsHitTestVisible)
            {
                int badgeClicks = 0;
                void CountBadge(object? _, RoutedEventArgs __) => badgeClicks++;
                badge.Click += CountBadge;
                try
                {
                    bool before = saved();
                    await ClickAsync(window, badge);
                    await settled();
                    Require(badgeClicks == 1 && saved() != before, "A mouse click on the favourite badge did not toggle the favourite once.");
                    Require(ReferenceEquals(Focused(window), viewer), "A mouse click on the favourite badge left the keys on the badge: " + Describe(Focused(window)));
                    bool after = saved();
                    await SpaceAsync(window);
                    await settled();
                    Require(badgeClicks == 1 && saved() == after, "Space after a click on the favourite badge un-favourited (pressed the badge again).");
                }
                finally { badge.Click -= CountBadge; }
            }

            // Keyboard activation is native: Tab to the star, Space presses it, and the focus stays there.
            bool beforeKey = saved();
            Require(star.Focus(NavigationMethod.Tab), "The star could not be focused by keyboard.");
            await SpaceAsync(window);
            await settled();
            Require(saved() != beforeKey, "Space on a keyboard-focused star did not activate it.");
            Require(ReferenceEquals(Focused(window), star), "Keyboard activation moved the focus off the star: " + Describe(Focused(window)));
        }
        finally { star.Click -= Count; }
    }

    /// <summary>The transport of a shown video: a click on a toolbar toggle and on a transport button, the volume slider after its release, then Space.</summary>
    internal static async Task CheckVideoControlsAsync(MainWindow window, GalleryViewer viewer)
    {
        window.Activate();
        await Program.WaitUntilAsync(() => window.IsActive);
        GalleryVideoPlayer player = viewer.GetGalleryVisualDescendants().OfType<GalleryVideoPlayer>().Single();
        viewer.ConfigurePlayback(new GalleryPlaybackPreferences { OverlayControls = false, AutoHideControls = false, MediaFocus = false });
        Button play = Named<Button>(viewer, "GalleryVideoPlayPause");
        await Program.WaitUntilAsync(() => play.IsEnabled && play.IsEffectivelyVisible);
        viewer.FocusGallerySurface();

        // A transport button: the click plays or pauses, the next Space does the opposite (it reaches the gallery's playback, not the button).
        VideoPlaybackMode start = player.PlaybackMode;
        await ClickAsync(window, play);
        await Program.WaitUntilAsync(() => player.PlaybackMode != start);
        VideoPlaybackMode clicked = player.PlaybackMode;
        Require(ReferenceEquals(Focused(window), viewer), "A mouse click on play/pause left the keys on the button: " + Describe(Focused(window)));
        await SpaceAsync(window);
        await Program.WaitUntilAsync(() => player.PlaybackMode != clicked);

        // A toolbar button of the gallery itself: Space plays or pauses and never presses the toggle again.
        ToggleButton context = Named<ToggleButton>(viewer, "GalleryToggleContext");
        await Program.WaitUntilAsync(() => context.IsEffectivelyVisible && context.IsEnabled);
        bool contextOpen = viewer.CaptureState().ContextOpen;
        VideoPlaybackMode beforeSpace = player.PlaybackMode;
        await ClickAsync(window, context);
        await Program.WaitUntilAsync(() => viewer.CaptureState().ContextOpen != contextOpen);
        Require(ReferenceEquals(Focused(window), viewer), "A mouse click on a toolbar button left the keys on the button: " + Describe(Focused(window)));
        await SpaceAsync(window);
        await Program.WaitUntilAsync(() => player.PlaybackMode != beforeSpace);
        Require(viewer.CaptureState().ContextOpen != contextOpen, "Space after a toolbar click pressed the toolbar button again.");
        await ClickAsync(window, context); // and back
        await Program.WaitUntilAsync(() => viewer.CaptureState().ContextOpen == contextOpen);

        // The volume slider (the popup above the speaker): after its release the keys are the gallery's, not the mute button's (GA-02).
        player.SetVolumePopupOpen(true);
        Slider volume = Named<Slider>(viewer, "GalleryVideoVolume");
        if (volume.IsEffectivelyVisible)
        {
            bool muted = player.Muted;
            VideoPlaybackMode beforeVolume = player.PlaybackMode;
            await ReleaseAsync(window, volume);
            Require(ReferenceEquals(Focused(window), viewer), "After the volume slider the keys are not the gallery's: " + Describe(Focused(window)));
            player.SetVolumePopupOpen(false);
            await FlushAsync();
            Require(ReferenceEquals(Focused(window), viewer), "Closing the volume popup handed the keys to the mute button: " + Describe(Focused(window)));
            await SpaceAsync(window);
            await Program.WaitUntilAsync(() => player.PlaybackMode != beforeVolume);
            Require(player.Muted == muted, "Space after the volume slider toggled mute.");
        }
        else player.SetVolumePopupOpen(false);

        // Keyboard activation keeps the focus on the control: Tab to play/pause, Space presses it, the focus stays.
        VideoPlaybackMode beforeKey = player.PlaybackMode;
        Require(play.Focus(NavigationMethod.Tab), "The play button could not be focused by keyboard.");
        await SpaceAsync(window);
        await Program.WaitUntilAsync(() => player.PlaybackMode != beforeKey);
        Require(ReferenceEquals(Focused(window), play), "Keyboard activation moved the focus off the play button: " + Describe(Focused(window)));
        viewer.FocusGallerySurface();
        Console.WriteLine("SMOKE gallery pointer focus: mouse click on the transport, a toolbar toggle and the volume slider returns the keys to the picture (Space plays or pauses, never presses the control again); keyboard activation keeps the focus on the control.");
    }

    private static string Describe(IInputElement? element) => element is Control control ? (control.Name ?? control.GetType().Name) : element?.GetType().Name ?? "nothing";

    private static T Named<T>(GalleryViewer viewer, string name) where T : Control =>
        viewer.GetGalleryVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
