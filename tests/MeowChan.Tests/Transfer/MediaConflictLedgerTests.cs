using Microsoft.Data.Sqlite;
using MeowChan.Archive;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;
using Workspace = MeowChan.Tests.Transfer.NativeMediaImportTests.Workspace;

namespace MeowChan.Tests.Transfer;

// Schema 12 conflict ledger: durable, idempotent, and never reopened by a replay.
public sealed class MediaConflictLedgerTests
{
    private static readonly ResourceId Resource = new("4chan", "g", "123");
    private static string H(char c) => new(c, 64);

    private static NativeMediaConflictInventory Inventory(params char[] candidates)
        => new(new NativeMediaConflictProvenance(Guid.NewGuid(), H('a'), H('b')),
            candidates.Select(c => new NativeMediaConflictEntry(new NativeMediaConflictIdentity(Resource, H('c'), H('d'), H(c)),
                new NativeMediaConflictVariantProjection(H('d'), NativeMediaConflictVariantStatus.Active, 10),
                new NativeMediaConflictVariantProjection(H(c), NativeMediaConflictVariantStatus.Inactive, 20))));

    [Fact]
    public async Task NewCatalogIsSchema12AndRecordsDecideAndSurvivesRestartIdempotently()
    {
        using Workspace files = new();
        NativeMediaConflictInventory inventory = Inventory('e', 'f');
        string first = inventory.Entries[0].Identity.IdentitySha256;
        string second = inventory.Entries[1].Identity.IdentitySha256;
        DateTimeOffset at = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
        using (ProfileLease lease = ProfileLease.Acquire(files.Profile))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
            lease.Admission.CompleteStartup();
            using (ProfileAdmissionToken admission = await lease.Admission.EnterExclusiveAsync())
            {
                Assert.Equal((2, 0), await catalog.RecordMediaConflictsAdmittedAsync(admission, inventory));
                Assert.Equal((0, 2), await catalog.RecordMediaConflictsAdmittedAsync(admission, inventory)); // replay
            }
            {
                Assert.Equal(ArchiveSql.MediaConflictDecisionOutcome.Applied,
                    await catalog.DecideMediaConflictAsync(first, NativeMediaConflictResolutionStatus.Discarded, at));
                Assert.Equal(ArchiveSql.MediaConflictDecisionOutcome.AlreadyApplied,
                    await catalog.DecideMediaConflictAsync(first, NativeMediaConflictResolutionStatus.Discarded, at));
                // A discarded candidate is a tombstone: it is final and a replay does not reopen it.
                Assert.Equal(ArchiveSql.MediaConflictDecisionOutcome.NotAllowed,
                    await catalog.DecideMediaConflictAsync(first, NativeMediaConflictResolutionStatus.Kept, at));
                using (ProfileAdmissionToken admission = await lease.Admission.EnterExclusiveAsync())
                    Assert.Equal((0, 2), await catalog.RecordMediaConflictsAdmittedAsync(admission, inventory));
                Assert.Equal(ArchiveSql.MediaConflictDecisionOutcome.Applied,
                    await catalog.DecideMediaConflictAsync(second, NativeMediaConflictResolutionStatus.Kept, at));
                Assert.Equal(ArchiveSql.MediaConflictDecisionOutcome.Applied, // Kept may still be promoted.
                    await catalog.DecideMediaConflictAsync(second, NativeMediaConflictResolutionStatus.Promoted, at));
                Assert.Equal(ArchiveSql.MediaConflictDecisionOutcome.Missing,
                    await catalog.DecideMediaConflictAsync(H('9'), NativeMediaConflictResolutionStatus.Kept, at));
            }
        }

