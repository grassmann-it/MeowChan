using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using MeowChan.Desktop.Booru;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

[Collection("Avalonia global state")]
public sealed class BooruSearchTabsViewToggleTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(500)]
    [InlineData(719.5)]
    public void NarrowWidthShowsTheIconsOnFirstRender(double width) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        BooruUiHarness harness = new();
        using BooruWorkbenchModel model = new(harness.Services);
        using BooruWorkbenchView view = new(model, ownsModel: false);

        view.RenderViewToggleLabels(width);

        foreach (ToggleButton button in new[] { view.ViewToggle.First, view.ViewToggle.Second, view.ViewToggle.Third })
        {
            Assert.NotNull(button.Content);
            Assert.IsNotType<StackPanel>(button.Content);
        }
    });

    [Fact]
    public void WideWidthAddsTheWordsAndNarrowingRestoresTheIcons() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        BooruUiHarness harness = new();
        using BooruWorkbenchModel model = new(harness.Services);
        using BooruWorkbenchView view = new(model, ownsModel: false);

        view.RenderViewToggleLabels(900);
        Assert.All(new[] { view.ViewToggle.First, view.ViewToggle.Second, view.ViewToggle.Third },
            button => Assert.IsType<StackPanel>(button.Content));
        view.RenderViewToggleLabels(500);
        Assert.All(new[] { view.ViewToggle.First, view.ViewToggle.Second, view.ViewToggle.Third },
            button => Assert.IsNotType<StackPanel>(Assert.IsAssignableFrom<Control>(button.Content)));
    });
}
