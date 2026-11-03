using System.Globalization;
using MeowChan.Archive;
using MeowChan.Favorites;
using MeowChan.Storage;

namespace MeowChan.Tests;

public sealed class CuratedExportSelectionTests
{
    private static readonly ResourceId Thread = new("synthetic", "b", "100");
    private static readonly Guid Instance = Guid.Parse("178b4d42-a9ac-4af6-bfea-e564d3209f68");
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-03T00:00:00Z");
    private static string Hash(char value = 'a') => new(value, 64);
    private static FavoriteAssetKey Key(string media = "m", char hash = 'a', ResourceId? thread = null) => new(thread ?? Thread, media, "original", Hash(hash));
    private static FavoriteEntry Entry(FavoriteAssetKey? key = null, long length = 3, string? name = "original.png") => new(key ?? Key(), length, name);

    // Synthetic inert fence only: this pure kernel must never dereference or use the owner as authority.
    // Production callers obtain the snapshot via FavoriteStore.ReadTransferSnapshot(), not this helper.
    private static FavoriteStoreSnapshot Snapshot(params FavoriteEntry[] entries) => new(null!, entries, Hash('f'), Instance, 7);
    private static MediaAssetReference Asset(FavoriteAssetKey? key = null, string? assetId = null, bool current = false, long length = 3)
    {
        key ??= Key();
        return new(assetId ?? Hash('1'), key.MediaIdentifier, key.Variant, "original.png", "/synthetic/never-opened/original.png",
            length, key.Sha256, "https://fixture.invalid/media", "recorded.png", current) { Md5 = Convert.ToBase64String(new byte[16]) };
    }
    private static CuratedExportManifest Manifest(string directory = "/synthetic/root", ResourceId? thread = null,
        IEnumerable<MediaAssetReference>? assets = null, MediaManifestStatus status = MediaManifestStatus.Available,
        char generation = 'd', IReadOnlyDictionary<string, MediaAttachmentBinding>? bindings = null)
        => new(thread ?? Thread, new MediaAssetSnapshot(directory, status, status == MediaManifestStatus.Available ? 2 : null,
            status == MediaManifestStatus.Available ? Hash(generation) : null, assets ?? [], attachmentBindings: bindings));
    private static CapturedAttachment LegacyAttachment(MediaAssetReference asset, long? length = null, string? sha256 = null,
        string? mediaId = null) => new(0, mediaId ?? asset.MediaIdentifier, asset.Variant,
        asset.SourceUrl, asset.OriginalFileName, ExpectedSize: length ?? asset.Length)
    { ExpectedSha256 = sha256 ?? asset.Sha256 };

    private static ArchivedThreadView Context(CapturedPost post, ResourceId? thread = null, string directory = "/synthetic/root")
    {
        ResourceId id = thread ?? Thread;
        ArchiveThreadSummary summary = new(id, "https://fixture.invalid/thread", "Synthetic", directory,
            MeowChan.Interfaces.ThreadSourceState.NotFound, false, At, At, 1, 11);
        return new ArchivedThreadView(summary, [post], [directory], decimalPostOrder: true);
    }

    private static ArchivedThreadView Context(params CapturedAttachment[] attachments)
        => Context(new CapturedPost("101", 101, PostFieldPresence.All, At,
            Html: "<p>synthetic</p>", Attachments: attachments));
    private static CuratedExportSelection Freeze(FavoriteStoreSnapshot? snapshot = null, IEnumerable<FavoriteAssetKey>? keys = null,
        IEnumerable<CuratedExportManifest>? manifests = null, bool previews = false, CancellationToken token = default)
        => CuratedExportSelection.Freeze(snapshot ?? Snapshot(Entry()), CuratedExportScope.Thread, CuratedExportMode.Files,
            previews, [Thread], keys ?? [Key()], manifests ?? [Manifest(assets: [Asset()])], [], token);
    private static void Invalid(CuratedExportValidationError expected, Action action)
    {
        var error = Assert.Throws<CuratedExportValidationException>(action);
        Assert.Equal(expected, error.Error);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("/synthetic", error.Message);
    }

    [Fact]
    public void FreezeRetainsCompleteFenceAndEntriesWithoutSelectingUnrequestedFavorites()
    {
        FavoriteEntry old = Entry(), other = Entry(Key("other", 'b'));
        var plan = Freeze(Snapshot(other, old));
        Assert.Equal(Instance, plan.InstanceId);
        Assert.Equal(7, plan.Revision);
        Assert.Equal(Hash('f'), plan.Fingerprint);
        Assert.Equal(new[] { old, other }, plan.FavoriteEntries);
        Assert.Equal(old, Assert.Single(plan.Originals).Entry);
        Assert.Equal(CuratedExportMode.Files, plan.Mode);
        Assert.Equal(CuratedExportScope.Thread, plan.Scope);
        Assert.False(plan.PreviewsRequested);
    }

