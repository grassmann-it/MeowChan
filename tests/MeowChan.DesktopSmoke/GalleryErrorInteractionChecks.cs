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
            GalleryEntry clip = await AssetAsync(directory, "notice-video", "notice-video.webm",
                await File.ReadAllBytesAsync(FindSyntheticAudioFixture(video)));
            GalleryEntry image = await ImageAsync(directory, "notice-image", SKColors.DarkSlateBlue);
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
            await UntilAsync(async () =>
            {
                var state = await player.InspectPlaybackAsync();
                return shell.IsActive && HasRealFrames(state) && state.Time >= 500
                    && (state.AudioAvailable || state.AudioTracks > 0 && state.DecodedAudio > 0 && state.PlayedAudioBuffers == 0);
            }, "Shell fixture has neither working audio nor explicit decoded-without-output evidence.");
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
        Find<ComboBox>(viewer, "GalleryPlaybackModeSelector").SelectedIndex = (int)GalleryPlaybackMode.Repeat;
        Click(Find<Button>(viewer, "GalleryToggleContext"));
        Click(Find<Button>(viewer, "GalleryOverlayToggle"));
        GalleryViewerState intendedSelection = viewer.CaptureState();
        Require(intendedSelection.ContextOpen, "Actual context action did not open the gallery context.");
        Task observed = (Task)ShellErrorCall(shell, "FlushGalleryPreferencesAsync")!;
        Task raw = ShellErrorField<Task>(shell, "_galleryPreferenceWrite");
        await observed.WaitAsync(Deadline); // The UI observer catches; the actual writer must still fault.
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
        // Exact production shell boundaries: no fake suspended flag or viewer-only notice.
        // OpenMediaGalleryAsync has no archive thread to fetch; avoid creating a source/network request.
        ShellErrorCall(shell, "OpenGalleryReader");
        ShellErrorCall(shell, "SuspendGallery");
        await shell.GalleryCleanupTask.WaitAsync(Deadline);
        Require(ShellErrorField<bool>(shell, "_largeReaderOpen") && ShellErrorField<bool>(shell, "_gallerySuspended")
            && !shell.FindControl<ContentControl>("GalleryHost")!.IsVisible,
            "Actual shell Reader/suspend path did not hide and retire gallery playback.");
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
