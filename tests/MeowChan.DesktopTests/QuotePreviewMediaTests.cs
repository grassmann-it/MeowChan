using System.Text.Json;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// Tom (16.11.2026, screenshot of the large reader, No. 2627830 quoting >>2627145 >>2627144 …): "if a post has media and someone replies to it,
// hovering the reference shows the medium LARGE as a hover preview; videos with autoplay (always muted?)". The quote preview (one at a time,
// the existing I1 preview) now carries the quoted post's first medium: sized to the reader and the window, decoded at that size, a GIF
// animating and a video playing muted and repeating in a player of its own that is released when the preview closes. It stays open while the
// pointer is on the reference or on the preview. The rules, the hover state and the medium are tested without a window; the page's wiring
// against its source, as the other reader packages do.
[Collection("Localization")]
public sealed class QuotePreviewMediaTests : IDisposable
{
    public QuotePreviewMediaTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    // ---- the hover life: delayed open, grace on the way to the preview, closing -----------------------------------------------------

    [Fact]
    public void APointerRequestWaitsForTheDelayAndLeavingBeforeItCancels()
    {
        QuotePreviewHoverState state = new();
        state.Request(pointer: true, immediate: false);
        Assert.True(state.Pending);
        Assert.False(state.IsOpen);
        // Leaving the reference while the preview still waits: nothing opens, whatever the direction.
        Assert.Equal(QuotePreviewLeave.CloseNow, state.LeaveLink(focused: false, towardPreview: true));
        Assert.False(state.Pending);
        Assert.False(state.DelayElapsed());
        Assert.False(state.IsOpen);
        // Resting long enough opens it.
        state.Request(pointer: true, immediate: false);
        Assert.True(state.DelayElapsed());
        Assert.True(state.IsOpen);
        Assert.False(state.Pending);
        Assert.False(state.DelayElapsed()); // a second tick opens nothing twice
    }

    [Fact]
    public void AnOpenPreviewStaysWhileThePointerCrossesToItAndIsOverIt()
    {
        QuotePreviewHoverState state = new();
        state.Request(pointer: true, immediate: false);
        state.DelayElapsed();
        // Leaving the reference toward the preview: a grace, not a close.
        Assert.Equal(QuotePreviewLeave.CloseAfterGrace, state.LeaveLink(focused: false, towardPreview: true));
        Assert.True(state.GracePending);
        // The pointer arrives on the preview within the grace: the grace passing closes nothing.
        state.EnterPreview();
        Assert.False(state.GracePending);
        Assert.False(state.GraceElapsed());
        Assert.True(state.IsOpen);
        // Leaving the preview back to the reference keeps it too.
        Assert.Equal(QuotePreviewLeave.CloseAfterGrace, state.LeavePreview(focused: false));
        state.EnterLink();
        Assert.False(state.GraceElapsed());
        Assert.True(state.IsOpen);
        // Across to the preview once more, then away from it: it closes once the grace passed.
        Assert.Equal(QuotePreviewLeave.CloseAfterGrace, state.LeaveLink(focused: false, towardPreview: true));
        state.EnterPreview();
        Assert.Equal(QuotePreviewLeave.CloseAfterGrace, state.LeavePreview(focused: false));
        Assert.True(state.GraceElapsed());
        Assert.False(state.IsOpen);
        Assert.False(state.GraceElapsed()); // closed once
    }

    [Fact]
    public void LeavingTheReferenceAwayFromThePreviewClosesAtOnce()
    {
        // As before the medium came in (and the smoke's synthetic leave, which has no pointer position): no grace.
        QuotePreviewHoverState state = new();
        state.Request(pointer: true, immediate: false);
        state.DelayElapsed();
        Assert.Equal(QuotePreviewLeave.CloseNow, state.LeaveLink(focused: false, towardPreview: false));
        Assert.False(state.IsOpen);
        Assert.False(state.GracePending);
    }

