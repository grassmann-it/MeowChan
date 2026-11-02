using System.IO.Compression;
using System.Security.Cryptography;
using MeowChan.Archive;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tests.GoalFixtures;
using MeowChan.Transfer;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests.Transfer;

public sealed class NativeTransferArchiveExporterMediaTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 1, 11, 0, 0, TimeSpan.Zero);
    private static readonly Guid PackageId = Guid.Parse("e38c133f-6f20-45e0-b9b2-cb07acb47cac");
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    [Theory]
    [InlineData(ThreadSourceState.Archived, null)] [InlineData(ThreadSourceState.NotFound, false)] [InlineData(ThreadSourceState.NotFound, true)]
    public async Task RealArchiveAndMediaStoreProduceExactVerifiedOriginalsWithoutLegacyUpgrade(ThreadSourceState state, bool? order)
    {
        await using NativeTransferArchiveFixture fixture = await NativeTransferArchiveFixture.CreateAsync(state, order);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(fixture.ProfilePath);
        TransferArchiveFreezeResult frozen = await NativeTransferArchiveExporter.FreezeAsync(reader, fixture.ThreadId, At, PackageId);
        Assert.True(frozen.IsFrozen, frozen.Error.ToString());
        NativeTransferArchiveSnapshot snapshot = frozen.Snapshot!;
        Assert.Equal(2, snapshot.Payloads.Length);
        using MemoryStream stage = new();
        TransferArchiveWriteResult result = await NativeTransferArchiveExporter.WriteAsync(snapshot, stage);
        Assert.True(result.IsComplete, result.Error + " / " + result.WriteError);
        TransferThread thread = result.Package!.Catalog.Threads.Single();
        Assert.Equal(state, thread.SourceState); Assert.Equal(order, thread.DecimalPostOrder);
        Assert.Equal(fixture.ExpectedPostIds, thread.Posts.Select(p => p.Metadata.PostId));
        TransferAttachment strict = thread.Posts.Single(p => p.Metadata.PostId == NativeTransferArchiveFixture.StrictPostId).Attachments.Single();
        TransferAttachment legacy = thread.Posts.Single(p => p.Metadata.PostId == NativeTransferArchiveFixture.LegacyPostId).Attachments.Single();
        TransferAttachment missing = thread.Posts.Single(p => p.Metadata.PostId == NativeTransferArchiveFixture.UnavailablePostId).Attachments.Single();
        Assert.Equal(TransferContentStatus.Contained, strict.Status); Assert.Equal(TransferContentStatus.Contained, legacy.Status);
        Assert.Equal(TransferContentStatus.Unknown, missing.Status); // no manifest association, not a failed download
        Assert.Equal(fixture.StrictRevision, strict.Metadata.AttachmentRevisionKey); Assert.Equal("strict", strict.History!.Kind);
        Assert.Equal(fixture.StrictBinding.Acceptance.AcceptedAtUtc, strict.History.AcceptedAtUtc);
        Assert.Null(legacy.Metadata.AttachmentRevisionKey); Assert.Null(legacy.History);
        Assert.Equal(Hash(fixture.StrictBytes.Span), strict.PayloadSha256); Assert.Equal(Hash(fixture.LegacyBytes.Span), legacy.PayloadSha256);
        using (ZipArchive zip = new(stage, ZipArchiveMode.Read, leaveOpen: true))
        {
            foreach ((string hash, byte[] expected) in new[] { (strict.PayloadSha256!, fixture.StrictBytes.ToArray()), (legacy.PayloadSha256!, fixture.LegacyBytes.ToArray()) })
            {
                using Stream input = zip.GetEntry("payload/" + hash)!.Open(); using MemoryStream actual = new();
                await input.CopyToAsync(actual); Assert.Equal(expected, actual.ToArray());
            }
        }
        MediaAssetSnapshot media = await MediaAssetReader.OpenAsync(fixture.MediaDirectory);
        Assert.Equal(fixture.ManifestGeneration, media.Generation);
        Assert.Equal(fixture.StrictBytes.ToArray(), await File.ReadAllBytesAsync(fixture.StrictCommit.Path));
        Assert.Equal(fixture.LegacyBytes.ToArray(), await File.ReadAllBytesAsync(fixture.LegacyCommit.Path));
        Assert.Equal(fixture.CaptureRevision, (await reader.ReadThreadAsync(fixture.ThreadId))!.Summary.Revision);
        Assert.Equal(0, fixture.UnexpectedProviderCalls);
        string json = System.Text.Encoding.UTF8.GetString(result.Package.Envelope.CatalogBytes.AsSpan());
        Assert.DoesNotContain(fixture.ProfilePath, json);
        foreach (string forbidden in new[] { "plannedPath", "profileId", "pluginOwnerId", "policyRevision", "captureEnabled", "favorites" })
            Assert.DoesNotContain('"' + forbidden + '"', json, StringComparison.Ordinal);
    }
    [Fact]
    public async Task ResolvedButMissingOriginalRemainsUnavailableAndDoesNotTriggerRepair()
    {
        await using NativeTransferArchiveFixture fixture = await NativeTransferArchiveFixture.CreateAsync();
        File.Delete(fixture.StrictCommit.Path);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(fixture.ProfilePath);
        TransferArchiveFreezeResult frozen = await NativeTransferArchiveExporter.FreezeAsync(reader, fixture.ThreadId, At, PackageId);
        Assert.True(frozen.IsFrozen, frozen.Error.ToString());
        TransferAttachment strict = frozen.Snapshot!.Catalog.Threads[0].Posts.Single(p => p.Metadata.PostId == NativeTransferArchiveFixture.StrictPostId).Attachments.Single();
        Assert.Equal(TransferContentStatus.Unavailable, strict.Status); Assert.Null(strict.History); Assert.Null(strict.PayloadSha256);
        Assert.Single(frozen.Snapshot.Payloads);
        using MemoryStream stage = new(); Assert.True((await NativeTransferArchiveExporter.WriteAsync(frozen.Snapshot, stage)).IsComplete);
        Assert.False(File.Exists(fixture.StrictCommit.Path)); Assert.Equal(0, fixture.UnexpectedProviderCalls);
    }
    [Fact]
    public async Task ModifiedOriginalDoesNotBecomeContainedOrGetRepaired()
    {
        await using NativeTransferArchiveFixture fixture = await NativeTransferArchiveFixture.CreateAsync();
        byte[] changed = fixture.StrictBytes.ToArray(); changed[0] ^= 0xff; await File.WriteAllBytesAsync(fixture.StrictCommit.Path, changed);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(fixture.ProfilePath);
        TransferArchiveFreezeResult result = await NativeTransferArchiveExporter.FreezeAsync(reader, fixture.ThreadId, At, PackageId);
        Assert.False(result.IsFrozen); Assert.Null(result.Snapshot); Assert.Equal(changed, await File.ReadAllBytesAsync(fixture.StrictCommit.Path));
        Assert.DoesNotContain(fixture.ProfilePath, result.ToString()); Assert.Equal(0, fixture.UnexpectedProviderCalls);
    }
    [Fact]
    public async Task OriginalChangedAfterFreezeFailsExactReopenInsteadOfSubstitutingLatest()
    {
        await using NativeTransferArchiveFixture fixture = await NativeTransferArchiveFixture.CreateAsync();
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(fixture.ProfilePath);
        NativeTransferArchiveSnapshot snapshot = (await NativeTransferArchiveExporter.FreezeAsync(reader, fixture.ThreadId, At, PackageId)).Snapshot!;
        byte[] changed = fixture.StrictBytes.ToArray(); changed[0] ^= 0xff; await File.WriteAllBytesAsync(fixture.StrictCommit.Path, changed);
        using MemoryStream stage = new(); TransferArchiveWriteResult result = await NativeTransferArchiveExporter.WriteAsync(snapshot, stage);
        Assert.False(result.IsComplete); Assert.Null(result.Package); Assert.Null(result.BytesWritten);
        Assert.Equal(TransferArchiveError.InvalidMedia, result.Error); Assert.True(stage.CanWrite);
        Assert.Equal(changed, await File.ReadAllBytesAsync(fixture.StrictCommit.Path));
    }
    [Fact]
    public async Task ExtraRetainedReferenceRejectsEvenWhenBytesAlreadyShareAnIncludedPayloadHash()
    {
        await using NativeTransferArchiveFixture fixture = await NativeTransferArchiveFixture.CreateAsync();
        await CommitExtraAsync(fixture, "114", fixture.StrictBytes.ToArray(), Path.Combine(fixture.MediaDirectory, "114.jpg"));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(fixture.ProfilePath);
        TransferArchiveFreezeResult result = await NativeTransferArchiveExporter.FreezeAsync(reader, fixture.ThreadId, At, PackageId);
        Assert.Equal(TransferArchiveError.UnrepresentedAssets, result.Error); Assert.Null(result.Snapshot);
        Assert.Equal(3, (await MediaAssetReader.OpenAsync(fixture.MediaDirectory)).Count);
    }
    [Fact]
    public async Task ExactBoundNoncurrentAssetIsIncludedWhenEveryOtherRetainedAssetAlsoHasARealOccurrence()
    {
        await using NativeTransferArchiveFixture fixture = await NativeTransferArchiveFixture.CreateAsync();
        byte[] extraBytes = [9, 8, 7, 6, 5];
        await CommitExtraAsync(fixture, "114", extraBytes, fixture.StrictCommit.Path); // same requested filename, older binding stays exact
        using (ProfileLease lease = ProfileLease.Acquire(fixture.ProfilePath))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(fixture.ProfilePath, lease);
            CapturedAttachment metadata = new(0, "114", Url: "https://i.4cdn.org/b/114.jpg", ExpectedSize: extraBytes.Length,
                ExpectedMd5: Convert.ToBase64String(MD5.HashData(extraBytes)));
            CapturedPost post = new("7004", 4, PostFieldPresence.Attachments, Attachments: [metadata]);
            await catalog.CapturePreservingRevisionsAsync(fixture.Snapshot with { Posts = fixture.Posts.Append(post).ToArray() });
        }
        MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(fixture.MediaDirectory);
        Assert.False(manifest.FindById(fixture.StrictAssetId)!.IsCurrent);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(fixture.ProfilePath);
        TransferArchiveFreezeResult frozen = await NativeTransferArchiveExporter.FreezeAsync(reader, fixture.ThreadId, At, PackageId);
        Assert.True(frozen.IsFrozen, frozen.Error.ToString());
        using MemoryStream stage = new(); TransferArchiveWriteResult result = await NativeTransferArchiveExporter.WriteAsync(frozen.Snapshot!, stage);
        Assert.True(result.IsComplete, result.Error.ToString());
        Assert.Equal(3, result.Package!.Envelope.Payloads.Length);
        TransferAttachment strict = result.Package.Catalog.Threads[0].Posts.Single(p => p.Metadata.PostId == NativeTransferArchiveFixture.StrictPostId).Attachments.Single();
        Assert.Equal(Hash(fixture.StrictBytes.Span), strict.PayloadSha256); Assert.Equal(fixture.StrictRevision, strict.Metadata.AttachmentRevisionKey);
    }
    [Fact]
    public async Task ManifestGenerationChangeInvalidatesFreezeEvenIfOnlyJsonWhitespaceChanged()
    {
        await using NativeTransferArchiveFixture fixture = await NativeTransferArchiveFixture.CreateAsync();
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(fixture.ProfilePath);
        NativeTransferArchiveSnapshot snapshot = (await NativeTransferArchiveExporter.FreezeAsync(reader, fixture.ThreadId, At, PackageId)).Snapshot!;
        await File.AppendAllTextAsync(Path.Combine(fixture.MediaDirectory, ".meowchan-manifest.json"), "\n ");
        using MemoryStream stage = new(); TransferArchiveWriteResult result = await NativeTransferArchiveExporter.WriteAsync(snapshot, stage);
        Assert.Equal(TransferArchiveError.StaleSnapshot, result.Error); Assert.Null(result.Package); Assert.Equal(0, stage.Length);
    }
    [Fact]
    public async Task SymbolicLinkCannotBecomeAnOriginalEvenWithIdenticalBytes()
    {
        await using NativeTransferArchiveFixture fixture = await NativeTransferArchiveFixture.CreateAsync();
        string target = Path.Combine(fixture.ProfilePath, "link-target-synthetic.jpg");
        await File.WriteAllBytesAsync(target, fixture.StrictBytes.ToArray()); File.Delete(fixture.StrictCommit.Path);
        File.CreateSymbolicLink(fixture.StrictCommit.Path, target);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(fixture.ProfilePath);
        TransferArchiveFreezeResult result = await NativeTransferArchiveExporter.FreezeAsync(reader, fixture.ThreadId, At, PackageId);
        Assert.False(result.IsFrozen); Assert.Null(result.Snapshot); Assert.DoesNotContain(fixture.ProfilePath, result.ToString());
        Assert.NotNull(new FileInfo(fixture.StrictCommit.Path).LinkTarget);
    }
    [Theory]
    [InlineData("archive_posts")] [InlineData("archive_attachments")] [InlineData("archive_media_directories")]
    public async Task MaterializationBudgetIsCheckedInsideReadTransactionBeforeProjectingRows(string table)
    {
        await using NativeTransferArchiveFixture fixture = await NativeTransferArchiveFixture.CreateAsync();
        using (ProfileLease lease = ProfileLease.Acquire(fixture.ProfilePath))
        await using (SqliteConnection connection = new("Data Source=" + Path.Combine(fixture.ProfilePath, "archive.db")))
        {
            await connection.OpenAsync(); await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = table switch
            {
                "archive_posts" => "WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<100001) INSERT INTO archive_posts(source,board,thread,post,ordinal,fields,is_deleted) SELECT '4chan','b','7001',CAST(8000+x AS TEXT),x,0,0 FROM n",
                "archive_attachments" => "WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<100001) INSERT INTO archive_attachments(source,board,thread,post,position,media_id,variant,is_deleted) SELECT '4chan','b','7001','7001',x,'114','original',0 FROM n",
                _ => "WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<32) INSERT INTO archive_media_directories(source,board,thread,directory) SELECT '4chan','b','7001',$root||x FROM n"
            };
            if (table == "archive_media_directories") command.Parameters.AddWithValue("$root", Path.Combine(fixture.ProfilePath, "other-root-"));
            await command.ExecuteNonQueryAsync();
        }
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(fixture.ProfilePath);
        TransferArchiveFreezeResult result = await NativeTransferArchiveExporter.FreezeAsync(reader, fixture.ThreadId, At, PackageId);
        Assert.False(result.IsFrozen); Assert.Equal(TransferArchiveError.InvalidArchive, result.Error); Assert.Null(result.Snapshot);
    }
    [Fact]
    public async Task ManifestChangedDuringCopyInvalidatesAnOtherwiseValidStage()
    {
        await using NativeTransferArchiveFixture fixture = await NativeTransferArchiveFixture.CreateAsync();
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(fixture.ProfilePath);
        NativeTransferArchiveSnapshot snapshot = (await NativeTransferArchiveExporter.FreezeAsync(reader, fixture.ThreadId, At, PackageId)).Snapshot!;
        using OnFirstWrite stage = new(() => File.AppendAllText(Path.Combine(fixture.MediaDirectory, ".meowchan-manifest.json"), "\n "));
        TransferArchiveWriteResult result = await NativeTransferArchiveExporter.WriteAsync(snapshot, stage);
        Assert.Equal(TransferArchiveError.StaleSnapshot, result.Error); Assert.Null(result.Package); Assert.Null(result.BytesWritten);
        Assert.True(stage.Length > 0); Assert.True(stage.CanWrite);
        Assert.True((await NativeTransferPackageValidator.InspectAsync(stage)).IsPackageValid); // success-shaped bytes are NOT publication permission
    }
    [Fact]
    public async Task NullableOrderChangedWithoutSummaryRevisionChangeStillInvalidatesFreeze()
    {
        await using NativeTransferArchiveFixture fixture = await NativeTransferArchiveFixture.CreateAsync(decimalPostOrder: true);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(fixture.ProfilePath);
        NativeTransferArchiveSnapshot snapshot = (await NativeTransferArchiveExporter.FreezeAsync(reader, fixture.ThreadId, At, PackageId)).Snapshot!;
        using (ProfileLease lease = ProfileLease.Acquire(fixture.ProfilePath))
        await using (SqliteConnection connection = new("Data Source=" + Path.Combine(fixture.ProfilePath, "archive.db")))
        {
            await connection.OpenAsync(); await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE archive_source_contracts SET decimal_post_order=0 WHERE source='4chan' AND board='b' AND thread='7001'";
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        Assert.Equal(snapshot.View.Summary, (await reader.ReadThreadAsync(fixture.ThreadId))!.Summary);
        using MemoryStream stage = new(); TransferArchiveWriteResult result = await NativeTransferArchiveExporter.WriteAsync(snapshot, stage);
        Assert.Equal(TransferArchiveError.StaleSnapshot, result.Error); Assert.Null(result.Package); Assert.Equal(0, stage.Length);
    }
    [Fact]
    public async Task AggregateStoredTextIsRejectedBeforeArchiveStringsAreMaterialized()
    {
        await using NativeTransferArchiveFixture fixture = await NativeTransferArchiveFixture.CreateAsync();
        using (ProfileLease lease = ProfileLease.Acquire(fixture.ProfilePath))
        await using (SqliteConnection connection = new("Data Source=" + Path.Combine(fixture.ProfilePath, "archive.db")))
        {
            await connection.OpenAsync(); await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE archive_posts SET html=CAST(zeroblob(33554433) AS TEXT) WHERE source='4chan' AND board='b' AND thread='7001' AND post='7001'";
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(fixture.ProfilePath);
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadNativeTransferAsync(fixture.ThreadId, CancellationToken.None));
        Assert.Equal("The selected archive exceeds native transfer limits.", error.Message);
    }
    private sealed class OnFirstWrite(Action once) : MemoryStream
    {
        private Action? _once = once;
        private void Before() => Interlocked.Exchange(ref _once, null)?.Invoke();
        public override void Write(byte[] buffer, int offset, int count) { Before(); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Before(); base.Write(buffer); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { Before(); return base.WriteAsync(buffer, cancellationToken); }
    }
    private static async Task<MediaCommitResult> CommitExtraAsync(NativeTransferArchiveFixture fixture, string mediaId, byte[] bytes, string path)
    {
        using StubImageBoard board = new(new DelegateHandler((_, _) => throw new InvalidOperationException("No network in synthetic commit.")));
        DownloadItem item = new(new Uri("https://i.4cdn.org/b/" + mediaId + ".jpg"), path, board,
            board.GetThread(new Uri("https://stub.test/g/thread/7001")), mediaId, bytes.Length, Convert.ToBase64String(MD5.HashData(bytes)), "synthetic-extra.jpg");
        using MemoryStream input = new(bytes, false);
        return await new MediaStore().CommitAsync(item, input, CancellationToken.None);
    }
}
