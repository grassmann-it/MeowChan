using System.Runtime.CompilerServices;
using Avalonia.Controls;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;
using MeowChan.Transfer;

namespace MeowChan.DesktopTests;

// TP4 (PLAN-SCHNELL-TAGGEN-2026-11-14, decision E4): marking in the gallery. The mark set and its cap, the tri-state selection over the
// marked media, one write for all of them, an undo that reverts exactly what its step changed even after the marks changed, the controller
// that turns marks into a selection target, the texts in twelve languages and the source contracts of the hooks. The viewer and the
// panel need a platform (the smokes cover them); everything else is pinned here against a real tag store.
[Collection("Localization")]
public sealed class GalleryMarkingTests : IDisposable
{
    public GalleryMarkingTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static ResourceId Thread(string id) => new("source", "board", id);
    // 64 hex digits, a different one for every character the tests use (letters past "f" included).
    private static string Hex(char value) => string.Concat(Enumerable.Repeat(((int)value).ToString("x2"), 32));

    private static MediaTagSubject Subject(string thread, char sha)
    {
        Assert.True(PrivateMediaTagTarget.TryCreate(new AttachmentRevisionReference(Thread(thread), "1", 0, "att-v2:" + Hex(sha)),
            "m", "original", TransferMediaRole.Original, Hex(sha), out PrivateMediaTagTarget? target, out _));
        return new MediaTagSubject.Bound(target!);
    }

    private static PrivateTagLoadSnapshot Load(PrivateTagStore store) => new(PrivateTagLoadState.Ready, store.ReadSnapshot(), null);

    private static MediaSelectionTagSession Selection(PrivateTagStore store, IEnumerable<MediaTagSubject> subjects) =>
        MediaSelectionTagSession.ForSubjects(subjects, () => Load(store),
            (name, many, revision, token) => Task.FromResult(store.DefineAndSetMediaSubjects(name, many.ToArray(), revision, token)),
            (tag, many, assigned, revision, token) => Task.FromResult(store.SetMediaSubjects(tag, many.ToArray(), assigned, revision, token)));

    private static MediaTagSession Single(PrivateTagStore store, MediaTagSubject subject) =>
        new(subject, () => Load(store),
            (name, one, token) => Task.FromResult(store.DefineAndSetMediaSubjects(name, [one], null, token)),
            (tag, one, assigned, token) => Task.FromResult(store.SetMediaSubjects(tag, [one], assigned, null, token)));

    private static IReadOnlyList<string> NamesOn(PrivateTagStore store, MediaTagSubject subject) =>
        store.ReadSnapshot().TagsOf(subject).Select(tag => tag.Name.Display).Order(StringComparer.Ordinal).ToArray();

    // ---- the mark set and its cap -------------------------------------------------------------------------------

    [Fact]
    public void MarksKeepTheirOrderCountEachMediumOnceAndCountEveryChange()
    {
        MediaTagSubject a = Subject("a", 'a'), b = Subject("b", 'b'), c = Subject("c", 'c');
        GalleryMarks marks = new();
        Assert.Equal(0, marks.Count);
        Assert.Equal(GalleryMarkOutcome.Marked, marks.Mark(a));
        Assert.Equal(GalleryMarkOutcome.Marked, marks.Mark(b));
        Assert.Equal(GalleryMarkOutcome.AlreadyMarked, marks.Mark(a));
        Assert.Equal(2, marks.Count);
        Assert.Equal(2, marks.Revision); // a refused or repeated mark changes nothing
        Assert.Equal(new[] { a, b }, marks.Snapshot());
        Assert.True(marks.IsMarked(a));
        Assert.False(marks.IsMarked(c));

        Assert.Equal(GalleryMarkOutcome.NotMarked, marks.Unmark(c));
        Assert.Equal(2, marks.Revision);
        Assert.Equal(GalleryMarkOutcome.Unmarked, marks.Unmark(a));
        Assert.Equal(new[] { b }, marks.Snapshot());
        // A snapshot is a copy: later marks never change a target that was made from it.
        IReadOnlyList<MediaTagSubject> before = marks.Snapshot();
        marks.Mark(c);
        Assert.Single(before);
        Assert.Equal(new[] { b, c }, marks.Snapshot());

        Assert.Equal(2, marks.Clear());
        Assert.Equal(0, marks.Clear());
        Assert.Equal(0, marks.Count);
        Assert.Empty(marks.Snapshot());
    }

