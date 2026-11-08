using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests.Transfer;

/// <summary>
/// Frozen schema-9 media-operation database for migration tests.  This fixture never
/// invokes production schema creators and never owns the supplied connection/transaction.
/// </summary>
internal static class Schema9MediaOperationsFixture
{
    internal const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    internal const string Request = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    internal static void CreateSchema9(SqliteConnection connection, SqliteTransaction transaction)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS archive_media_import_operations (
                operation_id TEXT NOT NULL PRIMARY KEY CHECK(length(operation_id)=36),
                request_sha256 TEXT NOT NULL CHECK(length(request_sha256)=64),
                package_fingerprint TEXT NOT NULL CHECK(length(package_fingerprint)=64),
                catalog_instance TEXT NOT NULL, catalog_generation INTEGER NOT NULL CHECK(catalog_generation>=0),
                source TEXT NOT NULL, board TEXT NOT NULL, thread TEXT NOT NULL,
                operations_parent TEXT NOT NULL UNIQUE, imported_at TEXT NOT NULL,
                state TEXT NOT NULL CHECK(state IN ('Preparing','Prepared','Committed')),
                revision_keys_json TEXT NOT NULL CHECK(length(CAST(revision_keys_json AS BLOB))<=8388608),
                candidate_directory TEXT, manifest_generation TEXT, bindings_sha256 TEXT,
                post_count INTEGER NOT NULL CHECK(post_count>=0), attachment_count INTEGER NOT NULL CHECK(attachment_count>0),
                CHECK((state='Preparing' AND candidate_directory IS NULL AND manifest_generation IS NULL AND bindings_sha256 IS NULL)
                    OR (state IN ('Prepared','Committed') AND candidate_directory IS NOT NULL
                        AND length(manifest_generation)=64 AND length(bindings_sha256)=64)));
            CREATE TABLE IF NOT EXISTS archive_media_import_receipts (
                operation_id TEXT NOT NULL PRIMARY KEY CHECK(length(operation_id)=36),
                request_sha256 TEXT NOT NULL CHECK(length(request_sha256)=64),
                post_count INTEGER NOT NULL CHECK(post_count>=0),
                attachment_count INTEGER NOT NULL CHECK(attachment_count>0));
            CREATE TABLE IF NOT EXISTS archive_media_merge_operations (
                operation_id TEXT NOT NULL PRIMARY KEY CHECK(length(operation_id)=36),
                request_sha256 TEXT NOT NULL CHECK(length(request_sha256)=64),
                package_fingerprint TEXT NOT NULL CHECK(length(package_fingerprint)=64),
                catalog_instance TEXT NOT NULL CHECK(length(catalog_instance)=36),
                catalog_generation INTEGER NOT NULL CHECK(catalog_generation>=0),
                source TEXT NOT NULL, board TEXT NOT NULL, thread TEXT NOT NULL,
                operations_parent TEXT NOT NULL UNIQUE, imported_at TEXT NOT NULL,
                state TEXT NOT NULL CHECK(state IN ('Preparing','Prepared','Committed')),
                roots_json TEXT NOT NULL CHECK(length(CAST(roots_json AS BLOB))<=8388608),
                full_keys_json TEXT NOT NULL CHECK(length(CAST(full_keys_json AS BLOB))<=8388608),
                safe_keys_json TEXT NOT NULL CHECK(length(CAST(safe_keys_json AS BLOB))<=8388608),
                catalog_bytes BLOB NOT NULL CHECK(length(catalog_bytes)<=16777216),
                full_directory TEXT, safe_directory TEXT,
                full_manifest_generation TEXT, safe_manifest_generation TEXT,
                full_bindings_sha256 TEXT, safe_bindings_sha256 TEXT,
                new_posts INTEGER NOT NULL CHECK(new_posts>=0),
                updated_posts INTEGER NOT NULL CHECK(updated_posts>=0),
                new_attachments INTEGER NOT NULL CHECK(new_attachments>=0),
                new_bindings INTEGER NOT NULL CHECK(new_bindings>=0),
                conflicts INTEGER NOT NULL CHECK(conflicts>=0),
                CHECK((state='Preparing' AND full_directory IS NULL AND safe_directory IS NULL
                    AND full_manifest_generation IS NULL AND safe_manifest_generation IS NULL
                    AND full_bindings_sha256 IS NULL AND safe_bindings_sha256 IS NULL)
                    OR (state IN ('Prepared','Committed') AND full_directory IS NOT NULL
                    AND full_manifest_generation IS NOT NULL AND length(full_manifest_generation)=64
                    AND full_bindings_sha256 IS NOT NULL AND length(full_bindings_sha256)=64
                    AND ((new_bindings=0 AND safe_directory IS NULL AND safe_manifest_generation IS NULL
                        AND safe_bindings_sha256 IS NULL)
                        OR (new_bindings>0 AND safe_directory IS NOT NULL
                        AND safe_manifest_generation IS NOT NULL AND length(safe_manifest_generation)=64
                        AND safe_bindings_sha256 IS NOT NULL AND length(safe_bindings_sha256)=64))))
            );
            CREATE TABLE IF NOT EXISTS archive_media_merge_receipts (
                operation_id TEXT NOT NULL PRIMARY KEY CHECK(length(operation_id)=36),
                request_sha256 TEXT NOT NULL CHECK(length(request_sha256)=64),
                new_posts INTEGER NOT NULL CHECK(new_posts>=0),
                updated_posts INTEGER NOT NULL CHECK(updated_posts>=0),
                new_attachments INTEGER NOT NULL CHECK(new_attachments>=0),
                new_bindings INTEGER NOT NULL CHECK(new_bindings>=0),
                conflicts INTEGER NOT NULL CHECK(conflicts>=0));
            """;
        command.ExecuteNonQuery();
    }

    /// Seeds one valid v1 row for each lifecycle state in each operation family. Only
    /// committed rows get historical receipts; receipts intentionally contain no content.
    internal static void SeedValidV1Rows(SqliteConnection connection, SqliteTransaction transaction)
    {
        for (int i = 0; i < 3; i++)
        {
            string state = i switch { 0 => "Preparing", 1 => "Prepared", _ => "Committed" };
            string op = $"00000000-0000-4000-8000-00000000000{i + 1}";
            string parent = $"schema9-fixture-{i + 1}";
            string? directory = state == "Preparing" ? null : $"/tmp/meowchan-schema9/{i + 1}/full";
            string? generation = state == "Preparing" ? null : Hash;
            Execute(connection, transaction, """
                INSERT INTO archive_media_import_operations VALUES
                ($op,$request,$fingerprint,$instance,0,'fixture','board',$thread,$parent,
                 '2026-11-08T00:00:00.0000000+00:00',$state,'[]',$directory,$generation,$bindings,1,1)
                """, ("$op", op), ("$request", Request), ("$fingerprint", Hash), ("$instance", op),
                ("$thread", $"thread-{i + 1}"), ("$parent", parent), ("$state", state),
                ("$directory", (object?)directory ?? DBNull.Value), ("$generation", (object?)generation ?? DBNull.Value),
                ("$bindings", (object?)generation ?? DBNull.Value));
            if (state == "Committed")
                Execute(connection, transaction, "INSERT INTO archive_media_import_receipts VALUES ($op,$request,1,1)",
                    ("$op", op), ("$request", Request));

            string mergeOp = $"10000000-0000-4000-8000-00000000000{i + 1}";
            string? safeDirectory = state == "Preparing" ? null : $"/tmp/meowchan-schema9/{i + 1}/safe";
            string? safeHash = state == "Preparing" ? null : Hash;
            Execute(connection, transaction, """
                INSERT INTO archive_media_merge_operations VALUES
                ($op,$request,$fingerprint,$instance,0,'fixture','board',$thread,$parent,
                 '2026-11-08T00:00:00.0000000+00:00',$state,'[]','[]','[]',X'7B7D',
                 $full,$safe,$generation,$safeGeneration,$bindings,$safeBindings,1,0,1,1,0)
                """, ("$op", mergeOp), ("$request", Request), ("$fingerprint", Hash), ("$instance", mergeOp),
                ("$thread", $"merge-thread-{i + 1}"), ("$parent", $"merge-{parent}"), ("$state", state),
                ("$full", (object?)directory ?? DBNull.Value), ("$safe", (object?)safeDirectory ?? DBNull.Value),
                ("$generation", (object?)generation ?? DBNull.Value), ("$safeGeneration", (object?)safeHash ?? DBNull.Value),
                ("$bindings", (object?)generation ?? DBNull.Value), ("$safeBindings", (object?)safeHash ?? DBNull.Value));
            if (state == "Committed")
                Execute(connection, transaction, "INSERT INTO archive_media_merge_receipts VALUES ($op,$request,1,0,1,1,0)",
                    ("$op", mergeOp), ("$request", Request));
        }
        // Historical successes after operation/content cleanup: no operation row or content FK.
        Execute(connection, transaction, "INSERT INTO archive_media_import_receipts VALUES ($op,$request,2,3)",
            ("$op", "20000000-0000-4000-8000-000000000001"), ("$request", Request));
        Execute(connection, transaction, "INSERT INTO archive_media_merge_receipts VALUES ($op,$request,2,1,3,1,1)",
            ("$op", "30000000-0000-4000-8000-000000000001"), ("$request", Request));
    }

    // Exact storage-class/value readback, ordered by primary key. Explicit schema-9
    // columns exclude future additions so before/after migration snapshots compare cleanly.
    // Cells encode NULL as N, text as T + unchanged text, BLOB as B + hex, integer as I + decimal.
    internal static TableSnapshot[] ReadSnapshot(SqliteConnection connection, SqliteTransaction transaction)
    {
        (string Table, string Columns)[] tables =
        [
            ("archive_media_import_operations", "operation_id,request_sha256,package_fingerprint,catalog_instance,catalog_generation,source,board,thread,operations_parent,imported_at,state,revision_keys_json,candidate_directory,manifest_generation,bindings_sha256,post_count,attachment_count"),
            ("archive_media_import_receipts", "operation_id,request_sha256,post_count,attachment_count"),
            ("archive_media_merge_operations", "operation_id,request_sha256,package_fingerprint,catalog_instance,catalog_generation,source,board,thread,operations_parent,imported_at,state,roots_json,full_keys_json,safe_keys_json,catalog_bytes,full_directory,safe_directory,full_manifest_generation,safe_manifest_generation,full_bindings_sha256,safe_bindings_sha256,new_posts,updated_posts,new_attachments,new_bindings,conflicts"),
            ("archive_media_merge_receipts", "operation_id,request_sha256,new_posts,updated_posts,new_attachments,new_bindings,conflicts")
        ];
        var snapshots = new List<TableSnapshot>();
        foreach (var (table, columns) in tables)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"SELECT {columns} FROM {table} ORDER BY operation_id COLLATE BINARY";
            using SqliteDataReader reader = command.ExecuteReader();
            var rows = new List<string[]>();
            while (reader.Read())
            {
                var cells = new string[reader.FieldCount];
                for (int i = 0; i < cells.Length; i++)
                    cells[i] = reader.GetValue(i) switch
                    {
                        DBNull => "N",
                        string text => "T" + text,
                        byte[] blob => "B" + Convert.ToHexString(blob),
                        long integer => "I" + integer.ToString(CultureInfo.InvariantCulture),
                        double real => "R" + real.ToString("R", CultureInfo.InvariantCulture),
                        _ => throw new InvalidOperationException("Unexpected SQLite storage class.")
                    };
                rows.Add(cells);
            }
            snapshots.Add(new(table, columns.Split(','), rows.ToArray()));
        }
        return snapshots.ToArray();
    }

    internal sealed record TableSnapshot(string Table, string[] Columns, string[][] Rows);

    private static void Execute(SqliteConnection c, SqliteTransaction tx, string sql, params (string Name, object Value)[] values)
    {
        using SqliteCommand command = c.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }
}
