using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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

// TP3: the quick tag popover. Flyouts cannot be shown without an application, so these tests cover everything that does not
// need a visible window: construction, ownership, close reasons, the guard, the blocked variant and the command routing.
[Collection("Localization")]
public sealed class QuickTagPopoverTests : IDisposable
{
    private static readonly DateTimeOffset Day = new(2026, 11, 14, 0, 0, 0, TimeSpan.Zero);

    public QuickTagPopoverTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static MediaLibraryEntry Entry(string thread, char hash) =>
        new(new ResourceId("s", "b", thread), "T" + thread, 1, Day, Day, "1", 1, null, 0,
            "att-v1:" + new string(hash, 64), "/dir/" + thread, "asset-" + thread, new string(hash, 64), 1, "m1", "original",
            "f" + thread + ".png", SavedMediaKind.Image, MediaLibraryPresence.Present, false, false);

    private static MediaTagSession Single(PrivateTagStore store, MediaLibraryEntry entry) =>
        new(new MediaTagSubject.Bound(MediaLibraryFilterEngine.TagTarget(entry)!),
            () => new PrivateTagLoadSnapshot(PrivateTagLoadState.Ready, store.ReadSnapshot(), null),
            (name, subject, token) => Task.FromResult(store.DefineAndSetMediaSubjects(name, [subject], null, token)),
            (id, subject, assigned, token) => Task.FromResult(store.SetMediaSubjects(id, [subject], assigned, null, token)));

    private static IReadOnlyList<string> NamesOn(PrivateTagStore store, MediaLibraryEntry entry) =>
        store.ReadSnapshot().TagsOf(MediaLibraryFilterEngine.TagTarget(entry)!).Select(tag => tag.Name.Display).Order(StringComparer.Ordinal).ToArray();

    private sealed class Fixture : IDisposable
    {
        internal readonly TempDirectory Temp = new();
        internal readonly ProfileLease Lease;
        internal readonly PrivateTagStore Store;
        internal readonly MediaLibraryEntry Entry = QuickTagPopoverTests.Entry("a", 'a');
        internal Fixture()
        {
            Lease = ProfileLease.Acquire(Temp.Path);
            Store = new PrivateTagStore(Temp.Path, Lease);
        }
        internal QuickTagSession Session() => new(QuickTagTarget.For(Single(Store, Entry)));
        public void Dispose() { Lease.Dispose(); Temp.Dispose(); }
    }

    private static KeyEventArgs Key(Key key, KeyModifiers modifiers) => new() { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers };

    // ---- construction -------------------------------------------------------------------------------------------

    [Fact]
    public void ACreatedPopoverIsClosedFramedAndCyclesTabInsideAndPutsTheSurfaceInTheFlyout()
    {
        using Fixture fixture = new();
        Border anchor = new();
        QuickTagPopover popover = QuickTagPopover.Create(anchor, fixture.Session(), "fa.png", new QuickTagPopoverOptions { Name = "T3Popover" });

        Assert.False(popover.IsOpen);
        Assert.False(popover.IsClosed);
        Assert.Null(popover.Reason);
        Assert.Same(anchor, popover.Anchor);
        Assert.Equal(QuickTagPopover.Width, popover.Frame.Width);
        Assert.Equal(QuickTagPopover.MaximumHeight, popover.Frame.MaxHeight);
        Assert.Equal(KeyboardNavigationMode.Cycle, KeyboardNavigation.GetTabNavigation(popover.Frame));
        Assert.Same(popover.Surface, popover.Frame.Child);
        Assert.Same(popover.Frame, popover.Flyout.Content);
        Assert.Equal("T3PopoverFrame", popover.Frame.Name);
        Assert.Contains("quick-tag-popover", popover.Flyout.FlyoutPresenterClasses);
        Assert.True(popover.Surface!.AdvancedButton.IsVisible);
        Assert.False(popover.Surface.NavigationEnabled);
        Assert.Contains("fa.png", popover.Surface.TargetLabel);
        popover.Close();
    }

    [Fact]
    public void AdvancedCanBeHiddenForAHostWithoutAFullDialog()
    {
        using Fixture fixture = new();
        QuickTagPopover popover = QuickTagPopover.Create(new Border(), fixture.Session(), "x", new QuickTagPopoverOptions { AdvancedAvailable = false });
        Assert.False(popover.Surface!.AdvancedButton.IsVisible);
        popover.Close();
    }