    [Fact]
    public void TheMarkedMediaLimitIsASettingWithDefaultMinimumAndMaximumAndIsClampedNeverRefused()
    {
        Assert.Equal((500, 10, 5000), (QuickTagOptions.DefaultMarkedMediaLimit, QuickTagOptions.MinimumMarkedMediaLimit, QuickTagOptions.MaximumMarkedMediaLimit));
        Assert.Equal(500, QuickTagOptions.Default.MarkedMediaLimit);
        Assert.Equal(500, new GalleryMarks().Limit);
        Assert.Equal(10, new GalleryMarks(0).Limit);
        Assert.Equal(10, new GalleryMarks(-5).Limit);
        Assert.Equal(10, new GalleryMarks(9).Limit);
        Assert.Equal(10, new GalleryMarks(10).Limit);
        Assert.Equal(5000, new GalleryMarks(5000).Limit);
        Assert.Equal(5000, new GalleryMarks(int.MaxValue).Limit);
        Assert.Equal(1234, new GalleryMarks(1234).Limit);
        // The options normalise it the same way, and one tag write never has to accept more than the store holds.
        Assert.Equal(10, new QuickTagOptions { MarkedMediaLimit = 1 }.Normalized().MarkedMediaLimit);
        Assert.Equal(5000, new QuickTagOptions { MarkedMediaLimit = 99999 }.Normalized().MarkedMediaLimit);
        Assert.Equal(777, new QuickTagOptions { MarkedMediaLimit = 777 }.Normalized().MarkedMediaLimit);
        Assert.True(QuickTagOptions.MaximumMarkedMediaLimit <= PrivateTagStore.MaximumMediaAssignments);
        Assert.True(QuickTagOptions.ClampMarkedMediaLimit(int.MaxValue) <= PrivateTagStore.MaximumMediaAssignments);
    }

    [Fact]
    public void ReachingTheLimitRefusesNewMarksAndKeepsTheOnesSetAndALoweredLimitDoesNotDropAny()
    {
        GalleryMarks marks = new(10);
        for (int index = 0; index < 10; index++) Assert.Equal(GalleryMarkOutcome.Marked, marks.Mark(Subject("t" + index, (char)('a' + index))));
        Assert.Equal(GalleryMarkOutcome.LimitReached, marks.Mark(Subject("extra", 'z')));
        Assert.Equal(10, marks.Count);
        int revision = marks.Revision;
        Assert.Equal(GalleryMarkOutcome.LimitReached, marks.Mark(Subject("extra", 'z')));
        Assert.Equal(revision, marks.Revision);
        // An already marked medium is "already marked", not "limit reached", even at the limit.
        Assert.Equal(GalleryMarkOutcome.AlreadyMarked, marks.Mark(Subject("t0", 'a')));

        // A raised limit allows more; a lowered one keeps what is marked and only refuses new marks.
        marks.Limit = 11;
        Assert.Equal(GalleryMarkOutcome.Marked, marks.Mark(Subject("extra", 'z')));
        marks.Limit = 10;
        Assert.Equal(11, marks.Count);
        Assert.Equal(GalleryMarkOutcome.LimitReached, marks.Mark(Subject("more", 'y')));
        Assert.Equal(GalleryMarkOutcome.Unmarked, marks.Unmark(Subject("extra", 'z')));
        Assert.Equal(GalleryMarkOutcome.Unmarked, marks.Unmark(Subject("t1", 'b')));
        Assert.Equal(GalleryMarkOutcome.Marked, marks.Mark(Subject("more", 'y'))); // room again: 9 + 1
    }

    // ---- tri-state, apply and remove over the selection ---------------------------------------------------------

    [Fact]
    public async Task ATagOnSomeOfTheMarkedMediaIsTriStateAndOneToggleGivesItToAllThenTakesItOffAll()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        MediaTagSubject a = Subject("a", 'a'), b = Subject("b", 'b'), c = Subject("c", 'c'), outside = Subject("d", 'd');
        TagId red = store.Define("red").Tag, blue = store.Define("blue").Tag, green = store.Define("green").Tag;
        store.SetMediaSubjects(red, [a, b], true, null);   // on two of three: some
        store.SetMediaSubjects(blue, [a, b, c], true, null); // on all marked
        store.SetMediaSubjects(green, [outside], true, null); // only on an unmarked medium: none
        using QuickTagSession session = new(QuickTagTarget.For(Selection(store, [a, b, c])));

