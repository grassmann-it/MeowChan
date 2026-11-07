using System.Collections.Immutable;
using System.IO.Compression;
using static MeowChan.Tests.Transfer.NativeMediaImportTests;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

// R01: contained retained input is historical data, never a captured-post surrogate.
// These tests deliberately use only synthetic writer/stage/store data.
public sealed class NativeMediaMergeRetainedTests
{
    private static readonly ResourceId Id = new("4chan", "b", "7001");
    private const string Url = "https://boards.4chan.org/b/thread/7001";
    private static readonly DateTimeOffset At = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RetainedOnlyInputAddsHistoricalBindingsWithoutPostsOrSqlAttachments()
    {
        using Workspace w = new();
        byte[] bytes = [3, 5, 7, 11];
        NativeTransferPackageWriter.TransferPackageBudget budget;
        using (ProfileLease lease = ProfileLease.Acquire(w.Profile))
        await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(w.Profile, lease))
        {
            await SeedAsync(lease, catalog, w);
            using ProfileAdmissionToken admission = await EnterExclusiveAsync(lease);

            NativeTransferCatalog input = CatalogWith(retained: [Retained("900", bytes, "r900")]);
            await using NativeTransferImportStage stage = await StageAsync(w, input, bytes);
            NativeMediaMergePreparation prepared = await catalog.PrepareNativeMediaMergeAsync(stage);
            Assert.Equal(NativeMediaMergeError.None, prepared.Error);
            NativeMediaMergePlan plan = Assert.IsType<NativeMediaMergePlan>(prepared.Plan);
            budget = plan.ExportBudget;
            Assert.Single(plan.Inputs);
            Assert.Empty(plan.AddedPosts);
            Assert.Empty(plan.AddedAttachments);
            Assert.Single(plan.SafeKeys);
            Assert.Equal(plan.Inputs.Select(x => x.RevisionKey), plan.FullKeys);
            Assert.Equal(NativeMediaImportCommitStatus.Committed,
                (await CommitMergeWithExclusiveAsync(admission, catalog, plan, ProfileExclusiveDisposition.SafeToResume)).Status);
        }

