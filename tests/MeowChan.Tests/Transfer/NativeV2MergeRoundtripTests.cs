using System.IO.Compression;
using System.Text;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;
using static MeowChan.Tests.Transfer.NativeV2ImportFixture;

namespace MeowChan.Tests.Transfer;

// W32b5 acceptance tests for the existing coordinator's v2 branch, not a second
// executor. Prepare currently rejects chan-v4: that is a red prerequisite, not a
// skip or an alternative success. Every seed and merge uses the real writer/stage.
public sealed class NativeV2MergeRoundtripTests
{
    private static readonly byte[] NewOriginal = Encoding.UTF8.GetBytes("synthetic W32b5 newer original");
    private static readonly byte[] NewPreview = Encoding.UTF8.GetBytes("synthetic W32b5 newer preview");
    private static readonly byte[] RetainedOriginal = Encoding.UTF8.GetBytes("synthetic W32b5 retained original");
    private static readonly byte[] RetainedPreview = Encoding.UTF8.GetBytes("synthetic W32b5 retained preview");

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Additive404MergeSurvivesRestartDuplicateMergeAndInventoryRoundtrip(
        bool retainedOriginalContained, bool captureEnabled)
    {
        NativeTransferCatalog seed = Content();
        // Old retained evidence shares the current original's hash, but must never
        // borrow its binding or become a captured post, even on reexport/reimport.
        TransferThread seedThread = seed.Threads[0] with
        {
            RetainedBindings = [new("900", Attachment("900", TransferContentStatus.Omitted))]
        };
        seed = seed with { Threads = [seedThread] };
        TransferAttachment added = NewAttachment("18", NewOriginal, NewPreview);
        TransferAttachment retained = NewAttachment("999", RetainedOriginal, RetainedPreview,
            retainedOriginalContained ? TransferContentStatus.Contained : TransferContentStatus.Omitted);
        TransferPost newer = Post("18", added);
        newer = newer with { Metadata = newer.Metadata with
            { Ordinal = 18, PostedAt = At.AddDays(1), Html = "synthetic newer post, not retained evidence" } };
        TransferThread expected = seedThread with
        {
            Posts = seedThread.Posts.Add(newer),
            RetainedBindings = seedThread.RetainedBindings.Add(new("999", retained))
        };
        NativeTransferCatalog incoming = new(At.AddDays(2), [expected with
        {
            SourceState = ThreadSourceState.Active,
            FirstSeenAtUtc = At.AddDays(-10),
            CapturedAtUtc = At.AddDays(2),
            CaptureRevision = 99
        }]);
        byte[][] payloads = retainedOriginalContained
            ? [Original, Preview, NewOriginal, NewPreview, RetainedOriginal, RetainedPreview]
            : [Original, Preview, NewOriginal, NewPreview, RetainedPreview];

        await using NativeV2ImportFixture source = await NativeV2ImportFixture.CreateAsync(seed);
        await CommitSeedAsync(source);
        await source.Catalog.SetCaptureEnabledAsync(Id, captureEnabled);
        ArchivedThreadView before;
        ResolvedMediaAttachment oldOriginal;
        string oldRoot;
        Dictionary<string, string> oldFiles;
        await using (ArchiveReader reader = await ArchiveReader.OpenAsync(source.Files.Profile))
        {
            before = await ReadAsync(reader);
            Assert.Equal(ThreadSourceState.NotFound, before.Summary.SourceState);
            Assert.Equal(captureEnabled, before.Summary.CaptureEnabled);
            MediaAssetSnapshot[] inventory = await AssertInventoryAsync(before, seedThread, [Original, Preview]);
            oldOriginal = Assert.IsType<ResolvedMediaAttachment>(MediaAttachmentResolver.Resolve(
                inventory, Id, "15", Attachment().Metadata));
            oldRoot = Assert.Single(before.MediaDirectories);
            oldFiles = NativeMediaImportTests.Files(oldRoot);
        }

        var first = await MergeAndReplayAsync(source.Catalog, source.Lease, source.Files, incoming, payloads);
        Assert.Equal(1, first.Result.NewPosts);
        Assert.Equal(0, first.Result.UpdatedPosts);
        Assert.Equal(1, first.Result.NewAttachments);
        Assert.Equal(retainedOriginalContained ? 2 : 1, first.Result.NewBindings);
        Assert.Equal(0, first.Result.Conflicts);
        // Close the import stage, catalog and lease, not just an ArchiveReader.
        // MergeAndReplayAsync has also disposed the incoming stage and its owner token.
        await source.CloseOwnerAsync();

        using ProfileLease restartedLease = ProfileLease.Acquire(source.Files.Profile);
        await using ArchiveCatalog restarted = await ArchiveCatalog.OpenAsync(source.Files.Profile, restartedLease);
        await restarted.EnsureNativeMediaRecoveryClearAsync();
        restartedLease.Admission.CompleteStartup();
        await using ArchiveReader restartedReader = await ArchiveReader.OpenAsync(source.Files.Profile);
        ArchivedThreadView merged = await ReadAsync(restartedReader);
        Assert.Equal(before.Summary with { PostCount = 4, Revision = before.Summary.Revision + 1 }, merged.Summary);
        Assert.Equal(2, merged.MediaDirectories.Count);
        Assert.Contains(oldRoot, merged.MediaDirectories);
        Assert.Equal(oldFiles, NativeMediaImportTests.Files(oldRoot));
        MediaAssetSnapshot[] mergedInventory = await AssertInventoryAsync(merged, expected, payloads);
        AssertOriginalIdentity(oldOriginal, mergedInventory);
        Dictionary<string, Dictionary<string, string>> activeFiles = ActiveFiles(merged);

        // A new package, new stage and newly issued plan after restart, NOT merely
        // a replay of the first operation. Equal content must not publish copies.
        var duplicate = await MergeAndReplayAsync(restarted, restartedLease, source.Files, incoming, payloads);
        Assert.NotEqual(first.OperationId, duplicate.OperationId);
        AssertNoAdditions(duplicate.Result);
        ArchivedThreadView unchanged = await ReadAsync(restartedReader);
        Assert.Equal(merged.Summary, unchanged.Summary);
        Assert.Equal(merged.MediaDirectories, unchanged.MediaDirectories);
        AssertActiveFilesUnchanged(activeFiles);
        AssertOriginalIdentity(oldOriginal, await AssertInventoryAsync(unchanged, expected, payloads));
        await restarted.EnsureNativeMediaRecoveryClearAsync();

        using MemoryStream exported = await ExportAsync(restartedReader, unchanged, expected, payloads);
        await AssertSecondProfileAsync(exported, expected, payloads);
        AssertActiveFilesUnchanged(activeFiles);
        Assert.Equal(oldFiles, NativeMediaImportTests.Files(oldRoot));
    }