        Assert.True(session.IsSelection);
        Assert.Equal(TagToggleState.Some, session.StateOf(red));
        Assert.Equal(TagToggleState.All, session.StateOf(blue));
        Assert.Equal(TagToggleState.None, session.StateOf(green));
        Assert.Equal((2, 3), (session.Coverage().Carriers[red], session.Coverage().Total));
        QuickTagAssignment[] assignments = session.Assignments().ToArray();
        Assert.Equal(new[] { "blue", "red" }, assignments.Select(item => item.Tag.Name.Display).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(assignments, item => item.Tag.Id == green); // a tag on none of the marked media is no chip
        Assert.Equal(TagToggleState.Some, assignments.Single(item => item.Tag.Id == red).State);
        Assert.Equal(new[] { blue }, session.OnAll()); // the field does not suggest what all carry; red can still be typed to give it to all

        await session.ToggleAsync(red); // some -> all
        Assert.Equal(TagToggleState.All, session.StateOf(red));
        Assert.All(new[] { a, b, c }, subject => Assert.Contains("red", NamesOn(store, subject)));
        Assert.DoesNotContain("red", NamesOn(store, outside));

        await session.ToggleAsync(red); // all -> none
        Assert.Equal(TagToggleState.None, session.StateOf(red));
        Assert.All(new[] { a, b, c }, subject => Assert.DoesNotContain("red", NamesOn(store, subject)));
    }

    [Fact]
    public async Task AddingANameGivesItToEveryMarkedMediumAndRemovingTakesItOffAllAndNeverTouchesAnUnmarkedOne()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        MediaTagSubject a = Subject("a", 'a'), b = Subject("b", 'b'), c = Subject("c", 'c'), unmarked = Subject("d", 'd');
        TagId cat = store.Define("cat").Tag;
        store.SetMediaSubjects(cat, [unmarked], true, null);
        using QuickTagSession session = new(QuickTagTarget.For(Selection(store, [a, b, c])));
        List<TagDialogChange> applied = new();
        session.Applied += applied.Add;

        await session.AddAsync(" cute "); // a new name: created and given to all marked, in one write
        Assert.All(new[] { a, b, c }, subject => Assert.Equal(new[] { "cute" }, NamesOn(store, subject)));
        Assert.Empty(NamesOn(store, unmarked).Where(name => name == "cute"));
        Assert.Equal("Added “cute” to 3 occurrences.", session.Status.Text);
        Assert.Equal(3, applied.Single().Subjects.Count);

        await session.AddAsync("cat"); // an existing tag that one unmarked medium already has
        Assert.All(new[] { a, b, c }, subject => Assert.Equal(new[] { "cat", "cute" }, NamesOn(store, subject)));
        Assert.Equal(new[] { "cat" }, NamesOn(store, unmarked));

