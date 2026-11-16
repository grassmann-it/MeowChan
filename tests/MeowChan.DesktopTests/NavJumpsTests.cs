using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Platform;
using Avalonia.Threading;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;

namespace MeowChan.DesktopTests;

/// <summary>
/// SWEEP S7 (16.11.2026), the jumps into the archive's reader:
/// F1 "Show in thread" for a thread the archive does not hold says so and changes nothing (no empty large reader, the loaded thread kept);
/// F6 a post the archived copy does not hold opens the thread with one note; F2 a jump starts without the reader history of another thread;
/// F3 an open gallery owns Alt+Left; F5 the Tagging page's jump has its own way back, "Back to Tagging".
/// </summary>
[Collection("Gallery viewer construction")]
public sealed class NavJumpsTests
{
    private const string Origin = "synthetic-S7";
    private static readonly ResourceId Shown = new(Origin, "board-A", "100");
    private static readonly ResourceId Quoted = new(Origin, "board-A", "200");
    private static readonly ResourceId Jumped = new(Origin, "board-B", "300");
    private static readonly ResourceId Missing = new(Origin, "board-B", "404");

    private const string MediaReturnFile = "Desktop/Shell/MainWindow.MediaReturn.cs";
    private const string TaggingFile = "Desktop/Shell/MainWindow.TaggingPage.cs";
    private const string GalleryFile = "Desktop/Shell/MainWindow.Gallery.cs";

    // ---- F1: a thread the archive does not hold ----

    [Fact]
    public async Task TheArchiveSaysWhichThreadsItDoesNotHoldAndKeepsTheThreadItShows()
    {
        using TempDirectory temp = new();
        await SeedAsync(temp.Path);
        BooruComponentDispatcher.Run(() => WithReadingPage(temp.Path, page => Pump(async () =>
        {
            await page.LoadThreadAsync(Shown);
            Assert.Equal(Shown, page.CurrentThread?.Summary.Id);

            Assert.True(await page.ThreadMissingAsync(Missing));
            Assert.False(await page.ThreadMissingAsync(Jumped));
            Assert.False(await page.ThreadMissingAsync(Shown));
            // The check reads the catalog only: the reader still shows its thread, nothing was cleared or loaded.
            Assert.Equal(Shown, page.CurrentThread?.Summary.Id);
        })));
    }

    [Fact]
    public void APageThatCannotReadIsNoAnswer() => BooruComponentDispatcher.Run(() => WithPage(
        () => new MeowChan.ArchivePage(_ => Task.FromResult<ArchiveReader>(null!), (_, _) => Task.CompletedTask, _ => { }), page =>
        {
            page.IsReadable = false;
            Task<bool> missing = page.ThreadMissingAsync(Missing); // not readable: the jump goes on and its own load reports
            Assert.True(missing.IsCompletedSuccessfully);
            Assert.False(missing.Result);
        }));

    [Fact]
    public void EveryShowInThreadChecksTheThreadBeforeItLeavesThePageOrSuspendsTheGallery()
    {
        foreach ((string file, string member) in new[]
        {
            (MediaReturnFile, "private async Task ShowMediaLibraryEntryInThreadAsync"),
            (TaggingFile, "private async Task ShowTaggingEntryInThreadAsync")
        })
        {
            string jump = Body(file, member);
            int check = jump.IndexOf("await ArchiveHoldsJumpTargetAsync(", StringComparison.Ordinal);
            Assert.True(check >= 0, member + " must check the thread.");
            Assert.True(check < jump.IndexOf("NavigateWorkspace(WorkspacePage.Archive", StringComparison.Ordinal), member + ": check before navigating.");
            Assert.True(check < jump.IndexOf("_mediaReturn = ", StringComparison.Ordinal), member + ": check before the return point.");
            Assert.Contains("JumpStillRequested(request)", jump);
        }
        foreach (string member in new[] { "private async Task ReadGalleryPostAsync", "private async Task ReadEmptyGalleryPostsAsync" })
        {
            string read = Body(GalleryFile, member);
            int check = read.IndexOf("await ArchiveHoldsJumpTargetAsync(", StringComparison.Ordinal);
            Assert.True(check >= 0, member + " must check the thread.");
            Assert.True(check < read.IndexOf("NavigateWorkspace(WorkspacePage.Archive", StringComparison.Ordinal), member + ": check before navigating.");
            Assert.True(check < read.IndexOf("SuspendGallery()", StringComparison.Ordinal), member + ": check before suspending the gallery.");
        }
        string holds = Body(MediaReturnFile, "private async Task<bool> ArchiveHoldsJumpTargetAsync");
        Assert.Contains("ThreadMissingAsync(thread)", holds);
        Assert.Contains("if (!stillRequested()) return false;", holds);
        Assert.Contains("Texts.T(\"archive.showInThread.missing\")", holds);
        Assert.Contains("NotificationType.Warning", holds);
        Assert.Contains("_gallery?.SetNotice(message)", holds); // the open gallery holds the shell's toasts back
        string owner = Body(MediaReturnFile, "private Func<bool> JumpStillRequested");
        Assert.Contains("request == _mediaJumpRequest", owner);
        Assert.Contains("_selectedPage == page", owner);
    }

