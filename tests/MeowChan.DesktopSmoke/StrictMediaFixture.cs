using System.Text.Json;
using System.Text.Json.Nodes;
using MeowChan.Archive;
using MeowChan.Storage;

namespace MeowChan.DesktopSmoke;

/// <summary>Controlled saved-image observations, with explicit current revision receipts.
/// This prepares synthetic UI data; it is not a production acquisition/permission proof.</summary>
internal static class StrictMediaFixture
{
    internal static async Task<CapturedPost[]> BindAsync(string directory, ResourceId resource,
        IEnumerable<CapturedPost> posts)
    {
        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(directory);
        MediaAssetReference[] assets = Enumerable.Range(0, (snapshot.Count + 499) / 500)
            .SelectMany(page => snapshot.GetPage(page * 500, 500)).ToArray();
        foreach (MediaAssetReference asset in assets)
        {
            await using Stream verified = await snapshot.OpenVerifiedReadAsync(asset.AssetId);
        }
        string path = Path.Combine(directory, ".meowchan-manifest.json");
        JsonObject manifest = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        JsonObject history = manifest["Assets"] as JsonObject ?? new();
        foreach (MediaAssetReference asset in assets)
            if (!history.ContainsKey(asset.AssetId))
                history[asset.AssetId] = new JsonObject { ["RequestedFileName"] = asset.RequestedFileName,
                    ["Entry"] = manifest["Entries"]![asset.RequestedFileName]!.DeepClone() };
        if (manifest["Assets"] == null) manifest["Assets"] = history;
        JsonObject bindings = manifest["AttachmentBindings"] as JsonObject ?? new();
        CapturedPost[] bound = posts.Select(post => post with { Attachments = post.Attachments?.Select(attachment =>
        {
            MediaAssetReference asset = assets.Single(asset => asset.MediaIdentifier == attachment.MediaId
                && asset.Variant == attachment.Variant);
            CapturedAttachment observed = attachment with { Url = asset.SourceUrl,
                ExpectedSize = asset.Length, ExpectedMd5 = asset.Md5, ExpectedSha256 = asset.Sha256 };
            MediaProviderMetadata provider = new(observed.MediaId!, observed.Variant, observed.Url!,
                observed.OriginalFileName, observed.ExpectedSize, observed.ExpectedMd5, observed.ExpectedSha256);
            MediaActualIntegrity actual = new(asset.Length, asset.Md5, asset.Sha256);
            if (!ProviderIntegrityAssessment.Assess(provider, actual).IsStrictMatch)
                throw new InvalidOperationException("Synthetic UI media must have strict provider/local values.");
            string key = MediaRevisionKeys.CreateAttachmentKey(resource, post.PostId, observed.Position, provider);
            bindings[key] = JsonSerializer.SerializeToNode(new MediaAttachmentBinding(
                new(resource, post.PostId, observed.Position, key), MediaRevisionKeys.CreateAcquisitionKey(resource, provider),
                asset.AssetId, provider, actual, new(MediaAcceptanceKind.Strict, false, false, DateTimeOffset.UnixEpoch)));
            return observed with { AttachmentRevisionKey = key };
        }).ToArray() }).ToArray();
        if (manifest["AttachmentBindings"] == null) manifest["AttachmentBindings"] = bindings;
        manifest["Version"] = 3;
        await File.WriteAllTextAsync(path, manifest.ToJsonString());
        MediaAssetSnapshot current = await MediaAssetReader.OpenAsync(directory);
        foreach (CapturedPost post in bound)
            foreach (CapturedAttachment attachment in post.Attachments ?? [])
                if (MediaAttachmentResolver.Resolve(current, resource, post.PostId, attachment) is not
                    { IsLegacy: false, Binding.Acceptance.Kind: MediaAcceptanceKind.Strict })
                    throw new InvalidOperationException("Synthetic UI attachment has no exact strict binding.");
        return bound;
    }
}
