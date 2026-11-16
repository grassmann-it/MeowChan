using System.Security.Cryptography;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Gallery;

namespace MeowChan.DesktopSmoke;

// Latest accepted interaction contract (29 October), reached through the existing feature
// entry point. Fixture/configuration/focus setup is not evidence for user input: every
// action under test below uses MediaNativeInput and the real native decoder observer.
internal static partial class GalleryPlaybackFeatureChecks
{
    private static void RequireSingleOverlayChoice(GalleryViewer viewer)
    {
        ToggleButton[] choices = viewer.GetGalleryVisualDescendants().OfType<ToggleButton>()
            .Where(c => c.Name is "GalleryOverlayToggle" or "GalleryAutoHideToggle")
            .Where(c => c.IsEffectivelyVisible).ToArray();
        Require(choices.Length == 1 && choices[0].IsEffectivelyEnabled,
            "Gallery must expose one actionable paired overlay/auto-hide choice, not two independent toggles.");
    }

    private static Button EdgeIndicator(GalleryViewer viewer, string name)
    {
        Button indicator = Find<Button>(viewer, name);
        MediaInteractionSmokeHelpers.RequireBorderlessEdgeTarget(indicator);
        Require(Visible(indicator) && indicator.IsEffectivelyEnabled && indicator.IsHitTestVisible
            && indicator.Focusable && !string.IsNullOrWhiteSpace(AutomationProperties.GetName(indicator)),
            "Collapsed edge has no available named keyboard/touch target (artwork may be idle-hidden): " + name);
        Require(indicator.Bounds.Width >= 16 && indicator.Bounds.Height >= 16,
            "Edge indicator has no usable pointer/touch hit area: " + name);
        MediaInteractionSmokeHelpers.RequireEdgeTargetWithinViewer(indicator, viewer);
        return indicator;
    }

