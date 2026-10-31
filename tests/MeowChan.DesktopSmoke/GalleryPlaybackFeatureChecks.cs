using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Storage;
using MeowChan.Testing;
using SkiaSharp;

namespace MeowChan.DesktopSmoke;

// Invoke from the parent's isolated video-fixture scenario, not a personal desktop.
// Uses real verified PNG/WebM reads and the ordinary viewer/MoveRequested boundary.
// Native Windows video overlays and MP4/audio acceptance are deliberately not claimed.
internal static partial class GalleryPlaybackFeatureChecks
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ImageObservation = TimeSpan.FromMilliseconds(1350);

    internal static async Task RunAsync(MainWindow owner)
    {
        if (!OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_NATIVE_ALLOWED") != "1")
            throw new PlatformNotSupportedException("Feature smoke requires an explicitly authorized disposable native desktop.");
        if (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_XVFB") != "1")
            throw new InvalidOperationException("MU feature smoke requires the parent's private Xvfb display on Linux, never a personal display.");
        string video = Environment.GetEnvironmentVariable("MEOWCHAN_VIDEO_FIXTURE")
            ?? throw new InvalidOperationException("MU feature smoke requires a real synthetic WebM in MEOWCHAN_VIDEO_FIXTURE; supply the existing pinned playback fixture.");
        Require(File.Exists(video), "The supplied real video fixture does not exist.");
        string directory = TemporaryWorkspacePaths.CreateDirectory("meowchan-playback-features-");
        string language = Texts.Language;
        try
        {
            GalleryEntry first = await ImageAsync(directory, "first", SKColors.DarkOrange);
            GalleryEntry last = await ImageAsync(directory, "last", SKColors.SteelBlue);
            GalleryEntry clip = await AssetAsync(directory, "clip", "clip" + Path.GetExtension(video), await File.ReadAllBytesAsync(video));
            Require(clip.Kind == SavedMediaKind.Video, "The mixed fixture has no classified real video.");
            // The edge/volume suite cannot use the historically silent default clip.
            // Retain silent-video coverage above and require pinned synthetic audio here.
            byte[] edgeAudio = await File.ReadAllBytesAsync(FindSyntheticAudioFixture(video));
            Require(Convert.ToHexString(SHA256.HashData(edgeAudio)).Equals(
                "a88f7ec3fa76c11c38719662ba41d5538a5775cc58afce5bf32e14e0cd5d1ad8", StringComparison.OrdinalIgnoreCase),
                "Edge volume checks require the pinned synthetic VP8/Vorbis fixture.");
            GalleryEntry edgeClip = await AssetAsync(directory, "edge-audio", "edge-audio.webm", edgeAudio);
            if (Environment.GetEnvironmentVariable("MEOWCHAN_NATIVE_OVERLAY_VISIBILITY_ONLY") == "1")
            { await CheckNativeOverlayVisibilityAsync(owner); return; }
            if (OperatingSystem.IsWindows()) await CheckNativeOverlayVisibilityAsync(owner);
            if (Environment.GetEnvironmentVariable("MEOWCHAN_GALLERY_EDGE_ONLY") == "1")
            { await CheckEdgeHoverAsync(owner, edgeClip); return; }
            await CheckFirstNavigationAsync(owner, first, last);
            await CheckSavedSlideshowAutoplayAsync(owner, clip, first);
            await CheckNativePlaybackIntentAsync(owner, directory, video, first);
            CheckPairedPreferenceNormalization();
            await CheckSavedSlideshowAutoplayPolicyAsync(owner, first, clip);
            GalleryPlaybackPreferences remembered = await CheckRememberedChoicesAsync(owner, directory, first);
            await CheckOverlayAsync(owner, first, remembered);
            await CheckMixedSlideshowAsync(owner, new[] { first, clip, last });
            await CheckStartAfterEndedAsync(owner, clip, last);
            await CheckDecodeFailureStopsAsync(owner, directory, first);
            await CheckRepeatSwitchKeepsPlaybackAsync(owner, clip);
            await CheckEdgeHoverAsync(owner, edgeClip);
            Console.WriteLine("SMOKE gallery playback features: profile-backed viewer recreation without automatic progression; first/manual video autoplay independent of saved mode; paired overlay/auto-hide and focus/keyboard/docked controls; verified image -> real WebM end -> image; interaction gate, terminal stop and explicit replay after actual video end passed. Only this recorded platform/fixture is accepted; physical audio/GPU remain separate.");
        }
        finally
        {
            Texts.SetLanguage(language);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void CheckPairedPreferenceNormalization()
    {
        GalleryPlaybackPreferences defaults = new();
        Require(!defaults.OverlayControls && !defaults.AutoHideControls,
            "The DTO default is not coherent docked/auto-hide OFF.");
        foreach (bool overlay in new[] { false, true })
        foreach (bool autoHide in new[] { false, true })
        {
            GalleryPlaybackPreferences legacy = new(GalleryPlaybackMode.Slideshow, 17, true, overlay, autoHide, true);
            GalleryPlaybackPreferences normalized = legacy.Normalize();
            Require(normalized == (legacy with { AutoHideControls = overlay }),
                "Legacy normalization must use OverlayControls as authority, including dormant auto-hide=true.");
            Require(normalized.Normalize() == normalized, "Paired preference normalization is not idempotent.");
            foreach (bool enabled in new[] { false, true })
                Require(legacy.WithControlsOverlay(enabled) == (legacy with { OverlayControls = enabled, AutoHideControls = enabled }),
                    "Paired choice lost unrelated mode/duration/focus/post fields.");
            Config config = new() { GalleryOverlayControls = overlay, GalleryAutoHideControls = autoHide };
            GalleryPlaybackPreferences loaded = GalleryPlaybackPreferences.FromConfig(config);
            Require(loaded.OverlayControls == overlay && loaded.AutoHideControls == overlay,
                "Config-to-viewer projection resurrected an independent legacy auto-hide choice.");
        }
    }

    private static async Task CheckSavedSlideshowAutoplayPolicyAsync(MainWindow owner, GalleryEntry image, GalleryEntry clip)
    {
        foreach (bool autoplay in new[] { true, false })
        {
            await using Fixture fixture = new(owner);
            await fixture.ReadyAsync();
            GalleryViewer viewer = fixture.Viewer;
            viewer.Configure(contextOpen: false, showShortcutHelp: true, videoAutoplay: autoplay, videoStartMuted: true, videoVolume: 0);
            viewer.ConfigurePlayback(new(GalleryPlaybackMode.Slideshow, 1));
            int moves = 0;
            viewer.MoveRequested += _ => moves++;
            foreach (bool manualSelection in new[] { false, true })
            {
                if (manualSelection) await fixture.ShowAsync(image); // Setup a subsequent manual selection, not Start.
                await viewer.ShowAsync(clip, CancellationToken.None).WaitAsync(Deadline);
                GalleryVideoPlayer player = viewer.GetGalleryVisualDescendants().OfType<GalleryVideoPlayer>().Single();
                await UntilAsync(async () =>
                {
                    var state = await player.InspectPlaybackAsync();
                    return state.HasPlayback && (autoplay ? state.State == "Playing" && state.Time > 100
                        && MediaInteractionSmokeHelpers.HasDecodedFrames(state) : state.State is "NothingSpecial" or "Paused" or "Stopped" && state.Time <= 120
                            && state.DecodedFrames == 0);
                }, "Saved Slideshow suppressed configured video autoplay or started playback with autoplay OFF.");
                Require(!Running(viewer) && moves == 0, "Autoplay or manual video selection started automatic progression.");
                if (autoplay) await MediaInteractionSmokeHelpers.HoldStateAsync(player, "Playing", "Saved Slideshow autoplay");
                else
                {
                    var before = await player.InspectPlaybackAsync();
                    await Task.Delay(650);
                    var after = await player.InspectPlaybackAsync();
                    Require(after.State is "NothingSpecial" or "Paused" or "Stopped"
                        && Math.Abs(after.Time - before.Time) <= 120 && after.DecodedFrames == 0,
                        "Autoplay-OFF decoder advanced/decoded without an explicit Play/Start.");
                }
                await HoldAsync(ImageObservation, () => Require(!Running(viewer) && moves == 0,
                    "Remembered Slideshow mode started automatic next without explicit Start."));
                Console.WriteLine($"SMOKE saved Slideshow autoplay={autoplay}, manualSelection={manualSelection}: no automatic progression.");
            }
        }
    }

    private static async Task<GalleryPlaybackPreferences> CheckRememberedChoicesAsync(MainWindow owner, string directory, GalleryEntry image)
    {
        GalleryPlaybackPreferences? changed = null;
        GalleryPlaybackPreferences expected = new(GalleryPlaybackMode.Slideshow, 1, true, true, true, true);
        await using (Fixture fixture = new(owner))
        {
            await fixture.ReadyAsync();
            int notifications = 0;
            fixture.Viewer.PlaybackPreferencesChanged += preferences => { notifications++; changed = preferences; };
            fixture.Viewer.ConfigurePlayback(new(ImageSeconds: 1));
            Require(notifications == 0, "Applying stored preferences emitted a user-change/save event.");
            await fixture.ShowAsync(image);
            try
            {
                var section = owner.SessionHost.Config.ShortcutBindings.Set("gallery.overlay-toggle", [ShortcutGesture.Parse("Primary+Shift+F6")]);
                ShortcutService.Publish(section);
                Click(Find<Button>(fixture.Viewer, "GalleryToolsToggle")); // Secondary overlay button now hidden.
                fixture.Viewer.Focus();
                using MediaNativeInput native = new(fixture.Window);
                await native.KeyAsync(MediaNativeInput.F6, modified: true);
                Require(changed?.OverlayControls == true && changed.AutoHideControls, "Configured native shortcut did not enable the paired overlay action.");
                await native.KeyAsync(MediaNativeInput.F6, modified: true);
                Require(changed?.OverlayControls == false && !changed.AutoHideControls, "Repeated native shortcut did not disable both halves of the same choice.");
                Click(Find<Button>(fixture.Viewer, "GalleryToolsToggle"));
                Require(ToolTip.GetTip(Find<Button>(fixture.Viewer, "GalleryOverlayToggle"))?.ToString()?.Contains("F6") == true,
                    "Live shortcut change is missing from the effective tooltip.");
                fixture.Viewer.SetInteractionAvailable(false);
                await fixture.Viewer.ExecutePlaybackActionAsync("gallery.slideshow-toggle");
                Require(!Running(fixture.Viewer), "Blocked shortcut executor started slideshow.");
                fixture.Viewer.SetInteractionAvailable(true);
            }
            finally { ShortcutService.Publish(owner.SessionHost.Config.ShortcutBindings); }
            ComboBox mode = Find<ComboBox>(fixture.Viewer, "GalleryPlaybackModeSelector");
            Require(mode.ItemCount == 3, "Playback modes must be the exclusive Manual/Repeat/Slideshow choices.");
            mode.SelectedIndex = (int)GalleryPlaybackMode.Repeat;
            Require(changed?.Mode == GalleryPlaybackMode.Repeat, "Selecting Repeat did not publish the matching preference.");
            mode.SelectedIndex = (int)GalleryPlaybackMode.Slideshow;
            Require(changed?.Mode == GalleryPlaybackMode.Slideshow, "Selecting Slideshow did not publish the matching preference.");
            Click(Find<Button>(fixture.Viewer, "GalleryOverlayToggle"));
            Require(changed?.OverlayControls == true && changed.AutoHideControls, "The visible controls choice did not publish paired ON.");
            RequireSingleControlsChoice(fixture.Viewer);
            // Retain both command IDs as aliases, not a second independent visible choice.
            await fixture.Viewer.ExecutePlaybackActionAsync("gallery.overlay-autohide-toggle");
            Require(changed?.OverlayControls == false && !changed.AutoHideControls, "Legacy auto-hide alias did not publish paired OFF.");
            await fixture.Viewer.ExecutePlaybackActionAsync("gallery.overlay-autohide-toggle");
            Require(changed?.OverlayControls == true && changed.AutoHideControls, "Legacy auto-hide alias did not publish paired ON from OFF.");
            Click(Find<Button>(fixture.Viewer, "GalleryPostOverlayToggle"));
            Require(changed?.OverlayPostContext == true, "Post overlay did not publish its own preference.");
            Click(Find<Button>(fixture.Viewer, "GalleryToolsToggle"));
            Require(changed == expected, "UI preference changes lost another playback/display field.");
            Require(notifications >= 7, "Not all explicit UI changes reached the preference owner.");
        }
        string profile = Path.Combine(directory, "profile");
        Directory.CreateDirectory(profile);
        Config saved = new() { DownloadPath = Path.Combine(profile, "media"), AutomaticUpdateChecks = false };
        changed!.ApplyTo(saved);
        saved.SaveConfig(profile);
        GalleryPlaybackPreferences loaded = GalleryPlaybackPreferences.FromConfig(Config.LoadConfig(profile));
        Require(loaded == expected, "Profile save/reload changed a viewer preference.");
        await using (Fixture reopened = new(owner))
        {
            await reopened.ReadyAsync();
            int writes = 0, moves = 0;
            reopened.Viewer.PlaybackPreferencesChanged += _ => writes++;
            reopened.Viewer.MoveRequested += _ => moves++;
            reopened.Viewer.ConfigurePlayback(loaded);
            await reopened.ShowAsync(image);
            AssertChoices(reopened.Viewer, loaded);
            Require(writes == 0, "Recreating a configured viewer dirtied stored preferences.");
            Require(!Running(reopened.Viewer), "A remembered Slideshow mode automatically started playback.");
            Require(Find<TextBlock>(reopened.Viewer, "GallerySlideshowStatus").Text?.Contains("Ready") == true,
                "A never-started remembered mode did not explain readiness.");
            reopened.Viewer.ConfigurePlayback(loaded); // Reapplying the same saved mode is not Start either.
            Require(!Running(reopened.Viewer), "Reapplying configured Slideshow mode became a Start request.");
            await HoldAsync(ImageObservation, () => Require(moves == 0 && !Running(reopened.Viewer),
                "A configured, never-started slideshow advanced after the saved image duration."));
        }
        return loaded;
    }

    private static async Task CheckOverlayAsync(MainWindow owner, GalleryEntry image, GalleryPlaybackPreferences remembered)
    {
        await using Fixture fixture = new(owner);
        await fixture.ReadyAsync();
        GalleryViewer viewer = fixture.Viewer;
        int moves = 0, closes = 0;
        viewer.MoveRequested += _ => moves++;
        viewer.CloseRequested += () => closes++;
        GalleryPlaybackPreferences docked = remembered with
        { Mode = GalleryPlaybackMode.Manual, MediaFocus = false, OverlayPostContext = false };
        docked = docked.WithControlsOverlay(false);
        viewer.ConfigurePlayback(docked);
        await fixture.ShowAsync(image);
        Grid viewport = Find<Grid>(viewer, "GalleryViewport");
        Border chrome = Find<Border>(viewer, "GalleryOverlayControls");
        double dockedHeight = viewport.Bounds.Height;
        // The full toolbar is intentionally retained: OverlayControls and MediaFocus are
        // independent preferences. A checked request with docked effective placement is a failure
        // for this verified image (there is no native-video transparency fallback to excuse it).
        viewer.ConfigurePlayback(docked.WithControlsOverlay(true));
        viewer.Focus();
        await LayoutAsync(fixture.Window);
        ToggleButton focus = Find<ToggleButton>(viewer, "GalleryToolsToggle");
        ToggleButton overlay = Find<ToggleButton>(viewer, "GalleryOverlayToggle");
        RequireSingleControlsChoice(viewer);
        bool effectiveOverlay = chrome.GetVisualAncestors().OfType<Control>().Any(parent => parent.Name == "GalleryOverlayFooter");
        Console.WriteLine("SMOKE feature requested/effective overlay: " + JsonSerializer.Serialize(new
        {
            requestedOverlay = true, requestedMediaFocus = false, projectedOverlay = overlay.IsChecked,
            projectedMediaFocus = focus.IsChecked, effectiveOverlay, pairedChoice = overlay.IsChecked,
            dockedHeight, effectiveMediaHeight = viewport.Bounds.Height,
            nativeFallbackVisible = Find<Control>(viewer, "GalleryOverlayFallbackNotice").IsEffectivelyVisible
        }));
        Require(focus.IsChecked == false && overlay.IsChecked == true,
            "Overlay/auto-hide incorrectly requires MediaFocus, or its requested choice was not retained. See requested/effective overlay evidence.");
        Require(effectiveOverlay && viewport.Bounds.Height > dockedHeight + 20,
            "Requested OverlayControls=true with MediaFocus=false is not an effective overlay: chrome remains docked or still reserves media height.");
        Require(!Find<Control>(viewer, "GalleryOverlayFallbackNotice").IsEffectivelyVisible,
            "A verified image reported an unsupported-native fallback instead of honoring independent overlay controls.");
        viewer.ConfigurePlayback(docked.WithControlsOverlay(false));
        viewer.Focus();
        await LayoutAsync(fixture.Window);
        await HoldAsync(TimeSpan.FromSeconds(4), () => Require(Visible(chrome) && focus.IsChecked == false && overlay.IsChecked == false,
            "Paired OFF failed to retain visible docked controls with MediaFocus=false."));
        viewer.ConfigurePlayback(docked.WithControlsOverlay(true));
        object? pixels = Find<Image>(viewer, "GalleryImage").Source;
        int decodes = fixture.Decodes;
        using MediaNativeInput native = new(fixture.Window);
        foreach (var presentation in new[] { (Language: "en", Theme: ThemeVariant.Light, Width: 1000d), (Language: "de", Theme: ThemeVariant.Dark, Width: 760d) })
        {
            Texts.SetLanguage(presentation.Language);
            fixture.Window.RequestedThemeVariant = presentation.Theme;
            fixture.Window.Width = presentation.Width;
            await LayoutAsync(fixture.Window);
            viewer.Focus();
            await UntilAsync(() => !Visible(chrome), "Idle edge overlay did not collapse with MediaFocus=false.");
            Require(focus.IsChecked == false && overlay.IsChecked == true,
                "Effective auto-hide silently enabled MediaFocus or lost its independently requested overlay.");
            Require(!chrome.IsEffectivelyVisible || !chrome.IsHitTestVisible,
                "An invisible overlay still intercepts pointer input.");
            Button controlsIndicator = RequireEdgeIndicator(viewer, "GalleryControlsEdgeIndicator");
            Require(!viewer.GetGalleryVisualDescendants().OfType<Control>().Any(c => c.Name == "GalleryPostEdgeIndicator" && Visible(c)),
                "A disabled post overlay exposed a misleading edge indicator.");
            // Keyboard focus is setup, not input proof. Activation is a real OS Space press.
            Require(controlsIndicator.Focus(NavigationMethod.Tab), "Controls edge indicator is not keyboard focusable.");
            await native.KeyAsync(MediaNativeInput.Space);
            await UntilAsync(() => Visible(chrome), "Keyboard activation of controls indicator did not expose the controls.");
            viewer.Focus();
            await native.MoveAsync(viewer, new Point(viewer.Bounds.Width * .45, viewer.Bounds.Height * .30));
            await UntilAsync(() => !Visible(chrome), "Controls did not collapse after leaving the activated edge indicator.");
            await native.KeyAsync(MediaNativeInput.Tab); // Explicit keyboard access, not reveal-only navigation/modifiers.
            await UntilAsync(() => Visible(chrome), "Tab keyboard access could not reveal the hidden controls.");
            RequireSingleOverlayChoice(viewer); // Actual expanded UI: exactly one visible, actionable choice.
            Require(moves == 0 && closes == 0 && viewer.CaptureState().Key == image.Key,
                "Merely revealing controls performed a media/navigation action.");
            Button close = Find<Button>(viewer, "GalleryClose");
            Point? closePosition = close.TranslatePoint(default, viewer);
            Require(closePosition.HasValue && closePosition.Value.X >= 0 && closePosition.Value.Y >= 0
                && closePosition.Value.X + close.Bounds.Width <= viewer.Bounds.Width + 1
                && closePosition.Value.Y + close.Bounds.Height <= viewer.Bounds.Height + 1, "Overlay Exit is clipped outside the usable viewport.");
            Require(close.IsEffectivelyVisible && close.Focusable && !string.IsNullOrWhiteSpace(AutomationProperties.GetName(close)),
                "Overlay Exit is not visible, keyboard reachable and accessibly named.");
            ComboBox mode = Find<ComboBox>(viewer, "GalleryPlaybackModeSelector");
            Require(mode.Focus(NavigationMethod.Tab), "Could not focus an overlay control.");
            mode.IsDropDownOpen = true;
            await HoldAsync(TimeSpan.FromSeconds(4), () => Require(Visible(chrome) && mode.IsDropDownOpen,
                "Auto-hide removed a focused control or an open playback menu."));
            await native.KeyAsync(MediaNativeInput.Escape);
            Require(!mode.IsDropDownOpen && closes == 0, "Escape in the playback popup closed the gallery or failed to close its popup.");
            Require(ReferenceEquals(pixels, Find<Image>(viewer, "GalleryImage").Source) && fixture.Decodes == decodes,
                "Overlay idle/reveal, focus or locale changes restarted the real verified image decode.");
            Program.CaptureWindow(fixture.Window, $"smoke-gallery-feature-overlay-{presentation.Language}-{presentation.Theme}.png");
        }
        fixture.Window.Width = 1000;
        viewer.ConfigurePlayback(docked with { OverlayPostContext = true });
        // Native resize is asynchronous, especially at non-unit scale. A dispatcher
        // layout alone can still describe the previous 760-DIP window.
        await UntilAsync(() =>
        {
            fixture.Window.UpdateLayout();
            return Math.Abs(fixture.Window.ClientSize.Width - 1000) < 1 && Math.Abs(viewer.Bounds.Width - 1000) < 1;
        }, "Native fixture resize did not reach the requested context-test width.");
        double fullWidth = viewport.Bounds.Width;
        Button postIndicator = RequireEdgeIndicator(viewer, "GalleryPostEdgeIndicator");
        Require(postIndicator.Focus(NavigationMethod.Tab), "Image post-edge indicator is not keyboard reachable.");
        await native.KeyAsync(MediaNativeInput.Space);
        await UntilAsync(() => viewer.CaptureState().ContextOpen, "OS Space on the image post indicator did not open the post.");
        await LayoutAsync(fixture.Window);
        Border context = Find<Border>(viewer, "GalleryContext");
        Require(viewer.CaptureState().ContextOpen && context.IsEffectivelyVisible && context.Opacity == 1,
            "Optional post overlay is missing or fades its entire readable content.");
        SelectableTextBlock[] postText = context.GetVisualDescendants().OfType<SelectableTextBlock>().ToArray();
        Require(postText.Length > 0 && postText.All(text => text.Opacity == 1)
            && context.Background is SolidColorBrush background && background.Color.A is > 0 and < 255,
            "Post overlay must use only a translucent background, retaining actual opaque post text.");
        Console.WriteLine("SMOKE post overlay geometry: " + JsonSerializer.Serialize(new
        { before = fullWidth, after = viewport.Bounds.Width, window = fixture.Window.ClientSize.ToString(),
            viewer = viewer.Bounds.ToString(), contextParent = (context.Parent as Control)?.Name }));
        Require(Math.Abs(viewport.Bounds.Width - fullWidth) < 1, "Overlay post context still reserves the docked media column.");
        Click(Find<Button>(viewer, "GalleryToggleContext"));
        viewer.ConfigurePlayback(docked); // Effective, fully usable fallback; no unsupported native transparency assumption.
        viewer.Focus();
        await LayoutAsync(fixture.Window);
        await HoldAsync(TimeSpan.FromSeconds(4), () => Require(Visible(chrome), "Docked fallback inherited overlay auto-hide."));
        Point? dockedPosition = chrome.TranslatePoint(default, viewport);
        Require(dockedPosition.HasValue && dockedPosition.Value.Y >= viewport.Bounds.Height - 1,
            "Returning to docked controls did not restore their placement outside the media viewport.");
        RequireSingleControlsChoice(viewer);
        Require(overlay.IsEffectivelyEnabled && overlay.IsChecked == false,
            "The single paired control must remain actionable from docked OFF.");
        foreach (string name in new[] { "GalleryClose", "GalleryPrevious", "GalleryNext", "GalleryToolsToggle" })
            Require(Find<Control>(viewer, name).IsEffectivelyVisible, "Docked fallback lost an essential action: " + name);
        Program.CaptureWindow(fixture.Window, "smoke-gallery-feature-docked.png");
        viewer.ConfigurePlayback(docked with { MediaFocus = true });
        await LayoutAsync(fixture.Window);
        Require(focus.IsChecked == true && overlay.IsChecked == false
            && !chrome.GetVisualAncestors().OfType<Control>().Any(parent => parent.Name == "GalleryOverlayFooter"),
            "MediaFocus alone implicitly enabled overlay/auto-hide instead of retaining independent docked controls.");
    }

    private static async Task CheckMixedSlideshowAsync(MainWindow owner, GalleryEntry[] entries)
    {
        await using Fixture fixture = new(owner);
        await fixture.ReadyAsync();
        GalleryViewer viewer = fixture.Viewer;
        viewer.ConfigurePlayback(new(GalleryPlaybackMode.Slideshow, 1));
        await fixture.ShowAsync(entries[0]);
        int index = 0, moves = 0;
        Exception? failure = null;
        Task transition = Task.CompletedTask;
        GalleryVideoPlayer.PlaybackEvidence? ended = null;
        async Task NavigateAsync(int delta)
        {
            try
            {
                Require(delta == 1 && index < entries.Length - 1, "Slideshow requested an unexpected backward/terminal move.");
                if (index == 1)
                {
                    GalleryVideoPlayer player = viewer.GetGalleryVisualDescendants().OfType<GalleryVideoPlayer>().Single();
                    ended = await player.InspectPlaybackAsync();
                    Require(ended.State == "Ended" || ended.Length > 0 && ended.Time >= ended.Length - 250,
                        "The mixed slideshow left the real video before its native end.");
                }
                index++;
                viewer.SetPosition($"{index + 1} / {entries.Length}", index > 0, index < entries.Length - 1);
                await viewer.ShowAsync(entries[index], CancellationToken.None);
            }
            catch (Exception exception) { failure = exception; }
        }
        viewer.MoveRequested += delta =>
        {
            moves++;
            if (!transition.IsCompleted) { failure = new InvalidOperationException("Duplicate navigation arrived during the previous media transition."); return; }
            transition = NavigateAsync(delta);
        };
        // The active-window/dialog gate stops real image timers, not just the toggle's rendering.
        Click(Find<Button>(viewer, "GallerySlideshowToggle"));
        Require(Running(viewer), "Explicit Start did not start the selected slideshow.");
        viewer.SetInteractionAvailable(false);
        await HoldAsync(ImageObservation, () => Require(moves == 0, "Unavailable interaction allowed a background slideshow advance."));
        viewer.SetInteractionAvailable(true);
        viewer.ConfigurePlayback(new(GalleryPlaybackMode.Manual, 1));
        viewer.ConfigurePlayback(new(GalleryPlaybackMode.Slideshow, 1));
        Require(!Running(viewer), "Configuring Slideshow mode implicitly resumed the previously blocked run.");
        Click(Find<Button>(viewer, "GallerySlideshowToggle"));
        await UntilAsync(() => failure != null || index == 1 && transition.IsCompleted, "The first ready image never advanced to the real video.");
        Require(failure == null, "Image-to-video transition failed: " + failure);
        GalleryVideoPlayer current = viewer.GetGalleryVisualDescendants().OfType<GalleryVideoPlayer>().Single();
        GalleryVideoPlayer.PlaybackEvidence playback = await current.InspectPlaybackAsync();
        await UntilAsync(async () =>
        {
            playback = await current.InspectPlaybackAsync();
            return playback.Time > 100 && (OperatingSystem.IsWindows() ? playback.DisplayedFrames >= 3 : playback.SurfaceFrames >= 3 && playback.SurfaceNonUniform);
        }, "Explicit slideshow Start did not play/decode the real video with normal video autoplay disabled.");
        Require(playback.HasPlayback && !playback.LoopEnabled, "Mixed slideshow enabled contradictory single-video Repeat or has no real playback.");
        Console.WriteLine("SMOKE feature mixed video frames: " + JsonSerializer.Serialize(playback));
        using (MediaNativeInput native = new(fixture.Window))
        {
            Slider seek = Find<Slider>(viewer, "GalleryVideoSeek");
            await native.ClickAsync(seek, MediaInteractionSmokeHelpers.TrackPoint(seek, .10));
            await UntilAsync(async () =>
            {
                var seeking = await current.InspectPlaybackAsync();
                return seeking.State == "Playing" && seeking.Length > 0 && Math.Abs(seeking.Time - seeking.Length * .10) < 1000;
            }, "Playing slideshow seek paused native playback or failed to seek.");
            await native.MoveAsync(viewer, new Point(viewer.Bounds.Width * .45, viewer.Bounds.Height * .30));
            Require(Running(viewer), "Releasing a native seek permanently stopped slideshow navigation.");
            await MediaInteractionSmokeHelpers.HoldStateAsync(current, "Playing", "Running slideshow seek release");
        }
        await UntilAsync(() => failure != null || index == 2 && transition.IsCompleted, "The actual video end never advanced to the last image.");
        Require(failure == null && moves == 2 && ended != null, "Mixed slideshow lost or duplicated an owned video-end transition: " + failure);
        Require(Find<Image>(viewer, "GalleryImage").Source is Bitmap && viewer.CaptureState().Key == entries[2].Key,
            "The real video end did not publish the next verified image.");
        // The last image gets its full display period; stopping at arrival would
        // contradict the controller contract. Then verify it remains terminal.
        await UntilAsync(() => failure != null || moves != 2 || !Running(viewer),
            "The last image did not stop after its display period.");
        await HoldAsync(ImageObservation, () => Require(moves == 2 && failure == null && !Running(viewer),
            "Terminal slideshow image looped, replayed an old callback or left a running toggle."));
        Console.WriteLine("SMOKE feature mixed video native-end evidence: " + JsonSerializer.Serialize(ended));
    }

    private static async Task CheckEdgeHoverAsync(MainWindow owner, GalleryEntry clip)
    {
        await using Fixture fixture = new(owner);
        await fixture.ReadyAsync();
        GalleryViewer viewer = fixture.Viewer;
        var prefs = new GalleryPlaybackPreferences(GalleryPlaybackMode.Slideshow, 1,
            OverlayControls: true, AutoHideControls: true, OverlayPostContext: true);
        viewer.ConfigurePlayback(prefs);
        viewer.SetPosition("1 / 1", false, false);
        await viewer.ShowAsync(clip, CancellationToken.None);
        using MediaNativeInput native = new(fixture.Window);
        Border controls = Find<Border>(viewer, "GalleryOverlayControls");
        Border post = Find<Border>(viewer, "GalleryContext");
        if (Find<Control>(viewer, "GalleryOverlayFallbackNotice").IsEffectivelyVisible)
        {
            // An unsupported native transparency backend must remain usable, not be
            // relabelled as successful edge coverage. Image edge checks still run separately.
            await HoldAsync(TimeSpan.FromMilliseconds(800), () => Require(Visible(controls), "Native fallback hid its docked controls."));
            foreach (string name in new[] { "GalleryClose", "GalleryPrevious", "GalleryNext", "GalleryToolsToggle", "GalleryVideoPlayPause" })
                Require(Find<Control>(viewer, name).IsEffectivelyVisible, "Native fallback lost an essential action: " + name);
            await native.ClickAsync(Find<Button>(viewer, "GalleryVideoPlayPause"));
            GalleryVideoPlayer fallbackPlayer = viewer.GetGalleryVisualDescendants().OfType<GalleryVideoPlayer>().Single();
            await UntilAsync(async () => (await fallbackPlayer.InspectPlaybackAsync()).State == "Playing", "Native fallback Play failed.");
            await MediaInteractionSmokeHelpers.HoldStateAsync(fallbackPlayer, "Playing", "Docked native fallback");
            Require(!Running(viewer), "Native fallback Play implicitly started saved Slideshow progression.");
            Console.WriteLine("SMOKE native-video edge controls UNSUPPORTED: actionable docked fallback verified; no edge/transparent-video PASS claimed.");
            return;
        }
        async Task CenterAsync() => await native.MoveAsync(viewer, new Point(viewer.Bounds.Width * .45, viewer.Bounds.Height * .3));
        object ObserveEdges() => new
        {
            viewer = viewer.Bounds.ToString(), body = Find<Grid>(viewer, "GalleryViewport").Bounds.ToString(),
            fields = new[] { "_lastPointerScreen", "_controlsHot", "_postHot", "_pointerInControls", "_overlayHidden", "_keyboardInteraction" }
                .ToDictionary(name => name, name => typeof(GalleryViewer).GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(viewer)?.ToString())
        };
        async Task BottomAsync()
        {
            Console.WriteLine("EDGE before move: " + JsonSerializer.Serialize(ObserveEdges()));
            await native.MoveAsync(viewer, new Point(viewer.Bounds.Width * .45, viewer.Bounds.Height - 2));
            Console.WriteLine("EDGE after move: " + JsonSerializer.Serialize(ObserveEdges()));
        }
        await CenterAsync();
        await UntilAsync(() => !Visible(controls), "A stored automatic overlay did not start collapsed.");
        RequireEdgeIndicator(viewer, "GalleryControlsEdgeIndicator");
        Button postIndicator = RequireEdgeIndicator(viewer, "GalleryPostEdgeIndicator");
        Require(!Visible(post), "Post overlay was visible before its right-edge activation.");
        // Targets remain available at idle; only their borderless chevron artwork is transient.
        Button controlsHint = RequireEdgeIndicator(viewer, "GalleryControlsEdgeIndicator");
        bool HintsVisible() => MediaInteractionSmokeHelpers.EdgeHintVisible(controlsHint)
            && MediaInteractionSmokeHelpers.EdgeHintVisible(postIndicator);
        bool HintsHidden() => !MediaInteractionSmokeHelpers.EdgeHintVisible(controlsHint)
            && !MediaInteractionSmokeHelpers.EdgeHintVisible(postIndicator);
        await native.MoveAsync(viewer, new Point(viewer.Bounds.Width * .40, viewer.Bounds.Height * .32));
        await UntilAsync(HintsVisible, "Actual center motion did not expose both edge hints.");
        Require(!Visible(controls) && !Visible(post), "Center hint motion opened a panel.");
        Program.CaptureWindow(fixture.Window, "smoke-gallery-edge-motion-hints.png");
        await UntilAsync(HintsHidden, "Edge hints did not expire after native pointer idle.");
        await HoldAsync(TimeSpan.FromMilliseconds(1600), () => Require(HintsHidden()
            && !Visible(controls) && !Visible(post), "Stationary timer ticks revived hints or opened panels."));
        Program.CaptureWindow(fixture.Window, "smoke-gallery-edge-idle-hints.png");
        await native.MoveAsync(viewer, new Point(viewer.Bounds.Width * .48, viewer.Bounds.Height * .34));
        await UntilAsync(HintsVisible, "New native center motion did not renew expired hints.");
        Require(!Visible(controls) && !Visible(post), "Renewing hints opened an edge panel.");
        Console.WriteLine("SMOKE edge hint lifecycle PASS: native center motion -> hints only -> idle expiry -> stationary ticks stay hidden -> new motion renews.");
        await BottomAsync();
        await UntilAsync(() => Visible(controls), "Bottom-edge hover did not reveal transport.");
        await native.ClickAsync(Find<Button>(viewer, "GallerySlideshowToggle"));
        await UntilAsync(() => Running(viewer), "Native pointer click did not start slideshow.");
        GalleryVideoPlayer player = viewer.GetGalleryVisualDescendants().OfType<GalleryVideoPlayer>().Single();
        await UntilAsync(async () => (await player.InspectPlaybackAsync()).State == "Playing",
            "Explicit Start failed to start the native video.");
        await CenterAsync();
        await UntilAsync(() => !Visible(controls), "Mouse focus on slideshow Start pinned the controls open.");
        Require(Running(viewer), "Hiding transport stopped a running slideshow.");
        await native.MoveAsync(viewer, new Point(viewer.Bounds.Width * .35, viewer.Bounds.Height * .35));
        await HoldAsync(TimeSpan.FromMilliseconds(700), () => Require(!Visible(controls) && Running(viewer),
            "Movement in the middle of the media reopened controls or paused playback."));
        Program.CaptureWindow(fixture.Window, "smoke-gallery-edge-slideshow-hidden.png");
        await BottomAsync();
        await UntilAsync(() => Visible(controls), "Bottom edge did not reopen the controls.");
        // Reset near the beginning using the actual timeline: this also proves seek release
        // preserves both native Playing and eventual slideshow advancement (mixed test above).
        Slider seek = Find<Slider>(viewer, "GalleryVideoSeek");
        await native.ClickAsync(seek, MediaInteractionSmokeHelpers.TrackPoint(seek, .05));
        await UntilAsync(async () =>
        {
            var state = await player.InspectPlaybackAsync();
            return state.State == "Playing" && state.Length > 0 && Math.Abs(state.Time - state.Length * .05) < 1000;
        }, "Edge transport seek changed native Playing intent.");
        foreach (string name in new[] { "GalleryVideoVolume", "GalleryVideoLoop" })
        {
            Control transport = Find<Control>(viewer, name);
            await native.MoveAsync(transport);
            await HoldAsync(TimeSpan.FromMilliseconds(700), () => Require(Visible(controls) && !Visible(post)
                && !viewer.CaptureState().ContextOpen && Running(viewer),
                "Hovering bottom transport leaked into right-post reveal or stopped slideshow: " + name));
        }
        Slider volume = Find<Slider>(viewer, "GalleryVideoVolume");
        var volumeEvidence = await player.InspectPlaybackAsync();
        if (Environment.GetEnvironmentVariable("MEOWCHAN_REQUIRE_FUNCTIONAL_AUDIO") == "1")
            Require(volumeEvidence.AudioAvailable && volumeEvidence.AudioTracks > 0 && volumeEvidence.PlayedAudioBuffers > 0,
                "MEOWCHAN_REQUIRE_FUNCTIONAL_AUDIO=1 requires actual available audio, an audio track and played audio buffers before edge volume branch selection.");
        if (!volumeEvidence.AudioAvailable)
        {
            bool ProvesNativeUnavailable(GalleryVideoPlayer.PlaybackEvidence sample) =>
                sample.HasPlayback && !sample.AudioAvailable && sample.AudioTracks > 0
                && (sample.Volume < 0 || (sample.DecodedAudio > 0 && sample.PlayedAudioBuffers == 0));
            var selection = viewer.CaptureState();
            bool running = Running(viewer), postVisible = Visible(post);
            int volumeContextWrites = 0;
            void VolumeContextChanged(bool _) => volumeContextWrites++;
            viewer.ContextOpenChanged += VolumeContextChanged;
            try
            {
                ToggleButton mute = Find<ToggleButton>(viewer, "GalleryVideoMute");
                Require(ProvesNativeUnavailable(volumeEvidence) && volumeEvidence.State == "Playing"
                    && !volume.IsEffectivelyEnabled && !mute.IsEffectivelyEnabled && !player.CanAdjustVolume,
                    "Native no-output edge check requires disabled controls and actual unavailable evidence.");
                await native.MoveAsync(volume, MediaInteractionSmokeHelpers.TrackPoint(volume, .60));
                await native.ButtonAsync(true);
                await native.ButtonAsync(false);
                await MediaInteractionSmokeHelpers.HoldStateAsync(player, "Playing", "Disabled bottom-edge volume interaction");
                var after = await player.InspectPlaybackAsync();
                Require(ProvesNativeUnavailable(after) && after.Volume == volumeEvidence.Volume
                    && after.Muted == volumeEvidence.Muted && after.State == volumeEvidence.State
                    && viewer.CaptureState().PostId == selection.PostId
                    && viewer.CaptureState().Key == selection.Key && viewer.CaptureState().AttachmentPosition == selection.AttachmentPosition && Running(viewer) == running
                    && viewer.CaptureState().ContextOpen == selection.ContextOpen && Visible(post) == postVisible
                    && volumeContextWrites == 0 && !volume.IsEffectivelyEnabled && !mute.IsEffectivelyEnabled && !player.CanAdjustVolume,
                    "Disabled edge volume pointer changed native volume/mute/state, selection, slideshow or context.");
                Console.WriteLine("SMOKE coverage-limit edge volume: native output unavailable; volume editing/focus NOT exercised; physical disabled-pointer invariance verified. Evidence: "
                    + JsonSerializer.Serialize(new { before = volumeEvidence, after }));
            }
            finally { viewer.ContextOpenChanged -= VolumeContextChanged; }
        }
        else
        {
            await native.ClickAsync(volume, MediaInteractionSmokeHelpers.TrackPoint(volume, .60));
        }
        await MediaInteractionSmokeHelpers.HoldStateAsync(player, "Playing", "Bottom-edge volume interaction");
        Require(Running(viewer) && !Visible(post) && !viewer.CaptureState().ContextOpen,
            "Bottom volume interaction opened right post or stopped automatic advancement.");
        if (volumeEvidence.AudioAvailable)
            Console.WriteLine("SMOKE functional-audio-positive edge volume: " + JsonSerializer.Serialize(new
            { before = volumeEvidence, after = await player.InspectPlaybackAsync() }));
        await native.MoveAsync(controls);
        await HoldAsync(TimeSpan.FromMilliseconds(800), () => Require(Visible(controls), "Controls collapsed while pointer was inside them."));
        Button pause = Find<Button>(viewer, "GallerySlideshowToggle");
        await native.MoveAsync(pause);
        await native.ButtonAsync(true);
        try
        {
            await CenterAsync();
            await HoldAsync(TimeSpan.FromMilliseconds(700), () => Require(Visible(controls), "Active pointer capture allowed controls to collapse."));
        }
        finally { await native.ButtonAsync(false); }
        await UntilAsync(() => !Visible(controls), "Cancelled pointer gesture left controls pinned.");
        await BottomAsync();
        await UntilAsync(() => Visible(controls), "Transport did not recover after pointer-capture release.");
        int contextWrites = 0, preferenceWrites = 0;
        viewer.ContextOpenChanged += _ => contextWrites++;
        viewer.PlaybackPreferencesChanged += _ => preferenceWrites++;
        // The normal decorated Window owns an outer resize border. Test inside the
        // gallery's 24-DIP edge strip, not on the native resize chrome over its last pixels.
        Point rightPoint = new(viewer.Bounds.Width - 12, viewer.Bounds.Height * .25);
        PixelPoint rightPixel = viewer.PointToScreen(rightPoint);
        Console.WriteLine("EDGE right target: " + JsonSerializer.Serialize(new {
            rightPoint = rightPoint.ToString(), rightPixel = rightPixel.ToString(), window = fixture.Window.ClientSize.ToString(),
            hit = (fixture.Window.InputHitTest(fixture.Window.PointToClient(rightPixel)) as Control)?.GetType().Name,
            hitName = (fixture.Window.InputHitTest(fixture.Window.PointToClient(rightPixel)) as Control)?.Name,
            roots = new[] { "_body", "_visualRoot" }.ToDictionary(name => name,
                name => ((Control)typeof(GalleryViewer).GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(viewer)!).Bounds.ToString()) }));
        await native.MoveAsync(viewer, rightPoint);
        Console.WriteLine("EDGE after right: " + JsonSerializer.Serialize(ObserveEdges()));
        await UntilAsync(() => Visible(post) && viewer.CaptureState().ContextOpen, "Right edge did not reveal captured post context.");
        await MediaInteractionSmokeHelpers.HoldStateAsync(player, "Playing", "Reading right-edge post");
        await native.MoveAsync(post, new Point(post.Bounds.Width / 2, Math.Min(80, post.Bounds.Height / 2)));
        await HoldAsync(TimeSpan.FromMilliseconds(700), () => Require(Visible(post), "Hover-opened post collapsed while being read."));
        Require(!Running(viewer), "Reading the hover post did not pause automatic progression.");
        Program.CaptureWindow(fixture.Window, "smoke-gallery-edge-post-open.png");
        await CenterAsync();
        await UntilAsync(() => !Visible(post) && !viewer.CaptureState().ContextOpen, "Post overlay did not collapse after leaving its area.");
        Require(contextWrites == 0 && preferenceWrites == 0, "Hover changed persisted gallery/context preferences.");
        Require(!Running(viewer), "Leaving the post overlay automatically resumed slideshow.");
        postIndicator = RequireEdgeIndicator(viewer, "GalleryPostEdgeIndicator");
        Require(postIndicator.Focus(NavigationMethod.Tab), "Post edge indicator is not keyboard focusable.");
        await native.KeyAsync(MediaNativeInput.Space);
        await UntilAsync(() => Visible(post) && viewer.CaptureState().ContextOpen,
            "Keyboard activation of post edge indicator did not expose readable post context.");
        Require(!Running(viewer), "Keyboard post activation resumed automatic progression.");
        // Dismiss by native Escape before the independent explicit context-open tests below.
        int nativeShellRequests = fixture.NativeShellKeyRequests;
        await native.KeyAsync(MediaNativeInput.Escape);
        Require(!viewer.CaptureState().ContextOpen, "Post-indicator Escape did not dismiss the active post.");
        if (OperatingSystem.IsWindows())
            Require(fixture.NativeShellKeyRequests == nativeShellRequests + 1,
                "Native post Escape did not cross the existing gallery-to-shell host boundary exactly once.");
        contextWrites = 0; // Explicit keyboard choices may persist; hover-only checks above must not.
        viewer.ConfigurePlayback(prefs.WithControlsOverlay(false));
        Click(Find<Button>(viewer, "GalleryToggleContext"));
        await CenterAsync();
        await HoldAsync(TimeSpan.FromMilliseconds(800), () => Require(Visible(post), "Explicitly opened post context was treated as transient hover."));
        Click(Find<Button>(viewer, "GalleryToggleContext"));
        Require(contextWrites == 2, "Explicit context choices did not retain their existing preference semantics.");
        viewer.ConfigurePlayback(prefs);
        viewer.Focus(); await CenterAsync();
        await UntilAsync(() => !Visible(controls), "Controls did not return to edge-auto mode.");
        viewer.SetLoadingNotice("Synthetic loading notice");
        await HoldAsync(TimeSpan.FromMilliseconds(650), () => Require(!Visible(controls), "Normal loading revealed hidden transport."));
        bool wasRunning = Running(viewer);
        int reports = 0;
        void Report(GalleryDiagnosticNotice notice) { if (notice.Operation == "notice") reports++; }
        viewer.DiagnosticNotice += Report;
        viewer.SetNotice("Synthetic source notification");
        Require(reports == 1 && Running(viewer) == wasRunning && !Visible(controls),
            "Pure source notification changed progression or revealed hidden controls.");
        viewer.DiagnosticNotice -= Report;
        // Actual codec/integrity failure coverage is separate in GalleryErrorInteractionChecks.
        await HoldAsync(TimeSpan.FromMilliseconds(650), () => Require(!Visible(controls), "Notification pinned controls."));
        Console.WriteLine("SMOKE edge panels PASS: " + JsonSerializer.Serialize(new
        { backend = native.Backend, scale = fixture.Window.RenderScaling, centerDoesNotReveal = true,
            slideshowPointerFocusDoesNotPin = true, bottomReveals = true, rightPostReveals = true, hoverPreferenceWrites = preferenceWrites }));
    }

    private static async Task CheckRepeatSwitchKeepsPlaybackAsync(MainWindow owner, GalleryEntry clip)
    {
        await using Fixture fixture = new(owner);
        await fixture.ReadyAsync();
        GalleryViewer viewer = fixture.Viewer;
        viewer.ConfigurePlayback(new(GalleryPlaybackMode.Slideshow, 1));
        await viewer.ShowAsync(clip, CancellationToken.None);
        Click(Find<Button>(viewer, "GallerySlideshowToggle"));
        GalleryVideoPlayer player = viewer.GetGalleryVisualDescendants().OfType<GalleryVideoPlayer>().Single();
        await UntilAsync(async () => (await player.InspectPlaybackAsync()).Time > 250,
            "The video did not start before the repeat-mode transition.");
        player.ToggleLoop();
        GalleryVideoPlayer.PlaybackEvidence state = await player.InspectPlaybackAsync();
        Require(state.LoopEnabled && state.State == "Playing" && !Running(viewer),
            "Enabling Repeat during a running slideshow paused the current video or kept contradictory automatic navigation.");
        await HoldAsync(TimeSpan.FromMilliseconds(300), () => Require(!Running(viewer),
            "Repeat unexpectedly restarted slideshow navigation."));
        state = await player.InspectPlaybackAsync();
        Require(state.State == "Playing" && state.LoopEnabled, "A delayed mode-change callback paused Repeat playback.");
        Console.WriteLine("SMOKE feature repeat switch: preference changes stop slideshow navigation without pausing the current video.");
    }

    private static async Task CheckDecodeFailureStopsAsync(MainWindow owner, string directory, GalleryEntry image)
    {
        GalleryEntry invalid = await AssetAsync(directory, "invalid-video", "invalid.webm",
            System.Text.Encoding.UTF8.GetBytes("verified fixture bytes that are not a video"));
        await using Fixture fixture = new(owner);
        await fixture.ReadyAsync();
        GalleryViewer viewer = fixture.Viewer;
        viewer.ConfigurePlayback(new(GalleryPlaybackMode.Slideshow, 1));
        await fixture.ShowAsync(image);
        int moves = 0;
        Exception? failure = null;
        Task transition = Task.CompletedTask;
        async Task NavigateAsync()
        {
            try
            {
                viewer.SetPosition("2 / 3", true, true);
                await viewer.ShowAsync(invalid, CancellationToken.None);
            }
            catch (Exception error) { failure = error; }
        }
        viewer.MoveRequested += delta =>
        {
            moves++;
            if (moves != 1 || delta != 1) { failure = new InvalidOperationException("Decode failure advanced beyond its current media."); return; }
            transition = NavigateAsync();
        };
        Click(Find<Button>(viewer, "GallerySlideshowToggle"));
        await UntilAsync(() => failure != null || moves == 1 && transition.IsCompleted && !Running(viewer)
            && !Find<Button>(viewer, "GallerySlideshowToggle").IsEnabled,
            "A verified but undecodable video did not stop the running slideshow.");
        Require(failure == null, "Invalid-video transition failed outside the player failure surface: " + failure);
        await HoldAsync(ImageObservation, () => Require(moves == 1 && !Running(viewer),
            "A decode failure was silently skipped or automatically restarted."));
        Console.WriteLine("SMOKE feature decode failure: verified invalid video stops progression and keeps the error actionable.");
    }

    private static async Task CheckStartAfterEndedAsync(MainWindow owner, GalleryEntry clip, GalleryEntry nextImage)
    {
        await using Fixture fixture = new(owner);
        await fixture.ReadyAsync();
        GalleryViewer viewer = fixture.Viewer;
        viewer.ConfigurePlayback(new(GalleryPlaybackMode.Manual, 1));
        viewer.SetPosition("1 / 2", false, true);
        await viewer.ShowAsync(clip, CancellationToken.None).WaitAsync(Deadline);
        GalleryVideoPlayer player = viewer.GetGalleryVisualDescendants().OfType<GalleryVideoPlayer>().Single();
        int nativeEnds = 0, moves = 0;
        player.PlaybackEnded += () => nativeEnds++;
        viewer.MoveRequested += _ => moves++;
        await player.PlayAsync().WaitAsync(Deadline);
        GalleryVideoPlayer.PlaybackEvidence original = await player.InspectPlaybackAsync();
        await UntilAsync(async () =>
        {
            original = await player.InspectPlaybackAsync();
            return original.HasPlayback && original.Time > 100 && (OperatingSystem.IsWindows() ? original.DisplayedFrames >= 3 : original.SurfaceFrames >= 3 && original.SurfaceNonUniform);
        }, "Replay fixture did not present real decoded frames during its initial manual playback.");
        await UntilAsync(() => nativeEnds == 1, "The manual replay fixture never reached its real native end.");
        original = await player.InspectPlaybackAsync();
        Require(original.State == "Ended" && original.Length > 500 && moves == 0,
            "Manual video end unexpectedly navigated or was not actually terminal before replay.");
        viewer.ConfigurePlayback(new(GalleryPlaybackMode.Slideshow, 1));
        await HoldAsync(ImageObservation, () => Require(!Running(viewer) && moves == 0 && nativeEnds == 1,
            "Configuring Slideshow consumed an already-ended video or started replay without an explicit request."));

        GalleryVideoPlayer.PlaybackEvidence configured = await player.InspectPlaybackAsync();
        Require(configured.State == "Ended" && !Running(viewer),
            "Applying Slideshow mode restarted the actual ended video even though no Start action was requested.");
        Task transition = Task.CompletedTask;
        Exception? failure = null;
        async Task ShowNextAsync(int delta)
        {
            try
            {
                // Viewer subscribed first: let the same native Ended delivery reach our
                // observer before asserting its count or disposing the ended player.
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                Require(delta == 1 && moves == 1 && nativeEnds == 2,
                    "Start-after-ended consumed the old end, duplicated navigation or advanced before the replay's real end.");
                viewer.SetPosition("2 / 2", true, false);
                await viewer.ShowAsync(nextImage, CancellationToken.None);
            }
            catch (Exception exception) { failure = exception; }
        }
        viewer.MoveRequested += delta => transition = ShowNextAsync(delta);
        Click(Find<Button>(viewer, "GallerySlideshowToggle"));
        Require(Running(viewer), "Explicit Start after an ended video failed to create a runnable replay generation.");
        GalleryVideoPlayer.PlaybackEvidence replay = await player.InspectPlaybackAsync();
        await UntilAsync(async () =>
        {
            replay = await player.InspectPlaybackAsync();
            return replay.State == "Playing" && replay.Time > 100 && replay.Time < original.Length - 500
                && (OperatingSystem.IsWindows() ? replay.DisplayedFrames >= 3 : replay.SurfaceFrames >= 3 && replay.SurfaceNonUniform);
        }, "Explicit Start after end did not replay actual video frames from a new playback generation.");
        Require(moves == 0 && nativeEnds == 1 && !replay.LoopEnabled,
            "The prior terminal callback was replayed as a Next or single-video Repeat was enabled.");
        await UntilAsync(() => failure != null || moves == 1 && transition.IsCompleted,
            "Replayed video end did not advance exactly once to the next image.");
        Require(failure == null && nativeEnds == 2 && viewer.CaptureState().Key == nextImage.Key
            && Find<Image>(viewer, "GalleryImage").Source is Bitmap,
            "Explicit replay did not complete the new real video-end/image transition: " + failure);
        await UntilAsync(() => moves != 1 || nativeEnds != 2 || !Running(viewer),
            "The replay successor did not stop after its image display period.");
        await HoldAsync(ImageObservation, () => Require(moves == 1 && nativeEnds == 2 && !Running(viewer),
            "Replay completion left an active terminal slideshow or delivered a stale duplicate end."));
        Console.WriteLine("SMOKE feature start-after-ended: " + JsonSerializer.Serialize(new
        { nativeEnds, moves, original, configured, replay }));
    }

    private static void AssertChoices(GalleryViewer viewer, GalleryPlaybackPreferences preferences)
    {
        Require(Find<ComboBox>(viewer, "GalleryPlaybackModeSelector").SelectedIndex == (int)preferences.Mode,
            "Recreated viewer lost its saved playback mode.");
        foreach ((string name, bool expected) in new[] {
            ("GalleryToolsToggle", preferences.MediaFocus), ("GalleryOverlayToggle", preferences.OverlayControls),
            ("GalleryPostOverlayToggle", preferences.OverlayPostContext) })
            Require(Find<ToggleButton>(viewer, name).IsChecked == expected, "Recreated viewer lost preference: " + name);
        Require(preferences.OverlayControls == preferences.AutoHideControls, "Stored controls preferences are not a normalized pair.");
        RequireSingleControlsChoice(viewer);
    }
    private static Button RequireEdgeIndicator(GalleryViewer viewer, string name)
    {
        Button indicator = Find<Button>(viewer, name);
        MediaInteractionSmokeHelpers.RequireBorderlessEdgeTarget(indicator);
        Require(Visible(indicator) && indicator.IsEffectivelyEnabled && indicator.IsHitTestVisible && indicator.Focusable
            && indicator.Bounds.Width >= 24 && indicator.Bounds.Height >= 24
            && !string.IsNullOrWhiteSpace(AutomationProperties.GetName(indicator)),
            "Collapsed overlay has no discoverable, named keyboard/pointer target (minimum 24 DIP): " + name);
        MediaInteractionSmokeHelpers.RequireEdgeTargetWithinViewer(indicator, viewer);
        return indicator;
    }
    private static void RequireSingleControlsChoice(GalleryViewer viewer)
    {
        ToggleButton overlay = Find<ToggleButton>(viewer, "GalleryOverlayToggle");
        // Structural check also runs with remembered MediaFocus=true: both the parent
        // chrome and individual secondary tools may intentionally be collapsed. Native
        // shortcut and expanded-toolbar checks elsewhere establish actual reachability.
        Require(overlay.IsEnabled && overlay.Focusable
            && !string.IsNullOrWhiteSpace(AutomationProperties.GetName(overlay)),
            "The single controls-overlay/auto-hide choice is not exposed, actionable and accessibly named.");
        Require(!viewer.GetGalleryVisualDescendants().OfType<Control>().Any(c => c.Name == "GalleryAutoHideToggle" && c.IsVisible),
            "Gallery still exposes two independent controls-overlay/auto-hide choices.");
    }
    private static bool Running(GalleryViewer viewer) => Find<ToggleButton>(viewer, "GallerySlideshowToggle").IsChecked == true;
    private static bool Visible(Control control) => control.IsEffectivelyVisible && control.Opacity > .01;
    private static T Find<T>(GalleryViewer viewer, string name) where T : Control =>
        viewer.GetGalleryVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void SendKey(Control control, Key key)
    {
        control.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, Source = control });
        control.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = key, Source = control });
    }
    private static void Click(Button button)
    {
        Require(button.IsEffectivelyVisible && button.IsEffectivelyEnabled && button.Focus(NavigationMethod.Tab),
            "Feature action is not visibly keyboard reachable: " + button.Name);
        SendKey(button, Key.Space); // Ordinary Button/ToggleButton activation, including its checked state.
    }
    private static async Task LayoutAsync(Window window) => await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
    private static async Task HoldAsync(TimeSpan duration, Action assertion)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        do { assertion(); await Task.Delay(25); } while (elapsed.Elapsed < duration);
        assertion();
    }
    private static Task UntilAsync(Func<bool> condition, string message) => UntilAsync(() => Task.FromResult(condition()), message);
    private static async Task UntilAsync(Func<Task<bool>> condition, string message)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (!await condition())
        {
            if (elapsed.Elapsed > Deadline) throw new TimeoutException(message);
            await Task.Delay(25);
        }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class Fixture : IAsyncDisposable
    {
        internal GalleryViewer Viewer { get; } = new();
        internal Window Window { get; }
        internal int Decodes { get; private set; }
        internal int NativeShellKeyRequests { get; private set; }
        internal Fixture(MainWindow owner)
        {
            Viewer.Configure(contextOpen: false, showShortcutHelp: true, videoAutoplay: false, videoStartMuted: true, videoVolume: 0);
            Viewer.DecodeImageAsync = async (entry, request, token) =>
            { Decodes++; return await GalleryImageDecoder.DecodeAsync(entry, request, token); };
            Window = new Window { Width = 1000, Height = 700, MinWidth = 640, MinHeight = 480,
                Content = Viewer, Title = "Isolated gallery playback feature fixture" };
            // This owned fixture bypasses MainWindow's normal Escape hierarchy. Supply
            // only that host boundary: popups retain their first Escape, then the
            // existing production dismiss operation handles the viewer's own panels.
            void HostEscape(object? sender, KeyEventArgs e)
            {
                Control keyRoot = e.Source is Visual source ? (Control?)TopLevel.GetTopLevel(source) ?? Window : Window;
                if (!e.Handled && !ShortcutService.IsCapturing && e.Key == Key.Escape
                    && e.KeyModifiers == KeyModifiers.None && !CommandRouter.HasOpenPopup(Window)
                    && !CommandRouter.HasOpenPopup(keyRoot) && Viewer.TryDismissOverlay()) e.Handled = true;
            }
            Window.AddHandler(InputElement.KeyDownEvent, HostEscape, RoutingStrategies.Tunnel);
            // Match MainWindow's existing host subscription as well: on Windows,
            // the focused post lives in VideoView's separate native overlay root,
            // whose events cannot tunnel through this fixture Window.
            Viewer.ShellKeyRequested += (sender, e) =>
            {
                NativeShellKeyRequests++;
                HostEscape(sender, e);
            };
            Window.Show(owner);
            Window.Activate();
        }
        internal async Task ReadyAsync()
        {
            await UntilAsync(() => Window.IsActive && Viewer.Bounds.Width > 500
                && Find<Grid>(Viewer, "GalleryViewport").Bounds.Height > 100, "Owned feature window did not become ready on the isolated harness display.");
            Viewer.SetInteractionAvailable(true);
            Viewer.SetPosition("1 / 3", true, true);
        }
        internal async Task ShowAsync(GalleryEntry entry)
        {
            await Viewer.ShowAsync(entry, CancellationToken.None).WaitAsync(Deadline);
            await UntilAsync(() => Find<Image>(Viewer, "GalleryImage").Source is Bitmap, "The verified image never became visible.");
            await LayoutAsync(Window);
        }
        public async ValueTask DisposeAsync()
        {
            // Terminal cleanup must not restore controls from an owned native
            // overlay into this still-attached root. That can leave queued arrange
            // work owned by the old LayoutManager on Windows.
            Control[] externalControls = Viewer.GetGalleryVisualDescendants().OfType<Control>()
                .Where(control => control.Name is "GalleryOverlayFooter" or "GalleryOverlayControls"
                    or "GalleryOverlayInfo" or "GalleryContext" or "GalleryShortcutHelp"
                    or "GalleryVideoTransport")
                .Where(control => TopLevel.GetTopLevel(control) is { } root && !ReferenceEquals(root, Window))
                .Distinct().ToArray();
            int retiredHostAttachments = 0;
            void Attached(object? sender, Avalonia.VisualTreeAttachmentEventArgs args)
            {
                if (sender is Control control && ReferenceEquals(TopLevel.GetTopLevel(control), Window))
                    retiredHostAttachments++;
            }
            foreach (Control control in externalControls) control.AttachedToVisualTree += Attached;
            Console.WriteLine($"SMOKE gallery fixture terminal stop begin: externalControls={externalControls.Length}");
            try
            {
                // Actual cleanup is awaited before closing the owned native window or deleting files.
                await Viewer.StopAsync();
                Require(retiredHostAttachments == 0,
                    "Terminal gallery retirement moved native overlay controls back into the host root.");
                await LayoutAsync(Window);
                Console.WriteLine("SMOKE gallery fixture terminal stop complete: no cross-root restoration");
            }
            finally
            {
                foreach (Control control in externalControls) control.AttachedToVisualTree -= Attached;
            }
            Window.Content = null;
            Window.Close();
        }
    }

    private static async Task<GalleryEntry> ImageAsync(string directory, string name, SKColor color)
    {
        using SKBitmap bitmap = new(320, 180);
        using (SKCanvas canvas = new(bitmap))
        {
            canvas.Clear(color);
            using SKPaint pen = new() { Color = SKColors.White, StrokeWidth = 3 };
            canvas.DrawLine(0, 0, 320, 180, pen);
        }
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
        return await AssetAsync(directory, name, name + ".png", png.ToArray());
    }
    private static async Task<GalleryEntry> AssetAsync(string parent, string name, string fileName, byte[] bytes)
    {
        string directory = Path.Combine(parent, name);
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, fileName), bytes);
        object record = new { MediaIdentifier = name, SourceUrl = "https://offline.invalid/" + fileName, FileName = fileName,
            Length = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), Md5 = Convert.ToBase64String(MD5.HashData(bytes)) };
        await File.WriteAllTextAsync(Path.Combine(directory, ".meowchan-manifest.json"), JsonSerializer.Serialize(new
        { Version = 1, Entries = new Dictionary<string, object> { [fileName] = record } }));
        MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(directory);
        MediaAssetReference asset = manifest.GetPage().Single();
        return new(new GalleryAssetKey(new ResourceId("4chan", "g", "feature-fixture"), directory, asset.AssetId), manifest, asset,
            new CapturedPost(name, 0, PostFieldPresence.Html, Html: "<p>Synthetic playback feature fixture</p>"), null, false, false);
    }
}