    // ---- F6: a post the archived copy does not hold ----

    [Fact]
    public void APostTheArchivedCopyDoesNotHoldOpensTheThreadWithOneNote()
    {
        string jump = Body(MediaReturnFile, "private async Task ShowThreadPostInLargeReaderAsync");
        int ensure = jump.IndexOf("archive.EnsureLargeReaderFocus()", StringComparison.Ordinal);
        int note = jump.IndexOf("Texts.T(\"archive.showInThread.postMissing\")", StringComparison.Ordinal);
        Assert.True(ensure >= 0 && note > ensure, "The note follows the reader's focus.");
        Assert.Contains("shown.FindPostIndex(postId) >= 0", jump);
        Assert.Contains("archive.ShowReadingPositionMessage(postHeld ? string.Empty", jump); // a later jump clears it
    }

    // ---- F2: no history of another thread after a jump ----

    [Fact]
    public async Task AJumpLoadStartsWithoutTheHistoryOfAnotherThread()
    {
        using TempDirectory temp = new();
        await SeedAsync(temp.Path);
        BooruComponentDispatcher.Run(() => WithReadingPage(temp.Path, page => Pump(async () =>
        {
            Button back = page.FindControl<Button>("ArchiveHistoryBack")!;
            await page.LoadThreadAsync(Shown);
            Assert.False(page.CanGoHistoryBack);

            // A quote into another thread: the way back to the last read place.
            await page.NavigateQuoteAsync(new ArchiveQuoteRow(new ArchiveQuote(new ArchivePostId(Shown, "p000"),
                new ArchivePostId(Quoted, "p001"), ">>p001", null)));
            Assert.Equal(Quoted, page.CurrentThread?.Summary.Id);
            Assert.True(page.CanGoHistoryBack);

            // A jump to another thread (Media, Tagging, the gallery) loads without it: Alt+Left and "Back to last read" never flip back.
            await page.LoadThreadAsync(Jumped);
            Assert.Equal(Jumped, page.CurrentThread?.Summary.Id);
            Assert.False(page.CanGoHistoryBack);
            Assert.False(back.IsVisible);
        })));
    }