    // ---- close reasons and ownership ----------------------------------------------------------------------------

    [Fact]
    public async Task TheCloseButtonClosesWithClosedAndReleasesSessionAndSurfaceOnce()
    {
        using Fixture fixture = new();
        QuickTagSession session = fixture.Session();
        QuickTagPopover popover = QuickTagPopover.Create(new Border(), session, "x", new QuickTagPopoverOptions { RestoreFocus = false });
        List<QuickTagPopoverCloseReason> raised = new();
        popover.Closed += raised.Add;
        bool applied = false;
        session.Applied += _ => applied = true;

        popover.Surface!.CloseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.True(popover.IsClosed);
        Assert.False(popover.IsOpen);
        Assert.Equal(QuickTagPopoverCloseReason.Closed, popover.Reason);
        Assert.Equal(QuickTagPopoverCloseReason.Closed, await popover.Result);
        Assert.Equal(new[] { QuickTagPopoverCloseReason.Closed }, raised);
        popover.Close(QuickTagPopoverCloseReason.Stale);            // a second close is ignored
        Assert.Equal(QuickTagPopoverCloseReason.Closed, popover.Reason);
        Assert.Single(raised);
        // The session was disposed with the popover: its subscribers are gone.
        await session.AddAsync("late");
        Assert.False(applied, "the session must not report to anyone after the popover closed");
    }

    [Fact]
    public async Task AdvancedClosesWithAdvancedSoTheHostCanOpenTheFullDialog()
    {
        using Fixture fixture = new();
        QuickTagPopover popover = QuickTagPopover.Create(new Border(), fixture.Session(), "x", new QuickTagPopoverOptions { RestoreFocus = false });
        popover.Surface!.AdvancedButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(QuickTagPopoverCloseReason.Advanced, await popover.Result);
        Assert.True(popover.IsClosed);
    }

    [Fact]
    public async Task ALostTargetClosesThePopoverAsStaleOnTheNextSessionUpdate()
    {
        using Fixture fixture = new();
        bool current = true;
        QuickTagSession session = fixture.Session();
        QuickTagPopover popover = QuickTagPopover.Create(new Border(), session, "x",
            new QuickTagPopoverOptions { IsCurrent = () => current, RestoreFocus = false });

        await session.AddAsync("kept");
        Assert.False(popover.IsClosed, "a write must not close the popover while the host's guard still holds");
        Assert.Equal(new[] { "kept" }, NamesOn(fixture.Store, fixture.Entry));

        current = false;
        await session.AddAsync("other");
        Assert.Equal(QuickTagPopoverCloseReason.Stale, await popover.Result);
    }

    [Fact]
    public async Task ADetachedAnchorEndsThePopoverBeforeItWasShownWithoutLeavingItAsTheCurrentOne()
    {
        using Fixture fixture = new();
        QuickTagPopover popover = QuickTagPopover.Create(new Border(), fixture.Session(), "x", new QuickTagPopoverOptions { RestoreFocus = false });
        popover.Close(QuickTagPopoverCloseReason.Detached);
        Assert.Equal(QuickTagPopoverCloseReason.Detached, await popover.Result);
        Assert.NotSame(popover, QuickTagPopover.Current);
        Assert.False(popover.Show().IsOpen, "a closed popover is never shown again");
    }

    // ---- blocked --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ABlockedPopoverStatesTheReasonAsTextHasNoSurfaceAndClosesWithTheButtonOrEsc()
    {
        Border anchor = new();
        string reason = Texts.T("booru.media.tags.unavailable");
        QuickTagPopover popover = QuickTagPopover.CreateBlocked(anchor, "provider.jpg", reason, new QuickTagPopoverOptions { RestoreFocus = false });

        Assert.Null(popover.Surface);
        Assert.Equal(reason, popover.BlockedReason);
        StackPanel content = Assert.IsType<StackPanel>(popover.Frame.Child);
        Assert.Contains(content.Children.OfType<TextBlock>(), text => text.Text == reason);
        Assert.NotNull(popover.BlockedClose);
        popover.BlockedClose!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(QuickTagPopoverCloseReason.Closed, await popover.Result);

        QuickTagPopover second = QuickTagPopover.CreateBlocked(anchor, "provider.jpg", reason, new QuickTagPopoverOptions { RestoreFocus = false });
        KeyEventArgs escape = Key(Avalonia.Input.Key.Escape, KeyModifiers.None);
        second.Frame.RaiseEvent(escape);
        Assert.True(escape.Handled);
        Assert.Equal(QuickTagPopoverCloseReason.Closed, await second.Result);
    }

