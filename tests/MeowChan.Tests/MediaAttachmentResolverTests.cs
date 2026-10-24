using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowChan.Archive;
using MeowChan.Storage;

namespace MeowChan.Tests;

public sealed class MediaAttachmentResolverTests
{
    private static readonly ResourceId Id = new("source", "board", "thread");
    private static readonly byte[] Bytes = [1, 3, 7];
    private static string Md5(byte[] bytes) => Convert.ToBase64String(MD5.HashData(bytes));
    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static MediaProviderMetadata Provider(long? size = 3, string? md5 = null) => new("media", "original",
        "https://source.test/media", "original.bin", size, md5, null);

    [Fact]
    public async Task WireBindingsPreserveMultiplePostsHistoricalAssetAndByteVerificationWithoutInflatingCount()
    {
        using TempDirectory temp = new();
        MediaProviderMetadata provider = Provider(999, Md5([8, 8, 8]));
        MediaActualIntegrity actual = new(Bytes.Length, Md5(Bytes), Sha(Bytes));
        string assetId = AssetId("media.bin", "saved.bin", actual.Sha256);
        var first = Binding("one", 0, assetId, provider, actual);
        var second = Binding("two", 1, assetId, provider, actual);
        await WriteManifest(temp.Path, provider, actual, assetId, new() { [first.Reference.RevisionKey] = first, [second.Reference.RevisionKey] = second });
        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(temp.Path);
        Assert.Equal(MediaManifestStatus.Available, snapshot.Status); Assert.Equal(1, snapshot.Count); Assert.Equal(2, snapshot.AttachmentBindings.Count);
        foreach (var binding in new[] { first, second })
        {
            var result = MediaAttachmentResolver.Resolve(snapshot, Id, binding.Reference.PostId, Attachment(provider, binding.Reference));
            Assert.NotNull(result); Assert.False(result.IsLegacy); Assert.Same(snapshot, result.Snapshot);
            Assert.Equal(assetId, result.Asset.AssetId); Assert.Equal(binding, result.Binding);
        }
        await using (Stream verified = await snapshot.OpenVerifiedReadAsync(assetId)) Assert.Equal(0, verified.Position);
        await File.WriteAllBytesAsync(Path.Combine(temp.Path, "saved.bin"), [9, 9, 9]);
        await Assert.ThrowsAsync<InvalidDataException>(() => snapshot.OpenVerifiedReadAsync(assetId));
    }

    [Fact]
    public void ExactBindingWinsOverCurrentnessAndCannotDriftToDifferentRawRevisionOrPost()
    {
        using TempDirectory temp = new();
        MediaProviderMetadata provider = Provider(999, Md5([8, 8, 8]));
        MediaActualIntegrity actual = new(3, Md5(Bytes), Sha(Bytes));
        MediaAssetReference old = Asset(temp.Path, "old", actual) with { IsCurrent = false };
        MediaAssetReference latest = Asset(temp.Path, "latest", new(3, Md5([2, 2, 2]), Sha([2, 2, 2])));
        var binding = Binding("post", 0, old.AssetId, provider, actual);
        Dictionary<string, MediaAttachmentBinding> mutable = new() { [binding.Reference.RevisionKey] = binding };
        MediaAssetSnapshot snapshot = new(temp.Path, MediaManifestStatus.Available, 3, "generation", [old, latest], attachmentBindings: mutable);
        mutable.Clear();
        CapturedAttachment attachment = Attachment(provider, binding.Reference);
        Assert.Equal(old, MediaAttachmentResolver.Resolve(snapshot, Id, "post", attachment)!.Asset);
        Assert.Null(MediaAttachmentResolver.Resolve(snapshot, Id, "other", attachment));
        Assert.Null(MediaAttachmentResolver.Resolve(snapshot, Id with { Thread = "other" }, "post", attachment));
        foreach (CapturedAttachment changed in new[] { attachment with { Url = provider.Url + "?changed" },
            attachment with { ExpectedSize = 1000 }, attachment with { ExpectedMd5 = Md5(Bytes) },
            attachment with { ExpectedSha256 = actual.Sha256 }, attachment with { Variant = "thumbnail" } })
            Assert.Null(MediaAttachmentResolver.Resolve(snapshot, Id, "post", changed));
        Assert.Null(MediaAttachmentResolver.Resolve(new MediaAssetSnapshot(temp.Path, MediaManifestStatus.Available, 3, "new", [latest]), Id, "post", attachment));
    }

