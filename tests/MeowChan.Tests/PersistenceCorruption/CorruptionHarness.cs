using System.Security.Cryptography;
using System.Text;
using MeowChan.Persistence;
using Xunit.Abstractions;

namespace MeowChan.Tests.PersistenceCorruption;

// Table-driven damaged-file tests for the persistence formats of 1.1.0. Every store is described by one StoreAdapter; the
// scenarios below assert the same contract for all of them: no unhandled exception, the documented fallback, a good copy is
// never overwritten by a write that follows a failed read, and messages that carry neither paths nor private values.

/// <summary>What a read produced. Data: a document that parsed (its fingerprint says which one); Unusable: damaged, newer or unreadable; Recovery: a good backup is offered.</summary>
internal enum ReadKind { Data, Unusable, Recovery }

internal sealed record ReadResult(ReadKind Kind, string Fingerprint = "", Exception? Error = null)
{
    internal static ReadResult Of(string fingerprint) => new(ReadKind.Data, fingerprint);
    internal static ReadResult Unusable(Exception? error = null) => new(ReadKind.Unusable, "", error);
    internal static ReadResult Offered(string fingerprint) => new(ReadKind.Recovery, fingerprint);

    /// <summary>Runs a reading store call; any exception becomes an "unusable" result that the scenarios then judge.</summary>
    internal static ReadResult Guard(Func<string> read)
    {
        try { return Of(read()); }
        catch (Exception error) { return Unusable(error); }
    }
}

/// <summary>Refuse: damaged data is never used or replaced. OfferRecovery: a good backup is offered, nothing is restored implicitly. Rebuild: disposable cache, the next save replaces it.</summary>
internal enum StorePolicy { Refuse, OfferRecovery, Rebuild }

internal abstract class StoreAdapter
{
    /// <summary>Secret that every seeded document carries; no failure message may repeat it.</summary>
    internal const string Secret = "secret-private-value-7f3a";

    internal abstract string Name { get; }
    internal abstract string PrimaryName { get; }
    internal abstract string VersionProperty { get; }
    /// <summary>The fixed technical read ceiling; a file above it is never read.</summary>
    internal abstract long ReadCeilingBytes { get; }
    internal virtual bool HasBackup => true;
    internal virtual StorePolicy Policy => StorePolicy.Refuse;
    /// <summary>
    /// True for a store that opens a final symbolic link as if it were the file (the others reject it). Such a store also
    /// cannot tell a dangling link from a missing file, so it may read the empty document there.
    /// </summary>
    internal virtual bool FollowsSymbolicLinks => false;

    /// <summary>
    /// True when the store promises failures that name neither the profile path nor a stored value. The disposable media
    /// index cache does not: its I/O failures are logged, never shown, and carry no private value.
    /// </summary>
    internal virtual bool HidesPaths => true;

    /// <summary>Writes two good generations so that the primary and (when kept) the backup are valid and different.</summary>
    internal abstract void Seed(string directory, ProfileLease lease);
    internal abstract ReadResult Read(string directory, ProfileLease lease);
    /// <summary>One real mutation. Throws when the store refuses; may block while an async store works.</summary>
    internal abstract void Mutate(string directory, ProfileLease lease);

    internal virtual bool IsAllowedFailure(Exception error) =>
        error is InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException;

    /// <summary>Extra expectations about the typed failure of a read (for stores with failure codes).</summary>
    internal virtual void CheckReadError(string variant, Exception error) { }

    /// <summary>True for a variant whose partly different reading is the documented behavior (the cache: "a repeated member keeps its last value").</summary>
    internal virtual bool ToleratesChangedData(string variant) => false;

    /// <summary>
    /// True when a damaged backup must stop a write of the good primary. By default only a backup that no longer reads as a
    /// valid document is evidence worth keeping; one that still parses (and a newer one, always) is judged by the store.
    /// </summary>
    internal virtual bool BackupBlocksWrite(string variant, bool stillParses) => !stillParses || variant == "future-version";

    /// <summary>
    /// True when a save over a damaged backup (one that no longer reads as a document) first moves it aside as <c>.bad</c>, byte for
    /// byte, so that the good primary is rotated into the backup and the evidence stays.
    /// </summary>
    internal virtual bool SetsDamagedBackupAside => false;