    [Fact]
    public void OldExactRevisionWinsWithoutConsultingCurrentFlagOrSameName()
    {
        MediaAssetReference old = Asset(current: false), latest = Asset(Key(hash: 'b'), Hash('2'), current: true);
        var plan = Freeze(manifests: [Manifest(assets: [latest, old])]);
        var chosen = Assert.Single(plan.Originals);
        Assert.Equal(CuratedExportStatus.OriginalIncluded, chosen.Status);
        Assert.Equal(old.AssetId, chosen.Asset!.AssetId);
        Assert.Equal(Key(), chosen.Asset.Key);
        Assert.Equal("original.png", chosen.Entry.OriginalFileName);
        Assert.Equal("recorded.png", chosen.Asset.OriginalFileName);
        Assert.Contains(plan.Omissions, row => row.Key == Key(hash: 'b') && row.Status == CuratedExportStatus.IntentionallyOmitted);
        Assert.False(plan.CreateLedger().AllOriginalsVerified);
        Assert.Equal(CuratedOriginalVerification.Required, Assert.Single(plan.CreateLedger().Originals).Verification);
    }

    [Fact]
    public void LaterFavoriteChangesAndCallerListMutationCannotChangeFrozenSelection()
    {
        FavoriteEntry entry = Entry();
        var entries = new List<FavoriteEntry> { entry };
        FavoriteStoreSnapshot snapshot = new(null!, entries, Hash('f'), Instance, 7);
        var keys = new List<FavoriteAssetKey> { entry.Key };
        var manifests = new List<CuratedExportManifest> { Manifest(assets: [Asset()]) };
        var plan = Freeze(snapshot, keys, manifests);
        entries.Clear(); keys.Clear(); manifests.Clear();
        FavoriteStoreSnapshot later = new(null!, [Entry(Key("new", 'b'))], Hash('e'), Instance, 8);
        Assert.NotEqual(later.Fingerprint, plan.Fingerprint);
        Assert.Equal(entry, Assert.Single(plan.FavoriteEntries));
        Assert.Equal(entry, Assert.Single(plan.Originals).Entry);
        Assert.Single(plan.ManifestFences);
        Assert.Throws<NotSupportedException>(() => ((IList<FavoriteEntry>)plan.FavoriteEntries)[0] = Entry(Key("new")));
    }

    [Fact]
    public void LatestPreviewSameHashOtherMediaAndWrongResourceNeverSubstitute()
    {
        var candidates = new[] { Asset(Key(hash: 'b'), Hash('2'), true),
            Asset(Key() with { Variant = "thumbnail" }, Hash('3'), true), Asset(Key("different"), Hash('4'), true) };
        var missing = Assert.Single(Freeze(manifests: [Manifest(assets: candidates)]).Originals);
        Assert.Equal(CuratedExportStatus.OriginalMissing, missing.Status);
        Assert.Null(missing.Asset);
        Assert.Empty(missing.Candidates);
        ResourceId other = Thread with { Source = "Other" };
        Invalid(CuratedExportValidationError.InvalidScope, () => Freeze(manifests: [Manifest(thread: other, assets: [Asset()])]));
    }

    [Fact]
    public void EmptyManifestsAreTypedMissingAndCorruptManifestIsNeverEmptySuccess()
    {
        Assert.Equal(CuratedExportStatus.OriginalMissing, Assert.Single(Freeze(manifests: []).Originals).Status);
        Assert.Equal(CuratedExportStatus.OriginalMissing, Assert.Single(Freeze(manifests: [Manifest(status: MediaManifestStatus.Missing)]).Originals).Status);
        Assert.Equal(CuratedExportStatus.OriginalVerificationFailed,
            Assert.Single(Freeze(manifests: [Manifest(status: MediaManifestStatus.Corrupt)]).Originals).Status);
        var mixed = Freeze(manifests: [Manifest(assets: [Asset()]), Manifest("/synthetic/corrupt", status: MediaManifestStatus.Corrupt)]);
        Assert.Equal(CuratedExportStatus.OriginalVerificationFailed, Assert.Single(mixed.Originals).Status);
        Assert.False(mixed.CreateLedger().AllOriginalsVerified);
    }

    [Fact]
    public void LengthConflictDoesNotSelectConvenientReplica()
    {
        var failed = Assert.Single(Freeze(manifests: [Manifest(assets: [Asset(length: 4)])]).Originals);
        Assert.Equal(CuratedExportStatus.OriginalVerificationFailed, failed.Status);
        Assert.Null(failed.Asset);
        var ambiguous = Assert.Single(Freeze(manifests: [Manifest(assets: [Asset(length: 4), Asset(assetId: Hash('2'))])]).Originals);
        Assert.Equal(CuratedExportStatus.OriginalAmbiguous, ambiguous.Status);
        Assert.Equal(2, ambiguous.Candidates.Length);
    }

