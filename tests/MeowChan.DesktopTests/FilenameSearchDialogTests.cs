using MeowChan;
using MeowChan.Archive;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// Pure coordinator/projection tests: no Window creation, database, filesystem or native UI.
public sealed class FilenameSearchDialogTests
{
    private static TaskCompletionSource<T> Gate<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Wait(Task task) => task.WaitAsync(TimeSpan.FromSeconds(5));
    private static ArchiveFilenameHit Hit(string name = "synthetic.png", int position = 0) =>
        new(new ResourceId("4chan", "g", "101"), "102", position, "revision-exact", "original", name, false, false, 7);
    private static ArchiveFilenamePage Page(ArchiveFilenameHit hit, long count = 1, int offset = 0) => new(new[] { hit }, count, offset);

    [Fact]
    public async Task ConstructionAndEditsNeverSearchAndTextIsNotTrimmedOrParsed()
    {
        int calls = 0;
        const string literal = "  %_ [name] ' ß 😀  ";
        var hit = Hit();
        var session = new FilenameSearchSession((query, offset, limit, token) =>
        {
            Interlocked.Increment(ref calls);
            Assert.Equal(literal, query.Text); Assert.Equal(0, offset); Assert.Equal(100, limit);
            return Task.FromResult(Page(hit, 345));
        });
        session.Edit(literal);
        Assert.Equal(0, calls); Assert.Empty(session.Current.Rows);
        await Wait(session.SearchAsync());
        var view = session.Current;
        Assert.Equal(literal, view.Text); Assert.Equal(345L, view.Total);
        Assert.Same(hit, Assert.Single(view.Rows)); Assert.True(session.CanReturn(hit));
        Assert.False(session.CanReturn(hit with { })); // equality is not selection identity
        await session.CloseAsync();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(512)]
    public async Task EmptyAndExactTextBoundaryReachTheExistingSearchContract(int length)
    {
        string text = new(' ', length);
        var session = new FilenameSearchSession((query, offset, limit, token) =>
        {
            Assert.Equal(text, query.Text);
            return Task.FromResult(new ArchiveFilenamePage(Array.Empty<ArchiveFilenameHit>(), 0, offset));
        });
        session.Edit(text); await Wait(session.SearchAsync());
        Assert.Equal("filename.search.ready", session.Current.StatusKey);
        Assert.False(session.Current.CanPrevious); Assert.False(session.Current.CanNext);
        await session.CloseAsync();
    }

    [Fact]
    public async Task CompleteSurrogatePairsArePreservedAndOverlengthRejectedBeforeHandoff()
    {
        var forwarded = new List<string>();
        var session = new FilenameSearchSession((q, o, l, t) =>
        {
            forwarded.Add(q.Text);
            return Task.FromResult(new ArchiveFilenamePage(Array.Empty<ArchiveFilenameHit>(), 0, o));
        });
        // These are coordinator inputs; the smoke helper separately exercises TextInput/Paste.
        string over = new string('a', 511) + "😀";
        session.Edit(over); await Wait(session.SearchAsync());
        Assert.Equal(over, session.Current.Text);
        Assert.Equal("filename.search.invalid", session.Current.StatusKey);
        Assert.Empty(forwarded);
        string replaced = new string('a', 510) + "😀";
        string emojiBoundary = string.Concat(Enumerable.Repeat("😀", 256));
        foreach (string text in new[] { replaced, emojiBoundary })
        {
            Assert.Equal(512, text.Length);
            session.Edit(text); await Wait(session.SearchAsync());
            Assert.Equal("filename.search.ready", session.Current.StatusKey);
            Assert.Equal(text, session.Current.Text);
        }
        Assert.Equal(new[] { replaced, emojiBoundary }, forwarded);
        await session.CloseAsync();
    }

    [Fact]
    public async Task InvalidTextNeverReachesReaderAndClearsOldSelection()
    {
        int calls = 0; var hit = Hit();
        var session = new FilenameSearchSession((q, o, l, t) => { calls++; return Task.FromResult(Page(hit)); });
        await Wait(session.SearchAsync()); Assert.True(session.CanReturn(hit));
        foreach (string text in new[] { new string('x', 513), "a\0b" })
        {
            session.Edit(text); Assert.False(session.CanReturn(hit)); Assert.Empty(session.Current.Rows);
            await session.SearchAsync(); Assert.Equal("filename.search.invalid", session.Current.StatusKey);
            Assert.Equal(0L, session.Current.Total);
        }
        Assert.Equal(1, calls); await session.CloseAsync();
    }

