using Avalonia.Input;
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

// PT6 (PLAN-POWER-TAGGEN-2026-11-14, decision E2): Shift+1 to Shift+5 rate, Shift+0 removes the rating, R moves to the Post tab's stars (review 15.11.: no dialog any more); the gallery tag panel
// carries a favorite and rating row, and the rating works on the marked media (TP4) as one step with undo. The shortcut defaults and their
// conflict freedom (gallery, media and reader), the yielding rule, the decision session over a fake owner, the controller's
// targets and the shared undo order against a real tag store, the twelve languages and the source contracts of the viewer and window hooks.
// The row and the panel need a platform (the smokes cover them).
[Collection("Localization")]
public sealed class GalleryRatingKeyTests : IDisposable
{
    public GalleryRatingKeyTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static readonly string[] RatingIds =
        { "media.rating.set-1", "media.rating.set-2", "media.rating.set-3", "media.rating.set-4", "media.rating.set-5" };

    private static KeyEventArgs Key(Key key, KeyModifiers modifiers) => new() { Key = key, KeyModifiers = modifiers };

    // ---- the shortcut defaults -------------------------------------------------------------------------------

    public static IEnumerable<object[]> Platforms() => Enum.GetValues<ShortcutPlatform>().Select(platform => new object[] { platform });

    [Theory]
    [MemberData(nameof(Platforms))]
    public void ShiftDigitsRateAndShiftZeroRemovesOnEveryPlatformWithoutConflicts(ShortcutPlatform platform)
    {
        var bindings = EffectiveShortcutBindings.Build(platform: platform);
        bool mac = platform == ShortcutPlatform.MacOS;
        for (int number = 1; number <= 5; number++)
            Assert.Equal(mac ? "⇧" + number : "Shift+" + number, bindings.Display("media.rating.set-" + number));
        Assert.Equal(mac ? "⇧0" : "Shift+0", bindings.Display("media.rating.clear"));
        // R stays the dialog, with no second gesture.
        Assert.Equal("R", bindings.Display("media.rating.edit"));
        Assert.Empty(bindings.Issues);
        foreach (string id in RatingIds.Append("media.rating.clear"))
            foreach (ShortcutGesture gesture in bindings.For(id)) Assert.Null(EffectiveShortcutBindings.InvalidReason(gesture, platform));
    }

    [Fact]
    public void ShiftDigitMatchesTheRatingAndNeitherThePlainDigitNorAChordDoes()
    {
        var bindings = EffectiveShortcutBindings.Build(platform: ShortcutPlatform.Linux);
        Assert.True(bindings.Matches(Key(Avalonia.Input.Key.D4, KeyModifiers.Shift), "media.rating.set-4"));
        Assert.True(bindings.Matches(Key(Avalonia.Input.Key.D0, KeyModifiers.Shift), "media.rating.clear"));
        // The plain digits keep their old readings (tag names such as 2girls, the zoom keys) and no other chord rates.
        Assert.False(bindings.Matches(Key(Avalonia.Input.Key.D4, KeyModifiers.None), "media.rating.set-4"));
        Assert.False(bindings.Matches(Key(Avalonia.Input.Key.D4, KeyModifiers.Control | KeyModifiers.Shift), "media.rating.set-4"));
        Assert.False(bindings.Matches(Key(Avalonia.Input.Key.D4, KeyModifiers.Alt), "media.rating.set-4"));
        Assert.False(bindings.Matches(Key(Avalonia.Input.Key.D0, KeyModifiers.None), "media.rating.clear"));
        // Shift+5 never reads as set-4 and the two commands never share a gesture.
        Assert.False(bindings.Matches(Key(Avalonia.Input.Key.D5, KeyModifiers.Shift), "media.rating.set-4"));
    }

    [Theory]
    [MemberData(nameof(Platforms))]
    public void NoOtherDefaultInTheGalleryTheMediaPageOrTheReaderUsesAShiftDigit(ShortcutPlatform platform)
    {
        var bindings = EffectiveShortcutBindings.Build(platform: platform);
        string[] mine = RatingIds.Append("media.rating.clear").ToArray();
        string[] contexts = { "gallery", "media", "reader" };
        // The effective table rejects both sides of a conflict, so the defaults themselves are compared.
        foreach (string id in mine) Assert.Contains(EffectiveShortcutBindings.ExecutionContexts(id), context => contexts.Contains(context));
        foreach (CommandDefinition command in CommandCatalog.All.Where(command => command.IsImplemented && !mine.Contains(command.Id)))
        {
            if (!mine.Any(id => EffectiveShortcutBindings.ContextsOverlap(id, command.Id))) continue;
            foreach (ShortcutGesture gesture in EffectiveShortcutBindings.Defaults(command, platform))
                foreach (string id in mine)
                    foreach (ShortcutGesture own in EffectiveShortcutBindings.Defaults(CommandCatalog.Get(id), platform))
                        Assert.False(EffectiveShortcutBindings.Equivalent(gesture, own, platform), command.Id + " collides with " + id);
        }
    }

