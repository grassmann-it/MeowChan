using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;

namespace MeowChan.Desktop;

// Two distinguishable, verified local targets for navigation during the apphost's
// real blocked download. Only the owned --smoke-workflow profile calls this helper.
internal static class ApphostWorkflowGalleryFixture
{
    internal static readonly ResourceId Id = new("4chan", "g", "9900");
    private static readonly byte[] Portrait = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAABgAAAAgCAIAAACHPC9vAAAAJElEQVR4nGPYp+tIFcQwatCoQaMGjRo0atCoQaMGjRo0PAwCALDUhC5HBWEgAAAAAElFTkSuQmCC");
    internal static string FixtureHash => Convert.ToHexString(SHA256.HashData(
        ApphostWorkflowSmoke.Payload.Concat(Portrait).ToArray())).ToLowerInvariant();

    internal static async Task SeedAsync(string root, string profile)
    {
        string directory = Path.Combine(root, "gallery");
        Directory.CreateDirectory(directory);
        Dictionary<string, object> entries = new();
        Dictionary<string, object> assets = new();
        Dictionary<string, MediaAttachmentBinding> bindings = new();
        async Task<CapturedPost> PostAsync(string postId, string name, byte[] bytes, int order)
        {
            await File.WriteAllBytesAsync(Path.Combine(directory, name), bytes).ConfigureAwait(false);
            string sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            string md5 = Convert.ToBase64String(MD5.HashData(bytes));
            MediaProviderMetadata provider = new(postId, "original", "https://offline.invalid/" + name,
                name, bytes.Length, md5, sha);
            string revision = MediaRevisionKeys.CreateAttachmentKey(Id, postId, 0, provider);
            string assetId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new[] { name, postId, name, sha })))).ToLowerInvariant();
            object entry = new { MediaIdentifier = postId, Variant = provider.Variant, SourceUrl = provider.Url,
                FileName = name, Length = bytes.Length, Sha256 = sha, Md5 = md5, OriginalFileName = name };
            entries.Add(name, entry);
            assets.Add(assetId, new { RequestedFileName = name, Entry = entry });
            bindings.Add(revision, new(new(Id, postId, 0, revision),
                MediaRevisionKeys.CreateAcquisitionKey(Id, provider), assetId, provider,
                new(bytes.Length, md5, sha), new(MediaAcceptanceKind.Strict, false, false, DateTimeOffset.UnixEpoch)));
            CapturedAttachment attachment = new(0, postId, provider.Variant, provider.Url, name,
                ExpectedSize: bytes.Length, ExpectedMd5: md5)
                { ExpectedSha256 = sha, AttachmentRevisionKey = revision };
            return new CapturedPost(postId, order, PostFieldPresence.All,
                Html: "Owned offline performance target " + postId, Attachments: new[] { attachment });
        }
        CapturedPost landscape = await PostAsync("9900", "landscape.png", ApphostWorkflowSmoke.Payload, 0).ConfigureAwait(false);
        CapturedPost portrait = await PostAsync("9901", "portrait.png", Portrait, 1).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(directory, ".meowchan-manifest.json"), JsonSerializer.Serialize(new
            { Version = 3, Entries = entries, Assets = assets, AttachmentBindings = bindings })).ConfigureAwait(false);
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease).ConfigureAwait(false);
        await catalog.CaptureAsync(new CapturedThreadSnapshot(Id, "https://offline.invalid/thread/9900",
            "Performance targets", ThreadSourceState.Archived, DateTimeOffset.UnixEpoch,
            directory, new[] { landscape, portrait })).ConfigureAwait(false);
    }
}
