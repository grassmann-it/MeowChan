using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.MediaActions;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

// Tom, 16.11.2026: "Rate and tag sometimes doesn't open on pressing R", "after closing the tagging dialog, arrow up/down no longer works" and "tagging
// must close again with Esc, keyboard-only and mouse-only". The popover's close, focus hand-back and key guard, written as the decisions the popover
// takes (a flyout cannot be shown without an application). Written for the integration stack; not run in the worker's tree.
[Collection("Localization")]
public sealed class RateAndTagPopoverCloseTests : IDisposable
{
    public RateAndTagPopoverCloseTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static readonly DateTimeOffset Day = new(2026, 11, 16, 0, 0, 0, TimeSpan.Zero);

    private static MediaLibraryEntry Entry() =>
        new(new ResourceId("s", "b", "r"), "Tr", 1, Day, Day, "1", 1, null, 0,
            "att-v1:" + new string('c', 64), "/dir/r", "asset-r", new string('c', 64), 1, "m1", "original",
            "fr.png", SavedMediaKind.Image, MediaLibraryPresence.Present, false, false);

    private sealed class Fixture : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly ProfileLease _lease;
        private readonly PrivateTagStore _store;
        private readonly MediaLibraryEntry _entry = Entry();
        internal Fixture()
        {
            _lease = ProfileLease.Acquire(_temp.Path);
            _store = new PrivateTagStore(_temp.Path, _lease);
        }
        internal QuickTagSession Session() => new(QuickTagTarget.For(new MediaTagSession(new MediaTagSubject.Bound(MediaLibraryFilterEngine.TagTarget(_entry)!),
            () => new PrivateTagLoadSnapshot(PrivateTagLoadState.Ready, _store.ReadSnapshot(), null),
            (name, subject, token) => Task.FromResult(_store.DefineAndSetMediaSubjects(name, [subject], null, token)),
            (id, subject, assigned, token) => Task.FromResult(_store.SetMediaSubjects(id, [subject], assigned, null, token)))));
        public void Dispose() { _lease.Dispose(); _temp.Dispose(); }
    }

    private static QuickTagPopoverDecision Decision(bool focusRating = true) => new()
    {
        View = () => new GalleryDecisionView(1, GalleryDecisionCoverage.None, null, false, true),
        SetRating = _ => Task.CompletedTask,
        FocusRating = focusRating
    };

    private static KeyEventArgs Key(Key key, KeyModifiers modifiers = KeyModifiers.None, object? source = null) =>
        new() { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers, Source = source };

    // ---- Esc: at most two presses, from any element ---------------------------------------------------------------------------

    [Fact]
    public void EscWithTextInTheFieldEmptiesItAndTheNextEscClosesThePopover()
    {
        using Fixture fixture = new();
        QuickTagPopover popover = QuickTagPopover.Create(new Border(), fixture.Session(), "x",
            new QuickTagPopoverOptions { RestoreFocus = false, Decision = Decision() });
        QuickTagSurface surface = popover.Surface!;
        surface.Input.Text = "cat";

        // First Esc with text: the route empties the field (the open list goes with it), the popover stays.
        QuickTagKeyRoute first = QuickTagKeys.Route(Avalonia.Input.Key.Escape, KeyModifiers.None, QuickTagFocus.Field, listOpen: true, fieldEmpty: false,
            navigation: false, ShortcutPlatform.Linux);
        Assert.Equal(QuickTagKeyAction.ClearField, first.Action);
        Assert.True(surface.Execute(first));
        Assert.True(string.IsNullOrEmpty(surface.Input.Text));
        Assert.False(popover.IsClosed);

        // Second Esc on the now empty field: the popover closes.
        QuickTagKeyRoute second = QuickTagKeys.Route(Avalonia.Input.Key.Escape, KeyModifiers.None, QuickTagFocus.Field, listOpen: false, fieldEmpty: true,
            navigation: false, ShortcutPlatform.Linux);
        Assert.Equal(QuickTagKeyAction.Close, second.Action);
        Assert.True(surface.Execute(second));
        Assert.Equal(QuickTagPopoverCloseReason.Closed, popover.Reason);
    }

    [Theory]
    [InlineData(QuickTagFocus.Suggestions)]
    [InlineData(QuickTagFocus.Assigned)]
    [InlineData(QuickTagFocus.Other)] // the stars, the favourite button, the ×, "Advanced…"
    public void EscOutsideTheFieldClosesAtOnce(object focus) =>
        Assert.Equal(QuickTagKeyAction.Close, QuickTagKeys.Route(Avalonia.Input.Key.Escape, KeyModifiers.None, (QuickTagFocus)focus, false, false, false,
            ShortcutPlatform.Linux).Action);

    [Fact]
    public async Task AnEscNothingInsideTookStillClosesThePopoverAtItsFrame()
    {
        using Fixture fixture = new();
        QuickTagPopover popover = QuickTagPopover.Create(new Border(), fixture.Session(), "x",
            new QuickTagPopoverOptions { RestoreFocus = false, Decision = Decision() });
        KeyEventArgs escape = Key(Avalonia.Input.Key.Escape);
        popover.Frame.RaiseEvent(escape);
        Assert.True(escape.Handled);
        Assert.Equal(QuickTagPopoverCloseReason.Closed, await popover.Result);
    }

    // ---- no key leaks: what nothing inside took stops at the frame -------------------------------------------------------------

    [Theory]
    [InlineData(Avalonia.Input.Key.Up)]
    [InlineData(Avalonia.Input.Key.Down)]
    [InlineData(Avalonia.Input.Key.PageDown)]
    [InlineData(Avalonia.Input.Key.M)]
    [InlineData(Avalonia.Input.Key.U)]
    [InlineData(Avalonia.Input.Key.R)]
    public void APageKeyInsideThePopoverNeverTravelsOnToThePageBehindIt(Key key)
    {
        QuickTagPopover popover = QuickTagPopover.CreateBlocked(new Border(), "provider.jpg", "reason",
            new QuickTagPopoverOptions { RestoreFocus = false, Decision = Decision(focusRating: false) });
        try
        {
            // Handled at the frame: every handler further up (the anchor's page, the posts list, the tile grid) skips it.
            KeyEventArgs press = Key(key);
            popover.Frame.RaiseEvent(press);
            Assert.True(press.Handled, key + " left the popover unhandled");
            Assert.True(QuickTagPopover.ConsumesUnhandledKey(Key(key)));
            Assert.False(popover.IsClosed);
        }
        finally { popover.Close(); }
    }

    [Fact]
    public void TabAndALoneModifierStayWithTheKeyboardNavigationOfThePopover()
    {
        Assert.False(QuickTagPopover.ConsumesUnhandledKey(Key(Avalonia.Input.Key.Tab)));
        Assert.False(QuickTagPopover.ConsumesUnhandledKey(Key(Avalonia.Input.Key.Tab, KeyModifiers.Shift)));
        Assert.False(QuickTagPopover.ConsumesUnhandledKey(Key(Avalonia.Input.Key.LeftAlt)));
        Assert.False(QuickTagPopover.ConsumesUnhandledKey(Key(Avalonia.Input.Key.LeftShift)));
    }

    [Fact]
    public void TheWindowsKeyGuardClosesOnEscBringsTabInsideAndConsumesEveryOtherKeyOfTheWindowWhileOpen()
    {
        Border frame = new();
        Button inside = new();
        frame.Child = inside;
        Button page = new();
        bool FromWindow(Visual _) => true;

        Assert.Equal(QuickTagPopover.GuardedKeyAction.Close, QuickTagPopover.GuardedRootKey(Key(Avalonia.Input.Key.Escape, source: page), FromWindow, frame));
        Assert.Equal(QuickTagPopover.GuardedKeyAction.FocusInside, QuickTagPopover.GuardedRootKey(Key(Avalonia.Input.Key.Tab, source: page), FromWindow, frame));
        Assert.Equal(QuickTagPopover.GuardedKeyAction.Consume, QuickTagPopover.GuardedRootKey(Key(Avalonia.Input.Key.Down, source: page), FromWindow, frame));
        Assert.Equal(QuickTagPopover.GuardedKeyAction.Consume, QuickTagPopover.GuardedRootKey(Key(Avalonia.Input.Key.M, source: page), FromWindow, frame));
        // The popover's own keys (on a route that reaches the window), another popup's or window's keys, handled keys and lone modifiers stay alone.
        Assert.Null(QuickTagPopover.GuardedRootKey(Key(Avalonia.Input.Key.Down, source: inside), FromWindow, frame));
        Assert.Null(QuickTagPopover.GuardedRootKey(Key(Avalonia.Input.Key.Down, source: page), _ => false, frame));
        KeyEventArgs handled = Key(Avalonia.Input.Key.Down, source: page);
        handled.Handled = true;
        Assert.Null(QuickTagPopover.GuardedRootKey(handled, FromWindow, frame));
        Assert.Null(QuickTagPopover.GuardedRootKey(Key(Avalonia.Input.Key.LeftCtrl, source: page), FromWindow, frame));
    }

    // ---- the focus after a close ----------------------------------------------------------------------------------------------

    [Fact]
    public void TheFocusIsLostWhenItIsNowhereInsideThePopoverOnTheAnchorOrItsAncestorsAndKeptElsewhere()
    {
        Border frame = new() { Child = new Button() };
        Button anchor = new();
        ListBox list = new();
        StackPanel card = new() { Children = { anchor } };
        StackPanel page = new() { Children = { card } };
        Button other = new();
        page.Children.Add(other);
        static bool Usable(Control? control) => control != null;

        Assert.True(QuickTagPopover.FocusIsLost(null, anchor, frame, Usable));
        Assert.True(QuickTagPopover.FocusIsLost((Control)frame.Child!, anchor, frame, Usable)); // still inside the closed popover
        Assert.True(QuickTagPopover.FocusIsLost(anchor, anchor, frame, Usable));               // where the flyout's own hand-back put it
        Assert.True(QuickTagPopover.FocusIsLost(card, anchor, frame, Usable));                 // an ancestor of the anchor (the toolbar, the posts list)
        Assert.True(QuickTagPopover.FocusIsLost(other, anchor, frame, _ => false));            // gone or hidden
        Assert.False(QuickTagPopover.FocusIsLost(other, anchor, frame, Usable));               // put elsewhere on purpose: kept
        Assert.False(QuickTagPopover.FocusIsLost(list, anchor, frame, Usable));
    }

    [Fact]
    public void TheNearestFocusableOwnerOfTheAnchorTakesTheKeyboardWhenTheOriginIsGone()
    {
        // A thumbnail (not focusable) inside a post card (focusable): the card takes the keyboard.
        Panel card = new() { Focusable = true };
        Border thumbnail = new() { Focusable = false };
        card.Children.Add(thumbnail);
        StackPanel list = new() { Children = { card } };

        Assert.Same(card, QuickTagPopover.NearestFocusable(thumbnail, control => control != null));
        Assert.Null(QuickTagPopover.NearestFocusable(thumbnail, _ => false)); // nothing usable: the host's fallback decides
        Assert.Same(list.Children[0], card);
    }

    [Theory]
    [InlineData(QuickTagPopoverCloseReason.Closed, true)]
    [InlineData(QuickTagPopoverCloseReason.Dismissed, true)]
    [InlineData(QuickTagPopoverCloseReason.Stale, true)]
    [InlineData(QuickTagPopoverCloseReason.Detached, true)]
    [InlineData(QuickTagPopoverCloseReason.Advanced, false)]
    [InlineData(QuickTagPopoverCloseReason.Replaced, false)]
    public void EveryCloseButAdvancedAndAReplacementGivesTheKeyboardBack(object reason, bool restores) =>
        Assert.Equal(restores, QuickTagPopover.RestoresFocus((QuickTagPopoverCloseReason)reason));

    [Fact]
    public void AnOriginWithoutAWindowOrInAnotherPopupIsNoOrigin()
    {
        Assert.Null(QuickTagPopover.OriginOf(null, new Button()));
        Assert.Null(QuickTagPopover.OriginOf(null, null));
    }

    // ---- toggle: open, close, R again opens ------------------------------------------------------------------------------------

    [Fact]
    public void AHostsOpenFlagEndsSynchronouslyWithTheCloseSoTheNextROpensAgain()
    {
        using Fixture fixture = new();
        bool open = false;
        int opened = 0;
        QuickTagPopover? current = null;
        // The host's gate as the shell, the media page and the tagging page keep it: R is ignored while their popover is open.
        void PressR()
        {
            if (open) return;
            open = true;
            opened++;
            current = QuickTagPopover.Create(new Border(), fixture.Session(), "x",
                new QuickTagPopoverOptions { RestoreFocus = false, Decision = Decision() });
            current.WhenClosed(() => open = false);
        }

        PressR();
        Assert.Equal(1, opened);
        PressR(); // still open: the same popover
        Assert.Equal(1, opened);
        current!.Close(QuickTagPopoverCloseReason.Closed); // Esc or ×
        // No continuation has run yet (nothing awaited Result): the flag is already down.
        Assert.False(open);
        PressR();
        Assert.Equal(2, opened);
        current!.Close(QuickTagPopoverCloseReason.Dismissed); // a click outside
        Assert.False(open);
        PressR();
        Assert.Equal(3, opened);
        current!.Close();

        // A release registered after the close runs at once.
        bool late = false;
        current.WhenClosed(() => late = true);
        Assert.True(late);
    }

    [Fact]
    public void EveryHostReleasesItsOpenFlagWithThePopoverNotWithItsContinuation()
    {
        string shell = Source("Desktop/Shell/MainWindow.QuickTags.cs");
        Assert.Equal(2, Count(shell, "popover.WhenClosed(Release);"));
        Assert.Contains("_mediaTagDialogOpen = false;", shell);
        Assert.Contains("_galleryTagDialogOpen = false;", shell);
        // The media page's popover gives the keyboard back at once (↑/↓ and R right after Esc), with the page's grid as the fallback.
        Assert.Contains("PopoverOptions(context, hostCurrent, restoreFocus: true, \"MediaTagPopover\")", shell);
        Assert.Contains("FocusFallback = request.FocusFallback", shell);
        Assert.Contains("popover.WhenClosed(Release);", Source("Desktop/Views/Media/MediaPage.Selection.cs"));
        Assert.Contains("popover.WhenClosed(Release);", Source("Desktop/Views/Archive/ArchivePage.MediaSelection.cs"));
        Assert.Contains("popover.WhenClosed(Release);", Source("Desktop/Views/Tagging/TaggingPage.Tagging.cs"));
        // The focus hand-back runs at once and again before the next key (Loaded), not at Background after it.
        string popover = Source("Desktop/Controls/QuickTagPopover.cs");
        Assert.Contains("Dispatcher.UIThread.Post(() => RestoreFocusNow(), DispatcherPriority.Loaded);", popover);
        Assert.DoesNotContain("}, DispatcherPriority.Background);\n    }\n\n    // ---- keys", popover);
    }

    // ---- R with a text selection --------------------------------------------------------------------------------------------

    [Fact]
    public void ATextSelectionInAPostDoesNotKeepABareLetterButKeepsCopyAndTheTextFieldKeepsEverything()
    {
        SelectableTextBlock text = new() { Text = "a post body to select" };
        text.SelectionStart = 0;
        text.SelectionEnd = 6;
        Assert.True(CommandRouter.IsNativeInput(text, Avalonia.Input.Key.R)); // the general rule is unchanged
        Assert.True(CommandRouter.OnlyTextSelectionHoldsKey(Key(Avalonia.Input.Key.R, source: text)));
        Assert.False(CommandRouter.OnlyTextSelectionHoldsKey(Key(Avalonia.Input.Key.C, KeyModifiers.Control, text)));
        Assert.False(CommandRouter.OnlyTextSelectionHoldsKey(Key(Avalonia.Input.Key.A, KeyModifiers.Meta, text)));
        Assert.False(CommandRouter.OnlyTextSelectionHoldsKey(Key(Avalonia.Input.Key.R, source: new TextBox())));
        SelectableTextBlock unselected = new() { Text = "nothing selected" };
        Assert.False(CommandRouter.IsNativeInput(unselected, Avalonia.Input.Key.R));
        Assert.False(CommandRouter.OnlyTextSelectionHoldsKey(Key(Avalonia.Input.Key.R, source: unselected)));
    }

    [Fact]
    public void TheReaderAndTheGalleryLetROutOfATextSelection()
    {
        Assert.Contains("AllowInTextSelection: true", Source("Desktop/Views/Archive/ArchivePage.RatingCommands.cs"));
        Assert.Contains("RequestRateAndTag, Priority: 200, AllowInTextSelection: true", Source("Desktop/GalleryViewer.cs"));
    }

    // ---- Esc on the other light-dismiss popups -------------------------------------------------------------------------------

    [Fact]
    public void EscClosesEveryOpenLightDismissPopupThatNothingInsideHandledItFor()
    {
        Assert.True(PopupEscape.ClosesOnEscape(Key(Avalonia.Input.Key.Escape), open: true, lightDismiss: true));
        Assert.False(PopupEscape.ClosesOnEscape(Key(Avalonia.Input.Key.Escape), open: true, lightDismiss: false)); // the quote preview, the hover enlargement
        Assert.False(PopupEscape.ClosesOnEscape(Key(Avalonia.Input.Key.Escape), open: false, lightDismiss: true));
        Assert.False(PopupEscape.ClosesOnEscape(Key(Avalonia.Input.Key.Escape, KeyModifiers.Shift), open: true, lightDismiss: true));
        KeyEventArgs taken = Key(Avalonia.Input.Key.Escape);
        taken.Handled = true; // a field that cleared its text, a list that closed first
        Assert.False(PopupEscape.ClosesOnEscape(taken, open: true, lightDismiss: true));
        Assert.Contains("MeowChan.Desktop.Controls.PopupEscape.Install();", Source("App.axaml.cs"));
    }

    internal static string Source(string relative, [CallerFilePath] string sourcePath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative))).Replace("\r\n", "\n");

    private static int Count(string text, string part)
    {
        int count = 0;
        for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal)) count++;
        return count;
    }
}

