using Avalonia.Controls;
using Avalonia.VisualTree;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

/// <summary>Small runtime seams missing from the existing BP2 page-host contracts; no Window or provider is used.</summary>
[Collection("Localization")]
public sealed class BooruPageBehaviorRegressionTests
{
    public BooruPageBehaviorRegressionTests()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
    }

    [Fact]
    public async Task ShowQueryBeforeAndAfterActivationOnlyChangesTheRealDraft()
    {
        BooruUiHarness harness = new();
        using BooruPage page = new(harness.Services);
        SearchBox input = Assert.Single(page.View.Body.GetVisualDescendants()
            .OfType<SearchBox>().Where(control => control.Name == BooruWorkbenchView.InputName));

        page.ShowQuery("before_activate");
        Assert.Equal("before_activate", input.Text);
        Assert.Equal("before_activate", page.Model.Search.View.Input);
        Assert.Equal(BooruSearchStatus.Idle, page.Model.Search.View.Status);
        Assert.Null(page.Model.Search.View.ExecutedQuery);

        await page.ActivateAsync();
        Assert.Equal("before_activate", input.Text);
        page.ShowQuery("after_activate");

        Assert.Equal("after_activate", input.Text);
        Assert.Equal("after_activate", page.Model.Search.View.Input);
        Assert.Equal(BooruSearchStatus.Idle, page.Model.Search.View.Status);
        Assert.Null(page.Model.Search.View.ExecutedQuery);
        Assert.Empty(harness.Search.Searches);
        Assert.Equal(0, harness.ProviderRequests);
        // Activation may read the local watcher list once; it must not search or check a provider.
        Assert.Equal(1, harness.Watchers.Calls.Count(call => call == "list"));
    }
}
