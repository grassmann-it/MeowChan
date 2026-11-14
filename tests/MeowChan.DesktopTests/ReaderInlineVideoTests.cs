using System.Text.Json;
using Avalonia.Input;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.DesktopTests;

/// <summary>
/// Inline video in the reader (R10): the opt-in settings and their bounds, the controller's rules (no player while off, one plays at a
/// time, the bound on live players, pause and release on the reader's events, disposal) and the keys. The players are fakes: the
/// rules are window-free; the real wrapper around the gallery's player is checked in the reader-inline-video smoke.
/// </summary>
[Collection("Localization")]
public sealed class ReaderInlineVideoTests : IDisposable
{
    public ReaderInlineVideoTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private sealed class FakePlayer : IReaderInlineVideoPlayer
    {
        internal bool Started, Disposed;
        internal int Plays, Pauses;
        public bool IsPlaying { get; private set; }
        public Task StartAsync() { Started = true; IsPlaying = true; return Task.CompletedTask; }
        public Task PlayAsync() { Plays++; IsPlaying = true; return Task.CompletedTask; }
        public Task PauseAsync() { Pauses++; IsPlaying = false; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Disposed = true; IsPlaying = false; return ValueTask.CompletedTask; }
    }

    private sealed class FakeOwner : IReaderInlineVideoOwner
    {
        internal int Releases;
        internal bool Current;
        internal readonly List<bool> Announced = new();
        public void PlayerReleased() => Releases++;
        public void CurrentChanged(bool current) => Current = current;
        public void PlaybackChanged(bool playing) => Announced.Add(playing);
    }

    private static ReaderInlineVideoOptions On(int players = 2, bool autoplay = false) =>
        ReaderInlineVideoOptions.Default with { Enabled = true, MaxPlayers = players, AutoplayMuted = autoplay };

    private static async Task<ReaderInlineVideoController> Controller(ReaderInlineVideoOptions options)
    {
        ReaderInlineVideoController controller = new();
        await controller.ConfigureAsync(options);
        return controller;
    }

    private static (FakeOwner Owner, List<FakePlayer> Players, Func<IReaderInlineVideoPlayer> Create) Video()
    {
        List<FakePlayer> players = new();
        return (new FakeOwner(), players, () => { FakePlayer player = new(); players.Add(player); return player; });
    }

    // ---- settings ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void InlineVideoAndItsAutoplayAreOffByDefaultWithTheirDefaultLimits()
    {
        Config config = new();
        Assert.False(config.ReaderInlineVideo);
        Assert.False(config.ReaderInlineVideoAutoplayMuted);
        Assert.Equal(2, config.ReaderInlineVideoMaxPlayers);
        Assert.Equal(256, config.ReaderInlineVideoMaxFileMiB);
        ReaderInlineVideoOptions options = ReaderInlineVideoOptions.From(config);
        Assert.Equal(ReaderInlineVideoOptions.Default, options);
        Assert.Equal(ReaderInlineVideoOptions.Default, ReaderMediaPreferences.From(config).Video);
        // The gallery's volume and "start muted" are reused, not a second preference.
        Config gallery = new() { VideoVolume = 35, VideoStartMuted = true };
        Assert.Equal(35, ReaderInlineVideoOptions.From(gallery).Volume);
        Assert.True(ReaderInlineVideoOptions.From(gallery).StartMuted);
    }

