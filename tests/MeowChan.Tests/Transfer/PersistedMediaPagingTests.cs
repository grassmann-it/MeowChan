using System.Globalization;
using System.Text;
using MeowChan.Archive;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests.Transfer;

// Real Schema10, real readers, in-memory evidence only. Catalog blobs here are
// deliberately opaque bytes, NEVER a parsed catalog or authority to recover/apply.
// Large fixtures are bounded SQL zeroblob/printf values, not giant member vectors.
public sealed class PersistedMediaPagingTests
{
    private const int MiB = 1024 * 1024;
    private const int JsonLimit = 8 * MiB;
    private const int CatalogLimit = 16 * MiB;
    private const long PageLimit = 32L * MiB;
    private const string BudgetError = "Invalid persisted media transfer evidence.";

    [Theory]
    [InlineData(false, 31)] [InlineData(false, 32)]
    [InlineData(false, 33)] [InlineData(false, 65)]
    [InlineData(true, 31)] [InlineData(true, 32)]
    [InlineData(true, 33)] [InlineData(true, 65)]
    public void Mixed_versions_have_exact_order_and_last_included_cursor_without_gaps(bool merge, int count)
    {
        using var fixture = new PersistedMediaFixture();
        string[] states = ["Preparing", "Prepared", "Committed"];
        // Deliberately opposite insertion order to the required canonical GUID order.
        for (int index = count; index >= 1; index--)
            fixture.Insert(merge, PersistedMediaFixture.Row(merge, Version(index),
                states[index % states.Length], Id(index)));

        var seen = new List<Guid>();
        Guid? after = null;
        int expectedPages = (count + 31) / 32;
        for (int pageIndex = 0; pageIndex < expectedPages; pageIndex++)
        {
            var page = ReadPage(fixture, merge, after);
            int offset = pageIndex * 32;
            int length = Math.Min(32, count - offset);
            Assert.Equal(Enumerable.Range(offset + 1, length).Select(Id).ToArray(), page.Ids);
            Assert.Equal(Enumerable.Range(offset + 1, length).Select(Version).ToArray(), page.Versions);
            Assert.InRange(page.Ids.Length, 1, 32);
            Guid? expectedNext = offset + length < count ? Id(offset + length) : null;
            Assert.Equal(expectedNext, page.Next);
            seen.AddRange(page.Ids);
            after = page.Next;
        }
        Assert.Equal(Enumerable.Range(1, count).Select(Id).ToArray(), seen.ToArray());
        Assert.Equal(count, seen.Distinct().Count());
        var beyondLast = ReadPage(fixture, merge, Id(count));
        Assert.Empty(beyondLast.Ids);
        Assert.Null(beyondLast.Next);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Empty_database_and_nonexistent_cursor_are_bounded_but_empty_guid_is_rejected(bool merge)
    {
        using var fixture = new PersistedMediaFixture();
        var empty = ReadPage(fixture, merge, null);
        Assert.Empty(empty.Ids);
        Assert.Null(empty.Next);
        Assert.Throws<ArgumentException>(() => ReadPage(fixture, merge, Guid.Empty));
        fixture.Insert(merge, PersistedMediaFixture.Row(merge, operation: Id(1)));
        fixture.Insert(merge, PersistedMediaFixture.Row(merge, operation: Id(3)));
        var gap = ReadPage(fixture, merge, Id(2));
        Assert.Equal(new[] { Id(3) }, gap.Ids);
        Assert.Null(gap.Next);
        Assert.Throws<ArgumentException>(() => ReadPage(fixture, merge, Guid.Empty));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Thirty_third_lookahead_is_not_decoded_and_is_not_skipped_by_resume(bool merge)
    {
        using var fixture = new PersistedMediaFixture();
        for (int index = 1; index <= 33; index++)
            fixture.Insert(merge, PersistedMediaFixture.Row(merge, operation: Id(index)));
        // Shape-valid SQL evidence, deliberately not a canonical selection.
        fixture.Execute($"UPDATE {Table(merge)} SET full_selection_json='!' WHERE operation_id='{Id(33):D}'");
        var first = ReadPage(fixture, merge, null);
        Assert.Equal(Enumerable.Range(1, 32).Select(Id).ToArray(), first.Ids);
        Assert.Equal(Id(32), first.Next);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => ReadPage(fixture, merge, first.Next));
        Assert.NotEqual(BudgetError, error.Message); // The deferred row now actually reaches its codec.
    }

    [Theory]
    [InlineData(false, 2, false)]
    [InlineData(true, 1, false)]
    [InlineData(true, 2, false)]
    [InlineData(true, 2, true)]
    public void Exact_combined_boundary_includes_every_document_and_alias_copies_are_both_charged(
        bool merge, int version, bool alias)
    {
        using var fixture = new PersistedMediaFixture();
        var first = PersistedMediaFixture.Row(merge, version, "Prepared", Id(1),
            safe: alias ? PersistedMediaFixture.Full : null);
        var second = PersistedMediaFixture.Row(merge, version, "Prepared", Id(2),
            safe: alias ? PersistedMediaFixture.Full : null);
        if (alias)
        {
            first["safe_directory"] = first["full_directory"];
            second["safe_directory"] = second["full_directory"];
            Assert.Equal(first["full_selection_json"], first["safe_selection_json"]);
            Assert.Equal(second["full_selection_json"], second["safe_selection_json"]);
        }
        fixture.Insert(merge, first);
        fixture.Insert(merge, second);
        // Make each row exactly 16 MiB INCLUDING its JSON. Omitting even the
        // two-byte legacy [] or either aliased selection changes the +1 outcome.
        long firstJson = JsonBytes(first);
        long secondJson = JsonBytes(second);
        SetCatalog(fixture, merge, Id(1), checked(CatalogLimit - (int)firstJson));
        int secondCatalog = checked(CatalogLimit - (int)secondJson);
        SetCatalog(fixture, merge, Id(2), secondCatalog);
        Assert.Equal(PageLimit, StoredDocumentBytes(fixture, merge));
        var exact = ReadPage(fixture, merge, null);
        Assert.Equal(new[] { Id(1), Id(2) }, exact.Ids);
        Assert.Null(exact.Next);

        SetCatalog(fixture, merge, Id(2), secondCatalog + 1);
        Assert.Equal(PageLimit + 1, StoredDocumentBytes(fixture, merge));
        var limited = ReadPage(fixture, merge, null);
        Assert.Equal(new[] { Id(1) }, limited.Ids);
        Assert.Equal(Id(1), limited.Next);
        var resumed = ReadPage(fixture, merge, limited.Next);
        Assert.Equal(new[] { Id(2) }, resumed.Ids);
        Assert.Null(resumed.Next);
        // A byte boundary is paging, not semantic rejection of the next row.
        AssertSinglePresent(fixture, merge, Id(2));
    }

    [Fact]
    public void V1_import_legacy_keys_alone_fill_the_page_budget_without_a_catalog()
    {
        using var fixture = new PersistedMediaFixture();
        for (int index = 1; index <= 5; index++)
            fixture.Insert(false, PersistedMediaFixture.Row(false, 1, operation: Id(index)));
        // JSON whitespace pads actual valid legacy keys; the reader must decode
        // them successfully, rather than the test merely exercising SQL CHECKs.
        fixture.Execute($"UPDATE {Table(false)} SET revision_keys_json=revision_keys_json || "
            + $"printf('%*s',{JsonLimit}-length(CAST(revision_keys_json AS BLOB)),'')");
        Assert.Equal(5L * JsonLimit, StoredDocumentBytes(fixture, false));
        var page = ReadPage(fixture, false, null);
        Assert.Equal(Enumerable.Range(1, 4).Select(Id).ToArray(), page.Ids);
        Assert.Equal(Id(4), page.Next);
        var resumed = ReadPage(fixture, false, page.Next);
        Assert.Equal(new[] { Id(5) }, resumed.Ids);
        Assert.Null(resumed.Next);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Byte_limited_next_row_is_not_decoded_until_resume(bool merge)
    {
        using var fixture = new PersistedMediaFixture();
        fixture.Insert(merge, PersistedMediaFixture.Row(merge, operation: Id(1)));
        fixture.Insert(merge, PersistedMediaFixture.Row(merge, operation: Id(2)));
        SetCatalog(fixture, merge, Id(1), CatalogLimit);
        SetCatalog(fixture, merge, Id(2), CatalogLimit);
        fixture.Execute($"UPDATE {Table(merge)} SET full_selection_json='!' WHERE operation_id='{Id(2):D}'");
        var page = ReadPage(fixture, merge, null);
        Assert.Equal(new[] { Id(1) }, page.Ids);
        Assert.Equal(Id(1), page.Next);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => ReadPage(fixture, merge, page.Next));
        Assert.NotEqual(BudgetError, error.Message);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)]
    public void Schema_valid_oversized_single_merge_row_rejects_combined_sum_before_decoding(int version)
    {
        using var fixture = new PersistedMediaFixture();
        fixture.Insert(true, PersistedMediaFixture.Row(true, version, operation: Id(1),
            safe: version == 2 ? PersistedMediaFixture.Full : null));
        string full = version == 2 ? "full_selection_json" : "full_keys_json";
        string safe = version == 2 ? "safe_selection_json" : "safe_keys_json";
        // Every individual field is within its SQL limit. Their sum is >32 MiB.
        // The malformed roots sentinel would fail with a different codec error
        // if the reader attempted any document decoding before summing sizes.
        fixture.Execute($"UPDATE {Table(true)} SET catalog_bytes=zeroblob({CatalogLimit}),"
            + $"{full}=printf('%*s',{JsonLimit},''),{safe}=printf('%*s',{JsonLimit},''),roots_json='!'");
        Assert.True(StoredDocumentBytes(fixture, true) > PageLimit);
        Assert.Equal(0L, Scalar(fixture, "PRAGMA ignore_check_constraints"));
        AssertBudgetRejected(fixture, true, Id(1));
    }