    private static async Task CheckFirstNavigationAsync(MainWindow owner, GalleryEntry first, GalleryEntry last)
    {
        // Deliberately pin the ordinary arrows in the in-session shortcut service, not the
        // user's saved profile. Custom-binding first-key coverage lives in MediaInteractionChecks.
        var bindings = owner.SessionHost.Config.ShortcutBindings;
        ShortcutService.Publish(bindings.Set("gallery.previous", [ShortcutGesture.Parse("Left")])
            .Set("gallery.next", [ShortcutGesture.Parse("Right")]));
        try
        {
            await using Fixture fixture = new(owner);
            await fixture.ReadyAsync();
            GalleryViewer viewer = fixture.Viewer;
            viewer.ConfigurePlayback(new(OverlayControls: true, AutoHideControls: true, OverlayPostContext: true));
            GalleryEntry[] entries = [first, last];
            int index = 0, moves = 0;
            Exception? failure = null;
            Task transition = Task.CompletedTask;
            async Task ShowNextAsync(int delta)
            {
                try
                {
                    index += delta;
                    Require(index is 0 or 1, "One native key requested a duplicate/out-of-range navigation.");
                    viewer.SetPosition($"{index + 1} / 2", index > 0, index < 1);
                    await fixture.ShowAsync(entries[index]);
                }
                catch (Exception error) { failure = error; }
            }
            viewer.MoveRequested += delta =>
            {
                moves++;
                if (!transition.IsCompleted) { failure = new InvalidOperationException("Native navigation overlapped the prior transition."); return; }
                transition = ShowNextAsync(delta);
            };
            viewer.SetPosition("1 / 2", false, true);
            await fixture.ShowAsync(first);
            using MediaNativeInput native = new(fixture.Window);
            Border controls = Find<Border>(viewer, "GalleryOverlayControls");
            async Task HiddenAsync()
            {
                // Establish a non-native-editing focus context before testing a hidden key.
                viewer.FocusGallerySurface();
                await native.MoveAsync(viewer, new Point(viewer.Bounds.Width * .4, viewer.Bounds.Height * .3));
                await UntilAsync(() => !Visible(controls) && !viewer.CaptureState().ContextOpen,
                    "First-key precondition: controls/post never collapsed after leaving their panels.");
                // Real mouse motion anywhere over the gallery intentionally reveals the
                // controls. The edge chevron is transient artwork, so verify the
                // persistent transparent handles as native targets instead of moving back
                // through the centre and reopening the footer.
                Button bottomHint = EdgeIndicator(viewer, "GalleryControlsEdgeIndicator");
                Button rightHint = EdgeIndicator(viewer, "GalleryPostEdgeIndicator");
                await UntilAsync(() => MediaInteractionSmokeHelpers.IsNativeGeometryReady(bottomHint)
                    && MediaInteractionSmokeHelpers.IsNativeGeometryReady(rightHint),
                    "Hidden gallery edge handles never became current native geometry targets.");
                MediaInteractionSmokeHelpers.RequireEdgeTargetWithinViewer(bottomHint, viewer);
                MediaInteractionSmokeHelpers.RequireEdgeTargetWithinViewer(rightHint, viewer);
            }
            async Task FirstKeyAsync(bool next, string scenario)
            {
                int before = moves;
                GalleryEntry expected = entries[next ? 1 : 0];
                await native.KeyAsync(next ? MediaNativeInput.Right : MediaNativeInput.Left); // exactly ONE press
                await UntilAsync(() => failure != null || moves == before + 1 && transition.IsCompleted
                    && viewer.CaptureState().Key == expected.Key, scenario + ": first arrow was swallowed as reveal-only.");
                Require(failure == null, scenario + ": " + failure);
                await HoldAsync(TimeSpan.FromMilliseconds(350), () => Require(moves == before + 1
                    && viewer.CaptureState().Key == expected.Key, scenario + ": one arrow navigated more than once."));
            }
            await HiddenAsync();
            await FirstKeyAsync(true, "hidden Right");
            await HiddenAsync();
            await FirstKeyAsync(false, "hidden Left");
            await HiddenAsync();
            await LayoutAsync(fixture.Window);
            await native.MoveAsync(() => NativeTargetAsync<Button>(viewer, "GalleryControlsEdgeIndicator"));
            await UntilAsync(() => Visible(controls), "Bottom indicator hover did not reveal controls.");
            await FirstKeyAsync(true, "after bottom-indicator hover");
            await HiddenAsync();
            await native.ClickAsync(() => NativeTargetAsync<Button>(viewer, "GalleryControlsEdgeIndicator"));
            await UntilAsync(() => Visible(controls), "Bottom indicator click did not reveal controls.");
            await FirstKeyAsync(false, "after bottom-indicator click");
            await HiddenAsync();
            Button bottom = EdgeIndicator(viewer, "GalleryControlsEdgeIndicator");
            Require(bottom.Focus(NavigationMethod.Tab), "Bottom indicator cannot acquire keyboard focus.");
            await FirstKeyAsync(true, "focused bottom indicator");
            await HiddenAsync();
            Button right = EdgeIndicator(viewer, "GalleryPostEdgeIndicator");
            Require(right.Focus(NavigationMethod.Tab), "Post indicator cannot acquire keyboard focus.");
            await native.KeyAsync(MediaNativeInput.Space);
            await UntilAsync(() => viewer.CaptureState().ContextOpen, "Keyboard activation of the post indicator did not open context.");
            await FirstKeyAsync(false, "after keyboard post-indicator activation");
            await HiddenAsync();
            await native.ClickAsync(() => NativeTargetAsync<Button>(viewer, "GalleryPostEdgeIndicator"));
            await UntilAsync(() => viewer.CaptureState().ContextOpen, "Post indicator click did not reveal context.");
            await FirstKeyAsync(true, "after post-indicator click");
            await HiddenAsync();
            await native.MoveAsync(() => NativeTargetAsync<Button>(viewer, "GalleryPostEdgeIndicator"));
            await UntilAsync(() => viewer.CaptureState().ContextOpen, "Post indicator hover did not reveal context.");
            await FirstKeyAsync(false, "after post-indicator hover");
            Program.CaptureWindow(fixture.Window, "smoke-gallery-first-key-indicators.png");
            Console.WriteLine("SMOKE latest first-key PASS: hidden Right/Left and indicator hover/click/keyboard-focus, exactly one move per native press; " + native.Backend);
        }
        finally { ShortcutService.Publish(bindings); }
    }

    private static async Task CheckSavedSlideshowAutoplayAsync(MainWindow owner, GalleryEntry clip, GalleryEntry image)
    {
        foreach (bool manualSelection in new[] { false, true })
        {
            await using Fixture fixture = new(owner);
            await fixture.ReadyAsync();
            GalleryViewer viewer = fixture.Viewer;
            viewer.Configure(contextOpen: false, showShortcutHelp: true, videoAutoplay: true, videoStartMuted: true, videoVolume: 0);
            viewer.ConfigurePlayback(new(GalleryPlaybackMode.Slideshow, 1));
            int moves = 0;
            viewer.MoveRequested += _ => moves++;
            if (manualSelection) await fixture.ShowAsync(image);
            viewer.SetPosition("1 / 2", false, true);
            await viewer.ShowAsync(clip, CancellationToken.None).WaitAsync(Deadline);
            GalleryVideoPlayer player = Player(viewer);
            await UntilAsync(async () => HasRealFrames(await player.InspectPlaybackAsync()),
                "VideoAutoplay=true did not play the first/manually selected video in remembered Slideshow mode.");
            await ObservePlayingAsync(player, "remembered slideshow autoplay");
            Require(!Running(viewer) && moves == 0, "Video autoplay silently started slideshow automatic advancement.");
            using MediaNativeInput native = new(fixture.Window);
            await NativeSeekAsync(native, viewer, .90, drag: false);
            await UntilAsync(async () => (await player.InspectPlaybackAsync()).State == "Ended",
                "Autoplay fixture did not reach its real terminal video end.");
            await HoldAsync(ImageObservation, () => Require(!Running(viewer) && moves == 0
                && viewer.CaptureState().Key == clip.Key,
                "Remembered Slideshow automatically navigated at video end without explicit Start."));
            Console.WriteLine($"SMOKE latest autoplay PASS: manualSelection={manualSelection}, native Playing then Ended, no automatic next.");
        }
    }