    [Fact]
    public void OnlyALoadOfAnotherThreadOrANewPageClearsTheHistory()
    {
        // The same thread re-read in place (live follow, a list page: resetPage false) keeps the way back.
        string load = Body("Desktop/Views/Archive/ArchivePage.axaml.cs", "private async Task LoadThreadAsync(ResourceId id, bool resetPage, ArchivedThreadView? preparedThread,");
        int guard = load.IndexOf("if (resetPage || _selectedThread != id)", StringComparison.Ordinal);
        int clear = load.IndexOf("_history.Clear();", StringComparison.Ordinal);
        Assert.True(guard >= 0 && clear > guard, "The history is cleared under the thread-change guard.");
        Assert.True(clear < load.IndexOf("_selectedThread = id;", StringComparison.Ordinal), "The guard reads the thread shown before the load.");
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(load, @"_history\.Clear\(\)").Count);
    }

    [Fact]
    public void ContinueReadingIsAJumpToo()
    {
        string resume = Body("Desktop/Views/Archive/ArchivePage.Resume.cs", "internal async Task ResumeReadingPositionAsync");
        int clear = resume.IndexOf("_history.Clear();", StringComparison.Ordinal);
        Assert.True(clear >= 0 && clear < resume.IndexOf("ShowCapturedView(view", StringComparison.Ordinal));
    }

    // ---- F3: an open gallery owns the way back ----

    [Fact]
    public void AnOpenGalleryKeepsAltLeftFromTheHiddenReadersHistory()
    {
        string canRun = Body(MediaReturnFile, "private bool CanRunBack");
        Assert.Contains("\"reader.history-back\" => (_gallery == null || _gallerySuspended) && _archivePage?.CanGoHistoryBack == true", canRun);
        // The other two already wait for the gallery: its own return is offered only while it is suspended, Media's only without one.
        Assert.Contains("\"gallery.return\" => _archivePage?.CanReturnToGallery == true", canRun);
        Assert.Contains("_gallery == null", Member(MediaReturnFile, "private bool CanReturnToMedia"));
        // F1's help lists the back commands by the same answer.
        Assert.Contains("foreach (string id in BackCommands) if (CanRunBack(id))", Source("Desktop/Shell/MainWindow.Fullscreen.cs"));
    }

    // ---- F5: "Back to Tagging" ----

    [Fact]
    public void TheTaggingJumpOffersBackToTaggingInTheReadersHeaderAndOnAltLeft()
    {
        string jump = Body(TaggingFile, "private async Task ShowTaggingEntryInThreadAsync");
        int navigate = jump.IndexOf("NavigateWorkspace(WorkspacePage.Archive", StringComparison.Ordinal);
        int point = jump.IndexOf("_mediaReturn = new TaggingPageReturn(item.Key);", StringComparison.Ordinal);
        Assert.True(navigate >= 0 && point > navigate, "The return point is set once the archive is shown.");
        Assert.True(jump.IndexOf("UpdateMediaReturn();", StringComparison.Ordinal) > point);
        Assert.Contains("ArchivePage.MediaReturn = ReturnToMedia;", jump);
        Assert.True(jump.IndexOf("ShowThreadPostInLargeReaderAsync(item.Thread, item.PostId, request)", StringComparison.Ordinal) > point);

        string ret = Body(MediaReturnFile, "private void ReturnToMedia");
        int close = ret.IndexOf("CloseLargeReader(restoreOrigin: false)", StringComparison.Ordinal);
        Assert.True(close >= 0 && close < ret.IndexOf("ReturnToTaggingPage(tagging.Key)", StringComparison.Ordinal));
        Assert.True(close < ret.IndexOf("NavigateWorkspace(WorkspacePage.Media)", StringComparison.Ordinal));
        // One command, one gesture slot: the Back gesture reaches it as media.return, innermost first.
        Assert.Contains("{ \"reader.history-back\", \"gallery.return\", \"media.return\" }", Source(MediaReturnFile));
        Assert.Contains("_mediaReturn is TaggingPageReturn ? _taggingPage != null : _mediaPage != null", Member(MediaReturnFile, "private bool CanReturnToMedia"));
        Assert.Contains("_mediaReturn is TaggingPageReturn ? \"command.media.return.tagging\" : null", Member(MediaReturnFile, "private void UpdateMediaReturn"));
        // The way back selects the jumped row on the Tagging page.
        string tagging = Body("Desktop/Shell/MainWindow.TagQueue.cs", "private void ReturnToTaggingPage");
        Assert.Contains("_taggingPage.SelectKey(key)", tagging);
    }

    [Fact]
    public void TheReturnButtonNamesThePageItGoesBackTo() => BooruComponentDispatcher.Run(() => WithPage(
        () => new MeowChan.ArchivePage(_ => Task.FromResult<ArchiveReader>(null!), (_, _) => Task.CompletedTask, _ => { }), page =>
        {
            Button back = page.FindControl<Button>("ArchiveMediaReturn")!;
            page.ShowMediaReturn(true, "command.media.return.tagging");
            Assert.True(back.IsVisible);
            Assert.Equal("Back to Tagging", page.MediaReturnLabel);
            Assert.Equal("Back to Tagging", AutomationProperties.GetName(back));
            page.ShowMediaReturn(false);
            Assert.False(back.IsVisible);
            Assert.Equal("Back to media", page.MediaReturnLabel);
            Assert.Equal("Back to media", AutomationProperties.GetName(back));
        }));

    // ---- texts: twelve languages ----

    private static readonly string[] NewKeys = { "archive.showInThread.missing", "archive.showInThread.postMissing", "command.media.return.tagging" };

    [Fact]
    public void EveryNewTextExistsInTwelveLanguages()
    {
        Assert.Equal("Back to Tagging", BuiltInCatalog.English["command.media.return.tagging"]);
        Assert.Equal("Zurück zum Tagging", BuiltInCatalog.German["command.media.return.tagging"]);
        foreach (string key in NewKeys)
        {
            Assert.False(string.IsNullOrWhiteSpace(BuiltInCatalog.English[key]), key);
            Assert.False(string.IsNullOrWhiteSpace(BuiltInCatalog.German[key]), key);
        }
        string[] files = Directory.GetFiles(Path.Combine(Root(), "Desktop/Localization/Packs"), "*.json");
        Assert.Equal(10, files.Length);
        foreach (string file in files)
        {
            using JsonDocument pack = JsonDocument.Parse(File.ReadAllText(file));
            JsonElement strings = pack.RootElement.GetProperty("strings");
            foreach (string key in NewKeys)
            {
                Assert.True(strings.TryGetProperty(key, out JsonElement value), Path.GetFileName(file) + ": " + key);
                string text = value.GetString() ?? string.Empty;
                Assert.False(string.IsNullOrWhiteSpace(text), Path.GetFileName(file) + ": " + key);
                Assert.NotEqual(BuiltInCatalog.English[key], text);
            }
        }
    }

    // ---- helpers ----

    /// <summary>Runs an async test step on the owned dispatcher: continuations come back to it and are pumped until the step ends.</summary>
    private static void Pump(Func<Task> step)
    {
        Dispatcher owner = Dispatcher.UIThread;
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new OwnerContext(owner));
        try
        {
            Task pending = step();
            DateTime deadline = DateTime.UtcNow.AddSeconds(20);
            while (!pending.IsCompleted)
            {
                owner.RunJobs();
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("The archive page step did not settle.");
                if (!pending.IsCompleted) Thread.Sleep(1);
            }
            pending.GetAwaiter().GetResult();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private sealed class OwnerContext(Dispatcher owner) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) => owner.Post(() =>
        {
            SynchronizationContext? previous = Current;
            SetSynchronizationContext(this);
            try { callback(state); }
            finally { SetSynchronizationContext(previous); }
        });
    }

    private static void WithReadingPage(string profile, Action<MeowChan.ArchivePage> test) => WithPage(
        () => new MeowChan.ArchivePage(token => ArchiveReader.OpenAsync(profile, token), (_, _) => Task.CompletedTask, _ => { }), page =>
        {
            page.IsAvailable = true;
            test(page);
        });

    private static void WithPage(Func<MeowChan.ArchivePage> create, Action<MeowChan.ArchivePage> test)
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
        TokenApplication app = new();
        AvaloniaLocator.CurrentMutable.Bind<Application>().ToConstant(app);
        app.Initialize();
        MeowChan.ArchivePage page = create();
        try
        {
            page.IsReadable = true;
            test(page);
        }
        finally { (page as IDisposable)?.Dispose(); }
    }

    private sealed class TokenApplication : Application
    {
        public override void Initialize() =>
            Styles.Add(new StyleInclude(new Uri("avares://MeowChan/")) { Source = new Uri("avares://MeowChan/Desktop/WorkspaceStyles.axaml") });
    }

    private static async Task SeedAsync(string profile)
    {
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        foreach (ResourceId id in new[] { Shown, Quoted, Jumped }) await Capture(catalog, profile, id, 3);
    }

    private static Task Capture(ArchiveCatalog catalog, string profile, ResourceId id, int posts) => catalog.CaptureAsync(new(id,
        "https://example.invalid/" + id.Board + "/" + id.Thread, "synthetic " + id.Board + " " + id.Thread, ThreadSourceState.NotFound,
        DateTimeOffset.UnixEpoch, Path.Combine(profile, "uncreated-media"),
        Enumerable.Range(0, posts).Select(i => new CapturedPost("p" + i.ToString("D3"), i, PostFieldPresence.All, Html: "stored " + i)).ToArray()));

    private static string Member(string file, string start)
    {
        string text = Source(file);
        int index = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(index >= 0, start + " is not in " + file);
        int semicolon = text.IndexOf(';', index), brace = text.IndexOf('{', index);
        return brace >= 0 && brace < semicolon ? Body(file, start) : text[index..(semicolon + 1)];
    }

    private static string Body(string file, string start)
    {
        string text = Source(file);
        int index = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(index >= 0, start + " is not in " + file);
        int open = text.IndexOf('{', index), depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0) return text[index..(i + 1)];
        }
        throw new InvalidOperationException("Unbalanced braces after " + start);
    }

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../.."));

    private static string Source(string relative) => File.ReadAllText(Path.Combine(Root(), relative));
}
