using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tests.GoalFixtures;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

// Existing synthetic archive/store seeds are converted to the new representation.
// This proves readers/reexport, not a production media import/publisher or crash recovery.
public sealed class PassiveMediaReexportTests
{
    private static readonly DateTimeOffset Imported = new(2026, 11, 1, 18, 0, 0, TimeSpan.Zero);
    private const string Operation = "aa271c05-855c-402a-af6e-c325ac92e936";
    private static readonly string Fingerprint = new('d', 64);

    private static async Task<MediaAttachmentBinding> MakePassiveAsync(NativeTransferArchiveFixture fixture,
        bool withHistory, bool retained = false, string mode = "strict")
    {
        MediaAttachmentBinding active = fixture.StrictBinding;
        MediaProviderMetadata provider = mode switch
        {
            "sha" => active.Provider with { ExpectedSha1 = Convert.ToBase64String(new byte[20]), ExpectedSha256 = new string('0', 64) },
            "mismatch" => active.Provider with { ExpectedSize = active.Actual.Length + 1, ExpectedMd5 = Convert.ToBase64String(new byte[16]) },
            _ => active.Provider
        };
        ProviderIntegrityAssessment assessment = ProviderIntegrityAssessment.Assess(provider, active.Actual);
        MediaImportedHistory? history = withHistory ? new(assessment.IsStrictMatch ? MediaAcceptanceKind.Strict : MediaAcceptanceKind.ExplicitProviderMismatch,
            active.Acceptance.AcceptedAtUtc, assessment.SizeMismatch, assessment.Md5Mismatch) : null;
        string key = MediaRevisionKeys.CreateAttachmentKey(fixture.ThreadId, active.Reference.PostId, active.Reference.Position, provider);
        MediaAttachmentBinding passive = active with
        {
            Provider = provider, Reference = active.Reference with { RevisionKey = key },
            AcquisitionRevisionKey = MediaRevisionKeys.CreateAcquisitionKey(fixture.ThreadId, provider),
            Acceptance = new(MediaAcceptanceKind.PassiveImport, assessment.SizeMismatch, assessment.Md5Mismatch, Imported),
            PassiveImport = new(Operation, Fingerprint, Imported, mode == "derived" ? null : key, history)
        };
        string path = Path.Combine(fixture.MediaDirectory, ".meowchan-manifest.json");
        JsonNode root = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        root["Version"] = 5;
        root["AttachmentBindings"]!.AsObject().Remove(active.Reference.RevisionKey);
        root["AttachmentBindings"]![key] = JsonSerializer.SerializeToNode(passive);
        if (retained)
        {
            string retainedKey = MediaRevisionKeys.CreateAttachmentKey(fixture.ThreadId, "7999", passive.Reference.Position, provider);
            MediaAttachmentBinding retainedBinding = passive with
            {
                Reference = passive.Reference with { PostId = "7999", RevisionKey = retainedKey },
                PassiveImport = passive.PassiveImport! with { IncomingRevisionKey = mode == "derived" ? null : retainedKey }
            };
            root["AttachmentBindings"]![retainedKey] = JsonSerializer.SerializeToNode(retainedBinding);
        }
        await File.WriteAllTextAsync(path, root.ToJsonString());
        Assert.Equal(passive, (await MediaAssetReader.OpenAsync(fixture.MediaDirectory)).FindAttachmentBinding(key));
        if (provider != active.Provider)
        {
            using ProfileLease lease = ProfileLease.Acquire(fixture.ProfilePath);
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(fixture.ProfilePath, lease);
            CapturedAttachment changed = fixture.StrictAttachment with
            {
                ExpectedSize = provider.ExpectedSize, ExpectedMd5 = provider.ExpectedMd5,
                ExpectedSha1 = provider.ExpectedSha1, ExpectedSha256 = provider.ExpectedSha256, AttachmentRevisionKey = key
            };
            await catalog.CapturePreservingRevisionsAsync(fixture.Snapshot with
            {
                Posts = fixture.Posts.Select(post => post.PostId == NativeTransferArchiveFixture.StrictPostId
                    ? post with { Attachments = [changed] } : post).ToArray()
            });
        }
        return passive;
    }

