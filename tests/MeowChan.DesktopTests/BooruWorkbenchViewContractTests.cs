using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Booru;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class BooruWorkbenchViewContractTests
{
    [Fact]
    public void WindowAndPageExposeTheSameSearchControls() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        BooruUiHarness harness = new();
        using BooruPage page = new(harness.Services);
        using BooruWorkbenchWindow window = new(harness.Services);
        window.Show();
        try
        {
            BooruWorkbenchView view = Assert.Single(window.GetVisualDescendants().OfType<BooruWorkbenchView>());
            Assert.Same(window.Model, view.Model);
            Assert.Equal(SearchControl<TextBox>(page.View, BooruWorkbenchView.InputName).Name,
                SearchControl<TextBox>(view, BooruWorkbenchWindow.InputName).Name);
            Assert.Equal(SearchControl<Button>(page.View, BooruWorkbenchView.SubmitName).Name,
                SearchControl<Button>(view, BooruWorkbenchWindow.SubmitName).Name);

            window.ShowQuery("cat");
            window.AddTagToSearch("blue_eyes");
            Assert.Equal("cat blue_eyes", window.Model.Search.View.Input);
            Assert.Empty(harness.Search.Searches);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void RequestButtonsKeepDelegatingToTheExistingGuardedModels() => BooruComponentDispatcher.Run(() =>
    {
        BooruUiHarness harness = new();
        harness.Search.OnSearch = async (_, _, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException();
        };
        using BooruWorkbenchView view = new(harness.Services);
        Button submit = SearchControl<Button>(view, BooruWorkbenchView.SubmitName);
        Button cancel = SearchControl<Button>(view, BooruWorkbenchView.CancelName);
        Button retry = SearchControl<Button>(view, BooruWorkbenchView.RetryName);
        Assert.False(submit.IsEnabled);
        Click(submit);
        Assert.Empty(harness.Search.Searches);

        SearchControl<TextBox>(view, BooruWorkbenchView.InputName).Text = "cat";
        Dispatcher.UIThread.RunJobs();
        Assert.True(submit.IsEnabled);
        Click(submit);
        Assert.Single(harness.Search.Searches);
        Assert.False(submit.IsEnabled);
        Assert.True(cancel.IsVisible);
        Click(submit);
        Assert.Single(harness.Search.Searches);

        Click(cancel);
        Assert.True(retry.IsVisible);
        harness.Serve(pages: 1);
        Click(retry);
        BooruComponentDispatcher.Wait(BooruTestSupport.WaitAsync(
            () => view.Model.Search.View.Status == BooruSearchStatus.Results, "the explicit retry"));
        Assert.Equal(2, harness.Search.Searches.Count);
    });

    [Fact]
    public void SidebarShowsTheSelectedSourceAndLoadsItsOriginalForAnEqualPostId() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        BooruUiHarness first = new();
        BooruUiHarness second = new();
        FakeBooruSearchPort other = new() { ProviderId = "test-source", ProviderName = "Fixture provider" };
        BooruPostView original = BooruUiFixtures.Post("101");
        BooruPostView scoped = BooruPostView.From(original.Post with { Id = new(other.ProviderId, original.PostId) });
        first.Search.OnSearch = (query, page, _) =>
            Task.FromResult(BooruCallResult<BooruResultPage>.Ok(BooruUiFixtures.Page(query, page, true, original)));
        other.OnSearch = (query, page, _) =>
            Task.FromResult(BooruCallResult<BooruResultPage>.Ok(BooruUiFixtures.Page(query, page, true, scoped)));
        BooruWorkbenchServices otherSource = new(other, second.Queue, second.Watchers, second.Previews, second.Clock);
        using BooruPage page = new(first.Services, [first.Services, otherSource]);
        page.View.RestoreMediaSidebar(open: true);
        Window window = new() { Width = 1840, Height = 920, Content = page };
        window.Show();
        try
        {
            BooruMediaSidebarTests.Search(page.Model, page.View);
            BooruMediaSidebar sidebar = BooruMediaSidebarTests.Sidebar(page.View);
            string loadId = "BooruPanel" + BooruWorkbenchView.LoadPrefix + original.PostId;
            Button stale = BooruMediaSidebarTests.ById<Button>(sidebar, loadId);
            Assert.True(page.View.TryShowQueryInNewTab("cat", other));
            BooruMediaSidebarTests.Search(page.Model, page.View);
            TextBlock summary = BooruMediaSidebarTests.ById<TextBlock>(sidebar, "BooruMediaSummary");
            Assert.Contains(other.ProviderName, AutomationProperties.GetName(summary));

            Click(stale);
            Click(BooruMediaSidebarTests.ById<Button>(sidebar, loadId));
            BooruComponentDispatcher.WaitUntil(() => second.Queue.Loads.Count == 1, "the scoped original admission");
            Assert.Empty(first.Queue.Loads);
            Assert.Equal((original.PostId, BooruLoadOrigin.Search), Assert.Single(second.Queue.Loads));
        }
        finally { window.Close(); }
    });

    private static T SearchControl<T>(BooruWorkbenchView view, string name) where T : Control
    {
        var tabs = Assert.IsType<TabControl>(view.Body);
        var tab = Assert.IsType<TabItem>(tabs.SelectedItem);
        var content = Assert.IsAssignableFrom<Control>(tab.Content);
        return Assert.Single(content.GetLogicalDescendants().OfType<T>(), control => AutomationProperties.GetAutomationId(control) == name);
    }

    private static void Click(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }
}
