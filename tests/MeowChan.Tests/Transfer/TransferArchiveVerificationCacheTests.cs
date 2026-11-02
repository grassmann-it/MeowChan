using System.Security.Cryptography;
using MeowChan.Diagnostics;
using MeowChan.Storage;
using MeowChan.Tests.GoalFixtures;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

[Collection("PerformanceDiagnostics")]
public sealed class TransferArchiveVerificationCacheTests
{
    private const long DefaultMaximumBytes = 2L * 1024 * 1024 * 1024;

    [Fact]
    public async Task SameRealFrozenOriginIsVerifiedAndChargedOnlyOncePerCache()
    {
        await using NativeTransferArchiveFixture fixture = await NativeTransferArchiveFixture.CreateAsync();
        MediaAssetSnapshot first = await MediaAssetReader.OpenAsync(fixture.MediaDirectory);
        MediaAssetSnapshot reopened = await MediaAssetReader.OpenAsync(fixture.MediaDirectory);
        MediaAssetReference asset = first.FindById(fixture.StrictAssetId)!;
        Assert.Equal(first.Generation, reopened.Generation);
        TransferArchiveVerificationCache cache = new(asset.Length);
        using var run = PerformanceDiagnostics.BeginRun("transfer-repeat-origin", "synthetic", "real-store", 1024);
        for (int occurrence = 0; occurrence < 25; occurrence++)
        {
            MediaAssetSnapshot manifest = occurrence % 2 == 0 ? first : reopened;
            // Equal records from independently read views, not object-reference identity.
            MediaAssetReference selected = manifest.FindById(asset.AssetId)! with { };
            Assert.Equal(TransferAssetVerification.Available, await cache.VerifyAsync(manifest, selected, CancellationToken.None));
        }
        AssertVerifications(run, 1, 1);
        Assert.Equal(asset.Length, Assert.Single(Events(run, "verified")).Value);
        // A new freeze must not inherit proof or a byte reservation from the previous cache.
        TransferArchiveVerificationCache nextFreeze = new(asset.Length);
        Assert.Equal(TransferAssetVerification.Available, await nextFreeze.VerifyAsync(reopened, asset, CancellationToken.None));
        AssertVerifications(run, 2, 2);
        Assert.Equal(0, fixture.UnexpectedProviderCalls);
    }

    [Fact]
    public async Task EqualPayloadHashesInDifferentRealOriginsAreVerifiedIndependently()
    {
        await using NativeTransferArchiveFixture first = await NativeTransferArchiveFixture.CreateAsync();
        await using NativeTransferArchiveFixture second = await NativeTransferArchiveFixture.CreateAsync();
        MediaAssetSnapshot left = await MediaAssetReader.OpenAsync(first.MediaDirectory);
        MediaAssetSnapshot right = await MediaAssetReader.OpenAsync(second.MediaDirectory);
        MediaAssetReference a = left.FindById(first.StrictAssetId)!;
        MediaAssetReference b = right.FindById(second.StrictAssetId)!;
        Assert.Equal(a.AssetId, b.AssetId);
        Assert.Equal(a.Sha256, b.Sha256);
        Assert.Equal(a.Length, b.Length);
        Assert.NotEqual(left.Directory, right.Directory);
        Assert.NotEqual(a.Path, b.Path);
        TransferArchiveVerificationCache cache = new(a.Length + b.Length);
        using var run = PerformanceDiagnostics.BeginRun("transfer-distinct-origins", "synthetic", "real-stores", 1024);
        Assert.Equal(TransferAssetVerification.Available, await cache.VerifyAsync(left, a, CancellationToken.None));
        Assert.Equal(TransferAssetVerification.Available, await cache.VerifyAsync(right, b, CancellationToken.None));
        Assert.Equal(TransferAssetVerification.Available, await cache.VerifyAsync(left, a, CancellationToken.None));
        Assert.Equal(TransferAssetVerification.Available, await cache.VerifyAsync(right, b, CancellationToken.None));
        AssertVerifications(run, 2, 2);
        Assert.Equal(a.Length + b.Length, Events(run, "verified").Sum(e => e.Value));
        Assert.Equal(0, first.UnexpectedProviderCalls);
        Assert.Equal(0, second.UnexpectedProviderCalls);
    }

