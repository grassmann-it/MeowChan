using System.Security.Cryptography;
using MeowChan.Gallery;
using MeowChan.Import;
using MeowChan.Persistence;
using Xunit.Abstractions;

namespace MeowChan.Tests.PersistenceCorruption;

/// <summary>
/// local-imports.json (contract manual-import-v1): a damaged or newer primary and a newer backup are never overwritten and block imports;
/// a damaged backup may be replaced by the next valid write.
/// </summary>
internal sealed class ManualImportCatalogAdapter : StoreAdapter
{
    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };

    internal override string Name => "local-imports";
    internal override string PrimaryName => ManualImportCatalogStore.FileName;
    internal override string VersionProperty => "Version";
    internal override long ReadCeilingBytes => ManualImportCatalogStore.MaximumDocumentBytes;

    internal override bool IsAllowedFailure(Exception error) => base.IsAllowedFailure(error) || error is ManualImportException;

    // Documented for manual-import-v1: only a newer backup is kept; a damaged one may be replaced by the next valid write.
    internal override bool BackupBlocksWrite(string variant, bool stillParses) => variant == "future-version";

    internal override void CheckReadError(string variant, Exception error)
    {
        if (error is not ManualImportException import) return;
        Assert.Equal(variant == "future-version" ? ManualImportError.CatalogNewer : ManualImportError.CatalogDamaged, import.Error);
    }

    internal static ManualImportBatch Batch(string name) => new(Guid.NewGuid(), name + " " + Secret, DateTimeOffset.Parse("2026-11-13T10:00:00Z"),
        DateTimeOffset.Parse("2026-11-13T10:01:00Z"), ManualImportBatchState.Completed,
        Path.Combine(Path.GetTempPath(), "dl", "meowchan-import", "x"), "Urlaub", false, false, new[] { "cat" }, false,
        new[] { new ManualImportItem(Guid.NewGuid(), ManualImportItemState.Committed, Convert.ToHexString(SHA256.HashData(Png)).ToLowerInvariant(),
            Png.Length, "IMG 1.png", SavedMediaKind.Image, ".png", new string('a', 64)) });

    internal override void Seed(string directory, ProfileLease lease)
    {
        ManualImportCatalogStore store = new(directory, lease);
        using ProfileAdmissionToken admission = lease.Admission.EnterStoreWriter();
        ManualImportCatalogSnapshot first = store.PutBatchAdmitted(Batch("one"), 0, admission);
        store.PutBatchAdmitted(Batch("two"), first.Revision, admission);
    }

    internal override ReadResult Read(string directory, ProfileLease lease) => ReadResult.Guard(() =>
    {
        ManualImportCatalogSnapshot snapshot = new ManualImportCatalogStore(directory, lease).Read();
        return snapshot.Revision + ":" + string.Join("|", snapshot.Batches.Select(batch => batch.Name)) + "#" + snapshot.ItemCount;
    });

    internal override void Mutate(string directory, ProfileLease lease)
    {
        ManualImportCatalogStore store = new(directory, lease);
        long revision = 0;
        try { revision = store.Read().Revision; } catch (Exception) { /* A damaged catalog: the write below must refuse by itself. */ }
        using ProfileAdmissionToken admission = lease.Admission.EnterStoreWriter();
        store.PutBatchAdmitted(Batch("three"), revision, admission);
    }
}

public sealed class ManualImportCatalogCorruptionTests(ITestOutputHelper log) : BackedStoreCorruptionTests(log)
{
    internal override StoreAdapter Adapter { get; } = new ManualImportCatalogAdapter();
}