    private static async Task CheckNativePlaybackIntentAsync(MainWindow owner, string directory, string suppliedVideo, GalleryEntry image)
    {
        string audio = FindSyntheticAudioFixture(suppliedVideo);
        byte[] bytes = await File.ReadAllBytesAsync(audio);
        Require(Convert.ToHexString(SHA256.HashData(bytes)).Equals(
            "a88f7ec3fa76c11c38719662ba41d5538a5775cc58afce5bf32e14e0cd5d1ad8", StringComparison.OrdinalIgnoreCase),
            "Native intent acceptance requires the pinned release102 synthetic VP8+Vorbis fixture.");
        GalleryEntry clip = await AssetAsync(directory, "native-intent-audio", "synthetic-audio.webm", bytes);
        await CheckPlayingSeekAsync(owner, clip, image);
        await CheckPausedSeekAsync(owner, clip);
        await CheckTransportPanelIntentAsync(owner, clip);
        Console.WriteLine("SMOKE latest native playback intent: synthetic VP8/Vorbis only; native clock/audio-track/volume state, NOT physical speaker/audio output proof.");
    }

    private static async Task CheckPlayingSeekAsync(MainWindow owner, GalleryEntry clip, GalleryEntry image)
    {
        await using Fixture fixture = new(owner);
        await fixture.ReadyAsync();
        GalleryViewer viewer = fixture.Viewer;
        viewer.ConfigurePlayback(new(GalleryPlaybackMode.Slideshow, 1)); // normal autoplay remains OFF
        viewer.SetPosition("1 / 2", false, true);
        await viewer.ShowAsync(clip, CancellationToken.None).WaitAsync(Deadline);
        using MediaNativeInput native = new(fixture.Window);
        int moves = 0;
        Task transition = Task.CompletedTask;
        Exception? failure = null;
        async Task NextAsync(int delta)
        {
            try
            {
                Require(delta == 1 && moves == 1, "Seek produced duplicate/backward slideshow navigation.");
                viewer.SetPosition("2 / 2", true, false);
                await fixture.ShowAsync(image);
            }
            catch (Exception error) { failure = error; }
        }
        viewer.MoveRequested += delta => { moves++; transition = NextAsync(delta); };
        await native.ClickAsync(Find<Button>(viewer, "GallerySlideshowToggle"));
        GalleryVideoPlayer player = Player(viewer);
        await UntilAsync(async () => HasRealFrames(await player.InspectPlaybackAsync()) && Running(viewer),
            "Explicit slideshow Start did not play the native clip with ordinary autoplay OFF.");
        Window? nativeOverlay = ErrorField<bool>(viewer, "_nativeOverlayActive")
            ? TopLevel.GetTopLevel(ErrorField<Grid>(viewer, "_nativeVideoOverlay")) as Window : null;
        await NativeSeekAsync(native, viewer, .20, drag: false);
        await ObservePlayingAsync(player, "playing track seek");
        await NativeSeekAsync(native, viewer, .45, drag: true);
        await ObservePlayingAsync(player, "playing thumb drag");
        await UntilAsync(() => Running(viewer), "Seeking permanently stopped automatic slideshow advancement.");
        Require(moves == 0 && viewer.CaptureState().Key == clip.Key, "Seek navigated before the actual video end.");
        await NativeSeekAsync(native, viewer, .90, drag: false);
        await UntilAsync(() => failure != null || moves == 1 && transition.IsCompleted,
            "After native seeking, actual video end no longer advanced the running slideshow.");
        Require(failure == null && viewer.CaptureState().Key == image.Key, "Post-seek successor failed: " + failure);
        TracePostSeekImageState(fixture.Window, viewer, nativeOverlay);
        Require(nativeOverlay?.IsVisible != true,
            "Retired native video overlay stayed visible above the image and can block slideshow interaction.");
        long? lastStateLog = null;
        await UntilAsync(() =>
        {
            if (!Running(viewer)) return true;
            if (lastStateLog == null || System.Diagnostics.Stopwatch.GetElapsedTime(lastStateLog.Value).TotalSeconds >= 1)
            {
                lastStateLog = System.Diagnostics.Stopwatch.GetTimestamp();
                TracePostSeekImageState(fixture.Window, viewer, nativeOverlay);
            }
            return false;
        }, "Post-seek terminal image did not stop the sequence.");
        await HoldAsync(ImageObservation, () => Require(moves == 1 && !Running(viewer),
            "A stale seek/end callback restarted or duplicated terminal navigation."));
    }