    [Fact]
    public async Task ForeignAndStaleDescriptorsRejectBeforeIoOrCachedSuccessWithoutSpendingBudget()
    {
        await using NativeTransferArchiveFixture fixture = await NativeTransferArchiveFixture.CreateAsync();
        MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(fixture.MediaDirectory);
        MediaAssetReference asset = manifest.FindById(fixture.StrictAssetId)!;
        MediaAssetReference[] foreign =
        [
            asset with { AssetId = new string('f', 64) },
            asset with { Path = fixture.UnavailablePath },
            asset with { Length = asset.Length + 1 },
            asset with { Sha256 = new string('0', 64) },
            // These fields are outside the cache key, but membership requires the entire record.
            asset with { SourceUrl = "https://i.4cdn.org/b/999.jpg" },
            asset with { OriginalFileName = "foreign-synthetic.jpg" },
            asset with { MediaIdentifier = "999" },
            asset with { IsCurrent = !asset.IsCurrent }
        ];
        TransferArchiveVerificationCache cache = new(asset.Length);
        using var run = PerformanceDiagnostics.BeginRun("transfer-membership", "synthetic", "real-store", 1024);
        foreach (MediaAssetReference selected in foreign)
            await Assert.ThrowsAsync<InvalidDataException>(() => cache.VerifyAsync(manifest, selected, CancellationToken.None));
        AssertVerifications(run, 0, 0);
        Assert.Equal(TransferAssetVerification.Available, await cache.VerifyAsync(manifest, asset, CancellationToken.None));
        foreach (MediaAssetReference selected in foreign)
            await Assert.ThrowsAsync<InvalidDataException>(() => cache.VerifyAsync(manifest, selected, CancellationToken.None));
        // UNIT membership controls: same origin key, but the supplied snapshot lacks/replaces its entry.
        MediaAssetSnapshot absent = UnitSnapshot(manifest.Directory, manifest.Generation);
        MediaAssetSnapshot replaced = UnitSnapshot(manifest.Directory, manifest.Generation,
            asset with { SourceUrl = "https://i.4cdn.org/b/999.jpg" });
        await Assert.ThrowsAsync<InvalidDataException>(() => cache.VerifyAsync(absent, asset, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => cache.VerifyAsync(replaced, asset, CancellationToken.None));
        AssertVerifications(run, 1, 1);
        Assert.Equal(0, fixture.UnexpectedProviderCalls);
    }

    // Explicitly constructed snapshots below are UNIT controls, not MediaStore manifest integration.
    [Theory]
    [InlineData("directory")]
    [InlineData("generation")]
    [InlineData("asset-id")]
    [InlineData("path")]
    [InlineData("length")]
    [InlineData("sha256")]
    public async Task EveryFrozenOriginKeyComponentPreventsReuse(string component)
    {
        using TempDirectory temp = new();
        byte[] bytes = [1, 2, 3];
        string path = Path.Combine(temp.Path, "unit.jpg");
        await File.WriteAllBytesAsync(path, bytes);
        MediaAssetReference original = UnitAsset(path, bytes);
        MediaAssetReference changed = original;
        string directory = temp.Path;
        string generation = "unit-generation-one";
        switch (component)
        {
            case "directory": directory = Path.Combine(temp.Path, "other-origin"); break;
            case "generation": generation = "unit-generation-two"; break;
            case "asset-id": changed = original with { AssetId = new string('b', 64) }; break;
            case "path":
                string copy = Path.Combine(temp.Path, "unit-copy.jpg");
                await File.WriteAllBytesAsync(copy, bytes);
                changed = original with { Path = copy }; break;
            case "length": changed = original with { Length = bytes.Length + 1 }; break;
            case "sha256": changed = original with { Sha256 = new string('0', 64) }; break;
            default: throw new ArgumentOutOfRangeException(nameof(component));
        }
        MediaAssetSnapshot first = UnitSnapshot(temp.Path, "unit-generation-one", original);
        MediaAssetSnapshot second = UnitSnapshot(directory, generation, changed);
        TransferArchiveVerificationCache cache = new(32);
        using var run = PerformanceDiagnostics.BeginRun("transfer-origin-component", "synthetic", "unit", 1024);
        Assert.Equal(TransferAssetVerification.Available, await cache.VerifyAsync(first, original, CancellationToken.None));
        if (component is "length" or "sha256")
        {
            // Changed expectations must reach the real verifier and fail, never reuse prior success.
            await Assert.ThrowsAsync<InvalidDataException>(() => cache.VerifyAsync(second, changed, CancellationToken.None));
            AssertVerifications(run, 2, 1);
        }
        else
        {
            Assert.Equal(TransferAssetVerification.Available, await cache.VerifyAsync(second, changed, CancellationToken.None));
            AssertVerifications(run, 2, 2);
        }
    }

    [Fact]
    public async Task UnitExactAggregateBudgetAcceptsBoundaryAndRejectsNextByteBeforeOpening()
    {
        using TempDirectory temp = new();
        byte[] first = [1, 2, 3], second = [4, 5];
        string left = Path.Combine(temp.Path, "left.jpg"), right = Path.Combine(temp.Path, "right.jpg");
        await File.WriteAllBytesAsync(left, first);
        await File.WriteAllBytesAsync(right, second);
        MediaAssetReference a = UnitAsset(left, first), b = UnitAsset(right, second);
        MediaAssetReference excess = UnitAsset(Path.Combine(temp.Path, "never-created.jpg"), [6]);
        MediaAssetSnapshot manifest = UnitSnapshot(temp.Path, "unit", a, b, excess);
        TransferArchiveVerificationCache cache = new(first.Length + second.Length);
        using var run = PerformanceDiagnostics.BeginRun("transfer-exact-budget", "synthetic", "unit", 1024);
        Assert.Equal(TransferAssetVerification.Available, await cache.VerifyAsync(manifest, a, CancellationToken.None));
        Assert.Equal(TransferAssetVerification.Available, await cache.VerifyAsync(manifest, b, CancellationToken.None));
        Assert.Equal(TransferAssetVerification.Available, await cache.VerifyAsync(manifest, a, CancellationToken.None));
        Assert.Equal(TransferAssetVerification.Limit, await cache.VerifyAsync(manifest, excess, CancellationToken.None));
        Assert.Equal(TransferAssetVerification.Limit, await cache.VerifyAsync(manifest, excess, CancellationToken.None));
        AssertVerifications(run, 2, 2); // no OpenVerifiedRead begin event for the over-budget path
        Assert.False(File.Exists(excess.Path));
    }

    [Fact]
    public async Task UnitOversizedFirstRequestDoesNotOpenOrConsumeTheRemainingBudget()
    {
        using TempDirectory temp = new();
        byte[] bytes = [1, 2, 3];
        string path = Path.Combine(temp.Path, "fits.jpg");
        await File.WriteAllBytesAsync(path, bytes);
        MediaAssetReference fits = UnitAsset(path, bytes);
        MediaAssetReference tooBig = UnitAsset(Path.Combine(temp.Path, "missing.jpg"), [4]) with { Length = 4 };
        MediaAssetSnapshot manifest = UnitSnapshot(temp.Path, "unit", tooBig, fits);
        TransferArchiveVerificationCache cache = new(bytes.Length);
        using var run = PerformanceDiagnostics.BeginRun("transfer-first-limit", "synthetic", "unit", 1024);
        Assert.Equal(TransferAssetVerification.Limit, await cache.VerifyAsync(manifest, tooBig, CancellationToken.None));
        AssertVerifications(run, 0, 0);
        Assert.Equal(TransferAssetVerification.Available, await cache.VerifyAsync(manifest, fits, CancellationToken.None));
        AssertVerifications(run, 1, 1);
    }

    [Fact]
    public async Task UnitZeroBudgetAllowsAnEmptyFileButNotOneAdditionalByte()
    {
        using TempDirectory temp = new();
        string path = Path.Combine(temp.Path, "empty.jpg");
        await File.WriteAllBytesAsync(path, []);
        MediaAssetReference empty = UnitAsset(path, []);
        MediaAssetReference extra = UnitAsset(Path.Combine(temp.Path, "missing.jpg"), [1]);
        MediaAssetSnapshot manifest = UnitSnapshot(temp.Path, "unit", empty, extra);
        TransferArchiveVerificationCache cache = new(0);
        using var run = PerformanceDiagnostics.BeginRun("transfer-zero-budget", "synthetic", "unit", 1024);
        Assert.Equal(TransferAssetVerification.Available, await cache.VerifyAsync(manifest, empty, CancellationToken.None));
        Assert.Equal(TransferAssetVerification.Limit, await cache.VerifyAsync(manifest, extra, CancellationToken.None));
        AssertVerifications(run, 1, 1);
        Assert.Equal(0L, Assert.Single(Events(run, "verified")).Value);
    }

    [Theory]
    [InlineData(DefaultMaximumBytes, true, 1)]
    [InlineData(DefaultMaximumBytes + 1, false, 0)]
    public async Task UnitDefaultTwoGiBBoundaryIsCheckedUsingMetadataWithoutLargeFiles(
        long declaredLength, bool withinBudget, int opens)
    {
        using TempDirectory temp = new();
        MediaAssetReference asset = UnitAsset(Path.Combine(temp.Path, "missing.jpg"), [1]) with { Length = declaredLength };
        MediaAssetSnapshot manifest = UnitSnapshot(temp.Path, "unit", asset);
        TransferArchiveVerificationCache cache = new();
        using var run = PerformanceDiagnostics.BeginRun("transfer-default-budget", "synthetic", "unit", 1024);
        TransferAssetVerification expected = withinBudget ? TransferAssetVerification.Unavailable : TransferAssetVerification.Limit;
        Assert.Equal(expected, await cache.VerifyAsync(manifest, asset, CancellationToken.None));
        Assert.Equal(expected, await cache.VerifyAsync(manifest, asset, CancellationToken.None));
        AssertVerifications(run, opens, 0);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(2147483649L)]
    [InlineData(long.MaxValue)]
    public async Task UnitInvalidMaximumReturnsLimitBeforeAnyFileOpen(long maximum)
    {
        using TempDirectory temp = new();
        string path = Path.Combine(temp.Path, "empty.jpg");
        await File.WriteAllBytesAsync(path, []);
        MediaAssetReference empty = UnitAsset(path, []);
        MediaAssetSnapshot manifest = UnitSnapshot(temp.Path, "unit", empty);
        TransferArchiveVerificationCache cache = new(maximum);
        using var run = PerformanceDiagnostics.BeginRun("transfer-invalid-budget", "synthetic", "unit", 1024);
        Assert.Equal(TransferAssetVerification.Limit, await cache.VerifyAsync(manifest, empty, CancellationToken.None));
        AssertVerifications(run, 0, 0);
    }

    [Fact]
    public async Task UnitMissingOriginsAreCachedOnceAndConservativelyChargedBeforeFurtherWork()
    {
        using TempDirectory temp = new();
        MediaAssetReference missing = UnitAsset(Path.Combine(temp.Path, "missing.jpg"), [1, 2, 3]);
        MediaAssetReference missingParent = UnitAsset(Path.Combine(temp.Path, "absent-parent", "missing.jpg"), [4, 5]);
        string path = Path.Combine(temp.Path, "exists.jpg");
        await File.WriteAllBytesAsync(path, [6]);
        MediaAssetReference exists = UnitAsset(path, [6]);
        MediaAssetSnapshot manifest = UnitSnapshot(temp.Path, "unit", missing, missingParent, exists);
        TransferArchiveVerificationCache cache = new(5);
        using var run = PerformanceDiagnostics.BeginRun("transfer-missing-budget", "synthetic", "unit", 1024);
        for (int repetition = 0; repetition < 3; repetition++)
        {
            Assert.Equal(TransferAssetVerification.Unavailable, await cache.VerifyAsync(manifest, missing, CancellationToken.None));
            Assert.Equal(TransferAssetVerification.Unavailable, await cache.VerifyAsync(manifest, missingParent, CancellationToken.None));
        }
        AssertVerifications(run, 2, 0);
        Assert.Equal(TransferAssetVerification.Limit, await cache.VerifyAsync(manifest, exists, CancellationToken.None));
        // Reappearance does not turn a cached frozen absence into fresh proof or repair the other path.
        await File.WriteAllBytesAsync(missing.Path, [1, 2, 3]);
        Assert.Equal(TransferAssetVerification.Unavailable, await cache.VerifyAsync(manifest, missing, CancellationToken.None));
        AssertVerifications(run, 2, 0);
        TransferArchiveVerificationCache nextFreeze = new(3);
        Assert.Equal(TransferAssetVerification.Available, await nextFreeze.VerifyAsync(manifest, missing, CancellationToken.None));
        AssertVerifications(run, 3, 1);
        Assert.False(Directory.Exists(Path.GetDirectoryName(missingParent.Path)));
    }

    [Theory]
    [InlineData("uncached")]
    [InlineData("available")]
    [InlineData("unavailable")]
    [InlineData("limit")]
    public async Task CancellationWinsBeforeFreshWorkCachedResultsAndLimit(string state)
    {
        using TempDirectory temp = new();
        byte[] bytes = [1, 2, 3];
        string path = Path.Combine(temp.Path, "available.jpg");
        await File.WriteAllBytesAsync(path, bytes);
        MediaAssetReference available = UnitAsset(path, bytes);
        MediaAssetReference missing = UnitAsset(Path.Combine(temp.Path, "missing.jpg"), bytes);
        MediaAssetSnapshot manifest = UnitSnapshot(temp.Path, "unit", available, missing);
        MediaAssetReference selected = state == "unavailable" ? missing : available;
        TransferArchiveVerificationCache cache = new(state == "limit" ? 0 : bytes.Length);
        using var run = PerformanceDiagnostics.BeginRun("transfer-cancellation", "synthetic", "unit", 1024);
        if (state is "available" or "unavailable")
            Assert.Equal(state == "available" ? TransferAssetVerification.Available : TransferAssetVerification.Unavailable,
                await cache.VerifyAsync(manifest, selected, CancellationToken.None));
        int opened = state is "available" or "unavailable" ? 1 : 0;
        int verified = state == "available" ? 1 : 0;
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        OperationCanceledException error = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            cache.VerifyAsync(manifest, selected, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        AssertVerifications(run, opened, verified);
        if (state == "uncached")
        {
            Assert.Equal(TransferAssetVerification.Available, await cache.VerifyAsync(manifest, available, CancellationToken.None));
            AssertVerifications(run, 1, 1); // cancelled admission did not reserve bytes
        }
    }

    private static MediaAssetReference UnitAsset(string path, byte[] bytes) => new(
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path))).ToLowerInvariant(),
        "111", "original", Path.GetFileName(path), path, bytes.LongLength,
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), "https://i.4cdn.org/b/111.jpg", "unit-synthetic.jpg", true);

    private static MediaAssetSnapshot UnitSnapshot(string directory, string? generation, params MediaAssetReference[] assets)
        => new(directory, MediaManifestStatus.Available, 4, generation, assets);

    private static PerformanceEvent[] Events(PerformanceDiagnostics.DiagnosticRun run, string outcome)
    {
        PerformanceSnapshot snapshot = run.Snapshot();
        Assert.Equal(0L, snapshot.Dropped);
        return snapshot.Events.Where(e => e.Phase == "media.original.verify" && e.Outcome == outcome).ToArray();
    }

    private static void AssertVerifications(PerformanceDiagnostics.DiagnosticRun run, int opened, int verified)
    {
        Assert.Equal(opened, Events(run, "begin").Length);
        Assert.Equal(opened, Events(run, "end").Length);
        Assert.Equal(verified, Events(run, "verified").Length);
    }
}
