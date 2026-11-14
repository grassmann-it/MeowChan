using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan.DesktopTests;

[CollectionDefinition("Booru exact navigation", DisableParallelization = true)]
public sealed class BooruExactNavigationCollection;

// Component/helper and source-wiring contracts, NOT an end-to-end or native-focus test. No window,
// profile, manifest, database, provider or asset loader is opened. The constructor is deliberately
// bypassed to exercise the real filter controls and private navigation guard without the UI host/icons.
[Collection("Booru exact navigation")]
public sealed class BooruCrossLinkExactFilterTests
{
    private const string Md5Name = "e99a18c428cb38d5f260853678922e03.jpg";
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void ResetNavigationFindsMd5OriginalNotPostIdSubstringDespiteEveryIncompatibleOldFilter() => OnDispatcher(() =>
    {
        MediaLibraryEntry exact = Entry("123", Md5Name), neighbour = Entry("1234", "1234.jpg");
        using var model = new FilterModel(exact, neighbour);
        model.IncompatibleFilters();
        MediaLibraryFilter old = model.Page.CurrentFilter();
        Assert.NotNull(old.Tags);
        Assert.True(old.FavoritesOnly);
        Assert.Equal(MediaLibrarySoundFilter.WithSound, old.Sound);
        // Own stores are intentionally unavailable: the old filter cannot be quietly treated as empty.
        Assert.Throws<MediaLibraryFilterException>(() => MediaLibraryFilterEngine.Apply([exact, neighbour], old,
            MediaLibraryUserData.Unavailable));

        Assert.True(model.Apply("123"));
        MediaLibraryFilter filter = model.Page.CurrentFilter();
        Assert.Equal(BooruMediaIdentity.Source, filter.Source);
        Assert.Equal(BooruMediaIdentity.Thread("123"), filter.Thread);
        Assert.Null(filter.NameContains);
        Assert.Null(filter.Board);
        Assert.Null(filter.Tags);
        Assert.Null(filter.MinimumRating);
        Assert.False(filter.FavoritesOnly);
        Assert.False(filter.WithoutMediaTags);
        Assert.Empty(filter.Kinds); // M1: no type restriction (the set replaced the single Kind)
        Assert.Equal(MediaLibrarySoundFilter.Any, filter.Sound);
        Assert.False(model.Get<CheckBox>("_missing").IsChecked);
        Assert.Equal(0, model.Get<TwoWayToggle>("_tagMode").SelectedIndex);
        Assert.False(model.Get<bool>("_localizing"));
        var passing = MediaLibraryFilterEngine.Apply([exact, neighbour], filter, MediaLibraryUserData.Unavailable);
        Assert.Same(exact, Assert.Single(passing));
        Assert.Same(exact, Assert.Single(MediaLibraryGrouping.Build([exact, neighbour], passing,
            MediaLibrarySort.ByThread, includeMissing: false)).Entry);
        Assert.True(MediaPage.IsSavedBooruEntry(exact, "123"));
        Assert.False(MediaPage.IsSavedBooruEntry(neighbour, "123"));
        Assert.Same(exact, model.Page.Snapshot!.Entries[0]);
        Assert.Equal("a different filename", old.NameContains); // Captured old query is not mutated in place.
        Assert.True(old.FavoritesOnly);
    });

    [Fact]
    public void ResetNavigationChipsDescribeOnlyTheEffectiveSourceAndExactPostContext() => OnDispatcher(() =>
    {
        using var model = new FilterModel(Entry("123", Md5Name), Entry("1234", "123.jpg"));
        model.IncompatibleFilters();
        Assert.True(model.Apply("123"));
        object[] chips = ((IEnumerable)Call(model.Page, "ActiveFilterChips")!).Cast<object>().ToArray();
        string Property(object chip, string name) => (string)chip.GetType().GetProperty(name)!.GetValue(chip)!;
        // M2: chip keys are term identities ("+field:value"); the order is the term set's (thread, then source).
        Assert.Equal(new[] { "thread", "source" }, chips.Select(chip => Property(chip, "Key").TrimStart('+', '-').Split(':')[0]));
        Assert.Equal(Texts.T("media.filter.threadChip", Texts.T("booru.media.tile.post", "123")), Property(chips[0], "Label"));
        Assert.Equal(Texts.T("archive.sourceChip", Texts.T("booru.media.source")), Property(chips[1], "Label"));
        Assert.DoesNotContain(chips, chip => Property(chip, "Key") == "name");
    });

