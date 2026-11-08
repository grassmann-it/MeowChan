using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace MeowChan.Archive;

internal static partial class ArchiveSql
{
    // Storage preparation only: neither this manifest nor a v2 row authorizes execution.
    // Keep the legacy manifest frozen; migration must not project away unknown columns.
    private sealed record MediaTransferTable10(string Name, bool Import, bool Operation,
        string Columns, string ColumnNames, string? LegacyState);

    private static IEnumerable<MediaTransferTable10> MediaTransferTables10()
    {
        yield return new("archive_media_import_operations", true, true,
            """
            operation_id TEXT NOT NULL PRIMARY KEY CHECK(length(operation_id)=36),
            request_sha256 TEXT NOT NULL CHECK(length(request_sha256)=64),
            package_fingerprint TEXT NOT NULL CHECK(length(package_fingerprint)=64),
            catalog_instance TEXT NOT NULL, catalog_generation INTEGER NOT NULL CHECK(catalog_generation>=0),
            source TEXT NOT NULL, board TEXT NOT NULL, thread TEXT NOT NULL,
            operations_parent TEXT NOT NULL UNIQUE, imported_at TEXT NOT NULL,
            state TEXT NOT NULL CHECK(state IN ('Preparing','Prepared','Committed')),
            revision_keys_json TEXT NOT NULL CHECK(length(CAST(revision_keys_json AS BLOB))<=8388608),
            candidate_directory TEXT, manifest_generation TEXT, bindings_sha256 TEXT,
            post_count INTEGER NOT NULL CHECK(post_count>=0), attachment_count INTEGER NOT NULL CHECK(attachment_count>0)
            """,
            "operation_id,request_sha256,package_fingerprint,catalog_instance,catalog_generation,source,board,thread,operations_parent,imported_at,state,revision_keys_json,candidate_directory,manifest_generation,bindings_sha256,post_count,attachment_count",
            """
            (state='Preparing' AND candidate_directory IS NULL AND manifest_generation IS NULL AND bindings_sha256 IS NULL)
                OR (state IN ('Prepared','Committed') AND candidate_directory IS NOT NULL
                    AND length(manifest_generation)=64 AND length(bindings_sha256)=64)
            """);
        yield return new("archive_media_import_receipts", true, false,
            """
            operation_id TEXT NOT NULL PRIMARY KEY CHECK(length(operation_id)=36),
            request_sha256 TEXT NOT NULL CHECK(length(request_sha256)=64),
            post_count INTEGER NOT NULL CHECK(post_count>=0),
            attachment_count INTEGER NOT NULL CHECK(attachment_count>0)
            """,
            "operation_id,request_sha256,post_count,attachment_count",
            null);
        yield return new("archive_media_merge_operations", false, true,
            """
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
            conflicts INTEGER NOT NULL CHECK(conflicts>=0)
            """,
            "operation_id,request_sha256,package_fingerprint,catalog_instance,catalog_generation,source,board,thread,operations_parent,imported_at,state,roots_json,full_keys_json,safe_keys_json,catalog_bytes,full_directory,safe_directory,full_manifest_generation,safe_manifest_generation,full_bindings_sha256,safe_bindings_sha256,new_posts,updated_posts,new_attachments,new_bindings,conflicts",
            """
            (state='Preparing' AND full_directory IS NULL AND safe_directory IS NULL
                AND full_manifest_generation IS NULL AND safe_manifest_generation IS NULL
                AND full_bindings_sha256 IS NULL AND safe_bindings_sha256 IS NULL)
                OR (state IN ('Prepared','Committed') AND full_directory IS NOT NULL
                AND full_manifest_generation IS NOT NULL AND length(full_manifest_generation)=64
                AND full_bindings_sha256 IS NOT NULL AND length(full_bindings_sha256)=64
                AND ((new_bindings=0 AND safe_directory IS NULL AND safe_manifest_generation IS NULL
                    AND safe_bindings_sha256 IS NULL)
                    OR (new_bindings>0 AND safe_directory IS NOT NULL
                    AND safe_manifest_generation IS NOT NULL AND length(safe_manifest_generation)=64
                    AND safe_bindings_sha256 IS NOT NULL AND length(safe_bindings_sha256)=64)))
            """);
        yield return new("archive_media_merge_receipts", false, false,
            """
            operation_id TEXT NOT NULL PRIMARY KEY CHECK(length(operation_id)=36),
            request_sha256 TEXT NOT NULL CHECK(length(request_sha256)=64),
            new_posts INTEGER NOT NULL CHECK(new_posts>=0),
            updated_posts INTEGER NOT NULL CHECK(updated_posts>=0),
            new_attachments INTEGER NOT NULL CHECK(new_attachments>=0),
            new_bindings INTEGER NOT NULL CHECK(new_bindings>=0),
            conflicts INTEGER NOT NULL CHECK(conflicts>=0)
            """,
            "operation_id,request_sha256,new_posts,updated_posts,new_attachments,new_bindings,conflicts",
            null);
    }

