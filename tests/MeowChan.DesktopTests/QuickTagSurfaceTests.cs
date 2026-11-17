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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NavigationAndSuggestionHelpFollowTheEffectiveBindings(bool rebound) => BooruComponentDispatcher.Run(() =>
    {
        string[] commands = [QuickTagCommands.Next, QuickTagCommands.Previous, QuickTagCommands.CommitNext,
            QuickTagCommands.MarkNext, QuickTagCommands.MarkPrevious, QuickTagCommands.ThreadSection];
        var previous = ShortcutService.CurrentSection;
        var section = new ShortcutBindingsSection();
        if (rebound)
            for (int i = 0; i < commands.Length; i++) section = section.Set(commands[i], [ShortcutGesture.Parse("Alt+F" + (i + 1))]);
        ShortcutService.Publish(section);
        try
        {
            using TempDirectory temp = new();
            using ProfileLease lease = ProfileLease.Acquire(temp.Path);
            PrivateTagStore store = new(temp.Path, lease);
            using QuickTagSession session = new(QuickTagTarget.For(Single(store, Entry("discovery", 'a'))));
            using QuickTagSurface surface = new(session) { NavigationEnabled = true };
            string help = AutomationProperties.GetHelpText(surface.Input);
            foreach (string command in commands)
            {
                var gestures = ShortcutService.Current.For(command);
                Assert.NotEmpty(gestures);
                foreach (var gesture in gestures) Assert.Contains(gesture.Display(ShortcutService.Current.Platform), help);
            }
            surface.NavigationEnabled = false;
            string localOnly = AutomationProperties.GetHelpText(surface.Input);
            Assert.DoesNotContain(CommandCatalog.Label(QuickTagCommands.Next), localOnly);
        }
        finally { ShortcutService.Publish(previous); }
    });

    [Fact]
    public void GalleryCounterMovesBesidePostTagsAndKeepsLiveTextVisibilityAndAccessibleName()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        using QuickTagSession session = new(QuickTagTarget.For(Single(store, Entry("a", 'a'))));
        using QuickTagSurface surface = new(session);
        surface.CounterText = "Medium 4 of 27";
        Border counter = surface.DetachCounter();
        GallerySidePanel panel = new(new Button()) { HeaderCounter = counter };
        Assert.DoesNotContain(counter, surface.Header.Children);
        Assert.Same(counter, panel.HeaderCounter);
        Control slot = (Control)counter.Parent!;
        Assert.Same(panel, slot.Parent);
        Assert.Equal(0, Grid.GetRow(slot));
        Assert.Equal(Grid.GetRow((Control)panel.PostButton.Parent!), Grid.GetRow(slot));
        Assert.Equal(1, Grid.GetColumn(slot));
        Assert.Equal(2, Grid.GetColumn(panel.CloseButton));
        Assert.False(slot.Focusable);
        Assert.Equal(24, slot.Margin.Right);
        TextBlock label = Assert.IsType<TextBlock>(counter.Child);
        Assert.Equal("Medium 4 of 27", label.Text);
        Assert.Equal(Avalonia.Media.TextWrapping.Wrap, label.TextWrapping);
        Assert.Equal(2, label.MaxLines);
        Assert.Equal(Avalonia.Media.TextTrimming.CharacterEllipsis, label.TextTrimming);
        surface.CounterText = "Medium 5 von mindestens 12345 · markiert";
        Assert.Equal(surface.CounterText, label.Text);
        Assert.Equal(surface.CounterText, AutomationProperties.GetName(counter));
        Assert.Equal(surface.CounterText, ToolTip.GetTip(counter));
        surface.CounterText = null; // tagging mode suppresses this counter in either header
        Assert.False(counter.IsVisible);
        surface.CounterText = "Medium 5 of 27";
        Assert.True(counter.IsVisible);
        panel.HeaderCounter = null;
        Assert.Null(counter.Parent);
    }

    [Fact]
    public void GalleryMovesTheBadgeAfterPoliteAnnouncementsAreEnabledAndClearsItOnRelease()
    {
        string source = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../Desktop/GalleryViewer.TagPanel.cs")));
        Assert.True(source.IndexOf("AnnounceCounter(surface);", StringComparison.Ordinal)
            < source.IndexOf("_sidePanel.HeaderCounter = surface.DetachCounter();", StringComparison.Ordinal));
        Assert.Contains("_sidePanel.HeaderCounter = null;", source);
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
    // Escape is layered and never needs more than two presses (Tom, 16.11.2026): text in the field goes first (with its list), an open list over an
    // empty field next, then the surface closes, from any of its elements.
    [InlineData(Key.Escape, KeyModifiers.None, QuickTagFocus.Field, true, false, ShortcutPlatform.Linux, QuickTagKeyAction.ClearField, 0)]
    [InlineData(Key.Escape, KeyModifiers.None, QuickTagFocus.Field, false, false, ShortcutPlatform.Linux, QuickTagKeyAction.ClearField, 0)]
    [InlineData(Key.Escape, KeyModifiers.None, QuickTagFocus.Field, true, true, ShortcutPlatform.Linux, QuickTagKeyAction.CloseList, 0)]
    [InlineData(Key.Escape, KeyModifiers.None, QuickTagFocus.Field, false, true, ShortcutPlatform.Linux, QuickTagKeyAction.Close, 0)]
    [InlineData(Key.Escape, KeyModifiers.None, QuickTagFocus.Assigned, false, true, ShortcutPlatform.Linux, QuickTagKeyAction.Close, 0)]
    [InlineData(Key.Escape, KeyModifiers.None, QuickTagFocus.Suggestions, false, false, ShortcutPlatform.Linux, QuickTagKeyAction.Close, 0)]
    [InlineData(Key.Escape, KeyModifiers.None, QuickTagFocus.Other, false, false, ShortcutPlatform.Linux, QuickTagKeyAction.Close, 0)]
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
        Assert.Equal(string.Join(" / ", EffectiveShortcutBindings.Defaults(CommandCatalog.Get(QuickTagCommands.Suggestion(1)), ShortcutPlatform.MacOS)
            .Select(gesture => gesture.Display(ShortcutPlatform.MacOS))), QuickTagKeys.SuggestionGesture(0, ShortcutPlatform.MacOS));
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

    [Theory]
    [InlineData(QuickTagFocus.Field, true)]
    [InlineData(QuickTagFocus.Field, false)]
    [InlineData(QuickTagFocus.Assigned, true)]
    [InlineData(QuickTagFocus.Other, true)]
    public void InitialLettersOnlyActWhenTheSuggestionStripHasFocus(object focus, bool fieldEmpty)
    {
        QuickTagKeyRoute route = QuickTagKeys.Route(Key.S, KeyModifiers.None, (QuickTagFocus)focus,
            listOpen: false, fieldEmpty: fieldEmpty, navigation: false, ShortcutPlatform.Linux);

        Assert.Equal(QuickTagKeyAction.None, route.Action);
    }

    [Fact]
    public void MouseFocusRepairIsWiredForTheRatingPopoverField()
    {
        string source = File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThisFile())!, "../../Desktop/Controls/QuickTagSurface.cs")));
        Assert.Contains("_input.AddHandler(PointerPressedEvent, OnInputPointerPressed, RoutingStrategies.Tunnel);", source);
        Assert.Contains("e.Pointer.Type != PointerType.Mouse", source);
        // Popup suggestions are logical descendants, not editor visuals: their captured gestures must not reclaim focus.
        Assert.Contains("e.Source is not Visual source || (source != _input && !_input.IsVisualAncestorOf(source))", source);
        Assert.Contains("if (!_disposed && _input.IsEffectivelyEnabled && !_input.IsKeyboardFocusWithin) _input.Focus();", source);
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
            Assert.Contains(WorkspaceChips.TagPill, chips[0].Classes);      // the 28 dip tag pill
            Assert.Contains("chip-keyed", chips[0].Classes);                // a number hint leads: 8 dip start padding
            Assert.DoesNotContain(WorkspaceChips.TagPillAssigned, chips[0].Classes); // a suggestion is never the accent-tinted assigned look
            ChipOutline sourceChip = Assert.IsType<ChipOutline>(chips[2].Content);
            Assert.Contains(WorkspaceChips.TagPill, sourceChip.Classes);
            Assert.All(new[] { chips[0], chips[1] }, chip => Assert.Contains(WorkspaceChips.TagPillContent, Assert.IsType<StackPanel>(chip.Content).Classes));
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

    // R04 (review 14.11. night): what the user types for the next tag while a write runs is never cleared, and an Enter during
    // the write is taken once that write is done instead of being ignored.
    [Fact]
    public async Task TypingOnDuringAWriteKeepsTheNewDraftAndAnEnterMeanwhileIsTakenAfterwards()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        MediaLibraryEntry entry = Entry("a", 'a');
        // Continuations run inline on this thread (no context, synchronous completion): the controls are only touched here.
        SynchronizationContext? context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        TaskCompletionSource gate = new();
        bool hold = true;
        MediaTagSession single = new(new MediaTagSubject.Bound(MediaLibraryFilterEngine.TagTarget(entry)!), () => Load(store),
            async (name, subject, token) =>
            {
                if (hold) { hold = false; await gate.Task; }
                return store.DefineAndSetMediaSubjects(name, [subject], null, token);
            },
            (id, subject, assigned, token) => Task.FromResult(store.SetMediaSubjects(id, [subject], assigned, null, token)));
        (QuickTagSurface surface, QuickTagSession session) = Surface(store, QuickTagTarget.For(single));
        using (session)
        using (surface)
        {
            surface.Input.Text = "first";
            Task running = surface.AddTypedAsync();
            Assert.True(session.Busy);

            surface.Input.Text = "sec";          // typing on for the next tag
            await surface.AddTypedAsync();       // an Enter while busy: neither written nor dropped
            Assert.Equal("sec", surface.Input.Text);
            surface.Input.Text = "second";       // edited after that Enter: the deferred Enter no longer applies

            gate.SetResult();
            await running;
            Assert.Equal(new[] { "first" }, NamesOn(store, entry));
            Assert.Equal("second", surface.Input.Text); // the new draft survives the end of the first write

            // An Enter during a write whose text is still unchanged afterwards is taken right after it.
            hold = true;
            gate = new();
            surface.Input.Text = "third";
            running = surface.AddTypedAsync();
            surface.Input.Text = "fourth";
            await surface.AddTypedAsync();
            gate.SetResult();
            await running;
            Assert.Equal(new[] { "first", "fourth", "third" }, NamesOn(store, entry));
            Assert.True(string.IsNullOrEmpty(surface.Input.Text));
            Assert.Equal("fourth", session.UndoSteps[^1].Name);
        }
        SynchronizationContext.SetSynchronizationContext(context);
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
            Assert.Equal(HorizontalAlignment.Stretch, surface.Footer.HorizontalAlignment); // the tag editor sits at the right edge of the row
            Assert.Equal(new Control[] { surface.UndoButton, surface.StampButton, surface.AdvancedButton }, surface.Footer.Children.ToArray());
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

    // Quick tag footer (wave 6): three labelled buttons, stacked at full width in the side panel, in a row elsewhere.
    [Fact]
    public void TheFooterButtonsShowIconAndTextAndStackAtFullWidthInANarrowHost()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        (QuickTagSurface surface, QuickTagSession session) = Surface(store, QuickTagTarget.For(Single(store, Entry("a", 'a'))));
        using (session)
        using (surface)
        {
            Button[] buttons = { surface.UndoButton, surface.StampButton, surface.AdvancedButton };
            string[] icons = { "corner-up-left", "check-square", "external-link" };
            string[] texts = { Texts.T("tags.quick.undo"), Texts.T("tags.quick.stamp.button"), Texts.T("tags.quick.advanced") };
            void AssertLabels()
            {
                for (int index = 0; index < buttons.Length; index++)
                {
                    (string Icon, string Text)? label = surface.FooterLabelOf(buttons[index]);
                    Assert.Equal((icons[index], texts[index]), label);
                    // Never wraps: one line, trimmed with an ellipsis (the tooltip has the full text).
                    TextBlock text = surface.FooterTextOf(buttons[index])!;
                    Assert.Equal(1, text.MaxLines);
                    Assert.Equal(Avalonia.Media.TextWrapping.NoWrap, text.TextWrapping);
                    Assert.Equal(Avalonia.Media.TextTrimming.CharacterEllipsis, text.TextTrimming);
                    Assert.Equal(HorizontalAlignment.Left, buttons[index].HorizontalContentAlignment);
                    Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(buttons[index])));
                    Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(buttons[index])));
                    Assert.False(string.IsNullOrWhiteSpace(ToolTip.GetTip(buttons[index]) as string));
                    Assert.True(ToolTip.GetShowOnDisabled(buttons[index]));
                }
            }

            // Wide host: one row, the buttons keep their natural width, the last one takes what is left.
            Assert.False(surface.FooterStacked);
            Assert.Equal(HorizontalAlignment.Stretch, surface.Footer.HorizontalAlignment);
            Assert.Single(surface.Footer.RowDefinitions);
            Assert.Equal(3, surface.Footer.ColumnDefinitions.Count);
            Assert.Equal(new[] { 0, 1, 2 }, buttons.Select(Grid.GetColumn));
            Assert.All(buttons, button => Assert.Equal(0, Grid.GetRow(button)));
            Assert.Equal(new[] { HorizontalAlignment.Left, HorizontalAlignment.Left, HorizontalAlignment.Right }, buttons.Select(button => button.HorizontalAlignment));
            AssertLabels();

            // Side panel width (the default panel is 320 dip wide): one column, one button per line, each at full width, 8 dip apart.
            surface.ApplyWidth(QuickTagSurface.CompactWidth - 1);
            Assert.True(surface.FooterStacked);
            Assert.Equal(HorizontalAlignment.Stretch, surface.Footer.HorizontalAlignment);
            Assert.Single(surface.Footer.ColumnDefinitions);
            Assert.Equal(3, surface.Footer.RowDefinitions.Count);
            Assert.Equal(new[] { 0, 1, 2 }, buttons.Select(Grid.GetRow));
            Assert.All(buttons, button => Assert.Equal(0, Grid.GetColumn(button)));
            Assert.All(buttons, button => Assert.Equal(HorizontalAlignment.Stretch, button.HorizontalAlignment));
            Assert.Equal(8, surface.Footer.RowSpacing);
            Assert.Equal(0, surface.Footer.ColumnSpacing);
            Assert.Equal(buttons, surface.Footer.Children.ToArray()); // the tab order stays undo, stamp, editor
            AssertLabels();

            // Back to the row from the compact width on; a width of 0 (not laid out yet) changes nothing.
            surface.ApplyWidth(0);
            Assert.True(surface.FooterStacked);
            surface.ApplyWidth(QuickTagSurface.CompactWidth);
            Assert.False(surface.FooterStacked);
            Assert.Equal(8, surface.Footer.ColumnSpacing);
            AssertLabels();
        }
    }

    // N6 (review wave 6): the texts name the key bound now, not the catalog default, and say nothing about a key the person removed.
    [Fact]
    public void TheFooterTextsNameTheKeysAsBoundNowAndStaySilentAboutAnUnboundKey()
    {
        OwnedUiDispatcher.Run(() =>
        {
            ShortcutService.Publish(new ShortcutBindingsSection()
                .Set(QuickTagCommands.Undo, [ShortcutGesture.Parse("Primary+Shift+J")])
                .Set(QuickTagCommands.StampApply, []));
            try
            {
                Assert.Equal("Ctrl+Shift+J", QuickTagKeys.UndoGesture(ShortcutPlatform.Linux));
                Assert.Equal("⇧⌘J", QuickTagKeys.UndoGesture(ShortcutPlatform.MacOS));
                Assert.Null(QuickTagKeys.StampGesture(ShortcutPlatform.Linux));
                using TempDirectory temp = new();
                using ProfileLease lease = ProfileLease.Acquire(temp.Path);
                PrivateTagStore store = new(temp.Path, lease);
                (QuickTagSurface surface, QuickTagSession session) = Surface(store, QuickTagTarget.For(Single(store, Entry("a", 'a'))));
                using (session)
                using (surface)
                {
                    Assert.Equal("Undo the last tag change · Ctrl+Shift+J", ToolTip.GetTip(surface.UndoButton));
                    Assert.Equal("Takes back the last change made here, one step at a time. Shortcut: Ctrl+Shift+J.", AutomationProperties.GetHelpText(surface.UndoButton));
                    string stampHelp = AutomationProperties.GetHelpText(surface.StampButton);
                    Assert.StartsWith("Gives the shown media file or selection every tag of your stamp", stampHelp);
                    Assert.DoesNotContain("Shortcut", stampHelp, StringComparison.Ordinal);
                    Assert.DoesNotContain("Ctrl", stampHelp, StringComparison.Ordinal);
                    Assert.False(string.IsNullOrEmpty(AutomationProperties.GetName(surface.StampButton)));
                    Assert.DoesNotContain("Ctrl", AutomationProperties.GetName(surface.StampButton), StringComparison.Ordinal);
                    Assert.DoesNotContain(" · ", AutomationProperties.GetName(surface.StampButton), StringComparison.Ordinal);
                    Assert.True(string.IsNullOrEmpty(AutomationProperties.GetAcceleratorKey(surface.StampButton)));
                }
            }
            finally { ShortcutService.Publish(new ShortcutBindingsSection()); }
        });
    }

    [Fact]
    public void EveryTextWithAKeyHasItsTwinWithoutOneInAllTwelveLanguages()
    {
        string[] keys = ["tags.quick.undoTip", "tags.quick.undoHelp", "tags.quick.stamp.help", "tags.quick.stamp.name", "tags.quick.stamp.nameEmpty", "tags.quick.fieldHintNavigation"];
        foreach (string code in new[] { "en", "de" }.Concat(Texts.BundledPackCodes()))
        {
            Texts.SetLanguage(code);
            foreach (string key in keys)
            {
                string without = Texts.T(key + "NoKey", "NAMES", "KEY");
                Assert.NotEqual(key + "NoKey", without);
                Assert.DoesNotContain("KEY", without, StringComparison.Ordinal);
                Assert.DoesNotContain("{1}", without, StringComparison.Ordinal);
            }
            Assert.Contains("NAMES", Texts.T("tags.quick.stamp.nameNoKey", "NAMES"), StringComparison.Ordinal);
            Assert.DoesNotContain("{", Texts.T("tags.quick.stamp.nameNoKey", "NAMES"), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheFooterTooltipsExplainTheActionInOneSentenceWithTheShortcutAndStayEnabledAsBefore()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        (QuickTagSurface surface, QuickTagSession session) = Surface(store, QuickTagTarget.For(Single(store, Entry("a", 'a'))));
        using (session)
        using (surface)
        {
            Assert.Equal("Undo the last tag change · Ctrl+Z", ToolTip.GetTip(surface.UndoButton));
            Assert.Equal("Open the full tag dialog to choose occurrences, sort and browse long lists", ToolTip.GetTip(surface.AdvancedButton));
            Assert.Equal("Takes back the last change made here, one step at a time. Shortcut: Ctrl+Z.", AutomationProperties.GetHelpText(surface.UndoButton));
            Assert.StartsWith("Gives the shown media file or selection every tag of your stamp", AutomationProperties.GetHelpText(surface.StampButton));
            Assert.EndsWith("Shortcut: Ctrl+Shift+Enter.", AutomationProperties.GetHelpText(surface.StampButton));
            Assert.StartsWith("Opens the full tag dialog", AutomationProperties.GetHelpText(surface.AdvancedButton));
            // The names stay what they were: the undo count and the stamp content with its key.
            Assert.Equal("Open the full tag dialog to choose occurrences, sort and browse long lists", AutomationProperties.GetName(surface.AdvancedButton));
            Assert.Equal("Ctrl+Shift+Enter", AutomationProperties.GetAcceleratorKey(surface.StampButton));
            // Enabled state unchanged: no step to undo, an empty stamp; the editor needs the loaded snapshot of the target.
            Assert.False(surface.UndoButton.IsEnabled);
            Assert.False(surface.StampButton.IsEnabled);
            Assert.True(surface.AdvancedButton.IsEnabled == (session.Snapshot != null));
        }
    }

    // The footer labels never wrap and are trimmed only as a fallback, so every language keeps them short: a weighted length
    // (a full-width CJK character counts twice) of at most 14 for undo and stamp (the row), 24 for the editor (a 280 dip panel).
    [Theory]
    [InlineData("en", "Undo", "Stamp", "Edit tags…")]
    [InlineData("de", "Rückgängig", "Stempel", "Tags bearbeiten…")]
    [InlineData("es", "Deshacer", "Sello", "Editor de etiquetas")]
    [InlineData("fr", "Annuler", "Tampon", "Éditeur d’étiquettes")]
    [InlineData("ru", "Отменить", "Штамп", "Редактор тегов")]
    [InlineData("zh-Hans", "撤销", "印章", "打开标签编辑器")]
    [InlineData("ja", "元に戻す", "スタンプ", "タグエディターを開く")]
    [InlineData("ko", "실행 취소", "스탬프", "태그 편집기 열기")]
    [InlineData("pt-BR", "Desfazer", "Carimbo", "Abrir editor de tags")]
    [InlineData("cs", "Zpět", "Razítko", "Otevřít editor štítků")]
    [InlineData("it", "Annulla", "Timbro", "Apri editor dei tag")]
    [InlineData("pl", "Cofnij", "Stempel", "Otwórz edytor tagów")]
    public void TheFooterLabelsAreRealTranslationsThatFitOnOneLineInEveryLanguage(string code, string undo, string stamp, string editor)
    {
        Texts.SetLanguage(code);
        Assert.Equal(undo, Texts.T("tags.quick.undo"));
        Assert.Equal(stamp, Texts.T("tags.quick.stamp.button"));
        Assert.Equal(editor, Texts.T("tags.quick.advanced"));
        Assert.InRange(WeightedLength(undo), 1, 14);
        Assert.InRange(WeightedLength(stamp), 1, 14);
        Assert.InRange(WeightedLength(editor), 1, 24);
        // The bundled packs use the action wording; only the built-in English/German entries retain the wave8 ellipsis.
        if (code is not ("en" or "de"))
        {
            Assert.NotEqual("Edit tags…", editor);
            Assert.DoesNotContain("…", editor);
        }
        Assert.False(string.IsNullOrWhiteSpace(editor));
        Assert.Contains("X", Texts.T("tags.quick.undoTip", "X"));
        Assert.False(string.IsNullOrWhiteSpace(Texts.T("tags.quick.advancedHelp")));
        Assert.False(string.IsNullOrWhiteSpace(Texts.T("tags.quick.undoHelp", "X")));
        Assert.Contains("X", Texts.T("tags.quick.stamp.help", "X"));
    }

    [Fact]
    public void TheGermanFooterSaysWhatEachButtonDoes()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        Texts.SetLanguage("de");
        (QuickTagSurface surface, QuickTagSession session) = Surface(store, QuickTagTarget.For(Single(store, Entry("a", 'a'))));
        using (session)
        using (surface)
        {
            Assert.Equal(("corner-up-left", "Rückgängig"), surface.FooterLabelOf(surface.UndoButton));
            Assert.Equal(("check-square", "Stempel"), surface.FooterLabelOf(surface.StampButton));
            Assert.Equal(("external-link", "Tags bearbeiten…"), surface.FooterLabelOf(surface.AdvancedButton));
            Assert.Equal("Letzte Tagänderung rückgängig machen · Strg+Z", ToolTip.GetTip(surface.UndoButton));
            Assert.Equal("Den vollständigen Tagdialog öffnen, um Fundstellen zu wählen, zu sortieren und lange Listen zu durchsuchen", ToolTip.GetTip(surface.AdvancedButton));
            Assert.StartsWith("Öffnet den vollständigen Tagdialog", AutomationProperties.GetHelpText(surface.AdvancedButton));
            Assert.Equal("Nimmt die letzte hier gemachte Änderung zurück, Schritt für Schritt. Tastenkürzel: Strg+Z.", AutomationProperties.GetHelpText(surface.UndoButton));
            Assert.InRange(WeightedLength("Tags bearbeiten…"), 1, 24);
        }
    }

    private static int WeightedLength(string text) => text.Sum(character => character >= '\u2E80' ? 2 : 1);

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
            Assert.Contains(WorkspaceChips.TagPillAssigned, chips[0].Chip.Classes); // all: accent tint and edge
            Assert.Contains(WorkspaceChips.TagPillPartial, chips[1].Chip.Classes);  // some: quiet fill, accent edge
            Assert.Empty(surface.Shown); // both tags are carried: no suggestion repeats a chip

            await session.ToggleAsync(red);
            surface.Refresh();
            Assert.All(surface.AssignedButtons, item => Assert.Contains("on all 2", AutomationProperties.GetName(item.Chip)));
        }
    }

    // Design plan "Rate and tag…" (16.11.2026): an assigned tag is one joined pill. The chip is the start, the cross the end, no gap; the pill's state class sits
    // on both halves; the cross is not a tab stop; the check mark is gone (on all = tint only), a dash marks a tag that is on some of a selection.
    [Fact]
    public void AnAssignedTagIsOneJoinedPillWithTheCrossAsItsEndAndNoCheckMark()
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
            (Button all, Button allRemove, _) = chips[0];   // blue: on both
            (Button some, Button someRemove, _) = chips[1]; // red: on one of two
            Assert.Contains(WorkspaceChips.TagPillAssigned, allRemove.Classes);
            Assert.Contains(WorkspaceChips.TagPillPartial, someRemove.Classes);
            Assert.DoesNotContain(WorkspaceChips.TagPillPartial, allRemove.Classes);
            Assert.All(new[] { all, some }, chip => Assert.Contains("tag-pill-joined", chip.Classes));
            Assert.All(new[] { allRemove, someRemove }, remove =>
            {
                Assert.Contains("tag-pill-remove", remove.Classes);
                Assert.Contains(WorkspaceIcons.SquareClass, remove.Classes); // kept: the 28 dip size of the pill's class replaces the 32
                Assert.DoesNotContain("bar-flat", remove.Classes);
                Assert.False(remove.IsTabStop); // Delete on the chip removes too: one stop per tag
                Assert.True(remove.Focusable);  // still clickable and in the automation tree
                Assert.Equal(WorkspaceIcons.CloseSmall, WorkspaceIcons.KeyOf(remove.Content));
            });
            Assert.True(all.IsTabStop);
            Assert.True(some.IsTabStop);

            // No gap between the halves: the host puts the cross right after the chip.
            StackPanel host = Assert.IsType<StackPanel>(all.Parent);
            Assert.Equal(0, host.Spacing);
            Assert.Equal(new Control[] { all, allRemove }, host.Children.ToArray());
            all.Width = 80; all.Height = 28; allRemove.Width = 28; allRemove.Height = 28;
            host.Measure(new Avalonia.Size(400, 100));
            host.Arrange(new Avalonia.Rect(0, 0, 400, 28));
            Assert.Equal(all.Bounds.Right, allRemove.Bounds.Left);
            Assert.Equal(all.Bounds.Height, allRemove.Bounds.Height);

            // The pill's content: on all, only the label (no check); on some, the dash and the label. Names are unchanged.
            StackPanel allContent = Assert.IsType<StackPanel>(all.Content), someContent = Assert.IsType<StackPanel>(some.Content);
            Assert.Single(allContent.Children);
            Assert.IsType<TextBlock>(allContent.Children[0]);
            Assert.Equal(2, someContent.Children.Count);
            Assert.IsType<Avalonia.Controls.Shapes.Path>(someContent.Children[0]);
            Assert.Equal(Avalonia.Automation.AccessibilityView.Raw, AutomationProperties.GetAccessibilityView(someContent.Children[0]));
            Assert.Equal("blue, on all 2 selected media files. Delete removes it from all.", AutomationProperties.GetName(all));
            Assert.Equal("Remove red from all", AutomationProperties.GetName(someRemove));
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

    // Refresh keeps the old candidates and chips consistent through the layout pass; the shared viewport, not the assigned block, now
    // determines fit capacity. Pin refresh ordering here as well as exercising actual layout in QuickTagScrollFlowTests.
    [Fact]
    public void RefreshBuildsTheAssignedBlockAndLaysItOutBeforeTheStripIsFittedAndNoSecondFitIsScheduled()
    {
        string source = File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThisFile())!, "../../Desktop/Controls/QuickTagSurface.cs")));
        string refresh = Between(source, "internal void Refresh()", "internal static IReadOnlyList<QuickTagSuggestion> Normalize(");
        int assigned = refresh.IndexOf("BuildAssigned();", StringComparison.Ordinal), layout = refresh.IndexOf("UpdateLayout();", StringComparison.Ordinal),
            suggestions = refresh.IndexOf("BuildSuggestions();", StringComparison.Ordinal), ask = refresh.IndexOf("_decorated.Suggest(", StringComparison.Ordinal);
        Assert.True(assigned >= 0 && assigned < layout && layout < suggestions, "BuildAssigned, then the layout, then the strip");
        Assert.True(layout < ask, "the ranking is asked after the layout, so the old candidates and chips agree while it runs");
        Assert.Contains("_fillSuggestions && _attached", refresh);
        Assert.Contains("_refreshing = true;", refresh);
        Assert.Contains("finally { _refreshing = false; }", refresh);
        string sizeChanged = Between(source, "private void OnStripHostSizeChanged(", "private DispatcherTimer CreateFitTimer()");
        Assert.True(sizeChanged.IndexOf("if (_refreshing) return;", StringComparison.Ordinal) is > 0 and var guard && guard < sizeChanged.IndexOf("_fitTimer", StringComparison.Ordinal),
            "a size change caused by the refresh's own layout starts no debounce timer");
        // The fit asks for the configured count as its minimum, and a measured size is kept per width.
        string fit = Between(source, "private void ApplyFit()", "private StripItem EnsureItem(");
        Assert.Contains("new(width, height, StripItemSpacing, StripMinimum, MinimumSuggestionRows)", fit);
        Assert.Contains("QuickTagStripFit.NeedsMeasure(item.Measured, item.MeasuredWidth, width)", fit);
        Assert.Contains("item.MeasuredWidth = width;", fit);
        Assert.DoesNotContain("QuickTagOptions.NumberedSuggestions", fit);
    }

    private static string Between(string source, string start, string end)
    {
        int first = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(first >= 0, "Missing source anchor: " + start);
        int last = source.IndexOf(end, first + start.Length, StringComparison.Ordinal);
        Assert.True(last > first, "Missing source anchor: " + end);
        return source[first..last];
    }

    private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    private static string[] Placeholders(string text) => System.Text.RegularExpressions.Regex.Matches(text, @"\{\d+(:[^}]*)?\}")
        .Select(match => match.Value).Distinct().Order(StringComparer.Ordinal).ToArray();
}
