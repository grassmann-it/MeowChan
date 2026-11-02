using System.Collections.Immutable;
using MeowChan.Favorites;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

public sealed class NativePrivateMetadataPreflightTests
{
    [Fact]
    public void AggregateBytePreflightStopsBeforeVisitingInvalidTailOrBuildingFullOutput()
    {
        ResourceId thread = new("synthetic", "b", "1");
        string prefix = new('x', 250);
        string hash = new('a', 64);
        var media = ImmutableArray.CreateBuilder<FavoriteAssetKey>(50_000);
        // Short control passes, while a large individually-valid prefix exceeds
        // the exact encoded aggregate budget well before a deliberately invalid
        // tail. No timing, memory-allocation threshold or product test hook.
        media.Add(new(thread, "short", "original", hash));
        var small = new NativePrivateMetadataProjection([thread], new(MediaFavorites: true), [], media.ToImmutable(), []);
        Assert.True(NativePrivateMetadataCodec.Inspect(NativePrivateMetadataCodec.Serialize(small)).IsValid);
        media.Clear();
        for (int i = 0; i < 49_999; i++) media.Add(new(thread, prefix + i, "original", hash));
        media.Add(null!);
        var oversized = new NativePrivateMetadataProjection([thread], new(MediaFavorites: true), [], media.ToImmutable(), []);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => NativePrivateMetadataCodec.Validate(oversized));
        Assert.Equal("The private metadata projection exceeds its size limit.", error.Message);
        Assert.Null(error.InnerException);
    }
}