    [Theory]
    [InlineData(false, false, "strict")] [InlineData(true, false, "strict")]
    [InlineData(false, true, "strict")] [InlineData(true, true, "strict")]
    [InlineData(false, false, "sha")] [InlineData(true, false, "sha")]
    [InlineData(false, true, "mismatch")] [InlineData(true, true, "mismatch")]
    [InlineData(false, false, "derived")] [InlineData(true, false, "derived")]
    public async Task CurrentAndRetainedReexportKeepOnlyOriginalHistoryAndExactBytes(bool retained, bool withHistory, string mode)
    {
        await using NativeTransferArchiveFixture fixture = await NativeTransferArchiveFixture.CreateAsync(ThreadSourceState.NotFound);
        MediaAttachmentBinding passive = await MakePassiveAsync(fixture, withHistory, retained, mode);
        byte[] before = await File.ReadAllBytesAsync(Path.Combine(fixture.MediaDirectory, ".meowchan-manifest.json"));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(fixture.ProfilePath);
        TransferArchiveFreezeResult freeze = await NativeTransferArchiveExporter.FreezeAsync(reader, fixture.ThreadId, Imported.AddHours(1), Guid.NewGuid());
        Assert.True(freeze.IsFrozen, freeze.Error.ToString());
        using MemoryStream output = new();
        TransferArchiveWriteResult write = await NativeTransferArchiveExporter.WriteAsync(freeze.Snapshot!, output);
        Assert.True(write.IsComplete, write.Error + "/" + write.WriteError);
        Assert.Equal(withHistory ? retained ? "chan-v2" : "chan-v1" : "chan-v3", write.Package!.Envelope.ContentSchema);
        TransferThread thread = Assert.Single(write.Package.Catalog.Threads);
        TransferAttachment attachment = retained
            ? Assert.Single(thread.RetainedBindings).Attachment
            : Assert.Single(Assert.Single(thread.Posts, post => post.Metadata.PostId == NativeTransferArchiveFixture.StrictPostId).Attachments);
        Assert.Equal(retained ? MediaRevisionKeys.CreateAttachmentKey(fixture.ThreadId, "7999", 0, passive.Provider) : passive.Reference.RevisionKey, attachment.Metadata.AttachmentRevisionKey);
        Assert.Equal(fixture.StrictCommit.Sha256, attachment.PayloadSha256);
        Assert.Equal(TransferContentStatus.Contained, attachment.Status);
        if (withHistory)
        {
            Assert.Equal(mode == "mismatch" ? "provider-mismatch" : "strict", attachment.History!.Kind);
            Assert.Equal(passive.PassiveImport!.History!.SizeMismatch, attachment.History.SizeMismatch);
            Assert.Equal(passive.PassiveImport.History.Md5Mismatch, attachment.History.Md5Mismatch);
            Assert.Equal(fixture.StrictBinding.Acceptance.AcceptedAtUtc, attachment.History.AcceptedAtUtc);
            Assert.NotEqual(Imported, attachment.History.AcceptedAtUtc);
        }
        else Assert.Null(attachment.History);
        if (retained) Assert.DoesNotContain(thread.Posts, post => post.Metadata.PostId == "7999");
        string json = Encoding.UTF8.GetString(write.Package.Envelope.CatalogBytes.AsSpan());
        Assert.DoesNotContain(Operation, json); Assert.DoesNotContain(Fingerprint, json);
        Assert.DoesNotContain("PassiveImport", json); Assert.DoesNotContain(fixture.ProfilePath, json);
        output.Position = 0;
        NativeTransferStageResult staged = await NativeTransferImportStage.CreateAsync(output, fixture.ProfilePath);
        Assert.NotNull(staged.Stage);
        await using (NativeTransferImportStage stage = staged.Stage!)
        {
            using MemoryStream copied = new();
            TransferMeasuredBlob measured = await stage.CopyPayloadToAsync(attachment.PayloadSha256!, copied);
            Assert.Equal(fixture.StrictBytes.ToArray(), copied.ToArray());
            Assert.Equal(passive.Actual.Length, measured.Length);
        }
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(fixture.MediaDirectory, ".meowchan-manifest.json")));
        Assert.Equal(0, fixture.UnexpectedProviderCalls);
        Assert.Equal(ThreadSourceState.NotFound, (await reader.ReadThreadAsync(fixture.ThreadId))!.Summary.SourceState);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task HtmlNamesPassiveOriginInsteadOfLocalSourcePermission(bool withHistory)
    {
        await using NativeTransferArchiveFixture fixture = await NativeTransferArchiveFixture.CreateAsync();
        await MakePassiveAsync(fixture, withHistory);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(fixture.ProfilePath);
        ArchiveHtmlExportResult html = await ArchiveHtmlExporter.ExportAsync(reader, fixture.ThreadId,
            Path.Combine(fixture.ProfilePath, "synthetic-html"));
        string text = await File.ReadAllTextAsync(html.IndexPath);
        Assert.Contains("Imported archive file; no local source permission is implied.", text);
        Assert.DoesNotContain("Saved with source-specific permission", text);
        Assert.DoesNotContain(Operation, text); Assert.DoesNotContain(Fingerprint, text);
        Assert.Equal(2, html.FileCount);
    }
}