// R on the Media page, raised on the real source element (the tile's container, the grid, another control of the page), over the page fixture of the tile
// Enter tests.
[Collection("Booru exact navigation")]
public sealed class RateAndTagMediaPageKeyTests
{
    private sealed record Opened(MediaLibraryEntry Entry, Control Anchor, RateAndTagRequest Request);

    private static List<Opened> RecordPopovers(MediaTileEnterTests.Fixture fixture)
    {
        List<Opened> opened = new();
        fixture.Page.TagPopoverAsync = (entry, anchor, _, _, request) => { opened.Add(new(entry, anchor, request)); return Task.CompletedTask; };
        return opened;
    }

    [Fact]
    public void ROnAFocusedTileThatIsNotSelectedMakesItTheSelectionAndOpensRateAndTagOnIt() => MediaTileEnterTests.Run(fixture =>
    {
        List<Opened> opened = RecordPopovers(fixture);
        fixture.Grid.SelectedIndex = 0; // Ctrl+arrow moved the focus and left the selection where it was

        fixture.Press(fixture.Container(2), Avalonia.Input.Key.R);

        Assert.True(fixture.LastHandled);
        Opened open = Assert.Single(opened);
        Assert.Same(fixture.Entries[2], open.Entry);
        Assert.True(open.Request.FocusRating);
        Assert.NotNull(open.Request.FocusFallback); // the grid takes the keyboard when the tile is gone after the close
        Assert.Equal(new[] { 2 }, fixture.Grid.Selection.SelectedIndexes.ToArray());
    });