    private static IEnumerable<string> MediaTransferCounters10(bool import)
    {
        yield return "evidence_additions";
        yield return "derived_descriptor_additions";
        yield return "contained_derived_additions";
        if (import) yield return "original_binding_count";
    }

    private static string MediaTransferHash10(string name)
        => $"{name} IS NOT NULL AND ({LocalHash(name)}) AND length(CAST({name} AS BLOB))=64";

    private static string MediaTransferSelection10(string name)
        => $"{name} TEXT CHECK({name} IS NULL OR ({LocalText(name, 8388608, true)}))";

    private static string MediaTransferDigest10(string name)
        => $"{name} TEXT CHECK({name} IS NULL OR ({MediaTransferHash10(name)}))";

    private static string MediaTransferCount10(string name, int maximum)
        => $"{name} INTEGER NOT NULL DEFAULT 0 CHECK({LocalInteger(name, $"BETWEEN 0 AND {maximum}")})";

    private static string MediaTransferRoot10(string directory, string generation, string inventory)
        => $"{directory} IS NOT NULL AND typeof({directory})='text' AND length(CAST({directory} AS BLOB))>0"
            + $" AND ({MediaTransferHash10(generation)}) AND ({MediaTransferHash10(inventory)})";

    private static string MediaTransferDefinition10(MediaTransferTable10 table, string name, bool legacy = false)
    {
        if (legacy)
            return $"CREATE TABLE {name} ({table.Columns}"
                + (table.LegacyState == null ? ")" : $", CHECK({table.LegacyState}))");

        // Retain the legacy affinities and checks. Stricter v2 requirements are gated,
        // so migrating supported v1 values does not normalize their storage classes.
        string columns = table.Import
            ? table.Columns.Replace("CHECK(attachment_count>0)",
                "CHECK((plan_version=1 AND attachment_count>0) OR (plan_version=2 AND attachment_count>=0))",
                StringComparison.Ordinal)
            : table.Columns;
        List<string> parts = new() { columns,
            $"plan_version INTEGER NOT NULL DEFAULT 1 CHECK({LocalInteger("plan_version", "IN (1,2)")})" };
        parts.AddRange(MediaTransferCounters10(table.Import).Select(n => MediaTransferCount10(n, int.MaxValue)));
        List<string> checks = new() { "CHECK(contained_derived_additions<=derived_descriptor_additions)" };
        List<string> v1Defaults = MediaTransferCounters10(table.Import).Select(n => n + "=0").ToList();

        if (table.Operation)
        {
            parts.Add(MediaTransferSelection10("full_selection_json"));
            parts.Add(MediaTransferDigest10("full_selection_sha256"));
            parts.Add(MediaTransferDigest10("full_inventory_sha256"));
            parts.Add(MediaTransferCount10("full_member_count", 100000));
            v1Defaults.AddRange(new[] { "full_selection_json IS NULL", "full_selection_sha256 IS NULL",
                "full_inventory_sha256 IS NULL", "full_member_count=0" });
            if (table.Import)
            {
                parts.Add("catalog_bytes BLOB CHECK(catalog_bytes IS NULL OR (typeof(catalog_bytes)='blob' AND length(catalog_bytes)<=16777216))");
                v1Defaults.Add("catalog_bytes IS NULL");
            }
            else
            {
                parts.Add(MediaTransferSelection10("safe_selection_json"));
                parts.Add(MediaTransferDigest10("safe_selection_sha256"));
                parts.Add(MediaTransferDigest10("safe_inventory_sha256"));
                parts.Add(MediaTransferCount10("safe_member_count", 100000));
                v1Defaults.AddRange(new[] { "safe_selection_json IS NULL", "safe_selection_sha256 IS NULL",
                    "safe_inventory_sha256 IS NULL", "safe_member_count=0" });
            }
            string legacyState = table.LegacyState!;
            if (table.Import)
                legacyState = legacyState.Replace("AND length(manifest_generation)=64",
                    "AND manifest_generation IS NOT NULL AND bindings_sha256 IS NOT NULL AND length(manifest_generation)=64",
                    StringComparison.Ordinal);
            checks.Add($"CHECK(plan_version=2 OR ({legacyState}))");
        }
        checks.Add($"CHECK(plan_version=2 OR ({string.Join(" AND ", v1Defaults)}))");

        // Receipts have only identities and counters; they deliberately have no operation FK.
        List<string> v2 = new() { MediaTransferHash10("request_sha256") };
        string[] oldCounts = table.Import ? new[] { "post_count", "attachment_count" }
            : new[] { "new_posts", "updated_posts", "new_attachments", "new_bindings", "conflicts" };
        v2.AddRange(oldCounts.Select(n => LocalInteger(n, "BETWEEN 0 AND 2147483647")));
        if (table.Operation)
        {
            v2.Add(MediaTransferHash10("package_fingerprint"));
            v2.Add(LocalInteger("catalog_generation", ">=0"));
            v2.Add("full_selection_json IS NOT NULL AND full_selection_sha256 IS NOT NULL AND full_member_count>0");
            v2.Add("catalog_bytes IS NOT NULL AND typeof(catalog_bytes)='blob' AND length(catalog_bytes)<=16777216");
            if (table.Import)
            {
                v2.Add("revision_keys_json='[]' AND bindings_sha256 IS NULL");
                v2.Add($"""
                    ((state='Preparing' AND candidate_directory IS NULL AND manifest_generation IS NULL
                        AND full_inventory_sha256 IS NULL)
                     OR (state IN ('Prepared','Committed') AND
                        ({MediaTransferRoot10("candidate_directory", "manifest_generation", "full_inventory_sha256")})))
                    """);
            }
            else
            {
                v2.Add("full_keys_json='[]' AND safe_keys_json='[]' AND full_bindings_sha256 IS NULL AND safe_bindings_sha256 IS NULL");
                v2.Add("""
                    ((safe_member_count=0 AND safe_selection_json IS NULL AND safe_selection_sha256 IS NULL
                        AND safe_inventory_sha256 IS NULL AND safe_directory IS NULL AND safe_manifest_generation IS NULL)
                     OR (safe_member_count>0 AND safe_selection_json IS NOT NULL AND safe_selection_sha256 IS NOT NULL))
                    """);
                // Safe membership, not the number of original bindings, decides whether a
                // Safe root exists. An evidence-/derived-only nonempty Safe is legitimate.
                v2.Add($"""
                    ((state='Preparing' AND full_directory IS NULL AND safe_directory IS NULL
                        AND full_manifest_generation IS NULL AND safe_manifest_generation IS NULL
                        AND full_inventory_sha256 IS NULL AND safe_inventory_sha256 IS NULL)
                     OR (state IN ('Prepared','Committed') AND
                        ({MediaTransferRoot10("full_directory", "full_manifest_generation", "full_inventory_sha256")})
                        AND (safe_member_count=0 OR
                            ({MediaTransferRoot10("safe_directory", "safe_manifest_generation", "safe_inventory_sha256")}))))
                    """);
                v2.Add("""
                    (safe_directory IS NULL OR full_directory IS NULL OR safe_directory<>full_directory
                        OR (conflicts=0 AND full_selection_json=safe_selection_json
                            AND full_selection_sha256=safe_selection_sha256 AND full_member_count=safe_member_count
                            AND full_inventory_sha256=safe_inventory_sha256
                            AND full_manifest_generation=safe_manifest_generation))
                    """);
            }
        }
        checks.Add($"CHECK(plan_version=1 OR ({string.Join(" AND ", v2.Select(p => "(" + p + ")"))}))");
        parts.AddRange(checks);
        return $"CREATE TABLE {name} (\n{string.Join(",\n", parts)})";
    }

