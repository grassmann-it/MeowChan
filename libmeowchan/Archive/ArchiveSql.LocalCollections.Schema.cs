using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;

namespace MeowChan.Archive;

internal static partial class ArchiveSql
{
    private static string LocalUuid(string name) => $"typeof({name})='text' AND length({name})=36 AND {name}=lower({name}) AND {name}<>'00000000-0000-0000-0000-000000000000' AND substr({name},9,1)='-' AND substr({name},14,1)='-' AND substr({name},19,1)='-' AND substr({name},24,1)='-' AND length(replace({name},'-',''))=32 AND replace({name},'-','') NOT GLOB '*[^0-9a-f]*'";
    private static string LocalHash(string name) => $"typeof({name})='text' AND length({name})=64 AND {name} NOT GLOB '*[^0-9a-f]*'";
    private static string LocalInteger(string name, string range) => $"typeof({name})='integer' AND {name} {range}";
    private static string LocalText(string name, int max, bool nonempty = false) => $"typeof({name})='text' AND length(CAST({name} AS BLOB))<={max}" + (nonempty ? $" AND length(CAST({name} AS BLOB))>0" : "");

    // These exact definitions are also the fail-closed schema manifest. In particular a table
    // with the right column names but missing CHECK/UNIQUE/FK clauses is not schema 9.
    private static IEnumerable<(string Type, string Name, string Sql)> LocalSchemaObjects()
    {
        yield return ("table", "local_collections", $"""
            CREATE TABLE local_collections (
                collection_id TEXT NOT NULL PRIMARY KEY CHECK({LocalUuid("collection_id")}),
                title TEXT NOT NULL CHECK({LocalText("title", 4096)}),
                revision INTEGER NOT NULL CHECK({LocalInteger("revision", "=1")}))
            """);
        yield return ("table", "local_items", $"""
            CREATE TABLE local_items (
                collection_id TEXT NOT NULL CHECK({LocalUuid("collection_id")}),
                item_id TEXT NOT NULL CHECK({LocalUuid("item_id")}),
                ordinal INTEGER NOT NULL CHECK({LocalInteger("ordinal", "BETWEEN 0 AND 999")}),
                claims_json TEXT CHECK(claims_json IS NULL OR ({LocalText("claims_json", 1048576, true)})),
                PRIMARY KEY(collection_id,item_id), UNIQUE(collection_id,ordinal),
                FOREIGN KEY(collection_id) REFERENCES local_collections(collection_id))
            """);
        yield return ("table", "local_media_revisions", $"""
            CREATE TABLE local_media_revisions (
                collection_id TEXT NOT NULL CHECK({LocalUuid("collection_id")}),
                item_id TEXT NOT NULL CHECK({LocalUuid("item_id")}),
                revision_id TEXT NOT NULL CHECK({LocalUuid("revision_id")}),
                role INTEGER NOT NULL CHECK({LocalInteger("role", "=1")}),
                sha256 TEXT NOT NULL CHECK({LocalHash("sha256")}),
                length INTEGER NOT NULL CHECK({LocalInteger("length", ">=0")}),
                payload_relative_path TEXT NOT NULL CHECK({LocalText("payload_relative_path", 256, true)}),
                display_name TEXT CHECK(display_name IS NULL OR ({LocalText("display_name", 4096)})),
                PRIMARY KEY(collection_id,item_id,revision_id), UNIQUE(collection_id,item_id),
                UNIQUE(collection_id,revision_id),
                FOREIGN KEY(collection_id,item_id) REFERENCES local_items(collection_id,item_id))
            """);
        yield return ("table", "local_import_operations", $"""
            CREATE TABLE local_import_operations (
                operation_id TEXT NOT NULL PRIMARY KEY CHECK({LocalUuid("operation_id")}),
                collection_id TEXT NOT NULL UNIQUE CHECK({LocalUuid("collection_id")}),
                catalog_instance TEXT NOT NULL CHECK({LocalUuid("catalog_instance")}),
                expected_generation INTEGER NOT NULL CHECK({LocalInteger("expected_generation", ">=0")}),
                attempt INTEGER NOT NULL CHECK({LocalInteger("attempt", ">0")}),
                state TEXT NOT NULL CHECK(state IN ('Capturing','Frozen','Prepared','Committed')),
                created_at TEXT NOT NULL CHECK({LocalText("created_at", 64, true)}),
                selection_json TEXT NOT NULL CHECK({LocalText("selection_json", 25165824, true)}),
                content_json TEXT CHECK(content_json IS NULL OR ({LocalText("content_json", 1048576, true)})),
                request_sha256 TEXT CHECK(request_sha256 IS NULL OR ({LocalHash("request_sha256")})),
                stage_bindings_sha256 TEXT CHECK(stage_bindings_sha256 IS NULL OR ({LocalHash("stage_bindings_sha256")})),
                candidate_bindings_sha256 TEXT CHECK(candidate_bindings_sha256 IS NULL OR ({LocalHash("candidate_bindings_sha256")})),
                item_count INTEGER NOT NULL CHECK({LocalInteger("item_count", "BETWEEN 1 AND 1000")}),
                payload_limit INTEGER NOT NULL CHECK({LocalInteger("payload_limit", ">0")}),
                metadata_reservation INTEGER NOT NULL CHECK({LocalInteger("metadata_reservation", "BETWEEN 1 AND 33554432")}),
                total_length INTEGER CHECK(total_length IS NULL OR ({LocalInteger("total_length", ">=0")} AND total_length<=payload_limit)),
                CHECK((state='Capturing' AND content_json IS NULL AND request_sha256 IS NULL
                    AND stage_bindings_sha256 IS NULL AND candidate_bindings_sha256 IS NULL AND total_length IS NULL)
                    OR (state IN ('Frozen','Prepared','Committed') AND content_json IS NOT NULL
                        AND request_sha256 IS NOT NULL AND stage_bindings_sha256 IS NOT NULL AND total_length IS NOT NULL
                        AND ((state='Frozen' AND candidate_bindings_sha256 IS NULL)
                            OR (state IN ('Prepared','Committed') AND candidate_bindings_sha256 IS NOT NULL)))))
            """);
        yield return ("table", "local_import_receipts", $"""
            CREATE TABLE local_import_receipts (
                operation_id TEXT NOT NULL PRIMARY KEY CHECK({LocalUuid("operation_id")}),
                request_sha256 TEXT NOT NULL CHECK({LocalHash("request_sha256")}),
                catalog_instance TEXT NOT NULL CHECK({LocalUuid("catalog_instance")}),
                collection_id TEXT NOT NULL CHECK({LocalUuid("collection_id")}),
                receipt_json TEXT NOT NULL CHECK({LocalText("receipt_json", 262144, true)}))
            """);
        foreach (string table in new[] { "local_collections", "local_items", "local_media_revisions" })
        foreach (string operation in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            string name = $"import_fence_{table}_{operation}";
            yield return ("trigger", name, $"""
                CREATE TRIGGER {name} AFTER {operation} ON {table} BEGIN
                    SELECT CASE WHEN NOT EXISTS (SELECT 1 FROM archive_import_fence WHERE singleton=1
                        AND typeof(generation)='integer' AND generation>=0 AND generation<9223372036854775807)
                        THEN RAISE(ABORT,'Invalid local import fence') END;
                    UPDATE archive_import_fence SET generation=generation+1 WHERE singleton=1;
                END
                """);
        }
    }

