using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

[Collection("Booru exact navigation")]
public sealed class BooruSearchDestinationTests
{
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
