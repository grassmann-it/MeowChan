using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;

namespace MeowChan.DesktopTests;

/// <summary>
/// How a reader is entered (reader-entry package, 17.11.2026): enlarging the thread the embedded reader shows keeps its page and post and writes no reading
/// position; another thread starts at its first post and writes none either (the saved place is only for "Continue reading"); the reader toolbar names
/// "Open a thread first" without a thread; Downloads refuses a thread with nothing captured; the media sidebar's and the focused-enlarge texts are true.
/// </summary>
[Collection("Gallery viewer construction")]
public sealed class ReaderEntryTests
{
    private const int Posts = 130; // three pages of the default reader page size

    // ---- AR-01: enlarging keeps the place, opening another thread starts at the top, neither writes a reading position ----

    [Fact]
    public async Task EnlargingTheShownThreadKeepsItsPageAndPostAndWritesNoReadingPosition()
    {
        ArchivedThreadView view = await SeedAsync("1000");
        WithPage(page =>
        {
            Assert.True(page.LoadThreadAsync(view.Summary.Id, true, view).IsCompletedSuccessfully);
            Show(page, view, 50, "p055");
            Assert.Equal("p055", page.CurrentReaderPostId);
            AddHistoryEntry(page, view);
            Assert.True(page.CanGoHistoryBack);
            List<LocalReadingPosition> written = [];
            page.ConsciousReadingPositionChanged += written.Add;

            Control? pane = null;
            Task open = page.OpenReaderOnThreadAsync(view.Summary.Id, () => pane = page.DetachReader(), view);
            Pump(open);

            Assert.NotNull(pane);
            Assert.Equal(50, PostOffset(page));
            Assert.Equal("p055", page.CurrentReaderPostId);
            Assert.Equal("p055", Assert.IsType<ArchivePostRow>(page.FindControl<ListBox>("ArchivePosts")!.SelectedItem).Id);
            Assert.True(page.CanGoHistoryBack, "enlarging is no jump to another thread: the way back to the last read place stays");
            Assert.Empty(written);
        });
    }

    [Fact]
    public async Task OpeningAnotherThreadUsesItsSavedReadingPlaceWithoutPublishingANewPosition()
    {
        ArchivedThreadView shown = await SeedAsync("1000"), other = await SeedAsync("2000");
        WithPage(page =>
        {
            page.ReadingPlaceFor = id => id == other.Summary.Id
                ? new LocalReadingPosition(id.Source, id.Board, id.Thread!, "p077", 12, null)
                : null;
            Assert.True(page.LoadThreadAsync(shown.Summary.Id, true, shown).IsCompletedSuccessfully);
            Show(page, shown, 50, "p055");
            List<LocalReadingPosition> written = [];
            page.ConsciousReadingPositionChanged += written.Add;

            Task open = page.OpenReaderOnThreadAsync(other.Summary.Id, () => page.DetachReader(), other);
            Pump(open);

            Assert.Equal(other.Summary.Id, page.CurrentThread?.Summary.Id);
            Assert.Equal("p077", page.CurrentReaderPostId);
            Assert.Equal("p077", Assert.IsType<ArchivePostRow>(page.FindControl<ListBox>("ArchivePosts")!.SelectedItem).Id);
            Assert.Empty(written);
        });
    }

    [Fact]
    public async Task OpeningAnotherThreadInTheLargeReaderStartsAtItsFirstPostWithoutReadingPositionOrBackHistory()
    {
        ArchivedThreadView shown = await SeedAsync("1000"), other = await SeedAsync("2000");
        WithPage(page =>
        {
            Assert.True(page.LoadThreadAsync(shown.Summary.Id, true, shown).IsCompletedSuccessfully);
            Show(page, shown, 50, "p055");
            AddHistoryEntry(page, shown);
            List<LocalReadingPosition> written = [];
            page.ConsciousReadingPositionChanged += written.Add;

            Task open = page.OpenReaderOnThreadAsync(other.Summary.Id, () => page.DetachReader(), other);
            Pump(open);

            Assert.Equal(0, PostOffset(page));
            Assert.Equal("p000", page.CurrentReaderPostId);
            Assert.Equal(other.Summary.Id, page.CurrentThread?.Summary.Id);
            Assert.False(page.CanGoHistoryBack, "the history belongs to the thread it was made in");
            Assert.Empty(written);
        });
    }

    [Fact]
    public void TheReaderOpensDoNotPublishAReadingPositionByThemselves()
    {
        string gallery = Source("Desktop/Views/Archive/ArchivePage.Gallery.cs");
        string readPosts = gallery[gallery.IndexOf("private async void OnReadPostsClick", StringComparison.Ordinal)..];
        readPosts = readPosts[..readPosts.IndexOf("private void OnVersionScopeChanged", StringComparison.Ordinal)];
        Assert.DoesNotContain("ReaderPositionChanged", readPosts);
        Assert.Contains("OpenReaderOnThreadAsync(selectedThread,", readPosts);
        string load = Source("Desktop/Views/Archive/ArchivePage.axaml.cs");
        Assert.Contains("focusIntent: focusIntent, passiveOpen: true)", load);
        string reader = Source("Desktop/Views/Archive/ArchivePage.Reader.cs");
        Assert.Contains("ReaderPositionChanged(deliberate: !interactionRevision.HasValue && !passiveOpen)", reader);
    }

