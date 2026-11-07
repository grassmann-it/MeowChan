using System.IO.Compression;
using System.Text.Json.Nodes;
using MeowChan.Archive;
using MeowChan.Persistence;
using MeowChan.Transfer;
using Microsoft.Data.Sqlite;
using static MeowChan.Tests.Transfer.NativeMediaImportTests;

namespace MeowChan.Tests.Transfer;

// A valid new wire schema is not permission for older materializers to discard
// fields they cannot preserve. Exercise real framing/staging and the actual APIs.
public sealed class NativeTransferV4ConsumerBoundaryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullOriginalV4CannotEnterAnOriginalOnlyMaterializer(bool existingTarget)
    {
        using Workspace files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog target = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        if (existingTarget)
            await target.RegisterThreadAsync(new(Id, Url, "existing target", Path.Combine(files.Profile, "inert"), false));
        NativeTransferCatalog content = Content();
        TransferThread thread = content.Threads[0];
        TransferPost post = thread.Posts[0];
        TransferAttachment original = post.Attachments[0] with
        {
            OriginalByteVersion = new(Bytes.Length, Hash(Bytes), Md5(Bytes), Sha1(Bytes))
        };
        content = content with { Threads = [thread with
        {
            Posts = thread.Posts.SetItem(0, post with { Attachments = [original] })
        }] };
        await using NativeTransferImportStage stage = await Stage(files, content);
        Assert.Equal("chan-v4", stage.Package.Envelope.ContentSchema);
        Assert.NotNull(stage.Package.Catalog.Threads[0].Posts[0].Attachments[0].OriginalByteVersion);
        await AssertMediaRejectedBeforeTargetRead(files, target, stage, existingTarget);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EqualHashContainedOriginalAndPreviewV4CannotEnterAnOriginalOnlyMaterializer(bool existingTarget)
    {
        using Workspace files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog target = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        if (existingTarget)
            await target.RegisterThreadAsync(new(Id, Url, "existing target", Path.Combine(files.Profile, "inert"), false));
        TransferAttachment original = Attachment() with
        {
            OriginalByteVersion = new(Bytes.Length, Hash(Bytes), Md5(Bytes), Sha1(Bytes))
        };
        original = original with
        {
            DerivedRepresentations = [new(TransferDerivedRepresentationKind.Preview,
                original.Metadata.AttachmentRevisionKey!, original.OriginalByteVersion!,
                TransferContentStatus.Contained, original.PayloadSha256)]
        };
        await using NativeTransferImportStage stage = await StageValidatedV4(files, WithAttachment(original), Bytes);
        TransferAttachment parsed = Assert.Single(stage.Package.Catalog.Threads[0].Posts[0].Attachments);
        Assert.Equal(TransferMediaRole.Original, parsed.Role);
        Assert.Equal(TransferContentStatus.Contained, parsed.Status);
        Assert.Equal(original.OriginalByteVersion, parsed.OriginalByteVersion);
        TransferDerivedRepresentation preview = Assert.Single(parsed.DerivedRepresentations);
        Assert.Equal(TransferDerivedRepresentationKind.Preview, preview.Kind);
        Assert.Equal(TransferContentStatus.Contained, preview.Status);
        Assert.Equal(parsed.Metadata.AttachmentRevisionKey, preview.SourceRevisionKey);
        Assert.Equal(parsed.OriginalByteVersion, preview.SourceByteVersion);
        Assert.Equal(parsed.PayloadSha256, preview.PayloadSha256);
        Assert.Equal(Hash(Bytes), Assert.Single(stage.Package.Envelope.Payloads).Sha256);
        Assert.Single(stage.Package.Envelope.MeasuredPayloads);
        using MemoryStream copied = new();
        TransferMeasuredBlob measured = await stage.CopyPayloadToAsync(parsed.PayloadSha256!, copied);
        Assert.Equal(Bytes, copied.ToArray());
        Assert.Equal(new TransferMeasuredBlob(Bytes.Length, Hash(Bytes), Md5(Bytes), Sha1(Bytes)), measured);
        // One physical blob is not permission to drop the separately bound Preview.
        await AssertMediaRejectedBeforeTargetRead(files, target, stage, existingTarget);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbsentOriginalWithKnownByteVersionV4IsEvidenceOnlyAndCannotEnterAnOriginalOnlyMaterializer(bool existingTarget)
    {
        using Workspace files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog target = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        if (existingTarget)
            await target.RegisterThreadAsync(new(Id, Url, "existing target", Path.Combine(files.Profile, "inert"), false));
        TransferAttachment original = Attachment() with
        {
            Status = TransferContentStatus.Omitted,
            PayloadSha256 = null,
            OriginalByteVersion = new(Bytes.Length, Hash(Bytes), Md5(Bytes), Sha1(Bytes))
        };
        await using NativeTransferImportStage stage = await StageValidatedV4(files, WithAttachment(original));
        TransferAttachment parsed = Assert.Single(stage.Package.Catalog.Threads[0].Posts[0].Attachments);
        Assert.Equal(TransferMediaRole.Original, parsed.Role);
        Assert.Equal(TransferContentStatus.Omitted, parsed.Status);
        Assert.Equal(original.OriginalByteVersion, parsed.OriginalByteVersion);
        Assert.Null(parsed.PayloadSha256);
        Assert.Null(parsed.History);
        Assert.Empty(parsed.DerivedRepresentations);
        Assert.Empty(stage.Package.Envelope.Payloads);
        Assert.Empty(stage.Package.Envelope.MeasuredPayloads);
        using MemoryStream absent = new();
        await Assert.ThrowsAsync<ArgumentException>(() => stage.CopyPayloadToAsync(parsed.OriginalByteVersion!.Sha256, absent));
        Assert.Equal(0L, absent.Length); // Evidence must not invent even an unpublished payload.
        await AssertMediaRejectedBeforeTargetRead(files, target, stage, existingTarget);
    }

    [Fact]
    public async Task TextOnlyV4IsRejectedByAdmittedWireVersionEvenWithoutNewFields()
    {
        using Workspace files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog target = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        TransferThread thread = Content().Threads[0];
        NativeTransferCatalog text = new(At, [thread with
        {
            Posts = [new(thread.Posts[1].Metadata, [])]
        }]);
        using MemoryStream older = new();
        Assert.True((await NativeTransferPackageWriter.WriteAsync(older, text, [], Guid.NewGuid())).IsComplete);
        using MemoryStream v4 = AsTextOnlyV4(older.ToArray());
        TransferPackageInspection inspection = await NativeTransferPackageValidator.InspectAsync(v4);
        Assert.True(inspection.IsPackageValid, inspection.ToString());
        Assert.Equal("chan-v4", inspection.Package!.Envelope.ContentSchema);
        // The DTO can downlevel to v1; the consumer must inspect the actual wire.
        Assert.Equal("chan-v1", NativeTransferCatalogSerializer.ContentSchema(inspection.Package.Catalog));
        Dictionary<string, string> before = Files(files.Profile);
        NativeTextImportPreparation result = await target.PrepareNativeTextImportAsync(v4);
        Assert.Equal(NativeTextImportError.InvalidPackage, result.Error);
        Assert.Null(result.Plan);
        Assert.Equal(before, Files(files.Profile));
        AssertNoOperations(files.Profile);
        await target.DisposeAsync();
        NativeTextImportPreparation rejected = await target.PrepareNativeTextImportAsync(v4);
        Assert.Equal(NativeTextImportError.InvalidPackage, rejected.Error);
        Assert.Null(rejected.Plan);
        Assert.Equal(before, Files(files.Profile));
    }

    private static NativeTransferCatalog WithAttachment(TransferAttachment attachment)
    {
        NativeTransferCatalog content = Content();
        TransferThread thread = content.Threads[0];
        TransferPost post = thread.Posts[0];
        return content with { Threads = [thread with
        {
            Posts = thread.Posts.SetItem(0, post with { Attachments = [attachment] })
        }] };
    }

    private static async Task<NativeTransferImportStage> StageValidatedV4(Workspace files,
        NativeTransferCatalog content, params byte[][] payloads)
    {
        using MemoryStream package = new();
        TransferPayloadSource[] sources = payloads.Select(bytes => new TransferPayloadSource(new(bytes.Length, Hash(bytes)),
            _ => Task.FromResult<Stream>(new MemoryStream(bytes, false)))).ToArray();
        TransferWriteResult written = await NativeTransferPackageWriter.WriteAsync(package, content, sources, Guid.NewGuid());
        Assert.True(written.IsComplete, written.ToString());
        Assert.True(written.Package!.IsChanV4);
        package.Position = 0;
        using (ZipArchive zip = new(package, ZipArchiveMode.Read, leaveOpen: true))
        {
            Assert.NotNull(zip.GetEntry("manifest.json"));
            Assert.NotNull(zip.GetEntry("catalog.json"));
            Assert.Equal(2 + payloads.Length, zip.Entries.Count);
            Assert.Equal(payloads.Select(bytes => "payload/" + Hash(bytes)).OrderBy(name => name, StringComparer.Ordinal),
                zip.Entries.Where(entry => entry.FullName.StartsWith("payload/", StringComparison.Ordinal))
                    .Select(entry => entry.FullName).OrderBy(name => name, StringComparer.Ordinal));
        }
        package.Position = 0;
        TransferPackageInspection inspection = await NativeTransferPackageValidator.InspectAsync(package);
        Assert.True(inspection.IsPackageValid, inspection.ToString());
        Assert.True(inspection.Package!.IsChanV4);
        Assert.Null(inspection.Package.Envelope.PrivateMetadata);
        package.Position = 0;
        NativeTransferStageResult staged = await NativeTransferImportStage.CreateAsync(package, files.StageRoot);
        Assert.Equal(NativeTransferStageError.None, staged.Error);
        NativeTransferImportStage stage = Assert.IsType<NativeTransferImportStage>(staged.Stage);
        Assert.True(stage.Package.IsChanV4);
        Assert.Equal("chan-v4", stage.Package.Envelope.ContentSchema);
        Assert.Equal(NativeTransferCatalogSerializer.Serialize(content), NativeTransferCatalogSerializer.Serialize(stage.Package.Catalog));
        return stage;
    }

    private static async Task AssertMediaRejectedBeforeTargetRead(Workspace files, ArchiveCatalog target,
        NativeTransferImportStage stage, bool existingTarget)
    {
        Dictionary<string, string> before = Files(files.Profile);
        string[] directoriesBefore = Directory.GetDirectories(files.Profile, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        if (existingTarget)
        {
            NativeMediaMergePreparation result = await target.PrepareNativeMediaMergeAsync(stage);
            Assert.Equal(NativeMediaMergeError.UnsupportedPackage, result.Error);
            Assert.Null(result.Plan);
        }
        else
        {
            NativeMediaImportPreparation result = await target.PrepareNativeMediaImportAsync(stage);
            Assert.Equal(NativeMediaImportError.UnsupportedPackage, result.Error);
            Assert.Null(result.Plan);
        }
        Assert.Equal(before, Files(files.Profile));
        Assert.Equal(directoriesBefore, Directory.GetDirectories(files.Profile, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal).ToArray());
        AssertNoOperations(files.Profile);
        // A disposed catalog is a target-read sentinel: wire admission must reject
        // before consulting target state or issuing any materialization authority.
        await target.DisposeAsync();
        if (existingTarget)
        {
            NativeMediaMergePreparation rejected = await target.PrepareNativeMediaMergeAsync(stage);
            Assert.Equal(NativeMediaMergeError.UnsupportedPackage, rejected.Error);
            Assert.Null(rejected.Plan);
        }
        else
        {
            NativeMediaImportPreparation rejected = await target.PrepareNativeMediaImportAsync(stage);
            Assert.Equal(NativeMediaImportError.UnsupportedPackage, rejected.Error);
            Assert.Null(rejected.Plan);
        }
        Assert.Equal(before, Files(files.Profile));
        Assert.Equal(directoriesBefore, Directory.GetDirectories(files.Profile, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal).ToArray());
        AssertNoOperations(files.Profile);
    }

    private static MemoryStream AsTextOnlyV4(byte[] bytes)
    {
        using MemoryStream input = new(bytes);
        using ZipArchive source = new(input, ZipArchiveMode.Read);
        JsonObject Read(string name)
        {
            using Stream stream = source.GetEntry(name)!.Open();
            return JsonNode.Parse(stream)!.AsObject();
        }
        JsonObject catalog = Read("catalog.json");
        catalog["schema"] = "chan-v4";
        foreach (JsonNode? thread in catalog["threads"]!.AsArray())
            thread!["retainedBindings"] = new JsonArray();
        byte[] catalogBytes = System.Text.Encoding.UTF8.GetBytes(catalog.ToJsonString());
        JsonObject manifest = Read("manifest.json");
        manifest["contentSchema"] = "chan-v4";
        manifest["requiredFeatures"] = new JsonArray("chan-v4");
        manifest["catalog"] = new JsonObject { ["length"] = catalogBytes.Length, ["sha256"] = Hash(catalogBytes) };
        MemoryStream output = new();
        using (ZipArchive zip = new(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (Stream entry = zip.CreateEntry("catalog.json", CompressionLevel.NoCompression).Open())
                entry.Write(catalogBytes);
            using (Stream entry = zip.CreateEntry("manifest.json", CompressionLevel.NoCompression).Open())
                entry.Write(System.Text.Encoding.UTF8.GetBytes(manifest.ToJsonString()));
        }
        output.Position = 0;
        return output;
    }

    private static void AssertNoOperations(string profile)
    {
        using SqliteConnection connection = ArchiveSql.OpenConnection(Path.Combine(profile, "archive.db"), SqliteOpenMode.ReadOnly);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT count(*) FROM archive_media_import_operations) + (SELECT count(*) FROM archive_media_merge_operations) + (SELECT count(*) FROM archive_import_candidates)";
        Assert.Equal(0L, command.ExecuteScalar());
    }
}