    internal static void CreateMediaTransferSchema10(SqliteConnection connection, SqliteTransaction transaction)
    {
        MediaTransferRequireTransaction10(connection, transaction);
        MediaTransferTable10[] tables = MediaTransferTables10().ToArray();
        // Check every collision before creating anything. Exact existing tables are harmless;
        // malformed names never get the apparent success of CREATE IF NOT EXISTS.
        foreach (MediaTransferTable10 table in tables)
        {
            MediaTransferRejectShadow10(connection, transaction, table.Name + "_schema10");
            if (MediaTransferObjectExists10(connection, transaction, table.Name))
                MediaTransferValidateTable10(connection, transaction, table, table.Name, legacy: false);
        }
        foreach (MediaTransferTable10 table in tables)
            if (!MediaTransferObjectExists10(connection, transaction, table.Name))
            {
                using SqliteCommand command = Command(connection, transaction, MediaTransferDefinition10(table, table.Name));
                command.ExecuteNonQuery();
            }
        ValidateMediaTransferSchema10(connection, transaction);
    }

    internal static void MigrateMediaTransferSchema10(SqliteConnection connection, SqliteTransaction transaction,
        CancellationToken cancellationToken = default, Action<string>? checkpoint = null)
    {
        MediaTransferRequireTransaction10(connection, transaction);
        cancellationToken.ThrowIfCancellationRequested();
        int version = GetPragma(connection, "user_version", transaction);
        if (version == 10)
        {
            ValidateMediaTransferSchema10(connection, transaction);
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }
        if (version is < 1 or > 9)
            throw new InvalidDataException("Unsupported media transfer migration version.");

        MediaTransferTable10[] tables = MediaTransferTables10().ToArray();
        foreach (MediaTransferTable10 table in tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MediaTransferRejectShadow10(connection, transaction, table.Name + "_schema10");
            MediaTransferValidateTable10(connection, transaction, table, table.Name, legacy: true);
        }
        MediaTransferRejectIncomingForeignKeys10(connection, transaction, tables, cancellationToken);
        foreach (MediaTransferTable10 table in tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using SqliteCommand create = Command(connection, transaction,
                MediaTransferDefinition10(table, table.Name + "_schema10"));
            create.ExecuteNonQuery();
        }
        cancellationToken.ThrowIfCancellationRequested();
        checkpoint?.Invoke("created");
        cancellationToken.ThrowIfCancellationRequested();
        foreach (MediaTransferTable10 table in tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // No casts, defaults for old columns, deduplication, or replacement. Constraint
            // failures (including old NULL-ready corruption) abort, for the caller to roll back.
            using SqliteCommand copy = Command(connection, transaction,
                $"INSERT INTO main.{table.Name}_schema10(rowid,{table.ColumnNames}) "
                + $"SELECT rowid,{table.ColumnNames} FROM main.{table.Name}");
            copy.ExecuteNonQuery();
            MediaTransferValidateCopy10(connection, transaction, table);
        }
        cancellationToken.ThrowIfCancellationRequested();
        checkpoint?.Invoke("copied");
        cancellationToken.ThrowIfCancellationRequested();
        // Verify all copies before dropping the first original, including after the test hook.
        foreach (MediaTransferTable10 table in tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MediaTransferValidateTable10(connection, transaction, table, table.Name + "_schema10", legacy: false);
            MediaTransferValidateCopy10(connection, transaction, table);
        }
        foreach (MediaTransferTable10 table in tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using SqliteCommand swap = Command(connection, transaction,
                $"DROP TABLE main.{table.Name}; ALTER TABLE main.{table.Name}_schema10 RENAME TO {table.Name};");
            swap.ExecuteNonQuery();
        }
        cancellationToken.ThrowIfCancellationRequested();
        checkpoint?.Invoke("swapped");
        cancellationToken.ThrowIfCancellationRequested();
        // No stamp or commit here: lifecycle owns final whole-catalog validation and version10.
        ValidateMediaTransferSchema10(connection, transaction);
        cancellationToken.ThrowIfCancellationRequested();
    }