        await session.RemoveAsync(cat); // taken off all marked ones; the unmarked one keeps it
        Assert.All(new[] { a, b, c }, subject => Assert.Equal(new[] { "cute" }, NamesOn(store, subject)));
        Assert.Equal(new[] { "cat" }, NamesOn(store, unmarked));
        Assert.Equal("Removed “cat” from 3 occurrences.", session.Status.Text);
    }

    // ---- undo uses what the step changed, not what is marked now ------------------------------------------------

    [Fact]
    public async Task AnUndoRevertsExactlyTheMediaTheStepChangedEvenAfterTheMarksChangedAndTheTargetWasMadeAgain()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        MediaTagSubject a = Subject("a", 'a'), b = Subject("b", 'b'), c = Subject("c", 'c');
        TagId cat = store.Define("cat").Tag;
        store.SetMediaSubjects(cat, [c], true, null); // c carried it before, and is marked only later
        GalleryMarks marks = new();
        marks.Mark(a); marks.Mark(b);
        using QuickTagSession session = new(QuickTagTarget.For(Selection(store, marks.Snapshot())));

        await session.AddAsync("cat"); // gives cat to a and b (c is not marked yet)
        TagDialogChange step = Assert.Single(session.UndoSteps);
        Assert.Equal(new[] { a, b }, step.Subjects);
        Assert.All(new[] { a, b, c }, subject => Assert.Equal(new[] { "cat" }, NamesOn(store, subject)));

        // The marks grow and the panel makes a new target for them, as the gallery does after Shift+Page Down.
        marks.Mark(c);
        session.Retarget(QuickTagTarget.For(Selection(store, marks.Snapshot())));
        Assert.Equal(TagToggleState.All, session.StateOf(cat));

        await session.UndoAsync();
        Assert.Empty(session.UndoSteps);
        Assert.Empty(NamesOn(store, a));
        Assert.Empty(NamesOn(store, b));
        Assert.Equal(new[] { "cat" }, NamesOn(store, c)); // c was not part of the step: it keeps what it had
        Assert.Equal(QuickTagStatusKind.UndoneAdd, session.Status.Kind);
    }

    [Fact]
    public async Task AnUndoAfterTheMarksWereClearedAndThePanelIsOnASingleMediumStillRevertsTheMarkedOnesOnly()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        MediaTagSubject a = Subject("a", 'a'), b = Subject("b", 'b'), shown = Subject("s", 's');
        using QuickTagSession session = new(QuickTagTarget.For(Selection(store, [a, b])));
        await session.AddAsync("cat");
        session.Retarget(QuickTagTarget.For(Single(store, shown)));
        await session.AddAsync("dog"); // a later step on the single medium

        await session.UndoAsync();     // reverts dog on the shown medium
        Assert.Empty(NamesOn(store, shown));
        Assert.Equal(new[] { "cat" }, NamesOn(store, a));
        await session.UndoAsync();     // reverts the selection step on exactly a and b
        Assert.Empty(NamesOn(store, a));
        Assert.Empty(NamesOn(store, b));
        Assert.Empty(NamesOn(store, shown));
    }

    [Fact]
    public async Task AnUndoOfARemovalGivesTheTagBackOnlyToTheMediaThatCarriedIt()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        MediaTagSubject a = Subject("a", 'a'), b = Subject("b", 'b'), c = Subject("c", 'c');
        TagId red = store.Define("red").Tag;
        store.SetMediaSubjects(red, [a, b], true, null);
        using QuickTagSession session = new(QuickTagTarget.For(Selection(store, [a, b, c])));

        await session.RemoveAsync(red); // only a and b carried it
        Assert.Equal(new[] { a, b }, Assert.Single(session.UndoSteps).Subjects);
        await session.UndoAsync();
        Assert.Equal(new[] { "red" }, NamesOn(store, a));
        Assert.Equal(new[] { "red" }, NamesOn(store, b));
        Assert.Empty(NamesOn(store, c)); // the undo never reaches the medium that did not have it
    }

    [Fact]
    public async Task ASelectionSessionCopiesItsSubjectsSoALiveListOfMarksCannotChangeItsWritesOrItsUndo()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        MediaTagSubject a = Subject("a", 'a'), b = Subject("b", 'b'), c = Subject("c", 'c');
        List<MediaTagSubject> live = [a, b, a]; // a duplicate counts once
        MediaSelectionTagSession session = Selection(store, live);
        Assert.Equal(new[] { a, b }, session.Subjects());
        Assert.Equal(2, session.Count);

        live.Add(c); live.RemoveAt(0);          // the caller's list keeps changing
        Assert.Equal(new[] { a, b }, session.Subjects());
        await session.AddAsync("cat");
        Assert.Equal(new[] { "cat" }, NamesOn(store, a));
        Assert.Equal(new[] { "cat" }, NamesOn(store, b));
        Assert.Empty(NamesOn(store, c));
        Assert.Equal(new[] { a, b }, session.LastChange!.Subjects);

        live.Clear();                           // even an emptied list leaves the undo as it was
        await session.UndoAsync();
        Assert.Empty(NamesOn(store, a));
        Assert.Empty(NamesOn(store, b));
        Assert.True(session.Undone);
    }

    // ---- the controller: marks become a selection target --------------------------------------------------------

    private static GalleryEntry GalleryEntryOf(string thread, char sha, string file)
    {
        string directory = Path.Combine(Path.GetTempPath(), "gallery-marking-fixture");
        MediaAssetReference asset = new(Hex(sha), file, "original", file, Path.Combine(directory, file), 3, Hex(sha), "https://offline.invalid/" + file, file, true);
        MediaAssetSnapshot snapshot = new(directory, MediaManifestStatus.Available, 3, "fixture", [asset]);
        return new GalleryEntry(new GalleryAssetKey(Thread(thread), directory, asset.AssetId), snapshot, asset, null, null, false, false);
    }

    private sealed class Owner(PrivateTagStore store) : IGalleryTagPanelOwner
    {
        internal readonly Dictionary<string, MediaTagSubject?> Subjects = new();
        internal readonly List<GalleryEntry> List = new();
        internal int Index;
        internal int SelectionOpens;
        internal QuickTagOptions Limits = QuickTagOptions.Default;
        public MediaTagSubject? SubjectOf(GalleryEntry entry, out string? unavailableReason)
        {
            MediaTagSubject? subject = Subjects[entry.Key.Thread.Thread];
            unavailableReason = subject == null ? "This medium is not bound to a post version." : null;
            return subject;
        }
        public IQuickTagTarget? OpenTarget(GalleryEntry entry, out string? unavailableReason) =>
            SubjectOf(entry, out unavailableReason) is { } subject ? QuickTagTarget.For(Single(store, subject)) : null;
        public IQuickTagTarget? OpenSelectionTarget(IReadOnlyList<MediaTagSubject> subjects, out string? unavailableReason)
        {
            SelectionOpens++;
            unavailableReason = null;
            return QuickTagTarget.For(Selection(store, subjects));
        }
        public IReadOnlyList<GalleryEntry> Entries => List;
        public GalleryTagPanelPosition? Position => Index >= 0 && Index < List.Count ? new GalleryTagPanelPosition(Index + 1, List.Count, false) : null;
        public TagRecentList Recent { get; } = new();
        public QuickTagOptions Options => Limits;
        public TagContextOptions ContextOptions => TagContextOptions.Default;
        public BooruCatalogSnapshot? ReadSourceTags() => null;
        public GalleryThreadTagModel OpenThreadTags(ResourceId thread) => throw new NotSupportedException();
        public void OpenAdvanced(GalleryEntry entry) { }
    }

    private static (Owner Owner, GalleryEntry[] Entries) ThreeMedia(PrivateTagStore store)
    {
        GalleryEntry[] entries = [GalleryEntryOf("a", 'a', "one.png"), GalleryEntryOf("b", 'b', "two.png"), GalleryEntryOf("c", 'c', "three.png")];
        Owner owner = new(store);
        owner.List.AddRange(entries);
        owner.Subjects["a"] = Subject("a", 'a');
        owner.Subjects["b"] = Subject("b", 'b');
        owner.Subjects["c"] = Subject("c", 'c');
        return (owner, entries);
    }

    [Fact]
    public async Task MarkingAndUnmarkingWithNavigationBuildsAndShrinksTheSelectionAndTheTargetFollowsTheMarks()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        (Owner owner, GalleryEntry[] entries) = ThreeMedia(store);
        using GalleryTagPanelController controller = GalleryTagPanelController.Create(owner, entries[0]);
        Assert.False(controller.Session.IsSelection); // no marks: the shown medium alone

        // Shift+Page Down on medium 1: mark it, then the gallery moves to medium 2.
        Assert.Equal(entries[0], controller.ShownEntry());
        GalleryMarkResult first = controller.Mark(controller.ShownEntry());
        Assert.Equal((GalleryMarkOutcome.Marked, 1, "one.png"), (first.Outcome, first.Count, first.Label));
        owner.Index = 1;
        GalleryTagPanelFrame frame = controller.Frame(entries[0]); // the viewer retargets at once, before the move arrives ...
        controller.Session.Retarget(frame.Target);
        Assert.True(controller.Session.IsSelection);
        Assert.Equal("1 media file", frame.Label);
        Assert.Equal("Medium 2 of 3", controller.CounterFor(controller.ShownEntry())); // ... and names the medium it is moving to: not marked
        frame = controller.Frame(entries[1]); // the move arrived
        Assert.Equal("Medium 2 of 3", frame.Counter);

        // ... and again on medium 2, then medium 3.
        Assert.Equal(entries[1], controller.ShownEntry());
        Assert.Equal(GalleryMarkOutcome.Marked, controller.Mark(controller.ShownEntry()).Outcome);
        owner.Index = 2;
        frame = controller.Frame(entries[2]);
        controller.Session.Retarget(frame.Target);
        Assert.Equal("2 media files", frame.Label);
        Assert.Equal(2, controller.Marks.Count);
        Assert.Equal(new[] { "one.png", "two.png" }.Length, controller.Session.Coverage().Total);

        // A tag now goes to both marked media, not to the shown third one.
        await controller.Session.AddAsync("cat");
        Assert.Equal(new[] { "cat" }, NamesOn(store, owner.Subjects["a"]!));
        Assert.Equal(new[] { "cat" }, NamesOn(store, owner.Subjects["b"]!));
        Assert.Empty(NamesOn(store, owner.Subjects["c"]!));

        // Shift+Page Up on medium 3: go back to medium 2 and unmark it.
        Assert.Equal(entries[1], controller.PreviousEntry());
        GalleryMarkResult removed = controller.Unmark(controller.PreviousEntry()!);
        Assert.Equal((GalleryMarkOutcome.Unmarked, 1), (removed.Outcome, removed.Count));
        owner.Index = 1;
        frame = controller.Frame(entries[1]);
        controller.Session.Retarget(frame.Target);
        Assert.Equal("1 media file", frame.Label);
        Assert.Equal("Medium 2 of 3", frame.Counter);
        // Unmarking one that is not marked changes nothing; at the start there is no previous medium.
        Assert.Equal(GalleryMarkOutcome.NotMarked, controller.Unmark(entries[1]).Outcome);
        owner.Index = 0;
        Assert.Null(controller.PreviousEntry());
        Assert.Equal("Medium 1 of 3 · marked", controller.CounterFor(entries[0]));
        Assert.True(controller.IsMarked(entries[0]));
        Assert.False(controller.IsMarked(entries[1]));
    }

    [Fact]
    public void PagingKeepsOneSelectionTargetPerMarksRevisionAndAMarkChangeMakesANewOne()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        (Owner owner, GalleryEntry[] entries) = ThreeMedia(store);
        using GalleryTagPanelController controller = GalleryTagPanelController.Create(owner, entries[0]);
        controller.Mark(entries[0]);
        owner.Index = 1;
        IQuickTagTarget made = controller.Frame(entries[1]).Target;
        owner.Index = 2;
        Assert.Same(made, controller.Frame(entries[2]).Target); // paging does not rebuild a selection of up to 5000 subjects
        Assert.Equal(1, owner.SelectionOpens);
        controller.Mark(entries[1]);
        Assert.NotSame(made, controller.Frame(entries[2]).Target);
        Assert.Equal(2, owner.SelectionOpens);

        Assert.Equal(2, controller.ClearMarks());
        IQuickTagTarget single = controller.Frame(entries[2]).Target;
        Assert.False(single.IsSelection);
        Assert.Equal("three.png", controller.Session.Label);
        Assert.Equal("Medium 3 of 3", controller.CounterFor(entries[2]));
    }

    [Fact]
    public void AMediumWithoutATagTargetIsRefusedWithItsReasonAndTheLimitComesFromTheOptions()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        (Owner owner, GalleryEntry[] entries) = ThreeMedia(store);
        owner.Subjects["b"] = null;
        owner.Limits = new QuickTagOptions { MarkedMediaLimit = 1 }; // clamped up to the minimum of 10
        using GalleryTagPanelController controller = GalleryTagPanelController.Create(owner, entries[0]);

        GalleryMarkResult refused = controller.Mark(entries[1]);
        Assert.Equal((GalleryMarkOutcome.Unavailable, 0, "two.png"), (refused.Outcome, refused.Count, refused.Label));
        Assert.Equal("This medium is not bound to a post version.", refused.Reason);
        Assert.False(refused.Changed);
        Assert.Equal("Not marked: “two.png”. This medium is not bound to a post version.", GalleryTagPanelCommands.MarkAnnouncement(refused));
        Assert.Equal(GalleryMarkOutcome.NotMarked, controller.Unmark(entries[1]).Outcome);
        Assert.Equal(0, controller.Marks.Count);
        Assert.Equal(10, controller.Marks.Limit);

        // The setting is read at every mark: a changed limit applies to the open panel.
        owner.Limits = new QuickTagOptions { MarkedMediaLimit = 5000 };
        controller.Mark(entries[0]);
        Assert.Equal(5000, controller.Marks.Limit);
        owner.Limits = new QuickTagOptions { MarkedMediaLimit = 10 };
        Assert.Equal(GalleryMarkOutcome.AlreadyMarked, controller.Mark(entries[0]).Outcome);
        Assert.Equal(10, controller.Marks.Limit);
    }

    [Fact]
    public void TheLimitReachedAnnouncementNamesTheLimitAndTheMarkTextsAreSaidInBothLanguages()
    {
        GalleryMarkResult Result(GalleryMarkOutcome outcome, int count = 3, int limit = 500, string? reason = null) =>
            new(outcome, "a.png", count, limit, reason);
        Assert.Equal("Marked “a.png”. Marked in all: 3.", GalleryTagPanelCommands.MarkAnnouncement(Result(GalleryMarkOutcome.Marked)));
        Assert.Equal("Mark removed from “a.png”. Marked in all: 2.", GalleryTagPanelCommands.MarkAnnouncement(Result(GalleryMarkOutcome.Unmarked, 2)));
        Assert.Equal("“a.png” is already marked. Marked in all: 3.", GalleryTagPanelCommands.MarkAnnouncement(Result(GalleryMarkOutcome.AlreadyMarked)));
        Assert.Equal("Marking limit reached (10). Clear marks or raise the limit in the settings.", GalleryTagPanelCommands.MarkAnnouncement(Result(GalleryMarkOutcome.LimitReached, 10, 10)));
        Assert.Null(GalleryTagPanelCommands.MarkAnnouncement(Result(GalleryMarkOutcome.NotMarked)));
        Assert.Equal("All marks cleared.", GalleryTagPanelCommands.ClearedAnnouncement());
        Assert.Equal("Medium 4 of 27 · marked", GalleryTagPanelCommands.CounterText(new GalleryTagPanelPosition(4, 27, false), marked: true));
        Assert.Equal("Medium 4 of 27", GalleryTagPanelCommands.CounterText(new GalleryTagPanelPosition(4, 27, false)));
        Assert.Equal("3 media files", GalleryTagPanelCommands.SelectionLabel(3));

        Texts.SetLanguage("de");
        Assert.Equal("„a.png“ markiert. Insgesamt markiert: 3.", GalleryTagPanelCommands.MarkAnnouncement(Result(GalleryMarkOutcome.Marked)));
        Assert.Equal("Markierungslimit erreicht (10). Markierungen löschen oder das Limit in den Einstellungen erhöhen.",
            GalleryTagPanelCommands.MarkAnnouncement(Result(GalleryMarkOutcome.LimitReached, 10, 10)));
        Assert.Equal("Medium 4 von 27 · markiert", GalleryTagPanelCommands.CounterText(new GalleryTagPanelPosition(4, 27, false), marked: true));
        Assert.Equal("3 Medien", GalleryTagPanelCommands.SelectionLabel(3));
    }

    // ---- the surface: chip, clear button, status notice ---------------------------------------------------------

    [Fact]
    public async Task TheMarkedChipShowsTheCountWithAClearButtonAndAnnouncesThroughThePoliteStatusLine()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        MediaTagSubject a = Subject("a", 'a'), b = Subject("b", 'b');
        using QuickTagSession session = new(QuickTagTarget.For(Selection(store, [a, b])));
        using QuickTagSurface surface = new(session, platform: ShortcutPlatform.Linux) { TargetLabel = "2 media files" };
        Assert.False(surface.MarkBar.IsVisible);

        surface.MarkedCount = 3;
        Assert.True(surface.MarkBar.IsVisible);
        Assert.Equal("3 marked", ((TextBlock)surface.MarkedBadge.Child!).Text);
        Assert.Equal("3 marked", Avalonia.Automation.AutomationProperties.GetName(surface.MarkedBadge));
        Assert.Equal(Avalonia.Automation.AutomationLiveSetting.Polite, Avalonia.Automation.AutomationProperties.GetLiveSetting(surface.MarkedBadge));
        Assert.Equal("Clear marks", surface.MarkedClearButton.Content);
        Assert.Equal("Clear all marks", Avalonia.Automation.AutomationProperties.GetName(surface.MarkedClearButton));
        bool cleared = false;
        surface.MarksClearRequested += () => cleared = true;
        surface.MarkedClearButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.True(cleared);

        // A notice is said once in the status line; the next accepted change replaces it, and an error is never hidden by it.
        surface.Announce("Marked “a.png”. Marked in all: 3.");
        Assert.Equal("Marked “a.png”. Marked in all: 3.", surface.Status.Text);
        Assert.True(surface.Status.IsVisible);
        Assert.Equal(Avalonia.Automation.AutomationLiveSetting.Polite, Avalonia.Automation.AutomationProperties.GetLiveSetting(surface.Status));
        await session.AddAsync("cat");
        surface.Refresh();
        Assert.StartsWith("Added “cat”", surface.Status.Text);

        surface.MarkedCount = 0;
        Assert.False(surface.MarkBar.IsVisible);
        surface.Announce(null);
        Assert.StartsWith("Added “cat”", surface.Status.Text);
    }

    // ---- texts and source contracts -----------------------------------------------------------------------------

    [Fact]
    public void EveryMarkTextExistsInTheTwelveLanguagesWithItsPlaceholders()
    {
        string[] keys = GalleryMarkCatalog.Entries.Select(entry => entry.Key).ToArray();
        Assert.Equal(10, keys.Length);
        Assert.Equal(keys.Length, keys.Distinct().Count());
        foreach (string key in keys)
        {
            Assert.True(BuiltInCatalog.English.ContainsKey(key), key);
            Assert.True(BuiltInCatalog.German.ContainsKey(key), key);
        }
        string packs = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Root())!, "../../Desktop/Localization/Packs"));
        string[] files = Directory.GetFiles(packs, "*.json");
        Assert.Equal(10, files.Length);
        foreach (string file in files)
        {
            using System.Text.Json.JsonDocument pack = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
            System.Text.Json.JsonElement strings = pack.RootElement.GetProperty("strings");
            foreach (string key in keys)
            {
                Assert.True(strings.TryGetProperty(key, out System.Text.Json.JsonElement text), Path.GetFileName(file) + " lacks " + key);
                string value = text.GetString()!;
                Assert.False(string.IsNullOrWhiteSpace(value), key);
                foreach (System.Text.RegularExpressions.Match placeholder in System.Text.RegularExpressions.Regex.Matches(BuiltInCatalog.English[key], @"\{\d+\}"))
                    Assert.Contains(placeholder.Value, value);
            }
        }
        // The counts are numbers in a fixed phrase: no language needs a plural form of these keys.
        foreach (string file in files)
            Assert.DoesNotContain("\"tags.quick.marked.one\"", File.ReadAllText(file));
    }

    [Fact]
    public void TheViewerMarksThroughTheControllerAndTheNamedFilesStayUntouched()
    {
        string host = ReadSource("Desktop/GalleryViewer.TagPanel.cs");
        Assert.Contains("controller.Mark(controller.ShownEntry())", host);
        Assert.Contains("controller.Unmark(previous)", host);
        Assert.Contains("controller.PreviousEntry()", host);
        Assert.Contains("surface.MarksClearRequested += OnTagMarksClearRequested;", host);
        Assert.Contains("surface.MarksClearRequested -= OnTagMarksClearRequested;", host);
        // The marks outlive the panel: they belong to the viewer, not to the controller that is disposed with the slot.
        Assert.Contains("_marks ??= new GalleryMarks(", host);
        Assert.DoesNotContain("Marks", ReadSource("Desktop/GalleryViewer.Overlay.cs"));
        Assert.DoesNotContain("Marks", ReadSource("Desktop/GalleryVideoPlayer.cs"));
        // The commands are wired only here: the ids are in the wired list and the handlers exist.
        Assert.Contains("MarkNext, MarkPrevious, ThreadSection, Undo }", ReadSource("Desktop/Commands/QuickTagCommands.cs"));
    }

    private static string ReadSource(string relative)
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Root())!, "../.."));
        return File.ReadAllText(Path.Combine(root, relative));
    }

    private static string Root([CallerFilePath] string source = "") => source;
}
