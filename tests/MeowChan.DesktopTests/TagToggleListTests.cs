using System.Diagnostics;
using Avalonia.Controls;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;
using Xunit.Abstractions;

namespace MeowChan.DesktopTests;

/// <summary>C5: the one toggle list of the media tag dialogs: sections, tri-state, in-place updates, undo and scale.</summary>
[Collection("Localization")]
public sealed class TagToggleListTests : IDisposable
{
    private static readonly DateTimeOffset Day = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly ITestOutputHelper _output;

    public TagToggleListTests(ITestOutputHelper output) { _output = output; Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static PrivateTagDefinition Tag(string name)
    {
        Assert.True(PrivateTagName.TryCreate(name, out PrivateTagName? parsed, out _));
        return new PrivateTagDefinition(new TagId(Guid.NewGuid()), parsed!);
    }

    private static PrivateTagStoreSnapshot Snapshot(IEnumerable<PrivateTagDefinition> tags, IEnumerable<(PrivateTagDefinition Tag, int Threads)>? used = null, long revision = 1)
    {
        PrivateThreadTagAssignment[] threads = (used ?? Array.Empty<(PrivateTagDefinition, int)>())
            .SelectMany(item => Enumerable.Range(0, item.Threads).Select(n => new PrivateThreadTagAssignment(item.Tag.Id, new ResourceId("s", "b", item.Tag.Name.Key + n))))
            .ToArray();
        return new PrivateTagStoreSnapshot(revision, "test", tags, threads, Array.Empty<PrivateMediaTagAssignment>());
    }

    private static TagCoverage On(int total, params (PrivateTagDefinition Tag, int Carriers)[] tags) =>
        new(tags.ToDictionary(item => item.Tag.Id, item => item.Carriers), total);

    private static string[] Shown(TagToggleList list) => list.Rows
        .Select(row => row.IsHeader ? "#" + row.Section : row.Label + row.State switch { TagToggleState.All => "+", TagToggleState.Some => "~", _ => "-" })
        .ToArray();

    [Fact]
    public void WithoutAFilterTheTagsOnTheFileComeFirstThenTheMostUsedThenEveryOtherTagOnceAlphabetically()
    {
        PrivateTagDefinition zeta = Tag("zeta"), alpha = Tag("Alpha"), beta = Tag("beta"), gamma = Tag("gamma"), delta = Tag("delta"), unused = Tag("omega");
        PrivateTagStoreSnapshot snapshot = Snapshot(new[] { zeta, alpha, beta, gamma, delta, unused }, new[] { (gamma, 5), (delta, 9), (zeta, 2) });
        TagToggleList list = new(many: false);

        list.Build(snapshot, On(1, (zeta, 1)), null);

        // Assigned (zeta), then the most used delta and gamma (zeta is placed already), then the rest A–Z.
        Assert.Equal(new[] { "#Assigned", "zeta+", "#Frequent", "delta-", "gamma-", "#Other", "Alpha-", "beta-", "omega-" }, Shown(list));
        Assert.Equal(6, list.TagCount);
        Assert.Equal(6, list.Rows.Count(row => !row.IsHeader)); // every tag exactly once
        Assert.Equal(("9", 9, string.Empty), (list.Rows.Single(row => row.Tag == delta).UsesText, list.Rows.Single(row => row.Tag == delta).Uses,
            list.Rows.Single(row => row.Tag == delta).CountText));
        Assert.Equal("0", list.Rows.Single(row => row.Tag == unused).UsesText);
        Assert.Equal("On this media file", list.Rows[0].Label);
        Assert.Equal("zeta: on this media file, used 2 times", list.Rows[1].AccessibleName);
        Assert.Equal("Alpha: not on this media file, not used yet", list.Rows.Single(row => row.Tag == alpha).AccessibleName);
        Assert.Equal("delta, number of uses: 9", list.Rows.Single(row => row.Tag == delta).ToolTipText); // as in the tag field
        Assert.Equal((1, 0), list.Counts());

        // Most used first: no separate section; ties stay alphabetical; uses match the tag field's suggestion index.
        list.Build(snapshot, On(1, (zeta, 1)), null, TagListSort.Uses);
        Assert.Equal(new[] { "#Assigned", "zeta+", "#Other", "delta-", "gamma-", "Alpha-", "beta-", "omega-" }, Shown(list));
        Assert.Equal("Other tags (most used first)", list.Rows[2].Label);
        Assert.Equal(TagListSort.Uses, list.Sort);
        Assert.Equal(TagSuggestionIndex.For(snapshot).Match("delta").Single().Uses, list.Rows.Single(row => row.Tag == delta).Uses);
        list.Build(snapshot, On(1, (zeta, 1)), "a", TagListSort.Uses);
        Assert.Equal(new[] { "#Assigned", "zeta+", "#Matching", "delta-", "gamma-", "Alpha-", "beta-", "omega-" }, Shown(list));
    }

    [Fact]
    public void TheTagManagerSortsByTheSameUsesOrAlphabetically()
    {
        TagManagerRow cat = new(new TagId(Guid.NewGuid()), "cat", 2, 1), ant = new(new TagId(Guid.NewGuid()), "Ant", 0, 1),
            bee = new(new TagId(Guid.NewGuid()), "bee", 1, 0), dog = new(new TagId(Guid.NewGuid()), "dog", 0, 0);
        TagManagerRow[] rows = [dog, cat, bee, ant];
        Assert.Equal(3, cat.Uses);
        Assert.Equal(new[] { "cat", "Ant", "bee", "dog" }, TagManagerRow.Sorted(rows, TagListSort.Uses).Select(row => row.Name));
        Assert.Equal(new[] { "Ant", "bee", "cat", "dog" }, TagManagerRow.Sorted(rows, TagListSort.Alphabetical).Select(row => row.Name));
    }

    [Fact]
    public void AFilterKeepsMatchingAssignedTagsOnTopAndListsTheOtherMatchesWithoutTheFrequentSection()
    {
        PrivateTagDefinition blue = Tag("blue_sky"), blur = Tag("Blur"), sky = Tag("sky"), cafe = Tag("Café");
        TagToggleList list = new(many: false);
        PrivateTagStoreSnapshot snapshot = Snapshot(new[] { blue, blur, sky, cafe }, new[] { (sky, 3) });

        list.Build(snapshot, On(1, (blue, 1)), "  BL ");
        Assert.Equal(new[] { "#Assigned", "blue_sky+", "#Matching", "Blur-" }, Shown(list));

        // Case and Unicode composition never hide a tag; nothing matching leaves no rows (the dialog shows its empty text).
        list.Build(snapshot, On(1, (blue, 1)), "café");
        Assert.Equal(new[] { "#Matching", "Café-" }, Shown(list));
        list.Build(snapshot, On(1, (blue, 1)), "nothing");
        Assert.Empty(list.Rows);
        list.Build(null, TagCoverage.Empty, null);
        Assert.Empty(list.Rows);
        Assert.Equal(0, list.TagCount);
    }

    [Fact]
    public void ATogglePaintsOnlyItsRowInPlaceAndANewVocabularyAsksForANewLayout()
    {
        PrivateTagDefinition a = Tag("a"), b = Tag("b"), c = Tag("c");
        PrivateTagStoreSnapshot snapshot = Snapshot(new[] { a, b, c });
        TagToggleList list = new(many: false);
        list.Build(snapshot, On(1, (a, 1)), null);
        IReadOnlyList<TagToggleRow> rows = list.Rows;
        TagToggleRow rowC = rows.Single(row => row.Tag == c);
        List<string?> raised = new();
        foreach (TagToggleRow row in rows) row.PropertyChanged += (sender, _) => raised.Add(((TagToggleRow)sender!).Label);

        // Same vocabulary, one more tag on the file: one row changes, the layout and the row objects stay.
        PrivateTagStoreSnapshot next = Snapshot(new[] { a, b, c }, revision: 2);
        Assert.Equal(1, list.Apply(next, On(1, (a, 1), (c, 1))));
        Assert.Same(rows, list.Rows);
        Assert.Equal(new[] { "c" }, raised);
        Assert.Equal(TagToggleState.All, rowC.State);
        Assert.Equal(0, list.Apply(next, On(1, (a, 1), (c, 1)))); // nothing new: nothing raised
        Assert.Single(raised);
        // The row stays where it was ("Other"); the sections follow only at the next layout.
        Assert.Equal(new[] { "#Assigned", "a+", "#Other", "b-", "c+" }, Shown(list));

        Assert.Equal(-1, list.Apply(Snapshot(new[] { a, b, c, Tag("d") }, revision: 3), On(1, (a, 1))));
        Assert.Equal(-1, list.Apply(Snapshot(new[] { a, b }, revision: 3), On(1, (a, 1))));
        Assert.Equal(-1, list.Apply(Snapshot(new[] { a, b, c with { Name = Tag("c2").Name } }, revision: 3), On(1, (a, 1))));
        // A new layout keeps each tag's row object, so a focused row survives a filter.
        list.Build(next, On(1, (a, 1), (c, 1)), null);
        Assert.Same(rowC, list.RowOf(c.Id));
        Assert.Equal(new[] { "#Assigned", "a+", "c+", "#Other", "b-" }, Shown(list));
    }

    [Fact]
    public void SelectionRowsAreTriStateWithCountsAndNames()
    {
        PrivateTagDefinition all = Tag("all_of_them"), some = Tag("some_of_them"), none = Tag("none_of_them");
        TagToggleList list = new(many: true);
        list.Build(Snapshot(new[] { all, some, none }), On(3, (all, 3), (some, 1)), null);

        Assert.Equal(new[] { "#Assigned", "all_of_them+", "some_of_them~", "#Other", "none_of_them-" }, Shown(list));
        TagToggleRow rowAll = list.Rows[1], rowSome = list.Rows[2], rowNone = list.Rows[4];
        Assert.Equal((true, (bool?)null, false), (rowAll.IsChecked!.Value, rowSome.IsChecked, rowNone.IsChecked!.Value));
        Assert.Equal("1 of 3", rowSome.CountText);
        Assert.Equal("0 of 3", rowNone.CountText);
        Assert.Equal("0", rowSome.UsesText);
        Assert.Equal("On ticked occurrences", list.Rows[0].Label);
        Assert.Equal("all_of_them: on all 3 ticked occurrences, not used yet", rowAll.AccessibleName);
        Assert.Equal("some_of_them: on 1 of 3 ticked occurrences, not used yet", rowSome.AccessibleName);
        Assert.Equal("none_of_them: on none of the ticked occurrences, not used yet", rowNone.AccessibleName);
        Assert.Equal((1, 1), list.Counts());
        // Nothing ticked: every row is "none" (the session refuses a write with its own message).
        Assert.Equal(TagToggleState.None, On(0, (all, 3)).StateOf(all.Id));

        Texts.SetLanguage("de");
        list.Relocalize();
        Assert.Equal("some_of_them: an 1 von 3 angehakten Fundstellen, noch nicht verwendet", rowSome.AccessibleName);
        Assert.Equal("1 von 3", rowSome.CountText);
    }

    [Fact]
    public void KeyboardStepsSkipSectionLabelsAndStopAtTheEnds()
    {
        TagToggleList list = new(many: false);
        PrivateTagDefinition a = Tag("a"), b = Tag("b"), c = Tag("c");
        list.Build(Snapshot(new[] { a, b, c }), On(1, (a, 1)), null);
        IReadOnlyList<TagToggleRow> rows = list.Rows; // #Assigned a #Other b c

        Assert.Equal(1, TagToggleList.Step(rows, -1, +1));
        Assert.Equal(3, TagToggleList.Step(rows, 1, +1)); // over "#Other"
        Assert.Equal(1, TagToggleList.Step(rows, 3, -1));
        Assert.Equal(1, TagToggleList.Step(rows, 1, -1)); // stays on the first tag
        Assert.Equal(4, TagToggleList.Step(rows, 4, +1)); // and on the last one
        Assert.Equal(1, TagToggleList.Step(rows, 0, -1)); // a label is never a stop
        Assert.Equal((1, 4), (TagToggleList.Edge(rows, true), TagToggleList.Edge(rows, false)));
        Assert.Equal(-1, TagToggleList.Step(Array.Empty<TagToggleRow>(), 0, 1));
    }

    [Fact]
    public void TheListVirtualizesAndBothDialogsUseItInsteadOfButtonWalls()
    {
        TagToggleListBox box = new();
        Assert.IsType<VirtualizingStackPanel>(box.ItemsPanel.Build());
        Assert.Equal(SelectionMode.Single, box.SelectionMode);
        foreach (string file in new[] { "Desktop/Views/Collections/MediaTagDialog.cs", "Desktop/Views/Media/MediaSelectionTagDialog.cs" })
        {
            string source = Source(file);
            Assert.Contains("new TagToggleSurface(", source);
            Assert.Contains("DialogFrame.Build(", source);
            Assert.Contains("fillBody: true", source);
            Assert.DoesNotContain("WrapPanel _assigned", source);
            Assert.DoesNotContain("Children.Clear()", source);
        }
    }

    [Theory]
    [InlineData(1_024)]
    [InlineData(20_000)]
    public void OpeningTogglingAndFilteringStayFastForLargeVocabularies(int count)
    {
        PrivateTagDefinition[] tags = Enumerable.Range(0, count).Select(index => Tag("tag_" + index.ToString("D5"))).ToArray();
        PrivateTagStoreSnapshot snapshot = Snapshot(tags, tags.Skip(10).Take(20).Select((tag, at) => (tag, at + 1)));
        TagCoverage coverage = On(1, tags.Skip(count / 2).Take(5).Select(tag => (tag, 1)).ToArray());
        TagToggleList list = new(many: false);

        // Correctness is asserted on the first, single run of every step. The cost bound is asserted on the fastest of a few
        // repeated runs: a scheduling or GC pause on a loaded machine hits one run, a real regression (one control per tag
        // on every refresh) hits all of them.
        list.Build(snapshot, coverage, null);
        Assert.Equal(count + 3, list.Rows.Count);

        PrivateTagStoreSnapshot next = Snapshot(tags, tags.Skip(10).Take(20).Select((tag, at) => (tag, at + 1)), revision: 2);
        TagCoverage toggledOn = On(1, tags.Skip(count / 2).Take(5).Append(tags[^1]).Select(tag => (tag, 1)).ToArray());
        int changed = list.Apply(next, toggledOn);
        Assert.Equal(1, changed);

        list.Build(next, list.Coverage, "_0001");
        Assert.Equal(tags.Count(tag => tag.Name.Display.Contains("_0001", StringComparison.Ordinal)), list.Rows.Count(row => !row.IsHeader));

        list.Build(next, list.Coverage, null, TagListSort.Uses);
        Assert.Equal(count + 2, list.Rows.Count);

        double open = Fastest(() => list.Build(snapshot, coverage, null));
        // The timed layout above left the baseline coverage showing; Apply alternates to the toggled one and back, one row each time.
        bool toBaseline = false;
        double toggle = Fastest(() => { Assert.Equal(1, list.Apply(next, toBaseline ? coverage : toggledOn)); toBaseline = !toBaseline; });
        double filter = Fastest(() => list.Build(next, list.Coverage, "_0001"));
        double byUses = Fastest(() => list.Build(next, list.Coverage, null, TagListSort.Uses));

        _output.WriteLine($"{count} tags: layout {open:F1} ms, toggle {toggle:F1} ms, filter {filter:F1} ms, most-used order {byUses:F1} ms");
        Assert.True(byUses < 2_000, "most-used order took " + byUses + " ms");
        // Generous bounds for a loaded test machine; the former dialog built one button per tag on every refresh.
        Assert.True(open < 2_000, "layout took " + open + " ms");
        Assert.True(toggle < 500, "toggle took " + toggle + " ms");
        Assert.True(filter < 1_000, "filter took " + filter + " ms");
    }

    private static double Fastest(Action step, int runs = 5)
    {
        double best = double.MaxValue;
        for (int run = 0; run < runs; run++)
        {
            Stopwatch watch = Stopwatch.StartNew();
            step();
            best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
        }
        return best;
    }

    // --- Sessions: one-step undo and the selection coverage. ---

    private static MediaLibraryEntry Entry(string thread, char hash, int captured = 0) =>
        new(new ResourceId("s", "b", thread), "T" + thread, 1, Day, Day.AddHours(captured), "1", 1, null, 0,
            "att-v1:" + new string(hash, 64), "/dir/" + thread, "asset-" + thread, new string(hash, 64), 1, "m1", "original",
            "f" + thread + ".png", SavedMediaKind.Image, MediaLibraryPresence.Present, false, false);

    [Fact]
    public async Task OneUndoRevertsTheLastChangeOfTheSingleFileDialogOnce()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        PrivateMediaTagTarget target = MediaLibraryFilterEngine.TagTarget(Entry("a", 'a'))!;
        TagId red = store.Define("red").Tag;
        MediaTagSession session = new(new MediaTagSubject.Bound(target),
            () => new PrivateTagLoadSnapshot(PrivateTagLoadState.Ready, store.ReadSnapshot(), null),
            (name, on, token) => Task.FromResult(store.DefineAndSetMediaSubjects(name, [on], null, token)),
            (tag, on, assigned, token) => Task.FromResult(store.SetMediaSubjects(tag, [on], assigned, null, token)));

        await session.AddExistingAsync(red);
        Assert.Equal(new TagDialogChange(red, "red", true, session.LastChange!.Subjects), session.LastChange);
        Assert.Equal(TagToggleState.All, session.Coverage().StateOf(red));

        await session.UndoAsync();
        Assert.Empty(store.ReadSnapshot().TagsOf(target));
        Assert.True(session.Undone);
        Assert.Null(session.LastChange);
        long revision = store.ReadSnapshot().Revision;
        await session.UndoAsync(); // one step only
        Assert.Equal(revision, store.ReadSnapshot().Revision);

        // Undo of a typed new name takes it off the file again; the new tag itself stays in the vocabulary.
        await session.AddAsync("fresh");
        Assert.False(session.Undone);
        await session.UndoAsync();
        Assert.Empty(store.ReadSnapshot().TagsOf(target));
        Assert.Contains(store.ReadSnapshot().Tags, tag => tag.Name.Display == "fresh");

        // A refused change leaves nothing to undo (and never undoes an older change instead).
        await session.AddExistingAsync(red);
        await session.AddAsync("bad\tname");
        Assert.Equal("tags.error.invalidName", session.MessageKey);
        Assert.Null(session.LastChange);
    }

