using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

// TP1 (PLAN-SCHNELL-TAGGEN-2026-11-14): the shared quick tag surface. Session and undo stack against a real tag store, the key
// routing table, badges, accessible names of the built chips (controls without an application) and the twelve languages.
[Collection("Localization")]
public sealed class QuickTagSurfaceTests : IDisposable
{
    private static readonly DateTimeOffset Day = new(2026, 11, 14, 0, 0, 0, TimeSpan.Zero);

    public QuickTagSurfaceTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
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

    private static MediaSelectionTagSession Selection(PrivateTagStore store, params MediaLibraryEntry[] entries)
    {
        IReadOnlyList<MediaLibraryTile> tiles = MediaLibraryGrouping.Build(entries, entries, MediaLibrarySort.ByThread, includeMissing: true);
        return new MediaSelectionTagSession(MediaLibraryTagging.Candidates(tiles), () => Load(store),
            (name, subjects, revision, token) => Task.FromResult(store.DefineAndSetMediaSubjects(name, subjects.ToArray(), revision, token)),
            (tag, subjects, assigned, revision, token) => Task.FromResult(store.SetMediaSubjects(tag, subjects.ToArray(), assigned, revision, token)));
    }

    private static IReadOnlyList<string> NamesOn(PrivateTagStore store, MediaLibraryEntry entry) =>
        store.ReadSnapshot().TagsOf(MediaLibraryFilterEngine.TagTarget(entry)!).Select(tag => tag.Name.Display).Order(StringComparer.Ordinal).ToArray();

    private static PrivateTagName Name(string text)
    {
        Assert.True(PrivateTagName.TryCreate(text, out PrivateTagName? name, out _));
        return name!;
    }

    // ---- session: add, create, remove, status -------------------------------------------------------------------

    [Fact]
    public async Task AddCreatesOrAssignsAToggleRemovesAndTheStatusSaysWhatHappened()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        MediaLibraryEntry entry = Entry("a", 'a');
        TagId red = store.Define("red").Tag;
        using QuickTagSession session = new(QuickTagTarget.For(Single(store, entry)));
        List<TagDialogChange> applied = new();
        session.Applied += applied.Add;

        await session.AddAsync(" cute ");
        Assert.Equal(new[] { "cute" }, NamesOn(store, entry));
        Assert.Equal(QuickTagStatusKind.Added, session.Status.Kind);
        Assert.Equal("Added “cute”.", session.Status.Text);

        await session.ToggleAsync(red);
        Assert.Equal(new[] { "cute", "red" }, NamesOn(store, entry));
        Assert.Equal(TagToggleState.All, session.StateOf(red));
        Assert.Equal(new[] { "red", "cute" }.Order(), session.Assignments().Select(item => item.Tag.Name.Display).Order());
        Assert.Contains(red, session.Carried());

        await session.ToggleAsync(red);
        Assert.Equal(new[] { "cute" }, NamesOn(store, entry));
        Assert.Equal(QuickTagStatusKind.Removed, session.Status.Kind);
        Assert.Equal(3, applied.Count);
        Assert.Equal(3, session.UndoSteps.Count);

