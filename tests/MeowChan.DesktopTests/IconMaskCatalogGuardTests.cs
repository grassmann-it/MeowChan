using Avalonia.Platform;
using MeowChan.Desktop.Commands;
using SkiaSharp;

namespace MeowChan.DesktopTests;

[Collection("Avalonia global state")]
public sealed class IconMaskCatalogGuardTests
{
    [Fact]
    public void CatalogAndStateIconsResolveToPackagedAlphaMasks() => BooruComponentDispatcher.Run(() =>
    {
        var keys = CommandCatalog.All.Select(command => (Id: command.Id, Icon: command.Icon))
            .Concat(CommandCatalog.StateIcons.SelectMany(command => command.Value.Keys.Select(state =>
                (Id: command.Key + "/" + state, Icon: CommandCatalog.IconForState(command.Key, state)))))
            .ToLookup(entry => entry.Icon, entry => entry.Id);
        Assert.NotEmpty(keys);
        foreach (var uses in keys)
            AssertMask(uses.Key, string.Join(", ", uses));
    });

    [Theory]
    [InlineData("chevron-down")]
    [InlineData("chevron-up")]
    [InlineData("chevron-left")]
    [InlineData("chevron-right")]
    [InlineData("shuffle")]
    [InlineData("trending-up")]
    [InlineData("trending-down")]
    [InlineData("maximize-2")]
    [InlineData("hard-drive")]
    [InlineData("star-half")]
    public void StandaloneMenuIconsResolveToPackagedAlphaMasks(string icon) =>
        BooruComponentDispatcher.Run(() => AssertMask(icon, "menu"));

    private static void AssertMask(string icon, string usedBy)
    {
        Uri uri = new(CommandBindings.IconUri(icon));
        Assert.True(AssetLoader.Exists(uri), usedBy + ": missing mask " + icon);
        using Stream stream = AssetLoader.Open(uri);
        using SKBitmap bitmap = SKBitmap.Decode(stream);
        Assert.Equal((48, 48), (bitmap.Width, bitmap.Height));
        SKColor[] pixels = bitmap.Pixels;
        Assert.Contains(pixels, pixel => pixel.Alpha == 0);
        Assert.Contains(pixels, pixel => pixel.Alpha > 0);
    }
}
