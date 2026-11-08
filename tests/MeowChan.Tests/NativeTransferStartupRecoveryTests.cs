using System.Security.Cryptography;
using MeowChan.Archive;
using MeowChan.Persistence;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests;

// Minimal synthetic SQL journal fixtures. These tests do not claim candidate-file
// verification or recovery; the startup guard must never open their recorded paths.
public sealed class NativeTransferStartupRecoveryTests
{
    private const string Operation = "01234567-89ab-cdef-0123-456789abcdef";
    private static readonly string Hash = new('a', 64);

    [Theory]
    [InlineData(false, "Preparing", false)] [InlineData(true, "Preparing", false)]
    [InlineData(false, "Prepared", false)] [InlineData(true, "Prepared", false)]
    [InlineData(false, "Committed", false)] [InlineData(true, "Committed", false)]
    [InlineData(false, "Preparing", true)] [InlineData(true, "Preparing", true)]
    [InlineData(false, "Prepared", true)] [InlineData(true, "Prepared", true)]
    [InlineData(false, "Committed", true)] [InlineData(true, "Committed", true)]
    public async Task OnlyCommittedWithOwnConsistentReceiptAllowsStartup(bool merge, string state, bool receipt)
    {
        using var directory = new TempDirectory();
        using var lease = ProfileLease.Acquire(directory.Path);
        await using var catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        await Seed(catalog, merge, state, receipt);
        byte[] before = SHA256.HashData(File.ReadAllBytes(catalog.DatabasePath));
        string[] files = Directory.GetFiles(directory.Path, "*", SearchOption.AllDirectories).Order().ToArray();
        if (state == "Committed" && receipt)
            await catalog.EnsureNativeMediaRecoveryClearAsync();
        else
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => catalog.EnsureNativeMediaRecoveryClearAsync());
            Assert.Equal("Native media transfer reconciliation is required.", error.Message);
            Assert.Null(error.InnerException);
        }
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(catalog.DatabasePath)));
        Assert.Equal(files, Directory.GetFiles(directory.Path, "*", SearchOption.AllDirectories).Order().ToArray());
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task MismatchedReceiptIsNotACommittedOutcome(bool merge, bool wrongCounts)
    {
        using var directory = new TempDirectory();
        using var lease = ProfileLease.Acquire(directory.Path);
        await using var catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        await Seed(catalog, merge, "Committed", true);
        await catalog.WriteAsync((c, tx) =>
        {
            string table = merge ? "archive_media_merge_receipts" : "archive_media_import_receipts";
            string change = wrongCounts ? (merge ? "new_posts=99" : "post_count=99") : "request_sha256='" + new string('b', 64) + "'";
            using var command = ArchiveSql.Command(c, tx, $"UPDATE {table} SET {change}");
            return command.ExecuteNonQuery();
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.EnsureNativeMediaRecoveryClearAsync());
    }

    [Fact]
    public async Task EmptyAndHistoricalReceiptsAfterOperationCleanupAreSafeButMissingTableIsNotEmpty()
    {
        using var directory = new TempDirectory();
        using var lease = ProfileLease.Acquire(directory.Path);
        await using var catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        await catalog.EnsureNativeMediaRecoveryClearAsync();
        await Seed(catalog, false, "Committed", true);
        await catalog.WriteAsync((c, tx) =>
        {
            using var command = ArchiveSql.Command(c, tx, "DELETE FROM archive_media_import_operations");
            return command.ExecuteNonQuery();
        });
        await catalog.EnsureNativeMediaRecoveryClearAsync();
        using (var c = ArchiveSql.OpenConnection(catalog.DatabasePath, SqliteOpenMode.ReadWrite))
        using (var command = ArchiveSql.Command(c, null, "DROP TABLE archive_media_merge_receipts"))
            command.ExecuteNonQuery();
        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.EnsureNativeMediaRecoveryClearAsync());
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task PreCancelledGuardPropagatesCancellationWithoutChangingEmptyOrBlockedJournal(bool blocked, bool merge)
    {
        using var directory = new TempDirectory();
        using var lease = ProfileLease.Acquire(directory.Path);
        await using var catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        if (blocked) await Seed(catalog, merge, "Preparing", false);
        byte[] before = SHA256.HashData(File.ReadAllBytes(catalog.DatabasePath));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => catalog.EnsureNativeMediaRecoveryClearAsync(cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(catalog.DatabasePath)));
        // A cancelled check neither clears a real blocker nor manufactures one.
        if (blocked) await AssertBlocked(catalog);
        else await catalog.EnsureNativeMediaRecoveryClearAsync();
    }

    [Theory]
    [InlineData(false, "Preparing")] [InlineData(true, "Preparing")]
    [InlineData(false, "Prepared")] [InlineData(true, "Prepared")]
    [InlineData(false, "Committed")] [InlineData(true, "Committed")]
    public async Task HealthyOtherFamilyCannotHideAnUnresolvedOperation(bool blockedMerge, string state)
    {
        using var directory = new TempDirectory();
        using var lease = ProfileLease.Acquire(directory.Path);
        await using var catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        // Same identity deliberately exists in both families. Its healthy opposite-family
        // receipt must never stand in for the blocked family's missing receipt.
        await Seed(catalog, !blockedMerge, "Committed", true);
        await catalog.EnsureNativeMediaRecoveryClearAsync();
        await Seed(catalog, blockedMerge, state, false);
        byte[] before = SHA256.HashData(File.ReadAllBytes(catalog.DatabasePath));
        await AssertBlocked(catalog);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(catalog.DatabasePath)));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task OrphanOppositeFamilyReceiptCannotCompleteCommittedOperation(bool merge)
    {
        using var directory = new TempDirectory();
        using var lease = ProfileLease.Acquire(directory.Path);
        await using var catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        await Seed(catalog, !merge, "Committed", true);
        await DeleteOperation(catalog, !merge);
        await catalog.EnsureNativeMediaRecoveryClearAsync(); // Opposite receipt is historical only.
        await Seed(catalog, merge, "Committed", false);
        await AssertBlocked(catalog);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task CaseOnlyReceiptIdentityMismatchBlocksUntilExactIdentityIsRestored(bool merge, bool operationId)
    {
        using var directory = new TempDirectory();
        using var lease = ProfileLease.Acquire(directory.Path);
        await using var catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        await Seed(catalog, merge, "Committed", true);
        await catalog.EnsureNativeMediaRecoveryClearAsync();
        string field = operationId ? "operation_id" : "request_sha256";
        string exact = operationId ? Operation : Hash;
        string changed = exact.ToUpperInvariant();
        Assert.NotEqual(exact, changed);
        await ChangeReceipt(catalog, merge, field, changed);
        byte[] before = SHA256.HashData(File.ReadAllBytes(catalog.DatabasePath));
        await AssertBlocked(catalog);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(catalog.DatabasePath)));
        await ChangeReceipt(catalog, merge, field, exact);
        await catalog.EnsureNativeMediaRecoveryClearAsync();
    }

    [Theory]
    [InlineData(false, "attachment_count", 1)]
    [InlineData(true, "updated_posts", 0)]
    [InlineData(true, "new_attachments", 1)]
    [InlineData(true, "new_bindings", 0)]
    [InlineData(true, "conflicts", 0)]
    public async Task EachAdditionalReceiptCountMustMatchItsOwnOperation(bool merge, string field, int original)
    {
        using var directory = new TempDirectory();
        using var lease = ProfileLease.Acquire(directory.Path);
        await using var catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        await Seed(catalog, merge, "Committed", true);
        await catalog.EnsureNativeMediaRecoveryClearAsync();
        await ChangeReceipt(catalog, merge, field, 99);
        byte[] before = SHA256.HashData(File.ReadAllBytes(catalog.DatabasePath));
        await AssertBlocked(catalog);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(catalog.DatabasePath)));
        // Restoring just this field proves that the selected mismatch was causal.
        await ChangeReceipt(catalog, merge, field, original);
        await catalog.EnsureNativeMediaRecoveryClearAsync();
    }

    [Fact]
    public async Task HistoricalMergeReceiptAfterOperationCleanupDoesNotBlockOrMutateStartup()
    {
        using var directory = new TempDirectory();
        using var lease = ProfileLease.Acquire(directory.Path);
        await using var catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        await Seed(catalog, true, "Committed", true);
        await DeleteOperation(catalog, true);
        byte[] before = SHA256.HashData(File.ReadAllBytes(catalog.DatabasePath));
        await catalog.EnsureNativeMediaRecoveryClearAsync();
        await catalog.EnsureNativeMediaRecoveryClearAsync();
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(catalog.DatabasePath)));
        await catalog.ReadAsync(c =>
        {
            using var command = ArchiveSql.Command(c, null, """
                SELECT (SELECT count(*) FROM archive_media_merge_operations),
                       (SELECT count(*) FROM archive_media_merge_receipts WHERE operation_id=$op AND request_sha256=$hash
                           AND new_posts=1 AND updated_posts=0 AND new_attachments=1 AND new_bindings=0 AND conflicts=0
                           AND plan_version=1 AND evidence_additions=0 AND derived_descriptor_additions=0
                           AND contained_derived_additions=0)
                """);
            command.Parameters.AddWithValue("$op", Operation);
            command.Parameters.AddWithValue("$hash", Hash);
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(0L, reader.GetInt64(0));
            Assert.Equal(1L, reader.GetInt64(1));
            return true;
        });
    }

    private static async Task AssertBlocked(ArchiveCatalog catalog)
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => catalog.EnsureNativeMediaRecoveryClearAsync());
        Assert.Equal("Native media transfer reconciliation is required.", error.Message);
        Assert.Null(error.InnerException);
    }

    private static async Task DeleteOperation(ArchiveCatalog catalog, bool merge)
    {
        int deleted = await catalog.WriteAsync((c, tx) =>
        {
            string table = merge ? "archive_media_merge_operations" : "archive_media_import_operations";
            using var command = ArchiveSql.Command(c, tx, $"DELETE FROM {table} WHERE operation_id=$op");
            command.Parameters.AddWithValue("$op", Operation);
            return command.ExecuteNonQuery();
        });
        Assert.Equal(1, deleted);
    }

    private static async Task ChangeReceipt(ArchiveCatalog catalog, bool merge, string field, object value)
    {
        // Column names come only from the fixed theory data/identity selectors above.
        Assert.Contains(field, merge
            ? new[] { "operation_id", "request_sha256", "updated_posts", "new_attachments", "new_bindings", "conflicts" }
            : new[] { "operation_id", "request_sha256", "attachment_count" });
        int changed = await catalog.WriteAsync((c, tx) =>
        {
            string table = merge ? "archive_media_merge_receipts" : "archive_media_import_receipts";
            using var command = ArchiveSql.Command(c, tx, $"UPDATE {table} SET {field}=$value");
            command.Parameters.AddWithValue("$value", value);
            return command.ExecuteNonQuery();
        });
        Assert.Equal(1, changed);
    }

    private static Task<int> Seed(ArchiveCatalog catalog, bool merge, string state, bool receipt) =>
        catalog.WriteAsync((c, tx) =>
        {
            string table = merge ? "archive_media_merge" : "archive_media_import";
            string columns = merge
                ? "roots_json,full_keys_json,safe_keys_json,catalog_bytes,full_directory,full_manifest_generation,full_bindings_sha256,new_posts,updated_posts,new_attachments,new_bindings,conflicts"
                : "revision_keys_json,candidate_directory,manifest_generation,bindings_sha256,post_count,attachment_count";
            string values = merge ? "'[]','[]','[]',X'7B7D',$dir,$generation,$generation,1,0,1,0,0" : "'[]',$dir,$generation,$generation,1,1";
            using var insert = ArchiveSql.Command(c, tx, $"""
                INSERT INTO {table}_operations (operation_id,request_sha256,package_fingerprint,catalog_instance,
                    catalog_generation,source,board,thread,operations_parent,imported_at,state,{columns})
                VALUES ($op,$hash,$hash,$op,0,'fixture','b','1','never-open-this-candidate','2026-11-06',$state,{values});
                """);
            insert.Parameters.AddWithValue("$op", Operation); insert.Parameters.AddWithValue("$hash", Hash);
            insert.Parameters.AddWithValue("$state", state);
            insert.Parameters.AddWithValue("$dir", state == "Preparing" ? DBNull.Value : "never-open-this-candidate/child");
            insert.Parameters.AddWithValue("$generation", state == "Preparing" ? DBNull.Value : Hash);
            int result = insert.ExecuteNonQuery();
            if (receipt)
            {
                // Seed only the legacy v1 fields; Schema10 supplies plan_version=1 and zero counters.
                string receiptColumns = merge
                    ? "operation_id,request_sha256,new_posts,updated_posts,new_attachments,new_bindings,conflicts"
                    : "operation_id,request_sha256,post_count,attachment_count";
                using var addReceipt = ArchiveSql.Command(c, tx,
                    $"INSERT INTO {table}_receipts({receiptColumns}) VALUES ($op,$hash," + (merge ? "1,0,1,0,0)" : "1,1)"));
                addReceipt.Parameters.AddWithValue("$op", Operation); addReceipt.Parameters.AddWithValue("$hash", Hash);
                addReceipt.ExecuteNonQuery();
            }
            return result;
        });
}