    [Fact]
    public void MultipleConcreteMappingsAreAmbiguousRegardlessOfInputOrderOrIdenticalBytes()
    {
        var one = Manifest("/synthetic/a", assets: [Asset()]);
        var two = Manifest("/synthetic/b", assets: [Asset(current: true)]);
        var first = Assert.Single(Freeze(manifests: [two, one]).Originals);
        var second = Assert.Single(Freeze(manifests: [one, two]).Originals);
        Assert.Equal(CuratedExportStatus.OriginalAmbiguous, first.Status);
        Assert.Null(first.Asset);
        Assert.Equal(first.Candidates.ToArray(), second.Candidates.ToArray());
        Assert.Equal(2, first.Candidates.Length);
    }

    [Fact]
    public void UnboundFileOnlyAssetDoesNotInventPostOrBinding()
    {
        var plan = Freeze();
        Assert.Empty(Assert.Single(plan.Originals).Occurrences);
        Assert.Empty(plan.ThreadContexts);
        Assert.Equal(CuratedExportStatus.OriginalIncluded, Assert.Single(plan.Originals).Status);
    }

    private static MediaAttachmentBinding Binding(MediaAssetReference asset, string post, ResourceId? thread = null)
    {
        ResourceId resource = thread ?? Thread;
        var provider = new MediaProviderMetadata(asset.MediaIdentifier, asset.Variant, asset.SourceUrl,
            asset.OriginalFileName, asset.Length, asset.Md5, asset.Sha256);
        string revision = MediaRevisionKeys.CreateAttachmentKey(resource, post, 0, provider);
        return new(new(resource, post, 0, revision), MediaRevisionKeys.CreateAcquisitionKey(resource, provider), asset.AssetId,
            provider, new(asset.Length, asset.Md5, asset.Sha256), new(MediaAcceptanceKind.Strict, false, false, At));
    }

    [Fact]
    public void MultipleOccurrencesRetainDistinctPostAndRevisionProvenance()
    {
        MediaAssetReference asset = Asset();
        var first = Binding(asset, "101"); var second = Binding(asset, "102");
        var bindings = new Dictionary<string, MediaAttachmentBinding> { [second.Reference.RevisionKey] = second, [first.Reference.RevisionKey] = first };
        var manifest = Manifest(assets: [asset], bindings: bindings);
        var plan = Freeze(manifests: [manifest]);
        bindings.Clear();
        var selected = Assert.Single(plan.Originals);
        Assert.Equal(new[] { first.Reference, second.Reference }, selected.Occurrences.Select(row => row.Reference!));
        Assert.Equal(new[] { first.Reference.PostId, second.Reference.PostId }, selected.Occurrences.Select(row => row.PostId));
        Assert.Equal(new[] { first.Reference.RevisionKey, second.Reference.RevisionKey }, selected.Occurrences.Select(row => row.RevisionKey));
        Assert.Equal(new[] { first.AcquisitionRevisionKey, second.AcquisitionRevisionKey },
            selected.Occurrences.Select(row => row.AcquisitionRevisionKey));
        Assert.All(selected.Occurrences, row =>
        {
            Assert.False(row.IsLegacy);
            Assert.Same(manifest.Snapshot, row.Manifest);
            Assert.Equal(asset.AssetId, row.AssetId);
        });
        Assert.Single(selected.Candidates);
    }

    [Fact]
    public void SharedManifestDoesNotRelabelForeignOccurrences()
    {
        MediaAssetReference asset = Asset();
        var own = Binding(asset, "101");
        var foreign = Binding(asset, "201", Thread with { Thread = "200" });
        var manifest = Manifest(assets: [asset], bindings: new Dictionary<string, MediaAttachmentBinding>
            { [own.Reference.RevisionKey] = own, [foreign.Reference.RevisionKey] = foreign });
        var occurrence = Assert.Single(Assert.Single(Freeze(manifests: [manifest]).Originals).Occurrences);
        Assert.Equal(own.Reference, occurrence.Reference!);
        Assert.Equal(own.Reference.Resource, occurrence.Resource);
        Assert.Equal(own.Reference.PostId, occurrence.PostId);
        Assert.Equal(own.Reference.Position, occurrence.Position);
        Assert.Equal(own.Reference.RevisionKey, occurrence.RevisionKey);
        Assert.False(occurrence.IsLegacy);
    }

    [Fact]
    public void IdenticalBytesAcrossResourcesAndMediaIdentitiesDoNotMergeSelections()
    {
        ResourceId otherThread = Thread with { Thread = "200" };
        FavoriteAssetKey first = Key(), second = Key(thread: otherThread), third = Key("other-media");
        var plan = CuratedExportSelection.Freeze(Snapshot(Entry(third), Entry(second), Entry(first)), CuratedExportScope.Threads,
            CuratedExportMode.Files, false, [otherThread, Thread], [third, second, first],
            [Manifest(assets: [Asset(first), Asset(third, Hash('2'))]), Manifest("/synthetic/other", otherThread, [Asset(second)])], []);
        Assert.Equal(3, plan.Originals.Length);
        Assert.Equal(3, plan.Originals.Select(row => row.Entry.Key).Distinct().Count());
        Assert.All(plan.Originals, row => Assert.Equal(Hash(), row.Entry.Key.Sha256));
        Assert.Equal(new[] { "original-000001", "original-000002", "original-000003" }, plan.Originals.Select(row => row.NameToken));
    }