    [Fact]
    public void ThePreviewOfTheFocusedReferenceStaysWhenThePointerLeaves()
    {
        QuotePreviewHoverState state = new();
        state.Request(pointer: false, immediate: true);
        Assert.True(state.IsOpen);
        Assert.Equal(QuotePreviewLeave.Keep, state.LeaveLink(focused: true, towardPreview: false));
        state.EnterPreview();
        Assert.Equal(QuotePreviewLeave.Keep, state.LeavePreview(focused: true));
        Assert.True(state.IsOpen);
        state.Reset();
        Assert.False(state.IsOpen || state.Pending || state.OverLink || state.OverPreview || state.GracePending);
    }

    [Fact]
    public void ThePointerIsOnItsWayOnlyBetweenTheReferenceAndThePreview()
    {
        PixelRect anchor = new(100, 100, 40, 18), preview = new(90, 120, 600, 400);
        Assert.True(QuotePreviewMediaRules.InCorridor(new PixelPoint(120, 119), anchor, preview));   // the gap below the reference
        Assert.True(QuotePreviewMediaRules.InCorridor(new PixelPoint(300, 300), anchor, preview));   // over the preview
        Assert.True(QuotePreviewMediaRules.InCorridor(new PixelPoint(85, 110), anchor, preview, 12)); // a few pixels beside it
        Assert.False(QuotePreviewMediaRules.InCorridor(new PixelPoint(120, 60), anchor, preview));   // up, away from it
        Assert.False(QuotePreviewMediaRules.InCorridor(new PixelPoint(900, 110), anchor, preview));  // far to the right
        Assert.False(QuotePreviewMediaRules.InCorridor(null, anchor, preview));                       // no pointer position known
        Assert.False(QuotePreviewMediaRules.InCorridor(new PixelPoint(120, 119), anchor, default));   // no preview on screen
    }

    // ---- the size: large, never off-screen, never enlarged, decoded at the shown size -------------------------------------------------

    private static readonly Size Chrome = new(26, 22);

    [Fact]
    public void TheMediumTakesUpToSixtyPercentOfTheReaderAndTheSideWithMoreRoom()
    {
        Size window = new(1920, 1080), reader = new(1400, 1000);
        // A reference near the top: below, 60 % of the reader's width and height (the room below is larger).
        QuotePreviewMediaBox below = QuotePreviewMediaRules.Box(reader, window, new Rect(400, 100, 60, 18), 80, Chrome);
        Assert.True(below.Below);
        Assert.Equal(new Size(840, 600), below.Box);
        // A reference near the bottom: above, as high as the room above minus the text and the chrome.
        QuotePreviewMediaBox above = QuotePreviewMediaRules.Box(reader, window, new Rect(400, 900, 60, 18), 80, Chrome);
        Assert.False(above.Below);
        Assert.Equal(600, above.Box.Height);
        QuotePreviewMediaBox tight = QuotePreviewMediaRules.Box(reader, window, new Rect(400, 520, 60, 18), 80, Chrome);
        Assert.Equal(Math.Floor(1080 - 538 - QuotePreviewMediaRules.WindowMargin - 80 - Chrome.Height), tight.Box.Height); // never past the window edge
        // No room at all: the preview shows its text only.
        Assert.False(QuotePreviewMediaRules.Box(new Size(300, 200), new Size(300, 200), new Rect(10, 90, 40, 18), 80, Chrome).HasRoom);
        // Before the reader was laid out the window stands in.
        Assert.True(QuotePreviewMediaRules.Box(default, window, new Rect(400, 100, 60, 18), 80, Chrome).HasRoom);
    }