    internal static void ValidateMediaTransferSchema10(SqliteConnection connection, SqliteTransaction? transaction)
    {
        foreach (MediaTransferTable10 table in MediaTransferTables10())
        {
            MediaTransferRejectShadow10(connection, transaction, table.Name + "_schema10");
            MediaTransferValidateTable10(connection, transaction, table, table.Name, legacy: false);
        }
    }

    private static void MediaTransferRequireTransaction10(SqliteConnection connection, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (!ReferenceEquals(transaction.Connection, connection))
            throw new InvalidOperationException("Media transfer schema changes require the caller's active transaction.");
        // The lifecycle acquires an immediate write transaction. Never begin/commit another
        // transaction, and never turn off enforcement to get corrupt rows through the copy.
        if (GetPragma(connection, "ignore_check_constraints", transaction) != 0)
            throw new InvalidDataException("Media transfer migration requires enabled CHECK constraints.");
    }

    private static bool MediaTransferObjectExists10(SqliteConnection c, SqliteTransaction? tx, string name)
    {
        using SqliteCommand command = Command(c, tx, """
            SELECT 1 FROM main.sqlite_master WHERE name=$name COLLATE NOCASE
            UNION ALL SELECT 1 FROM temp.sqlite_master WHERE name=$name COLLATE NOCASE LIMIT 1
            """);
        command.Parameters.AddWithValue("$name", name);
        return command.ExecuteScalar() != null;
    }

