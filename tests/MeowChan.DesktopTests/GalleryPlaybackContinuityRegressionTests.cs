using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;

namespace MeowChan.DesktopTests;

// Capture leases are global. Reuse the existing nonparallel collection without
// publishing profile bindings, constructing a native player or opening media.
[Collection("Media shortcut routing")]
public sealed class GalleryPlaybackContinuityRegressionTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 29)]
    [InlineData(true, 1)]
    [InlineData(true, 100)]
    public void NextPreviousAndImageGapKeepCombinedAudioAndLoopIntent(bool muted, int requestedVolume)
    {
        foreach (bool repeat in new[] { false, true })
        {
            var mute = new VideoMuteIntent();
            var volume = new VideoVolumeIntent();
            var transport = new VideoPlaybackIntent();
            mute.ConfigureStartup(!muted);
            mute.BeginMedia();
            // A user volume edit precedes the explicit final mute/unmute choice.
            mute.OnVolumeChanged(requestedVolume, userInitiated: true);
            mute.Toggle(); // Always establish an explicit session choice.
            if (mute.Muted != muted) mute.Toggle();
            transport.SetLoopEnabled(repeat);
            var previous = volume.Request(1, requestedVolume);

            // Video A -> B -> image -> A -> B. Generations are playback identities,
            // not item indices; returning to A must not revive an old request.
            foreach (long generation in new long[] { 2, 4, 5 })
            {
                mute.ConfigureStartup(!muted); // Settings/host replay, not a toggle.
                mute.OnVolumeChanged(requestedVolume, userInitiated: false);
                Assert.Equal(muted, mute.BeginMedia());
                var current = volume.Request(generation, volume.Current.Volume);
                Assert.False(volume.IsCurrent(previous));
                Assert.True(volume.IsCurrent(current));
                Assert.Equal(requestedVolume, current.Volume);
                transport.Set(generation, VideoPlaybackMode.Paused);
                Assert.Equal(VideoPlaybackMode.Paused, transport.Current.Mode);
                Assert.Equal(repeat, transport.LoopEnabled);
                previous = current;
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetirementRejectsQueuedAudioWritesButKeepsLatestSessionChoice(bool muted)
    {
        var lifetime = new VideoPlaybackLifetime(_ => Task.CompletedTask, () => Task.CompletedTask);
        using var operations = new SemaphoreSlim(0, 1);
        var volume = new VideoVolumeIntent();
        var mute = new VideoMuteIntent();
        var first = lifetime.AcceptShow(CancellationToken.None)!;
        VideoPlaybackLifetime.ShowRequest? next = null;
        NativeAudio current = new(first.Generation);
        int writes = 0;
        Task queued = Task.CompletedTask;
        try
        {
            mute.ConfigureStartup(true);
            mute.BeginMedia();
            var pending = volume.Request(first.Generation, 24);
            mute.OnVolumeChanged(pending.Volume, userInitiated: true);
            queued = VideoPlaybackCommands.RunAsync(operations, lifetime, first.Generation,
                () => current, slot => slot.Generation, slot =>
                {
                    writes++;
                    slot.Volume = pending.Volume;
                    slot.Muted = mute.Muted;
                });
            // Accept newer input while native work is held. A media replacement
            // must inherit this value even though the old write cannot complete.
            volume.Request(first.Generation, 61);
            if (muted) mute.Toggle();
            next = lifetime.AcceptShow(CancellationToken.None)!;
            var successorVolume = volume.Request(next.Generation, volume.Current.Volume);
            current = new(next.Generation);
            Assert.Equal(muted, mute.BeginMedia());
            Assert.False(volume.IsCurrent(pending));
            Assert.Equal(61, successorVolume.Volume);
            Assert.True(volume.IsCurrent(successorVolume));
        }
        finally
        {
            lifetime.FinishShow(first);
            if (next != null) lifetime.FinishShow(next);
            operations.Release();
            try { await queued.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { await lifetime.DisposeAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        }
        Assert.Equal(0, writes);
        Assert.Equal(-1, current.Volume);
        Assert.Equal(muted, mute.Muted);
        Assert.Equal(61, volume.Current.Volume);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void NewGenerationInvalidatesEvenSameClampedVolumeAndLaterSameGenerationRequest(int expected)
    {
        var volume = new VideoVolumeIntent();
        var old = volume.Request(7, expected == 0 ? -50 : 150);
        Assert.Equal(expected, old.Volume);
        var next = volume.Request(8, expected);
        Assert.False(volume.IsCurrent(old));
        Assert.True(next.Revision > old.Revision);
        var latest = volume.Request(8, expected);
        Assert.False(volume.IsCurrent(next));
        Assert.True(volume.IsCurrent(latest));
        Assert.Equal(latest, volume.Current);
    }

    [Theory]
    [InlineData(Key.Up, "video.volume-up")]
    [InlineData(Key.Down, "video.volume-down")]
    public void DefaultVolumeArrowsRecognizeOwnedSliderDescendantsButDoNotEditThemNatively(Key key, string id)
    {
        var seekChild = new Border();
        var volumeChild = new Border();
        var seek = new TimelineHost(seekChild) { Minimum = 0, Maximum = 1000, Value = 350 };
        var volume = new VolumeHost(volumeChild) { Minimum = 0, Maximum = 100, Value = 29 };
        foreach (Control source in new[] { seekChild, volumeChild })
        {
            var press = new KeyEventArgs { Source = source, Key = key };
            Assert.True(EffectiveShortcutBindings.Build().Matches(press, id));
            Assert.False(CommandBindings.Matches(press, id)); // Generic native-input guard.
            Assert.True(CommandBindings.MatchesGalleryTransport(press, id, seek, volume));
            Assert.False(press.Handled); // Matching is not execution.
            using (ShortcutService.BeginCapture())
                Assert.False(CommandBindings.MatchesGalleryTransport(press, id, seek, volume));
            press.Handled = true;
            Assert.False(CommandBindings.MatchesGalleryTransport(press, id, seek, volume));
        }
        seek.ApplyKey(new KeyEventArgs { Source = seekChild, Key = key });
        volume.ApplyKey(new KeyEventArgs { Source = volumeChild, Key = key });
        Assert.Equal(350, seek.Value);
        Assert.Equal(29, volume.Value);
    }

    [Theory]
    [InlineData(Key.Up, KeyModifiers.None)]
    [InlineData(Key.Down, KeyModifiers.None)]
    [InlineData(Key.Up, KeyModifiers.Control)]
    [InlineData(Key.Down, KeyModifiers.Shift)]
    [InlineData(Key.F9, KeyModifiers.Control | KeyModifiers.Shift)]
    public void TextInputDescendantsKeepArrowsAndCustomVolumeGestures(Key key, KeyModifiers modifiers)
    {
        var child = new Border();
        var editor = new TextInputHost(child) { Text = "synthetic note", AcceptsReturn = true };
        var seek = new TimelineHost(new Border());
        var volume = new VolumeHost(new Border());
        foreach (Control source in new Control[] { editor, child })
        {
            var press = new KeyEventArgs { Source = source, Key = key, KeyModifiers = modifiers };
            Assert.True(CommandRouter.IsNativeInput(source, key));
            Assert.False(GalleryViewer.IsGalleryShortcutInput(press));
            foreach (string id in new[] { "video.volume-up", "video.volume-down", "video.mute", "gallery.next", "gallery.previous" })
            {
                Assert.False(CommandBindings.Matches(press, id));
                Assert.False(CommandBindings.MatchesGalleryTransport(press, id, seek, volume));
            }
            // Also prove the editor guard does not rely on a lack of matching
            // bindings: an explicit custom volume gesture can match this key.
            string prefix = modifiers switch
            {
                KeyModifiers.None => "",
                KeyModifiers.Control => "Control+",
                KeyModifiers.Shift => "Shift+",
                _ => "Control+Shift+"
            };
            var bindings = EffectiveShortcutBindings.Build(new ShortcutBindingsSection()
                .Set("gallery.previous", []).Set("gallery.next", [])
                .Set("video.volume-down", [])
                .Set("video.volume-up", [ShortcutGesture.Parse(prefix + key)]));
            Assert.True(bindings.Matches(press, "video.volume-up"));
            Assert.False(press.Handled);
        }
        Assert.Equal("synthetic note", editor.Text);
    }

    [Fact]
    public void PlaybackPreferenceEditsDoNotOverwriteTheSeparateAudioPreferences()
    {
        var config = new Config { VideoVolume = 29, VideoStartMuted = true, VideoAutoplay = false };
        var baseline = GalleryPlaybackPreferences.FromConfig(config);
        var edited = baseline.WithControlsOverlay(true) with { VideoRepeat = true };
        // A newer live volume has priority over an unrelated playback draft.
        config.VideoVolume = 61;
        edited.ApplyChangesTo(config, baseline);
        edited.ApplyTo(config);
        Assert.Equal(61, config.VideoVolume);
        Assert.True(config.VideoStartMuted);
        Assert.False(config.VideoAutoplay);
        Assert.True(GalleryPlaybackPreferences.FromConfig(config).VideoRepeat);
    }

    [Fact]
    public void PlayerWiringSeedsSuccessorsFromCurrentVolumeWithoutResettingSessionMute()
    {
        // Complement headless intent cases with a narrow source-wiring contract.
        // This is not a decoder/UI integration test and does not construct VLC.
        string source = ReadDesktopSource("GalleryVideoPlayer.cs");
        string show = Between(source, "private async Task ShowCoreAsync(", "private bool IsCurrent(long generation)");
        Assert.Contains("_muteIntent.BeginMedia();", show);
        Assert.Contains("_volumeIntent.Request(generation, (int)_volume.Value);", show);
        Assert.Contains("VideoVolumeRequest initialVolume = _volumeIntent.Current;", show);
        Assert.Contains("player.Volume = initialVolume.Volume; ApplyMute(player);", show);
        Assert.DoesNotContain("_muteIntent =", show);
        Assert.DoesNotContain("_volume.Value =", show);

        string configure = Between(source, "internal void Configure(", "internal Task TogglePlaybackAsync()");
        Assert.Contains("_muteIntent.ConfigureStartup(startMuted);", configure);
        Assert.Contains("SetVolumeAsync(nextVolume, userInitiated: false)", configure);
        string applyVolume = Between(source, "private void ApplyVolume(", "private void ApplyMute(");
        Assert.Contains("if (!_volumeIntent.IsCurrent(request)) return;", applyVolume);
        Assert.Contains("ApplyMute(player);", applyVolume);
        Assert.DoesNotContain("OnVolumeChanged(", applyVolume);
    }

    [Fact]
    public void PlayerPublishesVolumeInInputOrderAndArrowAdjustmentUsesTheExistingSliderPath()
    {
        string source = ReadDesktopSource("GalleryVideoPlayer.cs");
        string input = Between(source, "_volume.PropertyChanged +=", "_seek.PropertyChanged +=");
        int accept = input.IndexOf("SetVolumeAsync(volume, userInitiated: true)", StringComparison.Ordinal);
        int publish = input.IndexOf("VolumeChanged?.Invoke(volume);", StringComparison.Ordinal);
        Assert.True(accept >= 0 && publish > accept);
        Assert.DoesNotContain("await ", input);

        string adjust = Between(source, "internal void AdjustVolume(", "private Task SetVolumeAsync(");
        Assert.Contains("if (!CanAdjustVolume || direction == 0) return;", adjust);
        Assert.Contains("_volume.SmallChange", adjust);
        Assert.Contains("_volume.Value = Math.Clamp(", adjust);
        Assert.DoesNotContain("PlayAsync(", adjust);
        Assert.DoesNotContain("PauseAsync(", adjust);
        Assert.DoesNotContain("SetVolumeAsync(", adjust);
    }

    private static string Between(string source, string start, string end)
    {
        int begin = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(begin >= 0, "Missing source boundary: " + start);
        int finish = source.IndexOf(end, begin + start.Length, StringComparison.Ordinal);
        Assert.True(finish > begin, "Missing source boundary: " + end);
        return source[begin..finish];
    }

    private static string ReadDesktopSource(string name, [CallerFilePath] string testPath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testPath)!, "..", "..", "Desktop", name)));

    private sealed class TimelineHost : GalleryTransportSlider
    {
        internal TimelineHost(Control child) => VisualChildren.Add(child);
        internal void ApplyKey(KeyEventArgs e) => base.OnKeyDown(e);
    }

    private sealed class VolumeHost : GalleryVolumeSlider
    {
        internal VolumeHost(Control child) => VisualChildren.Add(child);
        internal void ApplyKey(KeyEventArgs e) => base.OnKeyDown(e);
    }

    private sealed class TextInputHost : TextBox
    {
        internal TextInputHost(Control child) => VisualChildren.Add(child);
    }

    private sealed class NativeAudio(long generation)
    {
        internal long Generation { get; } = generation;
        internal int Volume { get; set; } = -1;
        internal bool Muted { get; set; }
    }
}