    [Fact]
    public void APictureKeepsItsAspectAndIsNeverEnlargedBeyondItsPixels()
    {
        Size box = new(840, 600);
        Assert.Equal(new Size(400, 300), QuotePreviewMediaRules.Fit(400, 300, box));     // small: its own size
        Assert.Equal(new Size(800, 600), QuotePreviewMediaRules.Fit(1600, 1200, box));   // large: fitted, aspect kept
        Assert.Equal(new Size(375, 600), QuotePreviewMediaRules.Fit(1000, 1600, box));   // portrait: height-limited
        Assert.Equal(default, QuotePreviewMediaRules.Fit(0, 100, box));
        // The frame before the picture arrived: the provider's size, else 4:3 at the box's width.
        Assert.Equal(new Size(800, 600), QuotePreviewMediaRules.Provisional(new CapturedAttachment(0, "m") { Width = 1600, Height = 1200 }, box));
        Assert.Equal(new Size(800, 600), QuotePreviewMediaRules.Provisional(new CapturedAttachment(0, "m"), box));
        // The decode is the box at the render scaling (physical pixels), never more.
        Assert.Equal((1680, 1200), QuotePreviewMediaRules.DecodeBounds(box, 2));
        Assert.Equal((840, 600), QuotePreviewMediaRules.DecodeBounds(box, double.NaN));
    }

    [Fact]
    public void TheFirstMediumIsTheFirstFileThatIsNotDeletedAndSeveralAreCounted()
    {
        Assert.Null(QuotePreviewMediaRules.FirstMedium(null));
        Assert.Null(QuotePreviewMediaRules.FirstMedium(Array.Empty<CapturedAttachment>()));
        CapturedAttachment gone = new(0, "a", OriginalFileName: "a.png", IsDeleted: true), kept = new(1, "b", OriginalFileName: "b.webm");
        Assert.Same(kept, QuotePreviewMediaRules.FirstMedium(new[] { gone, kept }));
        Assert.Null(QuotePreviewMediaRules.FirstMedium(new[] { gone }));
        Assert.Equal(QuotePreviewMediaKind.Video, QuotePreviewMediaRules.Classify(kept));
        Assert.Equal(QuotePreviewMediaKind.Gif, QuotePreviewMediaRules.Classify(new CapturedAttachment(0, "c", OriginalFileName: "c.GIF")));
        Assert.Equal(QuotePreviewMediaKind.Image, QuotePreviewMediaRules.Classify(new CapturedAttachment(0, "d", OriginalFileName: "d.jpg")));
        Assert.Equal(string.Empty, QuotePreviewMediaRules.CountText(1));
        Assert.Equal("1 of 3", QuotePreviewMediaRules.CountText(3));
    }

    [Fact]
    public void ThePreviewsPlayerRepeatsIsMutedByDefaultAndPlaysWhateverTheClickChoiceIs()
    {
        // "Open in the gallery" switches the reader's inline players off; the preview is no click and keeps its own one player.
        ReaderInlineVideoOptions reader = ReaderInlineVideoOptions.Default with { Volume = 35, MaxFileMiB = 64 };
        ReaderInlineVideoOptions preview = QuotePreviewMediaRules.VideoOptions(reader, muted: true);
        Assert.True(preview.Enabled);
        Assert.False(preview.InPost);
        Assert.True(preview.Repeat);
        Assert.True(preview.StartMuted);
        Assert.Equal(1, preview.MaxPlayers);
        Assert.Equal(35, preview.Volume);
        Assert.Equal(64, preview.MaxFileMiB);
        Assert.False(QuotePreviewMediaRules.VideoOptions(reader, muted: false).StartMuted);
    }

    // ---- the medium in the preview (a real view on a dispatcher of its own) ----------------------------------------------------------

