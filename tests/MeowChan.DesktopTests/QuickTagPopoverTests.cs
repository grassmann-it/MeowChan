using System.Runtime.CompilerServices;
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

    private static string ThisFile([CallerFilePath] string path = "") => path;

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
        Assert.Equal(440, QuickTagPopover.Width); // wider than the 360 of the first design: Tom, 16.11.2026 ("quite narrow")
        Assert.Equal(QuickTagPopover.Width, popover.Frame.Width);
        Assert.Equal(new Avalonia.Thickness(12), popover.Frame.Padding);
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

    // The width is a layout constant (not a setting). It never exceeds the window minus a 16 dip margin on both sides, so the narrow reader and gallery
    // still hold the whole popover; the floor keeps the five star cells and the remove button usable.
    [Theory]
    [InlineData(1380.0, 440.0)]
    [InlineData(760.0, 440.0)]   // the window minimum
    [InlineData(472.0, 440.0)]   // the narrowest window that still holds the default (440 + 2 x 16)
    [InlineData(471.0, 439.0)]
    [InlineData(400.0, 368.0)]
    [InlineData(320.0, 288.0)]
    [InlineData(300.0, 280.0)]   // the floor
    [InlineData(100.0, 280.0)]
    [InlineData(0.0, 440.0)]     // not measured yet: the default
    [InlineData(double.NaN, 440.0)]
    [InlineData(double.PositiveInfinity, 440.0)]
    public void TheWidthIsTheDefaultNarrowedToTheWindowAndNeverBelowTheFloor(double host, double expected)
    {
        Assert.Equal(expected, QuickTagPopover.WidthFor(host));
        Assert.InRange(QuickTagPopover.WidthFor(host), QuickTagPopover.MinimumWidth, QuickTagPopover.Width);
        Assert.Equal(16, QuickTagPopover.HostMargin);
    }

    [Fact]
    public void TheFrameIsFittedToTheWindowWhenTheFlyoutOpensAndTheSurfaceHostsAreNotTouched()
    {
        string popover = File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThisFile())!, "../../Desktop/Controls/QuickTagPopover.cs")));
        Assert.Contains("_flyout.Opening += OnFlyoutOpening;", popover);
        Assert.Contains("_frame.Width = WidthFor(TopLevel.GetTopLevel(_anchor)?.ClientSize.Width ?? 0);", popover);
        // The presenter's ceiling stays above the widest popover.
        string styles = File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThisFile())!, "../../Desktop/WorkspaceStyles.axaml")));
        int at = styles.IndexOf("<Style Selector=\"FlyoutPresenter.quick-tag-popover\">", StringComparison.Ordinal);
        Assert.True(at >= 0);
        string block = styles[at..styles.IndexOf("</Style>", at, StringComparison.Ordinal)];
        Assert.Contains("Property=\"MaxWidth\" Value=\"480\"", block);
        Assert.True(480 >= QuickTagPopover.Width + 2);
    }

    [Fact]
    public void ThePopoverSaysTheRatingCaptionPolitelyAndTheBlockedOneToo()
    {
        using Fixture fixture = new();
        DecisionHost host = new() { Rating = 3 };
        QuickTagPopover popover = QuickTagPopover.Create(new Border(), fixture.Session(), "x",
            new QuickTagPopoverOptions { RestoreFocus = false, Decision = host.Make() });
        Assert.Equal(Avalonia.Automation.AutomationLiveSetting.Polite, Avalonia.Automation.AutomationProperties.GetLiveSetting(popover.DecisionRow!.Summary));
        popover.Close();
        QuickTagPopover blocked = QuickTagPopover.CreateBlocked(new Border(), "p.jpg", "reason", new QuickTagPopoverOptions { RestoreFocus = false, Decision = host.Make() });
        Assert.Equal(Avalonia.Automation.AutomationLiveSetting.Polite, Avalonia.Automation.AutomationProperties.GetLiveSetting(blocked.DecisionRow!.Summary));
        blocked.Close();
        // The gallery panel announces through its own status line: its row has no live caption.
        Assert.Equal(Avalonia.Automation.AutomationLiveSetting.Off, Avalonia.Automation.AutomationProperties.GetLiveSetting(new GalleryDecisionRow("PanelRow").Summary));
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

    // ---- "Rate and tag…": the favourite and stars row on top ---------------------------------------------------------------------

    private sealed class DecisionHost
    {
        internal int? Rating;
        internal bool Favorite;
        internal int Released;
        internal bool Changed;
        internal QuickTagPopoverDecision Make(QuickTagInitialFocus initialFocus = QuickTagInitialFocus.TagInput, bool withFavorite = true) => new()
        {
            View = () => new GalleryDecisionView(1, Favorite ? GalleryDecisionCoverage.All : GalleryDecisionCoverage.None, Rating, false, true),
            SetRating = value => { Rating = value; Changed = true; return Task.CompletedTask; },
            SetFavorite = withFavorite ? (selected => { Favorite = selected; Changed = true; return Task.CompletedTask; }) : null,
            InitialFocus = initialFocus,
            HasChanged = () => Changed,
            Release = () => Released++
        };
    }

    [Fact]
    public void ThePopoverCarriesTheFavouriteAndStarsRowAboveTheFieldAndWritesThroughTheHost()
    {
        using Fixture fixture = new();
        DecisionHost host = new() { Rating = 2 };
        QuickTagPopover popover = QuickTagPopover.Create(new Border(), fixture.Session(), "x",
            new QuickTagPopoverOptions { RestoreFocus = false, Decision = host.Make() });
        GalleryDecisionRow row = popover.DecisionRow!;
        Assert.NotNull(row);
        Assert.Equal("QuickTagPopoverDecision", row.Name);
        Assert.True(row.FavoriteVisible);
        Assert.Equal(2, row.View.Rating);
        Assert.True(row.StarButtons[1].IsChecked);
        Assert.Same(row, popover.Surface!.DecisionContent);

        row.StarButtons[3].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(4, host.Rating);
        Assert.Equal(4, row.View.Rating); // the row shows what the host stores now
        row.ClearButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Null(host.Rating);
        row.FavoriteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(host.Favorite);
        Assert.Equal(GalleryDecisionCoverage.All, row.View.Favorite);

        popover.Close();
        Assert.Equal(1, host.Released);
        Assert.True(host.Changed);
    }

    [Fact]
    public void ExplicitStarFocusIsStillAvailableButGeneralRateAndTagDefaultsToTheField()
    {
        Assert.Equal(QuickTagInitialFocus.TagInput, new RateAndTagRequest().InitialFocus);
        using Fixture fixture = new();
        DecisionHost host = new();
        QuickTagPopover popover = QuickTagPopover.Create(new Border(), fixture.Session(), "x",
            new QuickTagPopoverOptions { RestoreFocus = false, Decision = host.Make(initialFocus: QuickTagInitialFocus.Rating, withFavorite: false) });
        Assert.False(popover.DecisionRow!.FavoriteVisible);
        Assert.False(popover.Surface!.FocusOnAttach);
        popover.Close();
        QuickTagPopover field = QuickTagPopover.Create(new Border(), fixture.Session(), "x",
            new QuickTagPopoverOptions { RestoreFocus = false, Decision = new DecisionHost().Make(initialFocus: QuickTagInitialFocus.TagInput) });
        Assert.True(field.Surface!.FocusOnAttach);
        field.Close();
    }

    [Fact]
    public void ABlockedPopoverStillOffersTheStarsAboveItsReason()
    {
        DecisionHost host = new() { Rating = 5 };
        string reason = Texts.T("booru.media.tags.unavailable");
        QuickTagPopover popover = QuickTagPopover.CreateBlocked(new Border(), "provider.jpg", reason,
            new QuickTagPopoverOptions { RestoreFocus = false, Decision = host.Make() });
        StackPanel content = Assert.IsType<StackPanel>(popover.Frame.Child);
        Assert.Equal(new object[] { content.Children[0], popover.DecisionRow!, content.Children[2] }, content.Children.Cast<object>().ToArray());
        Assert.Equal(reason, ((TextBlock)content.Children[2]).Text);
        Assert.True(popover.DecisionRow!.StarButtons[4].IsChecked);
        popover.Close();
        Assert.Equal(1, host.Released);
    }

    [Fact]
    public void AFocusedStarRovesWithTheArrowKeysInsideThePopover()
    {
        using Fixture fixture = new();
        QuickTagPopover popover = QuickTagPopover.Create(new Border(), fixture.Session(), "x",
            new QuickTagPopoverOptions { RestoreFocus = false, Decision = new DecisionHost().Make() });
        // Without a focused star the frame's tunnel leaves Left and Right alone (the field's caret keeps them; the star route is gated on a focused star).
        // What nothing inside took then stops at the frame and never moves anything behind the popover (audit 16.11.2026).
        Assert.False(popover.DecisionRow!.IsRatingFocused);
        KeyEventArgs right = Key(Avalonia.Input.Key.Right, KeyModifiers.None);
        Assert.False(popover.TryRouteDecisionKey(right, ShortcutService.Current));
        popover.Frame.RaiseEvent(right);
        Assert.True(right.Handled);
        Assert.False(popover.IsClosed);
        popover.Close();
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
    public void TheRankingAdapterIsTheStripIsReleasedWithThePopoverAndTheShellBindsTheSavedLimits()
    {
        using Fixture fixture = new();
        // The popover uses the gallery panel's adapter (TP2); it is warmed with the popover and disposed with it.
        TagRecentList recent = new();
        GalleryTagPanelSuggestions source = new();
        QuickTagPopover popover = QuickTagPopover.Create(new Border(), fixture.Session(), "x",
            new QuickTagPopoverOptions { Suggestions = source, Recent = recent, RestoreFocus = false });
        Assert.Same(source, popover.Surface!.Suggestions);
        popover.Close();
        // Disposed with the popover: a warm-up requested afterwards starts nothing and cannot reach the closed surface.
        bool raised = false;
        source.Ready += () => raised = true;
        PrivateTagStoreSnapshot snapshot = fixture.Store.ReadSnapshot();
        source.Warm(snapshot);
        source.NotifyReady();
        Assert.False(raised);
        Assert.False(source.IsWarm(snapshot));
        // The shell's options carry the saved limits and the same recent list.
        Config config = new() { QuickTagSuggestionCount = 7, QuickTagUndoDepth = 12 };
        QuickTagPopoverOptions bound = QuickTagSettings.PopoverOptions(config, TagContext.Empty, recent);
        Assert.Equal((12, 7), (bound.Quick!.UndoDepth, bound.Quick.SuggestionCount));
        Assert.Same(recent, bound.Recent);
        Assert.Equal(7, Assert.IsType<GalleryTagPanelSuggestions>(bound.Suggestions).Options.SuggestionCount);
    }
}
