using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;

namespace MeowChan.DesktopSmoke;

/// <summary>
/// MU-00/08 real-input regressions. Setup may open a fixture/apply a private-profile binding;
/// every interaction under assertion crosses the supported OS driver -> Avalonia, never RaiseEvent/Focus/Value setters.
/// Uses the existing production decoder observer, not a second player or a new product test API.
/// </summary>
internal static class MediaInteractionChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        string report = Environment.GetEnvironmentVariable("MEOWCHAN_MEDIA_INTERACTION_REPORT")
            ?? throw new InvalidOperationException("Media input requires an explicit report path from test-media-interaction-smoke.py.");
        if (!Path.IsPathFullyQualified(report) || File.Exists(report) || !Directory.Exists(Path.GetDirectoryName(report)))
            throw new IOException("Media input report must have an existing parent and a fresh absolute filename.");
        string fixture = Environment.GetEnvironmentVariable("MEOWCHAN_VIDEO_FIXTURE")
            ?? throw new InvalidOperationException("Media input requires MEOWCHAN_VIDEO_FIXTURE.");
        List<object> events = new();
        List<string> failures = new(), completed = new();
        Stopwatch clock = Stopwatch.StartNew();
        int dropped = 0, routedKeys = 0;
        string stage = "setup";
        GalleryViewer? viewer = null;
        Config original = window.SessionHost.Config.Clone();
        MediaNativeInput? native = null;
        string outcome = "failed";
        void Record(string kind, object data)
        {
            if (events.Count < 2048) events.Add(new { ms = clock.Elapsed.TotalMilliseconds, stage, kind, data });
            else dropped++;
        }
        object Target(object? value) => value is Control c ? new { type = (string?)c.GetType().Name, name = c.Name } : new { type = value?.GetType().Name, name = (string?)null };
        object RoutingState(object? source, KeyEventArgs? key = null)
        {
            TopLevel? sourceTop = source is Visual visual ? TopLevel.GetTopLevel(visual) : null;
            TopLevel? viewerTop = viewer == null ? null : TopLevel.GetTopLevel(viewer);
            var focus = window.FocusManager?.GetFocusedElement();
            TopLevel? focusTop = focus is Visual focusedVisual ? TopLevel.GetTopLevel(focusedVisual) : null;
            return new
            {
                sourceTop = Target(sourceTop), viewerTop = Target(viewerTop), focusTop = Target(focusTop),
                sourceTopIsHost = ReferenceEquals(sourceTop, window), viewerTopIsHost = ReferenceEquals(viewerTop, window),
                sourceWithinViewer = source is Visual sourceVisual && viewer != null
                    && sourceVisual.GetVisualAncestors().Prepend(sourceVisual).Contains(viewer),
                window.IsActive, window.IsEnabled, capturing = ShortcutService.IsCapturing,
                shortcutRevision = ShortcutService.Revision,
                previousGesture = CommandBindings.Gesture("gallery.previous")?.ToString(),
                nextGesture = CommandBindings.Gesture("gallery.next")?.ToString(),
                matchesPrevious = key != null && ShortcutService.Current.Matches(key, "gallery.previous"),
                matchesNext = key != null && ShortcutService.Current.Matches(key, "gallery.next"),
                nativeInputOwnsKey = key != null && CommandRouter.IsNativeInput(source, key.Key),
                hostRoutingContextActive = CommandRouter.IsRoutingContextActive(window, source),
                viewerRoutingContextActive = viewer != null && CommandRouter.IsRoutingContextActive(viewer, source),
                hostHasOpenPopup = CommandRouter.HasOpenPopup(window),
                viewerHasOverlay = viewer?.IsOverlayOpen,
                ownedWindows = window.OwnedWindows.Select(owned => new
                {
                    target = Target(owned), owned.IsVisible, owned.IsActive, owned.IsEnabled,
                    isGalleryOverlay = viewer?.OwnsOverlayWindow(owned) == true
                }).ToArray()
            };
        }
        void ViewerKeyTunnel(object? _, KeyEventArgs e) => KeyEvent("viewer-key-tunnel", e);
        void ViewerKeyBubble(object? _, KeyEventArgs e) => KeyEvent("viewer-key-bubble", e);
        void KeyTunnel(object? _, KeyEventArgs e) => KeyEvent("key-tunnel", e);
        void KeyBubble(object? _, KeyEventArgs e) => KeyEvent("key-bubble", e);
        void KeyEvent(string kind, KeyEventArgs e)
        {
            routedKeys++;
            // Fixed scripted keys only; no text input/labels/paths/profile values are captured.
            if (e.Key is Key.Left or Key.Right or Key.Home or Key.End or Key.Escape or Key.Space or Key.F7 or Key.F8 or Key.LeftCtrl or Key.LeftShift)
                Record(kind, new { key = e.Key.ToString(), modifiers = e.KeyModifiers.ToString(), e.Handled,
                    source = Target(e.Source), focus = Target(window.FocusManager?.GetFocusedElement()), post = viewer?.CaptureState().PostId,
                    routing = RoutingState(e.Source, e) });
        }
        void Pointer(object? _, PointerEventArgs e) => Record("pointer-" + e.RoutedEvent?.Name,
            new { source = Target(e.Source), focus = Target(window.FocusManager?.GetFocusedElement()), post = viewer?.CaptureState().PostId });
        HashSet<Window> observedRoots = new();
        void ObserveRoot(Window inputRoot)
        {
            if (!observedRoots.Add(inputRoot)) return;
            inputRoot.AddHandler(InputElement.KeyDownEvent, KeyTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);
            inputRoot.AddHandler(InputElement.KeyDownEvent, KeyBubble, RoutingStrategies.Bubble, handledEventsToo: true);
            inputRoot.AddHandler(InputElement.PointerPressedEvent, Pointer, RoutingStrategies.Tunnel, handledEventsToo: true);
            inputRoot.AddHandler(InputElement.PointerReleasedEvent, Pointer, RoutingStrategies.Tunnel, handledEventsToo: true);
        }
        ObserveRoot(window);
        /* main root registered above */

        try
        {
            native = new MediaNativeInput(window);
            await Program.WaitUntilAsync(() => window.IsActive);
            // Only an in-session test override using the established settings owner. No new defaults or schema.
            Config configured = original.Clone();
            configured.VideoAutoplay = true;
            configured.GalleryPlaybackMode = "Manual";
            configured.GalleryMediaFocus = false;
            configured.GalleryOverlayControls = configured.GalleryAutoHideControls = false;
            configured.ShortcutBindings = configured.ShortcutBindings
                .Set("gallery.previous", new[] { ShortcutGesture.Parse("Left"), ShortcutGesture.Parse("Primary+Shift+F7") })
                .Set("gallery.next", new[] { ShortcutGesture.Parse("Right"), ShortcutGesture.Parse("Primary+Shift+F8") });
            ShortcutService.ValidateForCommit(configured.ShortcutBindings);
            long revision = ShortcutService.Revision;
            await window.SessionHost.ApplySettingsAsync(configured);
            await Program.WaitUntilAsync(() => ShortcutService.Revision > revision);
            foreach (var (id, plain, custom) in new[] { ("gallery.previous", Key.Left, Key.F7), ("gallery.next", Key.Right, Key.F8) })
            {
                var effective = ShortcutService.Current;
                var primary = EffectiveShortcutBindings.CurrentPlatform == ShortcutPlatform.MacOS ? KeyModifiers.Meta : KeyModifiers.Control;
                Require(effective.For(id).Count == 2 && effective.For(id).Any(gesture => gesture.Key == plain.ToString())
                    && effective.For(id).Any(gesture => gesture.Key == custom.ToString())
                    && effective.Matches(new KeyEventArgs { Key = plain }, id)
                    && effective.Matches(new KeyEventArgs { Key = custom, KeyModifiers = primary | KeyModifiers.Shift }, id),
                    "Effective default AND custom media bindings were not published: " + id);
            }

            // Stages are independent; a baseline hover failure must not hide routing/loop failures.
            await StageAsync("hover-click", async () =>
            {
                await FreshVideoAsync();
                Slider seek = Seek();
                await SeekClickAsync(.20);
                Require(seek.IsPointerOver && seek.IsKeyboardFocusWithin, "Native timeline click did not leave pointer AND keyboard focus on slider.");
                await ProgressAsync("pointer-remains-over-timeline");
                await DragAsync(.40, release: true);
                await WaitPlaybackAsync(p => p.State == "Playing" && Math.Abs(p.Time - p.Length * .40) < 1200);
                await ProgressAsync("playing-drag-does-not-pause");
            });
            await StageAsync("focused-pointer-away", async () =>
            {
                await FreshVideoAsync();
                Slider seek = Seek();
                await SeekClickAsync(.20);
                await native.MoveAsync(Named<Button>("GalleryVideoPlayPause")); // move only; do not steal keyboard focus
                Require(!seek.IsPointerOver && seek.IsKeyboardFocusWithin, "Pointer-away repro did not retain real slider keyboard focus.");
                await ProgressAsync("focus-remains-with-pointer-away");
                await CheckVolumeAndPanelsAsync("Playing");
            });
            await StageAsync("paused-drag-key", async () =>
            {
                await FreshVideoAsync();
                await native.ClickAsync(Named<Button>("GalleryVideoPlayPause"));
                await WaitPlaybackAsync(p => p.State == "Paused");
                await SeekClickAsync(.25);
                await DragAsync(.45, release: true);
                await WaitPlaybackAsync(p => p.State == "Paused" && Math.Abs(p.Time - p.Length * .45) < 900);
                Slider seek = Seek();
                double beforeKey = seek.Value;
                int keyStart = events.Count;
                await native.KeyAsync(MediaNativeInput.Right);
                Require(events.Count > keyStart && seek.IsKeyboardFocusWithin && seek.Value > beforeKey,
                    "Real Right key did not reach/adjust the focused slider.");
                Require(viewer!.CaptureState().PostId == "2000", "Plain slider Right escaped into gallery navigation.");
                GalleryVideoPlayer.PlaybackEvidence first = await SampleAsync("paused-after-seek");
                await Task.Delay(650);
                GalleryVideoPlayer.PlaybackEvidence last = await SampleAsync("paused-held");
                Require(first.State == "Paused" && last.State == "Paused" && Math.Abs(last.Time - first.Time) <= 120,
                    "Paused native seek resumed/advanced the decoder clock.");
                Require(Math.Abs(seek.Value - last.Time * 1000d / last.Length) <= Math.Max(8, 900_000d / last.Length), "Paused native clock and visible seek diverged.");
                await CheckVolumeAndPanelsAsync("Paused");
                await native.ClickAsync(Named<Button>("GalleryVideoPlayPause"));
                await WaitPlaybackAsync(p => p.State == "Playing");
                await ProgressAsync("resumed-after-paused-seek");
            });
            await StageAsync("custom-routing-loop", async () =>
            {
                await FreshVideoAsync();
                await CheckHiddenFirstNavigationAsync();
                ToggleButton loop = Named<ToggleButton>("GalleryVideoLoop");
                if (loop.IsChecked != true) await native.ClickAsync(loop);
                Require(loop.IsChecked == true, "Native Loop activation did not update the visible toggle.");
                // Before slider interaction, bindings must work from an ordinary toolbar control.
                await NavigateAsync(next: true, "2001");
                await NavigateAsync(next: false, "2000");
                await ReadyAsync();
                await SeekClickAsync(.20);
                double before = Seek().Value;
                await native.KeyAsync(MediaNativeInput.Right);
                Require(Seek().IsKeyboardFocusWithin && Seek().Value > before && viewer!.CaptureState().PostId == "2000",
                    "The unmodified native slider arrow was lost or navigated the gallery.");
                // Modified effective binding is unrelated to native slider editing, unlike plain Right.
                await NavigateAsync(next: true, "2001");
                await NavigateAsync(next: true, "2002");
                await ReadyAsync();
                var loopingB = await SampleAsync("loop-video-b");
                Require(loopingB.LoopEnabled && loop.IsChecked == true,
                    "Loop did not survive video A -> image -> video B in this gallery.");
                await SeekClickAsync(.90);
                await WaitPlaybackAsync(p => p.LoopRestarts > loopingB.LoopRestarts && p.State == "Playing" && p.Time < p.Length / 2);
                Require(viewer!.CaptureState().PostId == "2002", "Loop end navigated away instead of repeating the current video.");
                await SeekClickAsync(.20);
                await NavigateAsync(next: false, "2001");
                await NavigateAsync(next: false, "2000");
                await ReadyAsync();
                Require((await SampleAsync("loop-return-a")).LoopEnabled, "Loop was lost on reverse media navigation.");
                await native.ClickAsync(loop);
                Require(loop.IsChecked == false, "Native Loop off did not update toggle.");
                await NavigateAsync(next: true, "2001");
                await NavigateAsync(next: true, "2002");
                await ReadyAsync();
                Require(!(await SampleAsync("loop-off-video-b")).LoopEnabled, "Retired medium resurrected Loop after explicit off.");
            });
            await StageAsync("retired-gesture-transition", async () =>
            {
                await FreshVideoAsync();
                // Keep the OS button down across navigation. A native seek is submitted immediately
                // before retiring A; release must not seek B, replay A or select another medium.
                await DragAsync(.75, release: false);
                try
                {
                    await NavigateAsync(next: true, "2001", settle: false);
                    await NavigateAsync(next: true, "2002", settle: false);
                }
                finally { await native.ButtonAsync(false); }
                await ReadyAsync();
                GalleryVideoPlayer.PlaybackEvidence initial = await SampleAsync("successor-ready");
                Require(initial.Time < 2200, "Successor inherited predecessor's late seek position.");
                await Task.Delay(850);
                GalleryVideoPlayer.PlaybackEvidence last = await SampleAsync("successor-settled");
                Require(viewer!.CaptureState().PostId == "2002" && last.State == "Playing" && last.Time < 3500,
                    "Retired gesture/callback changed the settled successor identity/clock/state.");
                Require(last.Time > initial.Time + 300, "Successor clock did not advance after transition.");
            });
            if (dropped != 0) failures.Add("trace overflow: incomplete input evidence");
            Require(failures.Count == 0, "Media native regression failures: " + string.Join(" | ", failures));
            outcome = "passed";
            Console.WriteLine("SMOKE media native input: click/hover/focus, playing/paused seek-volume-panels, first-key hidden navigation with default+custom bindings, Loop video-image-video, retired gesture passed on the recorded native backend.");
        }
        catch (Exception e)
        {
            Record("fatal", new { type = e.GetType().Name });
            // Do not store arbitrary native/library exception text (may contain private paths).
            throw;
        }
        finally
        {
            native?.Dispose();
            if (viewer != null)
            {
                viewer.RemoveHandler(InputElement.KeyDownEvent, ViewerKeyTunnel);
                viewer.RemoveHandler(InputElement.KeyDownEvent, ViewerKeyBubble);
            }
            foreach (Window inputRoot in observedRoots)
            {
                inputRoot.RemoveHandler(InputElement.KeyDownEvent, KeyTunnel);
                inputRoot.RemoveHandler(InputElement.KeyDownEvent, KeyBubble);
                inputRoot.RemoveHandler(InputElement.PointerPressedEvent, Pointer);
                inputRoot.RemoveHandler(InputElement.PointerReleasedEvent, Pointer);
            }
            try { await window.SessionHost.ApplySettingsAsync(original); }
            catch { outcome = "failed"; failures.Add("private-profile binding restore failed"); throw; }
            finally
            {
                File.WriteAllText(report, JsonSerializer.Serialize(new
                {
                    schema = 1, outcome, backend = native?.Backend ?? "unsupported/unavailable", os = Environment.OSVersion.Platform.ToString(),
                    scale = window.RenderScaling, sourceDigest = Environment.GetEnvironmentVariable("MEOWCHAN_MEDIA_SOURCE_DIGEST"),
                    assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Program).Assembly.Location))).ToLowerInvariant(),
                    fixtureImageSha256 = VideoGalleryChecks.FixtureImageSha256,
                    fixtureSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture))).ToLowerInvariant(),
                    completed, failures, dropped, events,
                    limits = "Only the recorded OS/backend was executed; no physical audio, MP4, touch-device, slideshow end-to-end, restart persistence or forced late-callback proof. Transition stress is a real-input race, not deterministic callback injection. Test stages are not production generation IDs. Source digest is inventory, not compiler provenance."
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
        }

        T Named<T>(string name) where T : Control => viewer!.GetGalleryVisualDescendants().OfType<T>().Single(c => c.Name == name);
        Slider Seek() => Named<Slider>("GalleryVideoSeek");
        GalleryVideoPlayer Player() => viewer!.GetGalleryVisualDescendants().OfType<GalleryVideoPlayer>().Single();
        async Task<GalleryVideoPlayer.PlaybackEvidence> SampleAsync(string label)
        {
            var evidence = await Player().InspectPlaybackAsync().WaitAsync(TimeSpan.FromSeconds(4));
            foreach (Window inputRoot in window.OwnedWindows.Where(w => viewer!.OwnsOverlayWindow(w))) ObserveRoot(inputRoot);
            Slider seek = Seek();
            Record("sample", new { label, post = viewer!.CaptureState().PostId, evidence,
                slider = seek.Value, seek.IsPointerOver, seek.IsKeyboardFocusWithin,
                focus = Target(window.FocusManager?.GetFocusedElement()), window.IsActive });
            return evidence;
        }
        async Task WaitPlaybackAsync(Func<GalleryVideoPlayer.PlaybackEvidence, bool> predicate)
        {
            long start = clock.ElapsedMilliseconds;
            while (clock.ElapsedMilliseconds - start < 6500)
            {
                var evidence = await SampleAsync("wait");
                if (predicate(evidence)) return;
                await Task.Delay(80);
            }
            throw new TimeoutException("Media decoder state deadline expired in " + stage);
        }
        async Task ReadyAsync()
        {
            await WaitPlaybackAsync(p => p.HasPlayback && p.State == "Playing" && p.Seekable && p.Length >= 8000
                && p.Length <= 60000 && (OperatingSystem.IsWindows() ? p.DisplayedFrames >= 3 : p.SurfaceFrames >= 3 && p.SurfaceNonUniform));
            Require(Seek().IsEffectivelyEnabled, "Ready video did not enable seek.");
        }
        async Task FreshVideoAsync()
        {
            if (viewer != null)
            {
                viewer.RemoveHandler(InputElement.KeyDownEvent, ViewerKeyTunnel);
                viewer.RemoveHandler(InputElement.KeyDownEvent, ViewerKeyBubble);
                // Recovery setup: a previous failed edge assertion may have left controls hidden.
                viewer.ConfigurePlayback(new GalleryPlaybackPreferences().WithControlsOverlay(false));
                await native!.ClickAsync(Named<Button>("GalleryClose"));
                await Program.WaitUntilAsync(() => !window.FindControl<ContentControl>("GalleryHost")!.IsVisible);
                await window.GalleryCleanupTask.WaitAsync(TimeSpan.FromSeconds(8));
            }
            // Fixture setup only, not evidence for opening by native input.
            await window.OpenGalleryAsync(VideoGalleryChecks.Id);
            viewer = (GalleryViewer)window.FindControl<ContentControl>("GalleryHost")!.Content!;
            viewer.AddHandler(InputElement.KeyDownEvent, ViewerKeyTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);
            viewer.AddHandler(InputElement.KeyDownEvent, ViewerKeyBubble, RoutingStrategies.Bubble, handledEventsToo: true);
            Require(viewer.CaptureState().PostId == "2000", "Fresh fixture did not start at video A.");
            await ReadyAsync();
        }
        Point TrackPoint(double fraction) => MediaInteractionSmokeHelpers.TrackPoint(Seek(), fraction);
        async Task SeekClickAsync(double fraction)
        {
            Record("inject", new { action = "native-track-click", fraction });
            await native!.ClickAsync(Seek(), TrackPoint(fraction));
            Require(Seek().IsKeyboardFocusWithin, "Native timeline click did not acquire focus.");
            await WaitPlaybackAsync(p => Math.Abs(p.Time - fraction * p.Length) < 1000);
        }
        async Task DragAsync(double fraction, bool release)
        {
            Slider seek = Seek();
            Thumb thumb = seek.GetVisualDescendants().OfType<Track>().Single().Thumb!;
            Record("inject", new { action = "native-thumb-drag", fraction, release });
            string stateBefore = (await SampleAsync("before-native-drag")).State;
            await native!.MoveAsync(thumb);
            await native.ButtonAsync(true);
            try
            {
                // Multiple OS motions exercise capture/drag, rather than assigning Slider.Value.
                double start = (seek.Value - seek.Minimum) / (seek.Maximum - seek.Minimum);
                for (int i = 1; i <= 4; i++)
                {
                    await native.MoveAsync(seek, TrackPoint(start + (fraction - start) * i / 4));
                    Require((await SampleAsync("native-drag-held")).State == stateBefore,
                        "An active native seek gesture changed the playback intent before release.");
                }
            }
            catch { await native.ButtonAsync(false); throw; }
            finally { if (release) await native.ButtonAsync(false); }
        }
        async Task ProgressAsync(string label)
        {
            var first = await SampleAsync(label + "-start");
            double firstSlider = Seek().Value;
            Require(first.State == "Playing", "Interaction paused playback before " + label);
            for (int i = 0; i < 9; i++)
            {
                await Task.Delay(150);
                Require((await SampleAsync(label)).State == "Playing", "Interaction transiently paused playback during " + label);
            }
            var last = await SampleAsync(label + "-end");
            double elapsed = last.Time - first.Time;
            Require(last.State == "Playing" && elapsed >= 600, "Real decoder clock did not progress during " + label);
            Require(Seek().Value > firstSlider + Math.Max(3, elapsed * 1000d / last.Length * .5), "Visible timeline froze while real decoder advanced during " + label);
            Require(Math.Abs(Seek().Value - last.Time * 1000d / last.Length) <= Math.Max(8, 750_000d / last.Length), "Visible timeline is not synchronized with native clock during " + label);
        }
        async Task NavigateAsync(bool next, string post, bool settle = true)
        {
            Record("inject", new { action = next ? "custom-next" : "custom-previous", expectedPost = post,
                actualPost = viewer!.CaptureState().PostId, focus = Target(window.FocusManager?.GetFocusedElement()),
                routing = RoutingState(window.FocusManager?.GetFocusedElement()) });
            int beforeKeys = events.Count;
            await native!.KeyAsync(next ? MediaNativeInput.F8 : MediaNativeInput.F7, modified: true);
            await Program.WaitUntilAsync(() => viewer!.CaptureState().PostId == post);
            if (settle) await Task.Delay(180);
            Require(events.Count > beforeKeys && viewer!.CaptureState().PostId == post,
                "Custom native media key did not produce exactly the expected single-step selection.");
            Record("selection", new { post, next, focus = Target(window.FocusManager?.GetFocusedElement()),
                routing = RoutingState(window.FocusManager?.GetFocusedElement()) });
        }
        async Task CheckVolumeAndPanelsAsync(string expected)
        {
            if (expected == "Playing") await SeekClickAsync(.05); // Keep short supported fixtures away from a natural end.
            Slider volume = Named<Slider>("GalleryVideoVolume");
            await native!.ClickAsync(volume, MediaInteractionSmokeHelpers.TrackPoint(volume, .60));
            Require(volume.IsKeyboardFocusWithin, "Native volume click did not focus the actual slider.");
            await native.KeyAsync(MediaNativeInput.Home);
            Require(volume.Value == volume.Minimum && viewer!.CaptureState().PostId == "2000",
                "Native volume Home did not stay local to the slider.");
            await native.KeyAsync(MediaNativeInput.End);
            await WaitPlaybackAsync(p => p.Volume == 100 && p.State == expected);
            Require(volume.Value == volume.Maximum && viewer!.CaptureState().PostId == "2000",
                "Native volume End escaped or failed to adjust volume.");
            await MediaInteractionSmokeHelpers.HoldStateAsync(Player(), expected, "Volume click/Home/End");
            foreach (string panel in new[] { "GalleryToggleContext", "GalleryHelp" })
            {
                if (expected == "Playing") await SeekClickAsync(.05);
                await native.ClickAsync(Named<Button>(panel));
                Require(panel == "GalleryHelp" ? viewer!.IsOverlayOpen : viewer!.CaptureState().ContextOpen,
                    "Native panel action failed to open its actual panel: " + panel);
                await MediaInteractionSmokeHelpers.HoldStateAsync(Player(), expected, panel + " open");
                await native.KeyAsync(MediaNativeInput.Escape);
                Require(!viewer!.CaptureState().ContextOpen && !viewer.IsOverlayOpen,
                    "Native Escape did not dismiss only the active panel.");
                await MediaInteractionSmokeHelpers.HoldStateAsync(Player(), expected, panel + " dismissed");
                Require(window.FindControl<ContentControl>("GalleryHost")!.IsVisible && viewer.CaptureState().PostId == "2000",
                    "Panel dismissal closed/navigated the gallery.");
            }
            if (expected == "Playing") await SeekClickAsync(.05);
            await native.ClickAsync(Named<ComboBox>("GalleryPlaybackModeSelector"));
            Require(Named<ComboBox>("GalleryPlaybackModeSelector").IsDropDownOpen, "Native mode click failed to open the menu.");
            await MediaInteractionSmokeHelpers.HoldStateAsync(Player(), expected, "Playback menu open");
            await native.KeyAsync(MediaNativeInput.Escape);
            Require(!Named<ComboBox>("GalleryPlaybackModeSelector").IsDropDownOpen && viewer!.CaptureState().PostId == "2000",
                "Menu Escape escaped into gallery close/navigation.");
            await MediaInteractionSmokeHelpers.HoldStateAsync(Player(), expected, "Playback menu dismissed");
        }
        async Task CheckHiddenFirstNavigationAsync()
        {
            GalleryPlaybackPreferences docked = GalleryPlaybackPreferences.FromConfig(window.SessionHost.Config).WithControlsOverlay(false);
            List<int> moves = new();
            void Moved(int delta) => moves.Add(delta);
            viewer!.MoveRequested += Moved;
            try
            {
                foreach (var (key, expectedPost, delta) in new[] {
                    (MediaNativeInput.Right, "2002", 1), (MediaNativeInput.Left, "2000", -1),
                    (MediaNativeInput.F8, "2002", 1), (MediaNativeInput.F7, "2000", -1) })
                {
                    // Start on the verified image so hidden-controls acceptance does not assume
                    // transparent native-video support on Windows. Setup navigation is not evidence.
                    viewer.ConfigurePlayback(docked);
                    await NavigateAsync(viewer.CaptureState().PostId == "2000", "2001");
                    viewer.ConfigurePlayback(docked.WithControlsOverlay(true));
                    await native!.MoveAsync(viewer, new Point(viewer.Bounds.Width * .45, viewer.Bounds.Height - 2));
                    await Program.WaitUntilAsync(() => Named<Border>("GalleryOverlayControls").IsEffectivelyVisible);
                    await native.ClickAsync(Named<Button>("GalleryFit"));
                    await native.MoveAsync(viewer, new Point(viewer.Bounds.Width * .45, viewer.Bounds.Height * .30));
                    await Program.WaitUntilAsync(() => !Named<Border>("GalleryOverlayControls").IsEffectivelyVisible);
                    string? before = viewer.CaptureState().PostId;
                    int beforeKeys = routedKeys;
                    moves.Clear();
                    Record("inject", new { action = "first-hidden-navigation-key", key, before, expectedPost });
                    // Exactly ONE OS press: no reveal preflight and no retry of the intended action.
                    await native.KeyAsync(key, modified: key == MediaNativeInput.F7 || key == MediaNativeInput.F8);
                    await Program.WaitUntilAsync(() => viewer.CaptureState().PostId == expectedPost);
                    await Task.Delay(180);
                    Require(routedKeys > beforeKeys && before != expectedPost && moves.SequenceEqual(new[] { delta })
                        && viewer.CaptureState().PostId == expectedPost,
                        "The first hidden-controls OS key merely revealed chrome, navigated twice or did not select the expected medium.");
                    Record("selection", new { action = "first-hidden-navigation-key", key, before, expectedPost, moves = moves.ToArray() });
                }
            }
            finally { viewer.MoveRequested -= Moved; viewer.ConfigurePlayback(docked); }
            await ReadyAsync(); // Last first-key case returned to video A.
        }
        async Task StageAsync(string name, Func<Task> action)
        {
            stage = name;
            try
            {
                await action(); completed.Add(name);
                Record("assertions-passed", new { name });
                Program.CaptureWindow(window, "smoke-media-" + name + ".png");
            }
            catch (Exception e)
            {
                failures.Add(name + ": " + (e is TimeoutException ? "deadline expired" : e is InvalidOperationException ? e.Message : e.GetType().Name));
                Record("assertions-failed", new { name, type = e.GetType().Name, post = viewer?.CaptureState().PostId,
                    focus = Target(window.FocusManager?.GetFocusedElement()), routing = RoutingState(window.FocusManager?.GetFocusedElement()) });
                Program.CaptureWindow(window, "smoke-media-" + name + "-failed.png");
            }
        }
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
