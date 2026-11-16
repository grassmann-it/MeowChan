using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

// Several tags in one tag-field entry (16.11.2026): the separators of the parser (comma, semicolon, line break, CJK forms, whitespace),
// quotes, trimming, de-duplication and the ceiling; completion of the last token; one undo step for the whole entry in the quick tag
// session and in the dialog session; the status that names added, new and already present tags; the own tag limit in the middle of an
// entry; and the stamp field of the tagging page.
[Collection("Localization")]
public sealed class MultiTagEntryTests : IDisposable
{
    private static readonly DateTimeOffset Day = new(2026, 11, 16, 0, 0, 0, TimeSpan.Zero);
    public MultiTagEntryTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static MediaLibraryEntry Entry(string thread, char hash) =>
        new(new ResourceId("s", "b", thread), "T" + thread, 1, Day, Day, "1", 1, null, 0,
            "att-v1:" + new string(hash, 64), "/dir/" + thread, "asset-" + thread, new string(hash, 64), 1, "m1", "original",
            "f" + thread + ".png", SavedMediaKind.Image, MediaLibraryPresence.Present, false, false);

    private static PrivateTagLoadSnapshot Load(PrivateTagStore store) => new(PrivateTagLoadState.Ready, store.ReadSnapshot(), null);

    private static MediaTagSession Single(PrivateTagStore store, MediaLibraryEntry entry) =>
        new(new MediaTagSubject.Bound(MediaLibraryFilterEngine.TagTarget(entry)!), () => Load(store),
            (name, subject, token) => Task.FromResult(store.DefineAndSetMediaSubjects(name, [subject], null, token)),
            (id, subject, assigned, token) => Task.FromResult(store.SetMediaSubjects(id, [subject], assigned, null, token)));

    private static string[] Names(TagEntry entry) => entry.Items.Select(item => item.Name.Display).ToArray();

    private static string[] Carried(PrivateTagStore store, MediaLibraryEntry entry) =>
        store.ReadSnapshot().TagsOf(MediaLibraryFilterEngine.TagTarget(entry)!).Select(tag => tag.Name.Display).Order(StringComparer.Ordinal).ToArray();

    // ---- parser ----------------------------------------------------------------------------------------------------

    [Fact]
    public void CommasSemicolonsLineBreaksAndTheirCjkFormsSeparateAndEachPartKeepsItsSpaces()
    {
        Assert.Equal(new[] { "a", "b c", "d" }, Names(TagEntryParser.Parse("a; b c\nd", null)));
        Assert.Equal(new[] { "a", "b", "c", "d" }, Names(TagEntryParser.Parse("a, b;c\r\nd", null)));
        Assert.Equal(new[] { "猫", "犬", "鳥", "魚" }, Names(TagEntryParser.Parse("猫，犬、鳥；魚", null)));
        // Edge spaces, empty parts and the same name in another case count once.
        Assert.Equal(new[] { "a", "b" }, Names(TagEntryParser.Parse(" a ,, A ; ;b \n", null)));
        // A separator inside quotes belongs to the name.
        Assert.Equal(new[] { "a;b", "c" }, Names(TagEntryParser.Parse("\"a;b\" c", null)));
        Assert.Equal(new[] { "x, y", "z" }, Names(TagEntryParser.Parse("\"x, y\"; z", null)));
    }

    [Fact]
    public void SpacesInAnyFormSeparateWordsWhenNoHardSeparatorIsThere()
    {
        Assert.Equal(new[] { "a", "b", "c", "d" }, Names(TagEntryParser.Parse("a b\u00A0c\u3000d", null)));
        Assert.Equal(new[] { "a", "b" }, Names(TagEntryParser.Parse("a    b", null)));
        // A tab is no separator: it stays in the name, which the name rule refuses (as before).
        TagEntry tab = TagEntryParser.Parse("bad\tname", null);
        Assert.Empty(tab.Items);
        Assert.Equal("bad\tname", Assert.Single(tab.Invalid));
        Assert.True(TagEntryParser.IsPlain(tab, "bad\tname"));
        // An underscore is part of the name.
        Assert.Equal(new[] { "big_tits", "anal" }, Names(TagEntryParser.Parse("big_tits anal", null)));
    }