    public static IEnumerable<object[]> JsonColumns()
    {
        yield return [false, 1, "revision_keys_json"];
        yield return [false, 2, "full_selection_json"];
        yield return [true, 1, "roots_json"];
        yield return [true, 1, "full_keys_json"];
        yield return [true, 1, "safe_keys_json"];
        yield return [true, 2, "full_selection_json"];
        yield return [true, 2, "safe_selection_json"];
    }

    [Theory]
    [MemberData(nameof(JsonColumns))]
    public void Individual_json_limit_is_checked_before_decode_on_single_and_page_reads(
        bool merge, int version, string column)
    {
        using var fixture = new PersistedMediaFixture();
        fixture.Insert(merge, PersistedMediaFixture.Row(merge, version, operation: Id(1),
            safe: merge && version == 2 ? PersistedMediaFixture.Full : null));
        // At the inclusive byte limit the schema accepts this malformed document;
        // a codec, not the byte gate, must reject it. The leading ! also prevents
        // an all-whitespace shortcut from making the assertion ambiguous.
        fixture.Execute($"UPDATE {Table(merge)} SET {column}='!' || printf('%*s',{JsonLimit - 1},'')");
        Assert.NotEqual(BudgetError, Assert.Throws<InvalidDataException>(
            () => AssertSinglePresent(fixture, merge, Id(1))).Message);
        Assert.NotEqual(BudgetError, Assert.Throws<InvalidDataException>(
            () => ReadPage(fixture, merge, null)).Message);

        // Only the individual-limit corruption bypasses CHECK. Restore checks
        // before invoking either reader; an insertion failure cannot satisfy us.
        fixture.Execute("PRAGMA ignore_check_constraints=ON");
        try { fixture.Execute($"UPDATE {Table(merge)} SET {column}={column} || 'x'"); }
        finally { fixture.Execute("PRAGMA ignore_check_constraints=OFF"); }
        Assert.Equal((long)JsonLimit + 1, Scalar(fixture,
            $"SELECT length(CAST({column} AS BLOB)) FROM {Table(merge)}"));
        AssertBudgetRejected(fixture, merge, Id(1));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Individual_catalog_limit_is_inclusive_and_rejected_before_selection_decode_when_exceeded(bool merge)
    {
        using var fixture = new PersistedMediaFixture();
        fixture.Insert(merge, PersistedMediaFixture.Row(merge, operation: Id(1)));
        SetCatalog(fixture, merge, Id(1), CatalogLimit);
        AssertSinglePresent(fixture, merge, Id(1), CatalogLimit);
        Assert.Equal(new[] { Id(1) }, ReadPage(fixture, merge, null).Ids);
        // Plant the decode sentinel while all constraints are still enabled;
        // updating a row after corrupting its catalog would itself recheck it.
        fixture.Execute($"UPDATE {Table(merge)} SET full_selection_json='!'");
        fixture.Execute("PRAGMA ignore_check_constraints=ON");
        try { SetCatalog(fixture, merge, Id(1), CatalogLimit + 1); }
        finally { fixture.Execute("PRAGMA ignore_check_constraints=OFF"); }
        Assert.Equal((long)CatalogLimit + 1, Scalar(fixture,
            $"SELECT length(catalog_bytes) FROM {Table(merge)}"));
        AssertBudgetRejected(fixture, merge, Id(1));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Json_limit_measures_utf8_bytes_not_sqlite_character_count(bool merge)
    {
        using var fixture = new PersistedMediaFixture();
        fixture.Insert(merge, PersistedMediaFixture.Row(merge, operation: Id(1)));
        // ! + (4 MiB - 1) two-byte characters + x = exactly 8 MiB.
        fixture.Execute($"UPDATE {Table(merge)} SET full_selection_json='!' || "
            + $"replace(printf('%*s',{JsonLimit / 2 - 1},''),' ','é') || 'x'");
        Assert.Equal((long)JsonLimit, Scalar(fixture,
            $"SELECT length(CAST(full_selection_json AS BLOB)) FROM {Table(merge)}"));
        Assert.NotEqual(BudgetError, Assert.Throws<InvalidDataException>(
            () => AssertSinglePresent(fixture, merge, Id(1))).Message);
        fixture.Execute("PRAGMA ignore_check_constraints=ON");
        try { fixture.Execute($"UPDATE {Table(merge)} SET full_selection_json=full_selection_json || 'é'"); }
        finally { fixture.Execute("PRAGMA ignore_check_constraints=OFF"); }
        Assert.True(Scalar(fixture, $"SELECT length(full_selection_json) FROM {Table(merge)}") < JsonLimit);
        Assert.Equal((long)JsonLimit + 2, Scalar(fixture,
            $"SELECT length(CAST(full_selection_json AS BLOB)) FROM {Table(merge)}"));
        AssertBudgetRejected(fixture, merge, Id(1));
    }

    private static void AssertBudgetRejected(PersistedMediaFixture fixture, bool merge, Guid id)
    {
        Assert.Equal(BudgetError, Assert.Throws<InvalidDataException>(
            () => AssertSinglePresent(fixture, merge, id)).Message);
        Assert.Equal(BudgetError, Assert.Throws<InvalidDataException>(
            () => ReadPage(fixture, merge, null)).Message);
    }

    private static void AssertSinglePresent(PersistedMediaFixture fixture, bool merge, Guid id, int? catalogLength = null)
    {
        if (merge)
        {
            var operation = ArchiveSql.ReadPersistedMediaMergeOperation(fixture.Connection, null, id, PersistedMediaFixture.Request);
            Assert.NotNull(operation);
            Assert.Equal(id, operation.Identity.OperationId);
            if (catalogLength.HasValue) Assert.Equal(catalogLength.Value, operation.CatalogBytes.Length);
        }
        else
        {
            var operation = ArchiveSql.ReadPersistedMediaImportOperation(fixture.Connection, null, id, PersistedMediaFixture.Request);
            Assert.NotNull(operation);
            Assert.Equal(id, operation.Identity.OperationId);
            if (catalogLength.HasValue) Assert.Equal(catalogLength.Value, operation.CatalogBytes.Length);
        }
    }

    private static (Guid[] Ids, int[] Versions, Guid? Next) ReadPage(PersistedMediaFixture fixture, bool merge, Guid? after)
    {
        if (merge)
        {
            var page = ArchiveSql.ReadPersistedMediaMergeOperationsPage(fixture.Connection, after);
            return (page.Items.Select(item => item.Identity.OperationId).ToArray(),
                page.Items.Select(item => item.Identity.PlanVersion).ToArray(), page.NextOperationId);
        }
        else
        {
            var page = ArchiveSql.ReadPersistedMediaImportOperationsPage(fixture.Connection, after);
            return (page.Items.Select(item => item.Identity.OperationId).ToArray(),
                page.Items.Select(item => item.Identity.PlanVersion).ToArray(), page.NextOperationId);
        }
    }

    private static long JsonBytes(Dictionary<string, object?> row) => row
        .Where(field => field.Key.EndsWith("_json", StringComparison.Ordinal) && field.Value is string)
        .Sum(field => (long)Encoding.UTF8.GetByteCount((string)field.Value!));

    private static long StoredDocumentBytes(PersistedMediaFixture fixture, bool merge)
    {
        string[] columns = merge
            ? ["catalog_bytes", "roots_json", "full_keys_json", "safe_keys_json", "full_selection_json", "safe_selection_json"]
            : ["catalog_bytes", "revision_keys_json", "full_selection_json"];
        return Scalar(fixture, "SELECT sum(" + string.Join("+", columns.Select(
            column => $"coalesce(length(CAST({column} AS BLOB)),0)")) + $") FROM {Table(merge)}");
    }

    private static void SetCatalog(PersistedMediaFixture fixture, bool merge, Guid id, int bytes)
    {
        Assert.InRange(bytes, 0, CatalogLimit + 1);
        fixture.Execute($"UPDATE {Table(merge)} SET catalog_bytes=zeroblob({bytes}) WHERE operation_id='{id:D}'");
    }

    private static long Scalar(PersistedMediaFixture fixture, string sql)
    {
        using SqliteCommand command = fixture.Connection.CreateCommand();
        command.CommandText = sql;
        return Assert.IsType<long>(command.ExecuteScalar());
    }

    private static string Table(bool merge) => merge ? "archive_media_merge_operations" : "archive_media_import_operations";
    private static int Version(int index) => index % 2 == 0 ? 2 : 1;
    private static Guid Id(int index) => Guid.ParseExact(
        "cd220000-0000-4000-8000-" + index.ToString("x12", CultureInfo.InvariantCulture), "D");
}
