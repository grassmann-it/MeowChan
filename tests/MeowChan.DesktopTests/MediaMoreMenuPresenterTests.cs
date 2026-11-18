using System.Reflection;
using Avalonia.Controls;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class MediaMoreMenuPresenterTests
{
    [Fact]
    public void PresenterCreatedBeforeOpeningSeesItemsAddedDuringOpening()
    {
        OverflowMenuFlyout flyout = new();
        ItemsControl presenter = (ItemsControl)typeof(MenuFlyout)
            .GetMethod("CreatePresenter", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(flyout, null)!;

        flyout.Items.Clear();
        flyout.Items.Add(new MenuItem { Header = "Export all" });
        flyout.Items.Add(new MenuItem { Header = "Manage saved ratings" });

        Assert.Equal(2, flyout.Items.Count);
        Assert.Equal(2, presenter.Items.Count);
    }
}