    [Theory]
    [InlineData(int.MinValue, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(4, 4)]
    [InlineData(5, 4)]
    [InlineData(int.MaxValue, 4)]
    public void ThePlayerBoundIsClampedOnLoadAndInTheOptions(int stored, int expected)
    {
        Config loaded = JsonSerializer.Deserialize<Config>("{\"ReaderInlineVideoMaxPlayers\":" + stored + "}")!;
        Assert.Equal(expected, loaded.ReaderInlineVideoMaxPlayers);
        Assert.Equal(expected, (On() with { MaxPlayers = stored }).Clamped().MaxPlayers);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(256, 256)]
    [InlineData(4096, 4096)]
    [InlineData(5000, 4096)]
    public void TheFileLimitIsClampedAndCountedInMebibytes(int stored, int expected)
    {
        Config loaded = JsonSerializer.Deserialize<Config>("{\"ReaderInlineVideoMaxFileMiB\":" + stored + "}")!;
        Assert.Equal(expected, loaded.ReaderInlineVideoMaxFileMiB);
        Assert.Equal(expected * 1024L * 1024, (On() with { MaxFileMiB = stored }).MaxFileBytes);
    }

    [Fact]
    public void CloneAndBothCopiesKeepEveryInlineVideoValue()
    {
        Config source = new() { ReaderInlineVideo = true, ReaderInlineVideoAutoplayMuted = true, ReaderInlineVideoMaxPlayers = 3, ReaderInlineVideoMaxFileMiB = 64 };
        Config clone = source.Clone(), copy = new(), settings = new();
        copy.CopyFrom(source);
        settings.CopySettingsFieldsFrom(source);
        foreach (Config config in new[] { clone, copy, settings })
            Assert.Equal(new ReaderInlineVideoOptions(true, true, 3, 64), ReaderInlineVideoOptions.From(config));
    }

    [Fact]
    public void TheLimitReasonNamesTheFileAndTheLimitInBinaryUnits()
    {
        ReaderInlineVideoOptions options = On() with { MaxFileMiB = 8 };
        Assert.Null(ReaderInlineVideoRules.LimitReason(options, 8L * 1024 * 1024, "clip.webm"));
        string reason = ReaderInlineVideoRules.LimitReason(options, 8L * 1024 * 1024 + 1, "clip.webm")!;
        Assert.Contains("clip.webm", reason);
        Assert.Contains(SizeText.Format(8L * 1024 * 1024), reason);
        Assert.EndsWith("MiB", SizeText.Format(8L * 1024 * 1024));
        Assert.Contains("gallery", reason);
    }

    // ---- what may play ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void OnlyASavedVideoAttachmentIsOfferedAndOnlyWithTheSettingOn()
    {
        ArchiveAttachmentRow video = new(new CapturedAttachment(0, "1", OriginalFileName: "clip.webm")) { ResolveEntry = _ => Task.FromResult<MeowChan.Gallery.GalleryEntry?>(null) };
        ArchiveAttachmentRow image = video with { Attachment = new CapturedAttachment(0, "2", OriginalFileName: "cat.jpg") };
        ArchiveAttachmentRow list = video with { ResolveEntry = null }; // the archive lists: no reader entry
        ArchiveAttachmentRow deleted = video with { Attachment = new CapturedAttachment(0, "3", OriginalFileName: "gone.mp4", IsDeleted: true) };
        Assert.True(ReaderInlineVideoRules.CanOffer(On(), video));
        Assert.False(ReaderInlineVideoRules.CanOffer(ReaderInlineVideoOptions.Default, video));
        Assert.False(ReaderInlineVideoRules.CanOffer(On(), image));
        Assert.False(ReaderInlineVideoRules.CanOffer(On(), list));
        Assert.False(ReaderInlineVideoRules.CanOffer(On(), deleted));
        Assert.False(ReaderInlineVideoRules.CanOffer(On(), null));
    }

    [Fact]
    public async Task WithTheSettingOffNoPlayerIsEverCreated()
    {
        ReaderInlineVideoController controller = await Controller(ReaderInlineVideoOptions.Default);
        var (owner, players, create) = Video();
        Assert.False(await controller.PlayAsync(owner, create));
        Assert.False(await controller.PlayAsync(owner, create));
        await controller.ToggleAsync(owner, create);
        Assert.Empty(players);
        Assert.Equal(0, controller.LiveCount);
        Assert.Equal(0, controller.Created);
        Assert.Null(controller.Current);
        Assert.False(ReaderInlineVideoRules.ShouldAutoplay(ReaderInlineVideoOptions.Default with { AutoplayMuted = true }, shown: true, presentedElsewhere: false));
    }

    [Fact]
    public void AutoplayNeedsBothOptInsAShownVideoAndTheFocusedMediaNotPresentedElsewhere()
    {
        Assert.False(ReaderInlineVideoRules.ShouldAutoplay(On(), shown: true, presentedElsewhere: false));
        Assert.True(ReaderInlineVideoRules.ShouldAutoplay(On(autoplay: true), shown: true, presentedElsewhere: false));
        Assert.False(ReaderInlineVideoRules.ShouldAutoplay(On(autoplay: true), shown: false, presentedElsewhere: false));
        Assert.False(ReaderInlineVideoRules.ShouldAutoplay(On(autoplay: true), shown: true, presentedElsewhere: true));
    }

    // ---- one at a time ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task StartingAVideoPausesThePreviousOneAndMakesItCurrent()
    {
        ReaderInlineVideoController controller = await Controller(On());
        var first = Video();
        var second = Video();
        Assert.True(await controller.PlayAsync(first.Owner, first.Create));
        Assert.True(first.Players[0].Started);
        Assert.Same(first.Owner, controller.Current);
        Assert.True(first.Owner.Current);
        Assert.True(await controller.PlayAsync(second.Owner, second.Create));
        Assert.False(first.Players[0].IsPlaying);
        Assert.True(second.Players[0].IsPlaying);
        Assert.Equal(1, controller.PlayingCount);
        Assert.Same(second.Owner, controller.Current);
        Assert.False(first.Owner.Current);
        Assert.Equal(new[] { true, false }, first.Owner.Announced);
        Assert.Equal(new[] { true }, second.Owner.Announced);
        // Continuing the first pauses the second again; the first keeps its player (no second one).
        Assert.True(await controller.PlayAsync(first.Owner, first.Create));
        Assert.Single(first.Players);
        Assert.Equal(1, first.Players[0].Plays);
        Assert.False(second.Players[0].IsPlaying);
        Assert.Equal(1, controller.PlayingCount);
    }

    [Fact]
    public async Task ThePlayersOwnPlayButtonFollowsTheSameRule()
    {
        ReaderInlineVideoController controller = await Controller(On());
        var first = Video();
        var second = Video();
        await controller.PlayAsync(first.Owner, first.Create);
        await controller.PlayAsync(second.Owner, second.Create);
        await first.Players[0].PlayAsync(); // the transport's play button
        controller.OnUserPlayback(first.Owner, playing: true);
        await controller.WhenReleasedAsync();
        Assert.False(second.Players[0].IsPlaying);
        Assert.Same(first.Owner, controller.Current);
        // A transport pause is only announced.
        await first.Players[0].PauseAsync();
        controller.OnUserPlayback(first.Owner, playing: false);
        Assert.False(first.Owner.Announced[^1]);
        Assert.Same(first.Owner, controller.Current);
    }

    [Fact]
    public async Task SpaceTogglesAndEnterPlays()
    {
        ReaderInlineVideoController controller = await Controller(On());
        var video = Video();
        await controller.ToggleAsync(video.Owner, video.Create); // a first Space plays
        Assert.True(controller.IsPlaying(video.Owner));
        await controller.ToggleAsync(video.Owner, video.Create);
        Assert.False(controller.IsPlaying(video.Owner));
        await controller.ToggleAsync(video.Owner, video.Create);
        Assert.True(controller.IsPlaying(video.Owner));
        Assert.Single(video.Players);

        Assert.Equal(ReaderInlineVideoRules.KeyAction.Play, ReaderInlineVideoRules.Classify(Key.Enter, KeyModifiers.None));
        Assert.Equal(ReaderInlineVideoRules.KeyAction.Toggle, ReaderInlineVideoRules.Classify(Key.Space, KeyModifiers.None));
        Assert.Equal(ReaderInlineVideoRules.KeyAction.None, ReaderInlineVideoRules.Classify(Key.Space, KeyModifiers.Shift));
        Assert.Equal(ReaderInlineVideoRules.KeyAction.None, ReaderInlineVideoRules.Classify(Key.Enter, KeyModifiers.Control));
        Assert.Equal(ReaderInlineVideoRules.KeyAction.None, ReaderInlineVideoRules.Classify(Key.Escape, KeyModifiers.None));
        Assert.Equal(ReaderInlineVideoRules.KeyAction.None, ReaderInlineVideoRules.Classify(Key.Down, KeyModifiers.None));
    }

    // ---- pause and release on the reader's events --------------------------------------------------------------------------------

    [Theory]
    [InlineData(true, true, false, "Keep")]
    [InlineData(true, false, false, "Keep")]
    [InlineData(false, false, false, "Pause")]
    [InlineData(true, true, true, "Keep")]
    [InlineData(true, false, true, "Release")]
    [InlineData(false, false, true, "Release")]
    public void ScrollingOutPausesAndANativeSurfaceIsReleasedOnceItIsNotWhollyInside(bool intersects, bool fully, bool native,
        string expected) =>
        Assert.Equal(expected, ReaderInlineVideoRules.OnViewport(intersects, fully, native).ToString());

    [Fact]
    public async Task ScrollOutPausesButKeepsThePlayerToContinue()
    {
        ReaderInlineVideoController controller = await Controller(On());
        var video = Video();
        await controller.PlayAsync(video.Owner, video.Create);
        await controller.PauseAsync(video.Owner); // the slot's Pause action of the viewport rule
        Assert.False(video.Players[0].IsPlaying);
        Assert.True(controller.IsLive(video.Owner));
        Assert.False(video.Players[0].Disposed);
        await controller.PlayAsync(video.Owner, video.Create);
        Assert.Single(video.Players);
        Assert.True(video.Players[0].IsPlaying);
    }

    [Fact]
    public async Task CoveringOrMinimisingTheReaderPausesEveryVideoAndKeepsThem()
    {
        ReaderInlineVideoController controller = await Controller(On());
        var first = Video();
        var second = Video();
        await controller.PlayAsync(first.Owner, first.Create);
        await controller.PlayAsync(second.Owner, second.Create);
        await controller.PauseAllAsync();
        Assert.Equal(0, controller.PlayingCount);
        Assert.Equal(2, controller.LiveCount);
        Assert.All(first.Players.Concat(second.Players), player => Assert.False(player.Disposed));
    }

    [Fact]
    public async Task ClosingTheReaderOrSwitchingThreadsReleasesAndDisposesEveryPlayer()
    {
        ReaderInlineVideoController controller = await Controller(On());
        var first = Video();
        var second = Video();
        await controller.PlayAsync(first.Owner, first.Create);
        await controller.PlayAsync(second.Owner, second.Create);
        await controller.ReleaseAllAsync();
        Assert.Equal(0, controller.LiveCount);
        Assert.Null(controller.Current);
        Assert.True(first.Players[0].Disposed);
        Assert.True(second.Players[0].Disposed);
        Assert.Equal(1, first.Owner.Releases);
        Assert.Equal(1, second.Owner.Releases);
        Assert.Equal(controller.Created, controller.Released);
        // The owners are no longer reachable from the controller: playing again creates a new player.
        await controller.PlayAsync(first.Owner, first.Create);
        Assert.Equal(2, first.Players.Count);
    }

    [Fact]
    public async Task ClosingTheSidebarOrChangingItsMediumReleasesOnlyThatPlayer()
    {
        ReaderInlineVideoController controller = await Controller(On());
        var post = Video();
        var sidebar = Video();
        await controller.PlayAsync(post.Owner, post.Create);
        await controller.PlayAsync(sidebar.Owner, sidebar.Create);
        await controller.ReleaseAsync(sidebar.Owner);
        Assert.True(sidebar.Players[0].Disposed);
        Assert.Equal(1, sidebar.Owner.Releases);
        Assert.False(post.Players[0].Disposed);
        Assert.Null(controller.Current); // the released one was current; the paused post does not start by itself
        Assert.Equal(0, controller.PlayingCount);
        await controller.ReleaseAsync(sidebar.Owner); // twice is harmless
        Assert.Equal(1, sidebar.Owner.Releases);
    }

    [Fact]
    public async Task TurningTheSettingOffReleasesEverything()
    {
        ReaderInlineVideoController controller = await Controller(On());
        var video = Video();
        await controller.PlayAsync(video.Owner, video.Create);
        await controller.ConfigureAsync(ReaderInlineVideoOptions.Default);
        Assert.Equal(0, controller.LiveCount);
        Assert.True(video.Players[0].Disposed);
        Assert.False(await controller.PlayAsync(video.Owner, video.Create));
        Assert.Single(video.Players);
    }

    // ---- the bound and the disposal ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task NeverMoreLivePlayersThanTheBoundAndTheLeastRecentlyUsedGoesFirst(int bound)
    {
        ReaderInlineVideoController controller = await Controller(On(bound));
        var videos = Enumerable.Range(0, 7).Select(_ => Video()).ToArray();
        int maximum = 0;
        foreach (var video in videos)
        {
            await controller.PlayAsync(video.Owner, () =>
            {
                // The room is made before the new player exists: the bound holds at every moment.
                maximum = Math.Max(maximum, controller.LiveCount + 1);
                return video.Create();
            });
            maximum = Math.Max(maximum, controller.LiveCount);
        }
        Assert.Equal(bound, maximum);
        Assert.Equal(bound, controller.LiveCount);
        Assert.Equal(7, controller.Created);
        Assert.Equal(7 - bound, controller.Released);
        // The oldest were released and disposed, the newest are live.
        for (int index = 0; index < 7; index++)
        {
            bool live = index >= 7 - bound;
            Assert.Equal(!live, videos[index].Players[0].Disposed);
            Assert.Equal(live ? 0 : 1, videos[index].Owner.Releases);
        }
    }

    [Fact]
    public async Task ContinuingAnOlderVideoKeepsItWhenRoomIsNeeded()
    {
        ReaderInlineVideoController controller = await Controller(On(2));
        var a = Video();
        var b = Video();
        var c = Video();
        await controller.PlayAsync(a.Owner, a.Create);
        await controller.PlayAsync(b.Owner, b.Create);
        await controller.PlayAsync(a.Owner, a.Create); // a is used again: b is now the least recently used
        await controller.PlayAsync(c.Owner, c.Create);
        Assert.False(a.Players[0].Disposed);
        Assert.True(b.Players[0].Disposed);
        Assert.False(c.Players[0].Disposed);
    }

    [Fact]
    public async Task LoweringTheBoundReleasesTheExtraPlayers()
    {
        ReaderInlineVideoController controller = await Controller(On(4));
        var videos = Enumerable.Range(0, 4).Select(_ => Video()).ToArray();
        foreach (var video in videos) await controller.PlayAsync(video.Owner, video.Create);
        await controller.ConfigureAsync(On(1));
        Assert.Equal(1, controller.LiveCount);
        Assert.False(videos[3].Players[0].Disposed);
        Assert.All(videos.Take(3), video => Assert.True(video.Players[0].Disposed));
    }

    private sealed class SlowPlayer : IReaderInlineVideoPlayer
    {
        internal readonly TaskCompletionSource Disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsPlaying { get; private set; }
        public Task StartAsync() { IsPlaying = true; return Task.CompletedTask; }
        public Task PlayAsync() { IsPlaying = true; return Task.CompletedTask; }
        public Task PauseAsync() { IsPlaying = false; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => new(Disposal.Task);
    }

    [Fact]
    public async Task ASlowNativeCleanupIsAwaitedNotForgotten()
    {
        ReaderInlineVideoController controller = await Controller(On());
        SlowPlayer player = new();
        FakeOwner owner = new();
        await controller.PlayAsync(owner, () => player);
        Task released = controller.ReleaseAllAsync();
        Assert.Equal(0, controller.LiveCount); // the bookkeeping is immediate
        Assert.False(released.IsCompleted); // the disposal is still running
        Assert.False(controller.WhenReleasedAsync().IsCompleted);
        player.Disposal.SetResult();
        await released;
        await controller.WhenReleasedAsync();
    }

    private sealed class FailingDisposal : IReaderInlineVideoPlayer
    {
        public bool IsPlaying => false;
        public Task StartAsync() => Task.CompletedTask;
        public Task PlayAsync() => Task.CompletedTask;
        public Task PauseAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.FromException(new InvalidOperationException("native cleanup failed"));
    }

    [Fact]
    public async Task AFailedDisposalDoesNotBreakTheController()
    {
        ReaderInlineVideoController controller = await Controller(On(1));
        FakeOwner failing = new();
        await controller.PlayAsync(failing, () => new FailingDisposal());
        var next = Video();
        Assert.True(await controller.PlayAsync(next.Owner, next.Create)); // evicts the failing one
        await controller.WhenReleasedAsync();
        Assert.Equal(1, failing.Releases);
        Assert.Equal(1, controller.LiveCount);
    }

    // ---- texts, keys and work views ------------------------------------------------------------------------------------------------

    [Fact]
    public void TheActivatorAndTheAnnouncementsNameTheVideo()
    {
        Assert.Equal("Play video here: clip.webm", Texts.T("reader.video.play", "clip.webm"));
        Assert.Equal("Playing: clip.webm", Texts.T("reader.video.playing", "clip.webm"));
        Assert.Equal("Paused: clip.webm", Texts.T("reader.video.paused", "clip.webm"));
        Texts.SetLanguage("de");
        Assert.Equal("Video hier abspielen: clip.webm", Texts.T("reader.video.play", "clip.webm"));
        Assert.Equal("Angehalten: clip.webm", Texts.T("reader.video.paused", "clip.webm"));
    }

    [Fact]
    public void TheSlotKeyIsTheAttachmentNotTheRow()
    {
        CapturedAttachment attachment = new(1, "m", OriginalFileName: "a.webm");
        Assert.Equal(ReaderInlineVideoSlot.KeyOf(attachment), ReaderInlineVideoSlot.KeyOf(attachment with { }));
        Assert.NotEqual(ReaderInlineVideoSlot.KeyOf(attachment), ReaderInlineVideoSlot.KeyOf(attachment with { Position = 2 }));
        Assert.NotEqual(ReaderInlineVideoSlot.KeyOf(attachment), ReaderInlineVideoSlot.KeyOf(attachment with { MediaId = "n" }));
    }

    // WV3: the inline video is post content, not a tool. No reader work-view row can reduce or hide it or its error line, in any view.
    [Fact]
    public void NoWorkViewRowCanReduceTheInlineVideoOrItsErrors()
    {
        Assert.DoesNotContain(WorkViewPolicy.Entries, entry => entry.Id.Contains("video", StringComparison.Ordinal) && entry.Page == WorkViewPage.Reader);
        // The reader's errors stay Required in every view (the owner default the inline error line follows).
        Assert.All(WorkViewSetting.All, view => Assert.Equal(Presence.Required, WorkViewPolicy.Resolve("reader.media.rating-error", view)));
    }
}