    [Fact]
    public void TheRatingCommandsActInTheGalleryTheMediaPageAndTheReader()
    {
        foreach (string id in RatingIds.Append("media.rating.clear"))
        {
            IReadOnlyList<string> contexts = EffectiveShortcutBindings.ExecutionContexts(id);
            Assert.Contains("gallery", contexts);
            Assert.Contains("media", contexts);
            Assert.Contains("reader", contexts);
            Assert.DoesNotContain("dialog:media-rating", contexts); // the rating modal is gone (review 15.11.)
            Assert.True(CommandCatalog.Get(id).Scope.HasFlag(CommandScope.Gallery));
        }
    }

    [Fact]
    public void TheDefaultsAreYieldingAndTheRevisionCoversThem()
    {
        Assert.True(EffectiveShortcutBindings.DefaultsRevision >= 7);
        foreach (string id in RatingIds.Append("media.rating.clear"))
            Assert.Contains(id, EffectiveShortcutBindings.YieldingDefaultCommandIds);
        // R is an older default and stays as it was (it now moves the focus to the stars of the Post tab).
        Assert.Contains("media.rating.edit", EffectiveShortcutBindings.YieldingDefaultCommandIds);
    }

    [Fact]
    public void ASavedBindingOnAShiftDigitKeepsItAndOnlyThatRatingDefaultYields()
    {
        // The user once put Fit on Shift+3 and Zoom out on Shift+0: both keep their keys, and only the matching ratings give way.
        var section = new ShortcutBindingsSection()
            .Set("gallery.fit", [ShortcutGesture.Parse("Shift+3")])
            .Set("gallery.zoom-out", [ShortcutGesture.Parse("Shift+0")]);
        var bindings = EffectiveShortcutBindings.Build(section, ShortcutPlatform.Linux);
        Assert.Equal("Shift+3", bindings.Display("gallery.fit"));
        Assert.Equal("Shift+0", bindings.Display("gallery.zoom-out"));
        Assert.Empty(bindings.For("media.rating.set-3"));
        Assert.Empty(bindings.For("media.rating.clear"));
        Assert.Equal("Shift+1", bindings.Display("media.rating.set-1"));
        Assert.Equal("Shift+5", bindings.Display("media.rating.set-5"));
        Assert.DoesNotContain(bindings.Issues, issue => issue.BlocksSave);
        Assert.Contains(bindings.Issues, issue => issue.CommandId == "media.rating.set-3" && issue.Reason?.Code == "defaultYields");
        // A profile that never used those keys gets all six.
        Assert.Equal("Shift+3", EffectiveShortcutBindings.Build(new ShortcutBindingsSection(), ShortcutPlatform.Linux).Display("media.rating.set-3"));
    }

    [Fact]
    public void ARatingBindingTheUserSavedIsNeverReplacedByTheNewDefault()
    {
        var section = new ShortcutBindingsSection().Set("media.rating.set-2", [ShortcutGesture.Parse("Primary+Shift+2")]);
        var bindings = EffectiveShortcutBindings.Build(section, ShortcutPlatform.Linux);
        Assert.Equal("Ctrl+Shift+2", bindings.Display("media.rating.set-2"));
    }

    [Fact]
    public void TheSwitchForSingleKeyShortcutsTurnsTheShiftDigitsOffAndSavedBindingsStay()
    {
        var section = new ShortcutBindingsSection().WithSingleKeyShortcuts(false);
        var off = EffectiveShortcutBindings.Build(section, ShortcutPlatform.Linux);
        foreach (string id in RatingIds.Append("media.rating.clear")) Assert.Empty(off.For(id));
        var saved = EffectiveShortcutBindings.Build(section.Set("media.rating.set-1", [ShortcutGesture.Parse("Primary+Shift+1")]), ShortcutPlatform.Linux);
        Assert.Equal("Ctrl+Shift+1", saved.Display("media.rating.set-1"));
    }

    // ---- the decision session over a fake owner --------------------------------------------------------------

    private static ResourceId Thread(string id) => new("source", "board", id);
    private static string Hex(char value) => string.Concat(Enumerable.Repeat(((int)value).ToString("x2"), 32));

    private static GalleryEntry EntryOf(string thread, char sha, string file)
    {
        string directory = Path.Combine(Path.GetTempPath(), "gallery-rating-fixture");
        MediaAssetReference asset = new(Hex(sha), file, "original", file, Path.Combine(directory, file), 3, Hex(sha), "https://offline.invalid/" + file, file, true);
        MediaAssetSnapshot snapshot = new(directory, MediaManifestStatus.Available, 3, "fixture", [asset]);
        return new GalleryEntry(new GalleryAssetKey(Thread(thread), directory, asset.AssetId), snapshot, asset, null, null, false, false);
    }

