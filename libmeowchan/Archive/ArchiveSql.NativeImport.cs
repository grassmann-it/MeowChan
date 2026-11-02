using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace MeowChan.Archive;

internal static partial class ArchiveSql
{
    internal static void CreateNativeImportSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        using SqliteCommand schema = Command(connection, transaction, """
            CREATE TABLE IF NOT EXISTS archive_import_fence (
                singleton INTEGER NOT NULL PRIMARY KEY CHECK(singleton=1),
                instance TEXT NOT NULL CHECK(length(instance)=36),
                generation INTEGER NOT NULL CHECK(typeof(generation)='integer' AND generation>=0));
            CREATE TABLE IF NOT EXISTS archive_import_receipts (
                operation_id TEXT NOT NULL PRIMARY KEY, request_sha256 TEXT NOT NULL CHECK(length(request_sha256)=64),
                new_threads INTEGER NOT NULL, new_posts INTEGER NOT NULL, updated_posts INTEGER NOT NULL,
                conflicting_threads INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS archive_import_candidates (
                source TEXT NOT NULL, board TEXT NOT NULL, thread TEXT NOT NULL,
                sha256 TEXT NOT NULL CHECK(length(sha256)=64), imported_at TEXT NOT NULL,
                exported_at TEXT NOT NULL, has_conflicts INTEGER NOT NULL CHECK(has_conflicts IN (0,1)),
                catalog_bytes BLOB NOT NULL CHECK(length(catalog_bytes)<=16777216),
                PRIMARY KEY(source,board,thread,sha256),
                FOREIGN KEY(source,board,thread) REFERENCES archive_threads(source,board,thread) ON DELETE CASCADE);
            """);
        schema.ExecuteNonQuery();
        using SqliteCommand seed = Command(connection, transaction,
            "INSERT INTO archive_import_fence(singleton,instance,generation) VALUES(1,$instance,0) ON CONFLICT DO NOTHING");
        seed.Parameters.AddWithValue("$instance", Guid.NewGuid().ToString("D"));
        seed.ExecuteNonQuery();
        // Literal host-owned table names only. Include revisionless registration/directory/decision
        // changes and candidate resolution/deletion, not merely capture's revision counter.
        foreach (string table in new[] { "archive_threads", "archive_posts", "archive_attachments",
            "archive_media_directories", "archive_source_contracts", "media_decisions", "archive_import_candidates" })
        foreach (string operation in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            using SqliteCommand trigger = Command(connection, transaction, $"""
                CREATE TRIGGER IF NOT EXISTS import_fence_{table}_{operation} AFTER {operation} ON {table} BEGIN
                    UPDATE archive_import_fence SET generation=generation+1 WHERE singleton=1;
                END;
                """);
            trigger.ExecuteNonQuery();
        }
    }

    internal static (string Instance, long Generation) ReadImportFence(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using SqliteCommand command = Command(connection, transaction,
            "SELECT instance,generation FROM archive_import_fence WHERE singleton=1");
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read() || !Guid.TryParseExact(reader.GetString(0), "D", out Guid id) || id == Guid.Empty
            || reader.GetInt64(1) < 0) throw new InvalidDataException("Invalid archive import fence.");
        return (reader.GetString(0), reader.GetInt64(1));
    }

    internal static void ValidateNativeImportSchema(SqliteConnection connection)
    {
        _ = ReadImportFence(connection, null);
        using SqliteCommand columns = Command(connection, null,
            "SELECT source,board,thread,sha256,imported_at,exported_at,has_conflicts,catalog_bytes FROM archive_import_candidates LIMIT 0; "
                + "SELECT operation_id,request_sha256,new_threads,new_posts,updated_posts,conflicting_threads FROM archive_import_receipts LIMIT 0");
        columns.ExecuteNonQuery();
        // A missing fence trigger must fail closed rather than admit a stale/delete-recreate plan.
        using SqliteCommand triggers = Command(connection, null, "SELECT count(*) FROM sqlite_master WHERE type='trigger' AND name IN ("
            + string.Join(",", System.Linq.Enumerable.Select(System.Linq.Enumerable.Range(0, 21), i => "$n" + i)) + ")");
        int index = 0;
        foreach (string table in new[] { "archive_threads", "archive_posts", "archive_attachments",
            "archive_media_directories", "archive_source_contracts", "media_decisions", "archive_import_candidates" })
        foreach (string operation in new[] { "INSERT", "UPDATE", "DELETE" })
            triggers.Parameters.AddWithValue("$n" + index++, $"import_fence_{table}_{operation}");
        if (Convert.ToInt32(triggers.ExecuteScalar()) != 21)
            throw new InvalidDataException("The archive import fence is incomplete.");
    }
}
