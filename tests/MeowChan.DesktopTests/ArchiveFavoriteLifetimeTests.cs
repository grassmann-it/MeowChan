using MeowChan.Desktop;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Storage;

namespace MeowChan.DesktopTests;

public sealed class ArchiveFavoriteLifetimeTests
{
    [Fact]
    public async Task AvailabilityGuardsLateRefreshAfterSessionShutdownAndDisposal()
    {
        using TempDirectory temp = new();
        await using DesktopSessionHost host = new(temp.Path, Array.Empty<IImageBoard>());
        await host.StartAsync();
        string directory = Path.Combine(temp.Path, "missing-original");
        MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(directory);
        MediaAssetReference asset = new("asset", "image", "original", "image.png",
            Path.Combine(directory, "image.png"), 7, new string('a', 64),
            "https://example.invalid/image.png", "image.png", true);
        GalleryEntry entry = new(new(new("source", "board", "42"), directory, asset.AssetId),
            manifest, asset, null, null, false, true);
        int queries = 0;
        Func<bool> available = () => host.State == DesktopSessionState.Ready;
        Func<bool> marked = () => { queries++; return host.IsFavorite(entry); };

        Assert.Equal((true, false, (string?)null), ArchiveFavoriteButton.ReadState(available, marked, null));
        await host.SetFavoriteAsync(entry, true);
        Assert.Equal((true, true, (string?)null), ArchiveFavoriteButton.ReadState(available, marked, null));
        await host.SetFavoriteAsync(entry, false);
        Assert.Equal((true, false, (string?)null), ArchiveFavoriteButton.ReadState(available, marked, null));
        Assert.Equal(3, queries);

        Task stopping = host.BeginShutdown();
        Assert.Equal((false, false, (string?)null), ArchiveFavoriteButton.ReadState(available, marked, null));
        await stopping;
        await host.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => host.IsFavorite(entry));

        // Both a newly realized row's Configure and a pending toggle's finally use this read.
        Assert.Equal((false, false, (string?)null), ArchiveFavoriteButton.ReadState(available, marked, null));
        Assert.Equal(3, queries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnavailableOrUnconfiguredBindingDoesNotQueryMarkOrError(bool unconfigured)
    {
        Func<bool>? available = unconfigured ? null : () => false;
        var state = ArchiveFavoriteButton.ReadState(available,
            () => throw new ObjectDisposedException("session"),
            () => throw new ObjectDisposedException("error source"));

        Assert.Equal((false, false, (string?)null), state);
    }

    [Fact]
    public void LiveBindingKeepsErrorPresentationAndDoesNotHideCallbackFailures()
    {
        Assert.Equal((true, true, "save failed"),
            ArchiveFavoriteButton.ReadState(() => true, () => true, () => "save failed"));
        InvalidDataException failure = new("damaged favorites");
        Assert.Same(failure, Assert.Throws<InvalidDataException>(() =>
            ArchiveFavoriteButton.ReadState(() => true, () => throw failure, null)));
        Assert.Throws<ObjectDisposedException>(() =>
            ArchiveFavoriteButton.ReadState(() => true, () => throw new ObjectDisposedException("session"), null));
    }
}