    private sealed class FakeDecisions : IGalleryDecisionOwner
    {
        internal readonly Dictionary<string, GalleryDecisionState> States = new();
        internal readonly HashSet<string> Refuse = new();
        internal readonly List<string> Writes = new();
        internal Func<Task>? Pause;
        internal bool Throw;
        private static string Id(GalleryEntry entry) => entry.Key.Thread.Thread;
        public GalleryDecisionState? Read(GalleryEntry entry) => States.TryGetValue(Id(entry), out GalleryDecisionState state) ? state : null;
        public async Task SetFavoriteAsync(GalleryEntry entry, bool selected, CancellationToken token)
        {
            Writes.Add("favorite " + Id(entry) + "=" + selected);
            if (Pause != null) await Pause();
            if (Throw) throw new InvalidOperationException("store");
            if (!Refuse.Contains(Id(entry))) States[Id(entry)] = States[Id(entry)] with { Favorite = selected };
        }
        public async Task SetRatingAsync(GalleryEntry entry, int? value, CancellationToken token)
        {
            Writes.Add("rating " + Id(entry) + "=" + (value?.ToString() ?? "none"));
            if (Pause != null) await Pause();
            if (Throw) throw new InvalidOperationException("store");
            if (!Refuse.Contains(Id(entry))) States[Id(entry)] = States[Id(entry)] with { Rating = value };
        }
    }

    private static (FakeDecisions Owner, GalleryEntry[] Entries) Three()
    {
        GalleryEntry[] entries = [EntryOf("a", 'a', "one.png"), EntryOf("b", 'b', "two.png"), EntryOf("c", 'c', "three.png")];
        FakeDecisions owner = new();
        owner.States["a"] = new(false, null);
        owner.States["b"] = new(true, 2);
        owner.States["c"] = new(false, 5);
        return (owner, entries);
    }

    [Fact]
    public void TheViewIsPlainForOneMediumAndCommonOrMixedForSeveral()
    {
        (FakeDecisions owner, GalleryEntry[] entries) = Three();
        using GalleryDecisionSession session = new(owner);
        GalleryDecisionView one = session.View([entries[1]]);
        Assert.Equal((1, GalleryDecisionCoverage.All, (int?)2, false, true), (one.Count, one.Favorite, one.Rating, one.RatingMixed, one.Available));
        GalleryDecisionView mixed = session.View(entries);
        Assert.Equal((3, GalleryDecisionCoverage.Some, (int?)null, true), (mixed.Count, mixed.Favorite, mixed.Rating, mixed.RatingMixed));
        Assert.True(mixed.NextFavorite); // some are favorites: the next click gives it to all
        owner.States["a"] = new(true, 4); owner.States["b"] = new(true, 4); owner.States["c"] = new(true, 4);
        GalleryDecisionView same = session.View(entries);
        Assert.Equal((GalleryDecisionCoverage.All, (int?)4, false), (same.Favorite, same.Rating, same.RatingMixed));
        Assert.False(same.NextFavorite); // all are favorites: the next click takes it off all
        // Media the owner cannot read are left out; none readable is unavailable.
        owner.States.Remove("b");
        Assert.Equal(2, session.View(entries).Count);
        Assert.Same(GalleryDecisionView.Unavailable, session.View([entries[1]]));
        Assert.False(GalleryDecisionView.Unavailable.Available);
    }

    [Fact]
    public async Task RatingOneMediumWritesItOnceAndOneUndoRestoresTheOldRating()
    {
        (FakeDecisions owner, GalleryEntry[] entries) = Three();
        using GalleryDecisionSession session = new(owner);
        GalleryDecisionStatus status = await session.SetRatingAsync([entries[1]], 4);
        Assert.Equal(4, owner.States["b"].Rating);
        Assert.Equal(new[] { "rating b=4" }, owner.Writes);
        Assert.Equal(GalleryDecisionStatusKind.RatingSaved, status.Kind);
        Assert.Equal("Rating 4 of 5 saved.", status.Text());
        GalleryDecisionStep step = Assert.Single(session.UndoSteps);
        Assert.Equal(GalleryDecisionKind.Rating, step.Kind);
        Assert.Equal(2, step.Changes[0].Before.Rating);
        Assert.Equal(4, step.Changes[0].After.Rating);

        GalleryDecisionStatus undone = await session.UndoAsync();
        Assert.Equal(2, owner.States["b"].Rating);
        Assert.Equal(GalleryDecisionStatusKind.UndoneRating, undone.Kind);
        Assert.Equal("Undone: rating change.", undone.Text());
        Assert.Empty(session.UndoSteps);
        Assert.False(session.CanUndo);
    }

    [Fact]
    public async Task ShiftZeroRemovesTheRatingAndTheUndoPutsItBack()
    {
        (FakeDecisions owner, GalleryEntry[] entries) = Three();
        using GalleryDecisionSession session = new(owner);
        GalleryDecisionStatus status = await session.SetRatingAsync([entries[2]], null);
        Assert.Null(owner.States["c"].Rating);
        Assert.Equal("Rating removed.", status.Text());
        await session.UndoAsync();
        Assert.Equal(5, owner.States["c"].Rating);
    }