    [Fact]
    public async Task PageSizeIsAlways100AndCountAndRowsReplaceTogetherPerPage()
    {
        var offsets = new List<int>();
        var session = new FilenameSearchSession((query, offset, limit, token) =>
        {
            Assert.Equal(100, limit); offsets.Add(offset);
            var rows = Enumerable.Range(offset, Math.Min(limit, 250 - offset)).Select(i => Hit(position: i)).ToArray();
            return Task.FromResult(new ArchiveFilenamePage(rows, 250, offset));
        });
        await session.PreviousAsync(); await session.NextAsync(); Assert.Empty(offsets);
        await Wait(session.SearchAsync()); Assert.True(session.Current.CanNext); Assert.False(session.Current.CanPrevious);
        var old = session.Current.Rows[0];
        await Wait(session.NextAsync()); Assert.False(session.CanReturn(old)); Assert.Equal(100, session.Current.Offset);
        await Wait(session.NextAsync()); Assert.Equal(50, session.Current.Rows.Length); Assert.Equal(250L, session.Current.Total);
        Assert.False(session.Current.CanNext); await session.NextAsync();
        await Wait(session.PreviousAsync());
        Assert.Equal(new[] { 0, 100, 200, 100 }, offsets);
        session.Edit("new"); await Wait(session.SearchAsync()); Assert.Equal(0, session.Current.Offset);
        await session.CloseAsync();
    }

    [Fact]
    public async Task ChangedCatalogCountIsNotReusedFromPriorPage()
    {
        var session = new FilenameSearchSession((q, offset, limit, token) => Task.FromResult(offset == 0
            ? new ArchiveFilenamePage(Enumerable.Range(0, 100).Select(i => Hit(position: i)).ToArray(), 200, 0)
            : Page(Hit("new-page"), 101, 100)));
        await Wait(session.SearchAsync()); await Wait(session.NextAsync());
        Assert.Equal(101L, session.Current.Total);
        Assert.Equal("new-page", Assert.Single(session.Current.Rows).OriginalName);
        Assert.False(session.Current.CanNext); await session.CloseAsync();
    }

    [Fact]
    public async Task EditingInvalidatesAnUncooperativeOldRequestBeforeExplicitNextSearch()
    {
        var started = Gate<CancellationToken>(); var oldResult = Gate<ArchiveFilenamePage>();
        var session = new FilenameSearchSession((q, o, l, token) => { started.TrySetResult(token); return oldResult.Task; });
        Task old = session.SearchAsync(); await Wait(started.Task);
        session.Edit("new query"); Assert.True(started.Task.Result.IsCancellationRequested);
        oldResult.SetResult(Page(Hit("stale"), 999)); await Wait(old);
        Assert.Equal("new query", session.Current.Text); Assert.Empty(session.Current.Rows); Assert.Equal(0L, session.Current.Total);
        Assert.Equal("filename.search.idle", session.Current.StatusKey); await session.CloseAsync();
    }

