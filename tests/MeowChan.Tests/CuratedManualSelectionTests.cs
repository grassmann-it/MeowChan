using MeowChan.Archive;
using MeowChan.Favorites;
using MeowChan.Storage;
using MeowChan.Transfer;

namespace MeowChan.Tests;

// Metadata-only fixtures. All paths deliberately inert; no stores or bytes are opened.
public sealed class CuratedManualSelectionTests
{
    private static readonly ResourceId Thread = new("synthetic", "b", "100");
    private static string Hash(char c) => new(c, 64);
    private static MediaAssetReference Asset(char digest = 'a', char id = '1', bool current = false,
        string variant = "original") => new(Hash(id), "media", variant, "same.png",
        "/synthetic/never-opened/same.png", 3, Hash(digest), null, "same.png", current);
    private static CuratedExportManifest Manifest(params MediaAssetReference[] assets) =>
        new(Thread, new MediaAssetSnapshot("/synthetic/never-opened", MediaManifestStatus.Available,
            2, Hash('f'), assets));
    private static FavoriteExportAsset Target(CuratedExportManifest manifest, MediaAssetReference asset) =>
        FavoriteExportAsset.From(manifest.Thread, manifest.Snapshot, asset);
    private static CuratedExportSelection Freeze(IEnumerable<FavoriteExportAsset> targets,
        IEnumerable<CuratedExportManifest> manifests, CancellationToken token = default) =>
        CuratedExportSelection.FreezeManual(CuratedExportMode.Files, false, targets, manifests, [], token);
    private static void Invalid(CuratedExportValidationError expected, Action action) =>
        Assert.Equal(expected, Assert.Throws<CuratedExportValidationException>(action).Error);

    [Fact]
    public void NonFavoriteOlderOriginalIsFrozenWithoutAStoreOrLatestSubstitution()
    {
        var old = Asset();
        var latest = Asset('b', '2', true);
        var manifest = Manifest(latest, old);
        var plan = Freeze([Target(manifest, old)], [manifest]);
        Assert.True(plan.IsManual);
        Assert.Equal(CuratedExportScope.ExplicitManual, plan.Scope);
        Assert.Null(plan.InstanceId);
        Assert.Equal(0, plan.Revision);
        Assert.Equal("", plan.Fingerprint);
        Assert.Empty(plan.FavoriteEntries);
        var chosen = Assert.Single(plan.Originals);
        Assert.Equal(FavoriteAssetKey.From(Thread, old), chosen.Entry.Key);
        Assert.Equal(old.AssetId, chosen.Asset!.AssetId);
        Assert.Same(manifest.Snapshot, chosen.Asset.Manifest);
        Assert.Equal(CuratedExportStatus.OriginalIncluded, chosen.Status);
        Assert.Contains(plan.Omissions, row => row.Key.Sha256 == latest.Sha256
            && row.Status == CuratedExportStatus.IntentionallyOmitted);
        Assert.Equal(CuratedOriginalVerification.Required, Assert.Single(plan.CreateLedger().Originals).Verification);
        Assert.False(plan.CreateLedger().AllOriginalsVerified);
    }