    [Fact]
    public async Task DerivedOnlyAdditionToExistingOriginalPublishesNoSecondOriginalAndRoundtrips()
    {
        NativeTransferCatalog seed = Content(derived: false);
        NativeTransferCatalog incoming = Content();
        await using NativeV2ImportFixture source = await NativeV2ImportFixture.CreateAsync(seed, [Original]);
        await CommitSeedAsync(source);
        ArchivedThreadView before;
        ResolvedMediaAttachment oldOriginal;
        MediaOriginalEvidence oldEvidence;
        string oldRoot;
        Dictionary<string, string> oldFiles;
        await using (ArchiveReader reader = await ArchiveReader.OpenAsync(source.Files.Profile))
        {
            before = await ReadAsync(reader);
            MediaAssetSnapshot[] inventory = await AssertInventoryAsync(before, seed.Threads[0], [Original]);
            oldRoot = Assert.Single(before.MediaDirectories);
            oldEvidence = Assert.Single(Assert.Single(inventory).OriginalEvidence);
            oldOriginal = Assert.IsType<ResolvedMediaAttachment>(MediaAttachmentResolver.Resolve(
                inventory, Id, "15", Attachment(derived: false).Metadata));
            oldFiles = NativeMediaImportTests.Files(oldRoot);
        }

        var attempt = await MergeAndReplayAsync(source.Catalog, source.Lease, source.Files, incoming, [Original, Preview]);
        Assert.Equal(0, attempt.Result.NewPosts);
        Assert.Equal(0, attempt.Result.UpdatedPosts);
        Assert.Equal(0, attempt.Result.NewAttachments);
        Assert.Equal(0, attempt.Result.NewBindings); // This is not a no-op: a derived representation was added.
        Assert.Equal(0, attempt.Result.Conflicts);
        await source.CloseOwnerAsync();

        using ProfileLease restartedLease = ProfileLease.Acquire(source.Files.Profile);
        await using ArchiveCatalog restarted = await ArchiveCatalog.OpenAsync(source.Files.Profile, restartedLease);
        await restarted.EnsureNativeMediaRecoveryClearAsync();
        await using ArchiveReader readerAfterRestart = await ArchiveReader.OpenAsync(source.Files.Profile);
        ArchivedThreadView after = await ReadAsync(readerAfterRestart);
        Assert.Equal(before.Summary with { Revision = before.Summary.Revision + 1 }, after.Summary);
        Assert.Equal(2, after.MediaDirectories.Count);
        Assert.Contains(oldRoot, after.MediaDirectories);
        Assert.Equal(oldFiles, NativeMediaImportTests.Files(oldRoot));
        MediaAssetSnapshot[] merged = await AssertInventoryAsync(after, incoming.Threads[0], [Original, Preview]);
        AssertOriginalIdentity(oldOriginal, merged);
        Assert.Equal(oldEvidence, Assert.Single(merged.Single(m => m.Directory == oldRoot).OriginalEvidence));
        MediaAssetSnapshot addedRoot = Assert.Single(merged, m => m.Directory != oldRoot);
        Assert.Empty(addedRoot.AttachmentBindings);
        Assert.Equal(0, addedRoot.Count);
        Assert.Single(addedRoot.DerivedRepresentations);
        // A safe derived-only candidate may carry parent context, but no invented
        // Original binding. AssertInventoryAsync checks the exact parent/version.
        Dictionary<string, Dictionary<string, string>> activeFiles = ActiveFiles(after);
        using MemoryStream exported = await ExportAsync(readerAfterRestart, after, incoming.Threads[0], [Original, Preview]);
        await AssertSecondProfileAsync(exported, incoming.Threads[0], [Original, Preview]);
        AssertActiveFilesUnchanged(activeFiles);
    }

