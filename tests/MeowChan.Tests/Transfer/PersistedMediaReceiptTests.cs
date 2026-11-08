using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MeowChan.Archive;
using MeowChan.Transfer;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests.Transfer;

// Synthetic SQL evidence in the real schema10, not public success, recovery or Apply.
// Only the explicitly named corruption tests bypass CHECK constraints, temporarily.
public sealed class PersistedMediaReceiptTests
{
    private static readonly string OtherRequest = new('9', 64);
    private static readonly Guid OtherOperation = Guid.Parse("ffffffff-ffff-4fff-8fff-ffffffffffff");
    private static readonly string[] ImportCounters =
    ["post_count", "attachment_count", "evidence_additions", "derived_descriptor_additions",
        "contained_derived_additions", "original_binding_count"];
    private static readonly string[] MergeCounters =
    ["new_posts", "updated_posts", "new_attachments", "new_bindings", "conflicts",
        "evidence_additions", "derived_descriptor_additions", "contained_derived_additions"];

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public void HistoricalOrphanRetainsExactVersionAndEveryCounterWithoutCreatingAnOperation(bool merge, int version)
    {
        using PersistedMediaFixture f = new();
        var row = PersistedMediaFixture.Row(merge, version);
        var receiptRow = PersistedMediaFixture.Receipt(merge, row);
        // Orphans have no content/selection against which counts could be interpreted.
        // Distinct values detect swapped projections; import v2 has zero originals.
        int[] values = merge
            ? version == 1 ? [2, 3, 5, 7, 11, 0, 0, 0] : [2, 3, 5, 7, 11, 13, 17, 16]
            : version == 1 ? [2, 3, 0, 0, 0, 0] : [2, 0, 5, 7, 3, 0];
        string[] names = merge ? MergeCounters : ImportCounters;
        for (int i = 0; i < names.Length; i++) receiptRow[names[i]] = values[i];
        f.InsertReceipt(merge, receiptRow); // Deliberately never f.Insert(operation).
        long changes = ScalarLong(f, "SELECT total_changes()");
        Assert.Equal(0L, ScalarLong(f, $"SELECT count(*) FROM {OperationTable(merge)}"));

        object ownedRead = ReadReceipt(f, merge, version)!;
        AssertExpectedReceipt(ownedRead, merge, version, values);
        using (SqliteTransaction transaction = f.Connection.BeginTransaction(deferred: true))
        {
            object? callerRead = ReadReceipt(f, merge, version, transaction: transaction);
            Assert.Equal(ownedRead, callerRead);
            using SqliteCommand probe = f.Connection.CreateCommand();
            probe.Transaction = transaction;
            probe.CommandText = "SELECT 1";
            Assert.Equal(1L, probe.ExecuteScalar()); // Reader left caller ownership intact.
            transaction.Rollback();
        }
        Assert.Equal(ownedRead, ReadReceipt(f, merge, version));
        Assert.Equal(changes, ScalarLong(f, "SELECT total_changes()"));
        Assert.Equal(0L, ScalarLong(f, $"SELECT count(*) FROM {OperationTable(merge)}"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void V2OrphanAcceptsEveryCounterAtTheInt32UpperBound(bool merge)
    {
        using PersistedMediaFixture f = new();
        var receipt = PersistedMediaFixture.Receipt(merge, PersistedMediaFixture.Row(merge));
        string[] names = merge ? MergeCounters : ImportCounters;
        foreach (string name in names) receipt[name] = int.MaxValue;
        f.InsertReceipt(merge, receipt);
        AssertExpectedReceipt(ReadReceipt(f, merge, 2)!, merge, 2,
            Enumerable.Repeat(int.MaxValue, names.Length).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void V2OrphanMayHaveAllZeroCountersWithoutAnOperation(bool merge)
    {
        using PersistedMediaFixture f = new();
        var receipt = PersistedMediaFixture.Receipt(merge, PersistedMediaFixture.Row(merge));
        string[] names = merge ? MergeCounters : ImportCounters;
        foreach (string name in names) receipt[name] = 0L;
        f.InsertReceipt(merge, receipt);
        AssertExpectedReceipt(ReadReceipt(f, merge, 2)!, merge, 2, new int[names.Length]);
        Assert.Equal(0L, ScalarLong(f, $"SELECT count(*) FROM {OperationTable(merge)}"));
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public void MissingReceiptIsNullButExistingReceiptRejectsDifferentRequestOrRequestedVersion(bool merge, int version)
    {
        using PersistedMediaFixture f = new();
        Assert.Null(ReadReceipt(f, merge, version));
        f.InsertReceipt(merge, PersistedMediaFixture.Receipt(merge, PersistedMediaFixture.Row(merge, version)));
        Assert.NotEqual(PersistedMediaFixture.Request, OtherRequest);
        long changes = ScalarLong(f, "SELECT total_changes()");
        Assert.Throws<InvalidDataException>(() => ReadReceipt(f, merge, version, request: OtherRequest));
        Assert.Throws<InvalidDataException>(() => ReadReceipt(f, merge, version == 1 ? 2 : 1));
        Assert.NotNull(ReadReceipt(f, merge, version));
        Assert.Equal(changes, ScalarLong(f, "SELECT total_changes()"));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, -1)]
    [InlineData(false, 3)]
    [InlineData(true, 0)]
    [InlineData(true, -1)]
    [InlineData(true, 3)]
    public void RequestedVersionMustBeExactlyOneOrTwoEvenWhenNoReceiptExists(bool merge, int version)
    {
        using PersistedMediaFixture f = new();
        Assert.Throws<ArgumentOutOfRangeException>(() => ReadReceipt(f, merge, version));
    }

    public static IEnumerable<object[]> CounterMismatchCases()
    {
        foreach (bool merge in new[] { false, true })
            foreach (int version in new[] { 1, 2 })
                foreach (string counter in merge ? MergeCounters : ImportCounters)
                    yield return [merge, version, counter];
    }

    [Theory]
    [MemberData(nameof(CounterMismatchCases))]
    public void EachIndividualCounterMustMatchExactly(bool merge, int version, string counter)
    {
        using PersistedMediaFixture f = new();
        var row = PersistedMediaFixture.Row(merge, version, state: "Committed");
        f.Insert(merge, row);
        f.InsertReceipt(merge, PersistedMediaFixture.Receipt(merge, row));
        if (merge)
        {
            NativeMediaPersistedMergeOperation operation = ReadMergeOperation(f);
            NativeMediaPersistedMergeReceipt receipt = Assert.IsType<NativeMediaPersistedMergeReceipt>(ReadReceipt(f, true, version));
            Assert.True(ArchiveSql.PersistedMediaMergeReceiptMatches(operation, receipt));
            var changed = receipt with { Counts = Increment(receipt.Counts, counter) };
            Assert.False(ArchiveSql.PersistedMediaMergeReceiptMatches(operation, changed));
        }
        else
        {
            NativeMediaPersistedImportOperation operation = ReadImportOperation(f);
            NativeMediaPersistedImportReceipt receipt = Assert.IsType<NativeMediaPersistedImportReceipt>(ReadReceipt(f, false, version));
            Assert.True(ArchiveSql.PersistedMediaImportReceiptMatches(operation, receipt));
            var changed = receipt with { Counts = Increment(receipt.Counts, counter) };
            Assert.False(ArchiveSql.PersistedMediaImportReceiptMatches(operation, changed));
        }
        // Mutated records only exercise the pure comparator; invalid v1 additions
        // are not inserted as though they were legitimate persisted v1 evidence.
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public void PureMatchRequiresEveryIdentityComponentAndCommittedState(bool merge, int version)
    {
        using PersistedMediaFixture f = new();
        var row = PersistedMediaFixture.Row(merge, version, state: "Committed");
        f.Insert(merge, row);
        f.InsertReceipt(merge, PersistedMediaFixture.Receipt(merge, row));
        Assert.NotEqual(PersistedMediaFixture.OperationId, OtherOperation);
        Assert.NotEqual(PersistedMediaFixture.Request, OtherRequest);
        if (merge)
        {
            NativeMediaPersistedMergeOperation operation = ReadMergeOperation(f);
            NativeMediaPersistedMergeReceipt receipt = Assert.IsType<NativeMediaPersistedMergeReceipt>(ReadReceipt(f, true, version));
            Assert.True(ArchiveSql.PersistedMediaMergeReceiptMatches(operation, receipt));
            foreach (NativeMediaReceiptIdentity identity in DifferentIdentities(receipt.Identity))
                Assert.False(ArchiveSql.PersistedMediaMergeReceiptMatches(operation, receipt with { Identity = identity }));
            foreach (NativeMediaImportOperationState state in NonCommittedStates())
                Assert.False(ArchiveSql.PersistedMediaMergeReceiptMatches(operation with { State = state }, receipt));
        }
        else
        {
            NativeMediaPersistedImportOperation operation = ReadImportOperation(f);
            NativeMediaPersistedImportReceipt receipt = Assert.IsType<NativeMediaPersistedImportReceipt>(ReadReceipt(f, false, version));
            Assert.True(ArchiveSql.PersistedMediaImportReceiptMatches(operation, receipt));
            foreach (NativeMediaReceiptIdentity identity in DifferentIdentities(receipt.Identity))
                Assert.False(ArchiveSql.PersistedMediaImportReceiptMatches(operation, receipt with { Identity = identity }));
            foreach (NativeMediaImportOperationState state in NonCommittedStates())
                Assert.False(ArchiveSql.PersistedMediaImportReceiptMatches(operation with { State = state }, receipt));
        }
    }

    public static IEnumerable<object[]> CorruptStoredValues()
    {
        foreach (bool merge in new[] { false, true })
        {
            foreach (int version in new[] { 1, 2 })
            {
                foreach (string column in (merge ? MergeCounters : ImportCounters).Append("plan_version"))
                    foreach ((string expression, string type) in new[]
                    {
                        ("-1", "integer"), ("2147483648", "integer"),
                        ("1.5", "real"), ("'not-an-integer'", "text"), ("X'31'", "blob")
                    })
                        yield return [merge, version, column, expression, type];
                foreach (string expression in new[] { "0", "3" })
                    yield return [merge, version, "plan_version", expression, "integer"];
                foreach ((string expression, string type) in new[]
                {
                    ("'short'", "text"), ("'" + new string('a', 65) + "'", "text"),
                    ("'" + new string('A', 64) + "'", "text"),
                    ("'" + new string('g', 64) + "'", "text"),
                    ("CAST('" + PersistedMediaFixture.Request + "' AS BLOB)", "blob")
                })
                    yield return [merge, version, "request_sha256", expression, type];
            }
            foreach (string counter in new[] { "evidence_additions", "derived_descriptor_additions", "contained_derived_additions" })
                yield return [merge, 1, counter, "1", "integer"];
            yield return [merge, 2, "contained_derived_additions", "derived_descriptor_additions + 1", "integer"];
        }
        yield return [false, 1, "original_binding_count", "1", "integer"];
        yield return [false, 1, "attachment_count", "0", "integer"];
    }

    [Theory]
    [MemberData(nameof(CorruptStoredValues))]
    public void CorruptStoredIdentityVersionTypeOrCounterRejectsWithoutRepair(
        bool merge, int version, string column, string expression, string expectedStorageType)
    {
        using PersistedMediaFixture f = new();
        f.InsertReceipt(merge, PersistedMediaFixture.Receipt(merge, PersistedMediaFixture.Row(merge, version)));
        // This bypass is solely for constructing otherwise-unrepresentable corruption.
        // Restore enforcement BEFORE invoking the production reader.
        f.Execute("PRAGMA ignore_check_constraints=ON");
        try { f.Execute($"UPDATE {ReceiptTable(merge)} SET {column}={expression}"); }
        finally { f.Execute("PRAGMA ignore_check_constraints=OFF"); }
        Assert.Equal(0L, ScalarLong(f, "PRAGMA ignore_check_constraints"));
        Assert.Equal(expectedStorageType, Scalar(f, $"SELECT typeof({column}) FROM {ReceiptTable(merge)}"));
        object? storedBefore = Scalar(f, $"SELECT {column} FROM {ReceiptTable(merge)}");
        long changes = ScalarLong(f, "SELECT total_changes()");
        Assert.Throws<InvalidDataException>(() => ReadReceipt(f, merge, version));
        Assert.Equal(changes, ScalarLong(f, "SELECT total_changes()"));
        AssertStoredValueEqual(storedBefore, Scalar(f, $"SELECT {column} FROM {ReceiptTable(merge)}"));
        Assert.Equal(1L, ScalarLong(f, $"SELECT count(*) FROM {ReceiptTable(merge)}"));
        Assert.Equal(0L, ScalarLong(f, $"SELECT count(*) FROM {OperationTable(merge)}"));
    }

    private static object? ReadReceipt(PersistedMediaFixture f, bool merge, int version, string? request = null,
        SqliteTransaction? transaction = null)
        => merge
            ? ArchiveSql.ReadPersistedMediaMergeReceipt(f.Connection, transaction, PersistedMediaFixture.OperationId,
                request ?? PersistedMediaFixture.Request, version, CancellationToken.None)
            : ArchiveSql.ReadPersistedMediaImportReceipt(f.Connection, transaction, PersistedMediaFixture.OperationId,
                request ?? PersistedMediaFixture.Request, version, CancellationToken.None);

    private static NativeMediaPersistedImportOperation ReadImportOperation(PersistedMediaFixture f)
        => Assert.IsType<NativeMediaPersistedImportOperation>(ArchiveSql.ReadPersistedMediaImportOperation(
            f.Connection, null, PersistedMediaFixture.OperationId, PersistedMediaFixture.Request, CancellationToken.None));

    private static NativeMediaPersistedMergeOperation ReadMergeOperation(PersistedMediaFixture f)
        => Assert.IsType<NativeMediaPersistedMergeOperation>(ArchiveSql.ReadPersistedMediaMergeOperation(
            f.Connection, null, PersistedMediaFixture.OperationId, PersistedMediaFixture.Request, CancellationToken.None));

    private static void AssertExpectedReceipt(object receipt, bool merge, int version, int[] counts)
    {
        NativeMediaReceiptIdentity identity = new(PersistedMediaFixture.OperationId, PersistedMediaFixture.Request, version);
        if (merge)
            Assert.Equal(new NativeMediaPersistedMergeReceipt(identity,
                new(counts[0], counts[1], counts[2], counts[3], counts[4], counts[5], counts[6], counts[7])),
                Assert.IsType<NativeMediaPersistedMergeReceipt>(receipt));
        else
            Assert.Equal(new NativeMediaPersistedImportReceipt(identity,
                new(counts[0], counts[1], counts[2], counts[3], counts[4], counts[5])),
                Assert.IsType<NativeMediaPersistedImportReceipt>(receipt));
    }

    private static NativeMediaImportCounts Increment(NativeMediaImportCounts c, string name) => name switch
    {
        "post_count" => c with { PostCount = c.PostCount + 1 },
        "attachment_count" => c with { AttachmentCount = c.AttachmentCount + 1 },
        "evidence_additions" => c with { EvidenceAdditions = c.EvidenceAdditions + 1 },
        "derived_descriptor_additions" => c with { DerivedDescriptorAdditions = c.DerivedDescriptorAdditions + 1 },
        "contained_derived_additions" => c with { ContainedDerivedAdditions = c.ContainedDerivedAdditions + 1 },
        "original_binding_count" => c with { OriginalBindingCount = c.OriginalBindingCount + 1 },
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    private static NativeMediaMergeCounts Increment(NativeMediaMergeCounts c, string name) => name switch
    {
        "new_posts" => c with { NewPosts = c.NewPosts + 1 },
        "updated_posts" => c with { UpdatedPosts = c.UpdatedPosts + 1 },
        "new_attachments" => c with { NewAttachments = c.NewAttachments + 1 },
        "new_bindings" => c with { NewBindings = c.NewBindings + 1 },
        "conflicts" => c with { Conflicts = c.Conflicts + 1 },
        "evidence_additions" => c with { EvidenceAdditions = c.EvidenceAdditions + 1 },
        "derived_descriptor_additions" => c with { DerivedDescriptorAdditions = c.DerivedDescriptorAdditions + 1 },
        "contained_derived_additions" => c with { ContainedDerivedAdditions = c.ContainedDerivedAdditions + 1 },
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    private static IEnumerable<NativeMediaReceiptIdentity> DifferentIdentities(NativeMediaReceiptIdentity identity)
    {
        yield return identity with { OperationId = OtherOperation };
        yield return identity with { RequestSha256 = OtherRequest };
        yield return identity with { PlanVersion = identity.PlanVersion == 1 ? 2 : 1 };
    }

    private static IEnumerable<NativeMediaImportOperationState> NonCommittedStates()
    {
        yield return NativeMediaImportOperationState.Preparing;
        yield return NativeMediaImportOperationState.Prepared;
        yield return (NativeMediaImportOperationState)int.MaxValue;
    }

    private static string ReceiptTable(bool merge) => merge ? "archive_media_merge_receipts" : "archive_media_import_receipts";
    private static string OperationTable(bool merge) => merge ? "archive_media_merge_operations" : "archive_media_import_operations";

    private static object? Scalar(PersistedMediaFixture f, string sql)
    {
        using SqliteCommand command = f.Connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static long ScalarLong(PersistedMediaFixture f, string sql) => Assert.IsType<long>(Scalar(f, sql));

    private static void AssertStoredValueEqual(object? expected, object? actual)
    {
        if (expected is byte[] expectedBytes && actual is byte[] actualBytes)
        {
            Assert.Equal(expectedBytes, actualBytes);
            return;
        }
        Assert.Equal(expected, actual);
    }
}