        await using ArchiveReader reader = await ArchiveReader.OpenAsync(w.Profile);
        ArchivedThreadView view = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Id));
        Assert.DoesNotContain(view.Posts, p => p.PostId == "900");
        Assert.Equal(1, view.Posts.Sum(p => p.Attachments?.Count ?? 0));
        Assert.Equal(2, view.Posts.Count); // the seeded 404 observations remain the only posts
        Assert.Equal(2, view.MediaDirectories.Count);
        MediaAssetSnapshot[] manifests = await Task.WhenAll(view.MediaDirectories.Select(x => MediaAssetReader.OpenAsync(x)));
        ResolvedMediaAttachment resolved = Assert.IsType<ResolvedMediaAttachment>(
            MediaAttachmentResolver.Resolve(manifests, Id, "900", Metadata("900", bytes, "r900")));
        Assert.Equal(MediaAcceptanceKind.PassiveImport, resolved.Binding.Acceptance.Kind);
        Assert.Equal(bytes, await ReadAsync(resolved));
        NativeTransferPackage exported = await ReexportAsync(reader, budget);
        TransferRetainedBinding historical = Assert.Single(exported.Catalog.Threads[0].RetainedBindings);
        Assert.Equal("900", historical.PostId);
        Assert.Equal(Retained("900", bytes, "r900").Attachment.History, historical.Attachment.History);
    }

    [Fact]
    public async Task CurrentAndSeveralRetainedRevisionsOfOneSlotAreAcceptedAndReexported()
    {
        using Workspace w = new();
        byte[] current = [13, 17], old = [19, 23], older = [29, 31, 37];
        using (ProfileLease lease = ProfileLease.Acquire(w.Profile))
        await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(w.Profile, lease))
        {
            await SeedAsync(lease, catalog, w);
            using ProfileAdmissionToken admission = await EnterExclusiveAsync(lease);

            TransferAttachment now = Attachment("17", current, "current");
            NativeTransferCatalog input = CatalogWith(
                posts: [new(new("17", 2, PostFieldPresence.Attachments | PostFieldPresence.PostedAt, At, Attachments: [now.Metadata]), [now])],
                retained: [Retained("17", old, "old"), Retained("17", older, "older")]);
            await using NativeTransferImportStage stage = await StageAsync(w, input, current, old, older);
            NativeMediaMergePreparation prepared = await catalog.PrepareNativeMediaMergeAsync(stage);
            Assert.Equal(NativeMediaMergeError.None, prepared.Error);
            NativeMediaMergePlan plan = Assert.IsType<NativeMediaMergePlan>(prepared.Plan);
            Assert.Equal(3, plan.Inputs.Length);
            Assert.Equal(3, plan.FullKeys.Length);
            Assert.Equal(3, plan.SafeKeys.Length);
            Assert.Equal(NativeMediaImportCommitStatus.Committed,
                (await CommitMergeWithExclusiveAsync(admission, catalog, plan, ProfileExclusiveDisposition.SafeToResume)).Status);
        }

        await using ArchiveReader reader = await ArchiveReader.OpenAsync(w.Profile);
        TransferArchiveFreezeResult frozen = await NativeTransferArchiveExporter.FreezeAsync(reader, Id, At, Guid.NewGuid());
        Assert.True(frozen.IsFrozen, frozen.Error.ToString());
        NativeTransferArchiveSnapshot snapshot = Assert.IsType<NativeTransferArchiveSnapshot>(frozen.Snapshot);
        using MemoryStream output = new();
        TransferArchiveWriteResult written = await NativeTransferArchiveExporter.WriteAsync(snapshot, output);
        Assert.True(written.IsComplete, written.Error + "/" + written.WriteError);
        TransferPackageInspection inspected = await NativeTransferPackageValidator.InspectAsync(output);
        Assert.True(inspected.IsPackageValid, inspected.ToString());
        TransferThread exported = Assert.Single(inspected.Package!.Catalog.Threads);
        Assert.Equal(2, exported.RetainedBindings.Length);
        Assert.Equal(3, exported.Posts.Length); // 15, 16 and explicit current 17, no retained surrogate
        Assert.All(exported.RetainedBindings, binding =>
        {
            Assert.Equal("17", binding.PostId);
            Assert.Equal(0, binding.Attachment.Metadata.Position);
            Assert.Equal(new TransferHistoricalIntegrity("strict", At.AddDays(-10), false, false), binding.Attachment.History);
        });
        ArchivedThreadView view = (await reader.ReadThreadAsync(Id))!;
        MediaAssetSnapshot[] active = await Task.WhenAll(view.MediaDirectories.Select(x => MediaAssetReader.OpenAsync(x)));
        foreach ((byte[] bytes, string revision) in new[] { (current, "current"), (old, "old"), (older, "older") })
        {
            ResolvedMediaAttachment resolved = Assert.IsType<ResolvedMediaAttachment>(
                MediaAttachmentResolver.Resolve(active, Id, "17", Metadata("17", bytes, revision)));
            Assert.Equal(MediaAcceptanceKind.PassiveImport, resolved.Binding.Acceptance.Kind);
            Assert.Equal(bytes, await ReadAsync(resolved));
        }
        Assert.Equal(new[] { Hash(old), Hash(older), Hash(current), Hash(Bytes) }.OrderBy(x => x, StringComparer.Ordinal),
            inspected.Package.Envelope.Payloads.Select(x => x.Sha256).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public async Task RetainedMergePreserves404RootBytesAndPassiveProvenanceAfterRestartAndReplay()
    {
        using Workspace w = new();
        byte[] bytes = [41, 43, 47];
        Guid operation;
        Dictionary<string, string> before;
        string originalRoot;
        NativeMediaMergePlan committedPlan;
        string fingerprint;
        using (ProfileLease ownerLease = ProfileLease.Acquire(w.Profile))
        await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(w.Profile, ownerLease))
        {
            await SeedAsync(ownerLease, catalog, w);
            using ProfileAdmissionToken admission = await EnterExclusiveAsync(ownerLease);
            await using ArchiveReader initial = await ArchiveReader.OpenAsync(w.Profile);
            string root = Assert.Single((await initial.ReadThreadAsync(Id))!.MediaDirectories);
            originalRoot = root;
            before = Files(root);
            NativeTransferCatalog input = CatalogWith(retained: [Retained("901", bytes, "r901")]);
            await using NativeTransferImportStage stage = await StageAsync(w, input, bytes);
            NativeMediaMergePlan plan = Assert.IsType<NativeMediaMergePlan>(
                (await catalog.PrepareNativeMediaMergeAsync(stage)).Plan);
            operation = plan.OperationId;
            committedPlan = plan;
            fingerprint = stage.Package.Envelope.Fingerprint;
            Assert.Equal(NativeMediaImportCommitStatus.Committed,
                (await CommitMergeWithExclusiveAsync(admission, catalog, plan, ProfileExclusiveDisposition.SafeToResume)).Status);
            Assert.Equal(NativeMediaImportCommitStatus.Committed,
                (await CommitMergeWithExclusiveAsync(admission, catalog, plan, ProfileExclusiveDisposition.SafeToResume)).Status);
        }
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(w.Profile);
        ArchivedThreadView view = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Id));
        Assert.Equal(ThreadSourceState.NotFound, view.Summary.SourceState);
        Assert.Equal(2, view.MediaDirectories.Count);
        Assert.Contains(originalRoot, view.MediaDirectories);
        Assert.Equal(before, Files(originalRoot));
        MediaAssetSnapshot[] manifests = await Task.WhenAll(view.MediaDirectories.Select(x => MediaAssetReader.OpenAsync(x)));
        ResolvedMediaAttachment resolved = Assert.IsType<ResolvedMediaAttachment>(
            MediaAttachmentResolver.Resolve(manifests, Id, "901", Metadata("901", bytes, "r901")));
        Assert.Equal(MediaAcceptanceKind.PassiveImport, resolved.Binding.Acceptance.Kind);
        Assert.NotNull(resolved.Binding.PassiveImport);
        Assert.Equal(fingerprint, resolved.Binding.PassiveImport.PackageFingerprint);
        Assert.Equal(operation.ToString("D"), resolved.Binding.PassiveImport.OperationId);
        Assert.Equal(bytes, await ReadAsync(resolved));
        Assert.Equal(MediaAcceptanceKind.Strict, resolved.Binding.PassiveImport.History!.Kind);
        Assert.Equal(At.AddDays(-10), resolved.Binding.PassiveImport.History.AcceptedAtUtc);
        using ProfileLease lease = ProfileLease.Acquire(w.Profile);
        await using ArchiveCatalog reopened = await ArchiveCatalog.OpenAsync(w.Profile, lease);
        NativeMediaMergeOperation op = Assert.Single((await reopened.ReadNativeMediaMergeOperationsPageAsync()).Items);
        Assert.Equal(operation, op.OperationId);
        Assert.Equal(NativeMediaImportOperationState.Committed, op.State);
        Assert.Equal(committedPlan.CatalogBytes.ToArray(), op.CatalogBytes.ToArray());
        NativeMediaMergeInspection inspection = await reopened.InspectNativeMediaMergeOperationAsync(
            operation, op.RequestSha256, new MediaStore());
        Assert.Equal(NativeMediaImportFilesystemStatus.Inspected, inspection.FilesystemStatus);
        Assert.All(inspection.Children, child =>
        {
            Assert.Equal(PassiveMediaCandidateStatus.Ready, child.Status);
            Assert.True(child.MatchesOperation);
        });
        NativeMediaMergeResult replay = Assert.IsType<NativeMediaMergeResult>(
            await reopened.ReadNativeMediaMergeReceiptAsync(operation, committedPlan.RequestSha256));
        Assert.Equal(NativeMediaImportCommitStatus.Committed, replay.Status);
        Assert.True(replay.IsReplay);
        Assert.Equal(before, Files(originalRoot));
        NativeTransferPackage exported = await ReexportAsync(reader, committedPlan.ExportBudget);
        Assert.Equal("901", Assert.Single(exported.Catalog.Threads[0].RetainedBindings).PostId);
    }

    [Fact]
    public async Task ExportBudgetMatchesActualWriterForCurrentPlusRetainedUnion()
    {
        using Workspace w = new();
        byte[] current = [53, 59, 61], retained = [67, 71];
        using ProfileLease lease = ProfileLease.Acquire(w.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(w.Profile, lease);
        await SeedAsync(lease, catalog, w);
        using ProfileAdmissionToken admission = await EnterExclusiveAsync(lease);

        NativeTransferCatalog input = CatalogWith(
            posts: [new(new("17", 2, PostFieldPresence.Attachments | PostFieldPresence.PostedAt, At, Attachments: [Attachment("17", current, "current").Metadata]), [Attachment("17", current, "current")])],
            retained: [Retained("902", retained, "retained")]);
        await using NativeTransferImportStage stage = await StageAsync(w, input, current, retained);
        NativeMediaMergePlan plan = Assert.IsType<NativeMediaMergePlan>(
            (await catalog.PrepareNativeMediaMergeAsync(stage)).Plan);
        Assert.Equal(NativeMediaImportCommitStatus.Committed,
            (await CommitMergeWithExclusiveAsync(admission, catalog, plan, ProfileExclusiveDisposition.SafeToResume)).Status);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(w.Profile);
        TransferArchiveFreezeResult frozen = await NativeTransferArchiveExporter.FreezeAsync(reader, Id, At, Guid.NewGuid());
        using MemoryStream output = new();
        Assert.True((await NativeTransferArchiveExporter.WriteAsync(frozen.Snapshot!, output)).IsComplete);
        Assert.Equal(plan.ExportBudget.ContainerBytes, output.Length);
        TransferPackageInspection inspected = await NativeTransferPackageValidator.InspectAsync(output);
        Assert.True(inspected.IsPackageValid, inspected.ToString());
        NativeTransferPackage package = inspected.Package!;
        using ZipArchive zip = new(output, ZipArchiveMode.Read, leaveOpen: true);
        long payloadBytes = package.Envelope.Payloads.Sum(x => x.Length);
        long manifestBytes = zip.GetEntry("manifest.json")!.Length;
        Assert.Equal(plan.ExportBudget.CatalogBytes, package.Envelope.CatalogBytes.Length);
        Assert.Equal(plan.ExportBudget.ManifestBytes, manifestBytes);
        Assert.Equal(plan.ExportBudget.PayloadBytes, payloadBytes);
        Assert.Equal(plan.ExportBudget.ZipOverhead,
            output.Length - payloadBytes - manifestBytes - package.Envelope.CatalogBytes.Length);
        foreach (byte[] expected in new[] { Bytes, current, retained })
        {
            await using Stream stream = zip.GetEntry("payload/" + Hash(expected))!.Open();
            using MemoryStream actual = new();
            await stream.CopyToAsync(actual);
            Assert.Equal(expected, actual.ToArray());
        }
    }

    [Fact]
    public async Task ExactRetainedToCurrentReuseKeepsOriginalBindingHistoryWithoutNewActiveRoot()
    {
        using Workspace w = new();
        byte[] bytes = [73, 79, 83];
        using ProfileLease lease = ProfileLease.Acquire(w.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(w.Profile, lease);
        await SeedAsync(lease, catalog, w);
        using ProfileAdmissionToken admission = await EnterExclusiveAsync(lease);

        TransferRetainedBinding retained = Retained("17", bytes, "reuse");
        await using (NativeTransferImportStage historical = await StageAsync(w, CatalogWith(retained: [retained]), bytes))
        {
            NativeMediaMergePlan initial = Assert.IsType<NativeMediaMergePlan>(
                (await catalog.PrepareNativeMediaMergeAsync(historical)).Plan);
            Assert.Equal(NativeMediaImportCommitStatus.Committed,
                (await CommitMergeWithExclusiveAsync(admission, catalog, initial, ProfileExclusiveDisposition.SafeToResume)).Status);
        }
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(w.Profile);
        ArchivedThreadView before = (await reader.ReadThreadAsync(Id))!;
        MediaAssetSnapshot[] snapshots = await Task.WhenAll(before.MediaDirectories.Select(x => MediaAssetReader.OpenAsync(x)));
        ResolvedMediaAttachment old = Assert.IsType<ResolvedMediaAttachment>(
            MediaAttachmentResolver.Resolve(snapshots, Id, "17", retained.Attachment.Metadata));
        Dictionary<string, Dictionary<string, string>> roots = before.MediaDirectories.ToDictionary(x => x, Files);
        TransferAttachment current = retained.Attachment with { History = new("strict", At.AddDays(-1), false, false) };
        NativeTransferCatalog input = CatalogWith(posts:
            [new(new("17", 2, PostFieldPresence.Attachments | PostFieldPresence.PostedAt, At, Attachments: [current.Metadata]), [current])]);
        await using NativeTransferImportStage stage = await StageAsync(w, input, bytes);
        NativeMediaMergePlan plan = Assert.IsType<NativeMediaMergePlan>((await catalog.PrepareNativeMediaMergeAsync(stage)).Plan);
        Assert.Empty(plan.SafeKeys);
        Assert.Single(plan.FullKeys);
        NativeMediaMergeResult result = await CommitMergeWithExclusiveAsync(admission, catalog, plan, ProfileExclusiveDisposition.SafeToResume);
        Assert.Equal(NativeMediaImportCommitStatus.Committed, result.Status);
        Assert.Equal(1, result.NewPosts);
        Assert.Equal(1, result.NewAttachments);
        Assert.Equal(0, result.NewBindings);
        ArchivedThreadView after = (await reader.ReadThreadAsync(Id))!;
        Assert.Equal(before.MediaDirectories.ToArray(), after.MediaDirectories.ToArray());
        Assert.Equal(ThreadSourceState.NotFound, after.Summary.SourceState);
        foreach (string root in after.MediaDirectories) Assert.Equal(roots[root], Files(root));
        ResolvedMediaAttachment reused = Assert.IsType<ResolvedMediaAttachment>(MediaAttachmentResolver.Resolve(
            snapshots, Id, "17", Assert.Single(after.Posts.Single(x => x.PostId == "17").Attachments!)));
        Assert.Equal(old.Binding, reused.Binding);
        Assert.Equal(old.Asset, reused.Asset);
        Assert.Equal(old.Snapshot.Directory, reused.Snapshot.Directory);
        Assert.Equal(bytes, await ReadAsync(reused));
        NativeTransferPackage exported = await ReexportAsync(reader, plan.ExportBudget);
        Assert.Empty(exported.Catalog.Threads[0].RetainedBindings);
        Assert.Equal(retained.Attachment.History,
            Assert.Single(exported.Catalog.Threads[0].Posts.Single(x => x.Metadata.PostId == "17").Attachments).History);
        NativeMediaMergeOperation op = (await catalog.ReadNativeMediaMergeOperationsPageAsync()).Items.Single(x => x.OperationId == plan.OperationId);
        Assert.DoesNotContain(op.FullDirectory!, after.MediaDirectories);
        Assert.Null(op.SafeDirectory);
    }

    [Fact]
    public async Task SameRetainedKeyWithDifferentBytesIsInactiveWhileIndependentRetainedAdditionIsSafe()
    {
        using Workspace w = new();
        byte[] conflictBytes = Bytes.ToArray();
        conflictBytes[0] ^= 1;
        byte[] safeBytes = [89, 97, 101];
        using ProfileLease lease = ProfileLease.Acquire(w.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(w.Profile, lease);
        await SeedAsync(lease, catalog, w);
        using ProfileAdmissionToken admission = await EnterExclusiveAsync(lease);

        await using ArchiveReader reader = await ArchiveReader.OpenAsync(w.Profile);
        ArchivedThreadView before = (await reader.ReadThreadAsync(Id))!;
        string root = Assert.Single(before.MediaDirectories);
        Dictionary<string, string> oldFiles = Files(root);
        MediaAssetSnapshot original = await MediaAssetReader.OpenAsync(root);
        CapturedAttachment existing = Assert.Single(before.Posts.Single(x => x.PostId == "15").Attachments!);
        ResolvedMediaAttachment old = Assert.IsType<ResolvedMediaAttachment>(
            MediaAttachmentResolver.Resolve([original], Id, "15", existing));
        TransferAttachment conflict = new(existing, TransferMediaRole.Original,
            TransferContentStatus.Contained, Hash(conflictBytes), null);
        TransferRetainedBinding safe = Retained("999", safeBytes, "safe");
        NativeTransferCatalog input = CatalogWith(retained: [new("15", conflict), safe]);
        await using NativeTransferImportStage stage = await StageAsync(w, input, conflictBytes, safeBytes);
        NativeMediaMergePreparation prepared = await catalog.PrepareNativeMediaMergeAsync(stage);
        Assert.Equal(NativeMediaMergeError.None, prepared.Error);
        NativeMediaMergePlan plan = Assert.IsType<NativeMediaMergePlan>(prepared.Plan);
        Assert.Equal(1, plan.Conflicts);
        Assert.Equal(safe.Attachment.Metadata.AttachmentRevisionKey, Assert.Single(plan.SafeKeys));
        NativeMediaMergeResult result = await CommitMergeWithExclusiveAsync(admission, catalog, plan, ProfileExclusiveDisposition.SafeToResume);
        Assert.Equal(NativeMediaImportCommitStatus.Committed, result.Status);
        Assert.Equal(0, result.NewPosts);
        Assert.Equal(0, result.NewAttachments);
        Assert.Equal(1, result.NewBindings);
        NativeMediaMergeOperation operation = Assert.Single((await catalog.ReadNativeMediaMergeOperationsPageAsync()).Items);
        ArchivedThreadView after = (await reader.ReadThreadAsync(Id))!;
        Assert.DoesNotContain(operation.FullDirectory!, after.MediaDirectories);
        Assert.Contains(operation.SafeDirectory!, after.MediaDirectories);
        MediaAssetSnapshot full = await MediaAssetReader.OpenAsync(operation.FullDirectory!);
        ResolvedMediaAttachment preservedConflict = Assert.IsType<ResolvedMediaAttachment>(
            MediaAttachmentResolver.Resolve([full], Id, "15", existing));
        Assert.Equal(conflictBytes, await ReadAsync(preservedConflict));
        MediaAssetSnapshot[] active = await Task.WhenAll(after.MediaDirectories.Select(x => MediaAssetReader.OpenAsync(x)));
        ResolvedMediaAttachment unchanged = Assert.IsType<ResolvedMediaAttachment>(
            MediaAttachmentResolver.Resolve(active, Id, "15", existing));
        Assert.Equal(old.Binding, unchanged.Binding);
        Assert.Equal(old.Asset, unchanged.Asset);
        Assert.Equal(Bytes, await ReadAsync(unchanged));
        Assert.Equal(oldFiles, Files(root));
        NativeTransferPackage exported = await ReexportAsync(reader, plan.ExportBudget);
        Assert.DoesNotContain(exported.Envelope.Payloads, x => x.Sha256 == Hash(conflictBytes));
        Assert.Equal("999", Assert.Single(exported.Catalog.Threads[0].RetainedBindings).PostId);
    }

    // Direct synthetic storage owner via existing Core IVT, not public Registry startup.
    // Await initial store setup before Ready; retain the real token through reconciliation.
    private static ValueTask<ProfileAdmissionToken> EnterExclusiveAsync(ProfileLease lease)
    {
        if (lease.Admission.Phase == ProfileAdmissionPhase.Starting) lease.Admission.CompleteStartup();
        return lease.Admission.EnterExclusiveAsync();
    }

    private static async Task<NativeMediaMergeResult> CommitMergeWithExclusiveAsync(
        ProfileAdmissionToken admission, ArchiveCatalog catalog, NativeMediaMergePlan plan,
        ProfileExclusiveDisposition expectedDisposition, Action<NativeMediaImportPhase>? checkpoint = null)
    {
        ProfileExclusiveOutcome<NativeMediaMergeResult> outcome =
            await catalog.CommitNativeMediaMergeAdmittedAsync(admission, plan, new MediaStore(), checkpoint: checkpoint);
        Assert.Equal(expectedDisposition, outcome.Disposition);
        catalog.EnsureTransferAdmission(admission); // Borrowed authority survives all work and reconciliation.
        return outcome.Result;
    }

    private static async Task<NativeMediaImportCommitResult> CommitSeedWithExclusiveAsync(
        ProfileAdmissionToken admission, ArchiveCatalog catalog, NativeMediaImportPlan plan)
    {
        ProfileExclusiveOutcome<NativeMediaImportCommitResult> outcome =
            await catalog.CommitNativeMediaImportAdmittedAsync(admission, plan, new MediaStore());
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, outcome.Disposition);
        catalog.EnsureTransferAdmission(admission); // The consumer must leave its borrowed owner live.
        return outcome.Result;
    }

    private static async Task<NativeTransferPackage> ReexportAsync(ArchiveReader reader,
        NativeTransferPackageWriter.TransferPackageBudget budget)
    {
        TransferArchiveFreezeResult frozen = await NativeTransferArchiveExporter.FreezeAsync(reader, Id, At, Guid.NewGuid());
        Assert.True(frozen.IsFrozen, frozen.Error.ToString());
        using MemoryStream output = new();
        TransferArchiveWriteResult written = await NativeTransferArchiveExporter.WriteAsync(frozen.Snapshot!, output);
        Assert.True(written.IsComplete, written.Error + "/" + written.WriteError);
        TransferPackageInspection inspected = await NativeTransferPackageValidator.InspectAsync(output);
        Assert.True(inspected.IsPackageValid, inspected.ToString());
        NativeTransferPackage package = inspected.Package!;
        using ZipArchive zip = new(output, ZipArchiveMode.Read, leaveOpen: true);
        long payload = package.Envelope.Payloads.Sum(x => x.Length);
        long manifest = zip.GetEntry("manifest.json")!.Length;
        Assert.Equal(budget.ContainerBytes, output.Length);
        Assert.Equal(budget.CatalogBytes, package.Envelope.CatalogBytes.Length);
        Assert.Equal(budget.ManifestBytes, manifest);
        Assert.Equal(budget.PayloadBytes, payload);
        Assert.Equal(budget.ZipOverhead, output.Length - payload - manifest - package.Envelope.CatalogBytes.Length);
        return package;
    }

    private static NativeTransferCatalog CatalogWith(ImmutableArray<TransferPost> posts = default,
        ImmutableArray<TransferRetainedBinding> retained = default)
        => Content() with
        {
            Threads = [Content().Threads[0] with
            {
                Posts = posts.IsDefault ? [] : posts,
                RetainedBindings = retained.IsDefault ? [] : retained
            }]
        };

    private static CapturedAttachment Metadata(string post, byte[] bytes, string revision)
    {
        MediaProviderMetadata provider = new("111", "original", "https://i.4cdn.org/b/111.jpg",
            revision + ".bin", bytes.Length, Md5(bytes), Hash(bytes));
        return new(0, provider.MediaId, provider.Variant, provider.Url, provider.OriginalFileName,
            ExpectedSize: provider.ExpectedSize, ExpectedMd5: provider.ExpectedMd5)
        {
            ExpectedSha256 = provider.ExpectedSha256,
            AttachmentRevisionKey = MediaRevisionKeys.CreateAttachmentKey(Id, post, 0, provider)
        };
    }

    private static TransferAttachment Attachment(string post, byte[] bytes, string revision)
        => new(Metadata(post, bytes, revision), TransferMediaRole.Original, TransferContentStatus.Contained,
            Hash(bytes), null);

    private static TransferRetainedBinding Retained(string post, byte[] bytes, string revision)
        => new(post, Attachment(post, bytes, revision) with
        { History = new("strict", At.AddDays(-10), false, false) });

    private static async Task SeedAsync(ProfileLease lease, ArchiveCatalog catalog, Workspace w)
    {
        await using NativeTransferImportStage stage = await Stage(w, Content());
        NativeMediaImportPreparation prepared = await catalog.PrepareNativeMediaImportAsync(stage);
        Assert.Equal(NativeMediaImportError.None, prepared.Error);
        using ProfileAdmissionToken admission = await EnterExclusiveAsync(lease);
        Assert.Equal(NativeMediaImportCommitStatus.Committed,
            (await CommitSeedWithExclusiveAsync(admission, catalog, prepared.Plan!)).Status);
    }

    private static async Task<NativeTransferImportStage> StageAsync(Workspace w,
        NativeTransferCatalog input, params byte[][] payloads)
    {
        using MemoryStream package = new();
        TransferPayloadSource[] sources = payloads.DistinctBy(Hash).Select(bytes =>
            new TransferPayloadSource(new(bytes.Length, Hash(bytes)),
                _ => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false)))).ToArray();
        TransferWriteResult written = await NativeTransferPackageWriter.WriteAsync(package, input, sources, Guid.NewGuid());
        Assert.True(written.IsComplete, written.Error.ToString());
        package.Position = 0;
        NativeTransferStageResult staged = await NativeTransferImportStage.CreateAsync(package, w.StageRoot);
        Assert.Equal(NativeTransferStageError.None, staged.Error);
        return Assert.IsType<NativeTransferImportStage>(staged.Stage);
    }

    private static async Task<byte[]> ReadAsync(ResolvedMediaAttachment resolved)
    {
        Assert.False(resolved.IsLegacy);
        await using Stream stream = await resolved.Snapshot.OpenVerifiedReadAsync(resolved.Asset.AssetId);
        using MemoryStream output = new();
        await stream.CopyToAsync(output);
        return output.ToArray();
    }
}