    [Fact]
    public void ROnAFocusedTileWithNothingSelectedOpensRateAndTagOnItInsteadOfDoingNothing() => MediaTileEnterTests.Run(fixture =>
    {
        // The return from the gallery focuses a tile and selects none: R did nothing there.
        List<Opened> opened = RecordPopovers(fixture);
        fixture.Grid.SelectedIndex = -1;

        fixture.Press(fixture.Container(1), Avalonia.Input.Key.R);

        Assert.Same(fixture.Entries[1], Assert.Single(opened).Entry);
    });

    [Fact]
    public void ROnATileWhoseFileIsNotSavedSaysSo() => MediaTileEnterTests.Run(fixture =>
    {
        List<Opened> opened = RecordPopovers(fixture);
        fixture.Press(fixture.Container(MediaTileEnterTests.Fixture.MissingIndex), Avalonia.Input.Key.R);
        Assert.Empty(opened);
        Assert.Equal(MediaMenuReasons.NotSaved, fixture.SelectionNote);
    });

    [Fact]
    public void ROnTheGridWithoutATileSaysWhyInsteadOfSilence() => MediaTileEnterTests.Run(fixture =>
    {
        List<Opened> opened = RecordPopovers(fixture);
        fixture.Grid.SelectedIndex = -1;
        fixture.Press(fixture.Grid, Avalonia.Input.Key.R);
        Assert.True(fixture.LastHandled);
        Assert.Empty(opened);
        Assert.Equal(MediaMenuReasons.Unavailable, fixture.SelectionNote);
    });