    private static void MediaTransferRejectShadow10(SqliteConnection c, SqliteTransaction? tx, string name)
    {
        if (MediaTransferObjectExists10(c, tx, name))
            throw new InvalidDataException("A media transfer migration shadow name is occupied.");
    }

    private static void MediaTransferValidateTable10(SqliteConnection c, SqliteTransaction? tx,
        MediaTransferTable10 table, string name, bool legacy)
    {
        using (SqliteCommand extra = Command(c, tx, """
            SELECT 1 FROM main.sqlite_master
            WHERE tbl_name=$name COLLATE NOCASE AND (type='trigger' OR (type='index' AND sql IS NOT NULL))
            UNION ALL SELECT 1 FROM temp.sqlite_master
            WHERE name=$name COLLATE NOCASE OR tbl_name=$name COLLATE NOCASE LIMIT 1
            """))
        {
            extra.Parameters.AddWithValue("$name", name);
            if (extra.ExecuteScalar() != null)
                throw new InvalidDataException("Unexpected media transfer index, trigger, or temporary object.");
        }
        if (legacy)
        {
            // table_xinfo includes hidden/generated columns. A restamped v10 table, even
            // one containing only v1 rows, is NOT a supported legacy copy source.
            using SqliteCommand columns = Command(c, tx, $"PRAGMA main.table_xinfo({name})");
            using SqliteDataReader reader = columns.ExecuteReader();
            string[] expected = table.ColumnNames.Split(',');
            int count = 0;
            while (reader.Read())
            {
                if (count >= expected.Length || reader.GetString(1) != expected[count] || reader.GetInt32(6) != 0)
                    throw new InvalidDataException("Unexpected legacy media transfer column.");
                count++;
            }
            if (count != expected.Length)
                throw new InvalidDataException("Missing legacy media transfer column.");
        }
        using SqliteCommand definition = Command(c, tx,
            "SELECT sql FROM main.sqlite_master WHERE type='table' AND name=$name COLLATE NOCASE");
        definition.Parameters.AddWithValue("$name", name);
        if (definition.ExecuteScalar() is not string sql
            || MediaTransferNormalizeSql10(sql) != MediaTransferNormalizeSql10(MediaTransferDefinition10(table, name, legacy)))
            throw new InvalidDataException("Invalid media transfer schema definition.");
        MediaTransferValidateIndexes10(c, tx, name, table.Operation);
    }

