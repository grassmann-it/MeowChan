using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

[Collection("Booru exact navigation")]
public sealed class BooruSearchOverflowTests
{
    private static readonly string[] OptionalIds =
        [BooruWorkbenchView.AlternateSubmitName, BooruWorkbenchView.CreateWatcherName, BooruWorkbenchView.DownloadAllName];

    [Fact]
    public void SearchActionsStayOnOneLineAndOverflowFromTheRight() => WithPage(1920, (window, page, _) =>
    {
        Control row = Find<Control>(page, "BooruSearchToolbar");
        TextBox input = Find<TextBox>(page, BooruWorkbenchView.InputName);
        Button submit = Find<Button>(page, BooruWorkbenchView.SubmitName);
        Button overflow = Find<Button>(page, BooruWorkbenchView.SearchOverflowName);
        Button[] actions = OptionalIds.Select(id => Find<Button>(page, id)).ToArray();
        Dictionary<Button, double> fullWidths = actions.Append(submit).ToDictionary(button => button, button => button.Bounds.Width);

        Verify(1920, 3);
        Verify(760, 0);
        Verify(820, 1);
        Verify(1920, 3);

        void Verify(int width, int inlineCount)
        {
            BooruReviewTestPlatform.Resize(window, width);
            Rect toolbar = InWindow(row);
            Assert.InRange(input.Bounds.Width, 160, 512);
            Assert.Equal(OptionalIds.Take(inlineCount), actions.Where(button => button.IsEffectivelyVisible).Select(AutomationProperties.GetAutomationId));
            Assert.Equal(inlineCount < actions.Length, overflow.IsEffectivelyVisible);

            Rect prior = InWindow(input);
            foreach (Button button in new[] { submit, overflow }.Concat(actions).Where(button => button.IsEffectivelyVisible))
            {
                Rect bounds = InWindow(button);
                Assert.True(toolbar.Contains(bounds), $"{width} DIP: {AutomationProperties.GetAutomationId(button)} {bounds} outside {toolbar}");
                Assert.True(bounds.Width > 0 && prior.Right <= bounds.Left);
                Assert.InRange(Math.Abs(bounds.Center.Y - prior.Center.Y), 0, 1);
                Assert.False(button.Focusable || button.IsTabStop);
                if (fullWidths.TryGetValue(button, out double fullWidth)) Assert.Equal(fullWidth, bounds.Width, 0);
                prior = bounds;
            }

            if (!overflow.IsEffectivelyVisible) return;
            Assert.Equal(8, InWindow(overflow).Left - InWindow(submit).Right, 0);
            Assert.Equal(Texts.T("booru.tabs.moreActions"), ToolTip.GetTip(overflow));
            MenuFlyout menu = Open(window, overflow);
            Assert.Equal(OptionalIds.Skip(inlineCount).Select(id => id + "Overflow"),
                menu.Items.OfType<MenuItem>().Where(item => item.IsEffectivelyVisible).Select(AutomationProperties.GetAutomationId));
            menu.Hide();
            Settle(window);
        }

        Rect InWindow(Control control) => new(control.TranslatePoint(default, window)!.Value, control.Bounds.Size);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MenuEntriesRunTheButtonActions(bool inMenu) => WithPage(inMenu ? 640 : 1920, (window, page, harness) =>
    {
        TextBox input = Find<TextBox>(page, BooruWorkbenchView.InputName);
        Button overflow = Find<Button>(page, BooruWorkbenchView.SearchOverflowName);
        if (inMenu)
        {
            MenuFlyout menu = Open(window, overflow);
            Assert.All(menu.Items.OfType<MenuItem>(), item => Assert.False(item.IsEffectivelyEnabled));
            menu.Hide();
            Settle(window);
            Assert.Same(input, window.FocusManager?.GetFocusedElement());
        }

        input.Text = "cat";
        Settle(window);
        Click(Find<Button>(page, BooruWorkbenchView.SubmitName));
        int firstTab = Assert.Single(page.View.SearchTabs.Ids);
        input.Text = "dog";
        Settle(window);
        Activate(BooruWorkbenchView.AlternateSubmitName);
        Assert.Equal(2, page.View.SearchTabs.Ids.Count);
        Assert.Equal("cat", page.View.SearchTabs.ViewOf(firstTab)?.ExecutedQuery);
        Assert.Equal("dog", page.Model.Search.View.ExecutedQuery);
        Assert.Equal(new[] { ("cat", 1), ("dog", 1) }, harness.Search.Searches);

        Activate(BooruWorkbenchView.CreateWatcherName);
        Assert.Single(harness.Watchers.Calls, call => call == "create:dog");
        Activate(BooruWorkbenchView.DownloadAllName);
        Assert.Equal(new[] { ("cat", 1), ("dog", 1), ("dog", 1) }, harness.Search.Searches);
        Assert.Single(harness.Queue.Loads);
        Assert.Equal("dog", Assert.Single(harness.Queue.LoadQueries));

        void Activate(string id)
        {
            Button button = Find<Button>(page, id);
            Assert.Equal(!inMenu, button.IsEffectivelyVisible);
            if (!inMenu)
            {
                Click(button);
                return;
            }

            MenuFlyout menu = Open(window, overflow);
            MenuItem item = Assert.Single(menu.Items.OfType<MenuItem>(), entry => AutomationProperties.GetAutomationId(entry) == id + "Overflow");
            Assert.True(item.IsEffectivelyVisible && item.IsEffectivelyEnabled);
            Assert.Equal(button.Content, item.Header);
            if (id == BooruWorkbenchView.AlternateSubmitName)
                Assert.Equal(new KeyGesture(Key.Enter, KeyModifiers.Shift), item.InputGesture);
            else Assert.Null(item.InputGesture);
            PointerClick(item, window);
            Assert.False(menu.IsOpen);
            Assert.Same(input, window.FocusManager?.GetFocusedElement());
        }

        void Click(Button button)
        {
            Assert.True(button.IsEffectivelyEnabled && button.IsEffectivelyVisible);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Settle(window);
        }
    });

    [Fact]
    public void ShiftReturnStillStartsANewTabWithTheButtonHidden() => WithPage(640, (window, page, harness) =>
    {
        TextBox input = Find<TextBox>(page, BooruWorkbenchView.InputName);
        Assert.True(input.Focus());
        Assert.False(Find<Button>(page, BooruWorkbenchView.AlternateSubmitName).IsEffectivelyVisible);
        input.Text = "cat";
        SendReturn(KeyModifiers.None);
        int firstTab = Assert.Single(page.View.SearchTabs.Ids);
        input.Text = "dog";
        SendReturn(KeyModifiers.Shift);
        Assert.Equal(2, page.View.SearchTabs.Ids.Count);
        Assert.Equal("cat", page.View.SearchTabs.ViewOf(firstTab)?.ExecutedQuery);
        Assert.Equal(new[] { ("cat", 1), ("dog", 1) }, harness.Search.Searches);
        Assert.Same(input, window.FocusManager?.GetFocusedElement());

        void SendReturn(KeyModifiers modifiers)
        {
            KeyEventArgs key = new() { Key = Key.Enter, KeyModifiers = modifiers, RoutedEvent = InputElement.KeyDownEvent };
            input.RaiseEvent(key);
            Settle(window);
            Assert.True(key.Handled);
        }
    });

    [Theory]
    [InlineData(640, false)]
    [InlineData(1920, true)]
    public void WatchShortcutRunsFromTheFocusedSearchField(int width, bool inline) => WithPage(width, (window, page, harness) =>
    {
        TextBox input = Find<TextBox>(page, BooruWorkbenchView.InputName);
        Button watch = Find<Button>(page, BooruWorkbenchView.CreateWatcherName);
        input.Text = "cat";
        Settle(window);
        Assert.True(input.Focus());
        Assert.Equal(inline, watch.IsEffectivelyVisible);
        Assert.True(watch.IsEffectivelyEnabled);
        Assert.False(watch.Focusable || watch.IsTabStop);

        PressSearchAction(window, PhysicalKey.F7);

        Assert.Single(harness.Watchers.Calls, call => call == "create:cat");
        Assert.Same(input, window.FocusManager?.GetFocusedElement());
    });

    [Theory]
    [InlineData(640, false)]
    [InlineData(1920, true)]
    public void BulkShortcutStartsAndStopsTheCollectionFromTheFocusedSearchField(int width, bool inline) =>
        WithPage(width, (window, page, harness) =>
    {
        TextBox input = Find<TextBox>(page, BooruWorkbenchView.InputName);
        Button bulk = Find<Button>(page, BooruWorkbenchView.DownloadAllName);
        input.Text = "cat";
        Settle(window);
        Assert.True(input.Focus());
        window.KeyPressQwerty(PhysicalKey.Enter);
        window.KeyReleaseQwerty(PhysicalKey.Enter);
        Settle(window);
        Assert.Equal(inline, bulk.IsEffectivelyVisible);
        Assert.True(bulk.IsEffectivelyEnabled);
        Assert.False(bulk.Focusable || bulk.IsTabStop);

        TaskCompletionSource<BooruCallResult<BooruResultPage>> provider = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Search.OnSearch = (_, _, token) => provider.Task.WaitAsync(token);
        try
        {
            PressSearchAction(window, PhysicalKey.F8);
            Assert.True(page.Model.Search.Bulk.IsRunning);
            Assert.Equal(new[] { ("cat", 1), ("cat", 1) }, harness.Search.Searches);
            Assert.Same(input, window.FocusManager?.GetFocusedElement());

            PressSearchAction(window, PhysicalKey.F8);
            BooruComponentDispatcher.Wait(BooruTestSupport.WaitAsync(() => !page.Model.Search.Bulk.IsRunning, "bulk download cancellation"));
            Assert.Equal(BooruBulkEnd.Canceled, page.Model.Search.Bulk.End);
            Assert.Empty(harness.Queue.Loads);
            Assert.Same(input, window.FocusManager?.GetFocusedElement());
        }
        finally { provider.TrySetCanceled(); }
    });

    [Theory]
    [InlineData(ShortcutPlatform.Linux, KeyModifiers.Control)]
    [InlineData(ShortcutPlatform.Windows, KeyModifiers.Control)]
    [InlineData(ShortcutPlatform.MacOS, KeyModifiers.Meta)]
    public void SearchActionBindingsResolveWithoutConflicts(ShortcutPlatform platform, KeyModifiers primary)
    {
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(platform: platform);
        Assert.Empty(bindings.Issues);
        AssertAction("booru.search.watch", Key.F7);
        AssertAction("booru.search.download-all", Key.F8);

        void AssertAction(string id, Key key)
        {
            KeyModifiers modifiers = primary | KeyModifiers.Shift;
            Assert.True(bindings.Matches(new KeyEventArgs { Key = key, KeyModifiers = modifiers }, id, "booru"));
            bool mac = platform == ShortcutPlatform.MacOS;
            string display = mac ? $"⇧⌘{key} (Fn)" : $"{Texts.T("key.ctrl")}+{Texts.T("key.shift")}+{key}";
            Assert.Equal(display, CommandCatalog.ShortcutTooltipDisplay(id, mac, "booru", bindings));
        }
    }

    private static void PressSearchAction(Window window, PhysicalKey key)
    {
        RawInputModifiers primary = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
        window.KeyPressQwerty(key, primary | RawInputModifiers.Shift);
        window.KeyReleaseQwerty(key, primary | RawInputModifiers.Shift);
        Settle(window);
    }

    private static void WithPage(int width, Action<Window, BooruPage, BooruUiHarness> test) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        Texts.SetLanguage("en");
        BooruUiHarness harness = new();
        harness.Search.OnSearch = (query, number, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(
            BooruUiFixtures.Page(query, number, true, BooruUiFixtures.Post("101", tags: [], preview: false))));
        using BooruPage page = new(harness.Services);
        Window window = new() { Content = page, Width = width, Height = 640 };
        try
        {
            window.Show();
            window.Activate();
            Settle(window);
            test(window, page, harness);
        }
        finally { window.Close(); }
    });

    private static MenuFlyout Open(Window window, Button overflow)
    {
        MenuFlyout menu = Assert.IsType<MenuFlyout>(overflow.Flyout);
        PointerClick(overflow, window);
        Assert.True(menu.IsOpen);
        return menu;
    }

    private static void PointerClick(Control control, Window window)
    {
        TopLevel root = Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(control));
        Point center = control.TranslatePoint(new Rect(control.Bounds.Size).Center, root)!.Value;
        root.MouseMove(center);
        root.MouseDown(center, MouseButton.Left);
        root.MouseUp(center, MouseButton.Left);
        Settle(window);
    }

    private static T Find<T>(Control root, string id) where T : Control => root.GetVisualDescendants().OfType<T>()
        .Single(control => AutomationProperties.GetAutomationId(control) == id);

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}