    /// <summary>
    /// True when an <see cref="StorePolicy.OfferRecovery"/> store must offer the good backup for this damage. A document that
    /// still parses but is not supported (a newer schema, unknown envelope fields) is kept read-only instead: the user's data is
    /// whole, so there is nothing to recover from.
    /// </summary>
    internal virtual bool RecoveryExpected(string variant) => variant != "future-version";

    /// <summary>"scenario:variant" -> reason, for a bug that is reported instead of fixed; the scenario is then skipped by name.</summary>
    internal virtual IReadOnlyDictionary<string, string> KnownBugs => new Dictionary<string, string>();

    internal string PrimaryPath(string directory) => Path.Combine(directory, PrimaryName);
    internal string BackupPath(string directory) => Path.Combine(directory, PrimaryName + ".bak");
    public override string ToString() => Name;
}

internal static class CorruptionScenarios
{
    /// <summary>Length and digest of one file, streamed: an oversized file must not be read into the test's memory either.</summary>
    private sealed record FileState(long Length, string Digest);

    private sealed record Files(string[] Names, FileState? Primary, FileState? Backup);

    private static FileState StateOf(byte[] bytes) => new(bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));

    /// <summary>Above this size only the head is digested: a deliberately huge file is never pulled through memory here either.</summary>
    private const int DigestedHead = 1024 * 1024;

    private static FileState? State(string path)
    {
        if (!File.Exists(path)) return null;
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long length = stream.Length;
        if (length <= DigestedHead) return new(length, Convert.ToHexString(SHA256.HashData(stream)));
        byte[] head = new byte[DigestedHead];
        stream.ReadExactly(head);
        return new(length, Convert.ToHexString(SHA256.HashData(head)));
    }

    private static Files Capture(StoreAdapter adapter, string directory) => new(
        Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToArray(),
        State(adapter.PrimaryPath(directory)), State(adapter.BackupPath(directory)));

    private static void AssertUnchanged(Files before, Files after, string context)
    {
        Assert.True(before.Names.SequenceEqual(after.Names), $"{context}: the directory changed ({string.Join(",", before.Names)} -> {string.Join(",", after.Names)}).");
        Assert.True(before.Primary == after.Primary, $"{context}: the primary file was changed.");
        Assert.True(before.Backup == after.Backup, $"{context}: the backup file was changed.");
    }

    private static bool Same(byte[]? left, byte[]? right) => left == null ? right == null : right != null && left.AsSpan().SequenceEqual(right);

    private sealed class Setup : IDisposable
    {
        internal readonly TempDirectory Temp = new();
        internal ProfileLease Lease;
        internal byte[] GoodPrimary = [];
        internal byte[]? GoodBackup;
        internal ReadResult Good = ReadResult.Unusable();
        internal string? BackupFingerprint;

        internal Setup(StoreAdapter adapter)
        {
            Lease = ProfileLease.Acquire(Temp.Path);
            adapter.Seed(Temp.Path, Lease);
            GoodPrimary = File.ReadAllBytes(adapter.PrimaryPath(Temp.Path));
            Good = adapter.Read(Temp.Path, Lease);
            Assert.True(Good.Kind == ReadKind.Data, $"{adapter.Name}: the seeded store must read back as good data ({Good.Error?.GetType().Name}).");
            Assert.NotEmpty(Good.Fingerprint);
            if (adapter.HasBackup)
            {
                Assert.True(File.Exists(adapter.BackupPath(Temp.Path)), $"{adapter.Name}: the seed must leave a backup.");
                GoodBackup = File.ReadAllBytes(adapter.BackupPath(Temp.Path));
                Assert.False(Same(GoodPrimary, GoodBackup), "The two seeded generations must differ.");
                // What the backup alone would read as, so an offered recovery can be compared with it.
                File.WriteAllBytes(adapter.PrimaryPath(Temp.Path), GoodBackup);
                ReadResult asPrimary = adapter.Read(Temp.Path, Lease);
                Assert.Equal(ReadKind.Data, asPrimary.Kind);
                BackupFingerprint = asPrimary.Fingerprint;
                File.WriteAllBytes(adapter.PrimaryPath(Temp.Path), GoodPrimary);
            }
        }

        public void Dispose()
        {
            Lease.Dispose();
            Temp.Dispose();
        }
    }

    private static string? Skipped(StoreAdapter adapter, string scenario, string variant, ITestOutputHelper? log)
    {
        if (!adapter.KnownBugs.TryGetValue(scenario + ":" + variant, out string? reason)) return null;
        log?.WriteLine($"Known bug, covered by a skipped test: {reason}");
        return reason;
    }

    private static Exception? Try(Action action)
    {
        try { action(); return null; }
        catch (Exception error) { return error; }
    }

    private static void AssertHygiene(StoreAdapter adapter, string directory, Exception error, string context)
    {
        Assert.True(adapter.IsAllowedFailure(error), $"{context}: unexpected {error.GetType().FullName}: {error.Message}");
        for (Exception? at = error; at != null; at = at.InnerException)
        {
            if (adapter.HidesPaths) Assert.DoesNotContain(directory, at.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(StoreAdapter.Secret, at.Message, StringComparison.Ordinal);
        }
    }

    private static void AssertReadJudged(StoreAdapter adapter, Setup setup, ReadResult read, string variant, bool backupGood, string context)
    {
        if (read.Error != null)
        {
            AssertHygiene(adapter, setup.Temp.Path, read.Error, context + " read");
            adapter.CheckReadError(variant, read.Error);
        }
        switch (read.Kind)
        {
            case ReadKind.Data:
                // A document that still parses must be exactly the good one; anything else is a silent change of the user's data.
                Assert.True(read.Fingerprint == setup.Good.Fingerprint || adapter.ToleratesChangedData(variant), $"{context}: the damaged file was read as different data ({read.Fingerprint} instead of {setup.Good.Fingerprint}).");
                break;
            case ReadKind.Recovery:
                Assert.Equal(StorePolicy.OfferRecovery, adapter.Policy);
                Assert.True(backupGood, $"{context}: a recovery was offered without a good backup.");
                Assert.Equal(setup.BackupFingerprint, read.Fingerprint);
                break;
            default:
                if (adapter.Policy == StorePolicy.OfferRecovery && backupGood && adapter.RecoveryExpected(variant))
                    Assert.True(false, $"{context}: the good backup was not offered ({read.Error?.Message}).");
                break;
        }
    }

    /// <summary>The primary is damaged in one named way; the backup is good (<paramref name="backupVariant"/> null) or damaged.</summary>
    internal static void DamagedPrimary(StoreAdapter adapter, string variant, string? backupVariant, string scenario, ITestOutputHelper? log = null)
    {
        if (Skipped(adapter, scenario, variant, log) != null) return;
        using Setup setup = new(adapter);
        string directory = setup.Temp.Path;
        byte[] damaged = Damage.Make(variant, setup.GoodPrimary, adapter.VersionProperty);
        File.WriteAllBytes(adapter.PrimaryPath(directory), damaged);
        if (backupVariant != null)
            File.WriteAllBytes(adapter.BackupPath(directory), Damage.Make(backupVariant, setup.GoodBackup!, adapter.VersionProperty));
        bool backupGood = backupVariant == null && adapter.HasBackup;
        Files before = Capture(adapter, directory);
        string context = $"{adapter.Name} [{variant}{(backupVariant == null ? "" : " + backup " + backupVariant)}]";

        ReadResult read = adapter.Read(directory, setup.Lease);
        AssertReadJudged(adapter, setup, read, variant, backupGood, context);
        AssertUnchanged(before, Capture(adapter, directory), context + ": a read changed files");
        if (read.Kind == ReadKind.Data) return;

        Exception? refused = Try(() => adapter.Mutate(directory, setup.Lease));
        if (adapter.Policy == StorePolicy.Rebuild)
        {
            Assert.True(refused == null, $"{context}: the cache could not be rebuilt over the damaged file ({refused?.GetType().Name}: {refused?.Message}).");
            Assert.Equal(setup.Good.Fingerprint, adapter.Read(directory, setup.Lease).Fingerprint);
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory, "*.tmp"));
            return;
        }
        Assert.True(refused != null, $"{context}: a write succeeded over an unusable document.");
        AssertHygiene(adapter, directory, refused!, context + " write");
        AssertUnchanged(before, Capture(adapter, directory), context + ": a refused write changed files");
        // The good copy is still what a later read of it yields.
        if (backupGood && backupVariant == null)
        {
            File.WriteAllBytes(adapter.PrimaryPath(directory), setup.GoodBackup!);
            Assert.Equal(setup.BackupFingerprint, adapter.Read(directory, setup.Lease).Fingerprint);
        }
    }

    /// <summary>The primary is good and the backup is damaged: the primary reads, and a write follows the store's documented rule.</summary>
    internal static void DamagedBackup(StoreAdapter adapter, string variant, ITestOutputHelper? log = null)
    {
        if (Skipped(adapter, "backup", variant, log) != null) return;
        using Setup setup = new(adapter);
        string directory = setup.Temp.Path;
        byte[] damaged = Damage.Make(variant, setup.GoodBackup!, adapter.VersionProperty);
        // Does this damage still read as a valid document? Asked of the store itself, in its own profile, before the backup
        // is touched: the primary is put back immediately afterwards.
        File.WriteAllBytes(adapter.PrimaryPath(directory), damaged);
        bool stillParses = adapter.Read(directory, setup.Lease).Kind == ReadKind.Data;
        File.WriteAllBytes(adapter.PrimaryPath(directory), setup.GoodPrimary);
        File.WriteAllBytes(adapter.BackupPath(directory), damaged);
        Files before = Capture(adapter, directory);
        string context = $"{adapter.Name} [backup {variant}{(stillParses ? ", still parses" : "")}]";

        ReadResult read = adapter.Read(directory, setup.Lease);
        Assert.True(read.Kind == ReadKind.Data && read.Fingerprint == setup.Good.Fingerprint, $"{context}: a good primary must read although its backup is damaged ({read.Error?.Message}).");
        AssertUnchanged(before, Capture(adapter, directory), context + ": a read changed files");

        Exception? outcome = Try(() => adapter.Mutate(directory, setup.Lease));
        if (adapter.BackupBlocksWrite(variant, stillParses))
        {
            Assert.True(outcome != null, $"{context}: the write replaced a backup that the store must keep.");
            AssertHygiene(adapter, directory, outcome!, context + " write");
            AssertUnchanged(before, Capture(adapter, directory), context + ": a refused write changed files");
            Assert.Empty(Directory.GetFiles(directory, Path.GetFileName(adapter.BackupPath(directory)) + ".bad*"));
        }
        else
        {
            Assert.True(outcome == null, $"{context}: a damaged backup stopped the write ({outcome?.GetType().Name}: {outcome?.Message}).");
            Files after = Capture(adapter, directory);
            // The good primary became the backup and the new primary is valid.
            Assert.True(StateOf(setup.GoodPrimary) == after.Backup, $"{context}: the previous primary did not become the backup.");
            Assert.Equal(ReadKind.Data, adapter.Read(directory, setup.Lease).Kind);
            if (adapter.SetsDamagedBackupAside && !stillParses)
            {
                string[] aside = Directory.GetFiles(directory, Path.GetFileName(adapter.BackupPath(directory)) + ".bad*");
                Assert.True(aside.Length == 1, $"{context}: the damaged backup was not set aside exactly once ({aside.Length} .bad files).");
                Assert.True(File.ReadAllBytes(aside[0]).AsSpan().SequenceEqual(damaged), $"{context}: the set-aside copy is not the damaged backup, byte for byte.");
            }
        }
    }

    /// <summary>A file above the read ceiling is never read; the store refuses it and keeps it.</summary>
    internal static void Oversized(StoreAdapter adapter, bool backup, ITestOutputHelper? log = null)
    {
        string scenario = backup ? "oversized-backup" : "oversized";
        if (Skipped(adapter, scenario, "", log) != null) return;
        using Setup setup = new(adapter);
        string directory = setup.Temp.Path;
        string target = backup ? adapter.BackupPath(directory) : adapter.PrimaryPath(directory);
        long size = adapter.ReadCeilingBytes + 1;
        Damage.WriteSparse(target, size);
        Files before = Capture(adapter, directory);
        string context = $"{adapter.Name} [{(backup ? "oversized backup" : "oversized primary")}]";
        long threshold = Math.Max(size / 2, 1);

        long smallest = long.MaxValue;
        ReadResult read = ReadResult.Unusable();
        for (int attempt = 0; attempt < 3; attempt++)
        {
            long start = GC.GetTotalAllocatedBytes(precise: false);
            read = adapter.Read(directory, setup.Lease);
            smallest = Math.Min(smallest, GC.GetTotalAllocatedBytes(precise: false) - start);
        }
        Assert.True(smallest < threshold, $"{context}: reading allocated {smallest} bytes for a file of {size}: it was read into memory.");
        if (backup)
            Assert.True(read.Kind == ReadKind.Data && read.Fingerprint == setup.Good.Fingerprint, $"{context}: the good primary must read ({read.Error?.Message}).");
        else
        {
            Assert.NotEqual(ReadKind.Data, read.Kind);
            Assert.True(adapter.Policy != StorePolicy.Rebuild ? read.Error != null || adapter.Policy == StorePolicy.OfferRecovery : true);
            if (read.Error != null) AssertHygiene(adapter, directory, read.Error, context + " read");
        }
        AssertUnchanged(before, Capture(adapter, directory), context + ": a read changed files");
        // An oversized backup never reads as a document, so the store's rule for unreadable evidence applies.
        if (backup ? !adapter.BackupBlocksWrite("oversized", stillParses: false) : adapter.Policy == StorePolicy.Rebuild)
        {
            // Nothing to protect (or nothing to keep): the write may go through.
            Assert.Null(Try(() => adapter.Mutate(directory, setup.Lease)));
            return;
        }

        long writeStart = GC.GetTotalAllocatedBytes(precise: false);
        Exception? refused = Try(() => adapter.Mutate(directory, setup.Lease));
        long used = GC.GetTotalAllocatedBytes(precise: false) - writeStart;
        Assert.True(used < threshold, $"{context}: a write allocated {used} bytes for a file of {size}.");
        Assert.True(refused != null, $"{context}: a write succeeded next to an unreadable file.");
        AssertHygiene(adapter, directory, refused!, context + " write");
        AssertUnchanged(before, Capture(adapter, directory), context + ": a refused write changed files");
    }

    internal static readonly string[] NotRegular = { "directory", "symlink-to-good-copy", "dangling-symlink" };

    /// <summary>A directory or a symbolic link stands where the primary should be: no crash, nothing behind the link is touched.</summary>
    internal static void NotARegularFile(StoreAdapter adapter, string kind, ITestOutputHelper? log = null)
    {
        if (Skipped(adapter, "not-regular", kind, log) != null) return;
        using Setup setup = new(adapter);
        string directory = setup.Temp.Path;
        string primary = adapter.PrimaryPath(directory);
        string outside = Path.Combine(directory, "..", "outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            string target = Path.Combine(outside, "target.json");
            byte[]? targetBytes = null;
            string marker = Path.Combine(primary, "marker.txt");
            File.Delete(primary);
            switch (kind)
            {
                case "directory":
                    Directory.CreateDirectory(primary);
                    File.WriteAllText(marker, "keep me");
                    break;
                case "symlink-to-good-copy":
                    File.WriteAllBytes(target, setup.GoodPrimary);
                    targetBytes = setup.GoodPrimary;
                    File.CreateSymbolicLink(primary, target);
                    break;
                default:
                    File.CreateSymbolicLink(primary, Path.Combine(outside, "missing.json"));
                    break;
            }
            string context = $"{adapter.Name} [{kind}]";
            string[] namesBefore = Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToArray();
            byte[]? backupBefore = File.Exists(adapter.BackupPath(directory)) ? File.ReadAllBytes(adapter.BackupPath(directory)) : null;

            ReadResult read = adapter.Read(directory, setup.Lease);
            if (read.Error != null) AssertHygiene(adapter, directory, read.Error, context + " read");
            if (read.Kind == ReadKind.Data)
                // A store that opens the final link sees the good copy through it, and cannot tell a dangling link from a
                // missing file: there it starts empty, exactly as it would in a fresh profile.
                Assert.True(adapter.FollowsSymbolicLinks
                    && (kind == "symlink-to-good-copy" && read.Fingerprint == setup.Good.Fingerprint || kind == "dangling-symlink"),
                    $"{context}: the entry was read as data ({read.Fingerprint}).");

            Exception? outcome = Try(() => adapter.Mutate(directory, setup.Lease));
            if (outcome != null) AssertHygiene(adapter, directory, outcome, context + " write");
            else Assert.True(adapter.Policy == StorePolicy.Rebuild || adapter.FollowsSymbolicLinks && kind != "directory",
                $"{context}: a write succeeded with a {kind} in place of the file.");

            // Nothing behind the entry may change, whatever the store did with the entry itself.
            if (targetBytes != null) Assert.True(Same(targetBytes, File.ReadAllBytes(target)), $"{context}: the linked file was written through.");
            if (kind == "directory" && (adapter.Policy != StorePolicy.Rebuild || outcome != null))
            {
                Assert.True(Directory.Exists(primary) && File.Exists(marker), $"{context}: the directory was removed or emptied.");
                Assert.Equal("keep me", File.ReadAllText(marker));
            }
            // Only a failed write must leave the backup alone (a successful one may have rotated the link itself into it).
            if (outcome != null)
                Assert.True(Same(backupBefore, File.Exists(adapter.BackupPath(directory)) ? File.ReadAllBytes(adapter.BackupPath(directory)) : null),
                    $"{context}: the backup changed although the write failed.");
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory, "*.tmp"));
            Assert.True(namesBefore.All(name => Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName).Contains(name)) || outcome == null,
                $"{context}: an entry disappeared although the write failed.");
        }
        finally { Directory.Delete(outside, recursive: true); }
    }

    /// <summary>The backup name is occupied by a directory or a link: a write must not destroy what is there.</summary>
    internal static void NotARegularBackup(StoreAdapter adapter, string kind, ITestOutputHelper? log = null)
    {
        if (Skipped(adapter, "not-regular-backup", kind, log) != null) return;
        using Setup setup = new(adapter);
        string directory = setup.Temp.Path;
        string backup = adapter.BackupPath(directory);
        string outside = Path.Combine(directory, "..", "outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            string target = Path.Combine(outside, "target.json");
            string marker = Path.Combine(backup, "marker.txt");
            File.Delete(backup);
            byte[] targetBytes = Encoding.UTF8.GetBytes("{\"user\":\"file\"}");
            if (kind == "directory") { Directory.CreateDirectory(backup); File.WriteAllText(marker, "keep me"); }
            else { File.WriteAllBytes(target, targetBytes); File.CreateSymbolicLink(backup, target); }
            Files before = Capture(adapter, directory);
            string context = $"{adapter.Name} [backup {kind}]";

            ReadResult read = adapter.Read(directory, setup.Lease);
            Assert.True(read.Kind == ReadKind.Data && read.Fingerprint == setup.Good.Fingerprint, $"{context}: a good primary must read ({read.Error?.Message}).");
            Exception? outcome = Try(() => adapter.Mutate(directory, setup.Lease));
            if (outcome != null)
            {
                AssertHygiene(adapter, directory, outcome, context + " write");
                Assert.True(before.Primary == State(adapter.PrimaryPath(directory)), $"{context}: a refused write changed the primary.");
            }
            if (kind == "directory") Assert.True(Directory.Exists(backup) && File.Exists(marker), $"{context}: the directory was removed or emptied.");
            else Assert.True(Same(targetBytes, File.ReadAllBytes(target)), $"{context}: the linked file was written through.");
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory, "*.tmp"));
        }
        finally { Directory.Delete(outside, recursive: true); }
    }
}