    private static void OnOwnDispatcher(Action test) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
        test();
    });

    private static WriteableBitmap Picture(int width, int height) => new(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

    private static ArchiveAttachmentRow Row(string name, Func<int, int, CancellationToken, Task<Bitmap?>> load, Func<CancellationToken, Task<MeowChan.Gallery.GalleryEntry?>>? resolve = null) =>
        new(new CapturedAttachment(0, "m-" + name, OriginalFileName: name) { Width = 400, Height = 300 }) { LoadSized = load, ResolveEntry = resolve };

    private sealed class FakePlayer : IReaderInlineVideoPlayer
    {
        internal bool Disposed;
        public bool IsPlaying { get; private set; }
        public Task StartAsync() { IsPlaying = true; return Task.CompletedTask; }
        public Task PlayAsync() { IsPlaying = true; return Task.CompletedTask; }
        public Task PauseAsync() { IsPlaying = false; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Disposed = true; IsPlaying = false; return ValueTask.CompletedTask; }
    }

    [Fact]
    public void AQuotedPostWithMediaShowsItsMediumLargeWithTheCountAndGivesItBackOnClose() => OnOwnDispatcher(() =>
    {
        QuotePreviewMedia media = new();
        WriteableBitmap picture = Picture(400, 300);
        (int Width, int Height) asked = default;
        List<Size> sized = new();
        media.FrameSized = sized.Add;
        media.Show(Row("cat.png", (width, height, _) => { asked = (width, height); return Task.FromResult<Bitmap?>(picture); }), count: 3,
            new Size(840, 600), scaling: 1.5, ReaderInlineVideoOptions.Default);
        Dispatcher.UIThread.RunJobs();
        Assert.True(media.LoadTask.IsCompletedSuccessfully);
        // Decoded at the shown size (the box at the render scaling), shown at its own size (never enlarged), with "1 of 3".
        Assert.Equal((1260, 900), asked);
        Assert.True(media.ShowsMedium);
        Assert.Same(picture, media.Shown);
        Assert.Same(picture, media.Picture.Source);
        Assert.Equal(400, media.Frame.Width);
        Assert.Equal(300, media.Frame.Height);
        Assert.Equal(new Size(400, 300), sized[^1]);
        Assert.Equal("1 of 3", media.CountLabel);
        Assert.Equal(string.Empty, media.NoteText);
        Assert.Equal(QuotePreviewMediaKind.Image, media.Kind);
        // Closing gives the picture back: nothing stays decoded.
        media.Clear();
        Assert.False(media.ShowsMedium);
        Assert.Null(media.Shown);
        Assert.Null(media.Picture.Source);
        Assert.Equal(string.Empty, media.CountLabel);
    });

    [Fact]
    public void AMediumThatIsNotSavedLeavesTheFrameOutWithAShortNote() => OnOwnDispatcher(() =>
    {
        QuotePreviewMedia media = new();
        string notSaved = Texts.T("archive.notSaved");
        media.Show(Row("gone.png", (_, _, _) => Task.FromException<Bitmap?>(new InvalidDataException(notSaved))), count: 1,
            new Size(840, 600), 1, ReaderInlineVideoOptions.Default);
        Dispatcher.UIThread.RunJobs();
        Assert.False(media.ShowsMedium);
        Assert.Null(media.Shown);
        Assert.Equal(notSaved, media.NoteText);
        // No saved bytes at all (the load answers nothing): the same note.
        media.Show(Row("none.png", (_, _, _) => Task.FromResult<Bitmap?>(null)), count: 1, new Size(840, 600), 1, ReaderInlineVideoOptions.Default);
        Dispatcher.UIThread.RunJobs();
        Assert.False(media.ShowsMedium);
        Assert.Equal(notSaved, media.NoteText);
        // Every file of the post deleted: the page shows the note without a load.
        media.ShowNote(notSaved);
        Assert.False(media.ShowsMedium);
        Assert.Equal(notSaved, media.NoteText);
        media.Clear();
        Assert.Equal(string.Empty, media.NoteText);
    });

    [Fact]
    public void ALoadThatAnswersAfterTheCloseIsGivenBackAndNotShown() => OnOwnDispatcher(() =>
    {
        QuotePreviewMedia media = new();
        TaskCompletionSource<Bitmap?> late = new();
        CancellationToken seen = default;
        media.Show(Row("slow.png", (_, _, token) => { seen = token; return late.Task; }), count: 1, new Size(840, 600), 1, ReaderInlineVideoOptions.Default);
        media.Clear();
        Assert.True(seen.IsCancellationRequested); // the decode is cancelled with the preview
        late.SetResult(Picture(40, 30));
        Dispatcher.UIThread.RunJobs();
        Assert.Null(media.Shown);
        Assert.Null(media.Picture.Source);
        Assert.False(media.ShowsMedium);
    });

    [Fact]
    public void WithAutoplayOffAVideoKeepsItsStillPictureAndNoPlayerIsMade() => OnOwnDispatcher(() =>
    {
        QuotePreviewMedia media = new() { Autoplay = false };
        bool resolved = false;
        media.Show(Row("clip.webm", (_, _, _) => Task.FromResult<Bitmap?>(Picture(400, 300)),
            _ => { resolved = true; return Task.FromResult<MeowChan.Gallery.GalleryEntry?>(null); }), count: 1, new Size(840, 600), 1, ReaderInlineVideoOptions.Default);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(QuotePreviewMediaKind.Video, media.Kind);
        Assert.NotNull(media.Shown); // the poster
        Assert.False(resolved);
        Assert.Equal(0, media.Videos.Created);
        media.Clear();
    });

    [Fact]
    public void TheVideoStartsMutedAndRepeatingInOnePlayerAndIsReleasedWhenThePreviewCloses() => OnOwnDispatcher(() =>
    {
        QuotePreviewMedia media = new();
        Assert.True(media.Autoplay);
        Assert.True(media.Muted);
        FakePlayer player = new();
        ReaderInlineVideoOptions made = default;
        bool? mutedAtStart = null;
        Task<bool> playing = media.PlayVideoAsync((options, muted) => { made = options; mutedAtStart = muted; return player; },
            ReaderInlineVideoOptions.Default with { Volume = 50 });
        Dispatcher.UIThread.RunJobs();
        Assert.True(playing.IsCompletedSuccessfully && playing.Result);
        Assert.True(mutedAtStart);
        Assert.True(made.Repeat);
        Assert.True(made.StartMuted);
        Assert.Equal(1, made.MaxPlayers);
        Assert.Equal(50, made.Volume);
        Assert.True(player.IsPlaying);
        Assert.Equal(1, media.Videos.LiveCount);
        Assert.Equal(1, media.Videos.PlayingCount);
        // Closing the preview releases the player: none is left running.
        media.Clear();
        Dispatcher.UIThread.RunJobs();
        Assert.True(player.Disposed);
        Assert.Equal(0, media.Videos.LiveCount);
        Assert.Equal(0, media.Videos.PlayingCount);
        Assert.Equal(1, media.Videos.Released);
        Assert.True(media.Videos.WhenReleasedAsync().IsCompleted);
        // A second preview makes a new player and the bound of one holds.
        FakePlayer second = new();
        Assert.True(media.PlayVideoAsync((_, _) => second, ReaderInlineVideoOptions.Default).Result);
        FakePlayer third = new();
        media.Clear();
        Assert.True(media.PlayVideoAsync((_, _) => third, ReaderInlineVideoOptions.Default).Result);
        Assert.True(second.Disposed);
        Assert.Equal(1, media.Videos.LiveCount);
        media.Clear();
        Assert.True(third.Disposed);
    });

    [Fact]
    public void WithMuteOffTheVideoStartsWithSound() => OnOwnDispatcher(() =>
    {
        QuotePreviewMedia media = new() { Muted = false };
        bool? mutedAtStart = null;
        Assert.True(media.PlayVideoAsync((_, muted) => { mutedAtStart = muted; return new FakePlayer(); }, ReaderInlineVideoOptions.Default).Result);
        Assert.False(mutedAtStart);
        media.Clear();
    });

    // ---- the page's wiring --------------------------------------------------------------------------------------------------------

    private static string RepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory != null && !File.Exists(Path.Combine(directory, "MeowChan.sln"))) directory = Path.GetDirectoryName(directory);
        return directory ?? throw new DirectoryNotFoundException("The repository root was not found.");
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(RepositoryRoot(), relative)).Replace("\r\n", "\n");

    [Fact]
    public void APostWithoutMediaOrWithHiddenMediaPreviewsItsTextOnly()
    {
        string preview = Read("Desktop/Views/Archive/ArchivePage.QuotePreview.cs");
        Assert.Contains("ShowQuotePreviewMedia(post, target);", preview);
        // The frame is hidden first; only a post with files whose media the view shows (no "Hide media", no concealing filter) brings it back.
        Assert.Contains("host.IsVisible = false;\n        if (_quotePreviewMedia is not { } media) return;\n        media.Clear();", preview);
        Assert.Contains("if (attachments.Count == 0 || _thread == null || _readerView.HideMedia || PostFilterConceals(post)) return;", preview);
        Assert.Contains("card.MaxWidth = QuotePreviewMediaRules.TextOnlyMaxWidth;", preview);
        Assert.Contains("card.MaxHeight = QuotePreviewMediaRules.TextOnlyMaxHeight;", preview);
    }

    [Fact]
    public void EveryReferencePreviewsAndThePreviewTakesThePointer()
    {
        string xaml = Read("Desktop/Views/Archive/ArchivePage.axaml");
        // The reply cards and the References tab's rows preview on hover like the quote buttons.
        Assert.Contains("Classes=\"reader-reply-card\"", xaml);
        Assert.Contains("Click=\"OnQuoteClick\" PointerEntered=\"OnQuoteButtonEntered\" PointerExited=\"OnQuoteButtonExited\"\n", xaml);
        Assert.Contains("<Button Classes=\"reader-reference\" Content=\"{Binding Label}\"", xaml);
        // One popup for both tabs, above the tab control; it is hit-testable (it stays open while the pointer is on it).
        int tabs = xaml.IndexOf("</TabControl>", StringComparison.Ordinal), popup = xaml.IndexOf("<Popup x:Name=\"QuotePreviewPopup\"", StringComparison.Ordinal);
        Assert.True(popup > tabs, "The quote preview must be outside the Posts tab so the References tab can open it.");
        Assert.Contains("<Border x:Name=\"QuotePreviewCard\"", xaml);
        Assert.Contains("<Panel x:Name=\"QuotePreviewMediaHost\" IsVisible=\"False\"", xaml);
        Assert.DoesNotContain("<Border IsHitTestVisible=\"False\" Background=\"{DynamicResource Workspace.Card}\" BorderBrush=\"{DynamicResource Workspace.Accent}\"", xaml);
        string preview = Read("Desktop/Views/Archive/ArchivePage.QuotePreview.cs");
        Assert.Contains("foreach (ListBox list in new[] { ArchivePosts, ArchiveReferences })", preview);
        Assert.Contains("button.Classes.Contains(\"reader-reply-card\") || button.Classes.Contains(\"reader-reference\")", preview);
    }

    [Fact]
    public void ClosingThePreviewAndReleasingTheReadersVideosLeaveNoPlayerRunning()
    {
        string preview = Read("Desktop/Views/Archive/ArchivePage.QuotePreview.cs");
        Assert.Contains("HideQuoteHighlight();\n        _quotePreviewMedia?.Clear();\n        if (QuotePreviewPopup.IsOpen) QuotePreviewPopup.IsOpen = false;", preview);
        string video = Read("Desktop/Views/Archive/ArchivePage.InlineVideo.cs");
        // The reader closing, moving, switching threads, deleting media or ending; the gallery over it or a minimised window.
        Assert.Contains("CloseQuotePreview(); // its own player and GIF go with it", video);
        Assert.Contains("Task preview = _quotePreviewMedia?.Videos.WhenReleasedAsync() ?? Task.CompletedTask;", video);
        Assert.Contains("CloseQuotePreview(); // a preview never plays under the gallery or in a minimised window", video);
    }

    // ---- the settings: "Play videos and GIFs in quote previews" and "Mute videos in quote previews", both on ----------------------------

    [Fact]
    public void BothSwitchesAreOnByDefaultAlsoForOldOrBrokenProfilesAndTravelWithTheSettings()
    {
        Config config = new();
        Assert.True(config.ReaderQuotePreviewAutoplay);
        Assert.True(config.ReaderQuotePreviewMuted);
        Config old = JsonSerializer.Deserialize<Config>("{}")!;
        Assert.True(old.ReaderQuotePreviewAutoplay && old.ReaderQuotePreviewMuted);
        Config broken = JsonSerializer.Deserialize<Config>("{\"ReaderQuotePreviewAutoplay\":\"x\",\"ReaderQuotePreviewMuted\":null}")!;
        Assert.True(broken.ReaderQuotePreviewAutoplay && broken.ReaderQuotePreviewMuted);
        Config saved = JsonSerializer.Deserialize<Config>("{\"ReaderQuotePreviewAutoplay\":false,\"ReaderQuotePreviewMuted\":false}")!;
        Assert.False(saved.ReaderQuotePreviewAutoplay);
        Assert.False(saved.ReaderQuotePreviewMuted);
        Config source = new() { ReaderQuotePreviewAutoplay = false, ReaderQuotePreviewMuted = false };
        Config clone = source.Clone(), copy = new(), settings = new();
        copy.CopyFrom(source);
        settings.CopySettingsFieldsFrom(source);
        foreach (Config current in new[] { clone, copy, settings, SettingsTransfer.CreatePortableSnapshot(source) })
        {
            Assert.False(current.ReaderQuotePreviewAutoplay);
            Assert.False(current.ReaderQuotePreviewMuted);
        }
        Assert.False(SettingsTransfer.Export(source, "1.1.0")[nameof(Config.ReaderQuotePreviewMuted)]!.GetValue<bool>());
        string apply = Read("Desktop/ReaderSettingsPreferences.cs");
        Assert.Contains("reader.QuotePreviewVideoAutoplay = config.ReaderQuotePreviewAutoplay;", apply);
        Assert.Contains("reader.QuotePreviewVideoMuted = config.ReaderQuotePreviewMuted;", apply);
    }

    [Fact]
    public void TheNewTextsExistInAllTwelveLanguages()
    {
        string[] keys =
        {
            "settings.reader.quotePreviewAutoplay", "settings.reader.quotePreviewAutoplay.hint",
            "settings.reader.quotePreviewMuted", "settings.reader.quotePreviewMuted.hint"
        };
        foreach (string key in keys)
        {
            Assert.False(string.IsNullOrWhiteSpace(BuiltInCatalog.English[key]), key);
            Assert.False(string.IsNullOrWhiteSpace(BuiltInCatalog.German[key]), key);
        }
        string packs = Path.Combine(RepositoryRoot(), "Desktop/Localization/Packs");
        string[] files = Directory.GetFiles(packs, "*.json");
        Assert.Equal(10, files.Length);
        foreach (string file in files)
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
            JsonElement strings = doc.RootElement.GetProperty("strings");
            foreach (string key in keys) Assert.True(strings.TryGetProperty(key, out JsonElement value) && !string.IsNullOrWhiteSpace(value.GetString()), $"{Path.GetFileName(file)} {key}");
        }
        string editor = Read("Desktop/Views/Settings/SettingsEditor.axaml");
        Assert.Contains("x:Name=\"ReaderQuotePreviewAutoplayInput\"", editor);
        Assert.Contains("x:Name=\"ReaderQuotePreviewMutedInput\"", editor);
        string aliases = Read("Desktop/Views/Settings/SettingsSearchAliases.cs");
        Assert.Contains("[\"ReaderQuotePreviewAutoplay\"] =", aliases);
        Assert.Contains("[\"ReaderQuotePreviewMuted\"] =", aliases);
    }
}
