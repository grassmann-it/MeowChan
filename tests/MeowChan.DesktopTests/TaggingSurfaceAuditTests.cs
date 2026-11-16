using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Platform;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Archive;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

// Audit of the tagging surface (16.11.2026, "every function that is shown has to work"): the tagging page's buttons, keys and double-click, and the
// keys of the "Rate and tag" popover that its own tooltips name. Everything here runs without a window: the page is built over a profile host that
// was never started, the popover over the same fixtures as QuickTagPopoverTests.
[Collection("Gallery viewer construction")]
public sealed class TaggingSurfaceAuditTests : IDisposable
{
    public TaggingSurfaceAuditTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static void WithPage(Action<MeowChan.TaggingPage> test) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
        TokenApplication.Bind();
        string profile = Directory.CreateTempSubdirectory("meowchan-tagging-audit-").FullName;
        DesktopSessionHost host = new(profile, Array.Empty<IImageBoard>());
        MeowChan.TaggingPage page = new(host);
        try { test(page); }
        finally
        {
            page.End();
            host.DisposeAsync().AsTask().GetAwaiter().GetResult();
            try { Directory.Delete(profile, true); } catch (IOException) { }
        }
    });

    // ---- the tagging page --------------------------------------------------------------------------------------------------

    [Fact]
    public void EveryPlainButtonOfTheTaggingPageHasSomethingWiredToItTheRetryButtonOfTheErrorBannerUsedToHaveNothing() => WithPage(page =>
    {
        Button[] unwired = ((ILogical)page).GetLogicalDescendants().OfType<Button>()
            .Where(button => button.GetType() == typeof(Button) && !string.IsNullOrEmpty(button.Name) && button.Name.StartsWith("Tagging", StringComparison.Ordinal))
            .Where(button => button.Command == null && button.Flyout == null && !DeadAffordanceGuard.Handles(button, Button.ClickEvent))
            .ToArray();
        Assert.True(unwired.Length == 0, "Buttons of the tagging page with nothing wired: " + string.Join(", ", unwired.Select(button => button.Name)));
        Assert.True(DeadAffordanceGuard.Handles(page.RetryLoadButton, Button.ClickEvent));
    });

    [Fact]
    public void AKeyOrDoubleClickThatCannotRunSaysWhyInTheStatusLineWhileAPlainCallStaysQuiet() => WithPage(page =>
    {
        // Nothing is selected: the plain call is refused silently (the menu has no entry to show a reason on), the key and double-click path explains.
        page.ExecuteRowCommandAsync("tagging.mark-done").GetAwaiter().GetResult();
        Assert.False(page.StatusText.IsVisible);
        page.ExecuteRowCommandAsync("tagging.mark-done", explain: true).GetAwaiter().GetResult();
        Assert.True(page.StatusText.IsVisible);
        Assert.Equal(Texts.T("tagging.menu.reason.none"), page.StatusText.Text);
    });

    [Fact]
    public void TheSelectionBarsGroupButtonDoesNotWaitForTheFirstGroupBecauseItsMenuStartsWithNewGroup()
    {
        string selection = File.ReadAllText(Path.Combine(Root(), "Desktop/Views/Tagging/TaggingPage.Selection.cs"));
        Assert.Contains("_selGroup.IsEnabled = count > 0 && CanChange;", selection);
        Assert.DoesNotContain("Groups.Count > 0", selection.Split('\n').Single(line => line.Contains("_selGroup.IsEnabled")));
    }

    [Fact]
    public void BothDoubleClicksReachTheExplainingPathInsteadOfBeingGatedOnTheCommandsAvailability()
    {
        foreach (string file in new[] { "Desktop/Views/Tagging/TaggingPage.Commands.cs", "Desktop/Views/Tagging/TaggingPage.Grid.cs" })
        {
            string text = File.ReadAllText(Path.Combine(Root(), file));
            int at = text.IndexOf(".DoubleTapped += (_, e) =>", StringComparison.Ordinal);
            Assert.True(at >= 0, file);
            string handler = text[at..text.IndexOf("};", at, StringComparison.Ordinal)];
            Assert.Contains("explain: true", handler);
            Assert.DoesNotContain("CanRowCommand", handler);
        }
    }

    [Fact]
    public void EveryKeyOfTheTaggingPageRunsInTheTaggingContextSoAPartialYieldKeepsItsDefaults() => WithPage(page =>
    {
        string? seen = null;
        page.AddHandler(InputElement.KeyDownEvent, (_, _) => seen = MeowChan.Desktop.Controls.KeyContextHost.Current, RoutingStrategies.Bubble, handledEventsToo: true);
        page.RaiseEvent(Key(Avalonia.Input.Key.F5, KeyModifiers.None));
        Assert.Equal("tagging", seen);
    });

    [Fact]
    public void ARightClickBetweenTheColumnWordsFindsTheHeaderThatCarriesTheColumnMenu() => WithPage(page =>
        Assert.NotNull(page.ColumnHeader.Background));

    // ---- the "Rate and tag" popover -----------------------------------------------------------------------------------------

    private static MediaLibraryEntry Entry() =>
        new(new ResourceId("s", "b", "a"), "Ta", 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "1", 1, null, 0,
            "att-v1:" + new string('a', 64), "/dir/a", "asset-a", new string('a', 64), 1, "m1", "original",
            "fa.png", SavedMediaKind.Image, MediaLibraryPresence.Present, false, false);

    private sealed class DecisionHost
    {
        internal int? Rating;
        internal bool Favorite;
        internal readonly List<string> Calls = new();
        internal QuickTagPopoverDecision Make() => new()
        {
            View = () => new GalleryDecisionView(1, Favorite ? GalleryDecisionCoverage.All : GalleryDecisionCoverage.None, Rating, false, true),
            SetRating = value => { Rating = value; Calls.Add("rating:" + (value?.ToString() ?? "none")); return Task.CompletedTask; },
            SetFavorite = selected => { Favorite = selected; Calls.Add("favorite:" + selected); return Task.CompletedTask; },
            HasChanged = () => Calls.Count > 0
        };
    }

    private static KeyEventArgs Key(Key key, KeyModifiers modifiers) => new() { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers };

    private static void WithPopover(DecisionHost host, Action<QuickTagPopover> test)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        MediaLibraryEntry entry = Entry();
        MediaTagSession session = new(new MediaTagSubject.Bound(MediaLibraryFilterEngine.TagTarget(entry)!),
            () => new PrivateTagLoadSnapshot(PrivateTagLoadState.Ready, store.ReadSnapshot(), null),
            (name, subject, token) => Task.FromResult(store.DefineAndSetMediaSubjects(name, [subject], null, token)),
            (id, subject, assigned, token) => Task.FromResult(store.SetMediaSubjects(id, [subject], assigned, null, token)));
        QuickTagPopover popover = QuickTagPopover.Create(new Border(), new QuickTagSession(QuickTagTarget.For(session)), "x",
            new QuickTagPopoverOptions { RestoreFocus = false, Decision = host.Make() });
        try { test(popover); }
        finally { popover.Close(); }
    }

    [Fact]
    public void TheRatingKeysThatTheStarsTooltipsNameRateAndRemoveFromInsideThePopover()
    {
        DecisionHost host = new() { Rating = 2 };
        WithPopover(host, popover =>
        {
            // The star's tooltip says "· Shift+3"; the key has to do it (the popover is a window of its own: the page's key routing never sees it).
            KeyEventArgs three = Key(Avalonia.Input.Key.D3, KeyModifiers.Shift);
            popover.Frame.RaiseEvent(three);
            Assert.True(three.Handled);
            Assert.Equal(3, host.Rating);
            Assert.Equal(3, popover.DecisionRow!.View.Rating);
            Assert.True(popover.DecisionRow.StarButtons[2].IsChecked);

            KeyEventArgs clear = Key(Avalonia.Input.Key.D0, KeyModifiers.Shift);
            popover.Frame.RaiseEvent(clear);
            Assert.True(clear.Handled);
            Assert.Null(host.Rating);

            // Nothing left to remove: the key is still the row's and writes nothing.
            int calls = host.Calls.Count;
            KeyEventArgs again = Key(Avalonia.Input.Key.D0, KeyModifiers.Shift);
            popover.Frame.RaiseEvent(again);
            Assert.True(again.Handled);
            Assert.Equal(calls, host.Calls.Count);

            // The plain digit and any other chord are not ratings.
            KeyEventArgs plain = Key(Avalonia.Input.Key.D3, KeyModifiers.None);
            popover.Frame.RaiseEvent(plain);
            Assert.False(plain.Handled);
            Assert.Equal(calls, host.Calls.Count);
        });
    }

    [Fact]
    public void TheFavouriteKeysAddRemoveAndToggleOnTheRowAsExplicitFlagsAndDoNothingWhenTheStateAlreadyIsAsked()
    {
        DecisionHost host = new();
        WithPopover(host, popover =>
        {
            GalleryDecisionRow row = popover.DecisionRow!;
            Assert.True(row.RequestFavorite(FavoriteKeyAction.Add));
            Assert.True(host.Favorite);
            Assert.True(row.RequestFavorite(FavoriteKeyAction.Add)); // already a favourite: the key is taken, nothing is written again
            Assert.Equal(["favorite:True"], host.Calls);
            Assert.True(row.RequestFavorite(FavoriteKeyAction.Toggle));
            Assert.False(host.Favorite);
            Assert.True(row.RequestFavorite(FavoriteKeyAction.Remove)); // none: nothing to remove
            Assert.Equal(["favorite:True", "favorite:False"], host.Calls);
            Assert.False(row.RequestFavorite(FavoriteKeyAction.None));
        });
    }

    [Fact]
    public void FavouriteLettersAreOnlyTheRowsWhileTheKeyboardIsOnTheRowNeverFromAnywhereElseInThePopover()
    {
        DecisionHost host = new();
        WithPopover(host, popover =>
        {
            // The keyboard is nowhere here (no window): M must not favourite, it is just a letter of the tag field.
            KeyEventArgs add = Key(Avalonia.Input.Key.M, KeyModifiers.None);
            popover.Frame.RaiseEvent(add);
            Assert.False(add.Handled);
            Assert.Empty(host.Calls);
        });
    }

    [Fact]
    public void ABlockedPopoverRatesWithTheKeysToo()
    {
        DecisionHost host = new();
        QuickTagPopover popover = QuickTagPopover.CreateBlocked(new Border(), "provider.jpg", "reason",
            new QuickTagPopoverOptions { RestoreFocus = false, Decision = host.Make() });
        try
        {
            KeyEventArgs five = Key(Avalonia.Input.Key.D5, KeyModifiers.Shift);
            popover.Frame.RaiseEvent(five);
            Assert.True(five.Handled);
            Assert.Equal(5, host.Rating);
        }
        finally { popover.Close(); }
    }

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../.."));
}
