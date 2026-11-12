using MeowChan.Archive;
using MeowChan.Booru;

namespace MeowChan.Tests.Booru;

public sealed class BooruOriginalDownloadTests
{
    [Fact]
    public void SelectsOnlyTheExplicitOriginalAndPreservesIntegrityClaims()
    {
        string md5 = Convert.ToBase64String(new byte[16]);
        BooruPostClaim post = new("danbooru", "42", BooruPostClaimPresence.Media,
            Media:
            [
                new BooruPostClaimMedia(BooruPostClaimMediaRole.Preview, "https://cdn.test/42-preview.jpg"),
                new BooruPostClaimMedia(BooruPostClaimMediaRole.Original, "https://cdn.test/42.png", ".PNG", 123, 640, 480, md5),
                new BooruPostClaimMedia(BooruPostClaimMediaRole.Sample, "https://cdn.test/42-sample.jpg")
            ]);

        Assert.True(BooruOriginalDownloadSelector.TrySelect(post, out BooruOriginalDownloadPlan? plan,
            out BooruOriginalSelectionError error));
        Assert.Equal(BooruOriginalSelectionError.None, error);
        Assert.NotNull(plan);
        Assert.Equal(new Uri("https://cdn.test/42.png"), plan.DownloadUri);
        Assert.Equal(".png", plan.Extension);
        Assert.Equal(123, plan.ExpectedSize);
        Assert.Equal(md5, plan.ExpectedMd5);
        Assert.Equal("original", BooruOriginalDownloadPlan.Variant);
        Assert.Equal("booru-danbooru-42", plan.MediaIdentifier);
    }

    [Fact]
    public void DoesNotPromoteSampleOrPreviewWhenOriginalIsUnavailable()
    {
        BooruPostClaim post = new("danbooru", "42", BooruPostClaimPresence.Media,
            Media: [new BooruPostClaimMedia(BooruPostClaimMediaRole.Sample, "https://cdn.test/42.jpg")]);

        Assert.False(BooruOriginalDownloadSelector.TrySelect(post, out _, out BooruOriginalSelectionError error));
        Assert.Equal(BooruOriginalSelectionError.MissingOriginal, error);

        post = post with
        {
            Media = [new BooruPostClaimMedia(BooruPostClaimMediaRole.Original, null)]
        };
        Assert.False(BooruOriginalDownloadSelector.TrySelect(post, out _, out error));
        Assert.Equal(BooruOriginalSelectionError.OriginalUrlMissing, error);
    }

    [Fact]
    public void RejectsInvalidOriginalTransportAndIntegrityClaims()
    {
        BooruPostClaim basePost = new("danbooru", "42", BooruPostClaimPresence.Media);
        BooruPostClaimMedia Media(string url, string? extension = null, long? size = null, string? md5 = null)
            => new(BooruPostClaimMediaRole.Original, url, extension, size, ExpectedMd5: md5);

        Assert.False(BooruOriginalDownloadSelector.TrySelect(basePost with
        {
            Media = [Media("http://cdn.test/42.png")]
        }, out _, out BooruOriginalSelectionError error));
        Assert.Equal(BooruOriginalSelectionError.OriginalUrlInvalid, error);

        Assert.False(BooruOriginalDownloadSelector.TrySelect(basePost with
        {
            Media = [Media("https://cdn.test/42.png", size: -1)]
        }, out _, out error));
        Assert.Equal(BooruOriginalSelectionError.InvalidSize, error);

        Assert.False(BooruOriginalDownloadSelector.TrySelect(basePost with
        {
            Media = [Media("https://cdn.test/42.png", md5: "not-base64")]
        }, out _, out error));
        Assert.Equal(BooruOriginalSelectionError.InvalidMd5, error);

        Assert.False(BooruOriginalDownloadSelector.TrySelect(basePost with
        {
            Media = [Media("https://cdn.test/42", extension: ".bad/name")]
        }, out _, out error));
        Assert.Equal(BooruOriginalSelectionError.InvalidExtension, error);
    }

    [Fact]
    public void UsesBoundedOpaqueExtensionWhenProviderOmitsOne()
    {
        BooruPostClaim post = new("danbooru", "42", BooruPostClaimPresence.Media,
            Media: [new BooruPostClaimMedia(BooruPostClaimMediaRole.Original, "https://cdn.test/42")]);

        Assert.True(BooruOriginalDownloadSelector.TrySelect(post, out BooruOriginalDownloadPlan? plan, out _));
        Assert.Equal(".bin", plan!.Extension);
    }
}
