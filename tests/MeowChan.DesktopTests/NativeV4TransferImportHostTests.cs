using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Storage;
using MeowChan.Transfer;
using Microsoft.Data.Sqlite;

namespace MeowChan.DesktopTests;

// The public host issues and consumes every plan. Hooks observe only; no manually
// entered admission, reflected Ready state, seeded SQL success, provider or personal data.
[Collection("Delivered plugin hosts")]
public sealed class NativeV4TransferImportHostTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(45);
    private static readonly ResourceId Id = new("4chan", "b", "7401");
    private static readonly DateTimeOffset At = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Original = Encoding.UTF8.GetBytes("W34 exact synthetic original, never preview bytes");
    private static readonly byte[] Preview = Encoding.UTF8.GetBytes("W34 synthetic preview, not a decoder fixture");
    private static readonly byte[] AddedOriginal = Encoding.UTF8.GetBytes("W34 independent added post original");
    private static readonly byte[] AddedPreview = Encoding.UTF8.GetBytes("W34 independent added post preview");
    private const string Html = "W34 retained source post";

    [Theory]
    [InlineData("unchanged")]
    [InlineData("overwrite")]
    [InlineData("delete")]
    public async Task PreviewOnlyReadyRetainsVerifiedV4AndWrongGenerationCannotWriteEvenAfterSourceMutation(string mutation)
    {
        using Workspace files = new();
        string source = await files.WritePackageAsync(Content(TransferContentStatus.Omitted), Preview);
        byte[] acceptedSource = File.ReadAllBytes(source);
        byte[]? replacement = null;
        if (mutation == "overwrite")
        {
            NativeTransferCatalog changed = Content(TransferContentStatus.Contained);
            TransferThread thread = changed.Threads[0];
            changed = changed with { Threads = [thread with { Posts = [thread.Posts[0] with
                { Metadata = thread.Posts[0].Metadata with { Html = "replacement must never be imported" } }] }] };
            replacement = File.ReadAllBytes(await files.WritePackageAsync(changed, Original, Preview));
        }
        await using (DesktopSessionHost host = files.CreateHost())
        {
            await StartAsync(host);
            CatalogState before = ReadState(files.Profile);
            int opens = 0, entries = 0, commits = 0;
            host.BeforeNativeImportCommitForTests = () => { Interlocked.Increment(ref entries); return Task.CompletedTask; };
            host.NativeImportCheckpointForTests = phase =>
            { if (phase == NativeMediaImportPhase.SqlCommitted) Interlocked.Increment(ref commits); };
            await using NativeTransferImportSession session = host.CreateNativeTransferImportSession(files.Work);
            session.BeforeSourceOpenForTests = _ => { Interlocked.Increment(ref opens); return Task.CompletedTask; };

            NativeTransferImportSnapshot ready = await session.InspectAsync(source).WaitAsync(Deadline);

            AssertReady(ready, new(Id, false, 1, 0, 1, 0, 1, 1, 1));
            TransferImportPreview inventory = Assert.IsType<TransferImportPreview>(ready.Inventory);
            Assert.Equal(1, inventory.CurrentOriginals.Omitted);
            Assert.Equal(0, inventory.CurrentOriginals.Contained);
            Assert.Equal(1, inventory.DerivedPreviews.Contained);
            Assert.Equal(0, inventory.CurrentPreviews.Total); // not the old Preview-role grammar
            Assert.Equal(1, inventory.UniquePayloads);
            Assert.Equal((long)Preview.Length, inventory.UniquePayloadBytes);
            string stage = AssertStage(files.Work, acceptedSource.LongLength);
            Assert.Equal(before, ReadState(files.Profile));
            await using (ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile).WaitAsync(Deadline))
                Assert.Null(await reader.ReadThreadAsync(Id).WaitAsync(Deadline));
            if (mutation == "overwrite") File.WriteAllBytes(source, replacement!);
            if (mutation == "delete") File.Delete(source);

            Assert.Equal(ready, await session.ApplyAsync(ready.Generation - 1).WaitAsync(Deadline));
            Assert.Equal(before, ReadState(files.Profile));
            Assert.True(File.Exists(stage));
            Assert.Equal(0, Volatile.Read(ref entries));
            Assert.Equal(0, Volatile.Read(ref commits));

            NativeTransferImportSnapshot done = await session.ApplyAsync(ready.Generation).WaitAsync(Deadline);

            AssertCompleted(done, ready.Generation, new(NativeMediaImportCommitStatus.Committed, 1, 1, 0, 1, 0, false, 1, 1, 1));
            Assert.Equal(1, Volatile.Read(ref opens));
            Assert.Equal(1, Volatile.Read(ref entries));
            Assert.Equal(1, Volatile.Read(ref commits));
            Assert.Equal(done, await session.ApplyAsync(ready.Generation).WaitAsync(Deadline));
            Assert.Equal(1, Volatile.Read(ref entries));
            Assert.Empty(Directory.GetFileSystemEntries(files.Work));
            AssertCounts(ReadState(files.Profile), 1, 1, 1, 1, 1, 0);
            AssertReceipt(files.Profile, false, done.Result!, originalBindings: 0);
            Assert.Equal(DesktopSessionState.Ready, host.State);
            if (mutation == "delete") Assert.False(File.Exists(source));
            else Assert.Equal(replacement ?? acceptedSource, File.ReadAllBytes(source));
        }

        // Restart the actual owner (and recovery), then dispose it before the offline export.
        await using (DesktopSessionHost restarted = files.CreateHost()) await StartAsync(restarted);
        ArchivedThreadView view = await ReadAsync(files.Profile);
        Assert.Equal(Html, Assert.Single(view.Posts).Html);
        Assert404(view);
        MediaAssetSnapshot[] roots = await ReadRootsAsync(view);
        MediaAssetSnapshot root = Assert.Single(roots);
        Assert.Empty(root.AttachmentBindings);
        Assert.Equal(0, root.Count);
        MediaOriginalEvidence evidence = Assert.Single(root.OriginalEvidence);
        Assert.Equal(MediaObservedContentStatus.Omitted, evidence.IncomingStatus);
        Assert.Null(evidence.OriginalByteVersion!.Sha1);
        Assert.Null(evidence.Metadata.ExpectedSha1);
        Assert.Null(MediaAttachmentResolver.Resolve(roots, Id, "15", evidence.Metadata));
        await AssertDerivedAsync(roots, "15", Preview);
        await AssertReexportAsync(files.Profile, hasOriginal: false, hasAdded: false);
    }

    [Theory]
    [InlineData((int)TransferContentStatus.Omitted)]
    [InlineData((int)TransferContentStatus.Unknown)]
    [InlineData((int)TransferContentStatus.Unavailable)]
    [InlineData((int)TransferContentStatus.Failed)]
    [InlineData((int)TransferContentStatus.Cancelled)]
    public async Task LaterExactOriginalAndIndependentPostSupplement404ThenFreshStageIsIdempotent(int priorStatus)
    {
        using Workspace files = new();
        string seed = await files.WritePackageAsync(Content((TransferContentStatus)priorStatus), Preview);
        NativeTransferCatalog incoming = Content(TransferContentStatus.Contained, addPost: true);
        // Incoming Active is descriptive only: the NotFound target must never resurrect.
        incoming = incoming with { Threads = [incoming.Threads[0] with { SourceState = ThreadSourceState.Active }] };
        string source = await files.WritePackageAsync(incoming, Original, Preview, AddedOriginal, AddedPreview);
        byte[] sourceBytes = File.ReadAllBytes(source);
        ArchivedThreadView before;
        MediaOriginalEvidence prior;
        Dictionary<string, string> priorFiles;
        await using (DesktopSessionHost host = files.CreateHost())
        {
            await StartAsync(host);
            await ImportAsync(host, files, seed, new(Id, false, 1, 0, 1, 0, 1, 1, 1),
                new(NativeMediaImportCommitStatus.Committed, 1, 1, 0, 1, 0, false, 1, 1, 1));
            before = await ReadAsync(files.Profile);
            MediaAssetSnapshot old = Assert.Single(await ReadRootsAsync(before));
            prior = Assert.Single(old.OriginalEvidence);
            Assert.Equal((MediaObservedContentStatus)priorStatus, prior.IncomingStatus);
            Assert.Null(prior.OriginalByteVersion!.Sha1);
            priorFiles = RootFiles(before.MediaDirectories);
            Assert.Empty(old.AttachmentBindings);
            Assert.Null(MediaAttachmentResolver.Resolve([old], Id, "15", prior.Metadata));
            CatalogState beforeInspect = ReadState(files.Profile);
            await using NativeTransferImportSession session = host.CreateNativeTransferImportSession(files.Work);

            NativeTransferImportSnapshot ready = await session.InspectAsync(source).WaitAsync(Deadline);

            // Existing evidence/preview are context, not fresh additions. Metadata is
            // already present for post 15: its new original is not a new SQL attachment.
            AssertReady(ready, new(Id, true, 1, 0, 1, 0, 1, 1, 1));
            Assert.Equal(2, ready.Inventory!.CurrentOriginals.Contained);
            Assert.Equal(2, ready.Inventory.DerivedPreviews.Contained);
            Assert.Equal(beforeInspect, ReadState(files.Profile));
            AssertStage(files.Work, sourceBytes.LongLength);
            NativeTransferImportSnapshot done = await session.ApplyAsync(ready.Generation).WaitAsync(Deadline);
            AssertCompleted(done, ready.Generation,
                new(NativeMediaImportCommitStatus.Committed, 0, 1, 0, 1, 0, false, 1, 1, 1));
            AssertReceipt(files.Profile, true, done.Result!, originalBindings: 2);
            AssertCounts(ReadState(files.Profile), 1, 2, 2, 2, 1, 1);
            Assert.Equal(sourceBytes, File.ReadAllBytes(source));
            Assert.Empty(Directory.GetFileSystemEntries(files.Work));
        }

        await using (DesktopSessionHost restarted = files.CreateHost())
        {
            await StartAsync(restarted);
            ArchivedThreadView merged = await ReadAsync(files.Profile);
            Assert.Equal(before.Summary with { Revision = before.Summary.Revision + 1, PostCount = 2 }, merged.Summary);
            Assert.Equal(new[] { "15", "17" }, merged.Posts.Select(p => p.PostId));
            Assert.Equal(Html, merged.Posts[0].Html);
            Assert.Equal("W34 independently added post", merged.Posts[1].Html);
            Assert404(merged);
            AssertFilesUnchanged(priorFiles);
            MediaAssetSnapshot[] roots = await ReadRootsAsync(merged);
            Assert.Equal(prior, Assert.Single(roots.Single(r => r.Directory == before.MediaDirectories[0]).OriginalEvidence));
            Assert.All(roots.SelectMany(r => r.OriginalEvidence), e => Assert.Null(e.OriginalByteVersion!.Sha1));
            await AssertOriginalAsync(merged, roots, "15", Original);
            await AssertOriginalAsync(merged, roots, "17", AddedOriginal);
            await AssertDerivedAsync(roots, "15", Preview);
            await AssertDerivedAsync(roots, "17", AddedPreview);
            Dictionary<string, string> activeFiles = RootFiles(merged.MediaDirectories);
            string[] operationsBefore = ReadMergeOperationIds(files.Profile);

            // New writer package ID, independent session/stage and owner-issued plan;
            // this is NOT same-generation Apply or a replay of a prior receipt.
            string duplicate = await files.WritePackageAsync(incoming, Original, Preview, AddedOriginal, AddedPreview);
            Assert.NotEqual(sourceBytes, File.ReadAllBytes(duplicate));
            await ImportAsync(restarted, files, duplicate, new(Id, true, 0, 0, 0, 0, 0, 0, 0),
                new(NativeMediaImportCommitStatus.Committed, 0, 0, 0, 0, 0, false, 0, 0, 0));
            string[] operationsAfter = ReadMergeOperationIds(files.Profile);
            string duplicateOperation = Assert.Single(operationsAfter.Except(operationsBefore));
            Assert.All(operationsBefore, id => Assert.Contains(id, operationsAfter));
            AssertReceipt(files.Profile, true,
                new(NativeMediaImportCommitStatus.Committed, 0, 0, 0, 0, 0, false, 0, 0, 0),
                originalBindings: 0, operation: duplicateOperation);
            ArchivedThreadView unchanged = await ReadAsync(files.Profile);
            Assert.Equal(merged.Summary, unchanged.Summary);
            Assert.Equal(merged.MediaDirectories, unchanged.MediaDirectories);
            AssertCounts(ReadState(files.Profile), 1, 2, 2, 2, 1, 2);
            AssertFilesUnchanged(activeFiles);
        }
        await using (DesktopSessionHost restartedAgain = files.CreateHost()) await StartAsync(restartedAgain);
        await AssertReexportAsync(files.Profile, hasOriginal: true, hasAdded: true);
        AssertFilesUnchanged(priorFiles);
        Assert.Equal(sourceBytes, File.ReadAllBytes(source));
    }

    [Fact]
    public async Task DerivedOnlyMergeReportsOneDerivedAdditionNotAnotherOriginalOrEvidence()
    {
        using Workspace files = new();
        string originalOnly = await files.WritePackageAsync(Content(TransferContentStatus.Contained, derived: false), Original);
        string withPreview = await files.WritePackageAsync(Content(TransferContentStatus.Contained), Original, Preview);
        ArchivedThreadView before;
        ResolvedMediaAttachment oldOriginal;
        Dictionary<string, string> oldFiles;
        await using (DesktopSessionHost host = files.CreateHost())
        {
            await StartAsync(host);
            // Real positive replacement of the former blanket v4 unsupported case.
            await ImportAsync(host, files, originalOnly, new(Id, false, 1, 0, 1, 0, 1, 0, 0),
                new(NativeMediaImportCommitStatus.Committed, 1, 1, 0, 1, 0, false, 1, 0, 0));
            before = await ReadAsync(files.Profile);
            oldOriginal = await AssertOriginalAsync(before, await ReadRootsAsync(before), "15", Original);
            oldFiles = RootFiles(before.MediaDirectories);
            await ImportAsync(host, files, withPreview, new(Id, true, 0, 0, 0, 0, 0, 1, 1),
                new(NativeMediaImportCommitStatus.Committed, 0, 0, 0, 0, 0, false, 0, 1, 1));
        }
        await using (DesktopSessionHost restarted = files.CreateHost()) await StartAsync(restarted);
        ArchivedThreadView after = await ReadAsync(files.Profile);
        Assert.Equal(before.Summary with { Revision = before.Summary.Revision + 1 }, after.Summary);
        MediaAssetSnapshot[] roots = await ReadRootsAsync(after);
        MediaAssetSnapshot derivedRoot = Assert.Single(roots, r => !before.MediaDirectories.Contains(r.Directory));
        Assert.Empty(derivedRoot.AttachmentBindings);
        Assert.Equal(0, derivedRoot.Count);
        Assert.Equal(1, roots.Sum(r => r.AttachmentBindings.Count));
        ResolvedMediaAttachment unchanged = await AssertOriginalAsync(after, roots, "15", Original);
        Assert.Equal(oldOriginal.Binding, unchanged.Binding);
        Assert.Equal(oldOriginal.Asset.AssetId, unchanged.Asset.AssetId);
        Assert.Equal(oldOriginal.Snapshot.Directory, unchanged.Snapshot.Directory);
        await AssertDerivedAsync(roots, "15", Preview);
        AssertFilesUnchanged(oldFiles);
        await AssertReexportAsync(files.Profile, hasOriginal: true, hasAdded: false);
    }

    private static async Task StartAsync(DesktopSessionHost host)
    {
        Assert.Null(host.Plugins);
        await host.StartAsync().WaitAsync(Deadline);
        Assert.Equal(DesktopSessionState.Ready, host.State);
        Assert.Null(host.StartupError);
        Assert.Null(host.Plugins);
    }

    private static async Task ImportAsync(DesktopSessionHost host, Workspace files, string source,
        NativeTransferImportPlanSummary plan, NativeTransferImportResult result)
    {
        await using NativeTransferImportSession session = host.CreateNativeTransferImportSession(files.Work);
        NativeTransferImportSnapshot ready = await session.InspectAsync(source).WaitAsync(Deadline);
        AssertReady(ready, plan);
        AssertStage(files.Work, new FileInfo(source).Length);
        AssertCompleted(await session.ApplyAsync(ready.Generation).WaitAsync(Deadline), ready.Generation, result);
        Assert.Empty(Directory.GetFileSystemEntries(files.Work));
        Assert.Equal(DesktopSessionState.Ready, host.State);
    }

    private static void AssertReady(NativeTransferImportSnapshot value, NativeTransferImportPlanSummary plan)
    {
        Assert.Equal(NativeTransferImportState.Ready, value.State);
        Assert.Equal(NativeTransferImportError.None, value.Error);
        Assert.True(value.Generation > 0);
        Assert.NotNull(value.Inventory);
        Assert.Equal(plan, value.Plan);
        Assert.Null(value.Result);
        Assert.False(value.CleanupRetained);
    }

    private static void AssertCompleted(NativeTransferImportSnapshot value, long generation, NativeTransferImportResult result)
    {
        Assert.Equal(NativeTransferImportState.Completed, value.State);
        Assert.Equal(NativeTransferImportError.None, value.Error);
        Assert.Equal(generation, value.Generation);
        Assert.Equal(result, value.Result);
        Assert.False(value.CleanupRetained);
    }

    private static string AssertStage(string work, long length)
    {
        string directory = Assert.Single(Directory.GetDirectories(work));
        Assert.StartsWith(".meowchan-native-import-", Path.GetFileName(directory));
        Assert.Empty(Directory.GetFiles(work));
        string stage = Assert.Single(Directory.GetFiles(directory));
        Assert.EndsWith(".pending", stage);
        Assert.Equal(length, new FileInfo(stage).Length);
        return stage; // retained stage owns an exclusive handle; never open it here
    }

    private static async Task<ArchivedThreadView> ReadAsync(string profile)
    {
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile).WaitAsync(Deadline);
        return Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Id).WaitAsync(Deadline));
    }

    private static void Assert404(ArchivedThreadView view)
    {
        Assert.Equal(ThreadSourceState.NotFound, view.Summary.SourceState);
        Assert.False(view.Summary.CaptureEnabled);
        Assert.Null(view.Summary.LastCapturedAt);
        Assert.Equal("W34 synthetic 404", view.Summary.Title);
        Assert.Equal(false, view.DecimalPostOrder);
    }

    private static async Task<MediaAssetSnapshot[]> ReadRootsAsync(ArchivedThreadView view)
    {
        MediaAssetSnapshot[] roots = await Task.WhenAll(view.MediaDirectories.Select(r => MediaAssetReader.OpenAsync(r))).WaitAsync(Deadline);
        Assert.NotEmpty(roots);
        Assert.All(roots, r => { Assert.Equal(MediaManifestStatus.Available, r.Status); Assert.Equal(6, r.ManifestVersion); });
        return roots;
    }

    private static async Task<ResolvedMediaAttachment> AssertOriginalAsync(ArchivedThreadView view,
        MediaAssetSnapshot[] roots, string post, byte[] expected)
    {
        CapturedAttachment metadata = Assert.Single(view.Posts.Single(p => p.PostId == post).Attachments!);
        Assert.Null(metadata.PlannedPath);
        Assert.Null(metadata.ExpectedSha1);
        ResolvedMediaAttachment resolved = Assert.IsType<ResolvedMediaAttachment>(MediaAttachmentResolver.Resolve(roots, Id, post, metadata));
        Assert.False(resolved.IsLegacy);
        Assert.Equal(MediaAcceptanceKind.PassiveImport, resolved.Binding!.Acceptance.Kind);
        Assert.NotNull(resolved.Binding.PassiveImport);
        await using Stream stream = await resolved.Snapshot.OpenVerifiedReadAsync(resolved.Asset.AssetId).WaitAsync(Deadline);
        await AssertBytesAsync(stream, expected);
        Assert.NotEqual(Hash(Preview), resolved.Asset.Sha256);
        return resolved;
    }

    private static async Task AssertDerivedAsync(MediaAssetSnapshot[] roots, string post, byte[] expected)
    {
        var member = Assert.Single(roots.SelectMany(r => r.DerivedRepresentations.Select(d => (Root: r, Derived: d))),
            item => item.Derived.Parent.PostId == post);
        Assert.Equal(MediaDerivedKind.Preview, member.Derived.Kind);
        Assert.Equal(MediaDerivedStatus.Contained, member.Derived.IncomingStatus);
        Assert.Null(member.Derived.SourceVersion.Sha1);
        Assert.Equal(Hash(post == "15" ? Original : AddedOriginal), member.Derived.SourceVersion.Sha256);
        Assert.Equal(Hash(expected), member.Derived.Blob!.Actual.Sha256);
        await using Stream stream = await member.Root.OpenVerifiedDerivedReadAsync(member.Derived).WaitAsync(Deadline);
        await AssertBytesAsync(stream, expected);
    }

    private static async Task AssertBytesAsync(Stream stream, byte[] expected)
    {
        using MemoryStream bytes = new();
        await stream.CopyToAsync(bytes).WaitAsync(Deadline);
        Assert.Equal(expected, bytes.ToArray());
    }

    private static async Task AssertReexportAsync(string profile, bool hasOriginal, bool hasAdded)
    {
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile).WaitAsync(Deadline);
        TransferArchiveFreezeResult frozen = await NativeTransferArchiveExporter.FreezeAsync(reader, Id, At.AddDays(2),
            Guid.NewGuid(), new NativeTransferArchiveMediaOptions(true, true, true)).WaitAsync(Deadline);
        Assert.Equal(TransferArchiveError.None, frozen.Error);
        NativeTransferArchiveSnapshot snapshot = Assert.IsType<NativeTransferArchiveSnapshot>(frozen.Snapshot);
        using MemoryStream output = new();
        TransferArchiveWriteResult written = await NativeTransferArchiveExporter.WriteAsync(snapshot, output).WaitAsync(Deadline);
        Assert.True(written.IsComplete, written.ToString());
        output.Position = 0;
        TransferPackageInspection inspected = await NativeTransferPackageValidator.InspectAsync(output).WaitAsync(Deadline);
        Assert.True(inspected.IsPackageValid, inspected.ToString());
        NativeTransferPackage package = Assert.IsType<NativeTransferPackage>(inspected.Package);
        Assert.True(package.IsChanV4);
        Assert.Null(package.PrivateMetadata);
        TransferThread thread = Assert.Single(package.Catalog.Threads);
        Assert.Equal(ThreadSourceState.NotFound, thread.SourceState);
        Assert.Equal(hasAdded ? new[] { "15", "17" } : new[] { "15" }, thread.Posts.Select(p => p.Metadata.PostId));
        TransferAttachment original = Assert.Single(thread.Posts.Single(p => p.Metadata.PostId == "15").Attachments);
        Assert.Equal(TransferMediaRole.Original, original.Role);
        Assert.Equal(hasOriginal ? TransferContentStatus.Contained : TransferContentStatus.Omitted, original.Status);
        Assert.Equal(hasOriginal ? Hash(Original) : null, original.PayloadSha256);
        Assert.Null(original.OriginalByteVersion!.Sha1);
        Assert.Null(original.Metadata.ExpectedSha1);
        Assert.Equal(Hash(Original), original.OriginalByteVersion.Sha256);
        TransferDerivedRepresentation preview = Assert.Single(original.DerivedRepresentations);
        Assert.Equal(TransferContentStatus.Contained, preview.Status);
        Assert.Equal(Hash(Preview), preview.PayloadSha256);
        Assert.Equal(original.Metadata.AttachmentRevisionKey, preview.SourceRevisionKey);
        Assert.Equal(original.OriginalByteVersion, preview.SourceByteVersion);
        Assert.Empty(thread.RetainedBindings);
        if (hasAdded)
        {
            TransferAttachment added = Assert.Single(thread.Posts.Single(p => p.Metadata.PostId == "17").Attachments);
            Assert.Equal(TransferContentStatus.Contained, added.Status);
            Assert.Equal(Hash(AddedOriginal), added.PayloadSha256);
            Assert.Equal(Hash(AddedPreview), Assert.Single(added.DerivedRepresentations).PayloadSha256);
        }
        Assert.All(thread.Posts.SelectMany(p => p.Attachments), a =>
        {
            Assert.Null(a.OriginalByteVersion!.Sha1);
            Assert.All(a.DerivedRepresentations, d => Assert.Null(d.SourceByteVersion.Sha1));
        });
        byte[][] expected = hasAdded ? [Original, Preview, AddedOriginal, AddedPreview]
            : hasOriginal ? [Original, Preview] : [Preview];
        Assert.Equal(expected.Select(Hash).Order(), package.Envelope.Payloads.Select(p => p.Sha256).Order());
        output.Position = 0;
        using ZipArchive zip = new(output, ZipArchiveMode.Read, leaveOpen: true);
        Assert.Equal(expected.Length, zip.Entries.Count(e => e.FullName.StartsWith("payload/", StringComparison.Ordinal)));
        foreach (byte[] bytes in expected)
        {
            ZipArchiveEntry entry = Assert.IsType<ZipArchiveEntry>(zip.GetEntry("payload/" + Hash(bytes)));
            using Stream stream = entry.Open();
            await AssertBytesAsync(stream, bytes);
        }
        if (!hasOriginal) Assert.Null(zip.GetEntry("payload/" + Hash(Original)));
    }

    // Read-only evidence, never SQL seeding or receipt manufacture.
    private sealed record CatalogState(long Generation, long Threads, long Posts, long Attachments,
        long Roots, long Imports, long ImportReceipts, long Merges, long MergeReceipts);

    private static SqliteConnection OpenReadOnly(string profile)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(profile, "archive.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static CatalogState ReadState(string profile)
    {
        using SqliteConnection connection = OpenReadOnly(profile);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT generation,(SELECT count(*) FROM archive_threads),(SELECT count(*) FROM archive_posts),
                (SELECT count(*) FROM archive_attachments),(SELECT count(*) FROM archive_media_directories),
                (SELECT count(*) FROM archive_media_import_operations),(SELECT count(*) FROM archive_media_import_receipts),
                (SELECT count(*) FROM archive_media_merge_operations),(SELECT count(*) FROM archive_media_merge_receipts)
            FROM archive_import_fence WHERE singleton=1
            """;
        using SqliteDataReader row = command.ExecuteReader();
        Assert.True(row.Read());
        return new(row.GetInt64(0), row.GetInt64(1), row.GetInt64(2), row.GetInt64(3), row.GetInt64(4),
            row.GetInt64(5), row.GetInt64(6), row.GetInt64(7), row.GetInt64(8));
    }

    private static void AssertCounts(CatalogState actual, int threads, int posts, int attachments, int roots, int imports, int merges)
        => Assert.Equal(new CatalogState(actual.Generation, threads, posts, attachments, roots, imports, imports, merges, merges), actual);

    private static void AssertReceipt(string profile, bool merge, NativeTransferImportResult expected, int originalBindings,
        string? operation = null)
    {
        using SqliteConnection connection = OpenReadOnly(profile);
        using SqliteCommand command = connection.CreateCommand();
        string family = merge ? "merge" : "import";
        string counts = merge ? "r.new_posts,r.updated_posts,r.new_attachments,r.conflicts,r.new_bindings"
            : "r.post_count,0,r.attachment_count,0,r.original_binding_count";
        command.CommandText = $"""
            SELECT o.plan_version,r.plan_version,o.state,o.request_sha256,r.request_sha256,{counts},
                r.evidence_additions,r.derived_descriptor_additions,r.contained_derived_additions
            FROM archive_media_{family}_operations o JOIN archive_media_{family}_receipts r ON r.operation_id=o.operation_id
            WHERE ($operation IS NULL OR o.operation_id=$operation)
            """;
        command.Parameters.AddWithValue("$operation", (object?)operation ?? DBNull.Value);
        using SqliteDataReader row = command.ExecuteReader();
        Assert.True(row.Read());
        Assert.Equal(2, row.GetInt32(0));
        Assert.Equal(2, row.GetInt32(1));
        Assert.Equal("Committed", row.GetString(2));
        Assert.Equal(row.GetString(3), row.GetString(4));
        Assert.Equal(originalBindings, row.GetInt32(9));
        Assert.Equal(new NativeTransferImportResult(NativeMediaImportCommitStatus.Committed, merge ? 0 : 1,
            row.GetInt32(5), row.GetInt32(6), row.GetInt32(7), row.GetInt32(8), false,
            row.GetInt32(10), row.GetInt32(11), row.GetInt32(12)), expected);
        Assert.False(row.Read());
    }

    private static string[] ReadMergeOperationIds(string profile)
    {
        using SqliteConnection connection = OpenReadOnly(profile);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT operation_id FROM archive_media_merge_operations ORDER BY operation_id";
        using SqliteDataReader rows = command.ExecuteReader();
        List<string> ids = [];
        while (rows.Read()) ids.Add(rows.GetString(0));
        return ids.ToArray();
    }

    private static Dictionary<string, string> RootFiles(IEnumerable<string> roots)
        => roots.SelectMany(r => Directory.GetFiles(r, "*", SearchOption.AllDirectories))
            .Where(p => Path.GetFileName(p) != ".meowchan-store.lock")
            .ToDictionary(p => p, p => Hash(File.ReadAllBytes(p)), StringComparer.Ordinal);

    private static void AssertFilesUnchanged(Dictionary<string, string> expected)
    {
        foreach ((string path, string hash) in expected) Assert.Equal(hash, Hash(File.ReadAllBytes(path)));
        string[] roots = expected.Keys.Where(p => Path.GetFileName(p) == ".meowchan-manifest.json")
            .Select(p => Path.GetDirectoryName(p)!).ToArray();
        Assert.NotEmpty(roots);
        Assert.Equal(expected.Keys.Order(), RootFiles(roots).Keys.Order());
    }

    private static NativeTransferCatalog Content(TransferContentStatus status, bool addPost = false, bool derived = true)
    {
        ImmutableArray<TransferPost> posts = [Post("15", Attachment("15", Original, Preview, status, derived), Html, 0)];
        if (addPost) posts = posts.Add(Post("17", Attachment("17", AddedOriginal, AddedPreview, TransferContentStatus.Contained, true),
            "W34 independently added post", 1));
        return new(At, [new(Id, "https://boards.4chan.org/b/thread/7401", "W34 synthetic 404", ThreadSourceState.NotFound,
            At, At, 1, false, posts)]);
    }

    private static TransferPost Post(string id, TransferAttachment attachment, string html, long ordinal)
        => new(new(id, ordinal, PostFieldPresence.All, At, "", "", html, false, [attachment.Metadata]), [attachment]);

    private static TransferAttachment Attachment(string post, byte[] original, byte[] preview, TransferContentStatus status, bool derived)
    {
        MediaProviderMetadata provider = new("media-" + post, "original", $"https://i.4cdn.org/b/{post}.webm",
            "synthetic.webm", original.Length, Convert.ToBase64String(MD5.HashData(original)), Hash(original));
        CapturedAttachment metadata = new(0, provider.MediaId, provider.Variant, provider.Url, provider.OriginalFileName,
            ExpectedSize: provider.ExpectedSize, ExpectedMd5: provider.ExpectedMd5)
        { ExpectedSha256 = provider.ExpectedSha256, AttachmentRevisionKey = MediaRevisionKeys.CreateAttachmentKey(Id, post, 0, provider) };
        TransferOriginalByteVersion version = new(original.LongLength, Hash(original), provider.ExpectedMd5, null);
        return new(metadata, TransferMediaRole.Original, status, status == TransferContentStatus.Contained ? Hash(original) : null, null)
        { OriginalByteVersion = version, DerivedRepresentations = derived ? [new(TransferDerivedRepresentationKind.Preview,
            metadata.AttachmentRevisionKey!, version, TransferContentStatus.Contained, Hash(preview))] : [] };
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class Workspace : IDisposable
    {
        private readonly TempDirectory _directory = new();
        internal string Profile { get; }
        internal string Work { get; }
        private readonly string _sources;
        internal Workspace()
        {
            Profile = Directory.CreateDirectory(Path.Combine(_directory.Path, "profile")).FullName;
            Work = Directory.CreateDirectory(Path.Combine(_directory.Path, "ingress")).FullName;
            _sources = Directory.CreateDirectory(Path.Combine(_directory.Path, "sources")).FullName;
            new Config { AutomaticUpdateChecks = false, SaveUrlsOnExit = true, DownloadsPaused = true,
                DownloadPath = Path.Combine(Profile, "absent-media") }.SaveConfig(Profile);
        }
        internal DesktopSessionHost CreateHost() => new(Profile, Array.Empty<IImageBoard>());
        internal async Task<string> WritePackageAsync(NativeTransferCatalog catalog, params byte[][] payloads)
        {
            using MemoryStream bytes = new();
            TransferPayloadSource[] sources = payloads.DistinctBy(Hash).Select(data => new TransferPayloadSource(
                new(data.LongLength, Hash(data)), _ => Task.FromResult<Stream>(new MemoryStream(data, writable: false)))).ToArray();
            TransferWriteResult written = await NativeTransferPackageWriter.WriteAsync(bytes, catalog, sources, Guid.NewGuid()).WaitAsync(Deadline);
            Assert.True(written.IsComplete, written.ToString());
            Assert.True(written.Package!.IsChanV4);
            Assert.Null(written.Package.PrivateMetadata);
            string path = Path.Combine(_sources, Guid.NewGuid().ToString("N") + ".mcw");
            await File.WriteAllBytesAsync(path, bytes.ToArray()).WaitAsync(Deadline);
            return path;
        }
        public void Dispose() => _directory.Dispose();
    }
}
