using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class GalleryMutePersistenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithoutUserChoiceEverySelectionUsesTheConfiguredStartupPolicy(bool startMuted)
    {
        var mute = new VideoMuteIntent();
        mute.ConfigureStartup(startMuted);
        Assert.Equal(startMuted, mute.BeginMedia());
        Assert.Equal(startMuted, mute.BeginMedia());
        mute.ConfigureStartup(!startMuted);
        Assert.Equal(!startMuted, mute.BeginMedia());
    }

    [Fact]
    public void ManualMuteSurvivesNextPreviousAndAnInterveningImage()
    {
        var mute = new VideoMuteIntent();
        mute.ConfigureStartup(false);
        Assert.False(mute.BeginMedia()); // Video A.
        Assert.True(mute.Toggle());
        Assert.True(mute.BeginMedia()); // Next video B, same native owner, new decoder.
        mute.ConfigureStartup(false); // Host config reapplication while showing an image.
        mute.OnVolumeChanged(70, userInitiated: false);
        Assert.True(mute.Muted);
        Assert.True(mute.BeginMedia()); // Image -> video A (Prev).
        Assert.True(mute.BeginMedia()); // Next again.
        Assert.False(mute.Toggle()); // Explicit unmute.
        Assert.False(mute.BeginMedia());
    }

    [Fact]
    public void ExplicitUnmuteAlsoSurvivesStartupMutedAndConfigEchoes()
    {
        var mute = new VideoMuteIntent();
        mute.ConfigureStartup(true);
        Assert.True(mute.BeginMedia());
        Assert.False(mute.Toggle());
        for (int i = 0; i < 5; i++)
        {
            mute.ConfigureStartup(true);
            mute.OnVolumeChanged(70 + i, userInitiated: false);
            Assert.False(mute.BeginMedia());
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(70)]
    [InlineData(100)]
    public void ProgrammaticVolumeNeverUnmutesOrChangesTheNextSelection(int volume)
    {
        var mute = new VideoMuteIntent();
        mute.BeginMedia();
        mute.Toggle();
        mute.OnVolumeChanged(volume, userInitiated: false);
        Assert.True(mute.Muted);
        Assert.True(mute.BeginMedia());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(69)] // Down from the default.
    [InlineData(71)] // Up from the default.
    [InlineData(100)]
    public void ExplicitPositiveVolumeRetainsExistingUnmuteAndOverridesStartupForThisSession(int volume)
    {
        var mute = new VideoMuteIntent();
        mute.ConfigureStartup(true);
        mute.BeginMedia();
        mute.OnVolumeChanged(volume, userInitiated: true);
        Assert.False(mute.Muted);
        mute.ConfigureStartup(true);
        Assert.False(mute.BeginMedia());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitZeroVolumePreservesMuteIntentRatherThanInventingAnotherChoice(bool startMuted)
    {
        var mute = new VideoMuteIntent();
        mute.ConfigureStartup(startMuted);
        mute.BeginMedia();
        mute.OnVolumeChanged(0, userInitiated: true);
        Assert.Equal(startMuted, mute.Muted);
        Assert.Equal(startMuted, mute.BeginMedia());
    }

    [Fact]
    public void LateAudioReadyVolumeReplayMustNotUndoAMoreRecentMute()
    {
        var volume = new VideoVolumeIntent();
        var mute = new VideoMuteIntent();
        mute.BeginMedia();
        var pending = volume.Request(1, 43);
        mute.OnVolumeChanged(pending.Volume, userInitiated: true);
        Assert.False(mute.Muted);
        mute.Toggle(); // Accepted while the volume worker/audio output is still delayed.
        Assert.True(volume.IsCurrent(pending)); // It is still a valid volume request!
        // Native application uses current mute intent, not "positive volume => unmute".
        mute.OnVolumeChanged(pending.Volume, userInitiated: false);
        Assert.Equal(43, volume.Current.Volume);
        Assert.True(mute.Muted);
        Assert.True(mute.BeginMedia());
    }

    [Fact]
    public void RepeatedToggleIntentIsAcceptedInInputOrderBeforeNativeWorkCompletes()
    {
        var mute = new VideoMuteIntent();
        mute.BeginMedia();
        Assert.True(mute.Toggle());
        Assert.False(mute.Toggle());
        Assert.True(mute.Toggle());
        mute.ConfigureStartup(false);
        Assert.True(mute.BeginMedia()); // Native retirement must not discard accepted intent.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewGalleryUsesSavedStartupPolicyNotThePreviousGallerysChoice(bool startMuted)
    {
        var oldSession = new VideoMuteIntent();
        oldSession.ConfigureStartup(startMuted);
        oldSession.BeginMedia();
        oldSession.Toggle();
        Assert.Equal(!startMuted, oldSession.Muted);
        var newSession = new VideoMuteIntent();
        newSession.ConfigureStartup(startMuted);
        Assert.Equal(startMuted, newSession.BeginMedia());
    }

    [Fact]
    public async Task AcceptedMuteSurvivesRetirementEvenWhenQueuedNativeCommandIsRejected()
    {
        var lifetime = new VideoPlaybackLifetime(_ => Task.CompletedTask, () => Task.CompletedTask);
        using var operations = new SemaphoreSlim(0, 1); // A slow native operation owns the slot.
        var mute = new VideoMuteIntent();
        var first = lifetime.AcceptShow(CancellationToken.None)!;
        mute.BeginMedia();
        NativeSlot current = new(first.Generation);
        mute.Toggle(); // Production accepts this synchronously, before queuing native work.
        int retiredWrites = 0;
        Task queued = VideoPlaybackCommands.RunAsync(operations, lifetime, first.Generation,
            () => current, slot => slot.Generation, _ => retiredWrites++);
        var next = lifetime.AcceptShow(CancellationToken.None)!;
        current = new(next.Generation);
        Assert.True(mute.BeginMedia());
        lifetime.FinishShow(first);
        lifetime.FinishShow(next);
        operations.Release();
        await queued.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, retiredWrites); // Existing stale-generation safety remains intact.
        Assert.True(mute.Muted); // It did not also discard the session-level choice.
        await lifetime.DisposeAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed record NativeSlot(long Generation);

    [Fact]
    public void ConfigureIsStartupPolicyNotAnActiveTransportCommand()
    {
        var mute = new VideoMuteIntent();
        mute.ConfigureStartup(false);
        mute.BeginMedia();
        mute.ConfigureStartup(true);
        Assert.False(mute.Muted);
        Assert.True(mute.BeginMedia());
        mute.ConfigureStartup(false);
        Assert.True(mute.Muted);
        Assert.False(mute.BeginMedia());
    }
}
