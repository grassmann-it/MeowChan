using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

// PT5 (PLAN-POWER-TAGGEN-2026-11-14): the stamp set as pinned suggestions. The stamp model, the source decorator (stamp first, inner
// source after, never longer than the suggestion count), applying the stamp to a medium or a selection with one undo step per tag,
// the key route and the chips the surface builds (badge, reason, accessible name, context menu).
[Collection("Localization")]
public sealed class QuickTagStampSourceTests : IDisposable
{
    private static readonly DateTimeOffset Day = new(2026, 11, 14, 0, 0, 0, TimeSpan.Zero);

    public QuickTagStampSourceTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
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

    // ---- the stamp model ----------------------------------------------------------------------------------------

    [Fact]
    public void TheStampReadsSavedNamesLeniently()
    {
        string?[] saved = { "surface", " WIP ", null, "", "   ", "Surface", "tab\tbad", "/g/", "extra" };
        QuickTagStamp stamp = new(saved, capacity: 4);

        // Blank, invalid and repeated (case-insensitive) names are dropped; the rest is cut at the capacity.
        Assert.Equal(new[] { "surface", "WIP", "/g/", "extra" }, stamp.ToNames());
        Assert.True(stamp.IsFull);
        Assert.Empty(new QuickTagStamp(null, 9).Names);
        Assert.Equal(QuickTagOptions.MinimumStampSize, new QuickTagStamp(null, -5).Capacity);
        Assert.Equal(QuickTagOptions.MaximumStampSize, new QuickTagStamp(null, 500).Capacity);
        Assert.Equal((9, 1, 18), (QuickTagOptions.DefaultStampSize, QuickTagOptions.MinimumStampSize, QuickTagOptions.MaximumStampSize));
        Assert.Equal(1, new QuickTagOptions { StampSize = 0 }.Normalized().StampSize);
        Assert.Equal(18, new QuickTagOptions { StampSize = 99 }.Normalized().StampSize);
        Assert.Equal(7, new QuickTagOptions { StampSize = 7 }.CreateStamp(new[] { "a" }).Capacity);
    }

    [Fact]
    public void AddingRefusesWhenFullIgnoresRepeatsAndRaisesChangedOnlyForRealChanges()
    {
        QuickTagStamp stamp = new(new[] { "a" }, capacity: 2);
        int changes = 0;
        stamp.Changed += () => changes++;

        Assert.Equal(QuickTagStampAdd.AlreadyThere, stamp.Add(Name("A")));
        Assert.Equal(0, changes);
        Assert.Equal(QuickTagStampAdd.Added, stamp.Add(Name("b")));
        Assert.Equal(QuickTagStampAdd.Full, stamp.Add(Name("c")));
        Assert.Equal(1, changes);
        Assert.Equal(new[] { "a", "b" }, stamp.ToNames());

        Assert.False(stamp.Remove(Name("zzz")));
        Assert.True(stamp.Remove(Name("A")));
        Assert.Equal(2, changes);
        stamp.Replace(new[] { "b" }); // the same content: no event
        Assert.Equal(2, changes);
        stamp.Replace(new[] { "x", "y", "z" }); // cut at the capacity
        Assert.Equal(new[] { "x", "y" }, stamp.ToNames());
        Assert.Equal(3, changes);

        stamp.Capacity = 1; // lowering shortens the stamp from its end
        Assert.Equal(new[] { "x" }, stamp.ToNames());
        Assert.Equal(4, changes);
        Assert.True(stamp.Clear());
        Assert.False(stamp.Clear());
        Assert.Equal(5, changes);
    }

    [Fact]
    public void ResolveFindsTheTagsByNameInStampOrderAndSkipsNamesWithoutATag()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId surface = store.Define("surface").Tag, wip = store.Define("wip").Tag;
        QuickTagStamp stamp = new(new[] { "WIP", "gone", "Surface" });