    [Fact]
    public void UnselectedOriginalAndPreviewAccountingAreSeparateWithoutDecoderClaims()
    {
        FavoriteAssetKey nonFavorite = Key("not-favorite", 'b');
        CuratedExportManifest manifest = Manifest(assets: [Asset(), Asset(nonFavorite, Hash('2'))]);
        foreach (bool requested in new[] { false, true })
        {
            var plan = Freeze(manifests: [manifest], previews: requested);
            Assert.Single(plan.Originals);
            var ledger = plan.CreateLedger();
            Assert.Equal(1, ledger.Count(CuratedExportStatus.IntentionallyOmitted));
            Assert.Equal(1, ledger.Count(requested ? CuratedExportStatus.PreviewUnsupported : CuratedExportStatus.PreviewNotRequested));
            Assert.Equal(2, plan.Omissions.Length);
            Assert.All(plan.Omissions, item => Assert.Equal(nonFavorite, item.Key));
        }
    }

    [Fact]
    public void ExplicitEmptySelectionNeverExpandsToThreadOrAllFavorites()
    {
        var threadPlan = Freeze(keys: []);
        Assert.Empty(threadPlan.Originals);
        Assert.Equal(1, threadPlan.CreateLedger().Count(CuratedExportStatus.IntentionallyOmitted));
        var favoritePlan = CuratedExportSelection.Freeze(Snapshot(Entry()), CuratedExportScope.ExplicitFavorites,
            CuratedExportMode.Files, false, [], [], [], []);
        Assert.Empty(favoritePlan.Originals);
        Assert.Empty(favoritePlan.Threads);
        Assert.Single(favoritePlan.FavoriteEntries);
    }

    [Fact]
    public void ValidEmptyLegacyAndAbsentStoreFencesAreSupported()
    {
        foreach (string fingerprint in new[] { "missing", Hash('f') })
        {
            var plan = Freeze(new(null!, [], fingerprint, null, 0), keys: [], manifests: []);
            Assert.Null(plan.InstanceId);
            Assert.Equal(0, plan.Revision);
            Assert.Empty(plan.FavoriteEntries);
            Assert.Empty(plan.Originals);
        }
    }

    [Fact]
    public void InvalidOrInconsistentSnapshotCannotBeCoercedToEmptyFavorites()
    {
        foreach (FavoriteStoreSnapshot bad in new[] {
            new FavoriteStoreSnapshot(null!, [Entry()], "missing", null, 0),
            new FavoriteStoreSnapshot(null!, [], "not-a-fingerprint", Instance, 7),
            new FavoriteStoreSnapshot(null!, [], Hash('f'), Guid.Empty, 7),
            new FavoriteStoreSnapshot(null!, [], Hash('f'), null, 7),
            new FavoriteStoreSnapshot(null!, [], Hash('f'), Instance, 0) })
            Invalid(CuratedExportValidationError.InvalidSnapshot, () => Freeze(bad, keys: [], manifests: []));
        Invalid(CuratedExportValidationError.DuplicateIdentity, () => Freeze(Snapshot(Entry(), Entry())));
        Invalid(CuratedExportValidationError.InvalidSnapshot, () => Freeze(Snapshot(Entry(length: -1))));
    }

    [Fact]
    public void UnknownDuplicateNonOriginalOrNonCanonicalSelectedKeysAreRejected()
    {
        Invalid(CuratedExportValidationError.SelectionNotInSnapshot, () => Freeze(keys: [Key("unrequested")]));
        Invalid(CuratedExportValidationError.DuplicateIdentity, () => Freeze(keys: [Key(), Key()]));
        var preview = Key() with { Variant = "thumbnail" };
        Invalid(CuratedExportValidationError.InvalidKey, () => Freeze(Snapshot(Entry(preview)), [preview]));
        foreach (FavoriteAssetKey bad in new[] { Key() with { Sha256 = Hash('A') }, Key() with { Sha256 = Hash('x') },
            Key() with { MediaIdentifier = "\ud800" }, Key() with { Thread = Thread with { Thread = null } } })
            Invalid(CuratedExportValidationError.InvalidKey, () => Freeze(Snapshot(Entry(bad)), [bad]));
    }

    [Fact]
    public void ScopeAndModeMustBeExplicitAndCannotAuthorizeOtherThreads()
    {
        void Call(CuratedExportScope scope, CuratedExportMode mode, ResourceId[] threads) =>
            CuratedExportSelection.Freeze(Snapshot(Entry()), scope, mode, false, threads, [Key()], [], []);
        Invalid(CuratedExportValidationError.InvalidScope, () => Call((CuratedExportScope)99, CuratedExportMode.Files, [Thread]));
        Invalid(CuratedExportValidationError.InvalidScope, () => Call(CuratedExportScope.Thread, (CuratedExportMode)99, [Thread]));
        Invalid(CuratedExportValidationError.InvalidScope, () => Call(CuratedExportScope.Thread, CuratedExportMode.Files, []));
        Invalid(CuratedExportValidationError.InvalidScope, () => Call(CuratedExportScope.Threads, CuratedExportMode.Files, []));
        Invalid(CuratedExportValidationError.InvalidScope, () => Call(CuratedExportScope.ExplicitFavorites, CuratedExportMode.Files, [Thread]));
        Invalid(CuratedExportValidationError.InvalidScope, () => Call(CuratedExportScope.Thread, CuratedExportMode.Files, [Thread with { Thread = "200" }]));
        Invalid(CuratedExportValidationError.DuplicateIdentity, () => Call(CuratedExportScope.Threads, CuratedExportMode.Files, [Thread, Thread]));
    }

