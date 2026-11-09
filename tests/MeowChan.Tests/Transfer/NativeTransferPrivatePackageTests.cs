using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MeowChan.Archive;
using MeowChan.Favorites;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

// Whole-package integration. Every byte/profile here is synthetic.
public sealed class NativeTransferPrivatePackageTests
{
    private static readonly ResourceId Thread = new("4chan", "b", "7001");
    private static readonly Guid PackageId = Guid.Parse("514cdd34-d75d-484a-9a84-842ec928365a");
    private static readonly DateTimeOffset At = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Media = Encoding.UTF8.GetBytes("synthetic transferred original");
    private const string Note = "PRIVATE NOTE must stay out of public catalog and diagnostics";
    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static NativeTransferCatalog Catalog()
    {
        CapturedAttachment metadata = new(0, "7001001", "original", "https://i.4cdn.org/b/7001001.jpg", "synthetic.jpg");
        CapturedPost post = new("1", 1, PostFieldPresence.All, At, "", null, "public synthetic post", false, [metadata]);
        return new(At, [new(Thread, "https://boards.4chan.org/b/thread/7001", "synthetic", ThreadSourceState.NotFound,
            At, At, 1, true, [new(post, [new(metadata, TransferMediaRole.Original, TransferContentStatus.Contained, Hash(Media), null)])])]);
    }

    private static NativePrivateMetadataProjection Private() => new([Thread], new(true, true, true),
        [Thread], [new FavoriteAssetKey(Thread, "7001001", "original", Hash(Media))],
        [new PostBookmark(new(Thread, "1"), Note)]);

    private static async Task<MemoryStream> WriteAsync(NativePrivateMetadataProjection? projection = null, Guid? packageId = null)
    {
        MemoryStream output = new();
        TransferPayloadSource source = new(new(Media.Length, Hash(Media)),
            _ => Task.FromResult<Stream>(new MemoryStream(Media, writable: false)));
        TransferWriteResult result = await NativeTransferPackageWriter.WriteAsync(output, Catalog(), [source], packageId ?? PackageId,
            privateMetadata: projection);
        Assert.True(result.IsComplete, result.Error.ToString());
        return output;
    }

    [Fact]
    public async Task PrivateRoundtripPreservesPublic404StateAndRequiresReceivingSelectionAgain()
    {
        using MemoryStream bytes = await WriteAsync(Private());
        bytes.Position = 9;
        TransferPackageInspection inspected = await NativeTransferPackageValidator.InspectAsync(bytes);
        Assert.True(inspected.IsPackageValid);
        Assert.Equal(9, bytes.Position);
        Assert.True(bytes.CanWrite);
        NativeTransferPackage package = inspected.Package!;
        Assert.Equal(ThreadSourceState.NotFound, package.Catalog.Threads[0].SourceState);
        NativePrivateMetadataProjection received = Assert.IsType<NativePrivateMetadataProjection>(package.PrivateMetadata);
        Assert.False(NativeTransferImportPreview.Create(package).HasPreview);
        Assert.Equal(Note, Assert.Single(received.PostBookmarksAndNotes).Note);
        Assert.Equal(Assert.Single(Private().MediaFavorites), Assert.Single(received.MediaFavorites));
        NativePrivateMetadataProjection unselected = received.Select();
        Assert.Equal(default, unselected.Selection);
        Assert.Empty(unselected.ThreadFavorites);
        Assert.Empty(unselected.MediaFavorites);
        Assert.Empty(unselected.PostBookmarksAndNotes);
        Assert.Equal(Thread, Assert.Single(unselected.Threads));
        Assert.DoesNotContain(Note, Encoding.UTF8.GetString(package.Envelope.CatalogBytes.AsSpan()));
        Assert.DoesNotContain(Note, inspected.ToString());
    }

