using System.Collections.Immutable;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;
using Microsoft.Data.Sqlite;
using static MeowChan.Tests.Transfer.NativeV2ImportFixture;

namespace MeowChan.Tests.Transfer;

// W32b5 conflict lane: real v4 package -> retained stage -> admitted import/merge.
// Deliberately expects v2 prepare success, including before Root removes the v4 gate.
public sealed class NativeV2MergeConflictInventoryTests
{
    private static readonly byte[] Alternative = [91, 92, 93, 94, 95];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CurrentSlotConflictKeepsOriginalWhileIndependentZeroBindingInventoryIsRegistered(bool preview)
    {
        await using NativeV2ImportFixture f = await CreateAsync();
        TransferAttachment conflict = DifferentOriginal();
        TransferAttachment independent = Attachment("999", TransferContentStatus.Omitted, derived: preview);
        NativeTransferCatalog incoming = Incoming([Post("15", conflict)], [new("999", independent)]);
        MergeObservation merged = await MergeAsync(f, incoming,
            preview ? [Alternative, Preview] : [Alternative], evidence: 1, derived: preview ? 1 : 0, safe: true);

        Assert.Contains(merged.Operation.Full.Selection!.Members, x =>
            x.Id.Role == NativeMediaMemberRole.OriginalPayload && x.Id.Occurrence.PostId == "15");
        Assert.All(merged.Operation.Safe.Selection!.Members, x => Assert.Equal("999", x.Id.Occurrence.PostId));
        Assert.Empty(merged.Safe!.AttachmentBindings);
        Assert.Equal(0, merged.Safe.Count);
        Assert.Equal("999", Assert.Single(merged.Safe.OriginalEvidence).Occurrence.PostId);
        Assert.Equal(MediaObservedContentStatus.Omitted, merged.Safe.OriginalEvidence[0].IncomingStatus);
        ResolvedMediaAttachment alternative = Assert.IsType<ResolvedMediaAttachment>(
            MediaAttachmentResolver.Resolve([merged.Full], Id, "15", conflict.Metadata));
        Assert.Equal(Alternative, await ReadOriginalAsync(alternative));
        Assert.Null(MediaAttachmentResolver.Resolve(merged.Active, Id, "15", conflict.Metadata));
        if (preview)
            Assert.Equal(Preview, await ReadDerivedAsync(merged.Safe, Assert.Single(merged.Safe.DerivedRepresentations)));
        else
            Assert.Empty(merged.Safe.DerivedRepresentations);
    }

