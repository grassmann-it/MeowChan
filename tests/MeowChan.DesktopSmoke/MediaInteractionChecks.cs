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
using MeowChan.Diagnostics;

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
        using var playbackTrace = PerformanceDiagnostics.BeginRun("media-native-input",
            Environment.GetEnvironmentVariable("MEOWCHAN_MEDIA_INTERACTION_SOURCE_DIGEST") ?? "unknown", "media-native-fixture");
        PerformanceSnapshot PlaybackTrace()
        {
            var snapshot = playbackTrace.Snapshot();
            return snapshot with { Events = snapshot.Events.Where(e => e.Phase.StartsWith("video.", StringComparison.Ordinal)).ToArray() };
        }
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
            if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.Escape or Key.Space or Key.F7 or Key.F8 or Key.LeftCtrl or Key.LeftShift)
                Record(kind, new { key = e.Key.ToString(), modifiers = e.KeyModifiers.ToString(), e.Handled,
                    source = Target(e.Source), focus = Target(window.FocusManager?.GetFocusedElement()), post = viewer?.CaptureState().PostId,
                    routing = RoutingState(e.Source, e) });
        }
        int timelinePointerPresses = 0;
        void Pointer(object? _, PointerEventArgs e)
        {
            if (e.RoutedEvent == InputElement.PointerPressedEvent && e.Source is Visual source
                && source.GetSelfAndVisualAncestors().OfType<GalleryTransportSlider>().Any())
                timelinePointerPresses++;
            Record("pointer-" + e.RoutedEvent?.Name,
                new { source = Target(e.Source), focus = Target(window.FocusManager?.GetFocusedElement()), post = viewer?.CaptureState().PostId });
        }
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
            native = new MediaNativeInput(window, Record);
            await Program.WaitUntilAsync(() => window.IsActive);
            // Only an in-session test override using the established settings owner. No new defaults or schema.
            Config configured = original.Clone();
            configured.VideoAutoplay = true;
            configured.GalleryPlaybackMode = "Manual";
            configured.GalleryMediaFocus = false;
            configured.GalleryOverlayControls = configured.GalleryAutoHideControls = false;
            configured.ShortcutBindings = configured.ShortcutBindings
                .Set("gallery.previous", new[] { ShortcutGesture.Parse("Left"), ShortcutGesture.Parse("Primary+Shift+F7") })
                .Set("gallery.next", new[] { ShortcutGesture.Parse("Right"), ShortcutGesture.Parse("Primary+Shift+F8") })
                .Set("video.volume-up", new[] { ShortcutGesture.Parse("Up") })
                .Set("video.volume-down", new[] { ShortcutGesture.Parse("Down") });
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
                Require(seek.IsPointerOver && seek.IsKeyboardFocusWithin, "Native timeline click did not preserve pointer and keyboard focus.");
                await ProgressAsync("pointer-remains-over-timeline");
                await DragAsync(.40, release: true);
                await WaitPlaybackAsync(p => p.State == "Playing" && Math.Abs(p.Time - p.Length * .40) < 1200);
                await ProgressAsync("playing-drag-does-not-pause");
                await CheckTimelineVolumeKeysAsync("Playing");
            });
            await StageAsync("focused-pointer-away", async () =>
            {
                await FreshVideoAsync();
                Slider seek = Seek();
                await SeekClickAsync(.20);
                await native.MoveAsync(() => NativeTargetAsync<Button>("GalleryVideoPlayPause")); // move only; do not steal keyboard focus
                Require(!seek.IsPointerOver && seek.IsKeyboardFocusWithin, "Pointer-away repro lost actual timeline focus.");
                await ProgressAsync("focus-remains-with-pointer-away");
                await CheckVolumeAndPanelsAsync("Playing");
            });
            await StageAsync("paused-drag-key", async () =>
            {
                await FreshVideoAsync();
                await native.ClickAsync(() => NativeTargetAsync<Button>("GalleryVideoPlayPause"));
                await WaitPlaybackAsync(p => p.State == "Paused");
                await SeekClickAsync(.25);
                await DragAsync(.45, release: true);
                await WaitPlaybackAsync(p => p.State == "Paused" && Math.Abs(p.Time - p.Length * .45) < 900);
                Slider seek = Seek();
                GalleryVideoPlayer.PlaybackEvidence first = await SampleAsync("paused-after-seek");
                await Task.Delay(650);
                GalleryVideoPlayer.PlaybackEvidence last = await SampleAsync("paused-held");
                Require(first.State == "Paused" && last.State == "Paused" && Math.Abs(last.Time - first.Time) <= 120,
                    "Paused native seek resumed/advanced the decoder clock.");
                Require(Math.Abs(seek.Value - last.Time * 1000d / last.Length) <= Math.Max(8, 900_000d / last.Length), "Paused native clock and visible seek diverged.");
                await CheckTimelineVolumeKeysAsync("Paused");
                await CheckVolumeAndPanelsAsync("Paused");
                // Seek again to establish genuine pointer input immediately before the key.
                await SeekClickAsync(.45);
                int keyStart = events.Count;
                await native.KeyAsync(MediaNativeInput.Right); // exactly one, no keyboard seek
                await Program.WaitUntilAsync(() => viewer!.CaptureState().PostId == "2001");
                Require(events.Count > keyStart && !seek.IsKeyboardFocusWithin,
                    "First Right after a paused pointer drag did not navigate to the image.");
                await Task.Delay(650);
                Require(viewer!.CaptureState().PostId == "2001", "Retired paused seek restarted or navigated late.");
                await NavigateAsync(next: false, "2000");
                await ReadyAsync();
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
                await native.KeyAsync(MediaNativeInput.Right); // first key after pointer seek
                await Program.WaitUntilAsync(() => viewer!.CaptureState().PostId == "2001");
                Require(viewer!.CaptureState().PostId == "2001", "First Right after timeline click did not select the image.");
                // Modified effective bindings remain independent, not hard-coded arrows.
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
            Console.WriteLine("SMOKE media native input: click/hover/focus, playing/paused seek-panels (volume editing/focus only with available native output; otherwise disabled-pointer invariance and seek-focus navigation), first-key hidden navigation with default+custom bindings, Loop video-image-video, retired gesture passed on the recorded native backend.");
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
                    completed, failures, dropped, events, playbackTrace = PlaybackTrace(),
                    limits = "Only the recorded OS/backend was executed; no physical audio, MP4, touch-device, slideshow end-to-end, restart persistence or forced late-callback proof. Transition stress is a real-input race, not deterministic callback injection. Test stages are not production generation IDs. Source digest is inventory, not compiler provenance."
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
        }

        IEnumerable<Visual> EvidenceVisuals()
        {
            GalleryViewer current = viewer!;
            IEnumerable<Visual> visuals = current.GetGalleryVisualDescendants();
            if (OperatingSystem.IsMacOS())
            {
                // The software-video presentation can leave the shared transport under its
                // player root while GalleryControlTransfer is moving it into the dock. That
                // root is still the authoritative control tree, even when it is not included
                // by the viewer's attached descendants for this UI turn.
                foreach (GalleryVideoPlayer player in visuals.OfType<GalleryVideoPlayer>())
                {
                    visuals = visuals.Concat(new[] { player.TransportControl }.Concat(player.TransportControl.GetVisualDescendants()));
                    break;
                }
            }
            return visuals.Distinct();
        }
        T Named<T>(string name) where T : Control
        {
            T[] matches = EvidenceVisuals().OfType<T>().Where(control => control.Name == name).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException($"Expected exactly one {typeof(T).Name} named {name}; found {matches.Length} in the current gallery presentation.");
            return matches[0];
        }
        async Task<T> NativeTargetAsync<T>(string name) where T : Control
        {
            // A native overlay/control transfer can replace the retained instance between
            // two dispatcher turns. Resolve the current visual-tree target on every probe;
            // waiting on the first instance leaves SendInput pointed at a retired root.
            T? target = null;
            await Program.WaitUntilAsync(() =>
            {
                try
                {
                    target = Named<T>(name);
                    return MediaInteractionSmokeHelpers.IsPointerTargetReady(target);
                }
                catch (InvalidOperationException)
                {
                    // During reparenting there can briefly be zero or multiple named
                    // controls. That is a target transition, not a smoke assertion.
                    target = null;
                    return false;
                }
                catch (ArgumentException)
                {
                    // Avalonia can reject a cross-root conversion while the retained
                    // control is moving into its current native transport root. Retry
                    // the probe so input is sent only to the settled current instance.
                    target = null;
                    return false;
                }
            }, () => $"The {name} control did not settle as a native hit target.");
            return target!;
        }
        async Task<T> NativeOverflowTargetAsync<T>(string name, bool requireHitTest = true) where T : Control
        {
            // Opening the overflow menu reparents its rows into the popup. Do not keep
            // the pre-open control returned by Named<T>: it may be the retained template
            // row and never become a valid native target after the reparenting turn.
            T? target = null;
            string? previousPredicate = null;
            int transitions = 0, probes = 0;
            object? lastState = null;
            await Program.WaitUntilAsync(() =>
            {
                target = null;
                probes++;
                TopLevel? popup = null;
                ScrollViewer? scroller = viewer?.OverflowFlyout.Content as ScrollViewer;
                Point? centerInRoot = null;
                string operation = "menu-state";
                bool Observe(string predicate)
                {
                    Control? content = viewer?.OverflowMenuContent;
                    lastState = new
                    {
                        name, requireHitTest, predicate, operation, probes,
                        overflowOpen = viewer?.IsOverflowOpen,
                        contentAttached = content?.IsAttachedToVisualTree(),
                        contentVisible = content?.IsEffectivelyVisible,
                        contentBounds = content?.Bounds.ToString(),
                        contentRoot = Target(content == null ? null : TopLevel.GetTopLevel(content)),
                        target = Target(target), attached = target?.IsAttachedToVisualTree(),
                        visible = target?.IsEffectivelyVisible, enabled = target?.IsEffectivelyEnabled,
                        arrangeValid = target?.IsArrangeValid, bounds = target?.Bounds.ToString(),
                        root = Target(popup), rootIsMainWindow = ReferenceEquals(popup, window),
                        rootVisible = popup?.IsVisible, clientSize = popup?.ClientSize.ToString(),
                        // An OverlayPopupHost legitimately shares the main TopLevel.
                        // Record it separately; do not infer popup ownership from TopLevel alone.
                        popupAncestry = (target as Control ?? content)?.GetVisualAncestors()
                            .Take(16).Select(v => v.GetType().Name).ToArray(),
                        centerInRoot = centerInRoot?.ToString(),
                        scrollOffset = scroller?.Offset.ToString(), viewport = scroller?.Viewport.ToString(),
                        extent = scroller?.Extent.ToString(),
                        focus = Target(window.FocusManager?.GetFocusedElement()), window.IsActive
                    };
                    // Keep first/changed predicates bounded, plus an unconditional final
                    // snapshot on timeout. A long layout race must not exhaust input evidence.
                    if (predicate != previousPredicate && transitions < 16)
                    {
                        Record("overflow-target-probe", lastState);
                        transitions++;
                    }
                    previousPredicate = predicate;
                    return predicate == "ready";
                }
                try
                {
                    if (viewer is not { IsOverflowOpen: true }) return Observe("overflow-closed");
                    if (!viewer.OverflowMenuContent.IsAttachedToVisualTree()) return Observe("content-detached");
                    if (!viewer.OverflowMenuContent.IsEffectivelyVisible) return Observe("content-hidden");
                    if (viewer.OverflowMenuContent.Bounds.Width <= 0 || viewer.OverflowMenuContent.Bounds.Height <= 0)
                        return Observe("content-empty-bounds");
                    target = viewer.OverflowMenuContent.GetVisualDescendants()
                        .OfType<T>().FirstOrDefault(control => control.Name == name);
                    if (target is null) return Observe("target-missing");

                    // Resolve the current row, then scroll it into the bounded viewport.
                    // Never reuse a retained row or screen coordinate from a prior popup.
                    operation = "layout-and-scroll";
                    popup = TopLevel.GetTopLevel(target);
                    popup?.UpdateLayout();
                    scroller?.UpdateLayout();
                    target.BringIntoView();
                    if (scroller != null && target.TranslatePoint(default, scroller) is { } origin)
                    {
                        double y = scroller.Offset.Y;
                        if (origin.Y < 0) y += origin.Y;
                        else if (origin.Y + target.Bounds.Height > scroller.Viewport.Height)
                            y += origin.Y + target.Bounds.Height - scroller.Viewport.Height;
                        double max = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
                        scroller.Offset = new Vector(scroller.Offset.X, Math.Clamp(y, 0, max));
                    }
                    scroller?.UpdateLayout();
                    popup?.UpdateLayout();
                    operation = "readiness";
                    // Evaluate the original admission expression unchanged. The checks
                    // below only diagnose rejection; they must not tighten or loosen it.
                    if (target.IsAttachedToVisualTree() && popup is not null
                        && !ReferenceEquals(popup, window)
                        && (requireHitTest
                            ? MediaInteractionSmokeHelpers.IsPointerTargetReady(target)
                            : MediaInteractionSmokeHelpers.IsNativeGeometryReady(target)))
                        return Observe("ready");
                    if (!target.IsAttachedToVisualTree()) return Observe("target-detached");
                    if (popup is null) return Observe("root-missing");
                    if (ReferenceEquals(popup, window)) return Observe("root-is-main-window");
                    if (!target.IsArrangeValid) return Observe("target-not-arranged");
                    if (!target.IsEffectivelyVisible) return Observe("target-hidden");
                    if (!target.IsEffectivelyEnabled) return Observe("target-disabled");
                    if (target.Bounds.Width <= 0 || target.Bounds.Height <= 0) return Observe("target-empty-bounds");
                    if (!popup.IsVisible) return Observe("root-hidden");
                    operation = "screen-conversion";
                    centerInRoot = popup.PointToClient(target.PointToScreen(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2)));
                    if (!new Rect(popup.ClientSize).Contains(centerInRoot.Value)) return Observe("center-outside-root");
                    operation = "geometry-or-hit-test";
                    return Observe(requireHitTest ? "hit-test-or-geometry-race" : "geometry-race");
                }
                catch (ArgumentException)
                {
                    // Preserve bounded retry for cross-root layout/conversion races.
                    // Keep the current probe's target and operation in the evidence.
                    return Observe("argument-exception");
                }
            }, () =>
            {
                Record("overflow-target-timeout", lastState!);
                return $"The {name} control did not settle as a native popup target: {JsonSerializer.Serialize(lastState)}";
            });
            return target!;
        }
        async Task OpenOverflowForNativeTargetAsync(string targetName)
        {
            // One native click only. Record both sides so a closed flyout is not
            // misdiagnosed as a row geometry/hit-test failure, and never retry a
            // toggle click that could close an already-open menu.
            void Snapshot(string phase) => Record("overflow-open-input", new
            {
                targetName, phase, overflowOpen = viewer?.IsOverflowOpen,
                contentAttached = viewer?.OverflowMenuContent.IsAttachedToVisualTree(),
                contentVisible = viewer?.OverflowMenuContent.IsEffectivelyVisible,
                root = Target(viewer == null ? null : TopLevel.GetTopLevel(viewer.OverflowMenuContent)),
                focus = Target(window.FocusManager?.GetFocusedElement()), window.IsActive
            });
            Snapshot("before-click");
            await native!.ClickAsync(() => NativeTargetAsync<Button>("GalleryMore"));
            Snapshot("after-click");
        }
        Slider Seek() => Named<Slider>("GalleryVideoSeek");
        GalleryVideoPlayer Player()
        {
            GalleryVideoPlayer[] players = viewer!.GetGalleryVisualDescendants().OfType<GalleryVideoPlayer>().ToArray();
            if (players.Length != 1)
                throw new InvalidOperationException($"Expected exactly one GalleryVideoPlayer in the current gallery presentation; found {players.Length}.");
            return players[0];
        }
        async Task<GalleryVideoPlayer.PlaybackEvidence> SampleAsync(string label)
        {
            GalleryVideoPlayer player = Player();
            using CancellationTokenSource observation = new();
            GalleryVideoPlayer.PlaybackEvidence evidence;
            try { evidence = await player.InspectPlaybackAsync(cancellationToken: observation.Token).WaitAsync(TimeSpan.FromSeconds(4)); }
            catch (TimeoutException)
            {
                var trace = PlaybackTrace();
                Record("playback-observation-timeout", new { label, post = viewer?.CaptureState().PostId, timeoutMs = 4000,
                    lifecycle = player.InspectPlaybackLifecycle(), trace.Frequency, trace.Dropped,
                    phases = trace.Events.TakeLast(256).ToArray() });
                Console.Error.WriteLine($"SMOKE playback observation timeout: stage={stage}, label={label}, post={viewer?.CaptureState().PostId}");
                throw;
            }
            // WaitAsync only abandons its await. Remove any still-queued observer so it
            // cannot enter native code after this sample/stage has already failed.
            finally { observation.Cancel(); }
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
        bool IsOverflowPopupRootVisible()
        {
            if (viewer == null || TopLevel.GetTopLevel(viewer.OverflowMenuContent) is not { } popup)
                return false;
            return !ReferenceEquals(popup, window) && popup.IsVisible;
        }
        bool HasTransientNativePopup(GalleryVideoPlayer? player) =>
            viewer?.IsOverflowOpen == true
            || CommandRouter.HasOpenPopup(window)
            || IsOverflowPopupRootVisible()
            || viewer?.IsOverlayOpen == true
            || MeowChan.Desktop.Views.Shortcuts.ShortcutsWindow.For(window) is { IsVisible: true }
            || player?.IsVolumePopupOpen == true;

        async Task DismissTransientNativeRootsAsync()
        {
            GalleryVideoPlayer? player = viewer?.GetGalleryVisualDescendants()
                .OfType<GalleryVideoPlayer>().SingleOrDefault();
            if (!HasTransientNativePopup(player)) return;

            // Volume is a retained popup in the native transport root, not an Avalonia
            // Flyout, so checking IsOverflowOpen/IsOverlayOpen alone misses it. Close it
            // explicitly before dismissing a possibly focused overflow PopupRoot.
            player?.SetVolumePopupOpen(false);
            // A failed popup-target assertion must not leave its native light-dismiss
            // root over the next stage. One real Escape dismisses the currently focused
            // root; the explicit Hide below is the owner-side cleanup for a PopupRoot
            // whose focus was already lost during a failed native target probe.
            if (HasTransientNativePopup(player))
                await native!.KeyAsync(MediaNativeInput.Escape);
            viewer?.OverflowFlyout.Hide();
            viewer?.TryDismissOverlay();
            await Program.WaitUntilAsync(() => !HasTransientNativePopup(player),
                () => "The previous native media popup/overlay did not dismiss during stage setup.");
        }
        async Task CloseVolumePopupAsync()
        {
            GalleryVideoPlayer player = Player();
            if (!player.IsVolumePopupOpen) return;
            // Leave the slider's native hit target before forcing the retained popup shut;
            // otherwise its hover callback can reopen it on the next layout turn.
            GalleryViewer current = viewer!;
            // This move only leaves the retained popup; the viewer is a parent, not
            // the asserted native hit target. Keep strict hit testing for every actual
            // control click and use geometry-only positioning for this cleanup move.
            await native!.MoveAsync(current, new Point(current.Bounds.Width / 2, current.Bounds.Height / 2),
                requireHitTest: false);
            player.SetVolumePopupOpen(false);
            await Program.WaitUntilAsync(() => !player.IsVolumePopupOpen,
                () => "The native volume popup did not close after its target was abandoned.");
        }
        async Task FreshVideoAsync()
        {
            if (viewer != null)
            {
                await DismissTransientNativeRootsAsync();
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
        Task<Point> TrackPointAsync(double fraction) => MediaInteractionSmokeHelpers.TrackPointAsync(Seek(), fraction);
        Point VolumeTrackPoint(Slider volume, double fraction, bool requireEnabled = true) =>
            MediaInteractionSmokeHelpers.ReadyTrackPoint(volume, fraction, requireEnabled);
        async Task SeekClickAsync(double fraction)
        {
            Record("inject", new { action = "native-track-click", fraction });
            Task ClickTimelineAsync() => native!.ClickAsync(async () =>
            {
                Slider seek = await NativeTargetAsync<Slider>("GalleryVideoSeek");
                // The preceding gallery transition can leave a different owned Windows root active.
                // Establish the timeline window as the input recipient before proving click focus.
                if (OperatingSystem.IsWindows() && TopLevel.GetTopLevel(seek) is Window { IsActive: false } targetWindow)
                {
                    targetWindow.Activate();
                    await Program.WaitUntilAsync(() => targetWindow.IsActive,
                        () => "The native timeline window did not become active before the click.");
                    seek = await NativeTargetAsync<Slider>("GalleryVideoSeek");
                }
                return seek;
            }, current => Task.FromResult((Point?)MediaInteractionSmokeHelpers.ReadyTrackPoint(current, fraction)));
            int pressesBefore = timelinePointerPresses;
            await ClickTimelineAsync();
            // On Windows a fresh gallery root can get the release without routing the activating press.
            // Retry only missing input delivery. A press seen by Avalonia must acquire focus on its own.
            if (OperatingSystem.IsWindows() && timelinePointerPresses == pressesBefore)
            {
                Record("inject", new { action = "retry-native-track-click-after-missing-press", fraction });
                await ClickTimelineAsync();
            }
            Require(timelinePointerPresses > pressesBefore, "No native timeline press reached the current slider.");
            Require(Seek().IsKeyboardFocusWithin, "Native timeline click did not acquire focus.");
            await WaitPlaybackAsync(p => Math.Abs(p.Time - fraction * p.Length) < 1000);
        }
        async Task DragAsync(double fraction, bool release)
        {
            Slider seek = Seek();
            Thumb[] thumbs = seek.GetVisualDescendants().OfType<Thumb>().ToArray();
            Require(thumbs.Length == 1,
                $"Expected exactly one native timeline thumb in the current slider presentation; found {thumbs.Length}.");
            Thumb thumb = thumbs[0];
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
                    await native.MoveAsync(seek, await TrackPointAsync(start + (fraction - start) * i / 4));
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
        async Task CheckTimelineVolumeKeysAsync(string expected)
        {
            await SeekClickAsync(.25);
            var before = await SampleAsync("timeline-volume-before");
            await native!.KeyAsync(MediaNativeInput.Down);
            if (before.AudioAvailable)
                await WaitPlaybackAsync(p => p.Volume == Math.Max(0, before.Volume - 1) && p.State == expected);
            else
                Require(!Player().CanAdjustVolume, "Unavailable audio enabled a volume command.");
            await SampleAsync("timeline-volume-after-down");
            await native.KeyAsync(MediaNativeInput.Up);
            if (before.AudioAvailable && before.Volume > 0)
                await WaitPlaybackAsync(p => p.Volume == before.Volume && p.State == expected);
            else if (!before.AudioAvailable)
                Require((await SampleAsync("timeline-volume-unavailable")).Volume == before.Volume,
                    "No-audio/output-unavailable key fabricated a volume update.");
            await SampleAsync("timeline-volume-after-up");
            Require(viewer!.CaptureState().PostId == "2000", "Timeline Up/Down changed media identity.");
            await MediaInteractionSmokeHelpers.HoldStateAsync(Player(), expected, "Timeline Up/Down volume");
        }
        async Task CheckVolumeAndPanelsAsync(string expected)
        {
            if (expected == "Playing") await SeekClickAsync(.05); // Keep short supported fixtures away from a natural end.
            Slider Volume() => Named<Slider>("GalleryVideoVolume");
            var volumeEvidence = await SampleAsync("volume-availability-before");
            if (Environment.GetEnvironmentVariable("MEOWCHAN_REQUIRE_FUNCTIONAL_AUDIO") == "1")
                Require(volumeEvidence.AudioAvailable && volumeEvidence.AudioTracks > 0 && volumeEvidence.PlayedAudioBuffers > 0,
                    "MEOWCHAN_REQUIRE_FUNCTIONAL_AUDIO=1 requires actual available audio, an audio track and played audio buffers before volume branch selection.");
            bool nativeUnavailable = !volumeEvidence.AudioAvailable;
            bool ProvesNativeUnavailable(GalleryVideoPlayer.PlaybackEvidence evidence) =>
                evidence.HasPlayback && !evidence.AudioAvailable && evidence.AudioTracks > 0
                && (evidence.Volume < 0 || (evidence.DecodedAudio > 0 && evidence.PlayedAudioBuffers == 0));
            async Task CheckDisabledVolumeClickAsync()
            {
                var before = await SampleAsync("disabled-volume-before-click");
                ToggleButton mute = Named<ToggleButton>("GalleryVideoMute");
                Require(ProvesNativeUnavailable(before) && before.State == expected
                    && !Volume().IsEffectivelyEnabled && !mute.IsEffectivelyEnabled && !Player().CanAdjustVolume,
                    "Disabled-volume branch lacks native unavailable-output evidence or disabled controls.");
                string? post = viewer!.CaptureState().PostId;
                Require(post == "2000", "Disabled-volume check started on unexpected media.");
                await native!.MoveAsync(
                    () => Task.FromResult(Volume()),
                    current => Task.FromResult((Point?)VolumeTrackPoint(current, .60, requireEnabled: false)),
                    requireEnabled: false, requireHitTest: false);
                await native.ButtonAsync(true);
                await native.ButtonAsync(false);
                await MediaInteractionSmokeHelpers.HoldStateAsync(Player(), expected, "Disabled volume pointer click");
                await CloseVolumePopupAsync();
                var after = await SampleAsync("disabled-volume-after-click");
                Require(ProvesNativeUnavailable(after) && after.Volume == before.Volume
                    && after.Muted == before.Muted && after.State == before.State
                    && viewer.CaptureState().PostId == post && !Volume().IsEffectivelyEnabled
                    && !mute.IsEffectivelyEnabled && !Player().CanAdjustVolume,
                    "Disabled volume pointer click changed native volume/mute/state/selection or availability.");
            }
            if (nativeUnavailable)
            {
                Require(ProvesNativeUnavailable(volumeEvidence),
                    "Unavailable audio alone is insufficient: expected an audio track and native unavailable-output evidence.");
                await CheckDisabledVolumeClickAsync();
                Record("coverage-limit", new { expected, volumeEvidence,
                    reason = "Native output unavailable: native volume editing, Home/End and volume-slider focus not exercised; no disabled-slider focus coverage claimed." });
                Console.WriteLine($"SMOKE media {expected}: native output unavailable; native volume editing/HomeEnd/slider focus NOT exercised. Disabled pointer invariance checked; navigation uses actual focused seek transport.");
            }
            else
            {
                await native!.ClickAsync(
                    () => Task.FromResult(Volume()),
                    current => Task.FromResult((Point?)VolumeTrackPoint(current, .60)));
                // The released slider hands the keys back to the gallery (one rule for chrome over a keyed surface): arrows are the
                // gallery's volume keys, never slider-local steps, and the popup's mute button does not get them either.
                await Program.WaitUntilAsync(() => !Volume().IsKeyboardFocusWithin);
                Require(!Volume().IsKeyboardFocusWithin, "Native volume click left the keys on the slider instead of the gallery.");
                var afterClick = await SampleAsync("volume-after-click");
                await native.KeyAsync(MediaNativeInput.Down);
                await WaitPlaybackAsync(p => p.Volume == Math.Max(0, afterClick.Volume - 1) && p.State == expected);
                Require(viewer!.CaptureState().PostId == "2000", "Volume Down navigated media.");
                await native.KeyAsync(MediaNativeInput.Up);
                await WaitPlaybackAsync(p => p.Volume == afterClick.Volume && p.State == expected);
                Require(viewer!.CaptureState().PostId == "2000", "Volume Up navigated media.");
                await MediaInteractionSmokeHelpers.HoldStateAsync(Player(), expected, "Volume click then gallery Down/Up");
                Console.WriteLine("SMOKE functional-audio-positive media volume: " + JsonSerializer.Serialize(new
                { expected, before = volumeEvidence, after = await SampleAsync("functional-audio-positive-volume") }));
            }
            await CloseVolumePopupAsync();
            foreach (string panel in new[] { "GalleryToggleContext", "GalleryHelp" })
            {
                if (expected == "Playing") await SeekClickAsync(.05);
                if (panel == "GalleryHelp")
                {
                    // More is in the gallery root; GalleryHelp is reparented into a
                    // separate Flyout/PopupRoot. Open it through the OS first, then
                    // wait for the popup's current screen geometry before converting
                    // the row coordinate. Popup-root hit testing can lag one layout
                    // turn, so the native click and shortcuts-window assertion below
                    // remain the delivery proof.
                    await OpenOverflowForNativeTargetAsync(panel);
                    bool requirePanelPopupHitTest = !OperatingSystem.IsMacOS();
                    await native.ClickAsync(
                        () => NativeOverflowTargetAsync<Button>(panel, requirePanelPopupHitTest),
                        requireHitTest: requirePanelPopupHitTest);
                }
                else
                    await native.ClickAsync(() => NativeTargetAsync<Button>(panel));
                // The Help tool opens the one modeless shortcuts window (KS3); the context panel stays in the gallery.
                if (panel == "GalleryHelp") await Program.WaitUntilAsync(() => MeowChan.Desktop.Views.Shortcuts.ShortcutsWindow.For(window) is { IsVisible: true });
                Require(panel == "GalleryHelp" ? MeowChan.Desktop.Views.Shortcuts.ShortcutsWindow.For(window) is { IsVisible: true } : viewer!.CaptureState().ContextOpen,
                    "Native panel action failed to open its actual panel: " + panel);
                await MediaInteractionSmokeHelpers.HoldStateAsync(Player(), expected, panel + " open");
                await native.KeyAsync(MediaNativeInput.Escape);
                if (panel == "GalleryHelp") await Program.WaitUntilAsync(() => MeowChan.Desktop.Views.Shortcuts.ShortcutsWindow.For(window) == null);
                Require(!viewer!.CaptureState().ContextOpen && !viewer.IsOverlayOpen
                    && MeowChan.Desktop.Views.Shortcuts.ShortcutsWindow.For(window) == null,
                    "Native Escape did not dismiss only the active panel.");
                await MediaInteractionSmokeHelpers.HoldStateAsync(Player(), expected, panel + " dismissed");
                Require(window.FindControl<ContentControl>("GalleryHost")!.IsVisible && viewer.CaptureState().PostId == "2000",
                    "Panel dismissal closed/navigated the gallery.");
            }
            if (expected == "Playing") await SeekClickAsync(.05);
            await OpenOverflowForNativeTargetAsync("GalleryPlaybackModeSelector");
            // On macOS Avalonia's PopupRoot can expose the current ComboBox with
            // valid native geometry one dispatcher turn before its hit-test cache
            // includes the freshly reparented row. Keep current-target resolution
            // and real CGEvent delivery, but do not reject that live target on the
            // stale framework hit-test probe. Other platforms retain the strict probe.
            bool requirePopupHitTest = !OperatingSystem.IsMacOS();
            await native.ClickAsync(
                () => NativeOverflowTargetAsync<ComboBox>("GalleryPlaybackModeSelector", requirePopupHitTest),
                requireHitTest: requirePopupHitTest);
            Require(Named<ComboBox>("GalleryPlaybackModeSelector").IsDropDownOpen, "Native mode click failed to open the menu.");
            await MediaInteractionSmokeHelpers.HoldStateAsync(Player(), expected, "Playback menu open");
            await native.KeyAsync(MediaNativeInput.Escape);
            Require(!Named<ComboBox>("GalleryPlaybackModeSelector").IsDropDownOpen && viewer!.CaptureState().PostId == "2000",
                "Menu Escape escaped into gallery close/navigation.");
            await MediaInteractionSmokeHelpers.HoldStateAsync(Player(), expected, "Playback menu dismissed");
            // Keep real-input navigation coverage even when the disabled volume cannot take focus.
            if (nativeUnavailable)
            {
                await CheckDisabledVolumeClickAsync();
                await SeekClickAsync(.05);
                Require(Seek().IsEffectivelyEnabled && Seek().IsKeyboardFocusWithin,
                    "Unavailable-output navigation fallback did not focus the actual eligible seek transport.");
                Record("navigation-focus", new { expected, source = "seek-transport-native-unavailable",
                    focus = Target(window.FocusManager?.GetFocusedElement()) });
            }
            else
            {
                // Actual volume pointer focus must yield media navigation on exactly one key.
                await native.ClickAsync(
                    () => Task.FromResult(Volume()),
                    current => Task.FromResult((Point?)VolumeTrackPoint(current, .60)));
            }
            await native.KeyAsync(MediaNativeInput.Right);
            await Program.WaitUntilAsync(() => viewer!.CaptureState().PostId == "2001");
            Record("image-return-input", new { expected, post = viewer!.CaptureState().PostId,
                focus = Target(window.FocusManager?.GetFocusedElement()), routing = RoutingState(window) });
            await native.KeyAsync(MediaNativeInput.Left); // exactly one OS key, including unfocused Window input
            await Program.WaitUntilAsync(() => viewer!.CaptureState().PostId == "2000");
            Record("image-return-selection", new { expected, post = viewer!.CaptureState().PostId });
            await ReadyAsync();
            if (expected == "Paused")
            {
                await native.ClickAsync(() => NativeTargetAsync<Button>("GalleryVideoPlayPause"));
                await WaitPlaybackAsync(p => p.State == "Paused");
            }
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
                    // Reveal/hide is a real OS pointer motion, but the viewer parent
                    // is not itself the interactive target at this edge. Actual key and
                    // click assertions below still resolve and hit-test their controls.
                    await native!.MoveAsync(viewer, new Point(viewer.Bounds.Width * .45, viewer.Bounds.Height - 2),
                        requireHitTest: false);
                    await Program.WaitUntilAsync(() => Named<Border>("GalleryOverlayControls").IsEffectivelyVisible);
                    // Navigation can expose the retained image toolbar before the
                    // decoder has re-enabled its fit commands. Resolve the current
                    // enabled presentation at delivery time; passing Named() here
                    // pins a temporarily disabled button and makes macOS native input
                    // fail with ArgumentException instead of retrying the transition.
                    await native.ClickAsync(() => NativeTargetAsync<Button>("GalleryFit"));
                    await native.MoveAsync(viewer, new Point(viewer.Bounds.Width * .45, viewer.Bounds.Height * .30),
                        requireHitTest: false);
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
