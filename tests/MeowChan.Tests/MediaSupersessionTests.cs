using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Storage;

namespace MeowChan.Tests;

// A promote root names the older binding it replaces; without that explicit declaration two roots stay ambiguous.
public sealed class MediaSupersessionTests
{
    private static readonly ResourceId Id = new("source", "board", "thread");
    private static readonly byte[] Old = [1, 3, 7];
    private static readonly byte[] Promoted = [2, 4, 8];
    private static readonly byte[] Third = [5, 6, 9];
    private static string Md5(byte[] bytes) => Convert.ToBase64String(MD5.HashData(bytes));
    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static MediaProviderMetadata Provider() => new("media", "original", "https://source.test/media", "original.bin", 999, Md5([8, 8, 8]), null);

    private static MediaAssetReference Asset(string directory, string label, byte[] bytes) => new(Sha(Encoding.UTF8.GetBytes(label)),
        "media", "original", "media.bin", Path.Combine(directory, label + ".bin"), bytes.Length, Sha(bytes),
        "https://source.test/media", "original.bin", true) { Md5 = Md5(bytes) };

    private static MediaAttachmentBinding Binding(string assetId, MediaProviderMetadata provider, byte[] bytes)
    {
        MediaActualIntegrity actual = new(bytes.Length, Md5(bytes), Sha(bytes));
        var assessment = ProviderIntegrityAssessment.Assess(provider, actual);
        return new(new(Id, "post", 0, MediaRevisionKeys.CreateAttachmentKey(Id, "post", 0, provider)),
            MediaRevisionKeys.CreateAcquisitionKey(Id, provider), assetId, provider, actual,
            new(assessment.IsStrictMatch ? MediaAcceptanceKind.Strict : MediaAcceptanceKind.ExplicitProviderMismatch,
                assessment.SizeMismatch, assessment.Md5Mismatch, DateTimeOffset.UnixEpoch, "profile", "owner", Id.Source, 1));
    }

    private static CapturedAttachment Attachment(MediaProviderMetadata provider, string key) => new(0, provider.MediaId, provider.Variant,
        provider.Url, provider.OriginalFileName, ExpectedSize: provider.ExpectedSize, ExpectedMd5: provider.ExpectedMd5)
    { ExpectedSha256 = provider.ExpectedSha256, AttachmentRevisionKey = key };

    private static MediaAssetSnapshot Root(string directory, string label, byte[] bytes, MediaProviderMetadata provider,
        params MediaSupersession[] supersedes)
    {
        MediaAssetReference asset = Asset(directory, label, bytes);
        MediaAttachmentBinding binding = Binding(asset.AssetId, provider, bytes);
        return new(Path.Combine(directory, label), MediaManifestStatus.Available, 6, label, [asset],
            attachmentBindings: new Dictionary<string, MediaAttachmentBinding> { [binding.Reference.RevisionKey] = binding },
            supersedes: supersedes);
    }

    [Fact]
    public void TwoRootsForOneKeyAreAmbiguousUntilThePromoteRootNamesTheReplacedBytes()
    {
        using TempDirectory temp = new();
        MediaProviderMetadata provider = Provider();
        string key = MediaRevisionKeys.CreateAttachmentKey(Id, "post", 0, provider);
        CapturedAttachment attachment = Attachment(provider, key);
        MediaAssetSnapshot older = Root(temp.Path, "old", Old, provider);

        Assert.Null(MediaAttachmentResolver.Resolve([older, Root(temp.Path, "plain", Promoted, provider)], Id, "post", attachment));

        MediaAssetSnapshot promote = Root(temp.Path, "promote", Promoted, provider, new MediaSupersession(key, Sha(Old)));
        foreach (MediaAssetSnapshot[] order in new[] { new[] { older, promote }, new[] { promote, older } })
        {
            ResolvedMediaAttachment resolved = Assert.IsType<ResolvedMediaAttachment>(MediaAttachmentResolver.Resolve(order, Id, "post", attachment));
            Assert.Equal(Sha(Promoted), resolved.Asset.Sha256);
            Assert.Same(promote, resolved.Snapshot);
        }
        // The older root alone is unchanged: nothing was rewritten.
        Assert.Equal(Sha(Old), MediaAttachmentResolver.Resolve(older, Id, "post", attachment)!.Asset.Sha256);
    }

    [Fact]
    public void ASupersessionOfOtherBytesOrAnotherKeyDoesNotHideTheOlderBinding()
    {
        using TempDirectory temp = new();
        MediaProviderMetadata provider = Provider();
        string key = MediaRevisionKeys.CreateAttachmentKey(Id, "post", 0, provider);
        CapturedAttachment attachment = Attachment(provider, key);
        MediaAssetSnapshot older = Root(temp.Path, "old", Old, provider);
        Assert.Null(MediaAttachmentResolver.Resolve([older, Root(temp.Path, "p1", Promoted, provider, new MediaSupersession(key, Sha(Third)))], Id, "post", attachment));
        Assert.Null(MediaAttachmentResolver.Resolve([older, Root(temp.Path, "p2", Promoted, provider, new MediaSupersession(new string('f', 64), Sha(Old)))], Id, "post", attachment));
    }