    private static void TracePostSeekImageState(Window host, GalleryViewer viewer, Window? retiredOverlay)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        object? Property(string name) => typeof(GalleryViewer).GetProperty(name, flags)?.GetValue(viewer);
        Console.WriteLine("SMOKE post-seek image state: " + System.Text.Json.JsonSerializer.Serialize(new
        {
            hostActive = host.IsActive, host.IsVisible, host.IsEnabled,
            viewerVisible = viewer.IsEffectivelyVisible, viewerEnabled = viewer.IsEffectivelyEnabled,
            running = Running(viewer), status = Property("CurrentSlideshowStatus")?.ToString(),
            canRun = Property("CanRunSlideshow"), popup = Property("HasGalleryPopup"), dialog = Property("HasGalleryDialog"),
            nativeActive = ErrorField<bool>(viewer, "_nativeOverlayActive"),
            retiredOverlayVisible = retiredOverlay?.IsVisible, retiredOverlayActive = retiredOverlay?.IsActive,
            available = ErrorField<bool>(viewer, "_interactionAvailable"),
            context = ErrorField<bool>(viewer, "_contextOpen"), help = ErrorField<bool>(viewer, "_helpOpen"),
            navigation = ErrorField<bool>(viewer, "_navigationPending"), suspended = ErrorField<bool>(viewer, "_videoSuspended"),
            failed = ErrorField<bool>(viewer, "_mediaFailed"),
            request = ErrorField<int>(viewer, "_request"), imageReady = ErrorField<int>(viewer, "_imageReadyRequest"),
            timer = ErrorField<Avalonia.Threading.DispatcherTimer>(viewer, "_interactionTimer").IsEnabled,
            owned = host.OwnedWindows.Select(window => new
            { galleryOverlay = viewer.OwnsOverlayWindow(window), window.IsVisible, window.IsActive }).ToArray()
        }));
    }

    private static async Task CheckPausedSeekAsync(MainWindow owner, GalleryEntry clip)
    {
        await using Fixture fixture = new(owner);
        await fixture.ReadyAsync();
        GalleryViewer viewer = fixture.Viewer;
        viewer.Configure(contextOpen: false, showShortcutHelp: true, videoAutoplay: true, videoStartMuted: true, videoVolume: 40);
        viewer.ConfigurePlayback(new(GalleryPlaybackMode.Slideshow, 1));
        await viewer.ShowAsync(clip, CancellationToken.None).WaitAsync(Deadline);
        GalleryVideoPlayer player = Player(viewer);
        await UntilAsync(async () => HasRealFrames(await player.InspectPlaybackAsync()), "Paused-seek fixture did not play first.");
        using MediaNativeInput native = new(fixture.Window);
        await native.ClickAsync(Find<Button>(viewer, "GalleryVideoPlayPause"));
        await UntilAsync(async () => (await player.InspectPlaybackAsync()).State == "Paused", "Explicit native Pause did not pause.");
        await NativeSeekAsync(native, viewer, .25, drag: false);
        await ObservePausedAsync(player, "paused track seek");
        await NativeSeekAsync(native, viewer, .45, drag: true);
        await ObservePausedAsync(player, "paused thumb drag");
        double before = Find<Slider>(viewer, "GalleryVideoSeek").Value;
        int moves = 0;
        viewer.SetPosition("1 / 2", false, true);
        viewer.MoveRequested += delta => { Require(delta == 1, "Paused timeline routed the wrong direction."); moves++; };
        await native.KeyAsync(MediaNativeInput.Right);
        Require(moves == 1, "First Right after paused timeline drag did not request exactly one media move.");
        Require(Math.Abs(Find<Slider>(viewer, "GalleryVideoSeek").Value - before) < 1,
            "Media navigation also performed a forbidden keyboard seek.");
        await ObservePausedAsync(player, "paused media navigation request");
        Require(!Running(viewer) && viewer.CaptureState().Key == clip.Key, "Paused navigation request resumed automatic advancement.");
    }

    private static async Task CheckTransportPanelIntentAsync(MainWindow owner, GalleryEntry clip)
    {
        await using Fixture fixture = new(owner);
        await fixture.ReadyAsync();
        GalleryViewer viewer = fixture.Viewer;
        viewer.Configure(contextOpen: false, showShortcutHelp: true, videoAutoplay: false, videoStartMuted: true, videoVolume: 40);
        var prefs = new GalleryPlaybackPreferences(GalleryPlaybackMode.Slideshow, 1,
            OverlayControls: true, AutoHideControls: true, OverlayPostContext: true);
        viewer.ConfigurePlayback(prefs);
        await viewer.ShowAsync(clip, CancellationToken.None).WaitAsync(Deadline);
        using MediaNativeInput native = new(fixture.Window);
        await native.MoveAsync(viewer, new Point(viewer.Bounds.Width * .4, viewer.Bounds.Height - 2));
        Border controls = Find<Border>(viewer, "GalleryOverlayControls");
        await UntilAsync(() => Visible(controls), "Bottom transport did not open for native intent acceptance.");
        await native.ClickAsync(Find<Button>(viewer, "GallerySlideshowToggle"));
        GalleryVideoPlayer player = Player(viewer);
        await UntilAsync(async () => HasRealFrames(await player.InspectPlaybackAsync()) && Running(viewer), "Transport fixture did not start.");
        var evidence = await player.InspectPlaybackAsync();
        int contextWrites = 0;
        viewer.ContextOpenChanged += _ => contextWrites++;
        Slider volume = Find<Slider>(viewer, "GalleryVideoVolume");
        if (Environment.GetEnvironmentVariable("MEOWCHAN_REQUIRE_FUNCTIONAL_AUDIO") == "1")
            Require(evidence.AudioAvailable && evidence.AudioTracks > 0 && evidence.PlayedAudioBuffers > 0,
                "MEOWCHAN_REQUIRE_FUNCTIONAL_AUDIO=1 requires actual available audio, an audio track and played audio buffers before transport volume branch selection.");
        bool nativeUnavailable = !evidence.AudioAvailable;
        bool ProvesNativeUnavailable(GalleryVideoPlayer.PlaybackEvidence sample) =>
            sample.HasPlayback && !sample.AudioAvailable && sample.AudioTracks > 0
            && (sample.Volume < 0 || (sample.DecodedAudio > 0 && sample.PlayedAudioBuffers == 0));
        if (nativeUnavailable)
            Require(ProvesNativeUnavailable(evidence), "Unavailable audio lacks native no-output evidence.");
        else
        {
            Require(evidence.AudioAvailable && evidence.AudioTracks > 0, "Synthetic audio track is unavailable; volume acceptance cannot silently skip.");
            Require(volume.IsEffectivelyEnabled, "Real audio fixture did not enable volume.");
        }
        await native.MoveAsync(volume);
        await ObservePlayingAsync(player, "volume hover");
        Require(Running(viewer) && !viewer.CaptureState().ContextOpen, "Volume hover opened post/paused slideshow.");
        if (nativeUnavailable)
        {
            var before = await player.InspectPlaybackAsync();
            var selection = viewer.CaptureState();
            bool running = Running(viewer);
            int writes = contextWrites;
            ToggleButton mute = Find<ToggleButton>(viewer, "GalleryVideoMute");
            Require(ProvesNativeUnavailable(before) && before.State == "Playing"
                && !volume.IsEffectivelyEnabled && !mute.IsEffectivelyEnabled && !player.CanAdjustVolume,
                "Native no-output volume check requires disabled controls and actual unavailable evidence.");
            await native.MoveAsync(volume, SliderPoint(volume, .65));
            await native.ButtonAsync(true);
            await native.ButtonAsync(false);
            await ObservePlayingAsync(player, "disabled volume click");
            var after = await player.InspectPlaybackAsync();
            Require(ProvesNativeUnavailable(after) && after.Volume == before.Volume && after.Muted == before.Muted
                && after.State == before.State && viewer.CaptureState().PostId == selection.PostId
                    && viewer.CaptureState().Key == selection.Key && viewer.CaptureState().AttachmentPosition == selection.AttachmentPosition
                && viewer.CaptureState().ContextOpen == selection.ContextOpen && Running(viewer) == running
                && contextWrites == writes && !volume.IsEffectivelyEnabled && !mute.IsEffectivelyEnabled && !player.CanAdjustVolume,
                "Disabled volume pointer changed native volume/mute/state, selection, slideshow or context.");
            Console.WriteLine("SMOKE coverage-limit transport volume: native output unavailable; volume editing/focus NOT exercised; physical disabled-pointer invariance verified. Evidence: "
                + System.Text.Json.JsonSerializer.Serialize(new { before, after }));
        }
        else
        {
            await NativeSliderClickAsync(native, volume, .65);
            await UntilAsync(async () => (await player.InspectPlaybackAsync()).Volume >= 55, "Native volume click did not change decoder volume.");
        }
        await ObservePlayingAsync(player, "volume click");
        Require(Running(viewer) && !viewer.CaptureState().ContextOpen, "Volume click opened right post or stopped advancement.");
        if (!nativeUnavailable)
            Console.WriteLine("SMOKE functional-audio-positive transport volume: " + System.Text.Json.JsonSerializer.Serialize(new
            { before = evidence, after = await player.InspectPlaybackAsync() }));
        Border transport = Find<Border>(viewer, "GalleryVideoTransport");
        // Reproduce the actual overlapping right-strip/bottom-panel geometry, not only
        // the center of a slider which may sit outside the right-edge hotspot.
        await native.MoveAsync(transport, new Point(transport.Bounds.Width - 2, transport.Bounds.Height / 2));
        await ObservePlayingAsync(player, "bottom transport at right hotspot");
        Require(Running(viewer) && !viewer.CaptureState().ContextOpen, "Right hotspot over visible bottom transport stole precedence and opened post.");
        ToggleButton loop = Find<ToggleButton>(viewer, "GalleryVideoLoop");
        // A disabled ToggleButton remains geometrically present but is not an actionable
        // native hit target; probe its real position without requiring hit-test ownership.
        await native.MoveAsync(loop, requireEnabled: false, requireHitTest: false);
        await ObservePlayingAsync(player, "loop hover");
        Require(Running(viewer) && !viewer.CaptureState().ContextOpen, "Loop hover opened post and stopped progression.");
        // A running slideshow owns video repetition, so the transport loop is intentionally
        // disabled. Do not make the native helper click a disabled target: switch the
        // existing playback mode while retaining the real native video clock, then wait for
        // the current transport root to publish an actually clickable loop control.
        Require(!loop.IsEffectivelyEnabled, "A running slideshow exposed a competing transport repeat action.");
        viewer.ConfigurePlayback(prefs with { Mode = GalleryPlaybackMode.Manual });
        await UntilAsync(() => !Running(viewer) && MediaInteractionSmokeHelpers.IsPointerTargetReady(loop),
            "Manual repeat setup did not produce a current native loop target.");
        await ObservePlayingAsync(player, "manual repeat ownership");
        await native.ClickAsync(loop);
        await ObservePlayingAsync(player, "loop click");
        Require(loop.IsChecked == true && !viewer.CaptureState().ContextOpen && contextWrites == 0,
            "Loop click opened post or failed to enable Repeat.");
        Require(!Running(viewer), "Explicit Repeat unexpectedly restarted slideshow automation.");
        loop = await NativeTargetAsync<ToggleButton>(viewer, "GalleryVideoLoop");
        await native.ClickAsync(loop);
        // Configuring mode is setup, not Start. Menu/panel interactions below must not
        // stop the native video even when they pause automatic advancement.
        viewer.ConfigurePlayback(prefs);
        Button restart = Find<Button>(viewer, "GallerySlideshowToggle");
        int restartClicks = 0;
        restart.Click += (_, _) => restartClicks++;
        // ConfigurePlayback changes toolbar labels/layout. A host layout pass alone
        // does not prove that the presenter has published its current hit geometry.
        await LayoutAsync(fixture.Window);
        // Configuration is fixture setup. Await an actual current-root hit target,
        // not merely arranged bounds while the presenter still hits its old parent.
        // The action remains one native click and exactly one Click event.
        await UntilAsync(() => MediaInteractionSmokeHelpers.IsPointerTargetReady(restart),
            "Slideshow restart setup never became a real current-root pointer target.");
        using var restartTrace = new ModeMenuInputTrace(fixture.Window, viewer, restart);
        restartTrace.Record("before-restart");
        await native.ClickAsync(restart);
        restartTrace.Record("after-restart");
        Require(restartClicks == 1, "Native slideshow restart click was lost or delivered twice; observed clicks=" + restartClicks);
        await UntilAsync(() => Running(viewer), "Could not restart automation before panel/menu intent checks.");
        await native.ClickAsync(Find<Button>(viewer, "GalleryToggleContext"));
        await UntilAsync(() => viewer.CaptureState().ContextOpen, "Explicit post panel did not open.");
        await ObservePlayingAsync(player, "reading explicit post panel");
        Require(!Running(viewer), "Reading post context did not pause automatic advancement.");
        await native.ClickAsync(Find<Button>(viewer, "GalleryToggleContext"));
        await ObservePlayingAsync(player, "closing explicit post panel");
        Require(!Running(viewer), "Closing post context silently resumed automatic advancement.");
        await native.ClickAsync(Find<Button>(viewer, "GallerySlideshowToggle"));
        await UntilAsync(() => Running(viewer), "Could not restart automation before menu intent check.");
        // The playback selector stays in the shared "More" flyout while the slideshow
        // toggle moves onto the toolbar. Opening the flyout is part of the user flow:
        // without it the selector is intentionally detached from any native hit root.
        viewer.SetOverflowOpen(true);
        await UntilAsync(() => viewer.IsOverflowOpen,
            "Playback mode overflow menu never opened.");
        ComboBox mode = Find<ComboBox>(viewer, "GalleryPlaybackModeSelector");
        // After the native start click and popup reparenting Avalonia can retain the
        // selector for one layout turn with visible-but-detached bounds. Do not pass that
        // stale target to OS input; wait for its current hit root.
        await UntilAsync(() => MediaInteractionSmokeHelpers.IsPointerTargetReady(mode),
            "Playback mode setup never became a current native pointer target.");
        using var menuTrace = new ModeMenuInputTrace(fixture.Window, viewer, mode);
        menuTrace.Record("before-click");
        await native.ClickAsync(mode);
        menuTrace.Record("after-click");
        await UntilAsync(() => mode.IsDropDownOpen, "Native mode menu did not open.");
        Require(menuTrace.ModePressed && menuTrace.ModeReleased && !menuTrace.ViewportCaptured,
            "Native mode click did not retain its press/release route independently of image panning.");
        await ObservePlayingAsync(player, "open playback menu");
        await native.KeyAsync(MediaNativeInput.Escape);
        await UntilAsync(() => !mode.IsDropDownOpen, "Native Escape did not close mode menu.");
        await ObservePlayingAsync(player, "closed playback menu");
        Require(contextWrites == 2, "Transient bottom-panel hover persisted post-context choices.");
    }

    private static async Task<T> NativeTargetAsync<T>(GalleryViewer viewer, string name) where T : Control
    {
        // Resolve the retained control on every dispatcher turn. A native gallery
        // transport can be between roots while the same named visual is being moved;
        // a settled hit-test probe must never keep a retired instance.
        T? target = null;
        await UntilAsync(() =>
        {
            try
            {
                T[] matches = viewer.GetGalleryVisualDescendants().OfType<T>()
                    .Where(control => control.Name == name).ToArray();
                if (matches.Length != 1) { target = null; return false; }
                target = matches[0];
                return MediaInteractionSmokeHelpers.IsPointerTargetReady(target);
            }
            catch (InvalidOperationException)
            {
                target = null;
                return false;
            }
        }, $"The {name} control did not settle as a native hit target.");
        return target!;
    }

    private static GalleryVideoPlayer Player(GalleryViewer viewer) => viewer.GetGalleryVisualDescendants().OfType<GalleryVideoPlayer>().Single();
    private static bool HasRealFrames(GalleryVideoPlayer.PlaybackEvidence evidence) => evidence.HasPlayback
        && evidence.State == "Playing" && evidence.Time > 100 && evidence.Seekable && evidence.Length >= 8000
        && (OperatingSystem.IsWindows() ? evidence.DisplayedFrames >= 3 : evidence.SurfaceFrames >= 3 && evidence.SurfaceNonUniform);

    private static async Task ObservePlayingAsync(GalleryVideoPlayer player, string action)
    {
        var first = await player.InspectPlaybackAsync().WaitAsync(Deadline);
        Require(first.State == "Playing", action + " changed native playback away from Playing.");
        for (int i = 0; i < 5; i++)
        {
            await Task.Delay(100);
            var state = await player.InspectPlaybackAsync().WaitAsync(Deadline);
            Require(state.State == "Playing", action + " caused a delayed native pause/stop.");
            if (i == 4) Require(state.Time >= first.Time + 200, action + " left a Playing label but a frozen native clock.");
        }
    }

    private static async Task ObservePausedAsync(GalleryVideoPlayer player, string action)
    {
        var first = await player.InspectPlaybackAsync().WaitAsync(Deadline);
        Require(first.State == "Paused", action + " resumed the native decoder.");
        for (int i = 0; i < 6; i++)
        {
            await Task.Delay(100);
            var state = await player.InspectPlaybackAsync().WaitAsync(Deadline);
            Require(state.State == "Paused" && Math.Abs(state.Time - first.Time) <= 120,
                action + " resumed/advanced the paused decoder clock.");
        }
    }

    private static Point SliderPoint(Slider slider, double fraction)
    {
        Track track = slider.GetVisualDescendants().OfType<Track>().Single();
        Point trackPoint = SliderTrackPoint(track, slider.Orientation, fraction);
        return track.TranslatePoint(trackPoint, slider)
            ?? throw new InvalidOperationException("Native slider track has no screen transform.");
    }

    private static async Task NativeSliderClickAsync(MediaNativeInput native, Slider slider, double fraction)
    {
        Track track = slider.GetVisualDescendants().OfType<Track>().Single();
        Point trackPoint = SliderTrackPoint(track, slider.Orientation, fraction);
        TopLevel root = TopLevel.GetTopLevel(slider)
            ?? throw new InvalidOperationException("Native slider has no top-level input root.");
        PixelPoint screen = track.PointToScreen(trackPoint);
        Point rootPoint = root.PointToClient(screen);
        Visual? hit = root.InputHitTest(rootPoint) as Visual;
        Console.WriteLine("SMOKE native slider input geometry: " + System.Text.Json.JsonSerializer.Serialize(new
        {
            slider = slider.Name, orientation = slider.Orientation.ToString(), fraction,
            sliderBounds = slider.Bounds.ToString(), trackBounds = track.Bounds.ToString(),
            thumbBounds = track.Thumb?.Bounds.ToString(), trackPoint = trackPoint.ToString(),
            sliderPoint = track.TranslatePoint(trackPoint, slider)?.ToString(), screen = screen.ToString(),
            rootPoint = rootPoint.ToString(), hit = hit?.GetType().Name, hitName = (hit as Control)?.Name,
            hitSlider = hit?.GetSelfAndVisualAncestors().Contains(slider) == true,
            popupVisible = slider.GetVisualAncestors().OfType<Control>().FirstOrDefault(c => c.Name == "GalleryVideoVolumePopup")?.IsEffectivelyVisible
        }));
        Require(hit != null && hit.GetSelfAndVisualAncestors().Contains(slider),
            "Native slider point is not on the slider input route; see native slider input geometry.");
        List<string> route = new();
        void Pointer(object? _, PointerEventArgs e) => route.Add($"{e.RoutedEvent?.Name ?? "Pointer"}:{(e.Source is Control source ? source.Name ?? source.GetType().Name : e.Source?.GetType().Name)}:handled={e.Handled}");
        void Changed(object? _, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == Slider.ValueProperty) route.Add($"Value:{e.GetNewValue<double>():0.##}");
        }
        slider.AddHandler(InputElement.PointerPressedEvent, Pointer, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        slider.AddHandler(InputElement.PointerReleasedEvent, Pointer, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        slider.PropertyChanged += Changed;
        try
        {
            // Use the actual PART_Track as the native coordinate owner. This avoids a second
            // transform through the 32-DIP popup child while retaining the strict decoder edit.
            await native.ClickAsync(track, trackPoint);
        }
        finally
        {
            slider.RemoveHandler(InputElement.PointerPressedEvent, Pointer);
            slider.RemoveHandler(InputElement.PointerReleasedEvent, Pointer);
            slider.PropertyChanged -= Changed;
        }
        Console.WriteLine("SMOKE native slider event route: " + System.Text.Json.JsonSerializer.Serialize(route));
        Require(route.Any(entry => entry.StartsWith("PointerPressed:", StringComparison.Ordinal))
            && route.Any(entry => entry.StartsWith("PointerReleased:", StringComparison.Ordinal)),
            "Native slider click did not traverse the slider pointer route; see native slider event route.");
    }

    private static Point SliderTrackPoint(Track track, Orientation orientation, double fraction)
    {
        if (fraction is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(fraction));
        double thumb = track.Thumb is { } trackThumb
            ? orientation == Orientation.Vertical ? trackThumb.Bounds.Height : trackThumb.Bounds.Width
            : 12;
        return orientation == Orientation.Vertical
            ? new Point(track.Bounds.Width / 2, track.Bounds.Height - (thumb / 2 + fraction * (track.Bounds.Height - thumb)))
            : new Point(thumb / 2 + fraction * (track.Bounds.Width - thumb), track.Bounds.Height / 2);
    }

    private static async Task NativeSeekAsync(MediaNativeInput native, GalleryViewer viewer, double fraction, bool drag)
    {
        Slider seek = Find<Slider>(viewer, "GalleryVideoSeek");
        Require(seek.IsEffectivelyEnabled, "Native seek acceptance has no enabled seek slider.");
        // Saved autoplay deliberately waits long enough for auto-hide. Reveal the
        // transport with a real pointer move before asking the native backend to
        // target the slider; do not turn a hidden-control race into a routed click.
        await MediaInteractionSmokeHelpers.WaitForNativeTargetAsync(native, viewer, seek,
            "Native seek controls did not become a current hit target after revealing the gallery transport.");
        if (!drag) await native.ClickAsync(seek, SliderPoint(seek, fraction));
        else
        {
            Thumb thumb = seek.GetVisualDescendants().OfType<Track>().Single().Thumb!;
            double start = (seek.Value - seek.Minimum) / (seek.Maximum - seek.Minimum);
            await native.MoveAsync(thumb);
            await native.ButtonAsync(true);
            try
            {
                for (int i = 1; i <= 4; i++)
                    await native.MoveAsync(seek, SliderPoint(seek, start + (fraction - start) * i / 4));
            }
            finally { await native.ButtonAsync(false); }
        }
        await UntilAsync(async () =>
        {
            var state = await Player(viewer).InspectPlaybackAsync();
            return state.Length > 0 && Math.Abs(state.Time - state.Length * fraction) < 900;
        }, "Native seek did not move the actual decoder to the intended position.");
    }

    private static string FindSyntheticAudioFixture(string suppliedVideo)
    {
        // Works for repository runners and relocated assemblies without depending on a
        // personal path or downloading media. Missing fixture is a failure, not a skip.
        foreach (string start in new[] { Path.GetDirectoryName(Path.GetFullPath(suppliedVideo))!, Environment.CurrentDirectory, AppContext.BaseDirectory })
            for (DirectoryInfo? directory = new(start); directory != null; directory = directory.Parent)
            {
                string candidate = Path.Combine(directory.FullName, "tests", "Fixtures", "release102", "playback-vp8-audio.webm");
                if (File.Exists(candidate)) return candidate;
            }
        throw new FileNotFoundException("Pinned synthetic audio fixture tests/Fixtures/release102/playback-vp8-audio.webm is required.");
    }
}