    private static void MediaTransferValidateIndexes10(SqliteConnection c, SqliteTransaction? tx,
        string table, bool operation)
    {
        Dictionary<string, string> expected = new(StringComparer.Ordinal) { ["pk"] = "operation_id" };
        if (operation) expected.Add("u", "operations_parent");
        List<(string Name, string Column)> indexes = new();
        using (SqliteCommand command = Command(c, tx, $"PRAGMA main.index_list({table})"))
        using (SqliteDataReader reader = command.ExecuteReader())
            while (reader.Read())
            {
                if (reader.GetInt32(2) != 1 || reader.GetInt32(4) != 0
                    || !expected.Remove(reader.GetString(3), out string? column))
                    throw new InvalidDataException("Unexpected media transfer index definition.");
                indexes.Add((reader.GetString(1), column));
            }
        if (expected.Count != 0) throw new InvalidDataException("Missing media transfer identity index.");
        foreach (var index in indexes)
        {
            string quoted = "\"" + index.Name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
            using SqliteCommand command = Command(c, tx, $"PRAGMA main.index_xinfo({quoted})");
            using SqliteDataReader reader = command.ExecuteReader();
            int keys = 0;
            while (reader.Read())
            {
                if (reader.GetInt32(5) == 0) continue; // implicit rowid payload
                if (++keys != 1 || reader.IsDBNull(2) || reader.GetString(2) != index.Column
                    || reader.GetInt32(3) != 0 || reader.GetString(4) != "BINARY")
                    throw new InvalidDataException("Invalid media transfer identity index.");
            }
            if (keys != 1) throw new InvalidDataException("Missing media transfer identity key.");
        }
    }

