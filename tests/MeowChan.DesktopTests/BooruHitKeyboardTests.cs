using System.Reflection;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

/// <summary>
/// Interaction audit V11 and V12: the Booru hit cards are keyboard stops (Enter, arrows, context menu) and a double click on a watcher
/// shows its query in the search box. Fixtures only: fake ports, no provider request, no window, no application. The live focus ring
/// and the focus move itself are checked by the desktop smoke (BooruWorkbenchViewChecks), which needs a window.
/// </summary>
[Collection("Localization")]
public sealed class BooruHitKeyboardTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    // ---- the routing, without a view --------------------------------------------------------------------------------

    private static KeyEventArgs Press(Key key, object source, KeyModifiers modifiers = KeyModifiers.None) =>
        new() { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers, Source = source };

    private static Border Card(out Button button)
    {
        button = new Button { Name = "action" };
        return new Border { Child = new StackPanel { Children = { new TextBlock(), button } } };
    }

    [Theory]
    [InlineData(nameof(Avalonia.Input.Key.Up), "Previous")]
    [InlineData(nameof(Avalonia.Input.Key.Down), "Next")]
    [InlineData(nameof(Avalonia.Input.Key.Home), "First")]
    [InlineData(nameof(Avalonia.Input.Key.End), "Last")]
    [InlineData(nameof(Avalonia.Input.Key.Enter), "Activate")]
    [InlineData(nameof(Avalonia.Input.Key.Return), "Activate")]
    [InlineData(nameof(Avalonia.Input.Key.Left), "None")]
    [InlineData(nameof(Avalonia.Input.Key.Space), "None")]
    public void TheKeysOfAFocusedHitCardRouteToTheirAction(string keyName, string expected)
    {
        // Enter and Return alias the same enum value; names keep both xUnit case IDs distinct.
        Key key = Enum.Parse<Key>(keyName);
        Border card = Card(out _);
        Assert.Equal(Enum.Parse<BooruHitKey>(expected), BooruHitKeys.Route(Press(key, card), card));
    }

    [Fact]
    public void ArrowsMoveOnlyWhileTheCardItselfHoldsTheFocusAndEnterNeverTakesOverAButton()
    {
        Border card = Card(out Button button);
        // A button inside the card keeps its own Enter, and the arrows stay with whatever part has the focus.
        Assert.Equal(BooruHitKey.None, BooruHitKeys.Route(Press(Avalonia.Input.Key.Enter, button), card));
        Assert.Equal(BooruHitKey.None, BooruHitKeys.Route(Press(Avalonia.Input.Key.Down, button), card));
        // Selectable text keeps its own keys too.
        Assert.Equal(BooruHitKey.None, BooruHitKeys.Route(Press(Avalonia.Input.Key.Enter, new SelectableTextBlock()), card));
        // A non-interactive part inside the card (its picture, a label) hands Enter to the card.
        TextBlock label = (TextBlock)((StackPanel)card.Child!).Children[0];
        Assert.Equal(BooruHitKey.Activate, BooruHitKeys.Route(Press(Avalonia.Input.Key.Enter, label), card));
    }

    [Fact]
    public void ModifiersHandledKeysAndShortcutCaptureNeverRoute()
    {
        Border card = Card(out _);
        Assert.Equal(BooruHitKey.None, BooruHitKeys.Route(Press(Avalonia.Input.Key.Enter, card, KeyModifiers.Control), card));
        Assert.Equal(BooruHitKey.None, BooruHitKeys.Route(Press(Avalonia.Input.Key.Down, card, KeyModifiers.Shift), card));
        KeyEventArgs handled = Press(Avalonia.Input.Key.Down, card);
        handled.Handled = true;
        Assert.Equal(BooruHitKey.None, BooruHitKeys.Route(handled, card));
        Assert.Equal(BooruHitKey.None, BooruHitKeys.Route(Press(Avalonia.Input.Key.Enter, card), card, shortcutCapture: true));
    }

    [Fact]
    public void TheTargetsStopAtBothEndsAndNeverWrap()
    {
        Assert.Equal(2, BooruHitKeys.Target(BooruHitKey.Next, 1, 4));
        Assert.Equal(3, BooruHitKeys.Target(BooruHitKey.Next, 3, 4));
        Assert.Equal(0, BooruHitKeys.Target(BooruHitKey.Previous, 0, 4));
        Assert.Equal(0, BooruHitKeys.Target(BooruHitKey.First, 3, 4));
        Assert.Equal(3, BooruHitKeys.Target(BooruHitKey.Last, 0, 4));
        Assert.Equal(-1, BooruHitKeys.Target(BooruHitKey.Next, -1, 4));
        Assert.Equal(-1, BooruHitKeys.Target(BooruHitKey.Next, 0, 0));
        Assert.Equal(-1, BooruHitKeys.Target(BooruHitKey.Activate, 0, 4));
    }

    [Fact]
    public void TheContextOpenCommandNamesTheBooruHitAndTheBooruPage()
    {
        MeowChan.Desktop.Commands.CommandDefinition open = MeowChan.Desktop.Commands.CommandCatalog.Get("menu.context-open");
        Assert.Contains("booru-hit", open.Contexts);
        Assert.Contains("booru", open.ExecutionContexts);
        Assert.Contains(Avalonia.Input.Key.Apps.ToString(), MeowChan.Desktop.Commands.EffectiveShortcutBindings
            .Build(null, MeowChan.Desktop.Commands.ShortcutPlatform.Linux).For("menu.context-open").Select(gesture => gesture.Key.ToString()));
    }

    // ---- the cards of a real view -----------------------------------------------------------------------------------

    [Fact]
    public void HitsAreKeyboardStopsWithStableNamesAndLocalizedHelp() => OnDispatcher(async () =>
    {
        BooruUiHarness harness = Harness(out BooruWorkbenchModel model, out BooruWorkbenchView view);
        using (model) using (view)
        {
            harness.Queue.States["101"] = BooruLocalState.Loaded;
            view.ShowInMedia = _ => Task.FromResult(true);
            List<Border> cards = await SearchAsync(harness, model, view);
            Assert.Equal(3, cards.Count);
            foreach (Border card in cards)
            {
                Assert.True(card.Focusable);
                Assert.True(card.IsTabStop);
                Assert.Contains("workspace-card-focusable", card.Classes);
                Assert.Equal(AutomationProperties.GetSizeOfSet(card), 3);
            }
            Assert.Equal(new[] { 1, 2, 3 }, cards.Select(AutomationProperties.GetPositionInSet));
            Assert.Equal("BooruResult:101", AutomationProperties.GetName(cards[0]));
            Assert.Equal("BooruResult:102", AutomationProperties.GetName(cards[1]));
            Assert.Equal("Post #101, rating General, local copy: yes", AutomationProperties.GetHelpText(cards[0]));
            Assert.Equal("Post #102, rating General, local copy: no", AutomationProperties.GetHelpText(cards[1]));
            Texts.SetLanguage("de");
            try
            {
                await Task.Delay(30); // the view renders the language change on a posted callback
                Assert.Equal("BooruResult:102", AutomationProperties.GetName(Cards(view)[1]));
                Assert.Equal("Post #102, Einstufung Allgemein, lokale Kopie: nein", AutomationProperties.GetHelpText(Cards(view)[1]));
            }
            finally { Texts.SetLanguage("en"); }
        }
    });

    [Fact]
    public void EnterOnALocallySavedHitShowsItInMediaAndEnterWithoutACopyLoadsNothing() => OnDispatcher(async () =>
    {
        BooruUiHarness harness = Harness(out BooruWorkbenchModel model, out BooruWorkbenchView view);
        using (model) using (view)
        {
            harness.Queue.States["101"] = BooruLocalState.Loaded;
            List<string> shown = new();
            view.ShowInMedia = id => { shown.Add(id); return Task.FromResult(true); };
            List<Border> cards = await SearchAsync(harness, model, view);

            TextBlock savedTitle = cards[0].GetLogicalDescendants().OfType<TextBlock>().First();
            KeyEventArgs enter = Press(Avalonia.Input.Key.Enter, savedTitle);
            Raise(savedTitle, enter);
            Assert.True(enter.Handled);
            await BooruTestSupport.WaitAsync(() => shown.Count == 1, "the Show in Media action");
            Assert.Equal("101", shown[0]);

            // No local copy: Enter and a double click open the inline gallery on the hit (F13a), in particular no download (decision 95).
            TextBlock title = cards[1].GetLogicalDescendants().OfType<TextBlock>().First();
            KeyEventArgs without = Press(Avalonia.Input.Key.Enter, title);
            Raise(title, without);
            Assert.True(without.Handled);
            Assert.Equal(BooruResultView.Gallery, view.ResultView);
            Assert.Equal("102", view.Gallery.CurrentPostId);
            view.SetResultView(BooruResultView.List, save: false);
            TappedEventArgs doubleTap = DoubleTap(cards[1], cards[1]);
            Raise(cards[1], doubleTap);
            Assert.True(doubleTap.Handled);
            Assert.Equal(BooruResultView.Gallery, view.ResultView);
            view.SetResultView(BooruResultView.List, save: false);
            await Task.Delay(20);
            Assert.Empty(harness.Queue.Loads);
            Assert.Equal(0, harness.ProviderRequests - harness.Search.Requests);
            Assert.Single(shown);

            // A double click on the saved hit is the same action; on its button it is not.
            Button showButton = Button(cards[0], BooruWorkbenchView.ShowInMediaPrefix + "101");
            TappedEventArgs onShow = DoubleTap(cards[0], showButton);
            Raise(showButton, onShow);
            Assert.False(onShow.Handled);
            TappedEventArgs onCard = DoubleTap(cards[0], cards[0]);
            Raise(cards[0], onCard);
            Assert.True(onCard.Handled);
            await BooruTestSupport.WaitAsync(() => shown.Count == 2, "the double click action");
        }
    });

    [Fact]
    public void ArrowKeysOnTheCardAreHandledAndTheContextKeysBelongToTheMenuHost() => OnDispatcher(async () =>
    {
        BooruUiHarness harness = Harness(out BooruWorkbenchModel model, out BooruWorkbenchView view);
        using (model) using (view)
        {
            List<Border> cards = await SearchAsync(harness, model, view);
            foreach (Key key in new[] { Avalonia.Input.Key.Down, Avalonia.Input.Key.Up, Avalonia.Input.Key.Home, Avalonia.Input.Key.End })
            {
                KeyEventArgs arrow = Press(key, cards[1]);
                Raise(cards[1], arrow);
                Assert.True(arrow.Handled, key.ToString());
            }
            // The Menu key and Shift+F10 reach the menu host from the card and from a button inside it.
            foreach (Interactive source in new Interactive[] { cards[0], Button(cards[0], BooruWorkbenchView.LoadPrefix + "101") })
            {
                KeyEventArgs apps = Press(Avalonia.Input.Key.Apps, source);
                Raise(source, apps);
                Assert.True(apps.Handled);
                KeyEventArgs shiftF10 = Press(Avalonia.Input.Key.F10, source, KeyModifiers.Shift);
                Raise(source, shiftF10);
                Assert.True(shiftF10.Handled);
            }
        }
    });

    // A card's link buttons belong to the card's own action toolbar: they are built without a panel of their own, so rendering the same
    // hit again (a language change, a new local state) never moves a control that still has a visual parent.
    [Fact]
    public void TheLinkActionsOfAHitLiveInTheCardsOwnToolbarAndSurviveARerender() => OnDispatcher(async () =>
    {
        BooruUiHarness harness = Harness(out BooruWorkbenchModel model, out BooruWorkbenchView view);
        using (model) using (view)
        {
            List<Border> cards = await SearchAsync(harness, model, view);
            WrapPanel actions = cards[0].GetLogicalDescendants().OfType<WrapPanel>().Single(panel => panel.Name == "BooruResultActions:101");
            foreach (string name in new[] { BooruWorkbenchView.LoadPrefix + "101", BooruWorkbenchView.OpenPostPrefix + "101", BooruWorkbenchView.CopyLinkPrefix + "101" })
                Assert.Contains(actions.Children.OfType<Button>(), button => button.Name == name);

            Texts.SetLanguage("de");
            try
            {
                await Task.Delay(30);
                Assert.Equal(3, Cards(view).Count);
                WrapPanel rebuilt = Cards(view)[0].GetLogicalDescendants().OfType<WrapPanel>().Single(panel => panel.Name == "BooruResultActions:101");
                Assert.Contains(rebuilt.Children.OfType<Button>(), button => button.Name == BooruWorkbenchView.OpenPostPrefix + "101");
            }
            finally { Texts.SetLanguage("en"); }
        }
    });

    // ---- V12: the watcher double click ------------------------------------------------------------------------------

    [Fact]
    public void ADoubleClickOnAWatcherShowsItsQueryInTheSearchBoxWithoutSubmitting() => OnDispatcher(async () =>
    {
        BooruUiHarness harness = new();
        harness.Previews.OnPreview = _ => null;
        harness.Watchers.Seed(BooruUiFixtures.Watcher("w1", "cat blue_eyes", clock: harness.Clock));
        using BooruWorkbenchModel model = new(harness.Services);
        using BooruWorkbenchView view = new(model, ownsModel: false);
        await view.ActivateAsync();
        await BooruTestSupport.WaitAsync(() => Field<StackPanel>(view, "_watchers").Children.Count == 1, "the watcher card");
        Border card = (Border)Field<StackPanel>(view, "_watchers").Children[0];
        SearchBox input = Field<SearchBox>(view, "_input");
        TabControl tabs = Field<TabControl>(view, "_tabs");
        view.ShowWatchers();
        int requests = harness.ProviderRequests;

        // A button or the selectable query keeps its own double click.
        Button check = card.GetLogicalDescendants().OfType<Button>().First();
        TappedEventArgs onButton = DoubleTap(card, check);
        Raise(check, onButton);
        Assert.False(onButton.Handled);
        Assert.False(onButton.Handled);
        TappedEventArgs onText = DoubleTap(card, card.GetLogicalDescendants().OfType<SelectableTextBlock>().First());
        Raise(card.GetLogicalDescendants().OfType<SelectableTextBlock>().First(), onText);
        Assert.False(onText.Handled);
        Assert.Equal(string.Empty, input.Text ?? string.Empty);

        TappedEventArgs onCard = DoubleTap(card, card);
        Raise(card, onCard);
        Assert.True(onCard.Handled);
        Assert.NotEqual(BooruSearchTabs.PrimaryId, view.SearchTabs.ActiveId);
        Assert.Equal(view.SearchTabs.ActiveId, Assert.IsType<TabItem>(tabs.SelectedItem).Tag);
        Assert.Equal("cat blue_eyes", input.Text);
        Assert.Equal("cat blue_eyes", model.Search.View.Input);
        // Settled once the tab is laid out: the caret sits behind the query, nothing is selected.
        await Task.Delay(30);
        Assert.Equal("cat blue_eyes".Length, input.CaretIndex);
        Assert.Equal(input.SelectionStart, input.SelectionEnd);
        // Nothing was requested: no search, no check, no load.
        Assert.Equal(requests, harness.ProviderRequests);
        Assert.Empty(harness.Search.Searches);
        Assert.Equal(0, harness.Watchers.Checks);
    });

    // ---- dead-affordance review 15.11.: source-tag chips, "+N more", middle click, watcher keyboard ------------------------------

    [Fact]
    public void ASourceTagChipHasItsOwnMenuToEditAndCopyTheTag() => OnDispatcher(async () =>
    {
        BooruUiHarness harness = Harness(out BooruWorkbenchModel model, out BooruWorkbenchView view);
        using (model) using (view)
        {
            List<Border> cards = await SearchAsync(harness, model, view);
            SearchBox input = Field<SearchBox>(view, "_input");
            input.Text = string.Empty;
            model.Search.SetInput(string.Empty);

            ContextMenuModel menu = TagMenu(view, "cat");
            Assert.Equal(new[] { BooruWorkbenchView.MenuTagAdd, BooruWorkbenchView.MenuTagExclude, BooruWorkbenchView.MenuTagRemove,
                FilterChipMenu.CopyName }, menu.Entries.Select(entry => entry.Id));
            Assert.All(menu.Entries, entry => Assert.True(entry.Available));
            Assert.Equal("Add to search", Entries(menu)[0].Label);
            Assert.Equal("Exclude from search", Entries(menu)[1].Label);

            // Add, then exclude another tag: "-tag" is the provider's exclusion; nothing is requested.
            int requests = harness.ProviderRequests;
            Entries(menu)[0].Invoke!();
            Assert.Equal("cat", input.Text);
            Entries(TagMenu(view, "sky"))[1].Invoke!();
            Assert.Equal("cat -sky", input.Text);
            Assert.Equal("cat -sky", model.Search.View.Input);
            // The same tag the other way round replaces its term (both together would match nothing), and an existing term is not added twice.
            Entries(TagMenu(view, "cat"))[1].Invoke!();
            Assert.Equal("-cat -sky", input.Text);
            Entries(TagMenu(view, "sky"))[1].Invoke!();
            Assert.Equal("-cat -sky", input.Text);
            Assert.Equal(requests, harness.ProviderRequests);
            Assert.DoesNotContain(harness.Search.Searches, search => search.Query.Contains('-'));

            // The chip of a hit carries a menu host of its own, so a right click on it never opens the card's menu.
            Button chip = Button(cards[0], BooruWorkbenchView.ResultTagPrefix + "101:cat");
            KeyEventArgs apps = Press(Avalonia.Input.Key.Apps, chip);
            Raise(chip, apps);
            Assert.True(apps.Handled);
        }
    });

    [Fact]
    public void AWatcherCardIsAKeyboardStopWhoseEnterShowsItsQueryAndWhoseMenuKeyOpensItsMenu() => OnDispatcher(async () =>
    {
        BooruUiHarness harness = new();
        harness.Previews.OnPreview = _ => null;
        harness.Watchers.Seed(BooruUiFixtures.Watcher("w1", "cat blue_eyes", clock: harness.Clock), BooruUiFixtures.Watcher("w2", "sky", clock: harness.Clock));
        using BooruWorkbenchModel model = new(harness.Services);
        using BooruWorkbenchView view = new(model, ownsModel: false);
        await view.ActivateAsync();
        StackPanel watchers = Field<StackPanel>(view, "_watchers");
        await BooruTestSupport.WaitAsync(() => watchers.Children.Count == 2, "the watcher cards");
        Border first = (Border)watchers.Children[0];
        Assert.True(first.Focusable);
        Assert.True(first.IsTabStop);
        Assert.Contains("workspace-card-focusable", first.Classes);
        Assert.Equal("cat blue_eyes", AutomationProperties.GetName(first));
        Assert.Equal(new[] { 1, 2 }, watchers.Children.Select(AutomationProperties.GetPositionInSet));
        Assert.Equal(AutomationControlType.List, AutomationProperties.GetControlTypeOverride(watchers));

        // Down is handled on the card itself; a button inside it keeps its own keys.
        KeyEventArgs down = Press(Avalonia.Input.Key.Down, first);
        Raise(first, down);
        Assert.True(down.Handled);
        Button check = first.GetLogicalDescendants().OfType<Button>().First();
        Assert.Equal(BooruHitKey.None, BooruHitKeys.Route(Press(Avalonia.Input.Key.Enter, check), first));

        TabControl tabs = Field<TabControl>(view, "_tabs");
        view.ShowWatchers();
        int requests = harness.ProviderRequests;
        KeyEventArgs enter = Press(Avalonia.Input.Key.Enter, first);
        Raise(first, enter);
        Assert.True(enter.Handled);
        Assert.NotEqual(BooruSearchTabs.PrimaryId, view.SearchTabs.ActiveId);
        Assert.Equal(view.SearchTabs.ActiveId, Assert.IsType<TabItem>(tabs.SelectedItem).Tag);
        Assert.Equal("cat blue_eyes", Field<SearchBox>(view, "_input").Text);
        Assert.Equal(requests, harness.ProviderRequests);
        Assert.Equal(0, harness.Watchers.Checks);

        KeyEventArgs apps = Press(Avalonia.Input.Key.Apps, first);
        Raise(first, apps);
        Assert.True(apps.Handled);
    });

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private static BooruUiHarness Harness(out BooruWorkbenchModel model, out BooruWorkbenchView view)
    {
        BooruUiHarness harness = new();
        harness.Previews.OnPreview = _ => null;
        harness.Serve(pages: 1, perPage: 3);
        model = new BooruWorkbenchModel(harness.Services);
        view = new BooruWorkbenchView(model, ownsModel: false);
        return harness;
    }

    private static async Task<List<Border>> SearchAsync(BooruUiHarness harness, BooruWorkbenchModel model, BooruWorkbenchView view)
    {
        await BooruTestSupport.SearchAsync(model.Search, "cat");
        await BooruTestSupport.WaitAsync(() => Cards(view).Count == 3, "the hit cards");
        // The local state arrives after the page and the view renders on its own posted callback: wait for the saved hit's button.
        if (harness.Queue.States.ContainsKey("101"))
            await BooruTestSupport.WaitAsync(() => Cards(view)[0].GetLogicalDescendants().OfType<Button>()
                .Any(button => button.Name == BooruWorkbenchView.ShowInMediaPrefix + "101"), "the Show in Media button");
        return Cards(view);
    }

    private static List<Border> Cards(BooruWorkbenchView view) =>
        Field<StackPanel>(view, "_results").Children.OfType<Border>().ToList();

    private static T Field<T>(BooruWorkbenchView view, string name) =>
        (T)typeof(BooruWorkbenchView).GetField(name, Private)!.GetValue(view)!;

    private static Button Button(Border card, string name) =>
        card.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == name);

    private static ContextMenuModel TagMenu(BooruWorkbenchView view, string tag) =>
        Assert.IsType<ContextMenuBuilder>(view.ComposeTagMenu(tag)).ToModel();

    private static ContextMenuEntry[] Entries(ContextMenuModel menu) => menu.Entries.ToArray();

    // The routed double-tap fixtures share one pointer.
    private static readonly Avalonia.Input.Pointer SyntheticMouse = new(1, PointerType.Mouse, true);

    /// <summary>Raises the event on the control it comes from, so it bubbles up to the card as a real pointer or key event does (RaiseEvent sets the source to its target).</summary>
    private static void Raise(Interactive source, RoutedEventArgs e) => source.RaiseEvent(e);

    private static TappedEventArgs DoubleTap(object card, object source) => new(InputElement.DoubleTappedEvent,
        new PointerEventArgs(InputElement.PointerReleasedEvent, source, SyntheticMouse, null, default, 0,
            PointerPointProperties.None, KeyModifiers.None)) { Source = source };

    // One actual Avalonia dispatcher owns creation, awaited continuations and assertions (as in BooruLinkLifetimeTests).
    private static void OnDispatcher(Func<Task> test)
    {
        FieldInfo primary = typeof(Dispatcher).GetField("s_uiThread", BindingFlags.Static | BindingFlags.NonPublic)!;
        object? previousDispatcher = primary.GetValue(null);
        SynchronizationContext? previousContext = SynchronizationContext.Current;
        string language = Texts.Language;
        Dispatcher owner = Dispatcher.CurrentDispatcher;
        try
        {
            primary.SetValue(null, owner);
            SynchronizationContext.SetSynchronizationContext(new ActionContext(owner));
            Texts.Initialize();
            Texts.SetLanguage("en");
            Task pending = test();
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (!pending.IsCompleted)
            {
                owner.RunJobs();
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("Booru hit test dispatcher did not finish.");
                if (!pending.IsCompleted) Thread.Sleep(1);
            }
            pending.GetAwaiter().GetResult();
        }
        finally
        {
            Texts.SetLanguage(language);
            SynchronizationContext.SetSynchronizationContext(previousContext);
            primary.SetValue(null, previousDispatcher);
        }
    }

    private sealed class ActionContext(Dispatcher owner) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) => owner.Post(() =>
        {
            SynchronizationContext? previous = Current;
            SetSynchronizationContext(this);
            try { callback(state); }
            finally { SetSynchronizationContext(previous); }
        });
    }
}