    [Fact]
    public void ConflictingCrossDirectoryBindingsFailClosedAndRepeatedIdenticalBindingIsStable()
    {
        using TempDirectory temp = new(); MediaProviderMetadata provider = Provider(3, Md5(Bytes));
        MediaActualIntegrity actual = new(3, Md5(Bytes), Sha(Bytes));
        MediaAssetReference a = Asset(Path.Combine(temp.Path, "a"), "a", actual), b = Asset(Path.Combine(temp.Path, "b"), "b", actual);
        MediaAttachmentBinding bindingA = Binding("post", 0, a.AssetId, provider, actual), bindingB = Binding("post", 0, b.AssetId, provider, actual);
        MediaAssetSnapshot first = new(Path.Combine(temp.Path, "a"), MediaManifestStatus.Available, 3, "a", [a],
            attachmentBindings: new Dictionary<string, MediaAttachmentBinding> { [bindingA.Reference.RevisionKey] = bindingA });
        MediaAssetSnapshot conflict = new(Path.Combine(temp.Path, "b"), MediaManifestStatus.Available, 3, "b", [b],
            attachmentBindings: new Dictionary<string, MediaAttachmentBinding> { [bindingB.Reference.RevisionKey] = bindingB });
        CapturedAttachment attachment = Attachment(provider, bindingA.Reference);
        Assert.Null(MediaAttachmentResolver.Resolve(new[] { first, conflict }, Id, "post", attachment));
        Assert.Equal(a, MediaAttachmentResolver.Resolve(new[] { first, first }, Id, "post", attachment)!.Asset);
    }

    [Fact]
    public void LegacyChecksAvailableMetadataUsesExactOriginalUrlWithoutExpectationsAndRejectsAmbiguity()
    {
        using TempDirectory temp = new(); MediaActualIntegrity actual = new(3, Md5(Bytes), Sha(Bytes));
        MediaAssetReference asset = Asset(temp.Path, "one", actual);
        MediaAssetSnapshot snapshot = new(temp.Path, MediaManifestStatus.Available, 2, "legacy", [asset]);
        CapturedAttachment legacy = new(0, "media", Url: asset.SourceUrl);
        Assert.True(MediaAttachmentResolver.Resolve(snapshot, Id, "post", legacy)!.IsLegacy);
        Assert.Null(MediaAttachmentResolver.Resolve(snapshot, Id, "post", legacy with { Url = "https://source.test/new" }));
        Assert.Null(MediaAttachmentResolver.Resolve(snapshot, Id, "post", legacy with { Url = null }));
        Assert.NotNull(MediaAttachmentResolver.Resolve(snapshot, Id, "post", legacy with { Url = null, ExpectedSize = 3 }));
        Assert.Null(MediaAttachmentResolver.Resolve(snapshot, Id, "post", legacy with { ExpectedSize = 4 }));
        string changedCase = new(asset.Md5.Select(character => char.IsAsciiLetter(character)
            ? char.IsUpper(character) ? char.ToLowerInvariant(character) : char.ToUpperInvariant(character) : character).ToArray());
        Assert.NotEqual(asset.Md5, changedCase);
        Assert.Null(MediaAttachmentResolver.Resolve(snapshot, Id, "post", legacy with { ExpectedMd5 = changedCase }));
        MediaAssetSnapshot ambiguous = new(temp.Path, MediaManifestStatus.Available, 2, "ambiguous", [asset, asset with { AssetId = new string('e', 64), IsCurrent = false }]);
        Assert.Null(MediaAttachmentResolver.Resolve(ambiguous, Id, "post", legacy));
        Assert.Null(MediaAttachmentResolver.Resolve(new[] { snapshot, ambiguous }, Id, "post", legacy));
    }