/// <summary>The scenarios for a store without a backup file.</summary>
public abstract class StoreCorruptionTests(ITestOutputHelper log)
{
    internal abstract StoreAdapter Adapter { get; }

    [Theory]
    [MemberData(nameof(Damage.Primary), MemberType = typeof(Damage))]
    public void DamagedPrimaryIsHandled(string variant) => CorruptionScenarios.DamagedPrimary(Adapter, variant, null, "primary", log);


    [Theory]
    [InlineData("directory")]
    [InlineData("symlink-to-good-copy")]
    [InlineData("dangling-symlink")]
    public void DirectoryOrLinkInPlaceOfThePrimaryIsHandled(string kind) => CorruptionScenarios.NotARegularFile(Adapter, kind, log);
}

/// <summary>The scenarios for a store that keeps a <c>.bak</c>.</summary>
public abstract class BackedStoreCorruptionTests(ITestOutputHelper log) : StoreCorruptionTests(log)
{
    [Theory]
    [MemberData(nameof(Damage.Primary), MemberType = typeof(Damage))]
    public void DamagedPrimaryWithGoodBackupIsHandled(string variant) => CorruptionScenarios.DamagedPrimary(Adapter, variant, null, "primary-good-backup", log);

    [Theory]
    [MemberData(nameof(Damage.Primary), MemberType = typeof(Damage))]
    public void BothFilesDamagedAreHandled(string variant) => CorruptionScenarios.DamagedPrimary(Adapter, variant, "truncated-half", "both", log);