        Assert.Equal(new[] { wip, surface }, stamp.Resolve(store.ReadSnapshot()).Select(tag => tag.Id));
        Assert.Empty(stamp.Resolve(null));
        Assert.Equal(new[] { "WIP", "gone", "Surface" }, stamp.ToNames()); // the saved names stay as they are
    }

    // ---- the source decorator -----------------------------------------------------------------------------------

    private static PrivateTagStoreSnapshot Vocabulary(PrivateTagStore store, params string[] names)
    {
        foreach (string name in names) store.Define(name);
        return store.ReadSnapshot();
    }

    private static TagId IdOf(PrivateTagStoreSnapshot snapshot, string name) => snapshot.Tags.Single(tag => tag.Name.Display == name).Id;

    [Fact]
    public void StampTagsComeFirstInStampOrderThenTheInnerSourceAndNeverMoreThanTheLimit()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        PrivateTagStoreSnapshot snapshot = Vocabulary(store, "a", "b", "c", "d", "e", "f");
        List<QuickTagSuggestionRequest> asked = new();
        DelegateQuickTagSuggestions inner = new(request =>
        {
            asked.Add(request);
            return new QuickTagSuggestion[]
            {
                new(Name("e"), IdOf(request.Snapshot, "e"), 3, QuickTagReason.Frequent, 5),
                new(Name("B"), IdOf(request.Snapshot, "b"), 2, QuickTagReason.Recent, 4), // a stamp tag again: dropped
                new(Name("f"), IdOf(request.Snapshot, "f"), 1, QuickTagReason.Thread, 1),
            };
        });
        QuickTagStampSource source = new(inner, new QuickTagStamp(new[] { "c", "gone", "a", "b" }));
        HashSet<TagId> carried = new() { IdOf(snapshot, "d") };

        IReadOnlyList<QuickTagSuggestion> shown = source.Suggest(new QuickTagSuggestionRequest(snapshot, carried, 5));

        Assert.Equal(new[] { "c", "a", "b", "e", "f" }, shown.Select(item => item.Name.Display));
        Assert.All(shown.Take(3), item => Assert.Equal(QuickTagReason.Stamp, item.Reason));
        Assert.All(shown.Take(3), item => Assert.Equal(QuickTagStampSource.StampScore, item.Score));
        Assert.Equal(QuickTagReason.Frequent, shown[3].Reason);
        // The inner source is asked not to offer the stamp tags again, and keeps the carried exclusions.
        Assert.Contains(IdOf(snapshot, "a"), asked[0].Excluded);
        Assert.Contains(IdOf(snapshot, "c"), asked[0].Excluded);
        Assert.Contains(IdOf(snapshot, "d"), asked[0].Excluded);

        // A short limit cuts the inner suggestions first, and finally the stamp itself; it never exceeds the limit.
        Assert.Equal(new[] { "c", "a", "b", "e" }, source.Suggest(new QuickTagSuggestionRequest(snapshot, carried, 4)).Select(item => item.Name.Display));
        Assert.Equal(new[] { "c", "a" }, source.Suggest(new QuickTagSuggestionRequest(snapshot, carried, 2)).Select(item => item.Name.Display));
        Assert.Empty(source.Suggest(new QuickTagSuggestionRequest(snapshot, carried, 0)));
    }

    [Fact]
    public void AStampTagTheTargetCarriesIsNotASuggestionAndDoesNotComeBackFromTheInnerSource()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        PrivateTagStoreSnapshot snapshot = Vocabulary(store, "a", "b");
        // The most used tags: with "a" carried the inner source must not return it (it is excluded) and "b" is a stamp tag.
        QuickTagStampSource source = new(FrequentQuickTagSuggestions.Instance, new QuickTagStamp(new[] { "a", "b" }));
        IReadOnlyList<QuickTagSuggestion> shown = source.Suggest(new QuickTagSuggestionRequest(snapshot, new HashSet<TagId> { IdOf(snapshot, "a") }, 9));

        Assert.Equal(new[] { "b" }, shown.Select(item => item.Name.Display));
        Assert.Equal(QuickTagReason.Stamp, shown[0].Reason);
    }

    [Fact]
    public void AnEmptyStampLeavesTheInnerSourceAsItIs()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        PrivateTagStoreSnapshot snapshot = Vocabulary(store, "a");
        DelegateQuickTagSuggestions inner = new(request => new QuickTagSuggestion[] { new(Name("a"), IdOf(request.Snapshot, "a"), 1, QuickTagReason.Recent, 0) });
        QuickTagStampSource source = new(inner, new QuickTagStamp());

        QuickTagSuggestion only = Assert.Single(source.Suggest(new QuickTagSuggestionRequest(snapshot, new HashSet<TagId>(), 9)));
        Assert.Equal(QuickTagReason.Recent, only.Reason);
        Assert.Same(inner, source.Inner);
    }

    // ---- applying the stamp -------------------------------------------------------------------------------------

    [Fact]
    public async Task ApplyingTheStampGivesTheMissingTagsOneUndoStepEachAndCtrlZTakesThemBackOneByOne()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        MediaLibraryEntry entry = Entry("a", 'a');
        TagId a = store.Define("a").Tag, b = store.Define("b").Tag, c = store.Define("c").Tag;
        store.SetMedia(c, MediaLibraryFilterEngine.TagTarget(entry)!, true); // already there: not written again
        QuickTagStamp stamp = new(new[] { "a", "b", "c" });
        using QuickTagSession session = new(QuickTagTarget.For(Single(store, entry)), stamp: stamp);
        List<TagDialogChange> applied = new();
        session.Applied += applied.Add;

        Assert.Equal(new[] { a, b, c }, session.StampSet);
        Assert.True(session.CanApplyStamp);
        int given = await session.ApplyStampAsync();

        Assert.Equal(2, given);
        Assert.Equal(new[] { "a", "b", "c" }, NamesOn(store, entry));
        Assert.Equal(2, session.UndoSteps.Count);
        Assert.Equal(new[] { "a", "b" }, applied.Select(change => change.Name));
        Assert.Equal(QuickTagStatusKind.StampApplied, session.Status.Kind);
        Assert.Equal("Stamp set: a, b.", session.Status.Text);
        Assert.False(session.CanApplyStamp); // everything is set

        await session.UndoAsync();
        Assert.Equal(new[] { "a", "c" }, NamesOn(store, entry));
        await session.UndoAsync();
        Assert.Equal(new[] { "c" }, NamesOn(store, entry)); // the tag that was there before stays

        // Applying again with everything set writes nothing and says so.
        await session.ApplyStampAsync();
        await session.ApplyStampAsync();
        Assert.Equal(new[] { "a", "b", "c" }, NamesOn(store, entry));
        int undoSteps = session.UndoSteps.Count;
        Assert.Equal(0, await session.ApplyStampAsync());
        Assert.Equal(undoSteps, session.UndoSteps.Count);
        Assert.Equal(QuickTagStatusKind.StampComplete, session.Status.Kind);
        Assert.Equal("All stamp tags are already set.", session.Status.Text);
        Assert.Equal("Alle Stempel-Tags sind schon gesetzt.", BuiltInCatalog.German["tags.quick.stamp.complete"]);
    }

    [Fact]
    public async Task AnEmptyStampOrOneWithoutExistingTagsWritesNothingAndTheStatusSaysWhy()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        MediaLibraryEntry entry = Entry("a", 'a');
        using QuickTagSession empty = new(QuickTagTarget.For(Single(store, entry)));
        using QuickTagSession missing = new(QuickTagTarget.For(Single(store, entry)), stamp: new QuickTagStamp(new[] { "no_such_tag" }));

        Assert.False(empty.CanApplyStamp);
        Assert.Equal(0, await empty.ApplyStampAsync());
        Assert.Equal(QuickTagStatusKind.StampEmpty, empty.Status.Kind);
        Assert.Equal("The stamp is empty. Right-click a tag to add it.", empty.Status.Text);
        Assert.Equal(0, await missing.ApplyStampAsync());
        Assert.Equal(QuickTagStatusKind.StampEmpty, missing.Status.Kind);
        Assert.Empty(store.ReadSnapshot().Tags); // applying the stamp never creates a tag
        Assert.Empty(empty.UndoSteps);
    }

    [Fact]
    public async Task OnASelectionTheStampGivesTagsThatOnlySomeCarryToAllAndAUnanimousOneIsLeftAlone()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        MediaLibraryEntry first = Entry("a", 'a'), second = Entry("b", 'b');
        TagId some = store.Define("some").Tag, all = store.Define("all").Tag, none = store.Define("none").Tag;
        store.SetMedia(some, MediaLibraryFilterEngine.TagTarget(first)!, true);
        store.SetMedia(all, MediaLibraryFilterEngine.TagTarget(first)!, true);
        store.SetMedia(all, MediaLibraryFilterEngine.TagTarget(second)!, true);
        using QuickTagSession session = new(QuickTagTarget.For(Selection(store, first, second)), stamp: new QuickTagStamp(new[] { "all", "some", "none" }));

        Assert.Equal(2, await session.ApplyStampAsync());

        Assert.Equal(new[] { "all", "none", "some" }, NamesOn(store, first));
        Assert.Equal(new[] { "all", "none", "some" }, NamesOn(store, second));
        Assert.Equal(new[] { 1, 2 }, session.UndoSteps.Select(change => change.Subjects.Count)); // only the files that lacked it
        Assert.Equal(new[] { some, none }, session.UndoSteps.Select(change => change.Tag));
        Assert.Equal(all, session.StampSet[0]);
    }

    [Fact]
    public async Task ARefusedWriteStopsTheStampRunAndKeepsItsErrorInTheStatus()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        MediaLibraryEntry first = Entry("a", 'a'), second = Entry("b", 'b');
        store.Define("a"); store.Define("b");
        using QuickTagSession session = new(QuickTagTarget.For(Selection(store, first, second)), stamp: new QuickTagStamp(new[] { "a", "b" }));

        store.Define("other"); // the vocabulary changed after the session looked: the revision-bound write is refused
        Assert.Equal(0, await session.ApplyStampAsync());

        Assert.True(session.Status.IsError);
        Assert.Equal(Texts.T("tags.error.stale"), session.Status.Text);
        Assert.Empty(session.UndoSteps);
        Assert.Empty(NamesOn(store, first));
        Assert.Equal(2, await session.ApplyStampAsync()); // reloaded: the retry works
    }

    [Fact]
    public void TheStampIsEditedByTagIdAndAFullStampSaysSoInTheStatus()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId a = store.Define("a").Tag, b = store.Define("b").Tag;
        using QuickTagSession session = new(QuickTagTarget.For(Single(store, Entry("x", 'c'))), stamp: new QuickTagStamp(null, capacity: 1));
        int updates = 0;
        session.Updated += () => updates++;

        Assert.True(session.AddToStamp(a));
        Assert.True(session.InStamp(a));
        Assert.Equal("“a” added to the stamp.", session.Status.Text);
        Assert.False(session.AddToStamp(b));
        Assert.Equal(QuickTagStatusKind.StampFull, session.Status.Kind);
        Assert.True(session.Status.IsError);
        Assert.Equal("The stamp is full (limit: 1). Remove a tag from it first.", session.Status.Text);
        Assert.False(session.InStamp(b));
        Assert.True(session.AddToStamp(a)); // already in: nothing to do
        Assert.True(session.RemoveFromStamp(a));
        Assert.Equal("“a” removed from the stamp.", session.Status.Text);
        Assert.False(session.RemoveFromStamp(a));
        Assert.False(session.AddToStamp(new TagId(Guid.NewGuid()))); // a tag that does not exist
        Assert.True(updates >= 3);
        Assert.Empty(session.StampSet);
    }

    [Fact]
    public void AStampSharedByTwoSessionsReachesBothAndADisposedSessionStopsListening()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId a = store.Define("a").Tag;
        QuickTagStamp stamp = new();
        QuickTagSession first = new(QuickTagTarget.For(Single(store, Entry("x", 'c'))), stamp: stamp);
        using QuickTagSession second = new(QuickTagTarget.For(Single(store, Entry("y", 'd'))), stamp: stamp);
        int firstUpdates = 0, secondUpdates = 0;
        first.Updated += () => firstUpdates++;
        second.Updated += () => secondUpdates++;

        stamp.Add(Name("a"));
        Assert.Equal((1, 1), (firstUpdates, secondUpdates));
        Assert.Equal(new[] { a }, second.StampSet);

        first.Dispose();
        stamp.Remove(Name("a"));
        Assert.Equal((1, 2), (firstUpdates, secondUpdates));
    }

    // ---- key route and commands ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, true, true)]
    [InlineData(0, false, false)]
    [InlineData(1, false, false)]
    [InlineData(2, false, true)]
    [InlineData(3, false, false)]
    public void CtrlShiftEnterAppliesTheStampInEveryFocusStateWithAndWithoutNavigation(int focusState, bool listOpen, bool navigation)
    {
        QuickTagFocus focus = (QuickTagFocus)focusState; // Field, Suggestions, Assigned, Other
        KeyModifiers chord = KeyModifiers.Control | KeyModifiers.Shift;
        Assert.Equal(QuickTagKeyAction.ApplyStamp, QuickTagKeys.Route(Key.Enter, chord, focus, listOpen, fieldEmpty: false, navigation, ShortcutPlatform.Windows).Action);
        Assert.Equal(QuickTagKeyAction.ApplyStamp, QuickTagKeys.Route(Key.Enter, KeyModifiers.Meta | KeyModifiers.Shift, focus, listOpen, true, navigation, ShortcutPlatform.MacOS).Action);
        // Ctrl+Enter alone is still "add and move on", Shift+Enter and Ctrl+Alt+Shift+Enter are not the stamp.
        Assert.Equal(QuickTagKeyAction.CommitAndNext, QuickTagKeys.Route(Key.Enter, KeyModifiers.Control, QuickTagFocus.Assigned, false, true, true, ShortcutPlatform.Windows).Action);
        Assert.NotEqual(QuickTagKeyAction.ApplyStamp, QuickTagKeys.Route(Key.Enter, KeyModifiers.Shift, focus, listOpen, true, navigation, ShortcutPlatform.Windows).Action);
        Assert.NotEqual(QuickTagKeyAction.ApplyStamp, QuickTagKeys.Route(Key.Enter, chord | KeyModifiers.Alt, focus, listOpen, true, navigation, ShortcutPlatform.Windows).Action);
        // On macOS Ctrl+Shift+Enter is not the stamp (⌘ is the primary key there).
        Assert.NotEqual(QuickTagKeyAction.ApplyStamp, QuickTagKeys.Route(Key.Enter, chord, focus, listOpen, true, navigation, ShortcutPlatform.MacOS).Action);
    }

    [Fact]
    public void TheStampCommandIsACatalogCommandOnEverySurfaceWithTheChordAsDefault()
    {
        Assert.Contains(QuickTagCommands.StampApply, QuickTagCommands.All);
        CommandDefinition command = CommandCatalog.Get(QuickTagCommands.StampApply);
        Assert.Equal("tags.quick.stamp-apply", command.Id);
        Assert.Equal(CommandScope.Gallery | CommandScope.Media | CommandScope.Reader | CommandScope.Archive, command.Scope);
        Assert.Equal(new[] { "gallery-tag-panel", "quick-tag-popover" }, command.Contexts);
        foreach (ShortcutPlatform platform in Enum.GetValues<ShortcutPlatform>())
            Assert.Equal(platform == ShortcutPlatform.MacOS ? "⇧⌘↩" : "Ctrl+Shift+Enter",
                string.Join(" / ", EffectiveShortcutBindings.Defaults(command, platform).Select(gesture => gesture.Display(platform))));
        Assert.Equal("Ctrl+Shift+Enter", QuickTagKeys.StampGesture(ShortcutPlatform.Linux));
        Assert.Equal("⇧⌘↩", QuickTagKeys.StampGesture(ShortcutPlatform.MacOS));
    }

    // ---- the surface --------------------------------------------------------------------------------------------

    private static (QuickTagSurface Surface, QuickTagSession Session) Surface(IQuickTagTarget target, QuickTagStamp stamp, IQuickTagSuggestionSource? source = null)
    {
        QuickTagSession session = new(target, stamp: stamp);
        QuickTagSurface surface = new(session, platform: ShortcutPlatform.Linux) { TargetLabel = "f.jpg" };
        if (source != null) surface.Suggestions = source;
        return (surface, session);
    }

    [Fact]
    public async Task TheStripShowsTheStampFirstWithABadgeTheReasonStampAndAContextMenuToEditIt()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        MediaLibraryEntry entry = Entry("a", 'a');
        TagId a = store.Define("a").Tag, b = store.Define("b").Tag, other = store.Define("other").Tag;
        DelegateQuickTagSuggestions source = new(request => new QuickTagSuggestion[]
        {
            new(Name("other"), other, 5, QuickTagReason.Thread, 3), new(Name("b"), b, 4, QuickTagReason.Recent, 2),
        });
        (QuickTagSurface surface, QuickTagSession session) = Surface(QuickTagTarget.For(Single(store, entry)), new QuickTagStamp(new[] { "a", "b" }), source);
        using (session)
        using (surface)
        {
            // The stamp (a, b) stands in front of the host's own source: the surface decorates whatever source it gets.
            Assert.Equal(new[] { "a", "b", "other" }, surface.Shown.Select(item => item.Name.Display));
            Assert.Equal(new[] { QuickTagReason.Stamp, QuickTagReason.Stamp, QuickTagReason.Thread }, surface.Shown.Select(item => item.Reason));
            Button[] chips = surface.SuggestionButtons.ToArray();
            Assert.Equal("a, suggestion 1 of 3, stamp, number of uses: 0. Enter adds it.", AutomationProperties.GetName(chips[0]));
            Assert.Equal("a · stamp · Alt+1", ToolTip.GetTip(chips[0]));
            Assert.Equal("Alt+2", AutomationProperties.GetAcceleratorKey(chips[1]));

            // A stamp chip carries the badge (a path, not a text glyph); an ordinary chip does not.
            Assert.Single(StampBadges(chips[0]));
            Assert.Single(StampBadges(chips[1]));
            Assert.Empty(StampBadges(chips[2]));
            Assert.IsType<Avalonia.Controls.Shapes.Path>(StampBadges(chips[0]).Single().Child);
            Assert.Contains("chip-keyed", chips[0].Classes);

            // The context menu of an offered own tag adds it to the stamp or takes it out (filled when it opens).
            Assert.All(new[] { chips[0], chips[1], chips[2] }, chip => Assert.NotNull(chip.ContextMenu));
            MenuItem remove = Assert.Single(surface.StampMenuItems(a));
            Assert.Equal("Remove from stamp", remove.Header);
            MenuItem add = Assert.Single(surface.StampMenuItems(other));
            Assert.Equal("Add to stamp", add.Header);
            add.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(new[] { "a", "b", "other" }, session.Stamp.ToNames());
            surface.Refresh();
            Assert.Equal(QuickTagReason.Stamp, surface.Shown[2].Reason);
            Assert.Equal("Remove from stamp", Assert.Single(surface.StampMenuItems(other)).Header);
            Assert.Equal("“other” added to the stamp.", surface.Status.Text);

            // An assigned chip offers the same menu, and removing a tag from the stamp does not unassign it.
            await surface.ToggleSuggestionAsync(0);
            surface.Refresh();
            Assert.Equal(new[] { "b", "other" }, surface.Shown.Select(item => item.Name.Display));
            Assert.NotNull(surface.AssignedButtons.Single().Chip.ContextMenu);
            MenuItem assignedMenu = Assert.Single(surface.StampMenuItems(a));
            Assert.Equal("Remove from stamp", assignedMenu.Header);
            assignedMenu.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            surface.Refresh();
            Assert.Equal(new[] { "a" }, NamesOn(store, entry));
            Assert.Equal(new[] { "b", "other" }, session.Stamp.ToNames());
            Assert.Equal(new[] { a }, session.Assignments().Select(item => item.Tag.Id));
            Assert.Equal("Add to stamp", Assert.Single(surface.StampMenuItems(a)).Header);
        }
    }

    private static IReadOnlyList<Border> StampBadges(Button chip) =>
        Descendants(chip.Content as Control).OfType<Border>().Where(border => border.Name == "StampBadge").ToArray();

    private static IEnumerable<Control> Descendants(Control? root)
    {
        if (root == null) yield break;
        yield return root;
        IEnumerable<Control> children = root switch
        {
            Panel panel => panel.Children,
            Decorator decorator => decorator.Child is { } child ? new[] { child } : Array.Empty<Control>(),
            ContentControl content => content.Content is Control inner ? new[] { inner } : Array.Empty<Control>(),
            _ => Array.Empty<Control>()
        };
        foreach (Control child in children)
            foreach (Control nested in Descendants(child)) yield return nested;
    }

    [Fact]
    public async Task TheFooterButtonAndTheKeyApplyTheStampAndReportItToTheHost()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        MediaLibraryEntry entry = Entry("a", 'a');
        store.Define("a"); store.Define("b");
        QuickTagStamp stamp = new();
        (QuickTagSurface surface, QuickTagSession session) = Surface(QuickTagTarget.For(Single(store, entry)), stamp);
        using (session)
        using (surface)
        {
            int reported = 0;
            surface.StampApplied += () => reported++;
            Assert.False(surface.StampButton.IsEnabled); // an empty stamp: nothing to give
            Assert.Equal("The stamp is empty: right-click a tag to add it · Ctrl+Shift+Enter", AutomationProperties.GetName(surface.StampButton));
            await surface.ApplyStampAsync();
            surface.Refresh();
            Assert.Equal(0, reported);
            Assert.Equal("The stamp is empty. Right-click a tag to add it.", surface.Status.Text);

            stamp.Replace(new[] { "a", "b" });
            surface.Refresh();
            Assert.True(surface.StampButton.IsEnabled);
            Assert.Equal("Set all stamp tags (a, b) · Ctrl+Shift+Enter", AutomationProperties.GetName(surface.StampButton));
            Assert.Equal("Ctrl+Shift+Enter", AutomationProperties.GetAcceleratorKey(surface.StampButton));
            Assert.Equal(new[] { "a", "b" }, surface.Shown.Select(item => item.Name.Display));

            Assert.True(surface.Execute(QuickTagKeys.Route(Key.Enter, KeyModifiers.Control | KeyModifiers.Shift, QuickTagFocus.Field,
                listOpen: false, fieldEmpty: true, navigation: false, ShortcutPlatform.Linux)));
            await WaitUntil(() => NamesOn(store, entry).Count == 2);
            surface.Refresh();

            Assert.Equal(new[] { "a", "b" }, NamesOn(store, entry));
            Assert.Equal(1, reported);
            Assert.Empty(surface.Shown); // both are assigned chips now
            Assert.True(surface.StampButton.IsEnabled); // stays usable: pressing it again says that everything is set
            Assert.Equal("Stamp set: a, b.", surface.Status.Text);

            // Ctrl+Z twice takes both back; the stamp itself is not an undo step.
            await surface.UndoAsync();
            await surface.UndoAsync();
            Assert.Empty(NamesOn(store, entry));
            Assert.Equal(new[] { "a", "b" }, stamp.ToNames());
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 200 && !condition(); attempt++) await Task.Delay(10);
        Assert.True(condition());
    }

    [Fact]
    public void StampTextsExistInBothBuiltInLanguagesWithMatchingPlaceholdersAndAShortReason()
    {
        foreach ((string key, string english, string german) in QuickTagCatalog.Entries.Where(entry => entry.Key.Contains("stamp", StringComparison.Ordinal)))
        {
            Assert.Equal(PlaceholdersOf(english), PlaceholdersOf(german));
            Assert.Equal(english, BuiltInCatalog.English[key]);
            Assert.Equal(german, BuiltInCatalog.German[key]);
        }
        Assert.Equal("stamp", QuickTagReasons.Text(QuickTagReason.Stamp));
        Texts.SetLanguage("de");
        Assert.Equal("Stempel", QuickTagReasons.Text(QuickTagReason.Stamp));
        Assert.Equal("Stempel gesetzt: a, b.", new QuickTagStatus(QuickTagStatusKind.StampApplied, "a, b").Text);
        Assert.Equal("Der Stempel ist voll (Grenze: 9). Zuerst einen Tag daraus entfernen.", new QuickTagStatus(QuickTagStatusKind.StampFull, Files: 9).Text);
    }

    private static string[] PlaceholdersOf(string text) => System.Text.RegularExpressions.Regex.Matches(text, @"\{\d+(:[^}]*)?\}")
        .Select(match => match.Value).Distinct().Order(StringComparer.Ordinal).ToArray();
}
