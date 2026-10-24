using System.Security.Cryptography;
using System.Text.Json;
using MeowChan;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;
using MeowChan.Storage;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class ArchiveAttachmentMatchingTests
{
    private static readonly ResourceId Resource = new("fixture", "g", "42");

    [Fact]
    public async Task LegacyInlineAttachmentChecksDecodedHashSizeAndVariant()
    {
        using Fixture fixture = await Fixture.CreateAsync(bound: false, mismatch: false);
        ArchiveAssetRow matching = new(fixture.Snapshot, fixture.Asset);
        Assert.Same(matching, ArchivePage.FindMatchingAsset(Resource, "43", fixture.Attachment, [matching]));
        Assert.Null(ArchivePage.FindMatchingAsset(Resource, "43", fixture.Attachment with { ExpectedSize = fixture.Asset.Length + 1 }, [matching]));
        Assert.Null(ArchivePage.FindMatchingAsset(Resource, "43", fixture.Attachment with { Variant = "thumbnail" }, [matching]));
        Assert.Null(ArchivePage.FindMatchingAsset(Resource, "43", fixture.Attachment with { MediaId = null }, [matching]));
        string checksum = fixture.Asset.Md5;
        int letter = Array.FindIndex(checksum.ToCharArray(), char.IsAsciiLetter);
        string wrongCase = checksum[..letter] + (char.IsUpper(checksum[letter]) ? char.ToLowerInvariant(checksum[letter]) : char.ToUpperInvariant(checksum[letter])) + checksum[(letter + 1)..];
        Assert.False(Convert.FromBase64String(checksum).SequenceEqual(Convert.FromBase64String(wrongCase)));
        Assert.Null(ArchivePage.FindMatchingAsset(Resource, "43", fixture.Attachment with { ExpectedMd5 = wrongCase }, [matching]));
    }

    [Fact]
    public async Task InlineUsesExactAcceptedBindingAndNeverReusesItForAnotherPostOrRevision()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        ArchiveAssetRow row = new(fixture.Snapshot, fixture.Asset);
        Assert.Same(row, ArchivePage.FindMatchingAsset(Resource, "43", fixture.Attachment, [row]));
        Assert.NotEqual(fixture.Attachment.ExpectedSize, fixture.Asset.Length);
        Assert.NotEqual(fixture.Attachment.ExpectedMd5, fixture.Asset.Md5);
        Assert.Null(ArchivePage.FindMatchingAsset(Resource, "44", fixture.Attachment, [row]));
        CapturedAttachment next = fixture.Attachment with { Url = "https://fixture.invalid/new.png" };
        MediaProviderMetadata provider = fixture.Binding!.Provider with { Url = next.Url! };
        next = next with { AttachmentRevisionKey = MediaRevisionKeys.CreateAttachmentKey(Resource, "43", next.Position, provider) };
        Assert.Null(ArchivePage.FindMatchingAsset(Resource, "43", next, [row]));
    }

    [Fact]
    public async Task ThumbnailReadRejectsChangedLocalBytesEvenWhenBindingStillResolves()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        byte[] changed = await File.ReadAllBytesAsync(fixture.Asset.Path);
        changed[^1] ^= 1;
        await File.WriteAllBytesAsync(fixture.Asset.Path, changed);
        ArchiveAssetRow? row = ArchivePage.FindMatchingAsset(Resource, "43", fixture.Attachment, [new(fixture.Snapshot, fixture.Asset)]);
        Assert.NotNull(row);
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchivePage.DecodeThumbnailAsync(row!, CancellationToken.None));
    }

    [Fact]
    public async Task CleanupAcceptsOnlyTheExactCompletedAcquisitionAndItsReferences()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        MediaWork work = new()
        {
            ResourceUrl = "https://fixture.invalid/g/thread/42", Generation = 1, MediaIdentifier = "777",
            DestinationPath = fixture.Asset.Path, DownloadUrl = fixture.Attachment.Url!,
            ExpectedSize = fixture.Attachment.ExpectedSize, ExpectedMd5 = fixture.Attachment.ExpectedMd5,
            OriginalFileName = fixture.Attachment.OriginalFileName, Status = MediaWorkStatus.Succeeded,
            AcquisitionRevisionKey = fixture.Binding!.AcquisitionRevisionKey, CommittedAssetId = fixture.Asset.AssetId,
            AttachmentReferences = [fixture.Binding.Reference]
        };
        Assert.True(DesktopSessionHost.HasMatchingCleanupMedia(Resource, work, [fixture.Snapshot]));
        Assert.False(DesktopSessionHost.HasMatchingCleanupMedia(Resource, work with { ExpectedSize = 99999 }, [fixture.Snapshot]));
        Assert.False(DesktopSessionHost.HasMatchingCleanupMedia(Resource, work with { AttachmentReferences = [] }, [fixture.Snapshot]));
        Assert.False(DesktopSessionHost.HasMatchingCleanupMedia(Resource, work with { CommittedAssetId = new string('0', 64) }, [fixture.Snapshot]));
    }

    [Theory]
    [InlineData("en", "source", "Source reported")]
    [InlineData("de", "Anbieter", "Anbieterangabe")]
    public async Task ProvenanceUsesTheConcreteBindingAndSeparatesReportedAndMeasuredValues(string language, string word, string detail)
    {
        using Fixture fixture = await Fixture.CreateAsync();
        string previous = Texts.Language;
        try
        {
            Texts.SetLanguage(language);
            Assert.Contains(word, MediaProvenanceText.Summary(fixture.Binding));
            Assert.Contains(detail, MediaProvenanceText.Details(fixture.Binding));
            Assert.Contains(fixture.Attachment.ExpectedMd5!, MediaProvenanceText.Details(fixture.Binding));
            Assert.Empty(MediaProvenanceText.Summary(null));
            Assert.Empty(MediaProvenanceText.Summary(fixture.Binding! with
                { Acceptance = new(MediaAcceptanceKind.Strict, false, false, DateTimeOffset.UtcNow) }));
            ArchiveAssetRow row = new(fixture.Snapshot, fixture.Asset);
            Assert.Equal((fixture.Asset.Length / 1_000_000d).ToString("0.###", Texts.Culture) + " MB", row.Size);
        }
        finally { Texts.SetLanguage(previous); }
    }

    private sealed record Fixture(string Directory, MediaAssetSnapshot Snapshot, MediaAssetReference Asset,
        CapturedAttachment Attachment, MediaAttachmentBinding? Binding) : IDisposable
    {
        internal static async Task<Fixture> CreateAsync(bool bound = true, bool mismatch = true)
        {
            string directory = Path.Combine(Path.GetTempPath(), "meowchan-consumer-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            byte[] bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAABEAAAALCAIAAAAWQvFQAAAAF0lEQVR4nGO8oxHAQCJgIlXDqJ5hqQcAEJEBagzqPS4AAAAASUVORK5CYII=");
            await File.WriteAllBytesAsync(Path.Combine(directory, "a.png"), bytes);
            object entry = new { MediaIdentifier = "777", Variant = "original", SourceUrl = "https://fixture.invalid/a.png",
                FileName = "a.png", Length = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                Md5 = Convert.ToBase64String(MD5.HashData(bytes)), OriginalFileName = "a.png" };
            string path = Path.Combine(directory, ".meowchan-manifest.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Version = 1, Entries = new Dictionary<string, object> { ["a.png"] = entry } }));
            MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(directory);
            MediaAssetReference asset = Assert.Single(snapshot.GetPage());
            MediaProviderMetadata provider = new("777", "original", "https://fixture.invalid/a.png", "a.png",
                mismatch ? bytes.Length + 17 : bytes.Length,
                mismatch ? Convert.ToBase64String(MD5.HashData(new byte[] { 3, 1, 4 })) : asset.Md5, null);
            CapturedAttachment attachment = new(0, provider.MediaId, provider.Variant, provider.Url, provider.OriginalFileName,
                Path.Combine(directory, "wrong-planned-path.png"), provider.ExpectedSize, provider.ExpectedMd5);
            MediaAttachmentBinding? binding = null;
            if (bound)
            {
                string key = MediaRevisionKeys.CreateAttachmentKey(Resource, "43", 0, provider);
                binding = new(new(Resource, "43", 0, key), MediaRevisionKeys.CreateAcquisitionKey(Resource, provider), asset.AssetId,
                    provider, new(asset.Length, asset.Md5, asset.Sha256), mismatch
                        ? new(MediaAcceptanceKind.ExplicitProviderMismatch, true, true, DateTimeOffset.UtcNow, "profile", "owner", Resource.Source, 1)
                        : new(MediaAcceptanceKind.Strict, false, false, DateTimeOffset.UtcNow));
                attachment = attachment with { AttachmentRevisionKey = key };
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Version = 3,
                    Entries = new Dictionary<string, object> { ["a.png"] = entry },
                    Assets = new Dictionary<string, object> { [asset.AssetId] = new { RequestedFileName = "a.png", Entry = entry } },
                    AttachmentBindings = new Dictionary<string, MediaAttachmentBinding> { [key] = binding } }));
                snapshot = await MediaAssetReader.OpenAsync(directory);
                asset = Assert.Single(snapshot.GetPage());
            }
            return new(directory, snapshot, asset, attachment, binding);
        }
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
}