    // ---- wired commands ---------------------------------------------------------------------------------------------

    private static QuickTagSuggestion Suggestion(PrivateTagStore store, string name)
    {
        TagId id = store.Define(name).Tag;
        return new QuickTagSuggestion(store.ReadSnapshot().Find(id)!.Name, id, 3, QuickTagReason.Frequent, 1);
    }

    [Fact]
    public void TheDefaultGesturesToggleASuggestionAndUndoItThroughTheCommandRoute()
    {
        using Fixture fixture = new();
        QuickTagSuggestion first = Suggestion(fixture.Store, "alpha");
        QuickTagSession session = fixture.Session();
        QuickTagPopover popover = QuickTagPopover.Create(new Border(), session, "x", new QuickTagPopoverOptions
        {
            Suggestions = new DelegateQuickTagSuggestions(_ => new[] { first }),
            RestoreFocus = false
        });
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(null, ShortcutPlatform.Linux);
        Assert.Single(popover.Surface!.Shown);

        // The test store answers synchronously, so a routed key has written when TryRouteCommand returns.
        Assert.True(QuickTagPopover.TryRouteCommand(popover.Surface, Key(Avalonia.Input.Key.D1, KeyModifiers.Alt), bindings));
        Assert.Equal(new[] { "alpha" }, NamesOn(fixture.Store, fixture.Entry));

        Assert.True(QuickTagPopover.TryRouteCommand(popover.Surface, Key(Avalonia.Input.Key.Z, KeyModifiers.Control), bindings));
        Assert.Empty(NamesOn(fixture.Store, fixture.Entry));

        Assert.False(QuickTagPopover.TryRouteCommand(popover.Surface, Key(Avalonia.Input.Key.Q, KeyModifiers.Control), bindings));
        popover.Close();
    }

    [Fact]
    public void ARebindingOfASuggestionCommandCountsInsideThePopoverAndTheOldGestureNoLongerDoes()
    {
        using Fixture fixture = new();
        QuickTagSuggestion first = Suggestion(fixture.Store, "alpha");
        QuickTagSession session = fixture.Session();
        QuickTagPopover popover = QuickTagPopover.Create(new Border(), session, "x", new QuickTagPopoverOptions
        {
            Suggestions = new DelegateQuickTagSuggestions(_ => new[] { first }),
            RestoreFocus = false
        });
        var section = new ShortcutBindingsSection().Set(QuickTagCommands.Suggestion(1), [ShortcutGesture.Parse("Primary+Shift+J")]);
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(section, ShortcutPlatform.Linux);

        Assert.False(QuickTagPopover.TryRouteCommand(popover.Surface!, Key(Avalonia.Input.Key.D1, KeyModifiers.Alt), bindings));
        Assert.True(QuickTagPopover.TryRouteCommand(popover.Surface!, Key(Avalonia.Input.Key.J, KeyModifiers.Control | KeyModifiers.Shift), bindings));
        Assert.Equal(new[] { "alpha" }, NamesOn(fixture.Store, fixture.Entry));
        popover.Close();
    }

    [Fact]
    public void TheWarmedEventOfAContextSourceRepaintsTheStripAndTheSourceIsReleasedWithThePopover()
    {
        using Fixture fixture = new();
        // A context source is recognised by type; its Warmed event is the only trigger of the repaint.
        QuickTagContextSource source = new(null, new TagRecentList(), post: action => action());
        QuickTagPopover popover = QuickTagPopover.Create(new Border(), fixture.Session(), "x", new QuickTagPopoverOptions { Suggestions = source, RestoreFocus = false });
        Assert.Same(source, popover.Surface!.Suggestions);
        popover.Close();
        // Disposed with the popover: a warm-up that finishes afterwards cannot reach the closed surface.
        bool raised = false;
        source.Warmed += () => raised = true;
        PrivateTagStoreSnapshot snapshot = fixture.Store.ReadSnapshot();
        source.Prewarm(snapshot);
        Assert.False(raised);
    }
}