    private static async Task CommitSeedAsync(NativeV2ImportFixture source)
    {
        using ProfileAdmissionToken admission = await source.ExclusiveAsync();
        var outcome = await source.CommitAsync(admission);
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, outcome.Disposition);
        Assert.Equal(NativeMediaImportCommitStatus.Committed, outcome.Result.Status);
        Assert.False(outcome.Result.IsReplay);
        source.Catalog.EnsureTransferAdmission(admission);
    }

    private static async Task<(Guid OperationId, NativeMediaMergeResult Result)> MergeAndReplayAsync(
        ArchiveCatalog catalog, ProfileLease lease, Workspace files, NativeTransferCatalog content, byte[][] payloads)
    {
        await using NativeTransferImportStage stage = await StageAsync(files, content, payloads);
        Assert.Single(stage.Package.Catalog.Threads);
        NativeMediaMergePreparation prepared = await catalog.PrepareNativeMediaMergeAsync(stage);
        Assert.Equal(NativeMediaMergeError.None, prepared.Error);
        NativeMediaMergePlan plan = Assert.IsType<NativeMediaMergePlan>(prepared.Plan);
        using ProfileAdmissionToken admission = await lease.Admission.EnterExclusiveAsync();
        var outcome = await catalog.CommitNativeMediaMergeAdmittedAsync(admission, plan, new MediaStore());
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, outcome.Disposition);
        Assert.Equal(NativeMediaImportCommitStatus.Committed, outcome.Result.Status);
        Assert.False(outcome.Result.IsReplay);
        catalog.EnsureTransferAdmission(admission);
        // Authoritative receipt-first pattern: the original issued plan, same owner,
        // no retained stage, cancelled caller and callbacks that must not be reached.
        await stage.DisposeAsync();
        ArchivedThreadView beforeReplay;
        await using (ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile))
            beforeReplay = await ReadAsync(reader);
        Dictionary<string, Dictionary<string, string>> filesBeforeReplay = ActiveFiles(beforeReplay);
        var replay = await catalog.CommitNativeMediaMergeAdmittedAsync(admission, plan, new MediaStore(),
            new CancellationToken(true),
            beforeAdmission: () => throw new IOException("receipt replay reached new admission"),
            checkpoint: _ => throw new IOException("receipt replay reached materialization"));
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, replay.Disposition);
        Assert.Equal(outcome.Result with { IsReplay = true }, replay.Result);
        catalog.EnsureTransferAdmission(admission);
        await using (ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile))
        {
            ArchivedThreadView afterReplay = await ReadAsync(reader);
            Assert.Equal(beforeReplay.Summary, afterReplay.Summary);
            Assert.Equal(beforeReplay.MediaDirectories, afterReplay.MediaDirectories);
            AssertPostsEqual(beforeReplay.GetPosts(), afterReplay.GetPosts());
        }
        AssertActiveFilesUnchanged(filesBeforeReplay);
        return (plan.OperationId, outcome.Result);
    }

    private static void AssertNoAdditions(NativeMediaMergeResult result)
    {
        Assert.Equal(NativeMediaImportCommitStatus.Committed, result.Status);
        Assert.False(result.IsReplay);
        Assert.Equal(0, result.NewPosts);
        Assert.Equal(0, result.UpdatedPosts);
        Assert.Equal(0, result.NewAttachments);
        Assert.Equal(0, result.NewBindings);
        Assert.Equal(0, result.Conflicts);
    }

    private static TransferAttachment NewAttachment(string post, byte[] original, byte[] preview,
        TransferContentStatus status = TransferContentStatus.Contained)
    {
        // Start with the established fixture shape, updating every byte-dependent
        // provider, occurrence and source-version field together. No forged plan.
        TransferAttachment template = Attachment(post, status);
        MediaProviderMetadata provider = new(template.Metadata.MediaId!, "original", template.Metadata.Url!,
            template.Metadata.OriginalFileName, original.LongLength, Md5(original), Hash(original));
        CapturedAttachment metadata = template.Metadata with
        {
            ExpectedSize = provider.ExpectedSize, ExpectedMd5 = provider.ExpectedMd5,
            ExpectedSha256 = provider.ExpectedSha256,
            AttachmentRevisionKey = MediaRevisionKeys.CreateAttachmentKey(Id, post, 0, provider)
        };
        TransferOriginalByteVersion version = new(original.LongLength, Hash(original), Md5(original), null);
        return template with
        {
            Metadata = metadata,
            PayloadSha256 = status == TransferContentStatus.Contained ? Hash(original) : null,
            OriginalByteVersion = version,
            DerivedRepresentations = [new(TransferDerivedRepresentationKind.Preview,
                metadata.AttachmentRevisionKey!, version, TransferContentStatus.Contained, Hash(preview))]
        };
    }

    private static async Task<ArchivedThreadView> ReadAsync(ArchiveReader reader)
        => Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Id));

    private static Dictionary<string, Dictionary<string, string>> ActiveFiles(ArchivedThreadView view)
        => view.MediaDirectories.ToDictionary(root => root, NativeMediaImportTests.Files, StringComparer.Ordinal);

    private static void AssertActiveFilesUnchanged(Dictionary<string, Dictionary<string, string>> expected)
    {
        // Hash manifests/journals as well as .bin payloads; the established helper
        // excludes transient .lock files. Inactive Full candidates are permitted.
        foreach (var root in expected) Assert.Equal(root.Value, NativeMediaImportTests.Files(root.Key));
    }

    private static void AssertOriginalIdentity(ResolvedMediaAttachment before, MediaAssetSnapshot[] after)
    {
        ResolvedMediaAttachment current = Assert.IsType<ResolvedMediaAttachment>(
            MediaAttachmentResolver.Resolve(after, Id, "15", Attachment().Metadata));
        Assert.Equal(before.Asset, current.Asset);
        Assert.Equal(before.Binding, current.Binding);
        Assert.Equal(before.Snapshot.Directory, current.Snapshot.Directory);
    }

    private static void AssertPostsEqual(IReadOnlyList<CapturedPost> expected, IReadOnlyList<CapturedPost> actual)
    {
        Assert.Equal(expected.Select(p => p.PostId), actual.Select(p => p.PostId));
        for (int index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index] with { Attachments = null }, actual[index] with { Attachments = null });
            Assert.Equal(expected[index].Attachments, actual[index].Attachments);
            if (expected[index].Attachments == null) Assert.Null(actual[index].Attachments);
            else Assert.NotNull(actual[index].Attachments);
        }
    }

    private static async Task<MediaAssetSnapshot[]> AssertInventoryAsync(
        ArchivedThreadView view, TransferThread expected, byte[][] payloads)
    {
        Assert.Equal(Id, view.Summary.Id);
        Assert.Equal(ThreadSourceState.NotFound, view.Summary.SourceState);
        Assert.Equal(expected.Title, view.Summary.Title);
        Assert.Equal(expected.PageUrl, view.Summary.Url);
        Assert.Equal(expected.DecimalPostOrder, view.DecimalPostOrder);
        Assert.Equal(expected.Posts.Length, view.PostCount);
        AssertPostsEqual(expected.Posts.Select(p => p.Metadata).ToArray(), view.GetPosts());
        foreach (TransferRetainedBinding retained in expected.RetainedBindings)
            Assert.DoesNotContain(view.GetPosts(), p => p.PostId == retained.PostId);

        MediaAssetSnapshot[] inventories = await Task.WhenAll(view.MediaDirectories.Select(root => MediaAssetReader.OpenAsync(root)));
        Assert.All(inventories, inventory =>
        {
            Assert.Equal(MediaManifestStatus.Available, inventory.Status);
            Assert.Equal(6, inventory.ManifestVersion);
        });
        var occurrences = expected.Posts.SelectMany(p => p.Attachments.Select(a => (PostId: p.Metadata.PostId, Attachment: a)))
            .Concat(expected.RetainedBindings.Select(r => (r.PostId, r.Attachment))).ToArray();
        MediaOriginalEvidence[] evidence = inventories.SelectMany(m => m.OriginalEvidence).ToArray();
        var derived = inventories.SelectMany(m => m.DerivedRepresentations.Select(d => (Snapshot: m, Value: d))).ToArray();
        // A derived-only Safe selection can carry duplicate parent *context* across
        // roots. Logical occurrences, original bindings and derived members cannot multiply.
        Assert.Equal(occurrences.Length, evidence.Select(e => e.Occurrence).Distinct().Count());
        Assert.Equal(occurrences.Count(o => o.Attachment.Status == TransferContentStatus.Contained),
            inventories.Sum(m => m.AttachmentBindings.Count));
        Assert.Equal(occurrences.Sum(o => o.Attachment.DerivedRepresentations.Length), derived.Length);
        Dictionary<string, byte[]> bytesByHash = payloads.DistinctBy(Hash).ToDictionary(Hash);
        foreach (var occurrence in occurrences)
        {
            TransferAttachment attachment = occurrence.Attachment;
            MediaOccurrence identity = new(Id, occurrence.PostId, attachment.Metadata.Position,
                attachment.Metadata.AttachmentRevisionKey);
            TransferOriginalByteVersion version = Assert.IsType<TransferOriginalByteVersion>(attachment.OriginalByteVersion);
            MediaActualIntegrity expectedVersion = new(version.Length, version.Md5, version.Sha256) { Sha1 = version.Sha1 };
            MediaOriginalEvidence[] parents = evidence.Where(e => e.Occurrence == identity).ToArray();
            Assert.NotEmpty(parents);
            Assert.All(parents, parent =>
            {
                Assert.Equal(attachment.Metadata, parent.Metadata);
                Assert.Equal(expectedVersion, parent.OriginalByteVersion);
                Assert.Null(parent.OriginalByteVersion!.Sha1); // measured SHA1 must not fill historical unknown evidence
                Assert.Equal(attachment.Status == TransferContentStatus.Contained
                    ? MediaObservedContentStatus.Contained : MediaObservedContentStatus.Omitted, parent.IncomingStatus);
            });
            ResolvedMediaAttachment? original = MediaAttachmentResolver.Resolve(inventories, Id, occurrence.PostId, attachment.Metadata);
            if (attachment.Status == TransferContentStatus.Contained)
            {
                ResolvedMediaAttachment resolved = Assert.IsType<ResolvedMediaAttachment>(original);
                Assert.False(resolved.IsLegacy);
                Assert.NotNull(resolved.Binding);
                await using Stream stream = await resolved.Snapshot.OpenVerifiedReadAsync(resolved.Asset.AssetId);
                await AssertBytesAsync(stream, bytesByHash[attachment.PayloadSha256!]);
            }
            else
            {
                Assert.Null(original);
                Assert.All(inventories, m => Assert.Null(m.FindAttachmentBinding(attachment.Metadata.AttachmentRevisionKey!)));
            }
            foreach (TransferDerivedRepresentation expectedDerived in attachment.DerivedRepresentations)
            {
                var member = Assert.Single(derived, d => d.Value.Parent == identity && d.Value.Kind == MediaDerivedKind.Preview);
                Assert.Equal(TransferDerivedRepresentationKind.Preview, expectedDerived.Kind);
                Assert.Equal(expectedVersion, member.Value.SourceVersion);
                Assert.Null(member.Value.SourceVersion.Sha1);
                Assert.Equal(MediaDerivedStatus.Contained, member.Value.IncomingStatus);
                MediaDerivedBlob blob = Assert.IsType<MediaDerivedBlob>(member.Value.Blob);
                Assert.Equal(expectedDerived.PayloadSha256, blob.Actual.Sha256);
                await using Stream stream = await member.Snapshot.OpenVerifiedDerivedReadAsync(member.Value);
                await AssertBytesAsync(stream, bytesByHash[expectedDerived.PayloadSha256!]);
            }
        }
        return inventories;
    }

    private static async Task<MemoryStream> ExportAsync(ArchiveReader reader, ArchivedThreadView view,
        TransferThread expected, byte[][] payloads)
    {
        TransferArchiveFreezeResult frozen = await NativeTransferArchiveExporter.FreezeAsync(reader, Id,
            At.AddDays(3), Guid.NewGuid(), new NativeTransferArchiveMediaOptions(true, true, true));
        Assert.Equal(TransferArchiveError.None, frozen.Error);
        NativeTransferArchiveSnapshot snapshot = Assert.IsType<NativeTransferArchiveSnapshot>(frozen.Snapshot);
        MemoryStream output = new();
        try
        {
            TransferArchiveWriteResult written = await NativeTransferArchiveExporter.WriteAsync(snapshot, output);
            Assert.True(written.IsComplete, written.ToString());
            output.Position = 0;
            TransferPackageInspection inspected = await NativeTransferPackageValidator.InspectAsync(output);
            Assert.True(inspected.IsPackageValid, inspected.ToString());
            NativeTransferPackage package = Assert.IsType<NativeTransferPackage>(inspected.Package);
            Assert.True(package.IsChanV4);
            Assert.Null(package.PrivateMetadata);
            AssertPortableThread(package.Catalog, view, expected);
            string catalog = Encoding.UTF8.GetString(NativeTransferCatalogSerializer.Serialize(package.Catalog));
            foreach (string directory in view.MediaDirectories) Assert.DoesNotContain(directory, catalog);
            Assert.DoesNotContain("plannedPath", catalog, StringComparison.OrdinalIgnoreCase);
            output.Position = 0;
            using (ZipArchive zip = new(output, ZipArchiveMode.Read, leaveOpen: true))
            {
                byte[][] distinct = payloads.DistinctBy(Hash).ToArray();
                Assert.Equal(distinct.Length, zip.Entries.Count(e => e.FullName.StartsWith("payload/", StringComparison.Ordinal)));
                foreach (byte[] bytes in distinct)
                {
                    ZipArchiveEntry entry = Assert.IsType<ZipArchiveEntry>(zip.GetEntry("payload/" + Hash(bytes)));
                    using Stream stream = entry.Open();
                    await AssertBytesAsync(stream, bytes);
                }
            }
            output.Position = 0;
            return output;
        }
        catch { output.Dispose(); throw; }
    }

    private static void AssertPortableThread(NativeTransferCatalog catalog, ArchivedThreadView view, TransferThread expected)
    {
        TransferThread thread = Assert.Single(catalog.Threads);
        Assert.Equal(Id, thread.Id);
        Assert.Equal(view.Summary.Url, thread.PageUrl);
        Assert.Equal(view.Summary.Title, thread.Title);
        Assert.Equal(ThreadSourceState.NotFound, thread.SourceState);
        Assert.Equal(view.Summary.FirstSeenAt, thread.FirstSeenAtUtc);
        Assert.Equal(view.Summary.LastCapturedAt, thread.CapturedAtUtc);
        Assert.Equal(view.Summary.Revision, thread.CaptureRevision);
        Assert.Equal(view.DecimalPostOrder, thread.DecimalPostOrder);
        AssertPostsEqual(expected.Posts.Select(p => p.Metadata).ToArray(), thread.Posts.Select(p => p.Metadata).ToArray());
        for (int index = 0; index < expected.Posts.Length; index++)
        {
            TransferPost wanted = expected.Posts[index], actual = thread.Posts[index];
            Assert.Equal(wanted.AttachmentsWereNull, actual.AttachmentsWereNull);
            Assert.Equal(wanted.Attachments.Length, actual.Attachments.Length);
            for (int slot = 0; slot < wanted.Attachments.Length; slot++)
                AssertPortableAttachment(wanted.Attachments[slot], actual.Attachments[slot]);
        }
        Assert.Equal(expected.RetainedBindings.Select(r => r.PostId), thread.RetainedBindings.Select(r => r.PostId));
        foreach (TransferRetainedBinding retained in expected.RetainedBindings)
        {
            TransferRetainedBinding actual = Assert.Single(thread.RetainedBindings, r => r.PostId == retained.PostId);
            AssertPortableAttachment(retained.Attachment, actual.Attachment);
            Assert.DoesNotContain(thread.Posts, p => p.Metadata.PostId == retained.PostId);
        }
    }

    private static void AssertPortableAttachment(TransferAttachment expected, TransferAttachment actual)
    {
        Assert.Equal(expected.Metadata, actual.Metadata);
        Assert.Null(actual.Metadata.PlannedPath);
        Assert.Equal(TransferMediaRole.Original, actual.Role);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.PayloadSha256, actual.PayloadSha256);
        Assert.Equal(expected.History, actual.History);
        Assert.Equal(expected.OriginalByteVersion, actual.OriginalByteVersion);
        Assert.Null(actual.OriginalByteVersion!.Sha1);
        Assert.Equal(expected.DerivedRepresentations.ToArray(), actual.DerivedRepresentations.ToArray());
    }

    private static async Task AssertSecondProfileAsync(MemoryStream exported, TransferThread expected, byte[][] payloads)
    {
        using Workspace second = new();
        using (ProfileLease lease = ProfileLease.Acquire(second.Profile))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(second.Profile, lease);
            await catalog.EnsureNativeMediaRecoveryClearAsync();
            exported.Position = 0;
            // Consume the exporter-written bytes; do NOT rebuild expected DTOs into
            // a substitute package that could hide a broken writer/projection.
            NativeTransferStageResult staged = await NativeTransferImportStage.CreateAsync(exported, second.StageRoot);
            Assert.Equal(NativeTransferStageError.None, staged.Error);
            await using NativeTransferImportStage stage = Assert.IsType<NativeTransferImportStage>(staged.Stage);
            Assert.True(stage.Package.IsChanV4);
            Assert.Null(stage.Package.PrivateMetadata);
            NativeMediaImportPreparation prepared = await catalog.PrepareNativeMediaImportAsync(stage);
            Assert.Equal(NativeMediaImportError.None, prepared.Error);
            NativeMediaImportPlan plan = Assert.IsType<NativeMediaImportPlan>(prepared.Plan);
            Assert.Equal(2, plan.PlanVersion);
            lease.Admission.CompleteStartup();
            using ProfileAdmissionToken admission = await lease.Admission.EnterExclusiveAsync();
            var imported = await catalog.CommitNativeMediaImportAdmittedAsync(admission, plan, new MediaStore());
            Assert.Equal(ProfileExclusiveDisposition.SafeToResume, imported.Disposition);
            Assert.Equal(NativeMediaImportCommitStatus.Committed, imported.Result.Status);
            Assert.False(imported.Result.IsReplay);
            Assert.Equal(1, imported.Result.NewThreads);
            Assert.Equal(expected.Posts.Length, imported.Result.NewPosts);
            Assert.Equal(expected.Posts.Sum(p => p.Attachments.Length), imported.Result.NewAttachments);
            catalog.EnsureTransferAdmission(admission);
        }
        using ProfileLease restartedLease = ProfileLease.Acquire(second.Profile);
        await using ArchiveCatalog restarted = await ArchiveCatalog.OpenAsync(second.Profile, restartedLease);
        await restarted.EnsureNativeMediaRecoveryClearAsync();
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(second.Profile);
        ArchivedThreadView copied = await ReadAsync(reader);
        Assert.Single(copied.MediaDirectories);
        Assert.False(copied.Summary.CaptureEnabled); // private/local preference is not exported
        await AssertInventoryAsync(copied, expected, payloads);
        TransferArchiveFreezeResult frozen = await NativeTransferArchiveExporter.FreezeAsync(reader, Id,
            At.AddDays(4), Guid.NewGuid(), new NativeTransferArchiveMediaOptions(true, true, true));
        Assert.Equal(TransferArchiveError.None, frozen.Error);
        AssertPortableThread(Assert.IsType<NativeTransferArchiveSnapshot>(frozen.Snapshot).Catalog, copied, expected);
    }

    private static async Task AssertBytesAsync(Stream stream, byte[] expected)
    {
        using MemoryStream actual = new();
        await stream.CopyToAsync(actual);
        Assert.Equal(expected, actual.ToArray());
    }
}