    private static void MediaTransferValidateCopy10(SqliteConnection c, SqliteTransaction tx, MediaTransferTable10 table)
    {
        string original = "main." + table.Name;
        string shadow = original + "_schema10";
        string equal = string.Join(" AND ", table.ColumnNames.Split(',').Select(n =>
            $"(typeof(o.{n})=typeof(s.{n}) AND o.{n} IS s.{n}"
            + $" AND (typeof(o.{n}) NOT IN ('text','blob') OR CAST(o.{n} AS BLOB) IS CAST(s.{n} AS BLOB)))"));
        using SqliteCommand verify = Command(c, tx, $"""
            SELECT (SELECT count(*) FROM {original})<>(SELECT count(*) FROM {shadow})
                OR EXISTS (SELECT 1 FROM {original} o LEFT JOIN {shadow} s ON s.rowid=o.rowid
                    WHERE s.rowid IS NULL OR NOT ({equal}))
            """);
        if (Convert.ToInt64(verify.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
            throw new InvalidDataException("Media transfer copy did not preserve every row and storage value.");
        List<string> defaults = new() { "plan_version=1 AND typeof(plan_version)='integer'" };
        defaults.AddRange(MediaTransferCounters10(table.Import).Select(n => $"{n}=0 AND typeof({n})='integer'"));
        if (table.Operation)
        {
            defaults.Add("full_selection_json IS NULL AND full_selection_sha256 IS NULL AND full_inventory_sha256 IS NULL"
                + " AND full_member_count=0 AND typeof(full_member_count)='integer'");
            defaults.Add(table.Import ? "catalog_bytes IS NULL"
                : "safe_selection_json IS NULL AND safe_selection_sha256 IS NULL AND safe_inventory_sha256 IS NULL"
                    + " AND safe_member_count=0 AND typeof(safe_member_count)='integer'");
        }
        using SqliteCommand newValues = Command(c, tx,
            $"SELECT 1 FROM {shadow} WHERE NOT ({string.Join(" AND ", defaults)}) LIMIT 1");
        if (newValues.ExecuteScalar() != null)
            throw new InvalidDataException("Media transfer copy has nonlegacy extension values.");
    }

    private static void MediaTransferRejectIncomingForeignKeys10(SqliteConnection c, SqliteTransaction tx,
        MediaTransferTable10[] tables, CancellationToken cancellationToken)
    {
        HashSet<string> replaced = tables.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // DROP can otherwise run cascades in an unrelated user table. Reject the dependency;
        // never disable foreign_keys or delete/repair foreign objects during this migration.
        foreach (string schema in new[] { "main", "temp" })
        {
            List<string> names = new();
            using (SqliteCommand command = Command(c, tx, $"SELECT name FROM {schema}.sqlite_master WHERE type='table'"))
            using (SqliteDataReader reader = command.ExecuteReader())
                while (reader.Read()) names.Add(reader.GetString(0));
            foreach (string name in names)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string quoted = "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
                using SqliteCommand command = Command(c, tx, $"PRAGMA {schema}.foreign_key_list({quoted})");
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                    if (replaced.Contains(reader.GetString(2)))
                        throw new InvalidDataException("Unexpected foreign key to a media transfer table.");
            }
        }
    }

    // Tokenize rather than remove quotes/whitespace globally: literal contents, doubled
    // quotes and token boundaries must survive comparison. SQLite quotes the table name
    // after RENAME; unquoting only identifier tokens accepts that without weakening CHECKs.
    private static string MediaTransferNormalizeSql10(string sql)
    {
        HashSet<string> identifiers = new(StringComparer.OrdinalIgnoreCase);
        foreach (MediaTransferTable10 table in MediaTransferTables10())
        {
            identifiers.Add(table.Name);
            identifiers.Add(table.Name + "_schema10");
            identifiers.UnionWith(table.ColumnNames.Split(','));
            identifiers.UnionWith(MediaTransferCounters10(table.Import));
        }
        identifiers.UnionWith(new[] { "plan_version", "full_selection_json", "full_selection_sha256",
            "full_inventory_sha256", "full_member_count", "safe_selection_json", "safe_selection_sha256",
            "safe_inventory_sha256", "safe_member_count" });
        List<string> tokens = new();
        for (int i = 0; i < sql.Length;)
        {
            char ch = sql[i];
            if (char.IsWhiteSpace(ch)) { i++; continue; }
            if (ch is '\'' or '"' or '`' or '[')
            {
                char end = ch == '[' ? ']' : ch;
                StringBuilder value = new();
                bool closed = false;
                i++;
                while (i < sql.Length)
                {
                    char current = sql[i++];
                    if (current != end) { value.Append(current); continue; }
                    if (end != ']' && i < sql.Length && sql[i] == end)
                    { value.Append(end); i++; continue; }
                    closed = true;
                    break;
                }
                if (!closed) throw new InvalidDataException("Invalid quoted media transfer schema token.");
                // Only known identifiers may lose quoting. In particular SQLite's DQS
                // fallback must not make a quoted "64" equivalent to the numeric token 64.
                string text = value.ToString();
                tokens.Add(ch == '\'' ? "s:" + text : identifiers.Contains(text)
                    ? "w:" + text.ToLowerInvariant() : "q:" + ch + text);
                continue;
            }
            if (char.IsLetterOrDigit(ch) || ch is '_' or '$')
            {
                int start = i++;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '$')) i++;
                tokens.Add("w:" + sql[start..i].ToLowerInvariant());
                continue;
            }
            // Comments and unfamiliar syntax deliberately remain distinct, never erased.
            tokens.Add("p:" + ch);
            i++;
        }
        if (tokens.Count > 0 && tokens[^1] == "p:;") tokens.RemoveAt(tokens.Count - 1);
        // sqlite_master normally omits IF NOT EXISTS; accept it only at this exact prefix.
        if (tokens.Count >= 5 && tokens[0] == "w:create" && tokens[1] == "w:table"
            && tokens[2] == "w:if" && tokens[3] == "w:not" && tokens[4] == "w:exists")
            tokens.RemoveRange(2, 3);
        // Length-delimit tokens: user strings cannot forge a token separator.
        return string.Concat(tokens.Select(t => t.Length.ToString(CultureInfo.InvariantCulture) + ":" + t));
    }
}
