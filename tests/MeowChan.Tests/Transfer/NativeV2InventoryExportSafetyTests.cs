using System.Text.Json;
using System.Text.Json.Nodes;
using MeowChan.Archive;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

// Real admitted v4 import first; adversarial edits affect only this fixture's files.
// The legacy bool lane is deliberately not widened by these inventory tests.
public sealed class NativeV2InventoryExportSafetyTests
{
    private static readonly Guid PackageId = Guid.Parse("6451d241-a91e-447c-9ad6-1738f50f8e69");
    private const string ManifestName = ".meowchan-manifest.json";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactDerivedTamperingAfterFreezeRejectsEvenWhenPayloadIsSharedWithOriginal(bool sharedHash)
    {
        await using NativeV2ImportFixture f = await CreateAsync(sharedHash);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(f.Files.Profile);
        string root = await RootAsync(reader);
        string path = await DerivedPathAsync(root);
        byte[] manifest = await File.ReadAllBytesAsync(Path.Combine(root, ManifestName));
        NativeTransferArchiveSnapshot snapshot = Frozen(await FreezeAsync(reader, new(true, true, true)));
        Assert.Equal(sharedHash ? 1 : 2, snapshot.Payloads.Length);
        byte[] changed = await File.ReadAllBytesAsync(path);
        changed[0] ^= 0xff;
        await File.WriteAllBytesAsync(path, changed);

        using MemoryStream output = new();
        TransferArchiveWriteResult result = await NativeTransferArchiveExporter.WriteAsync(snapshot, output);
        Assert.False(result.IsComplete);
        Assert.Equal(TransferArchiveError.InvalidMedia, result.Error);
        Assert.Null(result.Package);
        Assert.Null(result.BytesWritten);
        Assert.True(output.CanWrite);
        Assert.Equal(manifest, await File.ReadAllBytesAsync(Path.Combine(root, ManifestName)));
        Assert.Equal(changed, await File.ReadAllBytesAsync(path));
        Assert.DoesNotContain(f.Files.Profile, result.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreezeChecksBothPhysicalOriginsDespiteSharedPayloadHash(bool corruptDerived)
    {
        await using NativeV2ImportFixture f = await CreateAsync(sharedHash: true);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(f.Files.Profile);
        string root = await RootAsync(reader);
        string path = corruptDerived ? await DerivedPathAsync(root)
            : Assert.Single((await MediaAssetReader.OpenAsync(root)).GetPage()).Path;
        await PoisonAsync(path);
        TransferArchiveFreezeResult result = await FreezeAsync(reader, new(true, true, true));
        Assert.Equal(TransferArchiveError.InvalidMedia, result.Error);
        Assert.Null(result.Snapshot);
        Assert.Equal(new byte[] { 0xff }, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingRoleDoesNotBorrowEqualHashFromOtherRole(bool missingOriginal)
    {
        await using NativeV2ImportFixture f = await CreateAsync(sharedHash: true);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(f.Files.Profile);
        string root = await RootAsync(reader);
        MediaAssetSnapshot media = await MediaAssetReader.OpenAsync(root);
        string original = Assert.Single(media.GetPage()).Path;
        string derived = await DerivedPathAsync(root);
        Assert.NotEqual(original, derived);
        Assert.Equal(await File.ReadAllBytesAsync(original), await File.ReadAllBytesAsync(derived));
        string missing = missingOriginal ? original : derived;
        File.Delete(missing);

        NativeTransferArchiveSnapshot snapshot = Frozen(await FreezeAsync(reader, new(true, true, true)));
        TransferAttachment attachment = Attachment(snapshot.Catalog);
        TransferDerivedRepresentation descriptor = Assert.Single(attachment.DerivedRepresentations);
        TransferAttachment expected = NativeV2ImportFixture.Attachment();
        Assert.Equal(expected.OriginalByteVersion, attachment.OriginalByteVersion);
        Assert.Equal(expected.Metadata.AttachmentRevisionKey, descriptor.SourceRevisionKey);
        Assert.Equal(expected.OriginalByteVersion, descriptor.SourceByteVersion);
        Assert.Equal(TransferDerivedRepresentationKind.Preview, descriptor.Kind);
        Assert.Equal(missingOriginal ? TransferContentStatus.Unavailable : TransferContentStatus.Contained, attachment.Status);
        Assert.Equal(missingOriginal ? TransferContentStatus.Contained : TransferContentStatus.Unavailable, descriptor.Status);
        Assert.Equal(missingOriginal ? null : NativeV2ImportFixture.Hash(NativeV2ImportFixture.Original), attachment.PayloadSha256);
        Assert.Equal(missingOriginal ? NativeV2ImportFixture.Hash(NativeV2ImportFixture.Original) : null, descriptor.PayloadSha256);
        Assert.Single(snapshot.Payloads);
        using MemoryStream output = new();
        TransferArchiveWriteResult written = await NativeTransferArchiveExporter.WriteAsync(snapshot, output);
        Assert.True(written.IsComplete, written.ToString());
        Assert.True(written.Package!.IsChanV4);
        Assert.Equal(attachment.OriginalByteVersion, Attachment(written.Package.Catalog).OriginalByteVersion);
        Assert.Equal(descriptor, Assert.Single(Attachment(written.Package.Catalog).DerivedRepresentations));
        Assert.False(File.Exists(missing));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectedPhysicalOriginRejectsIdenticalByteSymlinkAfterFreeze(bool derivedOrigin)
    {
        await using NativeV2ImportFixture f = await CreateAsync(sharedHash: true);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(f.Files.Profile);
        string root = await RootAsync(reader);
        string original = Assert.Single((await MediaAssetReader.OpenAsync(root)).GetPage()).Path;
        string derived = await DerivedPathAsync(root);
        NativeTransferArchiveSnapshot snapshot = Frozen(await FreezeAsync(reader, new(true, true, true)));
        Assert.Single(snapshot.Payloads); // Dedup must not erase either physical origin's checks.
        string path = derivedOrigin ? derived : original;
        File.Delete(path);
        File.CreateSymbolicLink(path, derivedOrigin ? original : derived);
        using MemoryStream output = new();
        TransferArchiveWriteResult result = await NativeTransferArchiveExporter.WriteAsync(snapshot, output);
        Assert.Equal(TransferArchiveError.InvalidMedia, result.Error);
        Assert.False(result.IsComplete);
        Assert.Null(result.Package);
        Assert.NotNull(new FileInfo(path).LinkTarget);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task NonselectedRolesAreOmittedWithoutVerifyingTheirPoisonedBytes(bool originals, bool derived)
    {
        await using NativeV2ImportFixture f = await CreateAsync();
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(f.Files.Profile);
        string root = await RootAsync(reader);
        string originalPath = Assert.Single((await MediaAssetReader.OpenAsync(root)).GetPage()).Path;
        string derivedPath = await DerivedPathAsync(root);
        if (!originals) await PoisonAsync(originalPath);
        if (!derived) await PoisonAsync(derivedPath);
        NativeTransferArchiveSnapshot snapshot = Frozen(await FreezeAsync(reader, new(true, originals, derived)));
        TransferAttachment attachment = Attachment(snapshot.Catalog);
        TransferDerivedRepresentation representation = Assert.Single(attachment.DerivedRepresentations);
        Assert.Equal(originals ? TransferContentStatus.Contained : TransferContentStatus.Omitted, attachment.Status);
        Assert.Equal(derived ? TransferContentStatus.Contained : TransferContentStatus.Omitted, representation.Status);
        if (!originals) Assert.Null(attachment.PayloadSha256);
        if (!derived) Assert.Null(representation.PayloadSha256);
        Assert.Equal(NativeV2ImportFixture.Attachment().OriginalByteVersion, attachment.OriginalByteVersion);
        Assert.Equal(attachment.OriginalByteVersion, representation.SourceByteVersion);
        Assert.Equal((originals ? 1 : 0) + (derived ? 1 : 0), snapshot.Payloads.Length);
        using MemoryStream output = new();
        Assert.True((await NativeTransferArchiveExporter.WriteAsync(snapshot, output)).IsComplete);
        if (!originals) Assert.Equal(new byte[] { 0xff }, await File.ReadAllBytesAsync(originalPath));
        if (!derived) Assert.Equal(new byte[] { 0xff }, await File.ReadAllBytesAsync(derivedPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DerivedWithoutInventoryIsRejectedByBothOverloads(bool selection)
    {
        await using NativeV2ImportFixture f = await CreateAsync();
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(f.Files.Profile);
        NativeTransferArchiveMediaOptions options = new(false, false, true);
        TransferArchiveFreezeResult result = selection
            ? await NativeTransferArchiveExporter.FreezeAsync(reader, new NativeTransferArchiveSelection([NativeV2ImportFixture.Id]),
                NativeV2ImportFixture.At, PackageId, options)
            : await FreezeAsync(reader, options);
        Assert.False(result.IsFrozen);
        Assert.Null(result.Snapshot);
        Assert.NotEqual(TransferArchiveError.None, result.Error);
    }

    [Fact]
    public async Task LegacyTrueStillRejectsManifest6AndFalseIgnoresPoisonedManifest()
    {
        await using NativeV2ImportFixture f = await CreateAsync();
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(f.Files.Profile);
        TransferArchiveFreezeResult legacy = await NativeTransferArchiveExporter.FreezeAsync(reader,
            NativeV2ImportFixture.Id, NativeV2ImportFixture.At, PackageId, includeOriginals: true);
        Assert.Equal(TransferArchiveError.InvalidMedia, legacy.Error);
        Assert.Null(legacy.Snapshot);
        string path = Path.Combine(await RootAsync(reader), ManifestName);
        await File.WriteAllTextAsync(path, "{ POISONED-SYNTHETIC-MANIFEST");
        NativeTransferArchiveSnapshot snapshot = Frozen(await NativeTransferArchiveExporter.FreezeAsync(reader,
            NativeV2ImportFixture.Id, NativeV2ImportFixture.At, PackageId, includeOriginals: false));
        Assert.Empty(snapshot.Manifests);
        Assert.Empty(snapshot.Payloads);
        Assert.Empty(Attachment(snapshot.Catalog).DerivedRepresentations);
        using MemoryStream output = new();
        Assert.True((await NativeTransferArchiveExporter.WriteAsync(snapshot, output)).IsComplete);
        Assert.Equal("{ POISONED-SYNTHETIC-MANIFEST", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task UnsafeHistoricalEvidenceRejectsAsInvalidCatalogBeforeSelectedMediaVerification()
    {
        await using NativeV2ImportFixture f = await CreateAsync();
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(f.Files.Profile);
        string root = await RootAsync(reader);
        MediaAssetSnapshot media = await MediaAssetReader.OpenAsync(root);
        MediaOriginalEvidence current = Assert.Single(media.OriginalEvidence);
        const string secret = "SYNTHETIC-W32B4-SECRET";
        MediaProviderMetadata provider = new("retained-999", "original",
            "https://i.4cdn.org/b/999.webm?token=" + secret, "historical.webm",
            NativeV2ImportFixture.Original.Length, NativeV2ImportFixture.Md5(NativeV2ImportFixture.Original),
            NativeV2ImportFixture.Hash(NativeV2ImportFixture.Original));
        string key = MediaRevisionKeys.CreateAttachmentKey(NativeV2ImportFixture.Id, "999", 0, provider);
        CapturedAttachment metadata = new(0, provider.MediaId, provider.Variant, provider.Url,
            provider.OriginalFileName, ExpectedSize: provider.ExpectedSize, ExpectedMd5: provider.ExpectedMd5)
            { ExpectedSha256 = provider.ExpectedSha256, AttachmentRevisionKey = key };
        MediaOriginalEvidence historical = new(new(NativeV2ImportFixture.Id, "999", 0, key), metadata,
            MediaObservedContentStatus.Omitted, current.OriginalByteVersion, current.Origin);
        string manifestPath = Path.Combine(root, ManifestName);
        JsonObject manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        manifest["OriginalEvidence"] = JsonSerializer.SerializeToNode(new[] { current, historical });
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
        // Local storage can retain this observation; public transfer must not publish the token.
        MediaAssetSnapshot poisoned = await MediaAssetReader.OpenAsync(root);
        Assert.Equal(MediaManifestStatus.Available, poisoned.Status);
        Assert.Equal(2, poisoned.OriginalEvidence.Length);
        await PoisonAsync(Assert.Single(media.GetPage()).Path);
        await PoisonAsync(await DerivedPathAsync(root));

        TransferArchiveFreezeResult result = await FreezeAsync(reader, new(true, true, true));
        // InvalidMedia would mean the poisoned payload won before public privacy admission.
        Assert.Equal(TransferArchiveError.InvalidCatalog, result.Error);
        Assert.Null(result.Snapshot);
        Assert.DoesNotContain(secret, result.ToString());
        Assert.DoesNotContain(f.Files.Profile, result.ToString());
    }

    private static Task PoisonAsync(string path) => File.WriteAllBytesAsync(path, [0xff]);
    private static TransferAttachment Attachment(NativeTransferCatalog catalog)
        => Assert.Single(catalog.Threads[0].Posts.Single(p => p.Metadata.PostId == "15").Attachments);
    private static NativeTransferArchiveSnapshot Frozen(TransferArchiveFreezeResult result)
    {
        Assert.True(result.IsFrozen, result.ToString());
        return Assert.IsType<NativeTransferArchiveSnapshot>(result.Snapshot);
    }
    private static Task<TransferArchiveFreezeResult> FreezeAsync(ArchiveReader reader, NativeTransferArchiveMediaOptions options)
        => NativeTransferArchiveExporter.FreezeAsync(reader, NativeV2ImportFixture.Id,
            NativeV2ImportFixture.At, PackageId, options);
    private static async Task<string> RootAsync(ArchiveReader reader)
        => Assert.Single(Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(NativeV2ImportFixture.Id)).MediaDirectories);
    private static async Task<string> DerivedPathAsync(string root)
        => Path.Combine(root, Assert.Single((await MediaAssetReader.OpenAsync(root)).DerivedRepresentations).Blob!.RelativeFileName);

    private static async Task<NativeV2ImportFixture> CreateAsync(bool sharedHash = false)
    {
        NativeTransferCatalog content = NativeV2ImportFixture.Content(presence: false);
        if (sharedHash)
        {
            TransferAttachment attachment = Attachment(content);
            attachment = attachment with { DerivedRepresentations =
                [attachment.DerivedRepresentations[0] with { PayloadSha256 = attachment.PayloadSha256 }] };
            content = content with { Threads = [content.Threads[0] with { Posts = [NativeV2ImportFixture.Post("15", attachment)] }] };
        }
        NativeV2ImportFixture f = await NativeV2ImportFixture.CreateAsync(content,
            sharedHash ? [NativeV2ImportFixture.Original] : [NativeV2ImportFixture.Original, NativeV2ImportFixture.Preview]);
        try
        {
            using ProfileAdmissionToken admission = await f.ExclusiveAsync();
            var outcome = await f.CommitAsync(admission);
            Assert.Equal(ProfileExclusiveDisposition.SafeToResume, outcome.Disposition);
            Assert.Equal(NativeMediaImportCommitStatus.Committed, outcome.Result.Status);
            await f.Stage.DisposeAsync();
            return f;
        }
        catch { await f.DisposeAsync(); throw; }
    }
}
