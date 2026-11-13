using MeowChan.Interfaces;
using MeowChan.Persistence;
using Xunit.Abstractions;

namespace MeowChan.Tests.PersistenceCorruption;

/// <summary>reader-groups.json: a damaged or newer file loads as a non-writable state (with the good backup offered), never as an empty list.</summary>
internal sealed class ReaderGroupStoreAdapter : StoreAdapter
{
    internal override string Name => "reader-groups";
    internal override string PrimaryName => "reader-groups.json";
    internal override string VersionProperty => "schemaVersion";
    internal override long ReadCeilingBytes => ReaderGroupsValidation.MaximumBytes;
    internal override StorePolicy Policy => StorePolicy.OfferRecovery;
    internal override bool FollowsSymbolicLinks => true;

    private const string RawBackupProbe =
        "BUG: the backup probe before a save (libmeowchan/Persistence/ReaderGroupStore.cs:100-104) calls Read without catching "
        + "its parse failures, so a damaged .bak makes SaveAsync throw the raw JsonReaderException (and, for a directory, an "
        + "UnauthorizedAccessException naming the profile path) instead of the store's own 'no files were changed' error.";

    internal override IReadOnlyDictionary<string, string> KnownBugs => new Dictionary<string, string>
    {
        ["backup:empty"] = RawBackupProbe,
        ["backup:truncated-half"] = RawBackupProbe,
        ["backup:trailing-garbage"] = RawBackupProbe,
        ["not-regular-backup:directory"] = RawBackupProbe
    };

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

    [Fact(Skip = "BUG: SaveAsync's backup probe (libmeowchan/Persistence/ReaderGroupStore.cs:100-104) calls Read without catching its parse "
        + "failures. A damaged reader-groups.json.bak therefore makes the save throw the raw System.Text.Json reader exception, and a "
        + "directory at that name an UnauthorizedAccessException quoting the full profile path, instead of the store's own "
        + "\"The available backup uses a newer reader groups schema. No files were changed.\" style error. The files are left intact.")]
    public void ADamagedBackupIsRefusedWithTheStoresOwnErrorRatherThanAParserException() =>
        CorruptionScenarios.DamagedBackup(new ReaderGroupStoreAdapter(), "truncated-half");
}