    // ---- AR-03: the reader toolbar without a thread ----

    [Fact]
    public async Task ThreadActionsOfTheReaderToolbarAreDisabledWithTheOpenAThreadReasonUntilAThreadIsShown()
    {
        ArchivedThreadView view = await SeedAsync("1000");
        WithPage(page =>
        {
            string reason = Texts.T("reader.thread.openFirst");
            Assert.NotEqual("reader.thread.openFirst", reason);
            Assert.Equal(reason, page.ReaderThreadActionsReason);
            foreach (string name in new[] { "ReaderFindOpen", "ReaderThreadOpen" })
            {
                Button button = page.FindControl<Button>(name)!;
                Assert.False(button.IsEnabled, name);
                string tip = Assert.IsType<string>(ToolTip.GetTip(button));
                Assert.Contains(reason, tip);
                Assert.DoesNotContain(Texts.T("archive.command.busy"), tip);
                Assert.True(ToolTip.GetShowOnDisabled(button), name);
            }
            Assert.True(page.LoadThreadAsync(view.Summary.Id, true, view).IsCompletedSuccessfully);
            Assert.Null(page.ReaderThreadActionsReason);
            Assert.True(page.FindControl<Button>("ReaderFindOpen")!.IsEnabled);
            Assert.True(page.FindControl<Button>("ReaderThreadOpen")!.IsEnabled);
        });
    }

    // ---- DL-01: Read posts on a row with nothing captured ----

    [Fact]
    public void ReadPostsIsRefusedOnlyForAThreadWithNeitherCapturedPostsNorSavedMedia()
    {
        Assert.True(DownloadReaderGate.NothingCaptured(Snapshot(new ThreadContentStatus(true, null, null, null, 0), saved: 0)));
        Assert.False(DownloadReaderGate.NothingCaptured(Snapshot(new ThreadContentStatus(true, null, null, null, 0), saved: 3)), "saved media is something to read");
        Assert.False(DownloadReaderGate.NothingCaptured(Snapshot(new ThreadContentStatus(true, DateTimeOffset.UnixEpoch, null, null, 0), saved: 0)));
        Assert.False(DownloadReaderGate.NothingCaptured(Snapshot(null, saved: 0)), "a legacy provider says nothing and is never refused");
        string reason = Texts.T("menu.unavailable.nothingCaptured");
        Assert.NotEqual("menu.unavailable.nothingCaptured", reason);
        string reader = Source("Desktop/Shell/MainWindow.ReaderActions.cs");
        Assert.Contains("\"menu.unavailable.nothingCaptured\"", reader);
        Assert.Contains("operation: \"downloads.read.nothingCaptured\"", reader);
    }

    [Fact]
    public void AThreadWithoutPostsOpensOnItsMediaAndOtherThreadsOnTheirPosts()
    {
        Assert.True(DownloadReaderGate.OpensOnMedia(0, null));
        Assert.False(DownloadReaderGate.OpensOnMedia(0, DateTimeOffset.UnixEpoch));
        Assert.False(DownloadReaderGate.OpensOnMedia(5, null));
    }

    // ---- O2-01: focus after the large reader closed ----

    [Fact]
    public void AReaderOriginThatIsGoneOrCannotTakeFocusResolvesToTheFallbackOrNothing()
    {
        BooruComponentDispatcher.Run(() =>
        {
            Assert.Null(ReaderOriginFocus.Resolve(null));
            // Without a window nothing is attached to a visual tree: no control is a target (a detached row is never focused).
            Assert.Null(ReaderOriginFocus.Resolve(new ListBoxItem(), new DataGrid()));
        });
        string reader = Source("Desktop/Shell/MainWindow.Reader.cs");
        Assert.Contains("RestoreReaderOriginFocus(_readerOriginFocus, _readerOriginPage)", reader);
        Assert.DoesNotContain("_readerOriginFocus?.Focus()", reader);
    }

    // ---- RD-03 / RD-04 / RD-05: the media sidebar's empty texts and the focused-enlarge setting ----

    [Fact]
    public void TheSidebarsEmptyTextNamesWhatIsTrue()
    {
        Assert.Equal("reader.sidebar.loading", ReaderMediaSidebarRules.EmptyTextKey(loading: true, noPosts: false));
        Assert.Equal("reader.sidebar.loading", ReaderMediaSidebarRules.EmptyTextKey(loading: true, noPosts: true));
        Assert.Equal("reader.sidebar.noPosts", ReaderMediaSidebarRules.EmptyTextKey(loading: false, noPosts: true));
        Assert.Equal("reader.sidebar.empty", ReaderMediaSidebarRules.EmptyTextKey(loading: false, noPosts: false));
        foreach (string key in new[] { "reader.sidebar.loading", "reader.sidebar.noPosts", "reader.sidebar.empty" })
            Assert.NotEqual(key, Texts.T(key));
    }