    [Fact]
    public void ExactOlderAttachmentRevisionAndAcquisitionProvenanceRemainUnchanged()
    {
        var old = Asset() with { SourceUrl = "https://fixture.invalid/old.png",
            Md5 = Convert.ToBase64String(new byte[16]) };
        var provider = new MediaProviderMetadata(old.MediaIdentifier, old.Variant, old.SourceUrl,
            old.OriginalFileName, old.Length, old.Md5, old.Sha256);
        var reference = new AttachmentRevisionReference(Thread, "101", 0,
            MediaRevisionKeys.CreateAttachmentKey(Thread, "101", 0, provider));
        var binding = new MediaAttachmentBinding(reference,
            MediaRevisionKeys.CreateAcquisitionKey(Thread, provider), old.AssetId, provider,
            new(old.Length, old.Md5, old.Sha256), new(MediaAcceptanceKind.Strict, false, false,
                new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero)));
        var manifest = new CuratedExportManifest(Thread, new MediaAssetSnapshot(
            "/synthetic/never-opened", MediaManifestStatus.Available, 2, Hash('f'),
            [old, Asset('b', '2', true)], attachmentBindings:
            new Dictionary<string, MediaAttachmentBinding> { [reference.RevisionKey] = binding }));
        var plan = Freeze([Target(manifest, old)], [manifest]);
        var occurrence = Assert.Single(Assert.Single(plan.Originals).Occurrences);
        Assert.Equal(reference, occurrence.Reference);
        Assert.Equal(binding.AcquisitionRevisionKey, occurrence.AcquisitionRevisionKey);
        Assert.Equal(old.AssetId, occurrence.AssetId);
        Assert.Same(manifest.Snapshot, occurrence.Manifest);
        Assert.False(occurrence.IsLegacy);
    }

    [Fact]
    public void CallerMutationDoesNotChangeChosenAssetsOrManifestFences()
    {
        var asset = Asset();
        var manifest = Manifest(asset);
        List<FavoriteExportAsset> targets = [Target(manifest, asset)];
        List<CuratedExportManifest> manifests = [manifest];
        var plan = Freeze(targets, manifests);
        targets.Clear(); manifests.Clear();
        Assert.Equal(asset.AssetId, Assert.Single(plan.Originals).Asset!.AssetId);
        Assert.Equal(Hash('f'), Assert.Single(plan.ManifestFences).Generation);
        Assert.Empty(plan.FavoriteEntries);
    }

    [Fact]
    public void DuplicateKeysAreRejectedRatherThanDeduplicated()
    {
        var asset = Asset(); var manifest = Manifest(asset); var target = Target(manifest, asset);
        Invalid(CuratedExportValidationError.DuplicateIdentity, () => Freeze([target, target], [manifest]));
    }

    [Fact]
    public void AmbiguousExactIdentityIsRejectedEvenWhenHandleNamesOneCandidate()
    {
        var asset = Asset(); var manifest = Manifest(asset, Asset(id: '2'));
        Invalid(CuratedExportValidationError.InvalidManualTarget,
            () => Freeze([Target(manifest, asset)], [manifest]));
    }

    [Fact]
    public void ForgedHandleLengthNameIdDigestAndSnapshotAreRejected()
    {
        var asset = Asset(); var manifest = Manifest(asset); var target = Target(manifest, asset);
        foreach (var invalid in new[] {
            target with { Length = 4 }, target with { OriginalFileName = "forged.png" },
            target with { AssetId = Hash('9') }, target with { Key = target.Key with { Sha256 = Hash('b') } },
            target with { Manifest = Manifest(asset).Snapshot } })
            Invalid(CuratedExportValidationError.InvalidManualTarget, () => Freeze([invalid], [manifest]));
    }

    [Fact]
    public void PreviewWrongResourceMissingAndNullTargetsAreRejected()
    {
        var preview = Asset(variant: "thumbnail"); var manifest = Manifest(preview);
        Invalid(CuratedExportValidationError.InvalidKey, () => Freeze([Target(manifest, preview)], [manifest]));
        var asset = Asset(); manifest = Manifest(asset); var target = Target(manifest, asset);
        Invalid(CuratedExportValidationError.InvalidManualTarget, () => Freeze([target], []));
        Invalid(CuratedExportValidationError.InvalidManualTarget, () => Freeze([null!], [manifest]));
        Invalid(CuratedExportValidationError.InvalidScope, () => Freeze(
            [target with { Key = target.Key with { Thread = Thread with { Board = "other" } } }], [manifest]));
    }

    [Fact]
    public void FavoriteRouteStillRequiresMembershipAndRejectsManualScope()
    {
        var asset = Asset(); var manifest = Manifest(asset);
        var missing = new FavoriteStoreSnapshot(null!, [], "missing", null, 0);
        Invalid(CuratedExportValidationError.SelectionNotInSnapshot, () => CuratedExportSelection.Freeze(
            missing, CuratedExportScope.ExplicitFavorites, CuratedExportMode.Files, false, [],
            [Target(manifest, asset).Key], [manifest], []));
        Invalid(CuratedExportValidationError.InvalidScope, () => CuratedExportSelection.Freeze(
            missing, CuratedExportScope.ExplicitManual, CuratedExportMode.Files, false, [], [], [], []));
        Assert.Empty(missing.Entries);
    }

    [Fact]
    public void CancellationDuringManualEnumerationReturnsNoPartialPlan()
    {
        var asset = Asset(); var manifest = Manifest(asset);
        using CancellationTokenSource cancellation = new();
        IEnumerable<FavoriteExportAsset> Targets()
        {
            yield return Target(manifest, asset);
            cancellation.Cancel();
        }
        Assert.Throws<OperationCanceledException>(() => Freeze(Targets(), [manifest], cancellation.Token));
    }

    [Fact]
    public void ManualEnumerationIsBoundedWithoutTruncation()
    {
        int visited = 0;
        var asset = Asset(); var manifest = Manifest(asset);
        IEnumerable<FavoriteExportAsset> Targets()
        {
            while (true)
            {
                visited++;
                yield return Target(manifest, asset) with
                { Key = new(Thread, visited.ToString(), "original", asset.Sha256),
                    AssetId = "x", OriginalFileName = null };
            }
        }
        Invalid(CuratedExportValidationError.LimitExceeded, () => Freeze(Targets(), []));
        Assert.Equal(CuratedExportSelection.MaximumSelectionEntries + 1, visited);
    }

    [Fact]
    public void ManualCurationDoesNotSelectAnyPrivateMetadataCategory()
    {
        var asset = Asset(); var manifest = Manifest(asset); var target = Target(manifest, asset);
        var offered = new NativePrivateMetadataProjection([Thread], new(true, true, true),
            [Thread], [target.Key], []);
        var plan = Freeze([target], [manifest]);
        Assert.Single(plan.Originals);
        var withoutConsent = offered.Select();
        Assert.Equal(default(NativePrivateMetadataSelection), withoutConsent.Selection);
        // Content selection is not passed to Select and cannot manufacture export consent.
        Assert.Empty(plan.FavoriteEntries);
        Assert.Empty(withoutConsent.ThreadFavorites);
        Assert.Empty(withoutConsent.MediaFavorites);
        Assert.Empty(withoutConsent.PostBookmarksAndNotes);
    }
}
