using System.Text.Json;
using Avalonia;
using Avalonia.Input;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Commands;
using MeowChan.Gallery;
using MeowChan.PluginSdk;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

public sealed class BooruResultViewerContractTests
{
    [Fact]
    public void TheMacGalleryFullscreenBindingKeepsPlainFAndMissionControlFree()
    {
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(null, ShortcutPlatform.MacOS);
        Assert.True(bindings.Matches(new KeyEventArgs { Key = Key.F11 }, "window.fullscreen", "gallery"));
        Assert.True(bindings.Matches(new KeyEventArgs { Key = Key.F, KeyModifiers = KeyModifiers.Control | KeyModifiers.Meta },
            "window.fullscreen", "gallery"));
        Assert.False(bindings.Matches(new KeyEventArgs { Key = Key.F }, "window.fullscreen", "gallery"));
        Assert.True(bindings.Matches(new KeyEventArgs { Key = Key.F }, "favorites.toggle", "gallery"));
        var missionControl = new KeyEventArgs { Key = Key.Up, KeyModifiers = KeyModifiers.Control };
        Assert.False(bindings.Matches(missionControl, "gallery.previous", "gallery"));
    }

    [Fact]
    public void PopoutPlacementRoundTripsThroughTheProfilePreferences()
    {
        Config config = new();
        var bounds = (new Size(864.5, 672), new PixelPoint(-128, 64));
        config.WriteBooruViewerBounds(bounds);
        string json = JsonSerializer.Serialize(config.Clone(), ConfigJsonContext.Default.Config);
        Config loaded = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.Config)!;
        Assert.Equal(bounds, loaded.ReadBooruViewerBounds());
    }

    [Fact]
    public void SyntheticPreviewEntryRetainsThePostProviderIdentity()
    {
        BooruPostView post = BooruPostView.From(new BooruPost(
            new BooruPostId("fixture-provider", "42"),
            BooruFieldPresence.Media,
            Media: [new MeowChan.PluginSdk.BooruMediaVariant(BooruMediaRole.Preview,
                new Uri("https://cdn.example.test/preview/42.jpg"), "jpg", Width: 96, Height: 96)]));
        BooruResultRowView row = new(post, Preview: BooruPreviewState.Ready, PreviewBytes: BooruUiFixtures.Png(42));

        GalleryEntry entry = BooruResultViewerWindow.CreatePreviewEntry(row);

        Assert.Equal("fixture-provider", entry.Key.Thread.Source);
        Assert.Equal("preview", entry.Key.Thread.Board);
        Assert.Equal("42", entry.Key.Thread.Thread);
        Assert.Equal("booru-preview", entry.Key.Directory);
        Assert.Equal("42", entry.Asset.AssetId);
    }
}