    [Fact]
    public async Task PromoteRootMakesTheContainedAlternativeActiveAndKeepsTheOlderRootUntouched()
    {
        await using NativeV2ImportFixture f = await CreateAsync();
        TransferAttachment conflict = DifferentOriginal();
        MergeObservation merged = await MergeAsync(f, Incoming([Post("15", conflict)], []),
            [Alternative], evidence: 0, derived: 0, safe: false);
        string key;
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(f.Files.Profile);
        ArchivedThreadView thread = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Id));
        string seedRoot = thread.MediaDirectories.First(d => !PathsContain(merged.Operation, d));
        MediaAssetSnapshot seed = await MediaAssetReader.OpenAsync(seedRoot);
        string seedManifest = File.ReadAllText(Path.Combine(seedRoot, ".meowchan-manifest.json"));
        CapturedAttachment slot = Assert.Single(Assert.Single(thread.GetPosts(), x => x.PostId == "15").Attachments!);
        MediaAttachmentBinding active = Assert.Single(seed.AttachmentBindings.Values);
        key = active.Reference.RevisionKey;
        string activeSha = active.Actual.Sha256;
        Assert.NotEqual(Hash(Alternative), activeSha);

        string target = Path.Combine(Path.GetDirectoryName(merged.Operation.Full.Directory!)!, "promote-" + Guid.NewGuid().ToString("N"));
        PromotedMediaRoot root = await f.Store.CreatePromoteRootAsync(merged.Operation.Full.Directory!, target,
            Hash(Alternative), [active]);
        Assert.Equal(target, root.Directory);
        MediaAssetSnapshot promote = await MediaAssetReader.OpenAsync(target);
        Assert.Equal(MediaManifestStatus.Available, promote.Status);
        Assert.Equal(root.Generation, promote.Generation);
        Assert.Equal(new MediaSupersession(key, activeSha), Assert.Single(promote.Supersedes));

        CapturedAttachment sql = slot;
        // Both roots active: the explicit supersession decides, in either order.
        foreach (MediaAssetSnapshot[] order in new[] { new[] { seed, promote }, new[] { promote, seed } })
        {
            ResolvedMediaAttachment resolved = Assert.IsType<ResolvedMediaAttachment>(MediaAttachmentResolver.Resolve(order, Id, "15", sql));
            Assert.Equal(Alternative, await ReadOriginalAsync(resolved));
        }
        // The previous bytes and root are unchanged and still readable on their own.
        Assert.Equal(seedManifest, File.ReadAllText(Path.Combine(seedRoot, ".meowchan-manifest.json")));
        Assert.Equal(await ReadOriginalAsync(Assert.IsType<ResolvedMediaAttachment>(MediaAttachmentResolver.Resolve([seed], Id, "15", sql))),
            await File.ReadAllBytesAsync(Assert.IsType<ResolvedMediaAttachment>(MediaAttachmentResolver.Resolve([seed], Id, "15", sql)).Asset.Path));
        Assert.Empty(Directory.EnumerateDirectories(Path.GetDirectoryName(target)!, "*.part-*"));
        // A wrong digest or a binding the alternative's root does not hold is refused and leaves nothing.
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Store.CreatePromoteRootAsync(merged.Operation.Full.Directory!,
            target + "-x", new string('a', 64), [active]));
        Assert.False(Directory.Exists(target + "-x"));
        Assert.Empty(Directory.EnumerateDirectories(Path.GetDirectoryName(target)!, "*.part-*"));
        await Assert.ThrowsAsync<IOException>(() => f.Store.CreatePromoteRootAsync(merged.Operation.Full.Directory!, target,
            Hash(Alternative), [active]));
    }

    [Fact]
    public async Task CatalogPromoteRegistersTheRootAndTheDecisionTogetherAndIsRepeatable()
    {
        await using NativeV2ImportFixture f = await CreateAsync();
        // Same slot and provider tuple, different original bytes carried in the package: a ledgered alternative.
        TransferAttachment conflict = Attachment(derived: false) with
        {
            PayloadSha256 = Hash(Alternative),
            OriginalByteVersion = new(Alternative.Length, Hash(Alternative), Md5(Alternative), null)
        };
        MergeObservation merged = await MergeAsync(f, Incoming([Post("15", conflict)], []),
            [Alternative], evidence: 0, derived: 0, safe: false);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(f.Files.Profile);
        PersistedMediaConflict row = Assert.Single((await reader.ReadMediaConflictsAsync(0, 10)).Items);
        string id = row.Entry.Identity.IdentitySha256;
        ArchivedThreadView before = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Id));
        CapturedAttachment slot = Assert.Single(Assert.Single(before.GetPosts(), x => x.PostId == "15").Attachments!);
        string seedRoot = before.MediaDirectories.Single();
        string seedManifest = File.ReadAllText(Path.Combine(seedRoot, ".meowchan-manifest.json"));

        MediaPromoteResult first = await f.Catalog.PromoteMediaConflictAsync(id, f.Store);
        Assert.Equal(MediaPromoteStatus.Promoted, first.Status);
        ArchivedThreadView after = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Id));
        Assert.Contains(first.Directory!, after.MediaDirectories);
        Assert.Equal(2, after.MediaDirectories.Count);
        var snapshots = new List<MediaAssetSnapshot>();
        foreach (string directory in after.MediaDirectories) snapshots.Add(await MediaAssetReader.OpenAsync(directory));
        Assert.Equal(Alternative, await ReadOriginalAsync(Assert.IsType<ResolvedMediaAttachment>(
            MediaAttachmentResolver.Resolve(snapshots, Id, "15", slot))));
        Assert.Equal(NativeMediaConflictResolutionStatus.Promoted,
            Assert.Single((await reader.ReadMediaConflictsAsync(0, 10)).Items).Entry.Resolution);
        Assert.Equal(seedManifest, File.ReadAllText(Path.Combine(seedRoot, ".meowchan-manifest.json")));

        MediaPromoteResult again = await f.Catalog.PromoteMediaConflictAsync(id, f.Store);
        Assert.Equal(MediaPromoteStatus.AlreadyPromoted, again.Status);
        Assert.Equal(2, Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Id)).MediaDirectories.Count);
        Assert.Equal(MediaPromoteStatus.Missing, (await f.Catalog.PromoteMediaConflictAsync(new string('0', 64), f.Store)).Status);
    }

    [Fact]
    public async Task CatalogPromoteIsRefusedWhenTheAlternativeHasNoStoredBytes()
    {
        await using NativeV2ImportFixture f = await CreateAsync();
        TransferAttachment original = Attachment();
        TransferOriginalByteVersion otherVersion = new(Alternative.Length, Hash(Alternative), Md5(Alternative), null);
        TransferAttachment omitted = original with
        {
            Status = TransferContentStatus.Omitted, PayloadSha256 = null, OriginalByteVersion = otherVersion,
            DerivedRepresentations = [original.DerivedRepresentations[0] with { SourceByteVersion = otherVersion }]
        };
        MergeObservation merged = await MergeAsync(f, Incoming(retained: [new("15", omitted)]), [Preview], evidence: 0, derived: 0, safe: false);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(f.Files.Profile);
        PersistedMediaConflict row = Assert.Single((await reader.ReadMediaConflictsAsync(0, 10)).Items);
        MediaPromoteResult result = await f.Catalog.PromoteMediaConflictAsync(row.Entry.Identity.IdentitySha256, f.Store);
        Assert.Equal(MediaPromoteStatus.BytesUnavailable, result.Status);
        Assert.Equal(NativeMediaConflictResolutionStatus.Open, Assert.Single((await reader.ReadMediaConflictsAsync(0, 10)).Items).Entry.Resolution);
        Assert.Single(Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Id)).MediaDirectories);
    }

    private static bool PathsContain(NativeMediaPersistedMergeOperation operation, string directory)
        => string.Equals(directory, operation.Full.Directory, StringComparison.Ordinal)
            || string.Equals(directory, operation.Safe.Directory, StringComparison.Ordinal);

    [Fact]
    public async Task ExactOccurrenceWithDifferentSourceVersionIsOnlyAnInactiveFullAlternative()
    {
        await using NativeV2ImportFixture f = await CreateAsync();
        TransferAttachment original = Attachment();
        TransferOriginalByteVersion otherVersion = new(Alternative.Length, Hash(Alternative), Md5(Alternative), null);
        // Same exact occurrence/provider metadata; only the claimed original byte version changes.
        // An omitted original is not validated as a new contained original payload.
        TransferAttachment conflict = original with
        {
            Status = TransferContentStatus.Omitted,
            PayloadSha256 = null,
            OriginalByteVersion = otherVersion,
            DerivedRepresentations = [original.DerivedRepresentations[0] with { SourceByteVersion = otherVersion }]
        };
        MergeObservation merged = await MergeAsync(f, Incoming(retained: [new("15", conflict)]),
            [Preview], evidence: 0, derived: 0, safe: false);

        MediaOriginalEvidence alternative = Assert.Single(merged.Full.OriginalEvidence);
        Assert.Equal(original.Metadata.AttachmentRevisionKey, alternative.Occurrence.OriginalRevisionKey);
        Assert.Equal(Hash(Alternative), alternative.OriginalByteVersion!.Sha256);
        Assert.Equal(Hash(Alternative), Assert.Single(merged.Full.DerivedRepresentations).SourceVersion.Sha256);
        Assert.DoesNotContain(merged.Active.SelectMany(x => x.OriginalEvidence),
            x => x.OriginalByteVersion?.Sha256 == Hash(Alternative));
        Assert.DoesNotContain(merged.Active.SelectMany(x => x.DerivedRepresentations),
            x => x.SourceVersion.Sha256 == Hash(Alternative));
        Assert.Empty(merged.Full.AttachmentBindings);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(f.Files.Profile);
        (int total, List<PersistedMediaConflict> rows) = await reader.ReadMediaConflictsAsync(0, 100);
        PersistedMediaConflict row = Assert.Single(rows);
        Assert.Equal(1, total);
        Assert.Equal(Hash(Alternative), row.Entry.Candidate.VariantSha256);
        Assert.Equal(NativeMediaConflictVariantStatus.Inactive, row.Entry.Candidate.Status);
        Assert.NotEqual(Hash(Alternative), row.Entry.Target.VariantSha256);
    }

    [Fact]
    public async Task SamePreviewIdentityWithDifferentBytesNeverEntersActiveUnion()
    {
        await using NativeV2ImportFixture f = await CreateAsync();
        TransferAttachment parent = Attachment();
        TransferAttachment conflict = parent with
        {
            DerivedRepresentations = [parent.DerivedRepresentations[0] with { PayloadSha256 = Hash(Alternative) }]
        };
        MergeObservation merged = await MergeAsync(f, Incoming(retained: [new("15", conflict)]),
            [Original, Alternative], evidence: 0, derived: 0, safe: false);

        MediaDerivedRepresentation inactive = Assert.Single(merged.Full.DerivedRepresentations);
        MediaAssetSnapshot activeRoot = Assert.Single(merged.Active);
        MediaDerivedRepresentation active = Assert.Single(activeRoot.DerivedRepresentations);
        Assert.Equal(active.Parent, inactive.Parent);
        Assert.Equal(active.SourceVersion, inactive.SourceVersion);
        Assert.Equal(active.Kind, inactive.Kind);
        Assert.NotEqual(active.Blob!.Actual.Sha256, inactive.Blob!.Actual.Sha256);
        Assert.Equal(Alternative, await ReadDerivedAsync(merged.Full, inactive));
        Assert.Equal(Preview, await ReadDerivedAsync(activeRoot, active));
    }

    [Fact]
    public async Task EqualShaDerivedAdditionIsNotPromotedToOriginalEvenBesideAConflict()
    {
        await using NativeV2ImportFixture f = await CreateAsync();
        TransferAttachment parent = Attachment("999", TransferContentStatus.Omitted);
        TransferAttachment equalSha = parent with
        {
            DerivedRepresentations = [parent.DerivedRepresentations[0] with { PayloadSha256 = Hash(Original) }]
        };
        MergeObservation merged = await MergeAsync(f,
            Incoming([Post("15", DifferentOriginal())], [new("999", equalSha)]),
            [Alternative, Original], evidence: 1, derived: 1, safe: true);

        Assert.DoesNotContain(merged.Operation.Safe.Selection!.Members, x => x.Id.Role == NativeMediaMemberRole.OriginalPayload);
        Assert.Empty(merged.Safe!.AttachmentBindings);
        Assert.Equal(0, merged.Safe.Count);
        MediaDerivedRepresentation preview = Assert.Single(merged.Safe.DerivedRepresentations);
        Assert.Equal(preview.SourceVersion.Sha256, preview.Blob!.Actual.Sha256);
        Assert.Equal(Original, await ReadDerivedAsync(merged.Safe, preview));
        Assert.Null(MediaAttachmentResolver.Resolve(merged.Active, Id, "999", equalSha.Metadata));
        Assert.Single(merged.Active.SelectMany(x => x.AttachmentBindings)); // only seed's original
    }

    [Fact]
    public async Task SafePreviewOfExistingOriginalUsesContextOnlyParentAndCountsNoFreshEvidence()
    {
        await using NativeV2ImportFixture f = await CreateAsync(Content(derived: false), [Original]);
        TransferAttachment conflict = DifferentOriginal();
        MergeObservation merged = await MergeAsync(f,
            Incoming([Post("15", conflict)], [new("15", Attachment())]),
            [Alternative, Original, Preview], evidence: 0, derived: 1, safe: true);

        NativeMediaSelectedMember context = Assert.Single(merged.Operation.Safe.Selection!.Members,
            x => x.Id.Role == NativeMediaMemberRole.OriginalEvidence);
        Assert.True(context.ContextOnly);
        Assert.Equal(Attachment().Metadata.AttachmentRevisionKey, context.Id.Occurrence.OriginalRevisionKey);
        Assert.DoesNotContain(merged.Operation.Safe.Selection.Members, x => x.Id.Role == NativeMediaMemberRole.OriginalPayload);
        Assert.Empty(merged.Safe!.AttachmentBindings);
        Assert.Equal(Preview, await ReadDerivedAsync(merged.Safe, Assert.Single(merged.Safe.DerivedRepresentations)));
    }

    private sealed record MergeObservation(NativeMediaPersistedMergeOperation Operation,
        MediaAssetSnapshot Full, MediaAssetSnapshot? Safe, MediaAssetSnapshot[] Active);

    private static async Task<MergeObservation> MergeAsync(NativeV2ImportFixture f,
        NativeTransferCatalog incoming, byte[][] payloads, int evidence, int derived, bool safe)
    {
        using ProfileAdmissionToken admission = await f.ExclusiveAsync();
        ProfileExclusiveOutcome<NativeMediaImportCommitResult> seed = await f.CommitAsync(admission);
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, seed.Disposition);
        Assert.Equal(NativeMediaImportCommitStatus.Committed, seed.Result.Status);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(f.Files.Profile);
        ArchivedThreadView before = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Id));
        string seedRoot = Assert.Single(before.MediaDirectories);
        Dictionary<string, string> seedBytes = PayloadFiles(seedRoot);
        MediaAssetSnapshot seedSnapshot = await MediaAssetReader.OpenAsync(seedRoot);
        CapturedAttachment sqlOriginal = Assert.Single(Assert.Single(before.GetPosts(), x => x.PostId == "15").Attachments!);
        ResolvedMediaAttachment old = Assert.IsType<ResolvedMediaAttachment>(
            MediaAttachmentResolver.Resolve([seedSnapshot], Id, "15", sqlOriginal));

        await using NativeTransferImportStage stage = await StageAsync(f.Files, incoming, payloads);
        NativeMediaMergePreparation preparation = await f.Catalog.PrepareNativeMediaMergeAsync(stage);
        Assert.Equal(NativeMediaMergeError.None, preparation.Error);
        NativeMediaMergePlan plan = Assert.IsType<NativeMediaMergePlan>(preparation.Plan);
        Assert.Equal(2, plan.PlanVersion);
        NativeMediaMergeV2State state = Assert.IsType<NativeMediaMergeV2State>(plan.Representation);
        Assert.Equal(safe, plan.HasSafeCandidate);
        Assert.Equal(0, plan.NewBindingCount);
        Assert.Empty(plan.AddedPosts);
        Assert.Empty(plan.UpdatedPosts);
        Assert.Empty(plan.AddedAttachments);
        Assert.True(plan.Conflicts > 0);
        Assert.False(state.ReuseFullAsSafe);
        ProfileExclusiveOutcome<NativeMediaMergeResult> outcome =
            await f.Catalog.CommitNativeMediaMergeAdmittedAsync(admission, plan, f.Store);
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, outcome.Disposition);
        f.Catalog.EnsureTransferAdmission(admission);
        NativeMediaMergeResult result = outcome.Result;
        Assert.Equal(NativeMediaImportCommitStatus.Committed, result.Status);
        Assert.False(result.IsReplay);
        Assert.Equal(0, result.NewPosts);
        Assert.Equal(0, result.UpdatedPosts);
        Assert.Equal(0, result.NewAttachments);
        Assert.Equal(0, result.NewBindings);
        Assert.Equal(plan.Conflicts, result.Conflicts);
        // Every byte-level alternative of the plan is durable in the same commit; replays add nothing.
        await using (ArchiveReader ledgerReader = await ArchiveReader.OpenAsync(f.Files.Profile))
        {
            (int ledgerTotal, List<PersistedMediaConflict> ledgerRows) = await ledgerReader.ReadMediaConflictsAsync(0, 100);
            Assert.Equal(plan.ConflictLedger.Length, ledgerTotal);
            Assert.Equal(plan.ConflictLedger.Select(e => e.Identity.IdentitySha256).OrderBy(x => x, StringComparer.Ordinal),
                ledgerRows.Select(r => r.Entry.Identity.IdentitySha256));
            Assert.All(ledgerRows, r => Assert.Equal(NativeMediaConflictResolutionStatus.Open, r.Entry.Resolution));
        }
        Assert.Equal(evidence, result.EvidenceAdditions);
        Assert.Equal(derived, result.DerivedDescriptorAdditions);
        Assert.Equal(derived, result.ContainedDerivedAdditions);

        using SqliteConnection connection = ArchiveSql.OpenConnection(f.Catalog.DatabasePath, SqliteOpenMode.ReadOnly);
        NativeMediaPersistedMergeOperation operation = Assert.IsType<NativeMediaPersistedMergeOperation>(
            ArchiveSql.ReadPersistedMediaMergeOperation(connection, null, plan.OperationId, plan.RequestSha256));
        Assert.True(state.MatchesOperation(operation, NativeMediaImportOperationState.Committed));
        Assert.Equal(state.FullSelection.CanonicalJson, operation.Full.Selection!.CanonicalJson);
        Assert.Equal(evidence, operation.Counts.EvidenceAdditions);
        Assert.Equal(derived, operation.Counts.DerivedDescriptorAdditions);
        Assert.Equal(derived, operation.Counts.ContainedDerivedAdditions);
        Assert.Equal(0, operation.Counts.NewBindings);
        ArchivedThreadView after = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Id));
        Assert.Equal(ThreadSourceState.NotFound, after.Summary.SourceState);
        Assert.Equal(before.PostCount, after.PostCount);
        Assert.Equal(before.GetPosts().Select(x => x.PostId), after.GetPosts().Select(x => x.PostId));
        Assert.DoesNotContain(after.GetPosts(), x => x.PostId == "999");
        Assert.Equal(sqlOriginal, Assert.Single(Assert.Single(after.GetPosts(), x => x.PostId == "15").Attachments!));
        Assert.Null(Assert.Single(after.GetPosts(), x => x.PostId == "16").Attachments);
        Assert.Empty(Assert.Single(after.GetPosts(), x => x.PostId == "17").Attachments!);
        Assert.Contains(seedRoot, after.MediaDirectories);
        Assert.DoesNotContain(operation.Full.Directory!, after.MediaDirectories);
        Assert.Equal(safe ? 2 : 1, after.MediaDirectories.Count);
        MediaAssetSnapshot full = await MediaAssetReader.OpenAsync(operation.Full.Directory!);
        Assert.Equal(MediaManifestStatus.Available, full.Status);
        MediaAssetSnapshot? safeSnapshot = null;
        if (safe)
        {
            Assert.Equal(state.SafeSelection.CanonicalJson, operation.Safe.Selection!.CanonicalJson);
            Assert.NotEqual(operation.Full.Directory, operation.Safe.Directory);
            Assert.Contains(operation.Safe.Directory!, after.MediaDirectories);
            safeSnapshot = await MediaAssetReader.OpenAsync(operation.Safe.Directory!);
            Assert.Equal(MediaManifestStatus.Available, safeSnapshot.Status);
        }
        else
        {
            Assert.True(state.SafeSelection.IsEmpty);
            Assert.Null(operation.Safe.Selection);
            Assert.Null(operation.Safe.Directory);
            Assert.Null(operation.Safe.InventorySha256);
            Assert.Equal(0, operation.Safe.MemberCount);
        }
        MediaAssetSnapshot[] active = await Task.WhenAll(after.MediaDirectories.Select(x => MediaAssetReader.OpenAsync(x)));
        ResolvedMediaAttachment unchanged = Assert.IsType<ResolvedMediaAttachment>(
            MediaAttachmentResolver.Resolve(active, Id, "15", sqlOriginal));
        Assert.Equal(old.Binding, unchanged.Binding);
        Assert.Equal(old.Asset, unchanged.Asset);
        Assert.Equal(Original, await ReadOriginalAsync(unchanged));
        Assert.Equal(seedBytes, PayloadFiles(seedRoot));
        Assert.Equal(seedSnapshot.Generation, (await MediaAssetReader.OpenAsync(seedRoot)).Generation);
        return new(operation, full, safeSnapshot, active);
    }

    private static NativeTransferCatalog Incoming(ImmutableArray<TransferPost> posts = default,
        ImmutableArray<TransferRetainedBinding> retained = default)
        => Content() with { Threads = [Content().Threads[0] with
        { Posts = posts.IsDefault ? [] : posts, RetainedBindings = retained.IsDefault ? [] : retained }] };

    private static TransferAttachment DifferentOriginal()
    {
        TransferAttachment original = Attachment(derived: false);
        // Public 4cdn references require a numeric media stem, distinct from seed 15.
        const string mediaId = "15001";
        MediaProviderMetadata provider = new(mediaId, "original", $"https://i.4cdn.org/b/{mediaId}.webm",
            "different.webm", Alternative.Length, Md5(Alternative), Hash(Alternative));
        CapturedAttachment metadata = original.Metadata with
        {
            MediaId = provider.MediaId, Url = provider.Url, OriginalFileName = provider.OriginalFileName,
            ExpectedSize = provider.ExpectedSize, ExpectedMd5 = provider.ExpectedMd5, ExpectedSha256 = provider.ExpectedSha256,
            AttachmentRevisionKey = MediaRevisionKeys.CreateAttachmentKey(Id, "15", 0, provider)
        };
        return original with { Metadata = metadata, PayloadSha256 = Hash(Alternative),
            OriginalByteVersion = new(Alternative.Length, Hash(Alternative), Md5(Alternative), null) };
    }

    private static async Task<byte[]> ReadOriginalAsync(ResolvedMediaAttachment attachment)
    {
        await using Stream stream = await attachment.Snapshot.OpenVerifiedReadAsync(attachment.Asset.AssetId);
        using MemoryStream bytes = new();
        await stream.CopyToAsync(bytes);
        return bytes.ToArray();
    }

    private static async Task<byte[]> ReadDerivedAsync(MediaAssetSnapshot snapshot, MediaDerivedRepresentation member)
    {
        await using Stream stream = await snapshot.OpenVerifiedDerivedReadAsync(member);
        using MemoryStream bytes = new();
        await stream.CopyToAsync(bytes);
        return bytes.ToArray();
    }
}
