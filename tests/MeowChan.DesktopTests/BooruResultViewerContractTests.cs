using MeowChan.Desktop.Booru;
using MeowChan.Gallery;
using MeowChan.PluginSdk;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

public sealed class BooruResultViewerContractTests
{
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
