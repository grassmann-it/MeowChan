using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

// "Open in Media": from the Archive search (or a list selection) to the Media page with a thread set (MediaFilterField.Threads).
// The page itself needs Avalonia's asset loader, which this host does not start; what is pure is tested here (the term, the filter
// it makes, the chip text, the archive query that collects every thread, the shared selection rule) and the wiring by source contract.
[Collection("Localization")]
public sealed class ArchiveOpenInMediaTests : IDisposable
{
    public ArchiveOpenInMediaTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static ResourceId Thread(string number, string board = "a", string source = "fixture") => new(source, board, number);

    private static string Source(string relative, [System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative)));

    // ---- the term and the filter -------------------------------------------------------------------------------------------------

    [Fact]
    public void ThreadSetKeepsTheOrderOfItsThreadsWithoutDuplicatesAndComparesAsASetPerOrigin()
    {
        MediaThreadSet set = new([Thread("3"), Thread("1"), Thread("3"), Thread("2")], MediaThreadSetOrigin.ArchiveSearch);
        Assert.Equal(["3", "1", "2"], set.Ordered.Select(thread => thread.Thread));
        Assert.Equal(3, set.Count);
        Assert.Equal(set, new MediaThreadSet([Thread("2"), Thread("3"), Thread("1")], MediaThreadSetOrigin.ArchiveSearch));
        Assert.Equal(set.GetHashCode(), new MediaThreadSet([Thread("2"), Thread("3"), Thread("1")], MediaThreadSetOrigin.ArchiveSearch).GetHashCode());
        Assert.NotEqual(set, new MediaThreadSet(set.Ordered, MediaThreadSetOrigin.Selection)); // the origin words the chip
        Assert.NotEqual(set, new MediaThreadSet([Thread("1"), Thread("2")], MediaThreadSetOrigin.ArchiveSearch));
        Assert.Equal(MediaFilterTerm.InThreads(set), MediaFilterTerm.InThreads(new MediaThreadSet(set.Ordered.Reverse(), MediaThreadSetOrigin.ArchiveSearch)));
    }

    [Fact]
    public void TheTermBecomesTheCoreFilterSetAndAnEmptySetIsARestrictionNotNoTerm()
    {
        MediaThreadSet set = new([Thread("1"), Thread("2", "b")], MediaThreadSetOrigin.Selection);
        MediaLibraryFilter filter = MediaFilterTerms.ToFilter([MediaFilterTerm.InThreads(set), MediaFilterTerm.Type(MediaLibraryKindFilter.Video)]);
        Assert.Equal(set.Members, filter.Threads);
        Assert.Equal([MediaLibraryKindFilter.Video], filter.Kinds);
        // No term: no restriction. An empty set: a restriction that lets nothing pass.
        Assert.Null(MediaFilterTerms.ToFilter([]).Threads);
        MediaLibraryFilter empty = MediaFilterTerms.ToFilter([MediaFilterTerm.InThreads(new MediaThreadSet([], MediaThreadSetOrigin.ArchiveSearch))]);
        Assert.NotNull(empty.Threads);
        Assert.Empty(empty.Threads!);
        Assert.False(empty.IsEmpty);
        // It reads back as a term of the same set, and a second, different set cannot hold beside it.
        Assert.Equal(filter, MediaFilterTerms.ToFilter(MediaFilterTerms.FromFilter(filter)));
        Assert.Throws<ArgumentException>(() => MediaFilterTerms.ToFilter([MediaFilterTerm.InThreads(set),
            MediaFilterTerm.InThreads(new MediaThreadSet([Thread("9")], MediaThreadSetOrigin.Selection))]));
        Assert.Throws<ArgumentException>(() => MediaFilterTerms.ToFilter([new MediaFilterTerm(MediaFilterField.Threads)]));
        Assert.Throws<ArgumentException>(() => MediaFilterTerms.ToFilter([MediaFilterTerm.InThreads(set) with { Exclude = true }]));
    }

    [Fact]
    public void ADifferentThreadSetReplacesTheOldOneAndItCombinesWithTheOtherTerms()
    {
        MediaThreadSet first = new([Thread("1")], MediaThreadSetOrigin.ArchiveSearch), second = new([Thread("2"), Thread("3")], MediaThreadSetOrigin.Selection);
        IReadOnlyList<MediaFilterTerm> terms = MediaFilterTermSet.With([MediaFilterTerm.Type(MediaLibraryKindFilter.Image), MediaFilterTerm.InThreads(first)], MediaFilterTerm.InThreads(second));
        Assert.Equal(1, terms.Count(term => term.Field == MediaFilterField.Threads));
        Assert.Equal(second, terms.Single(term => term.Field == MediaFilterField.Threads).ThreadSet);
        Assert.Contains(MediaFilterTerm.Type(MediaLibraryKindFilter.Image), terms);
        // It sits beside the thread: chip, before the name, and a removal leaves the rest.
        IReadOnlyList<MediaFilterTerm> ordered = MediaFilterTermSet.Ordered([MediaFilterTerm.Name("x"), MediaFilterTerm.Type(MediaLibraryKindFilter.Image), MediaFilterTerm.InThreads(second)]);
        Assert.Equal([MediaFilterField.Threads, MediaFilterField.Name, MediaFilterField.Type], ordered.Select(term => term.Field));
        Assert.Equal([MediaFilterTerm.Type(MediaLibraryKindFilter.Image)], MediaFilterTermSet.Without(terms, MediaFilterTerm.InThreads(second)).Where(term => term.Field != MediaFilterField.Threads));
        Assert.DoesNotContain(MediaFilterTermSet.Without(terms, MediaFilterTerm.InThreads(second)), term => term.Field == MediaFilterField.Threads);
        // The in: scope is a term of its own and stays beside the set.
        MediaLibraryFilter filter = MediaFilterTerms.ToFilter([MediaFilterTerm.InThreads(second), MediaFilterTerm.In(MediaTextFields.Tag), MediaFilterTerm.Name("cat")]);
        Assert.Equal(MediaTextFields.Tag, filter.TextFields);
        Assert.Equal(2, filter.Threads!.Count);
    }

    [Fact]
    public void ARuntimeThreadSetHasNoQueryText()
    {
        MediaThreadSet set = new([Thread("1")], MediaThreadSetOrigin.ArchiveSearch);
        Assert.False(MediaFilterTerm.HasQueryText(MediaFilterField.Threads));
        Assert.All(Enum.GetValues<MediaFilterField>().Where(field => field != MediaFilterField.Threads), field => Assert.True(MediaFilterTerm.HasQueryText(field)));
        // "Copy filters as text" leaves it out, the other terms are written.
        Assert.Equal("type:video", MediaFilterQuery.Serialize([MediaFilterTerm.InThreads(set), MediaFilterTerm.Type(MediaLibraryKindFilter.Video)], MediaFilterQueryVocabulary.Empty));
        Assert.Equal(string.Empty, MediaFilterQuery.Serialize([MediaFilterTerm.InThreads(set)], MediaFilterQueryVocabulary.Empty));
        // A typed "threadset:1" is ordinary text; "threads:" is still the English alias of the thread: token.
        MediaFilterQueryResult typed = MediaFilterQuery.Parse("threadset:1", MediaFilterQueryVocabulary.Empty);
        Assert.All(typed.Tokens, token => Assert.Equal(MediaFilterQueryTokenKind.Text, token.Kind));
        Assert.Empty(typed.Tokens.Where(token => token.Term != null));
    }

    // ---- the chip -----------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("en", 1, "1 thread from Archive search", "1 selected thread")]
    [InlineData("en", 12, "12 threads from Archive search", "12 selected threads")]
    [InlineData("en", 0, "0 threads from Archive search", "0 selected threads")]
    [InlineData("de", 1, "1 Thread aus der Archivsuche", "1 ausgewählter Thread")]
    [InlineData("de", 228, "228 Threads aus der Archivsuche", "228 ausgewählte Threads")]
    [InlineData("ru", 1, "1 тред из поиска в архиве", "1 выбранный тред")]
    [InlineData("ru", 3, "3 треда из поиска в архиве", "3 выбранных треда")]
    [InlineData("ru", 5, "5 тредов из поиска в архиве", "5 выбранных тредов")]
    [InlineData("ru", 21, "21 тред из поиска в архиве", "21 выбранный тред")]
    [InlineData("pl", 2, "2 wątki z wyszukiwania w Archiwum", "2 wybrane wątki")]
    [InlineData("pl", 5, "5 wątków z wyszukiwania w Archiwum", "5 wybranych wątków")]
    [InlineData("cs", 1, "1 vlákno z hledání v Archivu", "1 vybrané vlákno")]
    [InlineData("cs", 4, "4 vlákna z hledání v Archivu", "4 vybraná vlákna")]
    [InlineData("fr", 1, "1 fil de la recherche dans Archives", "1 fil sélectionné")]
    [InlineData("ja", 7, "アーカイブ検索のスレッド 7 件", "選択したスレッド 7 件")]
    public void TheChipCountsThreadsInTheGrammaticalNumberOfEveryLanguage(string language, int count, string archive, string selection)
    {
        Texts.SetLanguage(language);
        ResourceId[] threads = [.. Enumerable.Range(0, count).Select(index => Thread(index.ToString()))];
        Assert.Equal(archive, MediaPage.ThreadSetChipText(new MediaThreadSet(threads, MediaThreadSetOrigin.ArchiveSearch)));
        Assert.Equal(selection, MediaPage.ThreadSetChipText(new MediaThreadSet(threads, MediaThreadSetOrigin.Selection)));
    }

    [Fact]
    public void EveryLanguageHasTheActionTheChipAndTheMoreText()
    {
        foreach (string code in Texts.BundledPackCodes().Append("en").Append("de"))
        {
            Texts.SetLanguage(code);
            foreach (string key in new[] { "command.archive.open-results-in-media", "media.filter.threadsChip.archive", "media.filter.threadsChip.selection", "media.filter.threadsMore" })
                Assert.False(string.IsNullOrWhiteSpace(Texts.T(key)) || Texts.T(key) == key, code + " lacks " + key);
            Assert.Contains("5", Texts.T("media.filter.threadsMore", 5));
        }
        Texts.SetLanguage("en");
        Assert.Equal("Open in Media", CommandCatalog.Label("archive.open-results-in-media"));
        Texts.SetLanguage("de");
        Assert.Equal("In Medien öffnen", CommandCatalog.Label("archive.open-results-in-media"));
    }

    // ---- every thread of the search, over all pages -----------------------------------------------------------------------------

    [Fact]
    public async Task TheResultsActionCollectsEveryThreadOfTheAppliedQueryNotJustTheVisiblePage()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using MeowChan.Archive.ArchiveCatalog catalog = await MeowChan.Archive.ArchiveCatalog.OpenAsync(directory.Path, lease);
        List<ThreadWorkSnapshot> work = new();
        for (int index = 0; index < 130; index++)
        {
            ResourceId id = new("fixture", index == 129 ? "other" : "a", index.ToString());
            string url = "https://fixture.test/open/" + index;
            await catalog.CaptureAsync(new CapturedThreadSnapshot(id, url, "match " + index.ToString("D3"), ThreadSourceState.Archived,
                DateTimeOffset.UtcNow, directory.Path, new[] { new CapturedPost(id.Thread!, 0, PostFieldPresence.All, Html: index < 128 ? "needle" : "haystack") }));
            work.Add(new ThreadWorkSnapshot(url, index % 2 == 0 ? WorkIntent.Completed : WorkIntent.Suppressed,
                ThreadSourceStateValue.Archived, null, false, Array.Empty<MediaWork>()));
        }
        ArchiveSearchRequest request = ArchiveSearchRequest.Create(ArchiveSearchSpec.FromLegacy(new ArchiveThreadQuery("needle", Source: "fixture", Board: "a", HasPosts: true, Sort: ArchiveThreadSort.Title)));
        IReadOnlySet<string>? completed = ArchivePage.CaptureExportWorkUrls(4, work);
        work.Clear(); // a later session change must not move the frozen eligibility
        Task<ArchiveReader> Open(CancellationToken token) => ArchiveReader.OpenAsync(directory.Path);

        await using (ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path))
        {
            var visible = await reader.SearchThreadsBySpecAsync(request, 0, 50);
            Assert.Equal(128, visible.TotalCount);
            Assert.Equal(50, visible.Items.Count); // what the list shows
        }
        ResourceId[] all = await ArchivePage.CollectResultThreadsAsync(Open, request, null, CancellationToken.None);
        Assert.Equal(128, all.Length);
        Assert.Equal(Enumerable.Range(0, 128).Select(index => index.ToString()), all.Select(thread => thread.Thread)); // the query's order
        ResourceId[] filtered = await ArchivePage.CollectResultThreadsAsync(Open, request, completed, CancellationToken.None);
        Assert.Equal(64, filtered.Length);
        Assert.Equal(Enumerable.Range(0, 64).Select(index => (index * 2).ToString()), filtered.Select(thread => thread.Thread));
        // The thread set that the shell builds from it is exactly those threads, and filters a media list down to them.
        MediaThreadSet set = new(all, MediaThreadSetOrigin.ArchiveSearch);
        Assert.Equal(128, set.Count);
        Assert.Empty(await ArchivePage.CollectResultThreadsAsync(Open, request, new HashSet<string>(), CancellationToken.None));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ArchivePage.CollectResultThreadsAsync(
            token => token.IsCancellationRequested ? Task.FromCanceled<ArchiveReader>(token) : Open(token), request, null, cancellation.Token));
    }

    // ---- the selection rule shared by the Archive and Downloads lists ------------------------------------------------------------

    [Fact]
    public void SeveralSelectedThreadsOpenAsOneSetAndOneThreadKeepsItsChipWithTheLeadsTitle()
    {
        ResourceId a = Thread("1"), b = Thread("2"), c = Thread("3");
        List<string> log = new();
        void Run(IReadOnlyList<ResourceId> selection, ResourceId lead, string? title) => MediaThreadTargets.Open(selection, lead, title,
            (thread, name) => log.Add("one " + thread.Thread + " " + (name ?? "-")),
            threads => log.Add("many " + string.Join(",", threads.Select(thread => thread.Thread))));

        Run([a, b, c], b, "B"); // Alt+M and the menu with a multi-selection: all of them, not only the lead
        Run([a], a, "A");
        Run([a], b, "B"); // a title belongs to the lead only
        Run([a, a], a, "A"); // the same thread twice is one thread
        Run([a, new ResourceId("fixture", "a")], a, "A"); // a board has no media
        Run([new ResourceId("fixture", "a")], a, "A"); // nothing to show
        Run([], a, "A");
        Assert.Equal(["many 1,2,3", "one 1 A", "one 1 -", "one 1 A", "one 1 A"], log);
    }

    [Fact]
    public void BothListsAndTheirKeyAndMenuGoThroughTheSameSelectionRule()
    {
        // Downloads: the menu entry (all selected threads, counted in its label) and Alt+M.
        string menus = Source("Desktop/Shell/MainWindow.Menus.cs"), selection = Source("Desktop/Shell/MainWindow.Selection.cs"), media = Source("Desktop/Shell/MainWindow.ThreadMedia.cs");
        Assert.Contains("Add(\"thread.show-in-media\", () => ShowSelectedThreadsInMedia(frozen.Threads, frozen.Lead,", menus);
        Assert.Contains("count: frozen.Threads.Length);", menus);
        Assert.Contains("ShowSelectedThreadsInMedia(SelectedThreads().Select(selected => selected.Snapshot.Id).ToArray(), row.Snapshot.Id, row.Title);", selection);
        Assert.DoesNotContain("ShowThreadInMedia(frozen.Lead", menus);
        Assert.DoesNotContain("ShowThreadInMedia(row.Snapshot.Id", selection);
        Assert.Contains("MediaThreadTargets.Open(selected, lead, leadTitle, ShowThreadInMedia,", media);
        Assert.Contains("ArchivePage.ShowThreadsInMediaRequested = ShowThreadsInMedia;", selection);
        // Archive: Alt+M reads the whole frozen selection, the menu offers the entry for several threads with their count.
        string archiveKeys = Source("Desktop/Views/Archive/ArchivePage.Selection.cs"), archiveMenu = Source("Desktop/Views/Archive/ArchivePage.Commands.cs"), archive = Source("Desktop/Views/Archive/ArchivePage.OpenInMedia.cs");
        Assert.Contains("ShowArchiveSelectionInMedia(FrozenArchiveSelection(), row);", archiveKeys);
        Assert.DoesNotContain("FrozenArchiveSelection().Length == 1 && ArchiveLead is ArchiveThreadRow row) ShowThreadInMediaRequested", archiveKeys);
        Assert.Contains("Add(\"thread.show-in-media\", () => ShowArchiveSelectionInMedia(ids, lead),", archiveMenu);
        Assert.Contains("count: () => ids.Length);", archiveMenu);
        Assert.Contains("MediaThreadTargets.Open(selection, lead.Summary.Id, lead.Summary.Title,", archive);
        // The command is offered in the lists' menus and the shortcut works in all of them.
        CommandDefinition show = CommandCatalog.Get("thread.show-in-media");
        Assert.Equal("Alt+M", show.Shortcut);
        Assert.True(show.Scope.HasFlag(CommandScope.Archive) && show.Scope.HasFlag(CommandScope.Downloads));
    }

    [Fact]
    public void TheResultsActionSitsNextToMediaFromMatchingThreadsAndKeepsIt()
    {
        string axaml = Source("Desktop/Views/Archive/ArchivePage.axaml"), toolbar = Source("Desktop/Views/Archive/ArchivePage.ActionToolBar.cs");
        int gallery = axaml.IndexOf("x:Name=\"ArchiveResultsGallery\"", StringComparison.Ordinal), open = axaml.IndexOf("x:Name=\"ArchiveResultsOpenInMedia\"", StringComparison.Ordinal);
        Assert.True(gallery > 0 && open > gallery);
        Assert.Contains("\"gallery.open-results\"", toolbar); // the gallery of the results stays
        Assert.Contains("\"archive.open-results-in-media\"", toolbar);
        Assert.Equal("Media from matching threads", CommandCatalog.Label("gallery.open-results"));
        Assert.Equal(CommandScope.Archive, CommandCatalog.Get("archive.open-results-in-media").Scope);
    }
}