    [Fact]
    public async Task ASecondRatingOfTheSameValueWritesNothingAndLeavesNoStep()
    {
        (FakeDecisions owner, GalleryEntry[] entries) = Three();
        using GalleryDecisionSession session = new(owner);
        GalleryDecisionStatus status = await session.SetRatingAsync([entries[1]], 2);
        Assert.Empty(owner.Writes);
        Assert.Empty(session.UndoSteps);
        Assert.Equal("Rating 2 of 5 saved.", status.Text());
        Assert.Equal(GalleryDecisionStatus.None, await session.SetRatingAsync([], 3));
    }

    [Fact]
    public async Task MarkedMediaAreRatedAsOneStepAndTheUndoRestoresEachOldValue()
    {
        (FakeDecisions owner, GalleryEntry[] entries) = Three();
        using GalleryDecisionSession session = new(owner);
        GalleryDecisionStatus status = await session.SetRatingAsync(entries, 3);
        Assert.All(owner.States.Values, state => Assert.Equal(3, state.Rating));
        Assert.Equal(3, owner.Writes.Count);
        GalleryDecisionStep step = Assert.Single(session.UndoSteps); // one step, not three
        Assert.Equal(3, step.Changes.Count);
        Assert.Equal("Rating 3 of 5 saved. Applies to all marked media: 3.", status.Text());

        await session.UndoAsync();
        Assert.Null(owner.States["a"].Rating);
        Assert.Equal(2, owner.States["b"].Rating);
        Assert.Equal(5, owner.States["c"].Rating);
        // Favorites were never touched by a rating.
        Assert.Equal((false, true, false), (owner.States["a"].Favorite, owner.States["b"].Favorite, owner.States["c"].Favorite));
    }

    [Fact]
    public async Task OnlyTheMediaThatReallyChangedAreInTheStep()
    {
        (FakeDecisions owner, GalleryEntry[] entries) = Three();
        owner.States["c"] = new(false, 3); // already 3
        using GalleryDecisionSession session = new(owner);
        await session.SetRatingAsync(entries, 3);
        Assert.Equal(new[] { "rating a=3", "rating b=3" }, owner.Writes);
        Assert.Equal(2, session.UndoSteps[0].Changes.Count);
        await session.UndoAsync();
        Assert.Equal(3, owner.States["c"].Rating); // not part of the step, so not touched
        Assert.Null(owner.States["a"].Rating);
    }

    [Fact]
    public async Task AMediumThatRefusesTheWriteIsCountedAndTheOthersStillGetTheirStep()
    {
        (FakeDecisions owner, GalleryEntry[] entries) = Three();
        owner.Refuse.Add("b");
        using GalleryDecisionSession session = new(owner);
        GalleryDecisionStatus status = await session.SetRatingAsync(entries, 1);
        Assert.Equal(GalleryDecisionStatusKind.Failed, status.Kind);
        Assert.Equal(1, status.Skipped);
        Assert.Equal("The change could not be saved. Not changed: 1.", status.Text());
        Assert.Equal(2, Assert.Single(session.UndoSteps).Changes.Count);
        Assert.Equal(2, owner.States["b"].Rating);
        // A throwing store is the same: the owner reports it, the session checks the state it finds.
        owner.Throw = true;
        GalleryDecisionStatus thrown = await session.SetRatingAsync([entries[0]], 5);
        Assert.Equal(GalleryDecisionStatusKind.Failed, thrown.Kind);
        Assert.Equal(1, owner.States["a"].Rating);
    }

    [Fact]
    public async Task NothingReadableIsUnavailableAndWritesNothing()
    {
        FakeDecisions owner = new();
        using GalleryDecisionSession session = new(owner);
        GalleryDecisionStatus status = await session.SetRatingAsync([EntryOf("x", 'x', "x.png")], 3);
        Assert.Equal(GalleryDecisionStatusKind.Unavailable, status.Kind);
        Assert.Empty(owner.Writes);
        Assert.Equal("Favorites and ratings are not available for this medium.", status.Text());
    }

    [Fact]
    public async Task TheFavoriteToggleGivesItToAllOrTakesItOffAllAndEachIsUndoable()
    {
        (FakeDecisions owner, GalleryEntry[] entries) = Three();
        using GalleryDecisionSession session = new(owner);
        GalleryDecisionStatus given = await session.ToggleFavoriteAsync(entries); // some are favorites: all become one
        Assert.All(owner.States.Values, state => Assert.True(state.Favorite));
        Assert.Equal("Saved as a favorite. Applies to all marked media: 3.", given.Text());
        Assert.Equal(2, session.UndoSteps[0].Changes.Count); // b already was
        GalleryDecisionStatus taken = await session.ToggleFavoriteAsync(entries); // all are: all lose it
        Assert.All(owner.States.Values, state => Assert.False(state.Favorite));
        Assert.Equal("Removed from favorites. Applies to all marked media: 3.", taken.Text());
        await session.UndoAsync();
        Assert.All(owner.States.Values, state => Assert.True(state.Favorite));
        await session.UndoAsync();
        Assert.Equal((false, true, false), (owner.States["a"].Favorite, owner.States["b"].Favorite, owner.States["c"].Favorite));
        Assert.Equal(GalleryDecisionStatusKind.UndoneFavorite, session.Status.Kind);
    }