    [Fact]
    public async Task PrivateNotesAreNotPublicCatalogFieldsOrMediaPayloads()
    {
        using MemoryStream bytes = await WriteAsync(Private());
        using ZipArchive zip = new(bytes, ZipArchiveMode.Read, leaveOpen: true);
        Assert.Equal(new[] { "catalog.json", "manifest.json", "payload/" + Hash(Media), "private.json" },
            zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal));
        using StreamReader catalog = new(zip.GetEntry("catalog.json")!.Open());
        Assert.DoesNotContain(Note, await catalog.ReadToEndAsync());
        using StreamReader manifest = new(zip.GetEntry("manifest.json")!.Open());
        string text = await manifest.ReadToEndAsync();
        Assert.DoesNotContain(Note, text);
        JsonObject metadata = JsonNode.Parse(text)!.AsObject();
        Assert.Equal(2, metadata["envelopeVersion"]!.GetValue<int>());
        Assert.Equal(4, zip.Entries.Count);
        Assert.Single(metadata["payloads"]!.AsArray());
        Assert.NotNull(metadata["privateMetadata"]);
    }

    [Fact]
    public async Task PublicOnlyPackageRemainsV1AndHasNoPrivateComponent()
    {
        using MemoryStream bytes = await WriteAsync();
        TransferPackageInspection inspected = await NativeTransferPackageValidator.InspectAsync(bytes);
        Assert.True(inspected.IsPackageValid);
        Assert.Null(inspected.Package!.PrivateMetadata);
        Assert.Null(inspected.Package.Envelope.PrivateMetadata);
        Assert.True(NativeTransferImportPreview.Create(inspected.Package).HasPreview);
        Assert.False(NativeTransferImportPreview.Create(inspected.Package with { PrivateMetadata = Private() }).HasPreview);
        Assert.False(NativeTransferImportPreview.Create(inspected.Package with
            { Envelope = inspected.Package.Envelope with { EnvelopeVersion = 2 } }).HasPreview);
        Assert.False(NativeTransferImportPreview.Create(inspected.Package with
            { Envelope = inspected.Package.Envelope with { PrivateMetadataBytes = default } }).HasPreview);
        using ZipArchive zip = new(bytes, ZipArchiveMode.Read, leaveOpen: true);
        Assert.Null(zip.GetEntry("private.json"));
        using StreamReader reader = new(zip.GetEntry("manifest.json")!.Open());
        JsonObject manifest = JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject();
        Assert.Equal(1, manifest["envelopeVersion"]!.GetValue<int>());
        Assert.False(manifest.ContainsKey("privateMetadata"));
    }

    [Fact]
    public async Task HonestIntegrityDoesNotAdmitPrivateReferencesToAbsentPublicPosts()
    {
        using MemoryStream bytes = await WriteAsync(Private());
        using MemoryStream altered = Rewrite(bytes, root => root["postBookmarksAndNotes"]![0]!["post"] = "999");
        Assert.True((await NativeTransferEnvelopeValidator.InspectAsync(altered)).IsEnvelopeValid);
        TransferPackageInspection result = await NativeTransferPackageValidator.InspectAsync(altered);
        Assert.False(result.IsPackageValid);
        Assert.Null(result.Package);
        Assert.Equal(TransferEnvelopeError.None, result.EnvelopeError);
        Assert.DoesNotContain(Note, result.ToString());
    }

    [Theory]
    [InlineData("mediaId", "DIFFERENT")]
    [InlineData("variant", "preview")]
    [InlineData("sha256", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task HonestIntegrityDoesNotAuthorizeFavoriteRekey(string field, string value)
    {
        using MemoryStream bytes = await WriteAsync(Private());
        using MemoryStream altered = Rewrite(bytes, root => root["mediaFavorites"]![0]![field] = value);
        Assert.True((await NativeTransferEnvelopeValidator.InspectAsync(altered)).IsEnvelopeValid);
        TransferPackageInspection result = await NativeTransferPackageValidator.InspectAsync(altered);
        Assert.False(result.IsPackageValid);
        Assert.Null(result.Package);
        Assert.DoesNotContain(Note, result.ToString());
    }

    [Theory]
    [InlineData("unknown-field")]
    [InlineData("unsupported-schema")]
    [InlineData("missing-field")]
    [InlineData("default-off")]
    [InlineData("extra-thread")]
    public async Task IntegrityConsistentComponentStillMustPassStrictPrivateAdmission(string mutation)
    {
        using MemoryStream bytes = await WriteAsync(Private());
        using MemoryStream altered = Rewrite(bytes, root =>
        {
            switch (mutation)
            {
                case "unknown-field": root["PRIVATE UNKNOWN"] = Note; break;
                case "unsupported-schema": root["schema"] = "meowchan-private-v99"; break;
                case "missing-field": root.Remove("mediaFavorites"); break;
                case "default-off":
                    root["threadFavorites"] = null; root["mediaFavorites"] = null; root["postBookmarksAndNotes"] = null;
                    break;
                case "extra-thread":
                    root["threads"]!.AsArray().Add(new JsonObject { ["source"] = "4chan", ["board"] = "b", ["thread"] = "7002" });
                    break;
            }
        });
        Assert.True((await NativeTransferEnvelopeValidator.InspectAsync(altered)).IsEnvelopeValid);
        TransferPackageInspection result = await NativeTransferPackageValidator.InspectAsync(altered);
        Assert.False(result.IsPackageValid);
        Assert.Null(result.Package);
        Assert.Equal(TransferEnvelopeError.None, result.EnvelopeError);
        Assert.NotEqual(NativePrivateMetadataCodecError.None, result.PrivateMetadataError);
        Assert.DoesNotContain(Note, result.ToString());
    }

    [Fact]
    public async Task StageAcceptsPrivatePackageAndRemovesItsPendingCopy()
    {
        string parent = Path.Combine(Path.GetTempPath(), "meowchan-private-package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        try
        {
            using MemoryStream bytes = await WriteAsync(Private());
            NativeTransferStageResult result = await NativeTransferImportStage.CreateAsync(bytes, parent);
            Assert.Equal(NativeTransferStageError.None, result.Error);
            Assert.NotNull(result.Stage);
            Assert.False(result.StageRetained);
            await result.Stage!.DisposeAsync();
            Assert.Empty(Directory.EnumerateFileSystemEntries(parent));
            Assert.True(bytes.CanRead);
            // The same store path retains its public-only ingress behavior.
            using MemoryStream plain = await WriteAsync();
            NativeTransferStageResult accepted = await NativeTransferImportStage.CreateAsync(plain, parent);
            Assert.Equal(NativeTransferStageError.None, accepted.Error);
            Assert.NotNull(accepted.Stage);
            await accepted.Stage!.DisposeAsync();
            Assert.Empty(Directory.EnumerateFileSystemEntries(parent));
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public async Task ImportAdmissionDefaultsToPublicOnlyAndRequiresBoundOwnerConsentForPrivateSelection()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);

        using MemoryStream bytes = await WriteAsync(Private());
        NativeTransferStageResult staged = await NativeTransferImportStage.CreateAsync(bytes, directory.Path);
        NativeTransferImportStage stage = Assert.IsType<NativeTransferImportStage>(staged.Stage);
        await using (stage)
        {
            NativeMediaImportPreparation publicPreparation =
                await catalog.PrepareNativeMediaImportAsync(stage);
            NativeMediaImportPlan publicPlan = Assert.IsType<NativeMediaImportPlan>(publicPreparation.Plan);
            Assert.Equal(NativeMediaImportError.None, publicPreparation.Error);
            Assert.True(publicPlan.PrivateConsent.IsPublicOnly);
            Assert.Null(publicPlan.SelectedPrivateMetadata);
            Assert.Throws<InvalidDataException>(() =>
                publicPlan.CreatePrivateApplyRequest(null!, null!, default));

            NativePrivateMetadataSelection selection = new(ThreadFavorites: true, PostBookmarksAndNotes: true);
            NativePrivateConsentDecision unissued = NativePrivateConsentDecision.Create(stage.Package,
                NativePrivateConsentProvenance.FromPackage(stage.Package),
                NativePrivateConsentScope.ExplicitPrivateMetadata, selection);
            NativeMediaImportPreparation unissuedPreparation =
                await catalog.PrepareNativeMediaImportAsync(stage, unissued);
            Assert.Equal(NativeMediaImportError.PrivateConsentNotIssued, unissuedPreparation.Error);
            Assert.Null(unissuedPreparation.Plan);

            NativePrivateConsentDecision issued = catalog.IssueNativePrivateConsentDecision(stage,
                NativePrivateConsentScope.ExplicitPrivateMetadata, selection);
            NativeMediaImportPreparation privatePreparation =
                await catalog.PrepareNativeMediaImportAsync(stage, issued);
            NativeMediaImportPlan privatePlan = Assert.IsType<NativeMediaImportPlan>(privatePreparation.Plan);
            Assert.Equal(NativeMediaImportError.None, privatePreparation.Error);
            Assert.Equal(selection, privatePlan.PrivateConsent.Selection);
            NativePrivateMetadataProjection selected = Assert.IsType<NativePrivateMetadataProjection>(
                privatePlan.SelectedPrivateMetadata);
            Assert.Equal(selection, selected.Selection);
            ResourceId selectedThread = Assert.Single(selected.ThreadFavorites);
            Assert.Equal(Thread.Source, selectedThread.Source);
            Assert.Equal(Thread.Board, selectedThread.Board);
            Assert.Equal(Thread.Thread, selectedThread.Thread);
            Assert.Single(selected.PostBookmarksAndNotes);
            Assert.Empty(selected.MediaFavorites);

            using MemoryStream otherBytes = await WriteAsync(Private(),
                Guid.Parse("fd2b8e89-5f7f-4b59-a6d8-8166b0f83a54"));
            NativeTransferStageResult otherStaged =
                await NativeTransferImportStage.CreateAsync(otherBytes, directory.Path);
            NativeTransferImportStage otherStage = Assert.IsType<NativeTransferImportStage>(otherStaged.Stage);
            await using (otherStage)
            {
                NativeMediaImportPreparation stalePreparation =
                    await catalog.PrepareNativeMediaImportAsync(otherStage, issued);
                Assert.Equal(NativeMediaImportError.PrivateConsentNotIssued, stalePreparation.Error);
                Assert.Null(stalePreparation.Plan);
            }
        }
    }

    [Fact]
    public async Task TextOnlyImporterAlsoRejectsPrivateComponentsWithoutIssuingAPlan()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        TransferThread thread = Catalog().Threads[0];
        CapturedPost metadata = thread.Posts[0].Metadata with
        { Fields = PostFieldPresence.Html, PostedAt = null, Subject = null, Author = null, Attachments = null };
        NativeTransferCatalog text = new(At, [thread with { Posts = [new(metadata, [])] }]);
        NativePrivateMetadataProjection selected = new([Thread], new(PostBookmarksAndNotes: true), [], [],
            [new PostBookmark(new(Thread, "1"), Note)]);
        using MemoryStream bytes = new();
        Assert.True((await NativeTransferPackageWriter.WriteAsync(bytes, text, [], PackageId,
            privateMetadata: selected)).IsComplete);
        Assert.True((await NativeTransferPackageValidator.InspectAsync(bytes)).IsPackageValid);
        NativeTextImportPreparation rejected = await catalog.PrepareNativeTextImportAsync(bytes);
        Assert.Equal(NativeTextImportError.InvalidPackage, rejected.Error);
        Assert.Null(rejected.Plan);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        Assert.Null(await reader.ReadThreadAsync(Thread));
        Assert.Empty(await catalog.ReadNativeTextImportCandidatesAsync(Thread));
    }

    [Fact]
    public async Task CanceledInspectionThrowsAndDoesNotBecomeAConsentOrErrorReceipt()
    {
        using MemoryStream bytes = await WriteAsync(Private());
        using CancellationTokenSource cancel = new(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NativeTransferPackageValidator.InspectAsync(bytes, cancel.Token));
        Assert.True(bytes.CanRead);
        Assert.Equal(0, bytes.Position);
    }

    // Rebuild all ZIP CRCs and the private component descriptor. The mutation is
    // intentionally integrity-consistent so only semantic binding can reject it.
    private static MemoryStream Rewrite(MemoryStream source, Action<JsonObject> mutate)
    {
        Dictionary<string, byte[]> entries = new(StringComparer.Ordinal);
        using (ZipArchive zip = new(source, ZipArchiveMode.Read, leaveOpen: true))
            foreach (ZipArchiveEntry item in zip.Entries)
            {
                using Stream read = item.Open(); using MemoryStream data = new(); read.CopyTo(data);
                entries.Add(item.FullName, data.ToArray());
            }
        JsonObject projection = JsonNode.Parse(entries["private.json"])!.AsObject(); mutate(projection);
        entries["private.json"] = Encoding.UTF8.GetBytes(projection.ToJsonString());
        JsonObject manifest = JsonNode.Parse(entries["manifest.json"])!.AsObject();
        manifest["privateMetadata"] = new JsonObject { ["length"] = entries["private.json"].Length, ["sha256"] = Hash(entries["private.json"]) };
        entries["manifest.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        MemoryStream result = new();
        using (ZipArchive zip = new(result, ZipArchiveMode.Create, leaveOpen: true))
            foreach ((string name, byte[] value) in entries)
            { using Stream output = zip.CreateEntry(name, CompressionLevel.NoCompression).Open(); output.Write(value); }
        result.Position = 0; return result;
    }
}
