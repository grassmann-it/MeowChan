using MeowChan.Archive;
using MeowChan.Persistence;
using Xunit.Abstractions;

namespace MeowChan.Tests.PersistenceCorruption;

/// <summary>archive-searches.json: a damaged or newer file loads as a non-writable state (with the good backup offered), never as an empty list.</summary>
internal sealed class SavedArchiveSearchStoreAdapter : StoreAdapter
{
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

    // A damaged .bak stops the commit with the store's own error; neither file changes and no message names the profile path.
    [Theory]
    [InlineData("empty")]
    [InlineData("truncated-half")]
    [InlineData("trailing-garbage")]
    [InlineData("invalid-utf8-in-string")]
    public void ADamagedBackupIsRefusedWithTheStoresOwnErrorRatherThanAParserException(string variant)
    {
        SavedArchiveSearchStoreAdapter adapter = new();
        CorruptionScenarios.DamagedBackup(adapter, variant);
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        adapter.Seed(temp.Path, lease);
        File.WriteAllBytes(adapter.BackupPath(temp.Path), Damage.Make(variant, File.ReadAllBytes(adapter.BackupPath(temp.Path)), "schemaVersion"));
        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => adapter.Mutate(temp.Path, lease));
        Assert.DoesNotContain(temp.Path, refused.Message, StringComparison.Ordinal);
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