    [Fact]
    public async Task TheUndoGoesBackNewestFirstAndAFavoriteStepNeverResetsARating()
    {
        (FakeDecisions owner, GalleryEntry[] entries) = Three();
        using GalleryDecisionSession session = new(owner);
        await session.SetRatingAsync([entries[0]], 4);
        await session.SetFavoriteAsync([entries[0]], true);
        await session.SetRatingAsync([entries[0]], 1);
        Assert.Equal(3, session.UndoSteps.Count);
        Assert.True(session.UndoSteps[0].Stamp < session.UndoSteps[1].Stamp && session.UndoSteps[1].Stamp < session.UndoSteps[2].Stamp);
        await session.UndoAsync();
        Assert.Equal(new GalleryDecisionState(true, 4), owner.States["a"]);
        await session.UndoAsync();
        Assert.Equal(new GalleryDecisionState(false, 4), owner.States["a"]); // the rating from step one stays
        await session.UndoAsync();
        Assert.Equal(new GalleryDecisionState(false, null), owner.States["a"]);
        Assert.Equal(GalleryDecisionStatus.None, await session.UndoAsync()); // nothing left
    }

    [Fact]
    public async Task AnUndoThatEveryMediumRefusesKeepsTheStepForAnotherTry()
    {
        (FakeDecisions owner, GalleryEntry[] entries) = Three();
        using GalleryDecisionSession session = new(owner);
        await session.SetRatingAsync([entries[0]], 4);
        owner.Refuse.Add("a");
        GalleryDecisionStatus failed = await session.UndoAsync();
        Assert.Equal(GalleryDecisionStatusKind.Failed, failed.Kind);
        Assert.Single(session.UndoSteps);
        owner.Refuse.Clear();
        await session.UndoAsync();
        Assert.Null(owner.States["a"].Rating);
        Assert.Empty(session.UndoSteps);
    }

    [Fact]
    public async Task TheStackKeepsAtMostTheUndoDepthAndRequestsRunOneAfterTheOther()
    {
        (FakeDecisions owner, GalleryEntry[] entries) = Three();
        using GalleryDecisionSession session = new(owner, undoDepth: 2);
        Assert.Equal(2, session.UndoDepth);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int paused = 0;
        owner.Pause = () => Interlocked.Increment(ref paused) == 1 ? release.Task : Task.CompletedTask;
        // A quick second key waits for the first; its write comes after, and the last value wins.
        Task<GalleryDecisionStatus> first = session.SetRatingAsync([entries[0]], 2);
        Task<GalleryDecisionStatus> second = session.SetRatingAsync([entries[0]], 3);
        await Task.Delay(50);
        Assert.Equal(new[] { "rating a=2" }, owner.Writes);
        Assert.True(session.Busy);
        release.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(new[] { "rating a=2", "rating a=3" }, owner.Writes);
        Assert.Equal(3, owner.States["a"].Rating);
        Assert.False(session.Busy);
        await session.SetRatingAsync([entries[0]], 4);
        Assert.Equal(2, session.UndoSteps.Count); // the oldest step fell off the stack
        Assert.Equal(3, session.UndoSteps[0].Changes[0].After.Rating);
    }

    [Fact]
    public async Task TheRequestIsMadeForTheMediaItWasGivenEvenIfTheCallersListChangesMeanwhile()
    {
        (FakeDecisions owner, GalleryEntry[] entries) = Three();
        using GalleryDecisionSession session = new(owner);
        owner.States["c"] = new(false, 1);
        List<GalleryEntry> live = [entries[0], entries[1]];
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.Pause = () => release.Task;
        Task<GalleryDecisionStatus> request = session.SetRatingAsync(live, 5);
        live.Clear(); live.Add(entries[2]);
        release.SetResult();
        await request;
        Assert.Equal(5, owner.States["a"].Rating);
        Assert.Equal(5, owner.States["b"].Rating);
        Assert.Equal(1, owner.States["c"].Rating); // the list that replaced it meanwhile was not the request's
        Assert.Equal(2, session.UndoSteps[0].Changes.Count);
    }