    [Fact]
    public void ResetRebuildsBoardOptionsFromNewTermsAndClearsExcludedSourcesWithoutUserTakeover() => OnDispatcher(() =>
    {
        using var model = new FilterModel(Entry("123", Md5Name), Entry("1234", "123.jpg"));
        model.IncompatibleFilters();
        Call(model.Page, "SyncFilterControls");
        model.Get<TextBlock>("_queryNote").Text = "old conversion";
        model.Get<TextBlock>("_queryNote").IsVisible = true;
        model.Get<TextBlock>("_queryProblem").Text = "old rejected token";
        model.Get<TextBlock>("_queryProblem").IsVisible = true;
        model.Get<StackPanel>("_queryHint").IsVisible = true;
        // Exercise the new synchronous hooks too. The legacy deferred Return.cs hook is outside
        // this component fixture; it remains a separately reported integration requirement.
        Call(model.Page, "InstallFilterHandoffInterruption");
        Func<bool> request = model.Page.CaptureBooruHandoffInteraction();
        Assert.True(model.Apply("123"));
        Assert.True(request());
        Assert.Null(model.Page.QueryNote);
        Assert.Null(model.Page.QueryProblem);
        Assert.Equal(new[] { MediaFilterTerm.InThread(BooruMediaIdentity.Thread("123")), MediaFilterTerm.Source(BooruMediaIdentity.Source) },
            model.Get<List<MediaFilterTerm>>("_terms"));
        Assert.Empty(model.Page.CurrentFilter().ExcludeSources);
        Assert.Empty(model.Page.CurrentFilter().ExcludeBoards);
        object[] boards = ((IEnumerable)model.Get<ComboBox>("_boardFilter").ItemsSource!).Cast<object>().ToArray();
        Assert.Contains(boards, board => (string?)board.GetType().GetProperty("Board")!.GetValue(board) == BooruMediaIdentity.Board);
        Assert.All(boards, board => Assert.True(board.GetType().GetProperty("Source")!.GetValue(board) is not string source
            || source == BooruMediaIdentity.Source));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingQueryCopyCannotAnnounceSuccessOrFailureAfterBooruReset(bool fail) => OnDispatcher(() =>
    {
        using var model = new FilterModel(Entry("123", Md5Name));
        Assert.True(model.Apply("123"));
        var clipboard = new TaskCompletionSource<bool>();
        model.Page.CopyTextForTests = _ => clipboard.Task;
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null); // Complete on this owned thread, no window/dispatcher pump.
        try
        {
            Task copy = model.Page.CopyFiltersAsTextAsync();
            Assert.False(copy.IsCompleted);
            Assert.True(model.Apply("123"));
            if (fail) clipboard.SetException(new InvalidOperationException("synthetic clipboard failure"));
            else clipboard.SetResult(true);
            copy.GetAwaiter().GetResult();
            Assert.Null(model.Page.QueryNote);
            Assert.Null(model.Page.QueryProblem);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    });

    [Fact]
    public void ExactOriginalIdentityDoesNotLinkByFilenameOrEqualBytes()
    {
        MediaLibraryEntry exact = Entry("123", Md5Name);
        // Same bytes, but a different post/source/variant is not this occurrence.
        Assert.False(MediaPage.IsSavedBooruEntry(Entry("1234", "123.jpg"), "123"));
        Assert.False(MediaPage.IsSavedBooruEntry(exact with { BooruPostId = "1234" }, "123"));
        Assert.False(MediaPage.IsSavedBooruEntry(exact with { Thread = new("other", "posts", "123") }, "123"));
        Assert.False(MediaPage.IsSavedBooruEntry(exact with { Thread = new(BooruMediaIdentity.Source, "other", "123") }, "123"));
        Assert.False(MediaPage.IsSavedBooruEntry(exact with { Variant = "preview" }, "123"));
        Assert.False(MediaPage.IsSavedBooruEntry(exact with { MediaId = BooruMediaIdentity.MediaIdentifier("1234") }, "123"));
        Assert.False(MediaPage.IsSavedBooruEntry(exact with { Presence = MediaLibraryPresence.Missing }, "123"));
        Assert.False(MediaPage.IsSavedBooruEntry(exact with { AssetId = null }, "123"));
    }

    [Theory]
    [InlineData("snapshot/profile")]
    [InlineData("index")]
    [InlineData("applied generation")]
    [InlineData("requested generation")]
    [InlineData("present task")]
    [InlineData("pending present")]
    [InlineData("build task")]
    [InlineData("building")]
    [InlineData("selection identity")]
    [InlineData("user interaction")]
    [InlineData("debounced filter input")]
    [InlineData("new return")]
    [InlineData("dispose")]
    public async Task CapturedNavigationGuardRejectsLateContinuation(string change)
    {
        // The production guard is held across a deterministic await. This proves invalidation of the
        // captured context, not container materialisation or actual keyboard focus.
        var page = (MediaPage)RuntimeHelpers.GetUninitializedObject(typeof(MediaPage));
        using var lifetime = new CancellationTokenSource();
        using var building = new CancellationTokenSource();
        Set(page, "_lifetime", lifetime);
        Set(page, "_snapshot", new MediaLibrarySnapshot([Entry("123", Md5Name)], [], [], true));
        SetProperty(page, "PresentTask", Task.CompletedTask);
        SetProperty(page, "BuildTask", Task.CompletedTask);
        object run = new();
        Set(page, "_returnRun", run);
        Func<bool> current = (Func<bool>)Call(page, "CaptureNavigationContext", run, false)!;
        Assert.True(current());
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> ContinueAsync() { await gate.Task; return current(); }
        Task<bool> continuation = ContinueAsync();
        switch (change)
        {
            case "snapshot/profile": Set(page, "_snapshot", new MediaLibrarySnapshot([Entry("123", Md5Name)], [], [], true)); break;
            case "index":
                Type indexType = typeof(MediaPage).GetNestedType("PresentIndexHolder", BindingFlags.NonPublic)!;
                Set(page, "_presentIndex", Activator.CreateInstance(indexType, BindingFlags.Public | Private, null,
                    new object[] { page.Snapshot! }, null));
                break;
            case "applied generation": SetProperty(page, "PresentCount", 1); break;
            case "requested generation": SetProperty(page, "PresentRunCount", 1); break;
            case "present task": SetProperty(page, "PresentTask", Task.FromResult(1)); break;
            case "pending present": Set(page, "_presentPending", true); break;
            case "build task": SetProperty(page, "BuildTask", Task.FromResult(1)); break;
            case "building": Set(page, "_building", building); break;
            case "selection identity": Set(page, "_selectionStamp", 1L); break;
            case "user interaction": page.EndPendingReturn(); break;
            case "debounced filter input": Call(page, "NoteBooruHandoffInteraction"); break;
            case "new return": Set(page, "_returnRun", new object()); break;
            case "dispose": lifetime.Cancel(); break; // EndLifetime's revocation signal, no UI cleanup on an uninitialised page.
            default: throw new ArgumentException(change);
        }
        gate.SetResult(true);
        Assert.False(await continuation);
    }

    [Fact]
    public void AwaitedPresentationMustApplyExactlyOnceAndCannotUseTheOldShownRows()
    {
        var page = (MediaPage)RuntimeHelpers.GetUninitializedObject(typeof(MediaPage));
        using var lifetime = new CancellationTokenSource();
        Set(page, "_lifetime", lifetime);
        SetProperty(page, "PresentTask", Task.CompletedTask);
        SetProperty(page, "BuildTask", Task.CompletedTask);
        object run = new();
        Set(page, "_returnRun", run);
        Set(page, "_presentPending", true);
        Func<bool> current = (Func<bool>)Call(page, "CaptureNavigationContext", run, true)!;
        Assert.False(current());
        Set(page, "_presentPending", false);
        Assert.False(current()); // Cancellation/failure without an applied result cannot focus stale _shown.
        SetProperty(page, "PresentCount", 1);
        Assert.True(current());
        SetProperty(page, "PresentCount", 2);
        Assert.False(current()); // Even synchronous replacement with the same CompletedTask revokes the request.
    }

    [Fact]
    public void DisposedPageRejectsNavigationBeforeResettingAnyOldFilter() => OnDispatcher(() =>
    {
        using var model = new FilterModel(Entry("123", Md5Name));
        model.IncompatibleFilters();
        model.Get<CancellationTokenSource>("_lifetime").Cancel();
        int cancelledNotices = 0;
        Assert.False(model.Page.ShowBooruPostAsync("123", cancelNotice: () => cancelledNotices++).GetAwaiter().GetResult());
        Assert.Equal(1, cancelledNotices);
        Assert.Equal("a different filename", model.Page.NameFilter.Text);
        Assert.True(model.Page.CurrentFilter().FavoritesOnly);
        Assert.Equal(2, model.Get<List<MediaFilterTerm>>("_terms").Count(term => term.Field == MediaFilterField.Tag)); // both old tags kept (M2 term set)
    });

    [Fact]
    public void RevokedHostRequestRejectsNavigationBeforeResettingAnyOldFilter() => OnDispatcher(() =>
    {
        using var model = new FilterModel(Entry("123", Md5Name));
        model.IncompatibleFilters();
        int gateCalls = 0, cancelledNotices = 0;
        Assert.False(model.Page.ShowBooruPostAsync("123", () => { gateCalls++; return false; }, () => cancelledNotices++).GetAwaiter().GetResult());
        Assert.Equal(1, gateCalls);
        Assert.Equal(1, cancelledNotices);
        Assert.Equal("a different filename", model.Page.NameFilter.Text);
        Assert.True(model.Page.CurrentFilter().FavoritesOnly);
        Assert.Equal(2, model.Get<List<MediaFilterTerm>>("_terms").Count(term => term.Field == MediaFilterField.Tag)); // both old tags kept (M2 term set)
    });

    [Fact]
    public void FocusWiringRechecksAfterTheLayoutAwaitAndRequiresTheExactContainerRow()
    {
        string returns = Source("Desktop/Views/Media/MediaPage.Return.cs");
        string focus = returns[returns.IndexOf("private async Task<ExactEntryFocusResult> FocusExactEntryAsync", StringComparison.Ordinal)..];
        focus = focus[..focus.IndexOf("// Any own input", StringComparison.Ordinal)];
        Assert.Contains("await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);\n            if (!Current()) return ExactEntryFocusResult.Cancelled;", focus);
        foreach (string guard in new[] { "ReferenceEquals(shown, _shown)", "ReferenceEquals(tiles, _tiles)",
            "ReferenceEquals(rows, _grid.ItemsSource)", "ReferenceEquals(exact, _shown[index])",
            "ReferenceEquals(row, _grid.Items[index])", "ReferenceEquals(row.Tile, _tiles[index])", "ReferenceEquals(row.Entry, exact)" })
            Assert.Contains(guard, focus);
        Assert.Contains("(stillRequested?.Invoke() ?? true)", focus);
        Assert.DoesNotContain("FocusGrid(", focus);
        Assert.DoesNotContain("FirstTileInView(", focus);
        string navigation = Source("Desktop/Views/Media/MediaPage.BooruCrossLinks.cs");
        Assert.Contains("await PresentTask.ConfigureAwait(true);\n            if (!Current()) return Cancelled();", navigation);
        Assert.Contains("if (!Current() || focused == ExactEntryFocusResult.Cancelled) return Cancelled();", navigation);
        Assert.Contains("if (focused == ExactEntryFocusResult.Failed) return Failed();", navigation);
        Assert.Contains("run, stillRequested).ConfigureAwait(true)", navigation);
        Assert.DoesNotContain("SetTextSilently(postId)", navigation);
        foreach (string persistence in new[] { "SaveConfig(", "SaveSortAsync", "SaveFilter", "ReadUserData(" })
            Assert.DoesNotContain(persistence, navigation);
    }

    private static MediaLibraryEntry Entry(string post, string name) => new(BooruMediaIdentity.Thread(post), "Post " + post, 0,
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null, null, null, null, null, "/synthetic/not-opened",
        "asset-" + post, new string('a', 64), 7, BooruMediaIdentity.MediaIdentifier(post), "original", name,
        SavedMediaKind.Image, MediaLibraryPresence.Present, true, false) { BooruPostId = post };

    private sealed class FilterModel : IDisposable
    {
        internal MediaPage Page { get; } = (MediaPage)RuntimeHelpers.GetUninitializedObject(typeof(MediaPage));
        private readonly CancellationTokenSource _lifetime = new();
        internal FilterModel(params MediaLibraryEntry[] entries)
        {
            Set(Page, "_lifetime", _lifetime);
            Set(Page, "_snapshot", new MediaLibrarySnapshot(entries, [], [], true));
            Set(Page, "_nameFilter", new SearchBox());
            Set(Page, "_userData", MediaLibraryUserData.Unavailable);
            Set(Page, "_queryNote", new TextBlock { IsVisible = false });
            Set(Page, "_queryProblem", new TextBlock { IsVisible = false });
            Set(Page, "_queryHint", new StackPanel { IsVisible = false });
            Set(Page, "_sortDirection", new Button());
            Set(Page, "_shuffle", new Button());
            foreach (string field in new[] { "_kindFilter", "_soundFilter", "_sourceFilter", "_boardFilter", "_minRating" })
                Set(Page, field, new ComboBox());
            Set(Page, "_tagMode", new TwoWayToggle()); // M5: the all | any switch
            foreach (string field in new[] { "_kindFilter", "_soundFilter", "_minRating" })
            { Get<ComboBox>(field).ItemsSource = new[] { "Any", "First", "Second", "Third", "Fourth", "Fifth" }; Get<ComboBox>(field).SelectedIndex = 0; }
            foreach (string field in new[] { "_favoritesOnly", "_withoutMediaTags", "_missing" }) Set(Page, field, new CheckBox());
            // M2: the term set is the filter (constructor bypassed, so the readonly list is set here).
            Set(Page, "_terms", new List<MediaFilterTerm>());
        }
        internal T Get<T>(string field) => (T)typeof(MediaPage).GetField(field, Private)!.GetValue(Page)!;
        internal void IncompatibleFilters()
        {
            Page.NameFilter.SetTextSilently("a different filename");
            Get<ComboBox>("_kindFilter").SelectedIndex = (int)MediaLibraryKindFilter.Video;
            Get<ComboBox>("_soundFilter").SelectedIndex = (int)MediaLibrarySoundFilter.WithSound;
            Get<ComboBox>("_minRating").SelectedIndex = 5;
            Get<TwoWayToggle>("_tagMode").SelectedIndex = 1;
            foreach (string field in new[] { "_favoritesOnly", "_withoutMediaTags", "_missing" }) Get<CheckBox>(field).IsChecked = true;
            // The fields show the old state; the term set (M2) is what CurrentFilter reads.
            Get<List<MediaFilterTerm>>("_terms").AddRange(new[]
            {
                MediaFilterTerm.Type(MediaLibraryKindFilter.Video), MediaFilterTerm.Sound(MediaLibrarySoundFilter.WithSound), MediaFilterTerm.Rating(5),
                MediaFilterTerm.Flag(MediaFilterField.Favorite), MediaFilterTerm.Flag(MediaFilterField.Untagged), MediaFilterTerm.Flag(MediaFilterField.Missing),
                MediaFilterTerm.Tag(Guid.NewGuid()), MediaFilterTerm.Tag(Guid.NewGuid(), exclude: true),
                MediaFilterTerm.InThread(new ResourceId("old", "board", "456")),
                MediaFilterTerm.Source("old"), MediaFilterTerm.Source(BooruMediaIdentity.Source, exclude: true),
                MediaFilterTerm.Board("old", "board"), MediaFilterTerm.Board(BooruMediaIdentity.Source, BooruMediaIdentity.Board, exclude: true)
            });
        }
        internal bool Apply(string post) => (bool)Call(Page, "ApplyBooruNavigationFilters", BooruMediaIdentity.Thread(post), post)!;
        public void Dispose() => _lifetime.Dispose();
    }

    private static object? Call(MediaPage page, string name, params object[] args) => typeof(MediaPage)
        .GetMethods(Private).Single(method => method.Name == name && method.GetParameters().Length == args.Length).Invoke(page, args);
    private static void Set(MediaPage page, string field, object? value) => typeof(MediaPage).GetField(field, Private)!.SetValue(page, value);
    private static void SetProperty(MediaPage page, string property, object value) => typeof(MediaPage).GetProperty(property, Private)!.SetValue(page, value);
    private static string Source(string path, [CallerFilePath] string caller = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(caller)!, "../..", path)));

    // Existing pinned Avalonia dispatcher seam, without starting an app, rendering or opening a window.
    private static void OnDispatcher(Action test)
    {
        FieldInfo primary = typeof(Dispatcher).GetField("s_uiThread", BindingFlags.Static | BindingFlags.NonPublic)!;
        object? previous = primary.GetValue(null);
        string language = Texts.Language;
        Dispatcher owner = Dispatcher.CurrentDispatcher;
        try
        {
            primary.SetValue(null, owner);
            owner.Invoke(() => { Texts.Initialize(); Texts.SetLanguage("en"); test(); }, DispatcherPriority.Send);
        }
        finally { Texts.Initialize(); Texts.SetLanguage(language); primary.SetValue(null, previous); }
    }
}