    [Fact]
    public void ChainedPromotesResolveToTheNewestRootAndRepeatedPromoteOfTheSameBytesIsStable()
    {
        using TempDirectory temp = new();
        MediaProviderMetadata provider = Provider();
        string key = MediaRevisionKeys.CreateAttachmentKey(Id, "post", 0, provider);
        CapturedAttachment attachment = Attachment(provider, key);
        MediaAssetSnapshot a = Root(temp.Path, "a", Old, provider);
        MediaAssetSnapshot b = Root(temp.Path, "b", Promoted, provider, new MediaSupersession(key, Sha(Old)));
        MediaAssetSnapshot c = Root(temp.Path, "c", Third, provider, new MediaSupersession(key, Sha(Promoted)));
        Assert.Equal(Sha(Third), MediaAttachmentResolver.Resolve([a, b, c], Id, "post", attachment)!.Asset.Sha256);
        Assert.Equal(Sha(Third), MediaAttachmentResolver.Resolve([c, a, b, c], Id, "post", attachment)!.Asset.Sha256);
    }

    private static async Task WriteManifest(string directory, int version, string? supersedesJson, byte[] bytes, string key, string assetId,
        MediaAttachmentBinding binding)
    {
        await File.WriteAllBytesAsync(Path.Combine(directory, "saved.bin"), bytes);
        object entry = new { MediaIdentifier = "media", Variant = "original", SourceUrl = binding.Provider.Url, FileName = "saved.bin",
            Length = bytes.Length, Sha256 = Sha(bytes), Md5 = Md5(bytes), OriginalFileName = binding.Provider.OriginalFileName };
        Dictionary<string, object?> manifest = new()
        {
            ["Version"] = version,
            ["Entries"] = new Dictionary<string, object> { ["media.bin"] = entry },
            ["Assets"] = new Dictionary<string, object> { [assetId] = new { RequestedFileName = "media.bin", Entry = entry } },
            ["AttachmentBindings"] = new Dictionary<string, MediaAttachmentBinding> { [key] = binding }
        };
        if (version == 6) { manifest["OriginalEvidence"] = Array.Empty<object>(); manifest["DerivedRepresentations"] = Array.Empty<object>(); }
        string json = JsonSerializer.Serialize(manifest);
        if (supersedesJson != null) json = json[..^1] + ",\"Supersedes\":" + supersedesJson + "}";
        await File.WriteAllTextAsync(Path.Combine(directory, ".meowchan-manifest.json"), json);
    }

    [Theory]
    [InlineData(6, "ok", MediaManifestStatus.Available)]
    [InlineData(3, "ok", MediaManifestStatus.Corrupt)]            // only representation manifests may supersede
    [InlineData(6, "sameBytes", MediaManifestStatus.Corrupt)]     // cannot replace its own bytes
    [InlineData(6, "unboundKey", MediaManifestStatus.Corrupt)]    // must name a key this manifest binds
    [InlineData(6, "badDigest", MediaManifestStatus.Corrupt)]
    [InlineData(6, "extraField", MediaManifestStatus.Corrupt)]    // exact record shape
    [InlineData(6, "duplicate", MediaManifestStatus.Corrupt)]
    public async Task ReaderAcceptsOnlyWellFormedSupersessionOnRepresentationManifests(int version, string shape, MediaManifestStatus expected)
    {
        using TempDirectory temp = new();
        MediaProviderMetadata provider = Provider();
        MediaAssetReference asset = Asset(temp.Path, "x", Promoted);
        string assetId = Sha(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { "media.bin", "media", "saved.bin", Sha(Promoted) })));
        MediaAttachmentBinding binding = Binding(assetId, provider, Promoted);
        string key = binding.Reference.RevisionKey;
        string json = shape switch
        {
            "ok" => $"[{{\"RevisionKey\":\"{key}\",\"SupersededSha256\":\"{Sha(Old)}\"}}]",
            "sameBytes" => $"[{{\"RevisionKey\":\"{key}\",\"SupersededSha256\":\"{Sha(Promoted)}\"}}]",
            "unboundKey" => $"[{{\"RevisionKey\":\"{new string('f', 64)}\",\"SupersededSha256\":\"{Sha(Old)}\"}}]",
            "badDigest" => $"[{{\"RevisionKey\":\"{key}\",\"SupersededSha256\":\"nothex\"}}]",
            "extraField" => $"[{{\"RevisionKey\":\"{key}\",\"SupersededSha256\":\"{Sha(Old)}\",\"Note\":\"x\"}}]",
            _ => $"[{{\"RevisionKey\":\"{key}\",\"SupersededSha256\":\"{Sha(Old)}\"}},{{\"RevisionKey\":\"{key}\",\"SupersededSha256\":\"{Sha(Third)}\"}}]"
        };
        await WriteManifest(temp.Path, version, json, Promoted, key, assetId, binding);
        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(temp.Path);
        Assert.Equal(expected, snapshot.Status);
        if (expected == MediaManifestStatus.Available)
            Assert.Equal(new MediaSupersession(key, Sha(Old)), Assert.Single(snapshot.Supersedes));
        _ = asset;
    }
}