    [Fact]
    public async Task NewRequestWinsOverLateOldResultEvenForTheSameText()
    {
        var first = Gate<ArchiveFilenamePage>(); var second = Gate<ArchiveFilenamePage>();
        var started1 = Gate<CancellationToken>(); var started2 = Gate<CancellationToken>(); int calls = 0;
        var session = new FilenameSearchSession((q, o, l, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1) { started1.SetResult(token); return first.Task; }
            started2.SetResult(token); return second.Task;
        });
        Task a = session.SearchAsync(); await Wait(started1.Task);
        Task b = session.SearchAsync(); await Wait(started2.Task); Assert.True(started1.Task.Result.IsCancellationRequested);
        var latest = Hit("latest"); second.SetResult(Page(latest, 33)); await Wait(b);
        first.SetResult(Page(Hit("stale"), 900)); await Wait(a);
        Assert.Same(latest, Assert.Single(session.Current.Rows)); Assert.Equal(33L, session.Current.Total);
        await session.CloseAsync();
    }

    [Fact]
    public async Task CancelAndCloseInvalidateResultsAndCloseDrainsWithoutLatePublish()
    {
        var result = Gate<ArchiveFilenamePage>(); var started = Gate<CancellationToken>();
        var session = new FilenameSearchSession((q, o, l, token) => { started.SetResult(token); return result.Task; });
        Task run = session.SearchAsync(); await Wait(started.Task);
        session.CancelSearch(); Assert.True(started.Task.Result.IsCancellationRequested);
        Assert.False(session.Current.Busy); Assert.Equal("filename.search.canceled", session.Current.StatusKey);
        int changes = 0; session.Changed += () => Interlocked.Increment(ref changes);
        Task drain = session.CloseAsync(); Assert.False(drain.IsCompleted);
        session.Edit("ignored after close"); await session.SearchAsync();
        result.SetResult(Page(Hit(), 20)); await Wait(drain); await Wait(run);
        Assert.True(session.Current.Closed); Assert.Empty(session.Current.Rows); Assert.Equal(0, changes);
        Assert.False(session.CanReturn(Hit())); await session.CloseAsync();
    }

    [Fact]
    public async Task CancelCanRetryAndLateFailureCannotReplaceTheNewPage()
    {
        var oldResult = Gate<ArchiveFilenamePage>(); var started = Gate<CancellationToken>(); int calls = 0;
        var latest = Hit("retry");
        var session = new FilenameSearchSession((q, o, l, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1) { started.SetResult(token); return oldResult.Task; }
            return Task.FromResult(Page(latest));
        });
        Task old = session.SearchAsync(); await Wait(started.Task); session.CancelSearch();
        await Wait(session.SearchAsync());
        oldResult.SetException(new Exception("stale private path")); await Wait(old);
        Assert.Equal("filename.search.ready", session.Current.StatusKey);
        Assert.Same(latest, Assert.Single(session.Current.Rows)); Assert.True(session.CanReturn(latest));
        await session.CloseAsync(); Assert.False(session.CanReturn(latest));
    }

    [Fact]
    public async Task InvalidBackendPageIsNotPublishedBeyondTheBound()
    {
        var session = new FilenameSearchSession((q, o, l, t) => Task.FromResult(new ArchiveFilenamePage(
            Enumerable.Range(0, 101).Select(i => Hit(position: i)).ToArray(), 101, 0)));
        await Wait(session.SearchAsync());
        Assert.Equal("filename.search.failed", session.Current.StatusKey);
        Assert.Empty(session.Current.Rows); Assert.Equal(0L, session.Current.Total);
        await session.CloseAsync();
    }

    [Fact]
    public async Task FailuresAreFixedStatusNotRawExceptionDetailsAndCanBeRetried()
    {
        int calls = 0;
        var session = new FilenameSearchSession((q, o, l, token) => ++calls == 1
            ? Task.FromException<ArchiveFilenamePage>(new Exception("PRIVATE-PATH secret-url"))
            : Task.FromResult(Page(Hit())));
        await Wait(session.SearchAsync());
        Assert.Equal("filename.search.failed", session.Current.StatusKey); Assert.Empty(session.Current.Rows);
        await Wait(session.SearchAsync()); Assert.Equal("filename.search.ready", session.Current.StatusKey);
        await session.CloseAsync();
    }

    [Fact]
    public async Task PassiveFactoryIsNotCalledUntilExplicitSearchOrAfterClose()
    {
        int calls = 0;
        var session = new FilenameSearchSession((CancellationToken token) =>
        { calls++; return Task.FromException<ArchiveReader>(new Exception("private catalog path")); });
        session.Edit("name"); Assert.Equal(0, calls);
        await Wait(session.SearchAsync()); Assert.Equal(1, calls); Assert.Equal("filename.search.failed", session.Current.StatusKey);
        await session.CloseAsync(); await session.SearchAsync(); Assert.Equal(1, calls);
    }

    [Fact]
    public void RowProjectionUsesOnlyExactClaimsAndNeverClaimsKnownBytes()
    {
        var hit = Hit("<script>inert name</script>") with
        { PostIsDeleted = true, AttachmentIsDeleted = true, AttachmentRevisionKey = null, Variant = "preview" };
        string row = FilenameSearchDialog.DescribeHit(hit, (key, args) => FilenameSearchCatalog.Resolve(key, "en", args));
        Assert.Contains("Original name claim: <script>inert name</script>", row);
        Assert.Contains("Source: 4chan · Board: g · Thread: 101 · Post: 102", row);
        Assert.Contains("Variant: preview · Position: 0", row);
        Assert.Contains("Attachment revision: unknown · Catalog thread revision: 7", row);
        Assert.Contains("Post: deleted · Attachment: deleted · Current bytes: unknown", row);
        Assert.Equal("revision-exact", Hit().AttachmentRevisionKey);
    }

    [Fact]
    public void CatalogHasUniqueEnDeKeysAndEnglishFallbackWithoutSharedRegistration()
    {
        Assert.Equal(FilenameSearchCatalog.Entries.Length, FilenameSearchCatalog.Entries.Select(e => e.Key).Distinct().Count());
        foreach (var entry in FilenameSearchCatalog.Entries)
        {
            object?[] args = Enumerable.Range(0, 11).Select(i => (object?)i.ToString()).ToArray();
            string english = FilenameSearchCatalog.Resolve(entry.Key, "en", args);
            string german = FilenameSearchCatalog.Resolve(entry.Key, "de", args);
            Assert.NotEqual(entry.Key, english); Assert.NotEqual(entry.Key, german);
            Assert.Equal(english, FilenameSearchCatalog.Resolve(entry.Key, "fr", args));
            Assert.Equal(english, FilenameSearchCatalog.Resolve(entry.Key, "ru", args));
        }
    }
}