    [Fact]
    public void ROnAnotherControlOfThePageLikeAFilterChipOpensRateAndTagForTheSelectedTile() => MediaTileEnterTests.Run(fixture =>
    {
        List<Opened> opened = RecordPopovers(fixture);
        fixture.Grid.SelectedIndex = 1;
        Button chip = new() { Name = "MediaFilterChip" }; // a chip, a sort or layout button: outside the grid's own route
        KeyEventArgs press = new() { RoutedEvent = InputElement.KeyDownEvent, Key = Avalonia.Input.Key.R, Source = chip };
        fixture.Page.RaiseEvent(press);
        for (int attempt = 0; attempt < 50; attempt++) Dispatcher.UIThread.RunJobs();

        Assert.True(press.Handled);
        Assert.Same(fixture.Entries[1], Assert.Single(opened).Entry);
    });

    [Fact]
    public void RInATextFieldOfThePageStaysTyping() => MediaTileEnterTests.Run(fixture =>
    {
        List<Opened> opened = RecordPopovers(fixture);
        fixture.Grid.SelectedIndex = 1;
        KeyEventArgs press = new() { RoutedEvent = InputElement.KeyDownEvent, Key = Avalonia.Input.Key.R, Source = new TextBox() };
        fixture.Page.RaiseEvent(press);
        for (int attempt = 0; attempt < 50; attempt++) Dispatcher.UIThread.RunJobs();

        Assert.False(press.Handled);
        Assert.Empty(opened);
    });

