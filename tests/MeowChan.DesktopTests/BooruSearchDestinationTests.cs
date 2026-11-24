using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

[Collection("Booru exact navigation")]
public sealed class BooruSearchDestinationTests
{
    [Fact]
    public void WatchersButtonCountsActiveLocalWatchersAndFollowsLanguageChanges() => BooruComponentDispatcher.Run(() =>
    {
        BooruUiHarness harness = new();
        DateTimeOffset now = harness.Clock.GetUtcNow();
        harness.Watchers.Seed(Enumerable.Range(1, 12)
            .Select(number => new BooruWatcherView($"watcher-{number}", $"tag_{number}", BooruWatcherStatus.Active, now)).ToArray());
        harness.Watchers.Seed(new BooruWatcherView("paused", "dog", BooruWatcherStatus.Paused, now),
            new BooruWatcherView("removed", "sky", BooruWatcherStatus.Removed, now));
        using BooruPage page = new(harness.Services);
        Button button = FindWatchersButton(page);
        Assert.Equal("eye", IconButtons.KeyOf(button));
        AssertLabel("Watchers (0)", "0 active watchers");

        BooruComponentDispatcher.Wait(page.ActivateAsync());
        Dispatcher.UIThread.RunJobs();
        AssertLabel("Watchers (12)", "12 active watchers");

        BooruComponentDispatcher.Wait(page.Model.Watchers.PauseAsync("watcher-1"));
        Dispatcher.UIThread.RunJobs();
        AssertLabel("Watchers (11)", "11 active watchers");

        Texts.SetLanguage("de");
        Dispatcher.UIThread.RunJobs();
        AssertLabel("Suchbeobachtungen (11)", "11 aktive Suchbeobachtungen");
        Assert.Equal(0, harness.ProviderRequests);

        void AssertLabel(string label, string tip)
        {
            Assert.Equal(label, button.Content);
            Assert.Equal(label, AutomationProperties.GetName(button));
            Assert.Contains(tip, Assert.IsType<string>(ToolTip.GetTip(button)));
            Assert.Equal(ToolTip.GetTip(button), AutomationProperties.GetHelpText(button));
        }
    });

    [Fact]
    public void HeaderActionsAlignAboveTheBrowserAndStackBeneathTheTitle() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        BooruUiHarness harness = new();
        using BooruPage page = new(harness.Services);
        Window window = new() { Content = page, Width = 1360, Height = 600 };
        try
        {
            window.Show();
            Button button = FindWatchersButton(page);
            HelpButton help = page.View.Header.GetVisualDescendants().OfType<HelpButton>()
                .Single(control => control.Name == BooruWorkbenchView.SearchHelpName);
            TextBlock title = page.View.Header.GetVisualDescendants().OfType<TextBlock>()
                .Single(text => text.Classes.Contains("page-title"));
            WrapPanel actions = Assert.IsType<WrapPanel>(help.GetVisualParent());
            Border browser = page.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "BooruPageBody");
            foreach (string language in new[] { "en", "de", "pl", "ja" })
            {
                Texts.SetLanguage(language);
                CheckAtWidth(1360, stacked: false);
                // Fit both action groups with too little room for the title beside them.
                double pageInsets = page.Bounds.Width - browser.Bounds.Width;
                CheckAtWidth(Math.Ceiling(actions.Bounds.Width + button.Bounds.Width + pageInsets) + 12, stacked: true);
            }

            void CheckAtWidth(double width, bool stacked)
            {
                BooruReviewTestPlatform.Resize(window, width);
                Rect buttonBox = InPage(button);
                Rect helpBox = InPage(help);
                Rect titleBox = InPage(title);
                Rect actionsBox = InPage(actions);
                Rect browserBox = InPage(browser);
                Assert.True(buttonBox.Width > 0 && helpBox.Width > 0);
                Assert.Equal(browserBox.Left, titleBox.Left, 0);
                Assert.Equal(PageHeader.PageTopOffset, titleBox.Top, 0);
                Assert.True(helpBox.Right <= buttonBox.Left);
                Assert.Equal(browserBox.Right, buttonBox.Right, 0);
                Assert.Equal(8, browserBox.Top - Math.Max(actionsBox.Bottom, buttonBox.Bottom), 0);
                if (stacked)
                    Assert.True(actionsBox.Top >= titleBox.Bottom + 7.5);
                else
                {
                    Assert.Equal(titleBox.Top, actionsBox.Top, 0);
                    Assert.True(actionsBox.Left >= titleBox.Right);
                }
            }