    [Fact]
    public async Task ASidebarThatPresentsTheFocusedMediaSaysSoWhenTheFocusedEnlargeSwitchIsToggled()
    {
        ArchivedThreadView view = await SeedAsync("1000");
        WithPage(page =>
        {
            Assert.True(page.LoadThreadAsync(view.Summary.Id, true, view).IsCompletedSuccessfully);
            TextBlock message = page.FindControl<TextBlock>("ArchiveMessage")!;
            Action toggle = FocusedEnlargeToggle(page);
            string note = Texts.T("reader.view.focusedEnlarge.inSidebar");
            Assert.NotEqual("reader.view.focusedEnlarge.inSidebar", note);

            message.Text = string.Empty;
            toggle();
            Assert.NotEqual(note, message.Text); // no sidebar: the switch simply works
            page.FocusedMediaPresentedElsewhere = true;
            toggle();
            Assert.Equal(note, message.Text);
            Assert.Contains(note, Texts.T("settings.reader.focusedEnlarge.hint"));
        });
    }

    // ---- helpers ----

    private static Action FocusedEnlargeToggle(ArchivePage page)
    {
        var options = (System.Collections.IEnumerable)typeof(ArchivePage).GetField("_viewOptions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
        foreach (object option in options)
            if ((string)option.GetType().GetProperty("CommandId")!.GetValue(option)! == "reader.view.focused-enlarge")
                return (Action)option.GetType().GetProperty("Toggle")!.GetValue(option)!;
        throw new InvalidOperationException("The focused-enlarge view option is not registered.");
    }

    private static void Show(ArchivePage page, ArchivedThreadView view, int offset, string postId) =>
        typeof(ArchivePage).GetMethod("ShowCapturedView", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, [view, offset, postId]);

    private static int PostOffset(ArchivePage page) =>
        (int)typeof(ArchivePage).GetField("_postOffset", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;

    // A way back to a place of this thread, as a followed quote leaves it.
    private static void AddHistoryEntry(ArchivePage page, ArchivedThreadView view)
    {
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Type entry = typeof(ArchivePage).GetNestedType("ReaderHistoryEntry", BindingFlags.NonPublic)!;
        object replies = typeof(ArchivePage).GetMethod("CaptureInlineReplyState", flags)!.Invoke(page, null)!;
        object created = Activator.CreateInstance(entry, view, 0, null, replies)!;
        object history = typeof(ArchivePage).GetField("_history", flags)!.GetValue(page)!;
        history.GetType().GetMethod("AddLast", [entry])!.Invoke(history, [created]);
    }

    private static ThreadSnapshot Snapshot(ThreadContentStatus? content, int saved) =>
        new(new ResourceId("synthetic-A", "board-A", "4242"), "https://example.invalid/4242", "Row", "/synthetic-never-opened/output",
            ThreadActivity.Pending, ThreadSourceState.Active, null, saved, saved, new Dictionary<string, FailureInfo>(), null, new DownloadCounts(0, 0))
        { Content = content };

    private static void Pump(Task task)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (!task.IsCompleted && elapsed.Elapsed < TimeSpan.FromSeconds(5))
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static string Source(string relative, [CallerFilePath] string file = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "../..", relative)));

    private static void WithPage(Action<ArchivePage> test)
    {
        BooruComponentDispatcher.Run(() =>
        {
            using IDisposable scope = AvaloniaLocator.EnterScope();
            AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
            TokenApplication.Bind();
            ArchivePage page = new(_ => Task.FromResult<ArchiveReader>(null!), (_, _) => Task.CompletedTask, _ => { });
            try { page.IsReadable = true; test(page); }
            finally { (page as IDisposable)?.Dispose(); }
        });
    }

    private static async Task<ArchivedThreadView> SeedAsync(string threadId)
    {
        ResourceId id = new("synthetic-A", "board-A", threadId);
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(temp.Path, lease))
        {
            CapturedPost[] posts = Enumerable.Range(0, Posts).Select(i => new CapturedPost(
                "p" + i.ToString("D3"), i, PostFieldPresence.All, Html: "synthetic stored text " + i)).ToArray();
            await catalog.CaptureAsync(new(id, "https://example.invalid/not-acquired", "Reader entry " + threadId,
                ThreadSourceState.NotFound, DateTimeOffset.UnixEpoch, Path.Combine(temp.Path, "uncreated-media"), posts));
        }
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(temp.Path);
        return (await reader.ReadThreadAsync(id))!;
    }
}
