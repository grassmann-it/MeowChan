using System.Text.Json;
using System.Text.Json.Nodes;
using MeowChan.Archive;
using MeowChan.Storage;

namespace MeowChan.Tests;

internal static class CommittedMediaTestFixture
{
    internal static async Task<MediaAssetSnapshot> BindAsync(string directory, ResourceId resource,
        IEnumerable<CapturedPost> posts, bool allowMismatch = false, string? assetId = null)
    {
        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(directory);
        var assets = Enumerable.Range(0, (snapshot.Count + 499) / 500).SelectMany(page => snapshot.GetPage(page * 500, 500)).ToArray();
        string path = Path.Combine(directory, ".meowchan-manifest.json");
        JsonObject manifest = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        JsonObject history = manifest["Assets"] as JsonObject ?? new();
        foreach (MediaAssetReference asset in assets)
            if (!history.ContainsKey(asset.AssetId))
                history[asset.AssetId] = new JsonObject { ["RequestedFileName"] = asset.RequestedFileName,
                    ["Entry"] = manifest["Entries"]![asset.RequestedFileName]!.DeepClone() };
        if (manifest["Assets"] == null) manifest["Assets"] = history;
        JsonObject bindings = manifest["AttachmentBindings"] as JsonObject ?? new();
        foreach (CapturedPost post in posts)
            foreach (CapturedAttachment attachment in post.Attachments ?? [])
            {
                if (attachment.MediaId == null || attachment.IsDeleted || attachment.Url == null) continue;
                MediaProviderMetadata provider = new(attachment.MediaId, attachment.Variant, attachment.Url!,
                    attachment.OriginalFileName, attachment.ExpectedSize, attachment.ExpectedMd5, attachment.ExpectedSha256);
                string key = MediaRevisionKeys.CreateAttachmentKey(resource, post.PostId, attachment.Position, provider);
                if (bindings.ContainsKey(key)) continue;
                MediaAssetReference[] matches = assets.Where(asset => asset.MediaIdentifier == attachment.MediaId
                    && asset.Variant == attachment.Variant && (assetId == null || asset.AssetId == assetId)
                    && (allowMismatch || ProviderIntegrityAssessment.Assess(provider, new(asset.Length, asset.Md5, asset.Sha256)).IsStrictMatch)).ToArray();
                if (matches.Length != 1) continue;
                MediaAssetReference chosen = matches[0];
                MediaActualIntegrity actual = new(chosen.Length, chosen.Md5, chosen.Sha256);
                ProviderIntegrityAssessment assessment = ProviderIntegrityAssessment.Assess(provider, actual);
                if (assessment.Sha256Mismatch) continue;
                MediaAcceptanceRecord acceptance = assessment.IsStrictMatch
                    ? new(MediaAcceptanceKind.Strict, false, false, DateTimeOffset.UtcNow)
                    : new(MediaAcceptanceKind.ExplicitProviderMismatch, assessment.SizeMismatch, assessment.Md5Mismatch,
                        DateTimeOffset.UtcNow, "fixture-profile", "fixture-owner", resource.Source, 1);
                MediaAttachmentBinding binding = new(new(resource, post.PostId, attachment.Position, key),
                    MediaRevisionKeys.CreateAcquisitionKey(resource, provider), chosen.AssetId, provider, actual, acceptance);
                bindings[key] = JsonSerializer.SerializeToNode(binding);
            }
        if (manifest["AttachmentBindings"] == null) manifest["AttachmentBindings"] = bindings;
        manifest["Version"] = 3;
        await File.WriteAllTextAsync(path, manifest.ToJsonString());
        return await MediaAssetReader.OpenAsync(directory);
    }
}