    [Fact]
    public void RAfterThePopoverClosedOpensItAgain() => MediaTileEnterTests.Run(fixture =>
    {
        // Open → close (the host's task completes when the popover closed) → R again opens: no stale "open" state on the page.
        List<Opened> opened = RecordPopovers(fixture);
        fixture.Grid.SelectedIndex = 1;
        fixture.Press(fixture.Container(1), Avalonia.Input.Key.R);
        fixture.Press(fixture.Container(1), Avalonia.Input.Key.R);
        Assert.Equal(2, opened.Count);
        Assert.All(opened, open => Assert.Same(fixture.Entries[1], open.Entry));
    });

    [Fact]
    public void RWhileTheIndexIsBeingBuiltSaysSo() => MediaTileEnterTests.Run(fixture =>
    {
        List<Opened> opened = RecordPopovers(fixture);
        FieldInfo building = typeof(MediaPage).GetField("_building", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object? before = building.GetValue(fixture.Page);
        using CancellationTokenSource indexing = new();
        building.SetValue(fixture.Page, indexing);
        try
        {
            fixture.Grid.SelectedIndex = 1;
            fixture.Press(fixture.Container(1), Avalonia.Input.Key.R);
            Assert.Empty(opened);
            Assert.Equal(Texts.T("media.selection.reason.building"), fixture.SelectionNote);
        }
        finally { building.SetValue(fixture.Page, before); }
    });
}

// The gallery, the tagging page and the reader: what can run without a window, plus the routing contracts of the reader's R.
[Collection("Gallery viewer construction")]
public sealed class RateAndTagSurfaceTests : IDisposable
{
    public RateAndTagSurfaceTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static string Source(string relative, [CallerFilePath] string sourcePath = "") => RateAndTagPopoverCloseTests.Source(relative, sourcePath);

