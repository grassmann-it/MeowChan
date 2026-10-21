using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class LinuxTrayHostTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-valid-dbus-address")]
    public async Task MissingOrMalformedBusKeepsTheTaskbarFallback(string? address)
    {
        Assert.False(await LinuxTrayHost.IsAvailableAsync(address));
    }

    [Theory]
    [InlineData("org.kde.StatusNotifierItem-123-0/StatusNotifierItem", true)]
    [InlineData("org.kde.StatusNotifierItem-123-1", true)]
    [InlineData("org.kde.StatusNotifierItem-1234-0/StatusNotifierItem", false)]
    [InlineData("org.kde.StatusNotifierItem-12-0/StatusNotifierItem", false)]
    [InlineData(":1.123/StatusNotifierItem", false)]
    [InlineData("", false)]
    [InlineData("org.kde.StatusNotifierItem-123-foreign", false)]
    public void RegisteredItemsRequireOurExactProcess(string item, bool expected)
    {
        Assert.Equal(expected, LinuxTrayHost.HasRegisteredIcon(new[] { item }, 123));
    }
}