    [Fact]
    public void BothFilesFromANewerVersionAreKept() => CorruptionScenarios.DamagedPrimary(Adapter, "future-version", "future-version", "both-newer", log);

    [Theory]
    [MemberData(nameof(Damage.Backup), MemberType = typeof(Damage))]
    public void DamagedBackupNextToAGoodPrimaryIsHandled(string variant) => CorruptionScenarios.DamagedBackup(Adapter, variant, log);


    [Theory]
    [InlineData("directory")]
    [InlineData("symlink")]
    public void DirectoryOrLinkInPlaceOfTheBackupIsHandled(string kind) => CorruptionScenarios.NotARegularBackup(Adapter, kind, log);
}

/// <summary>
/// Runs alone: the allocation counter is process-wide, so a test running in parallel would be counted as if the store had
/// read the oversized file.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AllocationMeasurementCollection
{
    public const string Name = "Allocation measurement";
}

/// <summary>A file above a store's read ceiling is refused from its length, never read into memory, and kept.</summary>
[Collection(AllocationMeasurementCollection.Name)]
public sealed class OversizedStoreFileTests(ITestOutputHelper log)
{
    private static readonly Dictionary<string, Func<StoreAdapter>> Stores = new(StringComparer.Ordinal)
    {
        ["favorites"] = () => new FavoriteStoreAdapter(),
        ["thread-favorites"] = () => new ThreadFavoriteStoreAdapter(),
        ["ratings"] = () => new MediaRatingStoreAdapter(),
        ["post-bookmarks"] = () => new PostBookmarkStoreAdapter(),
        ["saved-searches"] = () => new SavedArchiveSearchStoreAdapter(),
        ["post-filters"] = () => new PostFilterStoreAdapter(),
        ["reader-groups"] = () => new ReaderGroupStoreAdapter(),
        ["booru-catalog"] = () => new BooruCatalogAdapter(),
        ["private-tags-v1"] = () => new PrivateTagStoreAdapter(),
        ["private-tags-v2"] = () => new PrivateTagStoreV2Adapter(),
        ["local-imports"] = () => new ManualImportCatalogAdapter(),
        ["media-index-cache"] = () => new MediaIndexCacheAdapter()
    };

    public static IEnumerable<object[]> All() => Stores.Keys.Select(name => new object[] { name });
    public static IEnumerable<object[]> Backed() => Stores.Where(store => store.Value().HasBackup).Select(store => new object[] { store.Key });

    [Theory]
    [MemberData(nameof(All))]
    public void OversizedPrimaryIsNeverReadIntoMemory(string store) => CorruptionScenarios.Oversized(Stores[store](), backup: false, log);

    [Theory]
    [MemberData(nameof(Backed))]
    public void OversizedBackupIsNeverReadIntoMemory(string store) => CorruptionScenarios.Oversized(Stores[store](), backup: true, log);
}