            Rect InPage(Control control) => new(control.TranslatePoint(default, page)!.Value, control.Bounds.Size);
        }
        finally { window.Close(); }
    });

    private static Button FindWatchersButton(BooruPage page) => page.View.Header.GetVisualDescendants().OfType<Button>()
        .Single(button => button.Name == BooruWorkbenchView.OpenWatchersName);

    [Fact]
    public void TheRetiredPreferenceCanStillBeReadFromAnOldProfile()
    {
        Config old = JsonSerializer.Deserialize("""{"BooruNewSearchTarget":"CurrentTab"}""", ConfigJsonContext.Default.Config)!;
        old.Validate();
        Assert.Equal("CurrentTab", old.BooruNewSearchTarget);
        Assert.False(SettingsTransfer.Export(old).ContainsKey(nameof(Config.BooruNewSearchTarget)));
        old.BooruNewSearchTarget = "Unknown";
        old.Validate();
    }

    [Theory]
    [InlineData(ShortcutPlatform.Windows)]
    [InlineData(ShortcutPlatform.Linux)]
    [InlineData(ShortcutPlatform.MacOS)]
    public void EnterGesturesAreEffectiveAndCollisionFree(ShortcutPlatform platform)
    {
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(platform: platform);
        string submit = BooruWorkbenchView.SubmitCommand;
        string alternate = BooruWorkbenchView.AlternateSubmitCommand;
        Assert.DoesNotContain(bindings.Issues, issue => issue.CommandId == submit || issue.CommandId == alternate);
        Assert.True(BooruWorkbenchView.MatchesSearchShortcut(Enter(), submit, bindings));
        Assert.False(BooruWorkbenchView.MatchesSearchShortcut(Enter(KeyModifiers.Shift), submit, bindings));
        Assert.True(BooruWorkbenchView.MatchesSearchShortcut(Enter(KeyModifiers.Shift), alternate, bindings));
        Assert.False(BooruWorkbenchView.MatchesSearchShortcut(Enter(KeyModifiers.Meta), alternate, bindings));
        Assert.NotNull(CommandCatalog.ShortcutTooltipDisplay(alternate, platform == ShortcutPlatform.MacOS, "booru", bindings));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FirstEnterCreatesTheTabAndTheAlternateActionOpensAnother(bool alternate, bool fromMore) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        BooruUiHarness harness = new();
        harness.Serve(pages: 1, perPage: 1);
        using BooruPage page = new(harness.Services);
        Window window = new() { Content = page, Width = 900, Height = 550 };
        try
        {
            window.Show();
            window.Activate();
            Settle(window);
            BooruWorkbenchView view = page.View;
            Assert.Empty(view.SearchTabs.Ids);
            Assert.Null(view.SearchTabs.ActiveId);
            TextBox input = SearchControl<TextBox>(view, BooruWorkbenchView.InputName);
            Assert.True(input.IsEffectivelyVisible);
            Assert.True(input.Bounds.Width > 0);
            Assert.Empty(harness.Search.Searches);

            input.Text = "cat";
            KeyEventArgs first = Enter();
            input.RaiseEvent(first);
            Settle(window);
            Assert.True(first.Handled);
            Assert.Equal(("cat", 1), Assert.Single(harness.Search.Searches));
            int original = Assert.Single(view.SearchTabs.Ids);
            TabItem originalItem = Tab((TabControl)view.Body, original);
            Assert.Equal("cat", AutomationProperties.GetName(originalItem));

            input.Text = "dog";
            Settle(window);
            Assert.Equal("cat", AutomationProperties.GetName(originalItem));
            if (fromMore)
            {
                Button more = FindVisual<Button>(view.Header, "BooruMore");
                Click(more);
                MenuItem searchInNewTab = Assert.Single(more.ContextMenu!.Items.OfType<MenuItem>(),
                    item => Equals(item.Tag, BooruWorkbenchView.AlternateSubmitCommand));
                Assert.Equal(Texts.T("booru.tabs.searchNewShort"), AutomationProperties.GetName(searchInNewTab));
                searchInNewTab.Command!.Execute(searchInNewTab.CommandParameter);
            }
            else
            {
                KeyEventArgs key = Enter(alternate ? KeyModifiers.Shift : KeyModifiers.None);
                input.RaiseEvent(key);
                Assert.True(key.Handled);
            }
            Settle(window);
            Assert.Equal(alternate ? 2 : 1, view.SearchTabs.Ids.Count);
            Assert.Equal("dog", page.Model.Search.View.ExecutedQuery);
            Assert.Equal(alternate ? "cat" : "dog", view.SearchTabs.ViewOf(original)?.ExecutedQuery);
            Assert.Equal(new[] { ("cat", 1), ("dog", 1) }, harness.Search.Searches);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void CopyAndHandoffPreserveResultsAtCapacityAndWatchersCanReopenIndependently() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        BooruUiHarness harness = new();
        harness.Serve(pages: 1, perPage: 1);
        using BooruPage page = new(harness.Services);
        Window window = new() { Content = page, Width = 900, Height = 550 };
        try
        {
            window.Show();
            window.Activate();
            Settle(window);
            BooruWorkbenchView view = page.View;
            view.SearchTabsLimit = () => 2;
            page.ShowQuery("cat");
            BooruComponentDispatcher.Wait(page.Model.Search.SubmitAsync());
            Settle(window);

            TabControl strip = (TabControl)view.Body;
            int original = Assert.Single(view.SearchTabs.Ids);
            TabItem originalItem = Tab(strip, original);
            OpenTabMenu(originalItem);
            MenuItem copy = Assert.Single(originalItem.ContextMenu!.Items.OfType<MenuItem>(),
                item => Equals(item.Tag, "booru.tabs.duplicate"));
            copy.Command!.Execute(copy.CommandParameter);
            Settle(window);
            int active = Assert.IsType<int>(view.SearchTabs.ActiveId);
            Assert.NotEqual(original, active);
            Assert.Equal("cat", page.Model.Search.View.ExecutedQuery);
            Assert.Single(page.Model.Search.View.Rows);
            Assert.Single(harness.Search.Searches);

            Button watchersButton = FindWatchersButton(page);
            Click(watchersButton);
            Settle(window);
            TabItem watchers = Assert.IsType<TabItem>(strip.SelectedItem);
            Assert.Equal(BooruWorkbenchView.WatchersTabName, watchers.Name);
            Assert.False(view.TryShowQueryInNewTab("dog"));
            Assert.Same(watchers, strip.SelectedItem);
            TextBlock notice = view.Header.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == BooruWorkbenchView.TabLimitNoticeName);
            Assert.Equal(Texts.T("booru.tabs.limit", 2), notice.Text);

            Click(Assert.Single(watchers.GetVisualDescendants().OfType<Button>(), button => button.Name == BooruWorkbenchView.CloseWatchersName));
            Settle(window);
            Assert.DoesNotContain(watchers, strip.Items);
            Assert.Same(Tab(strip, active), strip.SelectedItem);
            Click(watchersButton);
            Assert.Same(watchers, strip.SelectedItem);

            Assert.True(view.SearchTabs.Close(active));
            Assert.True(view.TryShowQueryInNewTab("dog"));
            Assert.Equal("dog", page.Model.Search.View.Input);
            Assert.NotSame(watchers, strip.SelectedItem);
            Assert.Null(page.Model.Search.View.ExecutedQuery);
            Assert.Equal("cat", view.SearchTabs.ViewOf(original)?.ExecutedQuery);
            Assert.Single(harness.Search.Searches);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ThePlusScrollsBesideTheLastSearchBeforeWatchersAndMiddleClickClosesOnlyItsTarget() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        BooruUiHarness harness = new();
        using BooruPage page = new(harness.Services);
        Window window = new() { Content = page, Width = 540, Height = 560 };
        try
        {
            window.Show();
            window.Activate();
            Settle(window);
            BooruWorkbenchView view = page.View;
            TabControl tabs = (TabControl)view.Body;
            Button plus = FindVisual<Button>(tabs, BooruWorkbenchView.NewSearchTabName);
            Point initial = Center(plus);
            window.MouseDown(initial, MouseButton.Left);
            window.MouseUp(initial, MouseButton.Left);
            Settle(window);
            Assert.True(SearchControl<TextBox>(view, BooruWorkbenchView.InputName).IsFocused);
            int first = Assert.Single(view.SearchTabs.Ids);

            view.SearchTabsLimit = () => 33;
            for (int i = 1; i < 33; i++) Assert.True(view.TryShowQueryInNewTab("draft_" + i));
            view.ShowWatchers();
            Settle(window);
            int last = view.SearchTabs.Ids[^1];
            TabItem watchers = FindVisual<TabItem>(tabs, BooruWorkbenchView.WatchersTabName);
            TabItem lastItem = Tab(tabs, last);
            ScrollViewer scroll = FindVisual<ScrollViewer>(tabs, "BooruTabStrip");
            Assert.True(scroll.Extent.Width > scroll.Viewport.Width);
            TabItem plusItem = plus.GetVisualAncestors().OfType<TabItem>().First();
            foreach (string language in new[] { "en", "de", "cs", "es", "fr", "it", "ja", "ko", "pl", "pt-BR", "ru", "zh-Hans" })
            {
                Texts.SetLanguage(language);
                Settle(window);
                Rect lastBox = Bounds(lastItem), plusBox = Bounds(plusItem), watchersBox = Bounds(watchers);
                Assert.InRange(plusBox.Left - lastBox.Right, 3, 5);
                Assert.InRange(watchersBox.Left - plusBox.Right, 3, 5);
                Assert.Equal(Texts.T("booru.ui.search"), plus.Content);
                Assert.Equal(Texts.T("booru.tabs.new"), AutomationProperties.GetName(plus));
                Assert.Equal(AutomationProperties.GetName(plus), ToolTip.GetTip(plus));
            }
            Assert.True(scroll.Offset.X > 0);
            Assert.InRange(Center(plus).X, 0, window.ClientSize.Width);

            // One crossed gesture, then a complete middle click on the same search header.
            Point target = Center(((Panel)lastItem.Header!).Children[0]);
            Point separate = Center(((Panel)watchers.Header!).Children[0]);
            window.MouseDown(target, MouseButton.Middle);
            window.MouseUp(separate, MouseButton.Middle);
            Assert.Equal(33, view.SearchTabs.Ids.Count);
            window.MouseDown(target, MouseButton.Middle);
            window.MouseUp(target, MouseButton.Middle);
            Settle(window);
            Assert.DoesNotContain(last, view.SearchTabs.Ids);
            Assert.Equal(first, view.SearchTabs.Ids[0]);
            Assert.Equal(32, view.SearchTabs.Ids.Count);
            Assert.Same(watchers, tabs.SelectedItem);

            foreach (int id in view.SearchTabs.Ids.Skip(1)) Assert.True(view.SearchTabs.Close(id));
            view.CloseWatchers();
            Settle(window);
            TabItem firstItem = Tab(tabs, first);
            Button close = Assert.Single(((Panel)firstItem.Header!).Children.OfType<Button>());
            Point closePoint = Center(close);
            window.MouseDown(closePoint, MouseButton.Left);
            window.MouseUp(closePoint, MouseButton.Left);
            Settle(window);
            Assert.Empty(view.SearchTabs.Ids);
            Assert.True(SearchControl<TextBox>(view, BooruWorkbenchView.InputName).IsEffectivelyVisible);
            Assert.Equal(0, harness.ProviderRequests);

            Rect Bounds(Control c) => new(c.TranslatePoint(default, window)!.Value, c.Bounds.Size);
            Point Center(Control c) => Bounds(c).Center;
        }
        finally { window.Close(); }
    });

    private static TabItem Tab(TabControl strip, int id) => Assert.Single(strip.Items.OfType<TabItem>(), tab => Equals(tab.Tag, id));

    private static T FindVisual<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private static void OpenTabMenu(TabItem tab) =>
        tab.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Apps });

    private static void Settle(Window window)
    {
        for (int pass = 0; pass < 3; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    [Fact]
    public void AFullTabStripDoesNotCancelTheRunningRequest()
    {
        BooruComponentDispatcher.Run(() =>
        {
            BooruUiHarness harness = new();
            TaskCompletionSource<BooruCallResult<BooruResultPage>> finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken token = default;
            harness.Search.OnSearch = (_, _, cancellation) => { token = cancellation; return finish.Task; };
            using BooruPage page = new(harness.Services);
            BooruWorkbenchView view = page.View;
            view.SearchTabsLimit = () => 2;
            Assert.True(view.TryShowQueryInNewTab("draft"));
            Assert.True(view.TryShowQueryInNewTab("cat"));
            int activeId = Assert.IsType<int>(view.SearchTabs.ActiveId);
            Task search = page.Model.Search.SubmitAsync();
            try
            {
                Assert.True(page.Model.Search.View.IsBusy);
                Assert.False(view.TryShowQueryInNewTab("dog"));
                Assert.False(token.IsCancellationRequested);
                Assert.Equal(activeId, view.SearchTabs.ActiveId);
                Assert.Equal("cat", page.Model.Search.View.ExecutedQuery);
                Assert.True(page.Model.Search.View.IsBusy);
            }
            finally
            {
                finish.SetResult(BooruCallResult<BooruResultPage>.Ok(BooruUiFixtures.Page("cat", 1, true)));
                search.GetAwaiter().GetResult();
            }
            Assert.Equal(BooruSearchStatus.Empty, page.Model.Search.View.Status);
        });
    }

    [Fact]
    public void SavedShortcutsReplaceAndYieldInTheEffectiveMacBindings()
    {
        string alternate = BooruWorkbenchView.AlternateSubmitCommand;
        ShortcutBindingsSection profile = new ShortcutBindingsSection()
            .Set("media.tile-size.reset", [ShortcutGesture.Parse("Shift+Enter")]);
        EffectiveShortcutBindings yielded = EffectiveShortcutBindings.Build(profile, ShortcutPlatform.MacOS);
        Assert.Null(CommandCatalog.ShortcutTooltipDisplay(alternate, true, "booru", yielded));
        Assert.False(BooruWorkbenchView.MatchesSearchShortcut(Enter(KeyModifiers.Shift), alternate, yielded));
        Assert.True(yielded.Matches(Enter(KeyModifiers.Shift), "media.tile-size.reset", "booru"));

        EffectiveShortcutBindings rebound = EffectiveShortcutBindings.Build(
            profile.Set(alternate, [ShortcutGesture.Parse("Primary+Enter")]), ShortcutPlatform.MacOS);
        Assert.True(BooruWorkbenchView.MatchesSearchShortcut(Enter(KeyModifiers.Meta), alternate, rebound));
        Assert.Equal("⌘↩", CommandCatalog.ShortcutTooltipDisplay(alternate, true, "booru", rebound));
    }

    private static T SearchControl<T>(BooruWorkbenchView view, string name) where T : Control =>
        ((Control)((TabItem)((TabControl)view.Body).SelectedItem!).Content!).GetVisualDescendants().OfType<T>()
            .Single(control => control.Name == name);

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static KeyEventArgs Enter(KeyModifiers modifiers = KeyModifiers.None) =>
        new() { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, KeyModifiers = modifiers };
}