    [Theory]
    [InlineData("asset")]
    [InlineData("actual")]
    [InlineData("key")]
    public async Task InvalidWireBindingNeverBecomesLegacyOrAnotherAsset(string corruption)
    {
        using TempDirectory temp = new(); MediaProviderMetadata provider = Provider(3, Md5(Bytes));
        MediaActualIntegrity actual = new(3, Md5(Bytes), Sha(Bytes)); string assetId = AssetId("media.bin", "saved.bin", actual.Sha256);
        var binding = Binding("post", 0, assetId, provider, actual);
        await WriteManifest(temp.Path, provider, actual, assetId, new() { [binding.Reference.RevisionKey] = binding });
        string path = Path.Combine(temp.Path, ".meowchan-manifest.json"); JsonNode wire = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        JsonObject map = wire["AttachmentBindings"]!.AsObject(); JsonNode value = map[binding.Reference.RevisionKey]!;
        if (corruption == "asset") value["AssetId"] = new string('f', 64);
        else if (corruption == "actual") value["Actual"]!["Length"] = 4;
        else { map.Remove(binding.Reference.RevisionKey); map["att-v1:" + new string('a', 64)] = value; }
        string damaged = wire.ToJsonString(); await File.WriteAllTextAsync(path, damaged);
        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(temp.Path);
        Assert.Equal(MediaManifestStatus.Corrupt, snapshot.Status); Assert.Empty(snapshot.AttachmentBindings); Assert.Empty(snapshot.GetPage());
        Assert.Equal(damaged, await File.ReadAllTextAsync(path));
    }

    private static MediaAssetReference Asset(string directory, string label, MediaActualIntegrity actual) => new(Sha(Encoding.UTF8.GetBytes(label)),
        "media", "original", "media.bin", Path.Combine(directory, label + ".bin"), actual.Length, actual.Sha256,
        "https://source.test/media", "original.bin", true) { Md5 = actual.Md5 };
    private static MediaAttachmentBinding Binding(string post, int position, string assetId, MediaProviderMetadata provider, MediaActualIntegrity actual)
    {
        var assessment = ProviderIntegrityAssessment.Assess(provider, actual);
        return new(new(Id, post, position, MediaRevisionKeys.CreateAttachmentKey(Id, post, position, provider)),
            MediaRevisionKeys.CreateAcquisitionKey(Id, provider), assetId, provider, actual,
            new(assessment.IsStrictMatch ? MediaAcceptanceKind.Strict : MediaAcceptanceKind.ExplicitProviderMismatch,
                assessment.SizeMismatch, assessment.Md5Mismatch, DateTimeOffset.UnixEpoch, "profile", "owner", Id.Source, 1));
    }
    private static CapturedAttachment Attachment(MediaProviderMetadata provider, AttachmentRevisionReference reference) => new(reference.Position,
        provider.MediaId, provider.Variant, provider.Url, provider.OriginalFileName, ExpectedSize: provider.ExpectedSize, ExpectedMd5: provider.ExpectedMd5)
        { ExpectedSha256 = provider.ExpectedSha256, AttachmentRevisionKey = reference.RevisionKey };
    private static string AssetId(string requested, string file, string sha) => Sha(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { requested, "media", file, sha })));
    private static async Task WriteManifest(string directory, MediaProviderMetadata provider, MediaActualIntegrity actual, string assetId,
        Dictionary<string, MediaAttachmentBinding> bindings)
    {
        await File.WriteAllBytesAsync(Path.Combine(directory, "saved.bin"), Bytes);
        object entry = new { MediaIdentifier = "media", Variant = "original", SourceUrl = provider.Url, FileName = "saved.bin",
            Length = actual.Length, actual.Sha256, actual.Md5, OriginalFileName = provider.OriginalFileName };
        await File.WriteAllTextAsync(Path.Combine(directory, ".meowchan-manifest.json"), JsonSerializer.Serialize(new { Version = 3,
            Entries = new Dictionary<string, object> { ["media.bin"] = entry },
            Assets = new Dictionary<string, object> { [assetId] = new { RequestedFileName = "media.bin", Entry = entry } }, AttachmentBindings = bindings }));
    }
}