    [Fact]
    public void ComponentEqualityDoesNotCollapseDelimiterCaseOrUnicodeDistinctIdentities()
    {
        ResourceId[] threads = [new("source\0board", "x", "1"), new("source", "board\0x", "1"),
            new("Source", "board\0x", "1"), new("é", "x", "1"), new("e\u0301", "x", "1")];
        FavoriteEntry[] entries = threads.Select(thread => Entry(Key(thread: thread))).ToArray();
        var plan = CuratedExportSelection.Freeze(Snapshot(entries), CuratedExportScope.Threads, CuratedExportMode.Files,
            false, threads, entries.Select(entry => entry.Key), [], []);
        Assert.Equal(5, plan.Threads.Length);
        Assert.Equal(5, plan.Originals.Length);
    }

    [Fact]
    public void OrderAndNameTokensAreDeterministicAcrossPermutationsAndCulture()
    {
        FavoriteEntry a = Entry(Key("a")), z = Entry(Key("z", 'b'));
        var manifest = Manifest(assets: [Asset(z.Key, Hash('2')), Asset(a.Key)]);
        var first = Freeze(Snapshot(z, a), [z.Key, a.Key], [manifest]);
        CultureInfo previous = CultureInfo.CurrentCulture;
        CuratedExportSelection second;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-EG");
            second = Freeze(Snapshot(a, z), [a.Key, z.Key], [manifest]);
        }
        finally { CultureInfo.CurrentCulture = previous; }
        Assert.Equal(first.Originals.Select(row => (row.Entry, row.NameToken, row.Status, row.Asset)).ToArray(),
            second.Originals.Select(row => (row.Entry, row.NameToken, row.Status, row.Asset)).ToArray());
        Assert.Equal(first.ManifestFences.ToArray(), second.ManifestFences.ToArray());
        Assert.Equal(new[] { "original-000001", "original-000002" }, first.Originals.Select(row => row.NameToken));
        // Naming uses invariant numeric formatting; no filesystem-dependent or joined-key naming.
        Assert.Equal(a, first.Originals[0].Entry);
    }

    [Fact]
    public void DuplicateManifestAndBrokenBindingAreRejectedWithoutPickingFirst()
    {
        CuratedExportManifest manifest = Manifest(assets: [Asset()]);
        Invalid(CuratedExportValidationError.DuplicateIdentity, () => Freeze(manifests: [manifest, manifest]));
        var bad = Binding(Asset(), "101") with { AssetId = Hash('2') };
        Invalid(CuratedExportValidationError.InvalidManifest, () => Freeze(manifests: [Manifest(assets: [Asset()],
            bindings: new Dictionary<string, MediaAttachmentBinding> { [bad.Reference.RevisionKey] = bad })]));
    }

    [Fact]
    public void ManifestFenceDriftIsStaleNotReselectionAndComparisonDoesNotReadBytes()
    {
        var plan = Freeze();
        Assert.Equal(CuratedExportSourceFreshness.SameManifestFences, plan.CompareManifestFences(plan.ManifestFences));
        Assert.Equal(CuratedExportSourceFreshness.Stale, plan.CompareManifestFences([]));
        foreach (CuratedExportManifestFence changed in new[] { plan.ManifestFences[0] with { Generation = Hash('e') },
            plan.ManifestFences[0] with { Directory = "/synthetic/new-root" }, plan.ManifestFences[0] with { Thread = Thread with { Source = "other" } } })
            Assert.Equal(CuratedExportSourceFreshness.Stale, plan.CompareManifestFences([changed]));
        Assert.Equal(Key(), Assert.Single(plan.Originals).Entry.Key);
    }

    [Fact]
    public void VerificationResultsAndCancellationProduceNewLedgersWithoutChangingPlan()
    {
        FavoriteEntry first = Entry(), second = Entry(Key("second", 'b'));
        var plan = Freeze(Snapshot(first, second), [first.Key, second.Key], [Manifest(assets: [Asset(), Asset(second.Key, Hash('2'))])]);
        var initial = plan.CreateLedger();
        var oneVerified = initial.RecordOriginal(first.Key, CuratedExportStatus.OriginalIncluded);
        var cancelled = oneVerified.CancelPending();
        Assert.Equal(2, initial.Count(CuratedExportStatus.OriginalIncluded));
        Assert.All(initial.Originals, row => Assert.Equal(CuratedOriginalVerification.Required, row.Verification));
        Assert.Equal(CuratedOriginalVerification.Verified, cancelled.Originals[0].Verification);
        Assert.Equal(CuratedExportStatus.Cancelled, cancelled.Originals[1].Status);
        Assert.False(cancelled.AllOriginalsVerified);
        Assert.Same(plan, cancelled.Selection);
        Assert.Equal(2, plan.Originals.Length);
        Assert.True(oneVerified.RecordOriginal(second.Key, CuratedExportStatus.OriginalIncluded).AllOriginalsVerified);
        Assert.Equal(cancelled.Originals.ToArray(), cancelled.CancelPending().Originals.ToArray());
    }

    [Fact]
    public void MissingCorruptAndAmbiguousOriginalsCannotBePromotedOrReplacedViaLedger()
    {
        foreach (CuratedExportStatus outcome in new[] { CuratedExportStatus.OriginalMissing,
            CuratedExportStatus.OriginalVerificationFailed, CuratedExportStatus.Cancelled })
        {
            var ledger = Freeze().CreateLedger().RecordOriginal(Key(), outcome);
            Assert.Equal(outcome, Assert.Single(ledger.Originals).Status);
            Assert.False(ledger.AllOriginalsVerified);
            Invalid(CuratedExportValidationError.InvalidLedgerUpdate, () => ledger.RecordOriginal(Key(), CuratedExportStatus.OriginalIncluded));
        }
        var missing = Freeze(manifests: []).CreateLedger();
        Invalid(CuratedExportValidationError.InvalidLedgerUpdate, () => missing.RecordOriginal(Key(), CuratedExportStatus.OriginalIncluded));
        var ambiguous = Freeze(manifests: [Manifest(assets: [Asset(), Asset(assetId: Hash('2'))])]).CreateLedger();
        Invalid(CuratedExportValidationError.InvalidLedgerUpdate, () => ambiguous.RecordOriginal(Key(), CuratedExportStatus.OriginalIncluded));
        Invalid(CuratedExportValidationError.InvalidLedgerUpdate, () => Freeze().CreateLedger().RecordOriginal(Key("new"), CuratedExportStatus.OriginalIncluded));
        Invalid(CuratedExportValidationError.InvalidLedgerUpdate, () => Freeze().CreateLedger().RecordOriginal(Key(), CuratedExportStatus.PreviewFailed));
    }

    [Fact]
    public void CancellationBeforeOrDuringEnumerationDoesNotReturnPartialSnapshot()
    {
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => Freeze(token: cancellation.Token));
        using CancellationTokenSource during = new();
        IEnumerable<CuratedExportManifest> Source()
        {
            yield return Manifest(assets: [Asset()]);
            during.Cancel();
        }
        Assert.Throws<OperationCanceledException>(() => Freeze(manifests: Source(), token: during.Token));
    }

    [Fact]
    public void ManifestEnumerationStopsAtLimitPlusOneWithoutUnboundedMaterialization()
    {
        int visited = 0;
        IEnumerable<CuratedExportManifest> Source()
        {
            while (true)
            {
                visited++;
                if (visited > CuratedExportSelection.MaximumManifests + 1) throw new Exception("Unbounded enumeration");
                yield return Manifest("/synthetic/" + visited, status: MediaManifestStatus.Missing);
            }
        }
        Invalid(CuratedExportValidationError.LimitExceeded, () => Freeze(manifests: Source()));
        Assert.Equal(CuratedExportSelection.MaximumManifests + 1, visited);
    }

    [Fact]
    public void EntryCountAndPerStringBudgetsRejectInsteadOfTruncating()
    {
        FavoriteEntry[] tooMany = Enumerable.Range(0, CuratedExportSelection.MaximumSelectionEntries + 1)
            .Select(number => Entry(Key(number.ToString()), name: null)).ToArray();
        Invalid(CuratedExportValidationError.LimitExceeded, () => Freeze(Snapshot(tooMany), keys: [], manifests: []));
        string max = new('n', CuratedExportSelection.MaximumStringCharacters);
        Assert.Equal(max, Assert.Single(Freeze(Snapshot(Entry(name: max))).Originals).Entry.OriginalFileName);
        Invalid(CuratedExportValidationError.LimitExceeded, () => Freeze(Snapshot(Entry(name: max + "n"))));
        FavoriteEntry[] textOverflow = Enumerable.Range(0, 130).Select(number => Entry(Key(number.ToString()), name: max)).ToArray();
        Invalid(CuratedExportValidationError.LimitExceeded, () => Freeze(Snapshot(textOverflow), keys: [], manifests: []));
    }

    [Fact]
    public void UniqueLegacyNullRevisionIsRecordedAgainstTheFrozenManifestAsset()
    {
        MediaAssetReference asset = Asset();
        CapturedAttachment attachment = LegacyAttachment(asset);
        var plan = CuratedExportSelection.Freeze(Snapshot(Entry()), CuratedExportScope.Thread, CuratedExportMode.Html,
            false, [Thread], [Key()], [Manifest(assets: [asset])], [Context(attachment)]);
        var occurrence = Assert.Single(Assert.Single(plan.Originals).Occurrences);
        Assert.True(occurrence.IsLegacy);
        Assert.Null(occurrence.RevisionKey);
        Assert.Null(occurrence.AcquisitionRevisionKey);
        Assert.Null(occurrence.Reference);
        Assert.Equal(Thread, occurrence.Resource);
        Assert.Equal("101", occurrence.PostId);
        Assert.Equal(0, occurrence.Position);
        Assert.Equal(asset.AssetId, occurrence.AssetId);
        Assert.Equal("/synthetic/root", occurrence.Manifest.Directory);
        Assert.Equal(Hash('d'), occurrence.Manifest.Generation);
        Assert.Same(Assert.Single(plan.Originals).Asset!.Manifest, occurrence.Manifest);
        Assert.Equal(CuratedOriginalVerification.Required, Assert.Single(plan.CreateLedger().Originals).Verification);
    }

    [Fact]
    public void LegacyResolverAmbiguityRemainsUnresolvedAcrossFrozenManifests()
    {
        MediaAssetReference first = Asset(assetId: Hash('1'));
        MediaAssetReference second = Asset(assetId: Hash('2'));
        // Different asset IDs are ambiguous to the resolver; identical replicas are also
        // ambiguous under the plan's stricter concrete-manifest selection contract.
        foreach (MediaAssetReference competitor in new[] { second, first })
        {
            var plan = CuratedExportSelection.Freeze(Snapshot(Entry()), CuratedExportScope.Thread, CuratedExportMode.Html,
                false, [Thread], [Key()], [Manifest("/synthetic/a", assets: [first]), Manifest("/synthetic/b", assets: [competitor])],
                [Context(LegacyAttachment(first))]);
            var original = Assert.Single(plan.Originals);
            Assert.Equal(CuratedExportStatus.OriginalAmbiguous, original.Status);
            Assert.Equal(2, original.Candidates.Length);
            Assert.Empty(original.Occurrences);
        }
        // Ambiguous provider evidence must not gain an occurrence merely because the
        // explicitly selected revision itself has only one exact-key candidate.
        MediaAssetReference otherRevision = Asset(Key(hash: 'b'), Hash('2'), current: true);
        CapturedAttachment sizeOnly = new(0, first.MediaIdentifier, ExpectedSize: first.Length);
        var competing = CuratedExportSelection.Freeze(Snapshot(Entry()), CuratedExportScope.Thread, CuratedExportMode.Html,
            false, [Thread], [Key()], [Manifest(assets: [first, otherRevision])], [Context(sizeOnly)]);
        Assert.Equal(CuratedExportStatus.OriginalIncluded, Assert.Single(competing.Originals).Status);
        Assert.Empty(Assert.Single(competing.Originals).Occurrences);
    }

    [Fact]
    public void LegacyMetadataMismatchDoesNotCreateAnOccurrenceOrInventARevision()
    {
        MediaAssetReference asset = Asset();
        foreach (CapturedAttachment mismatch in new[] {
            LegacyAttachment(asset, length: asset.Length + 1),
            LegacyAttachment(asset, sha256: Hash('b')),
            LegacyAttachment(asset, mediaId: "not-the-recorded-media") })
        {
            var plan = CuratedExportSelection.Freeze(Snapshot(Entry()), CuratedExportScope.Thread, CuratedExportMode.Html,
                false, [Thread], [Key()], [Manifest(assets: [asset])], [Context(mismatch)]);
            Assert.Equal(CuratedExportStatus.OriginalIncluded, Assert.Single(plan.Originals).Status);
            Assert.Empty(Assert.Single(plan.Originals).Occurrences);
        }
    }

    [Fact]
    public void LegacyAttachmentWithoutResolverEvidenceRemainsUnbound()
    {
        MediaAssetReference asset = Asset();
        CapturedAttachment unbound = new(0, asset.MediaIdentifier, asset.Variant);
        var plan = CuratedExportSelection.Freeze(Snapshot(Entry()), CuratedExportScope.Thread, CuratedExportMode.Html,
            false, [Thread], [Key()], [Manifest(assets: [asset])], [Context(unbound)]);
        Assert.Empty(Assert.Single(plan.Originals).Occurrences);
    }

    [Fact]
    public void LegacyOccurrenceUsesFrozenContextAfterCallerInputMutation()
    {
        MediaAssetReference asset = Asset();
        List<CapturedAttachment> input = [LegacyAttachment(asset)];
        CapturedPost post = new("101", 101, PostFieldPresence.All, At, Attachments: input);
        List<CuratedExportManifest> manifests = [Manifest(assets: [asset])];
        List<ArchivedThreadView> contexts = [Context(post)];
        List<FavoriteAssetKey> selected = [Key()];
        var plan = CuratedExportSelection.Freeze(Snapshot(Entry()), CuratedExportScope.Thread, CuratedExportMode.Html,
            false, [Thread], selected, manifests, contexts);
        var frozenManifest = manifests[0].Snapshot;
        input.Clear();
        input.Add(new CapturedAttachment(0, "different-media"));
        selected.Clear(); contexts.Clear(); manifests.Clear();
        manifests.Add(Manifest(assets: [Asset(Key(hash: 'b'), Hash('2'), current: true)]));
        var occurrence = Assert.Single(Assert.Single(plan.Originals).Occurrences);
        Assert.True(occurrence.IsLegacy);
        Assert.Equal("101", occurrence.PostId);
        Assert.Null(occurrence.RevisionKey);
        Assert.Null(occurrence.AcquisitionRevisionKey);
        Assert.Equal(Key(), Assert.Single(plan.Originals).Entry.Key);
        Assert.Same(frozenManifest, occurrence.Manifest);
        Assert.Equal(asset.AssetId, occurrence.AssetId);
        Assert.Equal(asset.Sha256, Assert.Single(Assert.Single(plan.ThreadContexts).Posts).Attachments![0].ExpectedSha256);
    }

    [Fact]
    public void GlobalRowsLimitCombinesSnapshotSelectionManifestAndContextRowsWithoutTruncation()
    {
        // Seven input rows before posts: favorite, scope thread, selected key,
        // manifest, asset, context and context directory. No legacy attachments/scans.
        int count = CuratedExportSelection.MaximumRows - 7;
        var posts = Enumerable.Range(0, count).Select(number => new CapturedPost(
            number.ToString(CultureInfo.InvariantCulture), number, PostFieldPresence.None));
        ArchiveThreadSummary summary = new(Thread, "https://fixture.invalid/thread", "Synthetic", "/synthetic/root",
            MeowChan.Interfaces.ThreadSourceState.NotFound, false, At, At, count, 11);
        var context = new ArchivedThreadView(summary, posts, ["/synthetic/root"], decimalPostOrder: true);
        var manifest = Manifest(assets: [Asset()]);
        var exact = CuratedExportSelection.Freeze(Snapshot(Entry()), CuratedExportScope.Thread, CuratedExportMode.Html,
            false, [Thread], [Key()], [manifest], [context]);
        Assert.Equal(count, Assert.Single(exact.ThreadContexts).Posts.Length);
        Assert.Single(exact.Originals);
        // One additional unselected favorite is still a full-snapshot input row.
        // It must trigger the GLOBAL bound, not any individual collection limit.
        Invalid(CuratedExportValidationError.LimitExceeded, () => CuratedExportSelection.Freeze(
            Snapshot(Entry(), Entry(Key("unused"))), CuratedExportScope.Thread, CuratedExportMode.Html,
            false, [Thread], [Key()], [manifest], [context]));
    }

    [Fact]
    public void RepeatedLegacyResolverScansAreChargedToTheGlobalRowsBudget()
    {
        MediaAssetReference selected = Asset();
        var assets = Enumerable.Range(1, 399).Select(number => Asset(Key("unused-" + number),
            number.ToString("x64", CultureInfo.InvariantCulture))).Prepend(selected).ToArray();
        CapturedAttachment[] attachments = Enumerable.Range(0, 500)
            .Select(position => LegacyAttachment(selected) with { Position = position }).ToArray();
        Invalid(CuratedExportValidationError.LimitExceeded, () => CuratedExportSelection.Freeze(
            Snapshot(Entry()), CuratedExportScope.Thread, CuratedExportMode.Html, false,
            [Thread], [Key()], [Manifest(assets: assets)], [Context(attachments)]));
    }

    [Fact]
    public void HtmlContextIsFrozenLocallyWithoutInventingPostsForUnboundFavorite()
    {
        CapturedAttachment attachment = new(0, "other-media", ExpectedSize: 3);
        List<CapturedAttachment> attachments = [attachment];
        CapturedPost post = new("101", 101, PostFieldPresence.All, At, Html: "<script>untrusted()</script>", Attachments: attachments);
        ArchiveThreadSummary summary = new(Thread, "https://fixture.invalid/thread", "Synthetic", "/synthetic/root",
            MeowChan.Interfaces.ThreadSourceState.NotFound, false, At, At, 1, 11);
        var view = new ArchivedThreadView(summary, [post], ["/synthetic/root"], decimalPostOrder: true);
        var plan = CuratedExportSelection.Freeze(Snapshot(Entry()), CuratedExportScope.Thread, CuratedExportMode.Html, false,
            [Thread], [Key()], [Manifest(assets: [Asset()])], [view]);
        attachments.Clear();
        var context = Assert.Single(plan.ThreadContexts);
        Assert.Equal(11, context.Summary.Revision);
        Assert.True(context.DecimalPostOrder);
        Assert.Equal(attachment, Assert.Single(Assert.Single(context.Posts).Attachments!));
        Assert.Equal(post.Html, context.Posts[0].Html); // stored inertly, never rendered or executed here
        Assert.Empty(Assert.Single(plan.Originals).Occurrences);
        Assert.Throws<NotSupportedException>(() => ((IList<CapturedAttachment>)context.Posts[0].Attachments!).Clear());
    }
}
