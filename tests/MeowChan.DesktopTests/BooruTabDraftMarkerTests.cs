using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using MeowChan.Desktop.Booru;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class BooruTabDraftMarkerTests
{
    [Fact]
    public void TabKeepsTheExecutedTitleWhileAnUnsentDraftIsEditedOrDuplicated() => BooruComponentDispatcher.Run(() =>
    {
        BooruUiHarness harness = new();
        using BooruWorkbenchView view = new(harness.Services);
        view.Model.Search.SetInput("cat");
        view.Model.Search.SubmitAsync().GetAwaiter().GetResult();
        Dispatcher.CurrentDispatcher.RunJobs();
        int id = view.SearchTabs.ActiveId!.Value;
        TabControl tabs = view.GetLogicalDescendants().OfType<TabControl>().Single();
        TabItem original = tabs.Items.OfType<TabItem>().Single(item => Equals(item.Tag, id));
        ContentControl marker = Marker(original);
        Assert.False(marker.IsVisible);

        view.Model.Search.SetInput("CAT");
        Dispatcher.CurrentDispatcher.RunJobs();
        Assert.False(marker.IsVisible);

        view.Model.Search.SetInput("dog");
        Dispatcher.CurrentDispatcher.RunJobs();
        Assert.True(marker.IsVisible);
        Assert.Equal("cat", AutomationProperties.GetName(original));
        string help = AutomationProperties.GetHelpText(original)!;
        Assert.Contains("cat", help);
        Assert.Contains("dog", help);

        int duplicateId = view.SearchTabs.Add(copyCurrent: true);
        Dispatcher.CurrentDispatcher.RunJobs();
        TabItem duplicate = tabs.Items.OfType<TabItem>().Single(item => Equals(item.Tag, duplicateId));
        Assert.True(Marker(duplicate).IsVisible);
        Assert.True(view.SearchTabs.Select(id));
        view.Model.Search.SetInput("cat");
        Dispatcher.CurrentDispatcher.RunJobs();
        Assert.False(marker.IsVisible);
        Assert.True(Marker(duplicate).IsVisible);
        Assert.Equal(1, harness.Search.Requests);
    });

    private static ContentControl Marker(TabItem item) =>
        Assert.Single(((StackPanel)item.Header!).Children.OfType<ContentControl>(), control => control.Name == "BooruTabPendingDraft");
}
