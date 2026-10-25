using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowChan;
using MeowChan.Archive;
using MeowChan.Downloader;
using MeowChan.Storage;

namespace LibMeowChan.Consumer;

// Public package APIs only. The fixture is a historical manifest, not a writer upgrade.
internal static class LegacyIntegrityConsumerChecks
{
    internal static async Task VerifyAsync(HttpClient transport, string workspace)
    {
        string directory = Path.Combine(workspace, "legacy integrity fixture");
        Directory.CreateDirectory(directory);
        string savedPath = Path.Combine(directory, "saved.bin");
        byte[] bytes = [1, 3, 5, 7];
        await File.WriteAllBytesAsync(savedPath, bytes);
        string md5 = Convert.ToBase64String(MD5.HashData(bytes));
        string sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        ResourceId resource = DemoPayload.Resource;
        MediaProviderMetadata provider = new("legacy", "original", "https://demo.invalid/media/legacy.bin", "legacy.bin", bytes.Length, md5, null);
        object entry = new { MediaIdentifier = provider.MediaId, Variant = provider.Variant, SourceUrl = provider.Url,
            FileName = "saved.bin", Length = bytes.Length, Sha256 = sha256, Md5 = md5, OriginalFileName = provider.OriginalFileName };
        string path = Path.Combine(directory, ".meowchan-manifest.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Version = 1,
            Entries = new Dictionary<string, object> { ["saved.bin"] = entry } }));
        MediaAssetReference initial = (await MediaAssetReader.OpenAsync(directory)).GetPage().Single();
        string acquisition = MediaRevisionKeys.CreateAcquisitionKey(resource, provider);
        string key = MediaRevisionKeys.CreateAttachmentKey(resource, "legacy-post", 0, provider);
        if (!acquisition.StartsWith("acq-v1:", StringComparison.Ordinal) || !key.StartsWith("att-v1:", StringComparison.Ordinal))
            throw new InvalidOperationException("Metadata without the new fields lost its v1 identity.");
        AttachmentRevisionReference reference = new(resource, "legacy-post", 0, key);
        MediaAttachmentBinding binding = new(reference, acquisition, initial.AssetId, provider,
            new(bytes.Length, md5, sha256), new(MediaAcceptanceKind.Strict, false, false, DateTimeOffset.UtcNow));
        JsonObject legacy = JsonSerializer.SerializeToNode(new { Version = 3,
            Entries = new Dictionary<string, object> { ["saved.bin"] = entry },
            Assets = new Dictionary<string, object> { [initial.AssetId] = new { RequestedFileName = "saved.bin", Entry = entry } },
            AttachmentBindings = new Dictionary<string, MediaAttachmentBinding> { [key] = binding } })!.AsObject();
        legacy["AttachmentBindings"]![key]!["Actual"]!.AsObject().Remove("Sha1");
        legacy["AttachmentBindings"]![key]!["Provider"]!.AsObject().Remove("ExpectedSha1");
        legacy["AttachmentBindings"]![key]!["Provider"]!.AsObject().Remove("ProviderSizeHint");
        await File.WriteAllTextAsync(path, legacy.ToJsonString());
        byte[] originalManifest = await File.ReadAllBytesAsync(path);
        MediaAssetSnapshot historical = await MediaAssetReader.OpenAsync(directory);
        CapturedAttachment attachment = new(0, provider.MediaId, provider.Variant, provider.Url,
            provider.OriginalFileName, savedPath, provider.ExpectedSize, provider.ExpectedMd5) { AttachmentRevisionKey = key };
        ResolvedMediaAttachment? resolved = MediaAttachmentResolver.Resolve(historical, resource, "legacy-post", attachment);
        if (historical.ManifestVersion != 3 || resolved?.Binding?.Actual.Sha1 != null || resolved?.Asset.AssetId != initial.AssetId)
            throw new InvalidOperationException("Historical SHA256 binding without optional SHA1 no longer resolves.");
        await using (Stream read = await historical.OpenVerifiedReadAsync(initial.AssetId))
        {
            using MemoryStream copy = new();
            await read.CopyToAsync(copy);
            if (!copy.ToArray().AsSpan().SequenceEqual(bytes)) throw new InvalidOperationException("Historical binding returned different bytes.");
        }
        DemoProvider source = new(transport); // Borrows the caller-owned transport.
        DownloadItem item = new(new Uri(provider.Url), savedPath, source,
            source.GetThread(new Uri("https://demo.invalid/demo/thread/1")), provider.MediaId,
            provider.ExpectedSize, provider.ExpectedMd5, provider.OriginalFileName) {
            AcquisitionRevisionKey = acquisition, AttachmentReferences = [reference] };
        MediaCommitResult? restored = await new MediaStore().TryRestoreAsync(item, CancellationToken.None);
        byte[] unchangedManifest = await File.ReadAllBytesAsync(path);
        if (restored?.AssetId != initial.AssetId || !originalManifest.AsSpan().SequenceEqual(unchangedManifest))
            throw new InvalidOperationException("Legacy restore failed or modified its read-only manifest.");
        MediaProviderMetadata modern = provider with { ExpectedSha1 = Convert.ToBase64String(SHA1.HashData(bytes)) };
        if (!ProviderIntegrityAssessment.Assess(modern, resolved!.Binding!.Actual).Sha1Mismatch
            || MediaRevisionKeys.CreateAttachmentKey(resource, "legacy-post", 0, modern) == key)
            throw new InvalidOperationException("Missing historical SHA1 evidence was silently treated as a verified new SHA1 expectation.");
        await File.WriteAllBytesAsync(savedPath, [2, 4, 6, 8]);
        if (await new MediaStore().TryRestoreAsync(item, CancellationToken.None) != null)
            throw new InvalidOperationException("Damaged legacy bytes restored without local verification.");
        Console.WriteLine("Legacy integrity package checks passed: v1 keys, v3 binding, no rewrite, measured restore, missing-SHA1 rejection and damaged-byte rejection.");
    }
}
