using MeowChan.Interfaces;
using MeowChan.Persistence;
using Xunit.Abstractions;

namespace MeowChan.Tests.PersistenceCorruption;

/// <summary>reader-groups.json: a damaged or newer file loads as a non-writable state (with the good backup offered), never as an empty list.</summary>
internal sealed class ReaderGroupStoreAdapter : StoreAdapter
{
    // PF-R3: a damaged backup is set aside as .bad, so the good primary is rotated in and the evidence stays. A newer
    // backup is valid data of a newer version and still stops the save, now with a message that names the file.
    internal override bool BackupBlocksWrite(string variant, bool stillParses) => variant == "future-version";
    internal override bool SetsDamagedBackupAside => true;
    internal override string Name => "reader-groups";
    internal override string PrimaryName => "reader-groups.json";
    internal override string VersionProperty => "schemaVersion";
    internal override long ReadCeilingBytes => ReaderGroupsValidation.MaximumBytes;
    internal override StorePolicy Policy => StorePolicy.OfferRecovery;
    internal override bool FollowsSymbolicLinks => true;

    private static ReaderGroupsSnapshot Snapshot(string name, params ResourceId[] members) =>
        new(1, 0, new[] { new ReaderGroup(Guid.Parse("00000000-0000-0000-0000-000000000001"), name, members) });

    private static T Run<T>(Func<Task<T>> work) => Task.Run(work).GetAwaiter().GetResult();

    internal override void Seed(string directory, ProfileLease lease)
    {
        ReaderGroupStore store = new(directory, lease);
        ReaderGroupsSnapshot first = Run(() => store.SaveAsync(Snapshot("group " + Secret, new ResourceId("s", "b", "1")), 0));
        Run(() => store.SaveAsync(first with { Groups = [first.Groups[0] with { Members = [new ResourceId("s", "b", "2")] }] }, first.Revision));
    }

    private static string Fingerprint(ReaderGroupsSnapshot snapshot) =>
        snapshot.Revision + ":" + string.Join("|", snapshot.Groups.Select(group => group.Name + "=" + string.Join(",", group.Members)));

    internal override ReadResult Read(string directory, ProfileLease lease)
    {
        try
        {
            ReaderGroupsLoadResult loaded = Run(() => new ReaderGroupStore(directory, lease).LoadAsync());
            return loaded.State switch
            {
                ReaderGroupsLoadState.Ready => ReadResult.Of(Fingerprint(loaded.Snapshot!)),
                ReaderGroupsLoadState.RecoveryAvailable => ReadResult.Offered(Fingerprint(loaded.Snapshot!)),
                _ => ReadResult.Unusable()
            };
        }
        catch (Exception error) { return ReadResult.Unusable(error); }
    }

    internal override void Mutate(string directory, ProfileLease lease)
    {
        ReaderGroupStore store = new(directory, lease);
        ReaderGroupsLoadResult loaded = Run(() => store.LoadAsync());
        ReaderGroupsSnapshot draft = (loaded.Snapshot ?? ReaderGroupsSnapshot.Empty) with
        {
            Groups = [new ReaderGroup(Guid.NewGuid(), "another", [new ResourceId("s", "b", "9")])]
        };
        Run(() => store.SaveAsync(draft, loaded.Snapshot?.Revision ?? 0));
    }
}

public sealed class ReaderGroupStoreCorruptionTests(ITestOutputHelper log) : BackedStoreCorruptionTests(log)
{
    internal override StoreAdapter Adapter { get; } = new ReaderGroupStoreAdapter();

    // A damaged byte or a repeated, missing or misnamed member is damage: the good backup is offered, nothing is written.
    [Theory]
    [InlineData("duplicate-array-property-different-value")]
    [InlineData("invalid-utf8-in-string")]
    [InlineData("overlong-utf8-in-string")]
    [InlineData("version-missing")]
    public void ADamagedByteNeverSilentlyChangesTheReaderGroupsDocument(string variant) =>
        CorruptionScenarios.DamagedPrimary(new ReaderGroupStoreAdapter(), variant, null, "envelope");

