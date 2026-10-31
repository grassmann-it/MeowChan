using Avalonia.Layout;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Feedback;
using MeowChan.Interfaces;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Testing;
using SkiaSharp;

namespace MeowChan.DesktopSmoke;

// Optional central harness entry; no shared Program edit and no execution by S2.
internal static class GalleryErrorInteractionChecks
{
    internal static Task RunAsync(MainWindow owner) => GalleryPlaybackFeatureChecks.RunErrorInteractionChecksAsync(owner);
}

internal static partial class GalleryPlaybackFeatureChecks
{
    internal static async Task RunErrorInteractionChecksAsync(MainWindow owner)
    {
        if (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_XVFB") != "1"
            || !OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_NATIVE_ALLOWED") != "1")
            throw new InvalidOperationException("Error interaction checks require an authorized disposable native desktop.");
        string video = Environment.GetEnvironmentVariable("MEOWCHAN_VIDEO_FIXTURE")
            ?? throw new InvalidOperationException("Error interaction checks require the pinned synthetic video fixture.");
        string directory = TemporaryWorkspacePaths.CreateDirectory("meowchan-gallery-errors-");
        try
        {
            await CheckExternalTransportRetirementAsync(owner);
            await CheckNativeClipFeedbackAsync(owner);
            GalleryEntry clip = await AssetAsync(directory, "notice-video", "notice-video.webm",
                await File.ReadAllBytesAsync(FindSyntheticAudioFixture(video)));
            GalleryEntry image = await ImageAsync(directory, "notice-image", SKColors.DarkSlateBlue);
            await CheckContextLayoutRootsAsync(owner);
            foreach (bool overlay in new[] { false, true })
            foreach (bool paused in new[] { false, true })
                await CheckNotificationTransportAsync(owner, clip, overlay, paused);
            await CheckExplicitActualFailureAsync(owner, clip);
            await CheckImageNotificationAsync(owner, image);
            await CheckActualMediaUnavailableAsync(owner, directory);
            // FIX03: genuine shell/session IO and reader-return paths, not SetNotice stand-ins.
            foreach (bool paused in new[] { false, true })
                await CheckShellFailureAndReaderReturnAsync(owner, clip, paused);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task CheckNativeClipFeedbackAsync(MainWindow owner)
    {
        // No native decoder: reproduce the exact owner-layout -> fresh Clip
        // projection from the pinned VideoView using real roots/focus adorners.
        bool raw = Environment.GetEnvironmentVariable("MEOWCHAN_REPRO_RAW_NATIVE_CLIP") == "1";
        Grid nativeRoot = raw ? new Grid() : new NativeVideoOverlayRoot();
        Button moved = new() { Content = "Transfer focus target", Width = 180, Height = 44 };
        Button other = new() { Content = "Retain focus target", Width = 180, Height = 44 };
        StackPanel controls = new(); controls.Children.Add(moved); controls.Children.Add(other);
        Window main = new() { Width = 650, Height = 500, Content = controls, Title = "Private focus-clip source" };
        Window overlay = new() { Width = 650, Height = 500, Content = nativeRoot, Title = "Private focus-clip overlay" };
        int passes = 0;
        Avalonia.Rect clip = new(0, 0, 650, 500);
        void ProjectClip(object? sender, EventArgs args)
        {
            passes++;
            nativeRoot.Clip = new Avalonia.Media.RectangleGeometry(clip);
        }
        try
        {
            main.Show(owner); main.Activate();
            await LayoutAsync(main);
            Require(moved.Focus(Avalonia.Input.NavigationMethod.Tab), "Focus-clip source is not keyboard reachable.");
            await LayoutAsync(main);
            AdornerLayer layer = AdornerLayer.GetAdornerLayer(moved)!;
            Control ring = layer.Children.Single(adorner => AdornerLayer.GetAdornedElement(adorner)
                ?.GetSelfAndVisualAncestors().Contains(moved) == true);
            Require(other.Focus(Avalonia.Input.NavigationMethod.Tab), "Focus-clip source did not lose focus.");
            Require(!layer.Children.Contains(ring), "Former focus ring remained visibly attached.");
            // Drain the old root with the target detached, so this regression is
            // not an unrelated stale cross-root arrange-queue reproduction.
            controls.Children.Remove(moved);
            await LayoutAsync(main);
            nativeRoot.Children.Add(moved);
            overlay.Show(main);
            await LayoutAsync(overlay);
            await LayoutAsync(main);
            Require(layer.IsMeasureValid, "Former focus layer did not settle before clip-only probe.");
            nativeRoot.Clip = new Avalonia.Media.RectangleGeometry(clip);
            Require(!layer.IsMeasureValid,
                "Pinned orphan-focus reproduction did not observe the removed ring's old-layer clip subscription.");
            Console.WriteLine($"SMOKE native-clip orphan confirmed: raw={raw}, removed focus ring, target in other root, clip-only change invalidated former layer");
            await LayoutAsync(main);
            main.LayoutUpdated += ProjectClip;
            main.InvalidateMeasure();
            await Task.Delay(150);
            await LayoutAsync(main);
            Require(passes < 10, "Unchanged native clip repeatedly invalidated its former focus root.");
            int before = passes;
            clip = new Avalonia.Rect(4, 8, 620, 460);
            ProjectClip(null, EventArgs.Empty);
            await Task.Delay(150);
            await LayoutAsync(main);
            Require(passes - before < 10 && nativeRoot.Clip is Avalonia.Media.RectangleGeometry rectangle
                && rectangle.Rect == clip, "Changed clipping did not settle with its actual new bounds.");
            overlay.Activate();
            Require(moved.Focus(Avalonia.Input.NavigationMethod.Tab), "Transferred control lost keyboard focus capability.");
            await LayoutAsync(overlay);
            Require(AdornerLayer.GetAdornerLayer(moved)!.Children.Any(adorner => AdornerLayer.GetAdornedElement(adorner)
                ?.GetSelfAndVisualAncestors().Contains(moved) == true), "Transferred control lost its visible focus ring.");
            Console.WriteLine($"SMOKE native-clip feedback PASS: raw={raw}, passes={passes}, actual focus loss/root transfer, unchanged clip settled, changed clip/focus ring retained; no decoder claim");
        }
        finally
        {
            main.LayoutUpdated -= ProjectClip;
            overlay.Close(); main.Close();
        }
    }

    private static async Task CheckContextLayoutRootsAsync(MainWindow owner)
    {
        // Portable layout-ownership regression, NOT a native VideoView emulation.
        // Establish all parentage before showing either real layout root. Live
        // cross-root transfers remain covered by the actual native feature suite.
        foreach (bool externalRoot in new[] { false, true })
        foreach (bool postOverlay in new[] { false, true })
        {
            GalleryViewer viewer = new();
            viewer.Configure(contextOpen: true, showShortcutHelp: true, videoAutoplay: false,
                videoStartMuted: true, videoVolume: 0);
            if (externalRoot) ShellErrorSet(viewer, "_nativeOverlayActive", true);
            viewer.ConfigurePlayback(new GalleryPlaybackPreferences(OverlayPostContext: postOverlay).WithControlsOverlay(true));
            viewer.SetInteractionAvailable(false); // Explicit reveal, held stable during sizing.
            Window window = new() { Width = 1000, Height = 700, Content = viewer,
                Title = "Private gallery layout ownership regression" };
            Window? overlay = externalRoot ? new Window
            {
                Width = 650, Height = 500, Content = ErrorField<Grid>(viewer, "_nativeVideoOverlay"),
                Title = "Private external gallery layout root"
            } : null;
            try
            {
                window.Show(owner);
                overlay?.Show(window);
                Border context = Find<Border>(viewer, "GalleryContext");
                StackPanel footer = Find<StackPanel>(viewer, "GalleryOverlayFooter");
                foreach (double height in new[] { 180d, 280d, 180d })
                {
                    footer.Height = height; // Actual SizeChanged callbacks, including repeated sizing.
                    await LayoutAsync(window);
                    if (overlay != null) await LayoutAsync(overlay);
                    Require(footer.IsVisible && footer.Bounds.Height > 0 && context.IsVisible,
                        "Root layout regression requires actually visible footer/context controls.");
                    Require(Math.Abs(footer.Bounds.Height - height) < .01,
                        $"Footer did not realize the requested layout height {height}: {footer.Bounds.Height}.");
                    Require(ReferenceEquals(context.Parent, postOverlay
                        ? externalRoot ? ErrorField<Grid>(viewer, "_nativeVideoOverlay") : ErrorField<Grid>(viewer, "_visualRoot")
                        : ErrorField<Grid>(viewer, "_body")), "Context left its selected layout parent.");
                    Require(ReferenceEquals(TopLevel.GetTopLevel(footer), overlay ?? window),
                        "Footer did not belong to the selected real layout root.");
                    double expected = postOverlay ? footer.Bounds.Height + 16
                        : externalRoot ? 0 : footer.Bounds.Height + 8;
                    Require(Math.Abs(context.Margin.Bottom - expected) < .01,
                        $"Context inherited the wrong root's footer inset: external={externalRoot}, postOverlay={postOverlay}, actual={context.Margin.Bottom}, expected={expected}.");
                }
                // Put the actual OS pointer over the closing root, rather than
                // relying on where a hosted runner happened to leave its cursor.
                Control pointerRoot = externalRoot ? ErrorField<Grid>(viewer, "_nativeVideoOverlay") : viewer;
                using (var native = new MediaNativeInput(overlay ?? window))
                {
                    await native.MoveAsync(footer);
                    await UntilAsync(() => pointerRoot.IsPointerOver,
                        "Closing-root regression did not receive real OS pointer entry.");
                }
                Console.WriteLine($"SMOKE gallery context root PASS: external={externalRoot}, postOverlay={postOverlay}, repeated footer sizing, own-root clearance retained; real pointer over closing root; no native playback claim");
            }
            finally
            {
                // Do not close either root before terminal cleanup has succeeded.
                await viewer.StopAsync();
                overlay?.Close();
                window.Close();
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Require(ErrorField<object?>(viewer, "_lastPointer") == null
                        && ErrorField<Avalonia.PixelPoint?>(viewer, "_lastPointerScreen") == null,
                        "Closing-root pointer events revived the retired viewer's pointer state.");
                    Require(ErrorField<DispatcherTimer?>(viewer, "_indicatorExpiryTimer")?.IsEnabled != true,
                        "Closing-root pointer events restarted the retired indicator timer.");
                }, DispatcherPriority.Background);
            }
        }
    }

    private static async Task CheckExternalTransportRetirementAsync(MainWindow owner)
    {
        // Two real layout roots, even on software-video platforms. This isolates
        // terminal transport ownership from codecs and native audio availability.
        GalleryVideoPlayer player = new();
        StackPanel external = new();
        Window host = new() { Width = 700, Height = 400, Content = player,
            Title = "Private transport retirement host" };
        Window overlay = new() { Width = 700, Height = 180, Content = external,
            Title = "Private transport retirement overlay" };
        Control transport = player.TransportControl;
        int terminalHostAttachments = 0;
        bool retiring = false;
        void Attached(object? sender, Avalonia.VisualTreeAttachmentEventArgs args)
        {
            if (retiring && ReferenceEquals(TopLevel.GetTopLevel(transport), host))
                terminalHostAttachments++;
        }
        transport.AttachedToVisualTree += Attached;
        try
        {
            // Establish ownership before either root is attached. Live placement
            // is tested by the feature suite; this check must isolate retirement,
            // not inherit pending work from a prior live root transfer.
            player.SetExternalTransportHost(external);
            host.Show(owner);
            overlay.Show(host);
            await LayoutAsync(host);
            await LayoutAsync(overlay);
            Require(ReferenceEquals(TopLevel.GetTopLevel(transport), overlay),
                "Retirement precondition: transport is not in the external layout root.");
            Console.WriteLine("SMOKE gallery terminal transport begin: external root settled; invalidating layout before disposal");
            transport.InvalidateMeasure();
            transport.InvalidateArrange();
            retiring = true;
            await player.DisposeAsync();
            Require(terminalHostAttachments == 0 && transport.Parent == null
                && TopLevel.GetTopLevel(transport) == null,
                "Terminal cleanup reattached retired transport to the host layout root instead of detaching it.");
            await LayoutAsync(overlay);
            await LayoutAsync(host);
            await player.DisposeAsync(); // Idempotent retirement cannot restore it later.
            Require(terminalHostAttachments == 0 && transport.Parent == null,
                "Repeated terminal cleanup restored retired transport.");
            Console.WriteLine("SMOKE gallery terminal transport PASS: queued-layout retirement detached without host reattachment");
        }
        finally
        {
            transport.AttachedToVisualTree -= Attached;
            await player.DisposeAsync();
            overlay.Content = null;
            overlay.Close();
            host.Content = null;
            host.Close();
        }
    }

    private static async Task CheckNotificationTransportAsync(MainWindow owner, GalleryEntry clip, bool overlay, bool paused)
    {
        await using Fixture fixture = new(owner);
        await fixture.ReadyAsync();
        GalleryViewer viewer = fixture.Viewer;
        viewer.Configure(false, true, true, false, 40);
        viewer.ConfigurePlayback(new GalleryPlaybackPreferences(GalleryPlaybackMode.Repeat).WithControlsOverlay(overlay));
        await viewer.ShowAsync(clip, CancellationToken.None).WaitAsync(Deadline);
        GalleryVideoPlayer player = Player(viewer);
        await UntilAsync(async () => HasRealFrames(await player.InspectPlaybackAsync()), "Notice fixture did not decode real frames.");
        using MediaNativeInput native = new(fixture.Window);
        if (paused)
        {
            Button playPause = Find<Button>(viewer, "GalleryVideoPlayPause");
            if (!playPause.IsEffectivelyVisible)
            {
                await native.MoveAsync(viewer, new Avalonia.Point(viewer.Bounds.Width * .45, viewer.Bounds.Height - 2));
                await UntilAsync(() => playPause.IsEffectivelyVisible, "Bottom-edge hover did not expose native pause control.");
            }
            await native.ClickAsync(playPause);
            await UntilAsync(async () => (await player.InspectPlaybackAsync()).State == "Paused", "Notice fixture did not pause.");
        }
        string expected = paused ? "Paused" : "Playing";
        viewer.FocusGallerySurface();
        await native.MoveAsync(viewer, new Avalonia.Point(viewer.Bounds.Width * .4, viewer.Bounds.Height * .3));
        Border controls = Find<Border>(viewer, "GalleryOverlayControls");
        if (overlay) await UntilAsync(() => !Visible(controls), "Notice precondition: overlay did not auto-hide.");
        TopLevel inputRoot = TopLevel.GetTopLevel(Find<Button>(viewer, "GalleryVideoPlayPause"))!;
        object? focused = inputRoot.FocusManager?.GetFocusedElement();
        GalleryViewerState selection = viewer.CaptureState();
        GalleryVideoPlayer.PlaybackEvidence before = await player.InspectPlaybackAsync();
        var reports = new List<GalleryDiagnosticNotice>();
        viewer.DiagnosticNotice += reports.Add;
        viewer.SetFavoriteError("synthetic favorite failure");
        viewer.SetRating(null, "synthetic rating failure");
        viewer.SetWarnings("synthetic provenance warning");
        viewer.SetNotice("synthetic preferences failure");
        await viewer.SetMediaUnavailable("stale shell resolution failure", viewer.MediaRequestGeneration - 1);
        Require(reports.Count == 4 && reports.All(n => n.Entry?.Key == clip.Key && !n.MediaUnavailable),
            "Notification identity/classification did not reach the central bridge.");
        Require(ReferenceEquals(focused, inputRoot.FocusManager?.GetFocusedElement()), "Notification stole focus.");
        Require(viewer.CaptureState() == selection, "Notification changed selection/context/zoom.");
        Require(!ErrorField<bool>(viewer, "_mediaFailed"), "Notification marked valid playback unavailable.");
        Require(!Find<Control>(viewer, "GalleryFavoriteError").IsEffectivelyVisible
            && string.IsNullOrEmpty(Find<TextBlock>(viewer, "GalleryWarnings").Text)
            && !Find<TextBlock>(viewer, "GalleryRatingStatus").Text!.Contains("synthetic"), "Technical inline error banner survived.");
        if (overlay) Require(!Visible(controls), "Notification immediately revealed controls.");
        await MediaInteractionSmokeHelpers.HoldStateAsync(player, expected, "technical notification");
        var after = await player.InspectPlaybackAsync();
        Require(before.LoopEnabled == after.LoopEnabled && before.Volume == after.Volume,
            "Notification changed repeat/volume intent.");
        if (overlay) Require(!Visible(controls), "Notification pinned controls after idle.");
        Console.WriteLine($"SMOKE gallery-error notice PASS: overlay={overlay}, state={expected}, centralReports={reports.Count}");
    }

    private static async Task CheckExplicitActualFailureAsync(MainWindow owner, GalleryEntry clip)
    {
        await using Fixture fixture = new(owner);
        await fixture.ReadyAsync();
        GalleryViewer viewer = fixture.Viewer;
        viewer.Configure(false, true, true, false, 40);
        await viewer.ShowAsync(clip, CancellationToken.None).WaitAsync(Deadline);
        GalleryVideoPlayer player = Player(viewer);
        await UntilAsync(async () => HasRealFrames(await player.InspectPlaybackAsync()), "Actual-failure fixture did not start.");
        int request = viewer.MediaRequestGeneration;
        int moves = 0;
        viewer.MoveRequested += _ => moves++;
        await viewer.SetMediaUnavailable("synthetic actual shell load failure", request).WaitAsync(Deadline);
        var stopped = await player.InspectPlaybackAsync();
        Require(!stopped.HasPlayback && stopped.State != "Playing" && ErrorField<bool>(viewer, "_mediaFailed"),
            "Actual shell load failure left the prior video active or falsely ready.");
        Require(Find<TextBlock>(viewer, "GalleryStatus").IsEffectivelyVisible && !player.IsVisible,
            "Actual failure did not show the neutral viewer status after native cleanup.");
        await HoldAsync(TimeSpan.FromMilliseconds(900), () => Require(moves == 0 && !Running(viewer),
            "Actual shell load failure replayed/advanced stale media."));
    }

    private static async Task CheckImageNotificationAsync(MainWindow owner, GalleryEntry image)
    {
        await using Fixture fixture = new(owner);
        await fixture.ReadyAsync();
        await fixture.ShowAsync(image);
        GalleryViewer viewer = fixture.Viewer;
        viewer.ConfigurePlayback(new GalleryPlaybackPreferences().WithControlsOverlay(true));
        viewer.FocusGallerySurface();
        using MediaNativeInput native = new(fixture.Window);
        await native.MoveAsync(viewer, new Avalonia.Point(viewer.Bounds.Width * .4, viewer.Bounds.Height * .3));
        Border controls = Find<Border>(viewer, "GalleryOverlayControls");
        await UntilAsync(() => !Visible(controls), "Image overlay did not hide before notice.");
        var selection = viewer.CaptureState();
        viewer.SetNotice("synthetic source failure is only a notice");
        await HoldAsync(TimeSpan.FromMilliseconds(800), () => Require(!Visible(controls)
            && viewer.CaptureState() == selection && !ErrorField<bool>(viewer, "_mediaFailed"),
            "Image notification changed readiness/navigation/auto-hide."));
    }

    private static async Task CheckActualMediaUnavailableAsync(MainWindow owner, string directory)
    {
        // A manifest-bound asset whose bytes have disappeared must fail verification,
        // not become playable because notification presentation has been removed.
        GalleryEntry missing = await AssetAsync(directory, "missing", "missing.webm", new byte[] { 1, 2, 3, 4 });
        File.Delete(Path.Combine(directory, "missing", "missing.webm"));
        await using Fixture fixture = new(owner);
        await fixture.ReadyAsync();
        GalleryViewer viewer = fixture.Viewer;
        viewer.ConfigurePlayback(new(GalleryPlaybackMode.Slideshow, 1));
        var reports = new List<GalleryDiagnosticNotice>();
        viewer.DiagnosticNotice += reports.Add;
        int moves = 0;
        viewer.MoveRequested += _ => moves++;
        await viewer.ShowAsync(missing, CancellationToken.None).WaitAsync(Deadline);
        await UntilAsync(() => ErrorField<bool>(viewer, "_mediaFailed"), "Real missing video was not classified unavailable.");
        GalleryVideoPlayer player = Player(viewer);
        Require(!Running(viewer) && reports.Any(n => n.MediaUnavailable && n.Entry?.Key == missing.Key),
            "Actual media failure did not stop progression/report the bound asset.");
        var evidence = await player.InspectPlaybackAsync();
        Require(evidence.State is not ("Playing" or "Paused"), "Missing/unverified bytes became playable.");
        Require(!Find<Slider>(viewer, "GalleryVideoSeek").IsEnabled, "Failed media enabled seek.");
        string status = Find<TextBlock>(viewer, "GalleryVideoStatus").Text ?? "";
        Require(!status.Contains(directory, StringComparison.Ordinal) && !status.Contains("Exception", StringComparison.Ordinal),
            "Actual failure exposed a technical path/exception banner.");
        await HoldAsync(TimeSpan.FromMilliseconds(900), () => Require(moves == 0 && !Running(viewer),
            "Actual failure introduced automatic skip/retry/late progression."));
        await viewer.StopAsync();
        int stoppedCount = reports.Count;
        viewer.SetNotice("late stopped notification");
        Require(reports.Count == stoppedCount, "Stopped gallery accepted a late notice.");
        Console.WriteLine("SMOKE gallery-error actual failure PASS: missing verified asset, no false playback/automatic move, late notices rejected");
    }

    private static async Task CheckShellFailureAndReaderReturnAsync(MainWindow owner, GalleryEntry clip, bool paused)
    {
        string profile = TemporaryWorkspacePaths.CreateDirectory("meowchan-gallery-shell-errors-");
        string language = Texts.Language;
        DesktopSessionHost? host = null;
        MainWindow? shell = null;
        string settings = Path.Combine(profile, "settings.json");
        try
        {
            new Config
            {
                DownloadPath = Path.Combine(profile, "media"), SaveUrlsOnExit = false,
                AutomaticUpdateChecks = false, CloseToTray = false, MinimizeToTray = false,
                WorkspaceMode = "Advanced", ShowErrorNotifications = true, ShowWarningNotifications = true,
                VideoAutoplay = true, VideoStartMuted = true, VideoVolume = 25,
                GalleryPlaybackMode = "Manual", GalleryContextOpen = false,
                GalleryOverlayControls = false, GalleryAutoHideControls = false
            }.SaveConfig(profile);
            host = new DesktopSessionHost(profile, Array.Empty<IImageBoard>());
            shell = new MainWindow(host);
            shell.Show(owner); shell.Activate();
            await host.StartAsync().WaitAsync(Deadline);
            await UntilAsync(() => shell.IsActive && shell.FindControl<Button>("AddUrlButton")!.IsEnabled
                && ShellErrorField<bool>(shell, "_windowPreferencesReady"), "Child shell did not reach real Ready.");
            ShellErrorStopTimers(shell);
            await ((Task)ShellErrorCall(shell, "FlushWindowPreferencesAsync")!).WaitAsync(Deadline);
            await host.FlushPreferencesAsync().WaitAsync(Deadline);
            // Prevent unrelated geometry writes from retrying/merging the fault under test.
            ShellErrorSet(shell, "_windowPreferencesReady", false);
            ShellErrorStopTimers(shell);
            await UntilAsync(() => host.PreferenceWriteCounts.Active == 0 && host.PreferenceWriteCounts.Pending == 0,
                "Child shell startup preferences did not drain.");
            await shell.OpenMediaGalleryAsync([clip], clip.EntryKey).WaitAsync(Deadline);
            GalleryViewer viewer = ShellErrorField<GalleryViewer>(shell, "_gallery");
            GalleryVideoPlayer player = Player(viewer);
            long? lastReadinessLog = null;
            await UntilAsync(async () =>
            {
                var state = await player.InspectPlaybackAsync();
                // Windows video chrome is hosted in its own owned native window.
                // That valid input root can be active instead of the shell; do not
                // misreport that focus handoff as missing audio readiness.
                bool activeInput = shell.IsEnabled && viewer.IsEffectivelyVisible && viewer.IsEffectivelyEnabled
                    && (shell.IsActive || shell.OwnedWindows.Any(window => viewer.OwnsOverlayWindow(window)
                        && window.IsVisible && window.IsEnabled && window.IsActive));
                bool decoded = HasRealFrames(state) && state.Time >= 500;
                bool audio = state.AudioAvailable
                    || state.AudioTracks > 0 && state.DecodedAudio > 0 && state.PlayedAudioBuffers == 0;
                if (lastReadinessLog == null || System.Diagnostics.Stopwatch.GetElapsedTime(lastReadinessLog.Value).TotalSeconds >= 1
                    || activeInput && decoded && audio)
                {
                    lastReadinessLog = System.Diagnostics.Stopwatch.GetTimestamp();
                    Console.WriteLine("SMOKE gallery-shell readiness: " + System.Text.Json.JsonSerializer.Serialize(new
                    {
                        pausedCase = paused, shellActive = shell.IsActive, activeInput, decoded, audio,
                        ownedRoots = shell.OwnedWindows.Select(window => new
                        { overlay = viewer.OwnsOverlayWindow(window), window.IsActive, window.IsVisible, window.IsEnabled }).ToArray(),
                        playback = state
                    }));
                }
                return activeInput && decoded && audio;
            }, "Shell fixture readiness failed; inspect gallery-shell readiness records for input ownership, video and audio evidence.");
            if (paused)
            {
                Click(Find<Button>(viewer, "GalleryVideoPlayPause"));
                await UntilAsync(async () => (await player.InspectPlaybackAsync()).State == "Paused", "Shell fixture did not pause.");
            }
            string expected = paused ? "Paused" : "Playing";
            await CheckRealShellPreferenceFailureAsync(shell, host, viewer, player, settings, expected);
            await CheckRealShellReaderReturnAsync(shell, viewer, player, expected);
        }
        finally
        {
            // Only this newly created profile's path is faulted. Remove the owned empty
            // directory before shutdown so final flush can retry, even after an assertion.
            if (Directory.Exists(settings)) Directory.Delete(settings);
            if (shell != null)
            {
                ShellErrorStopTimers(shell);
                try { await ((Task)ShellErrorCall(shell, "FlushGalleryPreferencesAsync")!).WaitAsync(Deadline); }
                finally { await ((Task)ShellErrorCall(shell, "CloseApplicationAsync")!).WaitAsync(Deadline); }
            }
            if (host != null) await host.DisposeAsync().AsTask().WaitAsync(Deadline);
            Texts.SetLanguage(language);
            ShortcutService.Publish(owner.SessionHost.Config.ShortcutBindings);
            Directory.Delete(profile, recursive: true);
        }
    }

    private static async Task CheckRealShellPreferenceFailureAsync(MainWindow shell, DesktopSessionHost host,
        GalleryViewer viewer, GalleryVideoPlayer player, string settings, string expected)
    {
        Config durableBefore = Config.LoadConfig(Path.GetDirectoryName(settings)!);
        Require(durableBefore.VideoVolume == 25 && durableBefore.GalleryPlaybackMode == "Manual"
            && !durableBefore.GalleryContextOpen && !durableBefore.GalleryOverlayControls,
            "Shell persistence regression lacks a distinct durable predecessor.");
        File.Move(settings, settings + ".before-failure");
        Directory.CreateDirectory(settings); // Real atomic publication fails: destination is our own directory.
        Slider volume = Find<Slider>(viewer, "GalleryVideoVolume");
        var audioBefore = await player.InspectPlaybackAsync();
        bool availableAudio = audioBefore.AudioAvailable;
        if (availableAudio)
        {
            Require(volume.IsEnabled, "Working audio output has no enabled volume target.");
            volume.Value = 61; // Actual player ValueChanged -> shell QueueGalleryPreference -> host writer.
        }
        else
        {
            Require(!volume.IsEnabled && !Find<Button>(viewer, "GalleryVideoMute").IsEnabled
                && audioBefore.AudioTracks > 0 && audioBefore.DecodedAudio > 0 && audioBefore.PlayedAudioBuffers == 0,
                "Absent audio renderer must disable controls with explicit native evidence.");
            Console.WriteLine("SMOKE gallery-shell volume unavailable: decoded audio but zero rendered buffers; disabled-control contract checked, native volume edit NOT claimed.");
        }
        int expectedSavedVolume = availableAudio ? 61 : 25;
        using var layoutTrace = new ShellGalleryLayoutTrace(shell, viewer);
        layoutTrace.Record("before repeat");
        Find<ComboBox>(viewer, "GalleryPlaybackModeSelector").SelectedIndex = (int)GalleryPlaybackMode.Repeat;
        layoutTrace.Record("before context");
        Click(Find<Button>(viewer, "GalleryToggleContext"));
        layoutTrace.Record("before overlay");
        Click(Find<Button>(viewer, "GalleryOverlayToggle"));
        layoutTrace.Record("after overlay");
        GalleryViewerState intendedSelection = viewer.CaptureState();
        Require(intendedSelection.ContextOpen, "Actual context action did not open the gallery context.");
        Task observed = (Task)ShellErrorCall(shell, "FlushGalleryPreferencesAsync")!;
        Task raw = ShellErrorField<Task>(shell, "_galleryPreferenceWrite");
        await observed.WaitAsync(Deadline); // The UI observer catches; the actual writer must still fault.
        layoutTrace.Record("write observed");
        await LayoutAsync(shell);
        StackPanel shellFooter = Find<StackPanel>(viewer, "GalleryOverlayFooter");
        Require(Find<Border>(viewer, "GalleryContext").Margin.Bottom ==
            (!ErrorField<bool>(viewer, "_nativeOverlayActive") && shellFooter.IsVisible ? shellFooter.Bounds.Height + 8 : 0),
            "Real shell docked context reserved space for a footer in the wrong layout root.");
        bool ioFailed = false;
        try { await raw.WaitAsync(Deadline); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { ioFailed = true; }
        Require(ioFailed && raw.IsFaulted && host.PreferenceWriteCounts.Failed > 0,
            "The actual settings.json IO failure was hidden or its retry draft was dropped.");
        var failed = ShellErrorField<System.Collections.IDictionary>(host, "_failedFinalWrites");
        Require(failed.Contains("view") && ShellErrorField<int?>(shell, "_desiredGalleryVolume") == (availableAudio ? 61 : null)
            && ShellErrorField<bool?>(shell, "_desiredGalleryContext") == true,
            "Failed host draft/live desired volume or context was cleared by error observation.");
        GalleryPlaybackPreferences? desired = ShellErrorField<GalleryPlaybackPreferences?>(shell, "_desiredGalleryPlayback");
        Require(desired is { Mode: GalleryPlaybackMode.Repeat, OverlayControls: true, AutoHideControls: true },
            "Save failure rolled back desired mode/layout to the old durable snapshot.");
        Require(host.Config.VideoVolume == 25 && host.Config.GalleryPlaybackMode == "Manual",
            "A failed raw write was incorrectly published as durable success.");
        if (availableAudio)
            await UntilAsync(async () => (await player.InspectPlaybackAsync()).Volume == 61,
                "Native volume was rolled back by the real shell save-failure observer.");
        GalleryVideoPlayer.PlaybackEvidence intent = await player.InspectPlaybackAsync();
        Require(intent.State == expected && intent.LoopEnabled
            && (availableAudio ? !intent.Muted : intent.Muted == audioBefore.Muted && intent.Volume == audioBefore.Volume),
            "Real save failure altered Playing/Paused/repeat or available/unavailable audio intent.");
        SessionFeedbackStore store = ShellErrorField<SessionFeedbackStore>(shell, "_feedback");
        Require(store.Entries.Any(entry => entry.Operation == "preferences.gallery" && entry.Category == FeedbackCategory.Error),
            "Actual shell preference failure was not retained centrally.");
        await HoldAsync(TimeSpan.FromMilliseconds(600), () =>
        {
            Require(viewer.CaptureState() == intendedSelection
                && Find<ComboBox>(viewer, "GalleryPlaybackModeSelector").SelectedIndex == (int)GalleryPlaybackMode.Repeat
                && Find<ToggleButton>(viewer, "GalleryOverlayToggle").IsChecked == true,
                "Failure presentation reverted the actual gallery mode/context/layout.");
            RequireNoShellErrorPresentation(shell);
        });
        await RequireShellNativeIntentAsync(player, intent, expected, "failed settings publication");
        Directory.Delete(settings); // Remove only the owned empty directory; retry without replaying UI edits.
        await ((Task)ShellErrorCall(shell, "FlushGalleryPreferencesAsync")!).WaitAsync(Deadline);
        Task retriedRaw = ShellErrorField<Task>(shell, "_galleryPreferenceWrite");
        await retriedRaw.WaitAsync(Deadline);
        await UntilAsync(() => host.PreferenceWriteCounts.Failed == 0, "Explicit shell retry did not clear the failed host draft.");
        Config saved = Config.LoadConfig(Path.GetDirectoryName(settings)!);
        Require(retriedRaw.IsCompletedSuccessfully && saved.VideoVolume == expectedSavedVolume && saved.GalleryPlaybackMode == "Repeat"
            && saved.GalleryContextOpen && saved.GalleryOverlayControls && saved.GalleryAutoHideControls,
            "Explicit retry failed to commit the retained volume/mode/context/layout draft.");
        Require(viewer.CaptureState() == intendedSelection, "Successful retry changed gallery selection/context.");
        await RequireShellNativeIntentAsync(player, intent, expected, "explicit retained-draft retry");
        RequireNoShellErrorPresentation(shell);
        Console.WriteLine($"SMOKE gallery-shell preferences PASS: {expected}, real settings.json IO fault, raw fault/draft retained, live intent unchanged, explicit retry durable");
    }

    private static async Task CheckRealShellReaderReturnAsync(MainWindow shell, GalleryViewer viewer,
        GalleryVideoPlayer player, string expected)
    {
        var before = await player.InspectPlaybackAsync();
        GalleryViewerState selection = viewer.CaptureState();
        Window? overlayBeforeSuspend = ErrorField<bool>(viewer, "_nativeOverlayActive")
            ? TopLevel.GetTopLevel(ErrorField<Grid>(viewer, "_nativeVideoOverlay")) as Window : null;
        // Exact production shell boundaries: no fake suspended flag or viewer-only notice.
        // OpenMediaGalleryAsync has no archive thread to fetch; avoid creating a source/network request.
        ShellErrorCall(shell, "OpenGalleryReader");
        ShellErrorCall(shell, "SuspendGallery");
        await shell.GalleryCleanupTask.WaitAsync(Deadline);
        Require(ShellErrorField<bool>(shell, "_largeReaderOpen") && ShellErrorField<bool>(shell, "_gallerySuspended")
            && !shell.FindControl<ContentControl>("GalleryHost")!.IsVisible,
            "Actual shell Reader/suspend path did not hide and retire gallery playback.");
        Require(!player.IsVisible && overlayBeforeSuspend?.IsVisible != true,
            "Reader suspension left the player or its separate native overlay visible.");
        string operation = "fixture.gallery-reader-warning." + Guid.NewGuid().ToString("N");
        ShellErrorCall(shell, "ShowNotification", "Reader warning", "Private synthetic Reader warning",
            NotificationType.Warning, "gallery-reader-fixture", operation);
        SessionFeedbackStore store = ShellErrorField<SessionFeedbackStore>(shell, "_feedback");
        await UntilAsync(() => store.Entries.Any(entry => entry.Operation == operation)
            && shell.FindControl<Ursa.Controls.Banner>("ShellActionErrorBanner")!.IsVisible
            && ShellErrorCards(shell).Any(card => card.Content is FeedbackToastContent { Entry: { } entry }
                && entry.Operation == operation), "Real Reader warning did not produce its actual shell banner/toast.");
        SessionFeedback warning = store.Entries.Single(entry => entry.Operation == operation);
        Require(!store.IsDismissed(warning), "Reader warning was acknowledged without an explicit dismissal.");
        Button back = shell.ArchivePage.FindControl<Button>("ArchiveGalleryReturn")!;
        Require(back.IsVisible && back.IsEnabled, "Production Reader return action is unavailable.");
        Click(back); // Actual OnGalleryReturnClick -> ReturnToGallery, not direct flag manipulation.
        await shell.GalleryCleanupTask.WaitAsync(Deadline);
        await UntilAsync(async () => !ShellErrorField<bool>(shell, "_gallerySuspended")
            && shell.FindControl<ContentControl>("GalleryHost")!.IsVisible
            && Find<Button>(viewer, "GalleryVideoPlayPause").IsEnabled
            && (expected == "Playing" ? (await player.InspectPlaybackAsync()).State == "Playing"
                : (await player.InspectPlaybackAsync()).State is "Paused" or "Stopped" or "NothingSpecial"),
            "Gallery return lost prior Playing/non-playing intent.");
        Require(ReferenceEquals(viewer, ShellErrorField<GalleryViewer>(shell, "_gallery"))
            && viewer.CaptureState() == selection, "Reader return replaced the gallery or lost selection/context.");
        await HoldAsync(TimeSpan.FromMilliseconds(700), () =>
        {
            RequireNoShellErrorPresentation(shell);
            SessionFeedback kept = store.Entries.Single(entry => entry.Id == warning.Id);
            Require(kept.Count == warning.Count && !store.IsDismissed(kept),
                "Hiding Reader feedback on gallery return deleted/acknowledged/requeued diagnostic history.");
        });
        // Existing reader resume recreates a paused video's verified backend as Ready/
        // Stopped on some VLC versions. Preserve non-playing intent, not a fabricated
        // native Paused state; initial mute policy on backend recreation is unchanged.
        var resumed = await player.InspectPlaybackAsync();
        await MediaInteractionSmokeHelpers.HoldStateAsync(player, resumed.State, "Reader warning and gallery return");
        var settled = await player.InspectPlaybackAsync();
        Require(settled.Volume == before.Volume && settled.LoopEnabled == before.LoopEnabled
            && (expected == "Playing" ? settled.State == "Playing"
                : settled.State != "Playing" && Math.Abs(settled.Time - resumed.Time) <= 120),
            "Reader warning/return changed volume/repeat or restarted the paused playback clock.");
        Console.WriteLine($"SMOKE gallery-shell Reader return PASS: prior={expected}, resumed={settled.State}, real banner/toast hidden on reentry, history retained/unacknowledged, playing/non-playing intent preserved");
    }

    private sealed class ShellGalleryLayoutTrace : IDisposable
    {
        private readonly Window _shell;
        private readonly GalleryViewer _viewer;
        private readonly Window? _overlay;
        private int _remaining = 40;
        private string? _last;
        private int _layoutEvents;

        internal ShellGalleryLayoutTrace(Window shell, GalleryViewer viewer)
        {
            _shell = shell; _viewer = viewer;
            _overlay = TopLevel.GetTopLevel(ErrorField<Grid>(viewer, "_nativeVideoOverlay")) as Window;
            shell.LayoutUpdated += OnLayout;
            if (_overlay != null) _overlay.LayoutUpdated += OnLayout;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
        }
        private void OnLayout(object? sender, EventArgs args)
        {
            _layoutEvents++;
            Record(ReferenceEquals(sender, _shell) ? "main layout" : "overlay layout");
            if (_layoutEvents <= 128 && (_layoutEvents & (_layoutEvents - 1)) == 0)
                RecordPendingCallbacks("layout-event-" + _layoutEvents);
        }
        private void OnUnhandled(object sender, UnhandledExceptionEventArgs args) => RecordPendingCallbacks("unhandled-fatal");

        // Read-only, bounded harness diagnostics against the pinned framework.
        // Never invoke/dequeue callbacks or swallow/change the original exception.
        private void RecordPendingCallbacks(string phase)
        {
            try
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                object? Field(object? target, string name) => target?.GetType().GetField(name, flags)?.GetValue(target);
                object? Property(object? target, string name) => target?.GetType().GetProperty(name, flags)?.GetValue(target);
                string Label(object? target) => ReferenceEquals(target, _shell) ? "shell"
                    : ReferenceEquals(target, _overlay) ? "video-overlay"
                    : target is Control control ? control.GetType().Name + ":" + control.Name
                    : target?.GetType().FullName ?? "null";
                object[] Queue(object? target, string name) => Field(target, name) is System.Collections.IEnumerable values
                    ? values.Cast<object>().Take(12).Select(value => (object)new
                    {
                        control = Label(value), root = value is Control control ? Label(TopLevel.GetTopLevel(control)) : null,
                        measureValid = Property(value, "IsMeasureValid"), arrangeValid = Property(value, "IsArrangeValid")
                    }).ToArray() : [];
                Type? type = typeof(Avalonia.Visual).Assembly.GetType("Avalonia.Media.MediaContext");
                object? media = type?.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
                var callbacks = Field(media, "_invokeOnRenderCallbacks") as System.Collections.IEnumerable;
                object[] pending = callbacks?.Cast<Delegate>().Take(12).Select(callback => (object)new
                {
                    method = callback.Method.DeclaringType?.FullName + "." + callback.Method.Name,
                    target = Label(callback.Target),
                    owner = Label(Property(Field(callback.Target, "_owner"), "RootVisual")),
                    queued = Field(callback.Target, "_queued"), running = Field(callback.Target, "_running"),
                    passes = Field(callback.Target, "_totalPassCount"), disposed = Field(callback.Target, "_disposed"),
                    measure = Queue(callback.Target, "_toMeasure"), arrange = Queue(callback.Target, "_toArrange")
                }).ToArray() ?? [];
                object[] roots = new[] { _shell, _overlay }.OfType<Window>().Distinct().Select(root =>
                {
                    object manager = typeof(TopLevel).GetProperty("LayoutManager", flags)!.GetValue(root)!;
                    return (object)new
                    {
                        owner = Label(root), queued = Field(manager, "_queued"), running = Field(manager, "_running"),
                        passes = Field(manager, "_totalPassCount"), disposed = Field(manager, "_disposed"),
                        measure = Queue(manager, "_toMeasure"), arrange = Queue(manager, "_toArrange"),
                        afterMeasure = Queue(manager, "_toArrangeAfterMeasure")
                    };
                }).ToArray();
                Console.WriteLine("SMOKE gallery-render-callbacks " + phase + ": "
                    + System.Text.Json.JsonSerializer.Serialize(new { layoutEvents = _layoutEvents, roots, pending }));
            }
            catch (Exception error)
            {
                Console.WriteLine("SMOKE gallery-render-callback diagnostic unavailable: " + error.GetType().Name + ": " + error.Message);
            }
        }
        internal void Record(string phase)
        {
            if (_remaining <= 0) return;
            var footer = ErrorField<StackPanel>(_viewer, "_overlayFooter");
            var context = ErrorField<Border>(_viewer, "_contextPanel");
            var native = ErrorField<Grid>(_viewer, "_nativeVideoOverlay");
            var surface = Player(_viewer).GetVisualDescendants().OfType<LibVLCSharp.Avalonia.VideoView>().SingleOrDefault();
            string state = System.Text.Json.JsonSerializer.Serialize(new
            {
                viewer = _viewer.Bounds.ToString(), viewport = Find<Grid>(_viewer, "GalleryViewport").Bounds.ToString(),
                nativeActive = ErrorField<bool>(_viewer, "_nativeOverlayActive"),
                video = surface?.Bounds.ToString(), footer = footer.Bounds.ToString(),
                contextInset = context.Margin.Bottom, contextInMain = ReferenceEquals(TopLevel.GetTopLevel(context), _shell),
                overlay = _overlay?.Bounds.ToString(), overlayWidth = _overlay?.Width.ToString(),
                overlayHeight = _overlay?.Height.ToString(), sizeToContent = _overlay?.SizeToContent.ToString(),
                overlayValid = native.IsArrangeValid,
                banner = _shell.FindControl<Ursa.Controls.Banner>("ShellActionErrorBanner")?.IsVisible
            });
            if (phase.EndsWith("layout", StringComparison.Ordinal) && state == _last) return;
            _last = state; _remaining--;
            Console.WriteLine($"SMOKE gallery-shell layout {phase}: {state}");
        }
        public void Dispose()
        {
            _shell.LayoutUpdated -= OnLayout;
            if (_overlay != null) _overlay.LayoutUpdated -= OnLayout;
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandled;
        }
    }

    private static async Task RequireShellNativeIntentAsync(GalleryVideoPlayer player,
        GalleryVideoPlayer.PlaybackEvidence expectedIntent, string expectedState, string scenario)
    {
        await MediaInteractionSmokeHelpers.HoldStateAsync(player, expectedState, scenario);
        var after = await player.InspectPlaybackAsync();
        Require(after.Volume == expectedIntent.Volume && after.Muted == expectedIntent.Muted
            && after.LoopEnabled == expectedIntent.LoopEnabled, scenario + " changed native volume/mute/loop intent.");
    }

    private static NotificationCard[] ShellErrorCards(MainWindow shell) => shell.GetVisualDescendants()
        .OfType<NotificationCard>().Where(card => !card.IsClosing && !card.IsClosed).ToArray();
    private static void RequireNoShellErrorPresentation(MainWindow shell)
    {
        Require(!shell.FindControl<Ursa.Controls.Banner>("ShellActionErrorBanner")!.IsVisible
            && !ShellErrorCards(shell).Any(card => card.Content is FeedbackToastContent { Entry: { } entry }
                && entry.Category is FeedbackCategory.Warning or FeedbackCategory.Error),
            "Shell banner/toast overlays the active gallery after an actual shell error/return.");
        object queued = ShellErrorField<object>(shell, "_startupNotifications");
        Require((int)queued.GetType().GetProperty("Count")!.GetValue(queued)! == 0,
            "Gallery error was queued for a later shell notification flood.");
    }
    private static void ShellErrorStopTimers(MainWindow shell)
    {
        foreach (string name in new[] { "_windowPreferenceTimer", "_galleryPreferenceTimer" })
            (ShellErrorField<object?>(shell, name) as DispatcherTimer)?.Stop();
    }
    private static T ShellErrorField<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void ShellErrorSet(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static object? ShellErrorCall(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);

    private static T ErrorField<T>(GalleryViewer viewer, string name) =>
        (T)typeof(GalleryViewer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewer)!;
}