    // ---- the reader -------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheReadersRIsNoLongerConsumedInSilenceWithoutAFocusedMedium()
    {
        string commands = Source("Desktop/Views/Archive/ArchivePage.RatingCommands.cs");
        int at = commands.IndexOf("_ratingCommands.Register(new(\"media.rating.edit\"", StringComparison.Ordinal);
        Assert.True(at >= 0);
        string registration = commands[at..commands.IndexOf("Priority: 100", at, StringComparison.Ordinal)];
        // Before: CanExecute asked for a focused medium, and the router consumes a matched key whose action cannot run.
        Assert.DoesNotContain("FocusedMediaForRateAndTag() != null", registration);
        Assert.Contains("RateAndTagByKeyAsync", registration);

        string media = Source("Desktop/Views/Archive/ArchivePage.MediaCommands.cs");
        int body = media.IndexOf("internal async Task RateAndTagByKeyAsync()", StringComparison.Ordinal);
        string key = media[body..media.IndexOf("private void SelectPostOfFocusedText()", body, StringComparison.Ordinal)];
        Assert.Contains("await _mediaReady.Task;", key);            // a quote jump or a turned page: the key waits for the media scan
        Assert.Contains("SelectedPostRateAndTagMedium()", key);     // the focus on a toolbar or paging button: the selected post's medium
        Assert.Contains("Message(RateAndTagRefusal());", key);      // no medium: the reason, never silence
        Assert.Contains("SelectPostOfFocusedText();", key);         // a text selection in a post that is not the selected one
        Assert.Contains("new RateAndTagRequest(focusRating, subject.TagsBlocked, FocusAfterRateAndTag)", media);
        // After the close without its origin, the keyboard goes to the selected post card (or the posts list), where ↑/↓ and R work.
        Assert.Contains("posts.SelectedItem is { } item && posts.ContainerFromItem(item) is Control card && card.Focus()", media);
    }