        // Blank text writes nothing and leaves the stack alone.
        await session.AddAsync("   ");
        Assert.Equal(3, session.UndoSteps.Count);
    }

    [Fact]
    public async Task ASuggestionThatIsOnlyANameCreatesTheOwnTagAndAssignsIt()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        MediaLibraryEntry entry = Entry("a", 'a');
        using QuickTagSession session = new(QuickTagTarget.For(Single(store, entry)));

        await session.AcceptAsync(new QuickTagSuggestion(Name("blue_sky"), null, 2, QuickTagReason.SourceTag, 7));

        Assert.Equal(new[] { "blue_sky" }, NamesOn(store, entry));
        Assert.Single(store.ReadSnapshot().Tags);
    }

    // ---- undo stack ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task UndoGoesBackStepByStepForExactlyTheChangedFilesAndKeepsCreatedTags()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        MediaLibraryEntry entry = Entry("a", 'a');
        TagId red = store.Define("red").Tag;
        store.SetMedia(red, MediaLibraryFilterEngine.TagTarget(entry)!, true);
        using QuickTagSession session = new(QuickTagTarget.For(Single(store, entry)));

        await session.AddAsync("cute");         // 1: cute on
        await session.RemoveAsync(red);         // 2: red off
        await session.AddAsync("wip");          // 3: wip on
        Assert.Equal(new[] { "cute", "wip" }, NamesOn(store, entry));
        Assert.Equal(3, session.UndoSteps.Count);

        await session.UndoAsync();
        Assert.Equal(new[] { "cute" }, NamesOn(store, entry));
        Assert.Equal(QuickTagStatusKind.UndoneAdd, session.Status.Kind);
        Assert.Equal("Undone: “wip” taken off again.", session.Status.Text);
        await session.UndoAsync();
        Assert.Equal(new[] { "cute", "red" }, NamesOn(store, entry));
        Assert.Equal(QuickTagStatusKind.UndoneRemove, session.Status.Kind);
        await session.UndoAsync();
        Assert.Equal(new[] { "red" }, NamesOn(store, entry));
        Assert.Empty(session.UndoSteps);
        Assert.False(session.CanUndo);
        await session.UndoAsync(); // nothing left: no write, no change
        Assert.Equal(new[] { "red" }, NamesOn(store, entry));
        // Tags a change created stay in the vocabulary after its undo.
        Assert.Equal(new[] { "cute", "red", "wip" }, store.ReadSnapshot().Tags.Select(tag => tag.Name.Display).Order(StringComparer.Ordinal));
    }

    // R03 (review 14.11. night): a cancelled undo wrote nothing back, so it must neither drop the step nor report success.
    [Fact]
    public async Task ACancelledUndoKeepsTheStepAndClaimsNothing()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        MediaLibraryEntry entry = Entry("a", 'a');
        using CancellationTokenSource closing = new();
        bool cancelNext = false;
        MediaTagSession single = new(new MediaTagSubject.Bound(MediaLibraryFilterEngine.TagTarget(entry)!), () => Load(store),
            (name, subject, token) => Task.FromResult(store.DefineAndSetMediaSubjects(name, [subject], null, token)),
            (id, subject, assigned, token) =>
            {
                if (cancelNext) { closing.Cancel(); token.ThrowIfCancellationRequested(); }
                return Task.FromResult(store.SetMediaSubjects(id, [subject], assigned, null, token));
            });
        using QuickTagSession session = new(QuickTagTarget.For(single));
        await session.AddAsync("cute");
        Assert.Single(session.UndoSteps);

        cancelNext = true;
        await session.UndoAsync(closing.Token);
        Assert.Equal(new[] { "cute" }, NamesOn(store, entry)); // nothing was written back
        Assert.Single(session.UndoSteps);                       // the step stays for another try
        Assert.Equal(QuickTagStatusKind.None, session.Status.Kind);
        Assert.False(session.Status.IsError);
        Assert.True(session.CanUndo);

        cancelNext = false;
        await session.UndoAsync();
        Assert.Empty(NamesOn(store, entry));
        Assert.Empty(session.UndoSteps);
        Assert.Equal(QuickTagStatusKind.UndoneAdd, session.Status.Kind);
    }

    [Fact]
    public async Task ACancelledSelectionUndoKeepsTheStepAndClaimsNothing()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        MediaLibraryEntry first = Entry("a", 'a'), second = Entry("b", 'b');
        TagId red = store.Define("red").Tag;
        using CancellationTokenSource closing = new();
        bool cancelNext = false;
        IReadOnlyList<MediaLibraryTile> tiles = MediaLibraryGrouping.Build(new[] { first, second }, new[] { first, second }, MediaLibrarySort.ByThread, includeMissing: true);
        MediaSelectionTagSession selection = new(MediaLibraryTagging.Candidates(tiles), () => Load(store),
            (name, subjects, revision, token) => Task.FromResult(store.DefineAndSetMediaSubjects(name, subjects.ToArray(), revision, token)),
            (tag, subjects, assigned, revision, token) =>
            {
                if (cancelNext) { closing.Cancel(); token.ThrowIfCancellationRequested(); }
                return Task.FromResult(store.SetMediaSubjects(tag, subjects.ToArray(), assigned, revision, token));
            });
        using QuickTagSession session = new(QuickTagTarget.For(selection));
        await session.ToggleAsync(red);
        Assert.Equal(TagToggleState.All, session.StateOf(red));

        cancelNext = true;
        await session.UndoAsync(closing.Token);
        Assert.Equal(TagToggleState.All, session.StateOf(red));
        Assert.Single(session.UndoSteps);
        Assert.Equal(QuickTagStatusKind.None, session.Status.Kind);
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(0, QuickTagOptions.MinimumUndoDepth)]
    [InlineData(1000, QuickTagOptions.MaximumUndoDepth)]
    public async Task TheUndoDepthIsAClampedLimitAndTheOldestStepDropsOut(int requested, int effective)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        MediaLibraryEntry entry = Entry("a", 'a');
        using QuickTagSession session = new(QuickTagTarget.For(Single(store, entry)), requested);
        Assert.Equal(effective, session.UndoDepth);

        foreach (string name in new[] { "one", "two", "three" }) await session.AddAsync(name);
        Assert.Equal(Math.Min(3, effective), session.UndoSteps.Count);
        Assert.Equal("three", session.UndoSteps[^1].Name);
        if (effective < 3) Assert.DoesNotContain(session.UndoSteps, step => step.Name == "one");
    }

    [Fact]
    public void TheLimitsHaveTheirDocumentedDefaultMinimumAndMaximum()
    {
        Assert.Equal((20, 1, 100), (QuickTagOptions.DefaultUndoDepth, QuickTagOptions.MinimumUndoDepth, QuickTagOptions.MaximumUndoDepth));
        Assert.Equal((9, 3, 18), (QuickTagOptions.DefaultSuggestionCount, QuickTagOptions.MinimumSuggestionCount, QuickTagOptions.MaximumSuggestionCount));
        Assert.Equal(new QuickTagOptions { UndoDepth = 1, SuggestionCount = 3 }, new QuickTagOptions { UndoDepth = -5, SuggestionCount = 0 }.Normalized());
        Assert.Equal(new QuickTagOptions { UndoDepth = 100, SuggestionCount = 18 }, new QuickTagOptions { UndoDepth = 500, SuggestionCount = 99 }.Normalized());
        Assert.Equal(QuickTagOptions.Default, QuickTagOptions.Default.Normalized());
    }

    [Fact]
    public async Task TheStackSurvivesAMediumSwitchAndUndoesOnTheMediumItChanged()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        MediaLibraryEntry first = Entry("a", 'a'), second = Entry("b", 'b');
        using QuickTagSession session = new(QuickTagTarget.For(Single(store, first)));
        await session.AddAsync("cute");

        session.Retarget(QuickTagTarget.For(Single(store, second)));
        await session.AddAsync("wip");
        Assert.Equal(new[] { "cute" }, NamesOn(store, first));
        Assert.Equal(new[] { "wip" }, NamesOn(store, second));

        await session.UndoAsync();
        await session.UndoAsync(); // the step of the first medium, undone while the second one is shown
        Assert.Empty(NamesOn(store, first));
        Assert.Empty(NamesOn(store, second));
    }

    // ---- tri-state for a selection ------------------------------------------------------------------------------

    [Fact]
    public async Task ASelectionShowsTriStateChipsAndAToggleGoesSomeToAllToNone()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        MediaLibraryEntry first = Entry("a", 'a'), second = Entry("b", 'b');
        TagId red = store.Define("red").Tag;
        store.SetMedia(red, MediaLibraryFilterEngine.TagTarget(first)!, true);
        using QuickTagSession session = new(QuickTagTarget.For(Selection(store, first, second)));

        Assert.True(session.IsSelection);
        QuickTagAssignment some = Assert.Single(session.Assignments());
        Assert.Equal((TagToggleState.Some, 1, 2), (some.State, some.Carriers, some.Total));
        Assert.DoesNotContain(red, session.OnAll()); // a tag only some carry can still be typed to give it to all
        Assert.Contains(red, session.Carried());     // ... but is never offered as a suggestion: it is a chip

        await session.ToggleAsync(red);
        Assert.Equal(TagToggleState.All, session.StateOf(red));
        Assert.Equal(new[] { "red" }, NamesOn(store, second));
        Assert.Equal(1, session.UndoSteps[^1].Subjects.Count); // only the file that lacked it
        Assert.Equal("Added “red”.", session.Status.Text);

        await session.ToggleAsync(red);
        Assert.Equal(TagToggleState.None, session.StateOf(red));
        Assert.Equal(2, session.UndoSteps[^1].Subjects.Count);
        Assert.Equal("Removed “red” from 2 occurrences.", session.Status.Text);

        await session.UndoAsync();
        Assert.Equal(TagToggleState.All, session.StateOf(red));
        await session.UndoAsync();
        Assert.Equal(new[] { "red" }, NamesOn(store, first));
        Assert.Empty(NamesOn(store, second)); // back to exactly "some"
    }

    [Fact]
    public async Task ARefusedWriteStaysInTheStatusUntilTheNextSuccessfulChange()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        MediaLibraryEntry first = Entry("a", 'a'), second = Entry("b", 'b');
        TagId red = store.Define("red").Tag;
        using QuickTagSession session = new(QuickTagTarget.For(Selection(store, first, second)));

        store.Define("other"); // the vocabulary changed after the surface looked: the revision-bound write is refused
        await session.ToggleAsync(red);
        Assert.Equal(QuickTagStatusKind.Error, session.Status.Kind);
        Assert.True(session.Status.IsError);
        Assert.Equal(Texts.T("tags.error.stale"), session.Status.Text);
        Assert.Empty(session.UndoSteps);

        await session.ToggleAsync(red); // reloaded; the retry works and clears the error
        Assert.Equal(QuickTagStatusKind.Added, session.Status.Kind);
        Assert.False(session.Status.IsError);
        Assert.Single(session.UndoSteps);
    }

    [Fact]
    public async Task AnUnavailableIndexSaysSoAndWritesNothing()
    {
        int writes = 0;
        MediaTagSession unavailable = new(new MediaTagSubject.Bound(MediaLibraryFilterEngine.TagTarget(Entry("a", 'a'))!),
            () => new PrivateTagLoadSnapshot(PrivateTagLoadState.Unavailable, null, "broken"),
            (_, _, _) => { writes++; throw new InvalidOperationException(); },
            (_, _, _, _) => { writes++; throw new InvalidOperationException(); });
        using QuickTagSession session = new(QuickTagTarget.For(unavailable));
        await session.AddAsync("red");
        Assert.Equal(0, writes);
        Assert.Equal(QuickTagStatusKind.Unavailable, session.Status.Kind);
        Assert.Equal(Texts.T("tags.unavailable"), session.Status.Text);
        Assert.False(session.Ready);
    }

    // ---- key routing --------------------------------------------------------------------------------------------

    [Theory]
    // Alt+n (⌘n on macOS) toggles suggestion n everywhere, even in the field; bare digits there stay text.
    [InlineData(Key.D3, KeyModifiers.Alt, QuickTagFocus.Field, false, false, ShortcutPlatform.Linux, QuickTagKeyAction.ToggleSuggestion, 2)]
    [InlineData(Key.NumPad9, KeyModifiers.Alt, QuickTagFocus.Assigned, false, true, ShortcutPlatform.Windows, QuickTagKeyAction.ToggleSuggestion, 8)]
    [InlineData(Key.D1, KeyModifiers.Meta, QuickTagFocus.Field, false, true, ShortcutPlatform.MacOS, QuickTagKeyAction.ToggleSuggestion, 0)]
    [InlineData(Key.D1, KeyModifiers.Alt, QuickTagFocus.Field, false, true, ShortcutPlatform.MacOS, QuickTagKeyAction.None, 0)]
    [InlineData(Key.D2, KeyModifiers.None, QuickTagFocus.Field, false, true, ShortcutPlatform.Linux, QuickTagKeyAction.None, 0)]
    // In the strip, bare digits and badge letters toggle; arrows move; Up/Down change zones.
    [InlineData(Key.D2, KeyModifiers.None, QuickTagFocus.Suggestions, false, true, ShortcutPlatform.Linux, QuickTagKeyAction.ToggleSuggestion, 1)]
    [InlineData(Key.K, KeyModifiers.None, QuickTagFocus.Suggestions, false, true, ShortcutPlatform.Linux, QuickTagKeyAction.ToggleBadge, 'K')]
    [InlineData(Key.Right, KeyModifiers.None, QuickTagFocus.Suggestions, false, true, ShortcutPlatform.Linux, QuickTagKeyAction.Move, 1)]
    [InlineData(Key.Home, KeyModifiers.None, QuickTagFocus.Suggestions, false, true, ShortcutPlatform.Linux, QuickTagKeyAction.MoveFirst, 0)]
    [InlineData(Key.Space, KeyModifiers.None, QuickTagFocus.Suggestions, false, true, ShortcutPlatform.Linux, QuickTagKeyAction.ToggleFocused, 0)]
    [InlineData(Key.Up, KeyModifiers.None, QuickTagFocus.Suggestions, false, true, ShortcutPlatform.Linux, QuickTagKeyAction.FocusField, 0)]
    [InlineData(Key.Down, KeyModifiers.None, QuickTagFocus.Suggestions, false, true, ShortcutPlatform.Linux, QuickTagKeyAction.FocusAssigned, 0)]
    // Assigned chips: Delete and Backspace remove; Left moves; Up returns to the strip.
    [InlineData(Key.Delete, KeyModifiers.None, QuickTagFocus.Assigned, false, true, ShortcutPlatform.Linux, QuickTagKeyAction.RemoveFocused, 0)]
    [InlineData(Key.Back, KeyModifiers.None, QuickTagFocus.Assigned, false, true, ShortcutPlatform.Linux, QuickTagKeyAction.RemoveFocused, 0)]
    [InlineData(Key.Left, KeyModifiers.None, QuickTagFocus.Assigned, false, true, ShortcutPlatform.Linux, QuickTagKeyAction.Move, -1)]
    [InlineData(Key.Up, KeyModifiers.None, QuickTagFocus.Assigned, false, true, ShortcutPlatform.Linux, QuickTagKeyAction.FocusSuggestions, 0)]
    // Field with no text and a closed list: Backspace focuses (never deletes) the last chip; Down and Tab go to the strip.
    [InlineData(Key.Back, KeyModifiers.None, QuickTagFocus.Field, false, true, ShortcutPlatform.Linux, QuickTagKeyAction.FocusLastAssigned, 0)]
    [InlineData(Key.Back, KeyModifiers.None, QuickTagFocus.Field, false, false, ShortcutPlatform.Linux, QuickTagKeyAction.None, 0)]
    [InlineData(Key.Down, KeyModifiers.None, QuickTagFocus.Field, false, true, ShortcutPlatform.Linux, QuickTagKeyAction.FocusSuggestions, 0)]
    [InlineData(Key.Down, KeyModifiers.None, QuickTagFocus.Field, true, true, ShortcutPlatform.Linux, QuickTagKeyAction.None, 0)]
    [InlineData(Key.Tab, KeyModifiers.None, QuickTagFocus.Field, false, true, ShortcutPlatform.Linux, QuickTagKeyAction.FocusSuggestions, 0)]
    [InlineData(Key.Up, KeyModifiers.None, QuickTagFocus.Field, false, true, ShortcutPlatform.Linux, QuickTagKeyAction.FocusAssigned, 0)]
    [InlineData(Key.Enter, KeyModifiers.None, QuickTagFocus.Field, false, false, ShortcutPlatform.Linux, QuickTagKeyAction.None, 0)]
    // Ctrl+Z: the surface's undo, except while the field holds text (then it is the editor's).
    [InlineData(Key.Z, KeyModifiers.Control, QuickTagFocus.Field, false, true, ShortcutPlatform.Linux, QuickTagKeyAction.Undo, 0)]
    [InlineData(Key.Z, KeyModifiers.Control, QuickTagFocus.Field, false, false, ShortcutPlatform.Linux, QuickTagKeyAction.None, 0)]
    [InlineData(Key.Z, KeyModifiers.Control, QuickTagFocus.Suggestions, false, true, ShortcutPlatform.Linux, QuickTagKeyAction.Undo, 0)]
    [InlineData(Key.Z, KeyModifiers.Meta, QuickTagFocus.Assigned, false, true, ShortcutPlatform.MacOS, QuickTagKeyAction.Undo, 0)]
    // Escape is layered: the open list first, then the surface.
    [InlineData(Key.Escape, KeyModifiers.None, QuickTagFocus.Field, true, false, ShortcutPlatform.Linux, QuickTagKeyAction.CloseList, 0)]
    [InlineData(Key.Escape, KeyModifiers.None, QuickTagFocus.Field, false, false, ShortcutPlatform.Linux, QuickTagKeyAction.Close, 0)]
    [InlineData(Key.Escape, KeyModifiers.None, QuickTagFocus.Assigned, false, true, ShortcutPlatform.Linux, QuickTagKeyAction.Close, 0)]
    public void KeysRouteAsThePlanSays(Key key, KeyModifiers modifiers, object focus, bool listOpen, bool fieldEmpty,
        ShortcutPlatform platform, object action, int value)
    {
        Assert.Equal(new QuickTagKeyRoute((QuickTagKeyAction)action, value),
            QuickTagKeys.Route(key, modifiers, (QuickTagFocus)focus, listOpen, fieldEmpty, navigation: false, platform));
    }

    [Theory]
    [InlineData(Key.PageDown, KeyModifiers.None, QuickTagKeyAction.Next)]
    [InlineData(Key.PageUp, KeyModifiers.None, QuickTagKeyAction.Previous)]
    [InlineData(Key.PageDown, KeyModifiers.Shift, QuickTagKeyAction.MarkNext)]
    [InlineData(Key.PageUp, KeyModifiers.Shift, QuickTagKeyAction.UnmarkPrevious)]
    [InlineData(Key.Enter, KeyModifiers.Control, QuickTagKeyAction.CommitAndNext)]
    [InlineData(Key.T, KeyModifiers.Control, QuickTagKeyAction.ThreadSection)]
    public void GalleryNavigationKeysExistOnlyWhereTheHostMoves(Key key, KeyModifiers modifiers, object expected)
    {
        QuickTagKeyAction action = (QuickTagKeyAction)expected;
        Assert.Equal(action, QuickTagKeys.Route(key, modifiers, QuickTagFocus.Field, false, false, navigation: true, ShortcutPlatform.Linux).Action);
        Assert.NotEqual(action, QuickTagKeys.Route(key, modifiers, QuickTagFocus.Field, false, false, navigation: false, ShortcutPlatform.Linux).Action);
        // An open suggestion list keeps its own paging and Enter.
        Assert.Equal(QuickTagKeyAction.None, QuickTagKeys.Route(key, modifiers, QuickTagFocus.Field, true, false, navigation: true, ShortcutPlatform.Linux).Action);
    }

    [Fact]
    public void ShortcutTextsFollowThePlatformAndOnlyNineAreNumbered()
    {
        Assert.Equal("Alt+1", QuickTagKeys.SuggestionGesture(0, ShortcutPlatform.Linux));
        Assert.Equal("Alt+9", QuickTagKeys.SuggestionGesture(8, ShortcutPlatform.Windows));
        Assert.Equal("⌘1", QuickTagKeys.SuggestionGesture(0, ShortcutPlatform.MacOS));
        Assert.Null(QuickTagKeys.SuggestionGesture(9, ShortcutPlatform.Linux));
        Assert.Equal("Ctrl+Z", QuickTagKeys.UndoGesture(ShortcutPlatform.Linux));
        Assert.Equal("⌘Z", QuickTagKeys.UndoGesture(ShortcutPlatform.MacOS));
        Texts.SetLanguage("de");
        Assert.Equal("Strg+Z", QuickTagKeys.UndoGesture(ShortcutPlatform.Windows));
    }

    [Fact]
    public void BadgeLettersAreTheFirstFreeLetterOfEachNameAndNumbersStopAtNine()
    {
        Assert.Equal(new char?[] { 'S', 'K', 'G', null, 'W', 'U' }, QuickTagBadges.Letters(new[] { "surface", "sky", "2girls", "ßüé", "wip", "sus" }));
        Assert.Equal(1, QuickTagBadges.Number(0));
        Assert.Equal(9, QuickTagBadges.Number(8));
        Assert.Null(QuickTagBadges.Number(9));
    }

    // ---- the control (built without an application: names, layout, chips) --------------------------------------

    private static (QuickTagSurface Surface, QuickTagSession Session) Surface(PrivateTagStore store, IQuickTagTarget target, IQuickTagSuggestionSource? source = null)
    {
        QuickTagSession session = new(target);
        QuickTagSurface surface = new(session, platform: ShortcutPlatform.Linux) { TargetLabel = "1699999999123.jpg" };
        if (source != null) surface.Suggestions = source;
        return (surface, session);
    }

    [Fact]
    public async Task SuggestionChipsCarryNumberReasonAndUsesInTheirNameAndToggleThroughTheSession()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        MediaLibraryEntry entry = Entry("a", 'a');
        TagId surfaceTag = store.Define("surface").Tag, wip = store.Define("wip").Tag;
        DelegateQuickTagSuggestions source = new(request => new QuickTagSuggestion[]
        {
            new(request.Snapshot.Find(surfaceTag)!.Name, surfaceTag, 9, QuickTagReason.Thread, 4),
            new(request.Snapshot.Find(wip)!.Name, wip, 6, QuickTagReason.SameFile, 2, "/g/"),
            new(Name("blue_eyes"), null, 2, QuickTagReason.SourceTag, 11),
            new(request.Snapshot.Find(wip)!.Name, wip, 1, QuickTagReason.Frequent, 2), // duplicate: dropped
        });
        (QuickTagSurface surface, QuickTagSession session) = Surface(store, QuickTagTarget.For(Single(store, entry)), source);
        using (session)
        using (surface)
        {
            Assert.Equal(new[] { "surface", "wip", "blue_eyes" }, surface.Shown.Select(item => item.Name.Display));
            Button[] chips = surface.SuggestionButtons.ToArray();
            Assert.Equal("surface, suggestion 1 of 3, from this thread, number of uses: 4. Enter adds it.", AutomationProperties.GetName(chips[0]));
            Assert.Equal("wip, suggestion 2 of 3, same file in /g/, number of uses: 2. Enter adds it.", AutomationProperties.GetName(chips[1]));
            Assert.Equal("blue_eyes, suggestion 3 of 3, source tag. Enter creates it as an own tag and adds it.", AutomationProperties.GetName(chips[2]));
            Assert.Equal("surface · from this thread · Alt+1", ToolTip.GetTip(chips[0]));
            Assert.Equal("Alt+2", AutomationProperties.GetAcceleratorKey(chips[1]));
            Assert.Contains(WorkspaceChips.Available, chips[0].Classes);
            Assert.Contains(WorkspaceChips.SourceButton, chips[2].Classes); // dashed source chip, never an own-tag look
            Assert.Equal(new char?[] { 'S', 'W', 'B' }, surface.Letters);

            await surface.ToggleSuggestionAsync(1); // Alt+2
            await surface.ToggleSuggestionAsync(2); // Alt+3 creates blue_eyes
            surface.Refresh();
            Assert.Equal(new[] { "blue_eyes", "wip" }, NamesOn(store, entry));
            Assert.Equal(new[] { "surface" }, surface.Shown.Select(item => item.Name.Display)); // carried tags leave the strip
            Assert.True(surface.Execute(new QuickTagKeyRoute(QuickTagKeyAction.ToggleSuggestion, 5))); // Alt+6 without a sixth chip: swallowed
            Assert.False(surface.Execute(new QuickTagKeyRoute(QuickTagKeyAction.ToggleBadge, 'Q')));     // no chip has Q

            (Button chip, Button remove, TagId tag) = surface.AssignedButtons.Single(item => item.Tag == wip);
            Assert.Equal("wip, on this media file. Delete removes it.", AutomationProperties.GetName(chip));
            Assert.Equal("Remove wip", AutomationProperties.GetName(remove));
            Assert.Contains(WorkspaceIcons.SquareClass, remove.Classes);
            Assert.Equal("Undo the last tag change, steps left: 2", AutomationProperties.GetName(surface.UndoButton));
            Assert.True(surface.UndoButton.IsEnabled);
            Assert.Equal("Added “blue_eyes”.", surface.Status.Text);
            Assert.Contains("workspace-help", surface.Status.Classes);
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(surface.Status));
            Assert.Equal("Tags for 1699999999123.jpg", AutomationProperties.GetName(surface));
            Assert.Equal("Suggestions: 1, assigned: 2. Escape closes.", AutomationProperties.GetHelpText(surface));
        }
    }

    [Fact]
    public void ANameThatIsAlreadyAnOwnTagStandsForItAndCarriedOrRepeatedOnesAreDropped()
    {
        PrivateTagDefinition red = new(new TagId(Guid.NewGuid()), Name("Red")), blue = new(new TagId(Guid.NewGuid()), Name("blue"));
        PrivateTagStoreSnapshot snapshot = new(1, "fp", new[] { red, blue }, Array.Empty<PrivateThreadTagAssignment>(), Array.Empty<PrivateMediaTagAssignment>());
        QuickTagSuggestion[] items =
        {
            new(Name("red"), null, 5, QuickTagReason.SourceTag, 3),   // the own tag "Red" (same key): offered as it
            new(Name("BLUE"), null, 4, QuickTagReason.SourceTag, 1),  // the own tag "blue", carried: dropped
            new(Name("new_one"), null, 3, QuickTagReason.Title, 0),   // a name only: creates
            new(Name("New_One"), null, 2, QuickTagReason.FileName, 0), // the same key again: dropped
            new(Name("extra"), null, 1, QuickTagReason.FileName, 0),  // over the limit
        };
        IReadOnlyList<QuickTagSuggestion> shown = QuickTagSurface.Normalize(items, snapshot, new HashSet<TagId> { blue.Id }, limit: 2);
        Assert.Equal(new[] { "Red", "new_one" }, shown.Select(item => item.Name.Display));
        Assert.Equal(red.Id, shown[0].Id);
        Assert.False(shown[0].CreatesTag);
        Assert.True(shown[1].CreatesTag);
        Assert.Equal(QuickTagReason.SourceTag, shown[0].Reason); // the reason stays explainable
    }

    [Fact]
    public void TheCloseButtonSitsAloneAtTheRightEdgeAndActionsSitBottomLeft()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        (QuickTagSurface surface, QuickTagSession session) = Surface(store, QuickTagTarget.For(Single(store, Entry("a", 'a'))));
        using (session)
        using (surface)
        {
            Grid header = surface.Header;
            Assert.Equal(header.ColumnDefinitions.Count - 1, Grid.GetColumn(surface.CloseButton));
            Assert.Equal(1, Grid.GetColumn(surface.Title));
            Assert.Equal(24, header.ColumnDefinitions[^2].Width.Value); // empty gap without any action
            Assert.DoesNotContain(header.Children, child => Grid.GetColumn(child) == header.ColumnDefinitions.Count - 2);
            Assert.Equal(1, KeyboardNavigation.GetTabIndex(surface.Header)); // the close button is the last tab stop
            Assert.Contains(WorkspaceIcons.SquareClass, surface.CloseButton.Classes);
            Assert.Equal(WorkspaceIcons.Close, WorkspaceIcons.KeyOf(surface.CloseButton.Content));
            Assert.Equal("Close", AutomationProperties.GetName(surface.CloseButton));
            Assert.Equal("Close · Esc", ToolTip.GetTip(surface.CloseButton));
            Assert.Equal(HorizontalAlignment.Left, surface.Footer.HorizontalAlignment);
            Assert.Equal(new Control[] { surface.UndoButton, surface.AdvancedButton }, surface.Footer.Children.ToArray());
            Assert.DoesNotContain(surface.CloseButton, surface.Footer.Children);
            Assert.False(surface.UndoButton.IsEnabled); // nothing to undo yet
            Assert.Equal("Tags · 1699999999123.jpg", surface.Title.Text);
            Assert.Equal(Avalonia.Media.TextTrimming.CharacterEllipsis, surface.Title.TextTrimming);

            bool closed = false, advanced = false;
            surface.CloseRequested += () => closed = true;
            surface.AdvancedRequested += () => advanced = true;
            Assert.True(surface.Execute(new QuickTagKeyRoute(QuickTagKeyAction.Close)));
            surface.AdvancedButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.True(closed && advanced);
            // Navigation keys are not the surface's unless the host listens (a popover never does).
            Assert.False(surface.Execute(new QuickTagKeyRoute(QuickTagKeyAction.Next)));
            QuickTagNavigation? moved = null;
            surface.NavigationRequested += navigation => moved = navigation;
            Assert.True(surface.Execute(new QuickTagKeyRoute(QuickTagKeyAction.MarkNext)));
            Assert.Equal(QuickTagNavigation.MarkNext, moved);
        }
    }

    [Fact]
    public async Task ASelectionGetsACounterAndTriStateChipNames()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        MediaLibraryEntry first = Entry("a", 'a'), second = Entry("b", 'b');
        TagId red = store.Define("red").Tag, blue = store.Define("blue").Tag;
        store.SetMedia(red, MediaLibraryFilterEngine.TagTarget(first)!, true);
        store.SetMedia(blue, MediaLibraryFilterEngine.TagTarget(first)!, true);
        store.SetMedia(blue, MediaLibraryFilterEngine.TagTarget(second)!, true);
        (QuickTagSurface surface, QuickTagSession session) = Surface(store, QuickTagTarget.For(Selection(store, first, second)));
        using (session)
        using (surface)
        {
            var chips = surface.AssignedButtons;
            Assert.Equal(new[] { blue, red }, chips.Select(item => item.Tag)); // all first, then some
            Assert.Equal("blue, on all 2 selected media files. Delete removes it from all.", AutomationProperties.GetName(chips[0].Chip));
            Assert.Equal("red, on 1 of 2 selected media files. Space gives it to all, Delete removes it from all.", AutomationProperties.GetName(chips[1].Chip));
            Assert.Equal("Remove red from all", AutomationProperties.GetName(chips[1].Remove));
            Assert.Contains(WorkspaceChips.Own, chips[0].Chip.Classes);       // all: filled + check
            Assert.Contains(WorkspaceChips.Available, chips[1].Chip.Classes); // some: outline + half square
            Assert.Empty(surface.Shown); // both tags are carried: no suggestion repeats a chip

            await session.ToggleAsync(red);
            surface.Refresh();
            Assert.All(surface.AssignedButtons, item => Assert.Contains("on all 2", AutomationProperties.GetName(item.Chip)));
        }
    }

    [Fact]
    public void AssignedChipNamesAndTheCounterFollowTheLanguage()
    {
        PrivateTagDefinition tag = new(new TagId(Guid.NewGuid()), Name("macro"));
        Texts.SetLanguage("de");
        Assert.Equal("macro, an diesem Medium. Entf entfernt ihn.", QuickTagSurface.AccessibleName(new QuickTagAssignment(tag, TagToggleState.All, 1, 1), false));
        Assert.Equal("1 Medium", Texts.Count("tags.quick.files", 1));
        Assert.Equal("3 Medien", Texts.Count("tags.quick.files", 3));
        Texts.SetLanguage("ru");
        Assert.Equal(new[] { "1 медиафайл", "3 медиафайла", "5 медиафайлов", "21 медиафайл" },
            new[] { 1, 3, 5, 21 }.Select(count => Texts.Count("tags.quick.files", count)));
        Texts.SetLanguage("pl");
        Assert.Equal(new[] { "1 plik multimedialny", "2 pliki multimedialne", "5 plików multimedialnych", "22 pliki multimedialne" },
            new[] { 1, 2, 5, 22 }.Select(count => Texts.Count("tags.quick.files", count)));
        Texts.SetLanguage("cs");
        Assert.Equal(new[] { "1 mediální soubor", "4 mediální soubory", "5 mediálních souborů" },
            new[] { 1, 4, 5 }.Select(count => Texts.Count("tags.quick.files", count)));
    }

    // ---- languages ----------------------------------------------------------------------------------------------

    [Fact]
    public void EveryQuickTagKeyIsTranslatedInAllTwelveLanguagesWithTheSourcePlaceholders()
    {
        string[] keys = QuickTagCatalog.Entries.Select(entry => entry.Key).ToArray();
        Assert.All(keys, key => Assert.StartsWith("tags.quick.", key));
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(QuickTagCatalog.Entries, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.English));
            Assert.False(string.IsNullOrWhiteSpace(entry.German));
            Assert.Equal(Placeholders(entry.English), Placeholders(entry.German));
            Assert.Equal(entry.English, BuiltInCatalog.English[entry.Key]);
        });
        string[] codes = { "es", "fr", "ru", "zh-Hans", "ja", "ko", "pt-BR", "cs", "it", "pl" };
        Assert.Equal(codes.Order(StringComparer.Ordinal), Texts.BundledPackCodes().Order(StringComparer.Ordinal));
        foreach (string code in codes)
        {
            using Stream stream = typeof(Texts).Assembly.GetManifestResourceStream(Texts.BundledPackResourcePrefix + code + ".json")!;
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement strings = document.RootElement.GetProperty("strings");
            bool hasOne = PluralRules.Category(code, 1) == "one";
            foreach ((string key, string english, _) in QuickTagCatalog.Entries)
            {
                if (key.EndsWith(".one", StringComparison.Ordinal) && !hasOne) { Assert.False(strings.TryGetProperty(key, out _), code + " " + key); continue; }
                Assert.True(strings.TryGetProperty(key, out JsonElement value), code + " lacks " + key);
                string text = value.GetString()!;
                Assert.False(string.IsNullOrWhiteSpace(text));
                Assert.Equal(Placeholders(english), Placeholders(text));
            }
            if (PluralRules.Category(code, 2) == "few") Assert.True(strings.TryGetProperty("tags.quick.files.few", out _), code + " lacks the few form");
        }
    }

    [Fact]
    public void ReasonTextsAreShortEnoughToFollowTheNameInATooltip()
    {
        foreach (QuickTagReason reason in Enum.GetValues<QuickTagReason>())
            Assert.InRange(QuickTagReasons.Text(reason).Length, 1, 24);
        Assert.Equal("same file in /g/", QuickTagReasons.Text(QuickTagReason.SameFile, "/g/"));
        Texts.SetLanguage("de");
        Assert.Equal("aus diesem Thread", QuickTagReasons.Text(QuickTagReason.Thread));
    }

    private static string[] Placeholders(string text) => System.Text.RegularExpressions.Regex.Matches(text, @"\{\d+(:[^}]*)?\}")
        .Select(match => match.Value).Distinct().Order(StringComparer.Ordinal).ToArray();
}