        await using ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile);
        (int total, List<PersistedMediaConflict> items) = await reader.ReadMediaConflictsAsync(0, 100);
        Assert.Equal(2, total);
        Assert.Equal(inventory.Entries.Select(e => e.Identity.IdentitySha256).OrderBy(x => x, StringComparer.Ordinal),
            items.Select(i => i.Entry.Identity.IdentitySha256));
        Assert.Equal(NativeMediaConflictResolutionStatus.Discarded, items.Single(i => i.Entry.Identity.IdentitySha256 == first).Entry.Resolution);
        PersistedMediaConflict promoted = items.Single(i => i.Entry.Identity.IdentitySha256 == second);
        Assert.Equal(NativeMediaConflictResolutionStatus.Promoted, promoted.Entry.Resolution);
        Assert.Equal(inventory.Provenance, promoted.Provenance);
        Assert.NotNull(promoted.DecidedAtUtc);
        Assert.Equal(20, promoted.Entry.Candidate.ByteLength);
    }

    [Fact]
    public void SlotDigestIgnoresTheProviderTupleWhileTheLegacyDigestDoesNot()
    {
        MediaOccurrence a = new(Resource, "15", 0, "att-v2:" + H('1'));
        MediaOccurrence b = a with { OriginalRevisionKey = "att-v2:" + H('2') };
        Assert.Equal(NativeMediaConflictOccurrence.V2(a), NativeMediaConflictOccurrence.V2(b));
        Assert.NotEqual(NativeMediaConflictOccurrence.V1(a), NativeMediaConflictOccurrence.V1(b));
        Assert.NotEqual(NativeMediaConflictOccurrence.V2(a), NativeMediaConflictOccurrence.V2(a with { Position = 1 }));
        Assert.NotEqual(NativeMediaConflictOccurrence.V2(a), NativeMediaConflictOccurrence.V1(a));
    }

    [Fact]
    public async Task ADecidedLegacyRowSuppressesTheSlotLevelEntryAndSameSlotEntriesCollapse()
    {
        using Workspace files = new();
        MediaOccurrence a = new(Resource, "15", 0, "att-v2:" + H('1'));
        MediaOccurrence b = a with { OriginalRevisionKey = "att-v2:" + H('2') };
        NativeMediaConflictEntry Entry(string occurrence, string? legacy) => new(
            new NativeMediaConflictIdentity(Resource, occurrence, H('d'), H('e')),
            new NativeMediaConflictVariantProjection(H('d'), NativeMediaConflictVariantStatus.Active, 10),
            new NativeMediaConflictVariantProjection(H('e'), NativeMediaConflictVariantStatus.Inactive, 20))
        { LegacyIdentitySha256 = legacy };
        string Legacy(MediaOccurrence o) => new NativeMediaConflictIdentity(Resource, NativeMediaConflictOccurrence.V1(o), H('d'), H('e')).IdentitySha256;
        NativeMediaConflictEntry oldRow = Entry(NativeMediaConflictOccurrence.V1(a), null);
        NativeMediaConflictEntry newA = Entry(NativeMediaConflictOccurrence.V2(a), Legacy(a));
        NativeMediaConflictEntry newB = Entry(NativeMediaConflictOccurrence.V2(b), Legacy(b));
        Assert.Equal(newA.Identity.IdentitySha256, newB.Identity.IdentitySha256);
        NativeMediaConflictProvenance provenance = new(Guid.NewGuid(), H('a'), H('b'));
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        lease.Admission.CompleteStartup();
        using (ProfileAdmissionToken admission = await lease.Admission.EnterExclusiveAsync())
            Assert.Equal((1, 0), await catalog.RecordMediaConflictsAdmittedAsync(admission, new(provenance, [oldRow])));
        await catalog.DecideMediaConflictAsync(oldRow.Identity.IdentitySha256, NativeMediaConflictResolutionStatus.Discarded,
            new(2026, 11, 11, 0, 0, 0, TimeSpan.Zero));
        // The same conflict re-seen under the V2 digest is already known and stays discarded.
        using (ProfileAdmissionToken admission = await lease.Admission.EnterExclusiveAsync())
            Assert.Equal((0, 1), await catalog.RecordMediaConflictsAdmittedAsync(admission, new(provenance, [newA])));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile);
        Assert.Equal(1, (await reader.ReadMediaConflictsAsync(0, 10)).Total);
    }

    [Fact]
    public async Task Version11CatalogMigratesToSchema12WithoutLosingRows()
    {
        using Workspace files = new();
        using (ProfileLease lease = ProfileLease.Acquire(files.Profile))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        }
        string db = Path.Combine(files.Profile, "archive.db");
        using (SqliteConnection raw = new($"Data Source={db};Pooling=False"))
        {
            raw.Open();
            using SqliteCommand downgrade = raw.CreateCommand();
            downgrade.CommandText = "DROP TABLE media_conflict; PRAGMA user_version=11;";
            downgrade.ExecuteNonQuery();
        }
        using (ProfileLease lease = ProfileLease.Acquire(files.Profile))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        }
        using SqliteConnection check = new($"Data Source={db};Mode=ReadOnly;Pooling=False");
        check.Open();
        Assert.Equal(12, ArchiveSql.GetPragma(check, "user_version"));
        ArchiveSql.ValidateSchema(check);
    }

    [Fact]
    public void LedgerRejectsAnIdenticalActiveAndCandidateDigest()
    {
        Assert.Throws<ArgumentException>(() => new NativeMediaConflictIdentity(Resource, H('c'), H('d'), H('d')));
    }
}
