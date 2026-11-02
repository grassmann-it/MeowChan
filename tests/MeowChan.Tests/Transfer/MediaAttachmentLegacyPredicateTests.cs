using MeowChan.Archive;
using MeowChan.Storage;
using static MeowChan.Tests.Transfer.NativeMediaImportTests;

namespace MeowChan.Tests.Transfer;

// The prospective admission check must use the real legacy predicate, not a weaker
// media-id-only approximation. Snapshots here are metadata-only; no personal files/IO.
public sealed class MediaAttachmentLegacyPredicateTests
{
    [Theory]
    [InlineData("url", true)]
    [InlineData("different-url", false)]
    [InlineData("size", true)]
    [InlineData("different-size", false)]
    [InlineData("md5", true)]
    [InlineData("different-md5", false)]
    [InlineData("sha1", true)]
    [InlineData("different-sha1", false)]
    [InlineData("sha256", true)]
    [InlineData("different-sha256", false)]
    [InlineData("invalid-md5", false)]
    [InlineData("invalid-sha1", false)]
    [InlineData("invalid-sha256", false)]
    [InlineData("all", true)]
    [InlineData("partial-mismatch", false)]
    [InlineData("no-expectations", false)]
    [InlineData("different-media-id", false)]
    [InlineData("different-variant", false)]
    public void ProspectiveMatchAgreesWithNormalLegacyResolver(string scenario, bool expected)
    {
        byte[] bytes = [3, 5, 7, 11];
        MediaProviderMetadata provider = new("legacy-media", "original", "https://example.invalid/original", null, null, null, null);
        MediaActualIntegrity actual = new(bytes.Length, Md5(bytes), Hash(bytes)) { Sha1 = Sha1(bytes) };
        CapturedAttachment attachment = new(0, provider.MediaId, provider.Variant, null, null);
        attachment = scenario switch
        {
            "url" => attachment with { Url = provider.Url },
            "different-url" => attachment with { Url = "https://example.invalid/another" },
            "size" => attachment with { ExpectedSize = actual.Length },
            "different-size" => attachment with { ExpectedSize = actual.Length + 1 },
            "md5" => attachment with { ExpectedMd5 = actual.Md5 },
            "different-md5" => attachment with { ExpectedMd5 = Md5([13]) },
            "sha1" => attachment with { ExpectedSha1 = actual.Sha1 },
            "different-sha1" => attachment with { ExpectedSha1 = Sha1([13]) },
            "sha256" => attachment with { ExpectedSha256 = actual.Sha256.ToUpperInvariant() },
            "different-sha256" => attachment with { ExpectedSha256 = Hash([13]) },
            "invalid-md5" => attachment with { ExpectedMd5 = "invalid-base64" },
            "invalid-sha1" => attachment with { ExpectedSha1 = "invalid-base64" },
            "invalid-sha256" => attachment with { ExpectedSha256 = "invalid-sha256" },
            "all" => attachment with { Url = provider.Url, ExpectedSize = actual.Length,
                ExpectedMd5 = actual.Md5, ExpectedSha1 = actual.Sha1, ExpectedSha256 = actual.Sha256 },
            "partial-mismatch" => attachment with { Url = provider.Url, ExpectedSize = actual.Length,
                ExpectedMd5 = actual.Md5, ExpectedSha1 = Sha1([13]), ExpectedSha256 = actual.Sha256 },
            "different-media-id" => attachment with { MediaId = "another-id", Url = provider.Url },
            "different-variant" => attachment with { Variant = "preview", Url = provider.Url },
            _ => attachment
        };
        MediaAssetReference asset = new("synthetic-asset", provider.MediaId, provider.Variant,
            "synthetic.bin", "/synthetic/nonexistent/synthetic.bin", actual.Length, actual.Sha256,
            provider.Url, null, true) { Md5 = actual.Md5, Sha1 = actual.Sha1 };
        MediaAssetSnapshot snapshot = new("/synthetic/nonexistent", MediaManifestStatus.Available, 5, null, [asset]);
        bool resolved = MediaAttachmentResolver.Resolve([snapshot], Id, "15", attachment) != null;
        Assert.Equal(expected, resolved);
        Assert.Equal(resolved, MediaAttachmentResolver.WouldMatchLegacy(attachment, provider, actual));
    }

    [Fact]
    public void MissingSha1CannotSatisfyAnOldSha1Expectation()
    {
        byte[] bytes = [17, 19];
        MediaProviderMetadata provider = new("legacy-media", "original", "https://example.invalid/original", null, null, null, null);
        CapturedAttachment attachment = new(0, provider.MediaId, provider.Variant, provider.Url, null)
            { ExpectedSha1 = Sha1(bytes) };
        MediaActualIntegrity actual = new(bytes.Length, Md5(bytes), Hash(bytes));
        Assert.False(MediaAttachmentResolver.WouldMatchLegacy(attachment, provider, actual));
    }
}