    [Fact]
    public async Task ADisposedSessionDoesNothing()
    {
        (FakeDecisions owner, GalleryEntry[] entries) = Three();
        GalleryDecisionSession session = new(owner);
        session.Dispose();
        Assert.Equal(GalleryDecisionStatus.None, await session.SetRatingAsync([entries[0]], 4));
        Assert.Empty(owner.Writes);
        using GalleryDecisionSession live = new(owner);
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = live.SetRatingAsync([entries[0]], 6); });
    }

    // ---- the marks keep their entries ------------------------------------------------------------------------

    private static MediaTagSubject Subject(string thread, char sha)
    {
        Assert.True(PrivateMediaTagTarget.TryCreate(new AttachmentRevisionReference(Thread(thread), "1", 0, "att-v2:" + Hex(sha)),
            "m", "original", TransferMediaRole.Original, Hex(sha), out PrivateMediaTagTarget? target, out _));
        return new MediaTagSubject.Bound(target!);
    }

    [Fact]
    public void TheMarksKeepTheEntryEachMarkWasMadeOnInMarkingOrder()
    {
        (_, GalleryEntry[] entries) = Three();
        MediaTagSubject a = Subject("a", 'a'), b = Subject("b", 'b'), c = Subject("c", 'c');
        GalleryMarks marks = new();
        marks.Mark(b, entries[1]);
        marks.Mark(a, entries[0]);
        marks.Mark(c); // a mark made without an entry (older callers) is left out of the entries
        Assert.Equal(3, marks.Count);
        Assert.Equal(new[] { entries[1], entries[0] }, marks.Entries());
        marks.Unmark(b);
        Assert.Equal(new[] { entries[0] }, marks.Entries());
        marks.Clear();
        Assert.Empty(marks.Entries());
    }

    // ---- the controller: targets and the shared undo order ---------------------------------------------------

    private static PrivateTagLoadSnapshot Load(PrivateTagStore store) => new(PrivateTagLoadState.Ready, store.ReadSnapshot(), null);

    private static MediaSelectionTagSession Selection(PrivateTagStore store, IEnumerable<MediaTagSubject> subjects) =>
        MediaSelectionTagSession.ForSubjects(subjects, () => Load(store),
            (name, many, revision, token) => Task.FromResult(store.DefineAndSetMediaSubjects(name, many.ToArray(), revision, token)),
            (tag, many, assigned, revision, token) => Task.FromResult(store.SetMediaSubjects(tag, many.ToArray(), assigned, revision, token)));

    private static MediaTagSession Single(PrivateTagStore store, MediaTagSubject subject) =>
        new(subject, () => Load(store),
            (name, one, token) => Task.FromResult(store.DefineAndSetMediaSubjects(name, [one], null, token)),
            (tag, one, assigned, token) => Task.FromResult(store.SetMediaSubjects(tag, [one], assigned, null, token)));

    private sealed class PanelOwner(PrivateTagStore store, FakeDecisions decisions) : IGalleryTagPanelOwner, IGalleryDecisionOwner
    {
        internal readonly Dictionary<string, MediaTagSubject?> Subjects = new();
        internal readonly List<GalleryEntry> List = new();
        internal int Index;
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
            unavailableReason = null;
            return QuickTagTarget.For(Selection(store, subjects));
        }
        public IReadOnlyList<GalleryEntry> Entries => List;
        public GalleryTagPanelPosition? Position => Index >= 0 && Index < List.Count ? new GalleryTagPanelPosition(Index + 1, List.Count, false) : null;
        public TagRecentList Recent { get; } = new();
        public QuickTagOptions Options => QuickTagOptions.Default;
        public TagContextOptions ContextOptions => TagContextOptions.Default;
        public BooruCatalogSnapshot? ReadSourceTags() => null;
        public GalleryThreadTagModel OpenThreadTags(ResourceId thread) => throw new NotSupportedException();
        public void OpenAdvanced(GalleryEntry entry) { }
        public GalleryDecisionState? Read(GalleryEntry entry) => decisions.Read(entry);
        public Task SetFavoriteAsync(GalleryEntry entry, bool selected, CancellationToken token) => decisions.SetFavoriteAsync(entry, selected, token);
        public Task SetRatingAsync(GalleryEntry entry, int? value, CancellationToken token) => decisions.SetRatingAsync(entry, value, token);
    }

    // An owner that offers no decisions is a different type: the controller checks the interface, so build both shapes.
    private sealed class TagOnlyOwner(PanelOwner inner) : IGalleryTagPanelOwner
    {
        public IQuickTagTarget? OpenTarget(GalleryEntry entry, out string? unavailableReason) => inner.OpenTarget(entry, out unavailableReason);
        public MediaTagSubject? SubjectOf(GalleryEntry entry, out string? unavailableReason) => inner.SubjectOf(entry, out unavailableReason);
        public IQuickTagTarget? OpenSelectionTarget(IReadOnlyList<MediaTagSubject> subjects, out string? unavailableReason) => inner.OpenSelectionTarget(subjects, out unavailableReason);
        public IReadOnlyList<GalleryEntry> Entries => inner.Entries;
        public GalleryTagPanelPosition? Position => inner.Position;
        public TagRecentList Recent => inner.Recent;
        public QuickTagOptions Options => inner.Options;
        public TagContextOptions ContextOptions => inner.ContextOptions;
        public BooruCatalogSnapshot? ReadSourceTags() => null;
        public GalleryThreadTagModel OpenThreadTags(ResourceId thread) => throw new NotSupportedException();
        public void OpenAdvanced(GalleryEntry entry) { }
    }

    private static PanelOwner PanelWith(PrivateTagStore store, FakeDecisions decisions, GalleryEntry[] entries)
    {
        PanelOwner owner = new(store, decisions);
        owner.List.AddRange(entries);
        owner.Subjects["a"] = Subject("a", 'a');
        owner.Subjects["b"] = Subject("b", 'b');
        owner.Subjects["c"] = Subject("c", 'c');
        return owner;
    }

    [Fact]
    public void ThePanelRatesTheShownMediumUntilMediaAreMarkedAndThenExactlyTheMarkedOnes()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        (FakeDecisions decisions, GalleryEntry[] entries) = Three();
        PanelOwner owner = PanelWith(store, decisions, entries);
        using GalleryTagPanelController controller = GalleryTagPanelController.Create(owner, entries[0]);
        Assert.NotNull(controller.Decisions);
        Assert.NotNull(controller.ExtraUndo);
        Assert.Equal(new[] { entries[0] }, controller.DecisionTargets());
        Assert.Equal(new GalleryDecisionView(1, GalleryDecisionCoverage.None, null, false, true), controller.DecisionView());

        controller.Mark(entries[0]);
        owner.Index = 2;
        controller.Frame(entries[2]); // the panel moved on; the mark keeps pointing at medium 1
        controller.Mark(entries[2]);
        Assert.Equal(new[] { entries[0], entries[2] }, controller.DecisionTargets());
        GalleryDecisionView view = controller.DecisionView()!;
        Assert.Equal((2, true, (int?)null), (view.Count, view.RatingMixed, view.Rating));
        controller.Unmark(entries[0]);
        Assert.Equal(new[] { entries[2] }, controller.DecisionTargets());
    }

    [Fact]
    public void AnOwnerWithoutDecisionsLeavesThePanelWithoutTheRow()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        (FakeDecisions decisions, GalleryEntry[] entries) = Three();
        using GalleryTagPanelController controller = GalleryTagPanelController.Create(new TagOnlyOwner(PanelWith(store, decisions, entries)), entries[0]);
        Assert.Null(controller.Decisions);
        Assert.Null(controller.ExtraUndo);
        Assert.Null(controller.DecisionView());
    }

    [Fact]
    public async Task OneUndoKeyServesBothStacksAndTheNewestStepGoesFirst()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        (FakeDecisions decisions, GalleryEntry[] entries) = Three();
        PanelOwner owner = PanelWith(store, decisions, entries);
        using GalleryTagPanelController controller = GalleryTagPanelController.Create(owner, entries[0]);
        IQuickTagExtraUndo extra = controller.ExtraUndo!;
        Assert.False(extra.IsNewest);       // no decision step yet
        Assert.Equal(0, extra.Steps);

        await controller.Session.AddAsync("cat");                       // 1: a tag
        await controller.Decisions!.SetRatingAsync(controller.DecisionTargets(), 4); // 2: a rating
        Assert.Equal(1, extra.Steps);
        Assert.True(extra.IsNewest);        // the rating is newer than the tag
        await controller.Session.AddAsync("dog");                       // 3: a tag again
        Assert.False(extra.IsNewest);       // now the tag is newer
        Assert.Equal(2, controller.Session.UndoSteps.Count);

        await controller.Session.UndoAsync(); // the surface would undo the newest first: the dog goes
        Assert.Single(controller.Session.UndoSteps);
        Assert.True(extra.IsNewest);        // and the rating is the newest of what is left
        string? text = await extra.UndoAsync();
        Assert.Equal("Undone: rating change.", text);
        Assert.Null(decisions.States["a"].Rating);
        Assert.Equal(0, extra.Steps);
        Assert.False(extra.IsNewest);       // the cat tag is last
        Assert.Null(await extra.UndoAsync()); // nothing left on this stack
    }

    [Fact]
    public async Task TheExtraStackChangedEventFiresWhileAWriteRuns()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        (FakeDecisions decisions, GalleryEntry[] entries) = Three();
        using GalleryTagPanelController controller = GalleryTagPanelController.Create(PanelWith(store, decisions, entries), entries[0]);
        int changes = 0;
        controller.ExtraUndo!.Changed += () => changes++;
        await controller.Decisions!.SetFavoriteAsync([entries[0]], true);
        Assert.True(changes >= 2); // started and finished
    }

    // ---- texts and sources -----------------------------------------------------------------------------------

    private static readonly string[] DecisionKeys =
    {
        "gallery.decision.name", "gallery.decision.favorite", "gallery.decision.favorite.tip", "gallery.decision.favorite.some",
        "gallery.decision.rating.mixed", "gallery.decision.status.ratingSaved", "gallery.decision.status.ratingRemoved",
        "gallery.decision.status.favoriteSaved", "gallery.decision.status.favoriteRemoved", "gallery.decision.status.undoneRating",
        "gallery.decision.status.undoneFavorite", "gallery.decision.status.failed", "gallery.decision.status.unavailable",
        "gallery.decision.status.allMarked", "gallery.decision.status.skipped"
    };

    [Fact]
    public void EveryDecisionTextIsInEnglishAndGermanAndNoPlaceholderIsLost()
    {
        foreach (string key in DecisionKeys)
        {
            Assert.True(BuiltInCatalog.English.TryGetValue(key, out string? english), key);
            Assert.True(BuiltInCatalog.German.TryGetValue(key, out string? german), key);
            Assert.NotEqual(english, german);
            foreach (string placeholder in new[] { "{0}" })
                Assert.Equal(english!.Contains(placeholder), german!.Contains(placeholder));
        }
        Texts.SetLanguage("de");
        Assert.Equal("Bewertung 4 von 5 gespeichert.", new GalleryDecisionStatus(GalleryDecisionStatusKind.RatingSaved, 4).Text());
    }

    [Fact]
    public void ThePackKeysAreIdenticalInEveryBundledLanguageAndKeepTheirPlaceholders()
    {
        string[] codes = { "es", "fr", "ru", "zh-Hans", "ja", "ko", "pt-BR", "cs", "it", "pl" };
        string packs = Path.Combine(RepoRoot(), "Desktop", "Localization", "Packs");
        foreach (string code in codes)
        {
            using System.Text.Json.JsonDocument pack = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(packs, code + ".json")));
            System.Text.Json.JsonElement texts = pack.RootElement.GetProperty("strings");
            foreach (string key in DecisionKeys)
            {
                Assert.True(texts.TryGetProperty(key, out System.Text.Json.JsonElement value), code + " lacks " + key);
                string text = value.GetString()!;
                Assert.False(string.IsNullOrWhiteSpace(text), code + " " + key);
                Assert.Equal(BuiltInCatalog.English[key].Contains("{0}"), text.Contains("{0}"));
                Assert.NotEqual(BuiltInCatalog.English[key], text); // a real translation, not the English text
            }
        }
    }

    private static string RepoRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory != null && !File.Exists(Path.Combine(directory, "MeowChan.csproj"))) directory = Path.GetDirectoryName(directory);
        return directory ?? throw new InvalidOperationException("The repository root was not found.");
    }

    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

    [Fact]
    public void TheViewerRoutesTheRatingKeysThroughTheDecisionSessionAndAllowsThemInAnEmptyTagField()
    {
        string router = Source("Desktop", "GalleryViewer.cs");
        Assert.Contains("() => RequestRating(null)", router);
        Assert.Contains("() => RequestRating(selected)", router);
        string panel = Source("Desktop", "GalleryViewer.TagPanel.cs");
        // With the panel open the key rates the panel's targets with undo; without it the old path rates the shown medium.
        Assert.Contains("if (_tagPanelOpen && _tagController?.Decisions != null) { RequestDecisionRating(value); return; }", panel);
        Assert.Contains("RatingSetRequested?.Invoke(_entry, value);", panel);
        // The empty tag field lets Shift+digit through; text in the field (or an open list) keeps typing.
        Assert.Contains("string.IsNullOrEmpty(surface.Input.Text)", panel);
        Assert.Contains("surface.Input.IsDropDownOpen", panel);
        Assert.Contains("ShortcutService.Current.Matches(e, \"media.rating.set-\" + value)", panel);
        // The targets are read when the request is made.
        Assert.Contains("run(decisions, controller.DecisionTargets())", panel);
        // A click on the row keeps the focus in the tag field when it was there.
        Assert.Contains("FieldFocused = () => surface.Input.IsKeyboardFocusWithin", panel);
        Assert.Contains("surface.ExtraUndo = controller.ExtraUndo;", panel);
        // The header star and the row follow every change from the toolbar, the dialog or the keys.
        Assert.Contains("RefreshDecisionRow(); // the Tags panel's row shows the same rating", router);
        Assert.Contains("RefreshPostDecision(); // and so does the Post row", router);
        Assert.Contains("RefreshDecisionRow(); // the panel's row and the header star stay in step", router);
    }

    [Fact]
    public void TheWindowWritesThroughTheSameQueuesAsTheToolbarStarAndTheRatingKeys()
    {
        string window = Source("Desktop", "Shell", "MainWindow.GalleryTags.cs");
        Assert.Contains("IGalleryTagPanelOwner, IGalleryDecisionOwner", window);
        Assert.Contains("window._favoriteChanges.Target(key, window._session.IsFavorite(entry))", window);
        Assert.Contains("window._session.GetMediaRating(key)", window);
        Assert.Contains("window.SetGalleryFavoriteAsync(entry, selected)", window);
        Assert.Contains("window.SetGalleryRatingAsync(entry, value)", window);
    }

    [Fact]
    public void TheSurfaceShowsTheRowAboveTheFieldAndUndoesTheNewestOfTheTwoStacks()
    {
        string surface = Source("Desktop", "Controls", "QuickTagSurface.cs");
        Assert.Contains("_fieldArea.Children.Insert(0, value)", surface);
        Assert.Contains("_extraUndo is { IsNewest: true } extra", surface);
        Assert.Contains("_session.UndoSteps.Count + extraSteps", surface);
        string row = Source("Desktop", "Controls", "GalleryDecisionRow.cs");
        Assert.Contains("FavoriteStar.Create", row);
        Assert.Contains("media.rating.set-", row);
        Assert.Contains("GalleryDecisionText.Summary", row);
    }
}
