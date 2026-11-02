using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using MeowChan.Desktop;

namespace MeowChan.DesktopSmoke;

/// <summary>
/// Opt-in real OS input + real decoder check. Root must seed VideoGalleryChecks with
/// includeSecondVideo:true and a functional-audio fixture on the isolated native harness.
/// Hook: await GalleryMutePersistenceChecks.RunAsync(mainWindow), before the normal checks.
/// No MediaInteractionChecks/Program ownership changes here; no physical-audio claim.
/// </summary>
internal static class GalleryMutePersistenceChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        Config saved = window.SessionHost.Config.Clone();
        GalleryViewer? viewer = null;
        using var native = new MediaNativeInput(window);
        try
        {
            await OpenAsync();
            // Only live viewer configuration: no settings/profile write and no new preference.
            Configure(saved.VideoStartMuted, 42);
            await WaitAsync(p => p.AudioAvailable && p.Volume == 42 && p.State == "Playing");
            if ((await SampleAsync()).Muted) await ClickMuteAsync(false, "Playing");
            await native.ClickAsync(Named<Button>("GalleryVideoPlayPause"));
            await WaitAsync(p => p.State == "Paused");
            await ClickMuteAsync(true, "Paused");
            viewer!.ConfigurePlayback(new GalleryPlaybackPreferences { Mode = GalleryPlaybackMode.Repeat });

            // Exercise changed-value Configure, not just an idempotent settings echo. AudioReady
            // also reconciles after these operations; hold observations beyond a single sample.
            Configure(startMuted: false, volume: 55);
            await WaitAsync(p => p.Muted && p.Volume == 55 && p.State == "Paused");
            await HoldAsync(true, "Paused");
            Require(Named<ToggleButton>("GalleryVideoMute").IsChecked == true, "Mute UI disagrees with the decoder.");

            // Hold the normal native-operation semaphore, not an alternate player/decoder.
            // A queued positive volume application must see the LATER accepted mute intent.
            await ClickMuteAsync(false, "Paused");
            await QueueVolumeThenMuteAsync(54, navigateWhilePending: false);
            await WaitAsync(p => p.Muted && p.Volume == 54 && p.State == "Paused");
            await HoldAsync(true, "Paused");
            await ClickMuteAsync(false, "Paused");
            // Retire this playback while the native mute is still queued. The stale
            // native write may be rejected, but the next decoder must inherit mute.
            await QueueVolumeThenMuteAsync(56, navigateWhilePending: true); // Next: image.
            await NavigateAsync(MediaNativeInput.Right, "2002"); // Next: video B.
            await WaitAsync(p => p.AudioAvailable && p.Muted && p.LoopEnabled && p.State == "Playing");
            await HoldAsync(true, "Playing");
            await NavigateAsync(MediaNativeInput.Left, "2001");
            await NavigateAsync(MediaNativeInput.Left, "2000"); // Prev back to video A.
            await WaitAsync(p => p.AudioAvailable && p.Muted && p.LoopEnabled && p.State == "Playing");
            await HoldAsync(true, "Playing");

            // Explicit unmute survives even if the host reapplies startup-muted afterward.
            await ClickMuteAsync(false, "Playing");
            Configure(startMuted: true, volume: 42);
            await WaitAsync(p => !p.Muted && p.Volume == 42 && p.State == "Playing");
            await NavigateAsync(MediaNativeInput.Right, "2001");
            await NavigateAsync(MediaNativeInput.Right, "2002");
            await WaitAsync(p => p.AudioAvailable && !p.Muted && p.State == "Playing");
            await HoldAsync(false, "Playing");

            // Existing explicit user-volume unmute behavior, via actual Up/Down input.
            await native.ClickAsync(Named<Button>("GalleryVideoPlayPause"));
            await WaitAsync(p => p.State == "Paused");
            foreach (var (key, expectedVolume) in new[] { (MediaNativeInput.Up, 43), (MediaNativeInput.Down, 42) })
            {
                await ClickMuteAsync(true, "Paused");
                await native.ClickAsync(Named<Slider>("GalleryVideoSeek"),
                    MediaInteractionSmokeHelpers.TrackPoint(Named<Slider>("GalleryVideoSeek"), .20));
                await native.KeyAsync(key);
                await WaitAsync(p => !p.Muted && p.Volume == expectedVolume && p.State == "Paused");
                await HoldAsync(false, "Paused");
            }

            // Leave this gallery opposite its SAVED startup policy; close/reopen must not
            // inherit the runtime choice. Existing volume persistence remains intentionally live.
            if ((await SampleAsync()).Muted == saved.VideoStartMuted)
                await ClickMuteAsync(!saved.VideoStartMuted, "Paused");
            await CloseAsync();
            await OpenAsync();
            await WaitAsync(p => p.AudioAvailable && p.Muted == saved.VideoStartMuted && p.State == "Playing");
            await HoldAsync(saved.VideoStartMuted, "Playing");
            Require(window.SessionHost.Config.VideoStartMuted == saved.VideoStartMuted,
                "Gallery runtime mute changed the saved startup policy.");
            Console.WriteLine("SMOKE gallery mute persistence: native mute/unmute, held production volume/mute and retirement races, changed Configure volume, video/image/video Next/Prev, Repeat retained, explicit Up/Down unmute without resume, and fresh-gallery startup policy passed. Decoder observations only; no physical audio-output acceptance.");
        }
        finally
        {
            if (viewer != null) await CloseAsync();
        }

        T Named<T>(string name) where T : Control => viewer!.GetGalleryVisualDescendants().OfType<T>().Single(c => c.Name == name);
        GalleryVideoPlayer Player() => viewer!.GetGalleryVisualDescendants().OfType<GalleryVideoPlayer>().Single();
        Task<GalleryVideoPlayer.PlaybackEvidence> SampleAsync() => Player().InspectPlaybackAsync().WaitAsync(TimeSpan.FromSeconds(4));
        void Configure(bool startMuted, int volume) => viewer!.Configure(false, true, true, startMuted, volume);
        async Task OpenAsync()
        {
            await window.OpenGalleryAsync(VideoGalleryChecks.Id);
            viewer = (GalleryViewer)window.FindControl<ContentControl>("GalleryHost")!.Content!;
            Require(viewer.CaptureState().PostId == "2000", "Mute fixture must start at video A.");
            viewer.ConfigurePlayback(new GalleryPlaybackPreferences()); // Docked, no hidden native input targets.
            await WaitAsync(p => p.HasPlayback);
            // Fixture setup only: saved autoplay may be off; make audio readiness observable.
            await Player().PlayAsync();
            await WaitAsync(p => p.AudioAvailable && p.State == "Playing" && MediaInteractionSmokeHelpers.HasDecodedFrames(p));
        }
        async Task CloseAsync()
        {
            await native.ClickAsync(Named<Button>("GalleryClose"));
            await Program.WaitUntilAsync(() => !window.FindControl<ContentControl>("GalleryHost")!.IsVisible);
            await window.GalleryCleanupTask.WaitAsync(TimeSpan.FromSeconds(8));
            viewer = null;
        }
        async Task NavigateAsync(ulong key, string post)
        {
            await native.KeyAsync(key);
            await Program.WaitUntilAsync(() => viewer!.CaptureState().PostId == post);
        }
        async Task QueueVolumeThenMuteAsync(int volume, bool navigateWhilePending)
        {
            GalleryVideoPlayer player = Player();
            var operations = (SemaphoreSlim)typeof(GalleryVideoPlayer).GetField("_operations",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(player)!;
            Require(await operations.WaitAsync(TimeSpan.FromSeconds(4)), "Could not hold the native operation boundary.");
            try
            {
                // Direct Slider.Value event is controlled USER input here; the native
                // application still goes through SetVolumeAsync -> ApplyVolume -> ApplyMute.
                // Mute and navigation themselves use real OS input.
                Named<Slider>("GalleryVideoVolume").Value = volume;
                Require(Named<ToggleButton>("GalleryVideoMute").IsChecked == false,
                    "Explicit positive volume did not accept unmute intent synchronously.");
                await native.ClickAsync(Named<ToggleButton>("GalleryVideoMute"));
                Require(Named<ToggleButton>("GalleryVideoMute").IsChecked == true,
                    "Mute intent was not accepted before blocked native work.");
                if (!navigateWhilePending)
                {
                    // Deterministically apply the VALID older volume through the actual
                    // production method while queued mute work still cannot run. Otherwise
                    // a later queued mute could mask an incorrect implicit unmute here.
                    var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                    var volumeIntent = (VideoVolumeIntent)typeof(GalleryVideoPlayer)
                        .GetField("_volumeIntent", flags)!.GetValue(player)!;
                    VideoVolumeRequest request = volumeIntent.Current;
                    object playback = typeof(GalleryVideoPlayer).GetField("_playback", flags)!.GetValue(player)!;
                    var nativePlayer = (LibVLCSharp.Shared.MediaPlayer)playback.GetType().GetProperty("Player")!.GetValue(playback)!;
                    var applied = await Task.Run(() =>
                    {
                        typeof(GalleryVideoPlayer).GetMethod("ApplyVolume", flags)!
                            .Invoke(player, new object[] { nativePlayer, request });
                        var first = (Mute: nativePlayer.Mute, Volume: nativePlayer.Volume);
                        var observed = first;
                        var settle = Stopwatch.StartNew();
                        // LibVLC's audio setters enqueue output changes. Keep ALL ordinary
                        // commands blocked while observing their acknowledgement, so a later
                        // queued mute still cannot repair/mask this single ApplyVolume call.
                        while ((!observed.Mute || observed.Volume != volume) && settle.Elapsed < TimeSpan.FromSeconds(2))
                        {
                            Thread.Sleep(10);
                            observed = (nativePlayer.Mute, nativePlayer.Volume);
                        }
                        return (observed.Mute, observed.Volume, FirstMute: first.Mute, FirstVolume: first.Volume,
                            SettledMs: settle.ElapsedMilliseconds);
                    }); // Keep ownership until worker exits; outer isolated harness owns a hang timeout.
                    var muteIntent = (VideoMuteIntent)typeof(GalleryVideoPlayer).GetField("_muteIntent", flags)!.GetValue(player)!;
                    Console.WriteLine($"SMOKE held ApplyVolume: requested={volume}; current={request}; intentMuted={muteIntent.Muted}; nativeMute={applied.Mute}; nativeVolume={applied.Volume}; firstMute={applied.FirstMute}; firstVolume={applied.FirstVolume}; settleMs={applied.SettledMs}.");
                    Require(applied.Mute && applied.Volume == volume,
                        $"Production ApplyVolume lost a later mute while queued mute could not mask it: expectedVolume={volume}; request={request}; intentMuted={muteIntent.Muted}; nativeMute={applied.Mute}; nativeVolume={applied.Volume}; firstMute={applied.FirstMute}; firstVolume={applied.FirstVolume}; settleMs={applied.SettledMs}.");
                }
                if (navigateWhilePending) await NavigateAsync(MediaNativeInput.Right, "2001");
            }
            finally { operations.Release(); }
        }
        async Task ClickMuteAsync(bool muted, string state)
        {
            await native.ClickAsync(Named<ToggleButton>("GalleryVideoMute"));
            await WaitAsync(p => p.AudioAvailable && p.Muted == muted && p.State == state);
        }
        async Task WaitAsync(Func<GalleryVideoPlayer.PlaybackEvidence, bool> condition)
        {
            var clock = Stopwatch.StartNew();
            GalleryVideoPlayer.PlaybackEvidence? last = null;
            while (clock.Elapsed < TimeSpan.FromSeconds(8))
            {
                last = await SampleAsync();
                if (condition(last)) return;
                await Task.Delay(80);
            }
            throw new TimeoutException("Gallery mute decoder condition failed (functional audio required): " + last);
        }
        async Task HoldAsync(bool muted, string state)
        {
            for (int i = 0; i < 5; i++)
            {
                await Task.Delay(100);
                var evidence = await SampleAsync();
                Require(evidence.Muted == muted && evidence.State == state,
                    "Mute or playback state changed during readiness/readback hold: " + evidence);
            }
        }
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
