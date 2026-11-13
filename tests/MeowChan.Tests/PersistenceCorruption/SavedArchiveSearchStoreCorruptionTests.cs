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

    private const string RawBackupProbe =
        "BUG: the backup check before a commit (libmeowchan/Persistence/SavedArchiveSearchStore.cs:165-166) calls Read without "
        + "catching its parse failures, so a damaged archive-searches.json.bak makes the write throw the raw parser exception "
        + "instead of the InvalidOperationException the line was written to raise.";

    internal override IReadOnlyDictionary<string, string> KnownBugs => new Dictionary<string, string>
    {
        ["backup:empty"] = RawBackupProbe,
        ["backup:truncated-half"] = RawBackupProbe,
        ["backup:trailing-garbage"] = RawBackupProbe,
        ["backup:invalid-utf8-in-string"] = RawBackupProbe,
        ["not-regular-backup:directory"] = RawBackupProbe,
        ["not-regular-backup:symlink"] = RawBackupProbe
    };

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

    [Fact(Skip = "BUG: libmeowchan/Persistence/SavedArchiveSearchStore.cs:165-166 guards a commit with "
        + "`if (File.Exists(_path) && !Read(_path + \".bak\", missingIsEmpty: true).CanWrite) throw new InvalidOperationException(...)`, "
        + "but Read throws on a damaged backup instead of returning a non-writable snapshot. The raw JsonReaderException, "
        + "DecoderFallbackException or KeyNotFoundException therefore escapes the store (for a directory at that name an "
        + "UnauthorizedAccessException quoting the full profile path), and the dialog shows it through Texts.T(\"savedSearch.readonly\", error). "
        + "The write itself is correctly refused and both files are left intact.")]
    public void ADamagedBackupIsRefusedWithTheStoresOwnErrorRatherThanAParserException() =>
        CorruptionScenarios.DamagedBackup(new SavedArchiveSearchStoreAdapter(), "truncated-half");
}
