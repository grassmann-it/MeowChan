using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
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
            WrapPanel actions = Assert.IsType<WrapPanel>(button.GetVisualParent());
            Border browser = page.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "BooruPageBody");
            foreach (string language in new[] { "en", "de", "pl", "ja" })
            {
                Texts.SetLanguage(language);
                CheckAtWidth(1360, stacked: false);
                // Fit both actions plus the page insets, with too little room for the title beside them.
                double pageInsets = page.Bounds.Width - browser.Bounds.Width;
                CheckAtWidth(Math.Ceiling(actions.Bounds.Width + pageInsets) + 4, stacked: true);
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
                Assert.True(buttonBox.Right <= helpBox.Left);
                Assert.Equal(browserBox.Right, helpBox.Right, 0);
                Assert.Equal(4, browserBox.Top - actionsBox.Bottom, 0);
                if (stacked)
                    Assert.True(actionsBox.Top >= titleBox.Bottom + 7.5);
                else
                {
                    Assert.Equal(titleBox.Bottom, actionsBox.Bottom, 0);
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
    public void PreferenceDefaultsForOldProfilesAndSurvivesCopyAndTransfer()
    {
        Config oldProfile = JsonSerializer.Deserialize("{}", ConfigJsonContext.Default.Config)!;
        Assert.Equal(Config.BooruSearchTargetNewTab, oldProfile.BooruNewSearchTarget);
        Config source = new() { BooruNewSearchTarget = Config.BooruSearchTargetCurrentTab };
        source.Validate();

        Config copy = new();
        copy.CopyFrom(source);
        Assert.Equal(Config.BooruSearchTargetCurrentTab, copy.BooruNewSearchTarget);
        Assert.Equal(Config.BooruSearchTargetCurrentTab, source.Clone().BooruNewSearchTarget);
        Config exported = SettingsTransfer.Export(source).Deserialize(ConfigJsonContext.Default.Config)!;
        copy.CopySettingsFieldsFrom(exported);
        Assert.Equal(Config.BooruSearchTargetCurrentTab, copy.BooruNewSearchTarget);
        source.BooruNewSearchTarget = "Unknown";
        Assert.Equal(nameof(Config.BooruNewSearchTarget), Assert.Throws<ArgumentOutOfRangeException>(source.Validate).ParamName);
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
    [InlineData(Config.BooruSearchTargetNewTab, false, true)]
    [InlineData(Config.BooruSearchTargetNewTab, true, false)]
    [InlineData(Config.BooruSearchTargetCurrentTab, false, false)]
    [InlineData(Config.BooruSearchTargetCurrentTab, true, true)]
    public void EnterAndShiftEnterUseOppositeDestinations(string target, bool shift, bool opensNewTab) =>
        BooruComponentDispatcher.Run(() =>
        {
            BooruUiHarness harness = new();
            harness.Serve(pages: 1, perPage: 1);
            using BooruPage page = new(harness.Services);
            page.View.NewSearchTarget = () => target;
            page.ShowQuery("cat");
            page.Model.Search.SubmitAsync().GetAwaiter().GetResult();
            Dispatcher.UIThread.RunJobs();
            BooruSearchView original = page.Model.Search.View;

            TextBox input = SearchControl<TextBox>(page.View, BooruWorkbenchView.InputName);
            input.Text = "dog";
            KeyEventArgs key = Enter(shift ? KeyModifiers.Shift : KeyModifiers.None);
            input.RaiseEvent(key);
            Dispatcher.UIThread.RunJobs();

            Assert.True(key.Handled);
            Assert.Equal(opensNewTab ? 1 : 0, page.View.SearchTabs.ExtraCount);
            Assert.Equal("dog", page.Model.Search.View.ExecutedQuery);
            if (opensNewTab)
            {
                BooruSearchView previous = page.View.SearchTabs.ViewOf(BooruSearchTabs.PrimaryId)!;
                Assert.Equal("cat", previous.ExecutedQuery);
                Assert.Equal(original.Rows.Select(row => row.Post.PostId), previous.Rows.Select(row => row.Post.PostId));
                Assert.Equal(page.View.SearchTabs.ActiveId, ((TabItem)((TabControl)page.View.Body).SelectedItem!).Tag);
            }
            Assert.Equal(new[] { ("cat", 1), ("dog", 1) }, harness.Search.Searches);
        });

    [Fact]
    public void CopyAndHandoffPreserveResultsAtCapacityAndWatchersCanReopenIndependently() =>
        BooruComponentDispatcher.Run(() =>
        {
            BooruUiHarness harness = new();
            harness.Serve(pages: 1, perPage: 1);
            using BooruPage page = new(harness.Services);
            BooruWorkbenchView view = page.View;
            view.SearchTabsLimit = () => 1;
            page.ShowQuery("cat");
            page.Model.Search.SubmitAsync().GetAwaiter().GetResult();
            Dispatcher.UIThread.RunJobs();

            Button copy = SearchControl<Button>(view, BooruWorkbenchView.OpenInNewTabName);
            Click(copy);
            int activeId = view.SearchTabs.ActiveId;
            Assert.NotEqual(BooruSearchTabs.PrimaryId, activeId);
            Assert.Equal("cat", page.Model.Search.View.ExecutedQuery);
            Assert.Single(page.Model.Search.View.Rows);
            Assert.Single(harness.Search.Searches);

            Button openWatchers = view.Header.GetVisualDescendants().OfType<Button>().Single(button => button.Name == BooruWorkbenchView.OpenWatchersName);
            TabControl tabs = (TabControl)view.Body;
            Click(openWatchers);
            TabItem watchers = Assert.IsType<TabItem>(tabs.SelectedItem);
            Assert.Equal(BooruWorkbenchView.WatchersTabName, watchers.Name);
            Assert.False(view.TryShowQueryInNewTab("dog"));
            Assert.Same(watchers, tabs.SelectedItem);
            TextBlock notice = view.Header.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == BooruWorkbenchView.TabLimitNoticeName);
            Assert.Equal(Texts.T("booru.tabs.limit", 1), notice.Text);
            Assert.Equal(activeId, view.SearchTabs.ActiveId);

            Button close = ((Control)watchers.Header!).GetVisualDescendants().OfType<Button>().Single(button => button.Name == BooruWorkbenchView.CloseWatchersName);
            Click(close);
            Assert.DoesNotContain(watchers, tabs.Items);
            Assert.Equal(activeId, ((TabItem)tabs.SelectedItem!).Tag);
            Click(openWatchers);
            Assert.Same(watchers, tabs.SelectedItem);
            Assert.Equal(1, view.SearchTabs.ExtraCount);

            Assert.True(view.SearchTabs.Close(activeId));
            view.NewSearchTarget = () => Config.BooruSearchTargetCurrentTab;
            Assert.True(view.TryShowQueryInNewTab("dog"));
            Assert.Equal("dog", page.Model.Search.View.Input);
            Assert.NotSame(watchers, tabs.SelectedItem);
            Assert.Null(page.Model.Search.View.ExecutedQuery);
            Assert.Equal("cat", view.SearchTabs.ViewOf(BooruSearchTabs.PrimaryId)?.ExecutedQuery);
            Assert.Single(harness.Search.Searches);
        });

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
            view.SearchTabsLimit = () => 1;
            Assert.True(view.TryShowQueryInNewTab("cat"));
            int activeId = view.SearchTabs.ActiveId;
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
