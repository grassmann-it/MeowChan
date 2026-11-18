using Avalonia.Controls;
using MeowChan.Desktop.Controls;

namespace MeowChan.DesktopTests;

/// <summary>
/// Populate / Build on real popups. The Archive selection menu crashed with "Items collection must be empty before using ItemsSource"
/// (static XAML items under the ItemsSource) and then with "Operation is not valid while ItemsSource is in use" (rows added with
/// Items.Add after Populate, also Items.Clear on the second opening). ItemCollection verifies the UI thread, so the bodies run owned.
/// </summary>
[CollectionDefinition("Context menu populate", DisableParallelization = true)]
public sealed class ContextMenuPopulateCollection;

[Collection("Context menu populate")]
public sealed class ContextMenuPopulateTests
{
    private static ContextMenuBuilder Make() => new ContextMenuBuilder().Named("Test").Item("a", "A", () => { }).Section().Item("b", "B", () => { });

    private static string[] Headers(ContextMenu menu) => menu.Items.OfType<MenuItem>().Select(item => (string)item.Header!).ToArray();

    [Fact]
    public void PopulateOverStaticItemsOfThePopupDoesNotThrow() => OwnedUiDispatcher.Run(() =>
    {
        ContextMenu menu = new();
        menu.Items.Add(new MenuItem { Header = "Static" });
        menu.Items.Add(new Separator());
        Assert.True(Make().Populate(menu));
        Assert.Equal(new[] { "A", "B" }, Headers(menu));
        Assert.Equal(3, menu.ItemCount);
    });

    [Fact]
    public void PopulateAgainReplacesTheEarlierItemsAndTheRowsAppendedThroughTheBuilder() => OwnedUiDispatcher.Run(() =>
    {
        ContextMenu menu = new();
        MenuItem info = new() { Header = "Info" };
        Assert.True(Make().Append(new Separator(), info).Populate(menu));
        Assert.Equal(new[] { "A", "B", "Info" }, Headers(menu));
        Assert.True(Make().Populate(menu));
        Assert.Equal(new[] { "A", "B" }, Headers(menu));
    });

    [Fact]
    public void AnEmptyCompositionEmptiesThePopupEvenAfterAnItemsSourceWasSet() => OwnedUiDispatcher.Run(() =>
    {
        ContextMenu menu = new();
        Assert.True(Make().Populate(menu));
        Assert.False(new ContextMenuBuilder().Populate(menu));
        Assert.Equal(0, menu.ItemCount);
        // ContextMenuBuilder.Clear is the one way to empty it (menu.Items.Clear() throws while an ItemsSource is in use).
        Assert.True(Make().Populate(menu));
        ContextMenuBuilder.Clear(menu);
        Assert.Equal(0, menu.ItemCount);
        Assert.Null(menu.ItemsSource);
        Assert.True(Make().Populate(menu));
    });

    [Fact]
    public void ItemsAddedAfterPopulateOrBuildAreTheKnownFailureThatAppendReplaces() => OwnedUiDispatcher.Run(() =>
    {
        ContextMenu populated = new();
        Make().Populate(populated);
        Assert.Throws<InvalidOperationException>(() => populated.Items.Add(new MenuItem()));
        Assert.Throws<InvalidOperationException>(() => populated.Items.Clear());
        ContextMenu built = Make().Append(new MenuItem { Header = "Rich" }).Build()!;
        Assert.Equal(new[] { "A", "B", "Rich" }, Headers(built));
        Assert.Throws<InvalidOperationException>(() => built.Items.Add(new MenuItem()));
        Assert.Null(new ContextMenuBuilder().Build());
    });
}
