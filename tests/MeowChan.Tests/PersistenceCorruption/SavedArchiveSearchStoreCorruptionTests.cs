using MeowChan.Archive;
using MeowChan.Persistence;
using Xunit.Abstractions;

namespace MeowChan.Tests.PersistenceCorruption;

/// <summary>archive-searches.json: a damaged or newer file loads as a non-writable state (with the good backup offered), never as an empty list.</summary>
internal sealed class SavedArchiveSearchStoreAdapter : StoreAdapter
{
    // PF-R3: a damaged backup is set aside as .bad, so the good primary is rotated in and the evidence stays. A newer
    // backup is valid data of a newer version and still stops the save, now with a message that names the file.
    internal override bool BackupBlocksWrite(string variant, bool stillParses) => variant == "future-version";
    internal override bool SetsDamagedBackupAside => true;
    internal override string Name => "saved-searches";
    internal override string PrimaryName => "archive-searches.json";
    internal override string VersionProperty => "schemaVersion";
    internal override long ReadCeilingBytes => SavedArchiveSearchStore.MaximumFileBytes;
    internal override StorePolicy Policy => StorePolicy.OfferRecovery;
    // The store opens the final link like the file it names; a write replaces the link, never the file behind it.
    internal override bool FollowsSymbolicLinks => true;
    // An unknown envelope field makes the document "unsupported": it is kept, read-only, with every search listed but
    // not applicable. Nothing of the user's is lost, so the backup is not offered.
    internal override bool RecoveryExpected(string variant) => variant is not ("future-version" or "unknown-extra-field");

    internal override void Seed(string directory, ProfileLease lease)
    {
        SavedArchiveSearchStore store = new(directory, lease);
        SavedArchiveSearchSnapshot first = store.Load();
        SavedArchiveSearchSnapshot second = store.Create(Guid.NewGuid(), "search " + Secret, new ArchiveSearchSpec("first"), first.Revision);
        store.Create(Guid.NewGuid(), "second", new ArchiveSearchSpec("second", hasPosts: true), second.Revision);
    }

    private static string Fingerprint(SavedArchiveSearchSnapshot snapshot) =>
        snapshot.Revision + ":" + string.Join("|", snapshot.Searches.Select(search => search.Name + "=" + search.RawDefinition));

    internal override ReadResult Read(string directory, ProfileLease lease)
    {
        try
        {
            SavedArchiveSearchSnapshot snapshot = new SavedArchiveSearchStore(directory, lease).Load();
            return snapshot.State switch
            {
                SavedSearchStoreState.Ready => ReadResult.Of(Fingerprint(snapshot)),
                SavedSearchStoreState.RecoveryAvailable => ReadResult.Offered(Fingerprint(snapshot with { Searches = snapshot.Searches })),
                _ => ReadResult.Unusable()
            };
        }
        catch (Exception error) { return ReadResult.Unusable(error); }
    }

    internal override void Mutate(string directory, ProfileLease lease)
    {
        SavedArchiveSearchStore store = new(directory, lease);
        SavedArchiveSearchSnapshot current = store.Load();
        store.Create(Guid.NewGuid(), "third", new ArchiveSearchSpec("third"), current.Revision);
    }
}

public sealed class SavedArchiveSearchStoreCorruptionTests(ITestOutputHelper log) : BackedStoreCorruptionTests(log)
{
    internal override StoreAdapter Adapter { get; } = new SavedArchiveSearchStoreAdapter();

    // PF-R3: a damaged .bak next to a good primary no longer blocks saving for good. It is set aside as .bad (byte for byte, never
    // overwritten), the good primary becomes the backup, and nothing names the profile path.
    [Theory]
    [InlineData("empty")]
    [InlineData("truncated-half")]
    [InlineData("trailing-garbage")]
    [InlineData("invalid-utf8-in-string")]
    public void ADamagedBackupIsSetAsideAndTheCommitGoesThrough(string variant)
    {
        SavedArchiveSearchStoreAdapter adapter = new();
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
        Assert.Equal(2, Directory.GetFiles(temp.Path, "archive-searches.json.bak.bad*").Length);
    }

    // A backup of a newer version is valid data of that version: the commit is refused before any file changes, and the typed
    // refusal names the file (not the path) for the window's localized text.
    [Fact]
    public void ABackupOfANewerVersionStopsTheCommitWithAMessageThatNamesTheFile()
    {
        SavedArchiveSearchStoreAdapter adapter = new();
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        adapter.Seed(temp.Path, lease);
        File.WriteAllBytes(adapter.BackupPath(temp.Path), Damage.Make("future-version", File.ReadAllBytes(adapter.BackupPath(temp.Path)), "schemaVersion"));
        byte[] primary = File.ReadAllBytes(adapter.PrimaryPath(temp.Path));
        byte[] backup = File.ReadAllBytes(adapter.BackupPath(temp.Path));

        ProfileBackupBlockedException refused = Assert.Throws<ProfileBackupBlockedException>(() => adapter.Mutate(temp.Path, lease));

        Assert.Equal("archive-searches.json.bak", refused.FileName);
        Assert.Equal(ProfileBackupBlockReason.NewerVersion, refused.Reason);
        Assert.Contains("archive-searches.json.bak", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(temp.Path, refused.Message, StringComparison.Ordinal);
        Assert.Equal(primary, File.ReadAllBytes(adapter.PrimaryPath(temp.Path)));
        Assert.Equal(backup, File.ReadAllBytes(adapter.BackupPath(temp.Path)));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.bad*"));
    }

    [Fact]
    public void ADirectoryInPlaceOfTheFileFailsWithoutNamingTheProfilePath()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        Directory.CreateDirectory(Path.Combine(temp.Path, "archive-searches.json"));
        SavedArchiveSearchSnapshot loaded = new SavedArchiveSearchStore(temp.Path, lease).Load();
        Assert.False(loaded.CanWrite);
        Assert.DoesNotContain(temp.Path, loaded.Error ?? "", StringComparison.Ordinal);
    }
}