    // ---- the gallery ------------------------------------------------------------------------------------------------------

    private static GalleryEntry LooseImage()
    {
        var asset = new MediaAssetReference("asset", "image", "original", "loose.png", "/fixture/loose.png", 7,
            new string('a', 64), "https://fixture.invalid/loose.png", "loose.png", true);
        var manifest = new MediaAssetSnapshot("fixture", MediaManifestStatus.Available, 1, "generation", [asset]);
        return new(new(new("fixture", "b", "42"), manifest.Directory, asset.AssetId), manifest, asset, null, null, false, true);
    }

    [Fact]
    public void RInTheGalleryWhoseStarsCannotTakeTheKeyboardSaysWhyInsteadOfNothing() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
        GalleryViewer viewer = new();
        List<string> said = new();
        viewer.MenuExplanationRequested += said.Add;
        typeof(GalleryViewer).GetField("_entry", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewer, LooseImage());
        viewer.SetRating(null, null, available: true); // the stars could rate it, but no window gives them the keyboard
        Assert.True(viewer.CanRateAndTag);

        viewer.RequestRateAndTag();
        for (int attempt = 0; attempt < 50; attempt++) Dispatcher.UIThread.RunJobs();

        Assert.False(string.IsNullOrWhiteSpace(Assert.Single(said)));
    });

    // ---- the tagging page -------------------------------------------------------------------------------------------------

    private static void WithTaggingPage(Action<MeowChan.TaggingPage> test) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
        TokenApplication.Bind();
        string profile = Directory.CreateTempSubdirectory("meowchan-rate-and-tag-").FullName;
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

    [Fact]
    public void RIsACommandOfTheTaggingPageAndSaysWhyWhenNothingCanBeRatedAndTagged() => WithTaggingPage(page =>
    {
        Assert.Contains("tagging", EffectiveShortcutBindings.ExecutionContexts("media.rating.edit"));
        page.RateAndTagByKeyAsync(null).GetAwaiter().GetResult();
        Assert.True(page.StatusText.IsVisible);
        Assert.False(string.IsNullOrWhiteSpace(page.StatusText.Text));
        Assert.Null(page.TagPopover);
    });

    [Fact]
    public void TheTaggingListRoutesRBeforeItsOwnCommandsAndTheStarsComeFromTheShell()
    {
        string commands = Source("Desktop/Views/Tagging/TaggingPage.Commands.cs");
        int r = commands.IndexOf("ShortcutService.Current.Matches(e, \"media.rating.edit\")", StringComparison.Ordinal);
        int loop = commands.IndexOf("foreach (string id in TaggingCommands.All)", StringComparison.Ordinal);
        Assert.True(r >= 0 && loop > r, "R must be taken before the list's commands and its type-ahead.");
        Assert.Contains("DecisionForEntries = DecisionForMediaEntries", Source("Desktop/Shell/MainWindow.TaggingPage.cs"));
        Assert.Contains("TagSelectionAsync(anchor, focusRating: true)", Source("Desktop/Views/Tagging/TaggingPage.Tagging.cs"));
    }
}