    [Fact]
    public void AMisnamedRevisionIsDamageNotRevisionZero()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        ReaderGroupStoreAdapter adapter = new();
        adapter.Seed(temp.Path, lease);
        string path = adapter.PrimaryPath(temp.Path);
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"revision\"", "\"revisiom\"", StringComparison.Ordinal));
        Assert.Equal(ReadKind.Recovery, adapter.Read(temp.Path, lease).Kind);
    }

    [Fact]
    public void FilesWrittenByTheStoreStillLoad()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        ReaderGroupStoreAdapter adapter = new();
        adapter.Seed(temp.Path, lease);
        Assert.Equal(ReadKind.Data, adapter.Read(temp.Path, lease).Kind);
        adapter.Mutate(temp.Path, lease);
        Assert.Equal(ReadKind.Data, adapter.Read(temp.Path, lease).Kind);
    }

    // PF-R3: a damaged .bak next to a good primary no longer blocks saving for good. It is set aside as .bad (byte for byte, never
    // overwritten), the good primary becomes the backup, and nothing names the profile path.
    [Theory]
    [InlineData("empty")]
    [InlineData("truncated-half")]
    [InlineData("trailing-garbage")]
    public void ADamagedBackupIsSetAsideAndTheSaveGoesThrough(string variant)
    {
        ReaderGroupStoreAdapter adapter = new();
        CorruptionScenarios.DamagedBackup(adapter, variant);
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        adapter.Seed(temp.Path, lease);
        byte[] goodPrimary = File.ReadAllBytes(adapter.PrimaryPath(temp.Path));
        byte[] damaged = Damage.Make(variant, File.ReadAllBytes(adapter.BackupPath(temp.Path)), "schemaVersion");
        File.WriteAllBytes(adapter.BackupPath(temp.Path), damaged);

        adapter.Mutate(temp.Path, lease);

        Assert.Equal(goodPrimary, File.ReadAllBytes(adapter.BackupPath(temp.Path)));
        Assert.Equal(damaged, File.ReadAllBytes(adapter.BackupPath(temp.Path) + ".bad"));
        Assert.Equal(ReadKind.Data, adapter.Read(temp.Path, lease).Kind);
        // A second damaged backup never overwrites the first set-aside copy.
        File.WriteAllBytes(adapter.BackupPath(temp.Path), damaged.Concat(new byte[] { 0x20, 0x7B }).ToArray());
        adapter.Mutate(temp.Path, lease);
        Assert.Equal(damaged, File.ReadAllBytes(adapter.BackupPath(temp.Path) + ".bad"));
        Assert.Equal(2, Directory.GetFiles(temp.Path, "reader-groups.json.bak.bad*").Length);
    }

    // A backup of a newer version is valid data of that version: the save is refused before any file changes, and the typed
    // refusal names the file (not the path) for the window's localized text.
    [Fact]
    public void ABackupOfANewerVersionStopsTheSaveWithAMessageThatNamesTheFile()
    {
        ReaderGroupStoreAdapter adapter = new();
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        adapter.Seed(temp.Path, lease);
        File.WriteAllBytes(adapter.BackupPath(temp.Path), Damage.Make("future-version", File.ReadAllBytes(adapter.BackupPath(temp.Path)), "schemaVersion"));
        byte[] primary = File.ReadAllBytes(adapter.PrimaryPath(temp.Path));
        byte[] backup = File.ReadAllBytes(adapter.BackupPath(temp.Path));

        ProfileBackupBlockedException refused = Assert.Throws<ProfileBackupBlockedException>(() => adapter.Mutate(temp.Path, lease));

        Assert.Equal("reader-groups.json.bak", refused.FileName);
        Assert.Equal(ProfileBackupBlockReason.NewerVersion, refused.Reason);
        Assert.Contains("reader-groups.json.bak", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(temp.Path, refused.Message, StringComparison.Ordinal);
        Assert.Equal(primary, File.ReadAllBytes(adapter.PrimaryPath(temp.Path)));
        Assert.Equal(backup, File.ReadAllBytes(adapter.BackupPath(temp.Path)));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.bad*"));
    }
}
