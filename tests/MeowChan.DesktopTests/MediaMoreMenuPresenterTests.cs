using System.Reflection;
using Avalonia.Controls;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class MediaMoreMenuPresenterTests
{
    [Fact]
    public void PresenterCreatedBeforeOpeningSeesItemsAddedDuringOpening() => OwnedUiDispatcher.Run(() =>
    {
        OverflowMenuFlyout flyout = new();
        flyout.Items.Add(new MenuItem { Header = "Old item" });
        ItemsControl presenter = (ItemsControl)typeof(MenuFlyout)
            .GetMethod("CreatePresenter", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(flyout, null)!;
        MenuItem export = new() { Header = "Export all" };
        MenuItem ratings = new() { Header = "Manage saved ratings" };
        flyout.Opening += (_, _) =>
        {
            flyout.Items.Clear();
            flyout.Items.Add(export);
            flyout.Items.Add(ratings);
        };

        System.ComponentModel.CancelEventArgs opening = new();
        typeof(OverflowMenuFlyout).GetMethod("OnOpening", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(flyout, new object[] { opening });

        Assert.False(opening.Cancel);
        Assert.Equal(2, flyout.Items.Count);
        Assert.Equal(2, presenter.Items.Count);
        Assert.Same(export, presenter.Items[0]);
        Assert.Same(ratings, presenter.Items[1]);
    });
}