    [Fact]
    public void AnExistingNameWithSeparatorsStaysOneTagAndAliasesResolveInsideAnEntryWithSemicolons()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId piss = store.Define("piss").Tag, pee = store.Define("pee").Tag;
        store.Define("a;b");
        store.Define("big tits");
        store.Merge(piss, [pee]);
        PrivateTagStoreSnapshot snapshot = store.ReadSnapshot();

        Assert.Equal(new[] { "a;b" }, Names(TagEntryParser.Parse("a;b", snapshot)));
        TagEntry entry = TagEntryParser.Parse("pee; PISS ;cute\nbig tits", snapshot);
        Assert.Equal(new[] { "piss", "cute", "big tits" }, Names(entry));
        Assert.Equal(piss, entry.Items[0].Id);
        Assert.True(entry.Items[0].Mapped);
        Assert.Null(entry.Items[1].Id);
        Assert.NotNull(entry.Items[2].Id);
        Assert.Equal("piss (from pee), cute (new), big tits", TagEntryParser.Describe(entry.Items));
        // Without a hard separator the words that together name an existing tag stay together.
        Assert.Equal(new[] { "big tits", "anal" }, Names(TagEntryParser.Parse("big tits anal", snapshot)));
    }

    [Fact]
    public void AnEntryNamesAtMostTheCeilingAndAHugeTextIsRefusedWhole()
    {
        string hundred = string.Join(' ', Enumerable.Range(0, TagEntryParser.MaximumNames).Select(index => "t" + index));
        Assert.Equal(TagEntryParser.MaximumNames, TagEntryParser.Parse(hundred, null).Items.Count);
        // The same name again does not count twice.
        Assert.Equal(TagEntryParser.MaximumNames, TagEntryParser.Parse(hundred + " T0 t1", null).Items.Count);

        string tooMany = hundred + " extra";
        TagEntry refused = TagEntryParser.Parse(tooMany, null);
        Assert.True(refused.TooMany);
        Assert.Empty(refused.Items);
        Assert.Equal(tooMany, Assert.Single(refused.Invalid));
        Assert.False(TagEntryParser.IsPlain(refused, tooMany));

        Assert.True(TagEntryParser.Parse(new string('x', TagEntryParser.MaximumTextLength + 1), null).TooMany);
    }

    [Fact]
    public void PastedLinesBecomeACommaListAndTheLastTokenIsTheOneCompleted()
    {
        Assert.Equal("a, b, c", TagEntryParser.ForField("a\r\nb\n\n c \n"));
        Assert.Equal(" a b ", TagEntryParser.ForField(" a b "));
        Assert.Equal(new[] { "a", "b", "c" }, Names(TagEntryParser.Parse(TagEntryParser.ForField("a\nb\r\nc"), null)));

        Assert.Equal(("a; ", "b"), TagEntryParser.SplitLast("a; b"));
        Assert.Equal(("a\n", "b"), TagEntryParser.SplitLast("a\nb"));
        Assert.Equal(("x\u3000", "y"), TagEntryParser.SplitLast("x\u3000y"));
        Assert.Equal("\"a;b\"", TagEntryParser.Quote("a;b"));
        Assert.Equal("plain", TagEntryParser.Quote("plain"));

        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        store.Define("bar");
        PrivateTagStoreSnapshot snapshot = store.ReadSnapshot();
        Assert.Contains(TagSuggestionIndex.CombineEntry(snapshot, null, "foo; ba", new HashSet<TagId>()), item => item.Value == "foo; bar");
        Assert.Contains(TagSuggestionIndex.CombineEntry(snapshot, null, "foo\nba", new HashSet<TagId>()), item => item.Value == "foo\nbar");
    }

    // ---- quick tag session: one undo step per entry ------------------------------------------------------------------

    [Fact]
    public async Task OneUndoTakesBackEveryTagOfTheEntryAndTheCreatedTagsStay()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        MediaLibraryEntry entry = Entry("a", 'a');
        using QuickTagSession session = new(QuickTagTarget.For(Single(store, entry)));

        await session.AddAsync("red, green; blue");

        Assert.Equal(new[] { "blue", "green", "red" }, Carried(store, entry));
        TagDialogChange step = Assert.Single(session.UndoSteps);
        Assert.Equal("red, green, blue", step.Name);
        Assert.Equal(3, step.Parts.Count);
        Assert.Equal("Added: red (new), green (new), blue (new)", session.Status.Text);

        await session.UndoAsync();

        Assert.Empty(Carried(store, entry));
        Assert.Empty(session.UndoSteps);
        Assert.Equal(QuickTagStatusKind.UndoneAdd, session.Status.Kind);
        Assert.Equal("red, green, blue", session.Status.Name);
        Assert.Equal(3, store.ReadSnapshot().Tags.Count);
    }

    [Fact]
    public async Task TheStatusNamesTagsThatWereThereAlreadyAndOnlyTheNewOnesAreUndone()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        MediaLibraryEntry entry = Entry("a", 'a');
        using QuickTagSession session = new(QuickTagTarget.For(Single(store, entry)));
        await session.AddAsync("red");

        await session.AddAsync("red green");

        Assert.Equal(QuickTagStatusKind.AddedSeveral, session.Status.Kind);
        Assert.Equal("Added: green (new) · Already there: red", session.Status.Text);
        Assert.Equal(2, session.UndoSteps.Count);

        await session.AddAsync("green, red");

        Assert.Equal("Already there: green, red", session.Status.Text);
        Assert.False(session.Status.IsError);
        Assert.Equal(2, session.UndoSteps.Count);

        await session.UndoAsync();
        Assert.Equal(new[] { "red" }, Carried(store, entry));
    }

    [Fact]
    public async Task AnEntryCountsAsOneStepAgainstTheUndoDepth()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        MediaLibraryEntry entry = Entry("a", 'a');
        using QuickTagSession session = new(QuickTagTarget.For(Single(store, entry)), undoDepth: 2);
        await session.AddAsync("x");
        await session.AddAsync("y");

        await session.AddAsync("a b c d e");

        // The five writes of the entry did not push "y" out before they were joined; only the oldest step is cut afterwards.
        Assert.Equal(2, session.UndoSteps.Count);
        Assert.Equal("y", session.UndoSteps[0].Name);
        Assert.Equal("a, b, c, d, e", session.UndoSteps[1].Name);
    }

    [Fact]
    public async Task AnEntryWithTooManyTagsOrAnInvalidNameWritesNothing()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        MediaLibraryEntry entry = Entry("a", 'a');
        using QuickTagSession session = new(QuickTagTarget.For(Single(store, entry)));

        await session.AddAsync(string.Join(' ', Enumerable.Range(0, TagEntryParser.MaximumNames + 1).Select(index => "t" + index)));

        Assert.Equal(QuickTagStatusKind.Error, session.Status.Kind);
        Assert.Equal(Texts.T("tags.entry.tooMany"), session.Status.Text);
        Assert.Empty(store.ReadSnapshot().Tags);
        Assert.Empty(session.UndoSteps);

        // One name of the entry is too long: none of the others is written either.
        await session.AddAsync("fine, " + new string('x', 200));
        Assert.Equal(Texts.T("tags.error.invalidName"), session.Status.Text);
        Assert.Empty(store.ReadSnapshot().Tags);
    }

    [Fact]
    public async Task TheOwnTagLimitInTheMiddleOfAnEntryKeepsTheEarlierTagsAsOneUndoStep()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease, new PrivateTagLimits { TagLimit = PrivateTagLimits.MinimumTagLimit });
        for (int index = 0; index < PrivateTagLimits.MinimumTagLimit - 1; index++) store.Define("t" + index);
        MediaLibraryEntry entry = Entry("a", 'a');
        using QuickTagSession session = new(QuickTagTarget.For(Single(store, entry)));

        await session.AddAsync("n1 n2 n3");

        Assert.Equal(QuickTagStatusKind.Error, session.Status.Kind);
        Assert.Equal(Texts.T("tags.error.tagLimit") + " " + Texts.T("tags.entry.kept", "n1 (new)"), session.Status.Text);
        Assert.Equal(new[] { "n1" }, Carried(store, entry));
        Assert.Equal("n1", Assert.Single(session.UndoSteps).Name);

        await session.UndoAsync();
        Assert.Empty(Carried(store, entry));
    }

    // ---- the tag dialog: its one undo takes the whole entry back ------------------------------------------------------

    [Fact]
    public async Task TheDialogSessionJoinsTheWritesOfAnEntryIntoItsOneUndo()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        MediaLibraryEntry entry = Entry("a", 'a');
        MediaTagSession session = Single(store, entry);
        TagEntry parsed = TagEntryParser.Parse("a, b; c", session.Snapshot);

        await TagEntryParser.AddEachAsync(parsed, text => session.AddAsync(text), id => session.AddExistingAsync(id),
            () => session.MessageKey != null, () => session.LastChange, session.ReplaceLastChange);

        Assert.Equal(new[] { "a", "b", "c" }, Carried(store, entry));
        TagDialogChange change = session.LastChange!;
        Assert.Equal("a, b, c", change.Name);
        Assert.Equal(3, change.Parts.Count);

        await session.UndoAsync();

        Assert.True(session.Undone);
        Assert.Empty(Carried(store, entry));
        Assert.Null(session.LastChange);
    }

    [Fact]
    public async Task TheDialogRefusesATooLongEntryBeforeAnyWrite()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        MediaTagSession session = Single(store, Entry("a", 'a'));

        session.Refuse("tags.entry.tooMany");

        Assert.Equal("tags.entry.tooMany", session.MessageKey);
        Assert.Empty(store.ReadSnapshot().Tags);
        // The next real attempt clears the refusal.
        await session.AddAsync("ok");
        Assert.Null(session.MessageKey);
    }

    // ---- the stamp field of the tagging page -------------------------------------------------------------------------

    private static async Task<DesktopSessionHost> StartAsync(string directory)
    {
        DesktopSessionHost host = new(directory, Array.Empty<IImageBoard>());
        await host.StartAsync();
        return host;
    }

    [Fact]
    public async Task TheStampFieldTakesSeveralTagsAtOnceUpToItsSizeAndSaysWhatDidNotFit()
    {
        using TempDirectory temp = new();
        await using DesktopSessionHost host = await StartAsync(temp.Path);
        TaggingQueueController work = new(host);
        await work.LoadAsync();

        Assert.True(await work.AddStampAsync("surface, wip; SURFACE", 3));
        Assert.Equal(new[] { "surface", "wip" }, host.GetTagQueue()!.Stamp);
        Assert.Equal("“surface, wip” added to the stamp.", work.Status);

        Assert.True(await work.AddStampAsync("wip third fourth", 3));
        Assert.Equal(new[] { "surface", "wip", "third" }, host.GetTagQueue()!.Stamp);
        Assert.Equal("“third” added to the stamp. · Already there: wip · Not added, the stamp is full (limit: 3): fourth", work.Status);

        Assert.False(await work.AddStampAsync("surface wip", 3));
        Assert.Equal("“surface, wip” is already in the stamp.", work.Status);
        Assert.False(await work.AddStampAsync("fifth sixth", 3));
        Assert.Equal("The stamp is full (limit: 3). Remove a tag from it first.", work.Status);
        Assert.Equal(new[] { "surface", "wip", "third" }, host.GetTagQueue()!.Stamp);

        Assert.False(await work.AddStampAsync(string.Join(' ', Enumerable.Range(0, TagEntryParser.MaximumNames + 1).Select(index => "t" + index)), 18));
        Assert.Equal(Texts.T("tags.entry.tooMany"), work.Status);
    }
}