    internal static void CreateLocalCollectionSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        foreach (var definition in LocalSchemaObjects())
        {
            // Legacy schema-shape fixtures may already contain the exact additive objects.
            // Collisions are never repaired: complete target validation follows before stamping.
            string sql = definition.Sql.Replace("CREATE TABLE ", "CREATE TABLE IF NOT EXISTS ", StringComparison.Ordinal)
                .Replace("CREATE TRIGGER ", "CREATE TRIGGER IF NOT EXISTS ", StringComparison.Ordinal);
            using SqliteCommand command = Command(connection, transaction, sql + ";");
            command.ExecuteNonQuery();
        }
    }

    internal static void ValidateLocalCollectionSchema(SqliteConnection connection, SqliteTransaction? transaction)
    {
        HashSet<string> expectedTriggers = new(StringComparer.Ordinal);
        foreach (var definition in LocalSchemaObjects())
        {
            using SqliteCommand command = Command(connection, transaction,
                "SELECT sql FROM sqlite_master WHERE type=$type AND name=$name");
            command.Parameters.AddWithValue("$type", definition.Type);
            command.Parameters.AddWithValue("$name", definition.Name);
            if (command.ExecuteScalar() is not string sql || LocalSchemaSql(sql) != LocalSchemaSql(definition.Sql))
                throw new InvalidDataException("Invalid local collection schema definition.");
            if (definition.Type == "trigger") expectedTriggers.Add(definition.Name);
        }
        using (SqliteCommand triggers = Command(connection, transaction,
            "SELECT name FROM sqlite_master WHERE type='trigger' AND tbl_name IN "
            + "('local_collections','local_items','local_media_revisions','local_import_operations','local_import_receipts')"))
        using (SqliteDataReader reader = triggers.ExecuteReader())
            while (reader.Read())
                if (!expectedTriggers.Remove(reader.GetString(0)))
                    throw new InvalidDataException("Unexpected local collection trigger.");
        using (SqliteCommand indexes = Command(connection, transaction,
            "SELECT 1 FROM sqlite_master WHERE type='index' AND sql IS NOT NULL AND tbl_name IN "
            + "('local_collections','local_items','local_media_revisions','local_import_operations','local_import_receipts') LIMIT 1"))
            if (indexes.ExecuteScalar() != null)
                throw new InvalidDataException("Unexpected local collection index.");
        // FK declarations are part of the exact manifest; check existing row relationships too.
        foreach (string table in new[] { "local_items", "local_media_revisions" })
        {
            using SqliteCommand fk = Command(connection, transaction, $"PRAGMA foreign_key_check({table})");
            using SqliteDataReader reader = fk.ExecuteReader();
            if (reader.Read()) throw new InvalidDataException("Invalid local collection foreign key.");
        }
    }

    private static string LocalSchemaSql(string sql)
    {
        StringBuilder result = new();
        char quote = '\0';
        foreach (char c in sql.Trim().TrimEnd(';'))
        {
            if (quote != '\0') { result.Append(c); if (c == quote) quote = '\0'; }
            else if (c is '\'' or '"') { quote = c; result.Append(c); }
            else if (!char.IsWhiteSpace(c)) result.Append(c);
        }
        return result.ToString();
    }
}