    [Fact]
    public async Task SelectionUndoRestoresExactlyTheChangedOccurrencesWhateverIsTickedNow()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        MediaLibraryEntry a = Entry("a", 'a'), b = Entry("b", 'b'), c = Entry("c", 'c');
        MediaLibraryEntry[] all = [a, b, c];
        IReadOnlyList<MediaTagCandidate> candidates = MediaLibraryTagging.Candidates(MediaLibraryGrouping.Build(all, all, MediaLibrarySort.ByThread, includeMissing: true));
        PrivateMediaTagTarget TargetOf(MediaLibraryEntry entry) => MediaLibraryFilterEngine.TagTarget(entry)!;
        TagId red = store.Define("red").Tag;
        store.SetMedia(red, TargetOf(a), true);
        MediaSelectionTagSession session = new(candidates,
            () => new PrivateTagLoadSnapshot(PrivateTagLoadState.Ready, store.ReadSnapshot(), null),
            (name, subjects, revision, token) => Task.FromResult(store.DefineAndSetMediaSubjects(name, subjects.ToArray(), revision, token)),
            (tag, subjects, assigned, revision, token) => Task.FromResult(store.SetMediaSubjects(tag, subjects.ToArray(), assigned, revision, token)));

        TagCoverage coverage = session.Coverage();
        Assert.Equal((3, 1, TagToggleState.Some), (coverage.Total, coverage.Carriers[red], coverage.StateOf(red)));
        Assert.Same(coverage, session.Coverage()); // cached per snapshot and choice
        Assert.Equal(1, Assert.Single(session.Removable()).Count);
        Assert.Equal(2, Assert.Single(session.Addable()).Count);

        await session.AddExistingAsync(red); // "some" → all ticked
        Assert.Equal(TagToggleState.All, session.Coverage().StateOf(red));
        Assert.Equal(2, session.LastChange!.Subjects.Count); // a already had it

        session.Choose(candidates.ToList().FindIndex(item => item.Entry == b), false);
        await session.UndoAsync();
        PrivateTagStoreSnapshot stored = store.ReadSnapshot();
        Assert.Single(stored.TagsOf(TargetOf(a))); // never part of the change
        Assert.Empty(stored.TagsOf(TargetOf(b))); // unticked meanwhile, still restored
        Assert.Empty(stored.TagsOf(TargetOf(c)));
        Assert.True(session.Undone);

        // A typed name that a ticked file already has: the undo leaves that file alone.
        await session.AddAsync("red");
        Assert.Equal(new[] { TargetOf(c) }, session.LastChange!.Subjects.OfType<MediaTagSubject.Bound>().Select(subject => subject.Target));
    }

    private static string Source(string relative, [System.Runtime.CompilerServices.CallerFilePath] string path = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../..", relative)));
}
