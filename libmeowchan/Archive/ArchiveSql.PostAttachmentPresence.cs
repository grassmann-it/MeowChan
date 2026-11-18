using System;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace MeowChan.Archive;

internal static partial class ArchiveSql
{
    // Schema 11 records the distinction between an explicitly null attachment field and an
    // observed empty collection without manufacturing attachment rows. Zero is the legacy and
    // non-null shape; the column is deliberately part of archive_posts so every content write
    // and its post row remain in the same transaction.
    internal static void MigratePostAttachmentPresenceSchema11(SqliteConnection connection,
        SqliteTransaction transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (!ReferenceEquals(transaction.Connection, connection))
            throw new InvalidOperationException("Schema 11 migration requires the caller's active transaction.");
        cancellationToken.ThrowIfCancellationRequested();
        int version = GetPragma(connection, "user_version", transaction);
        if (version >= 11 && version <= SchemaVersion)
        {
            ValidatePostAttachmentPresenceSchema11(connection, transaction);
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }
        if (version != 10)
            throw new InvalidDataException("Post attachment presence requires schema 10 as its migration source.");

        if (GetPragma(connection, "ignore_check_constraints", transaction) != 0)
            throw new InvalidDataException("Post attachment presence migration requires enabled CHECK constraints.");

        // Validate the complete schema10 shape before changing archive_posts. The caller owns
        // the immediate transaction; a failed ALTER or validation therefore rolls back with the
        // catalog's version stamp.
        ValidateTargetSchema(connection, transaction, 10);
        cancellationToken.ThrowIfCancellationRequested();
        using (SqliteCommand alter = Command(connection, transaction, """
            ALTER TABLE main.archive_posts
            ADD COLUMN attachments_were_null INTEGER NOT NULL DEFAULT 0
                CHECK(attachments_were_null IN (0,1));
            """))
            alter.ExecuteNonQuery();
        cancellationToken.ThrowIfCancellationRequested();
        ValidatePostAttachmentPresenceSchema11(connection, transaction);
        cancellationToken.ThrowIfCancellationRequested();
    }

    internal static void ValidatePostAttachmentPresenceSchema11(SqliteConnection connection,
        SqliteTransaction? transaction = null)
    {
        using (SqliteCommand columns = Command(connection, transaction, "PRAGMA main.table_xinfo(archive_posts)"))
        using (SqliteDataReader reader = columns.ExecuteReader())
        {
            bool found = false;
            while (reader.Read())
            {
                // table_xinfo: cid, name, type, notnull, default, pk, hidden.
                if (reader.GetInt32(6) != 0) continue;
                if (reader.GetString(1) != "attachments_were_null") continue;
                if (found || reader.GetString(2) != "INTEGER" || reader.GetInt32(3) != 1
                    || reader.IsDBNull(4) || reader.GetString(4) != "0" || reader.GetInt32(5) != 0)
                    throw new InvalidDataException("The archive has an invalid post attachment presence column.");
                found = true;
            }
            if (!found) throw new InvalidDataException("The archive is missing its post attachment presence column.");
        }
        using SqliteCommand definition = Command(connection, transaction, """
            SELECT sql FROM main.sqlite_master WHERE type='table' AND name='archive_posts'
            """);
        string sql = Convert.ToString(definition.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? "";
        string normalized = string.Concat(sql.Where(character => !char.IsWhiteSpace(character)))
            .ToUpperInvariant();
        if (!normalized.Contains("ATTACHMENTS_WERE_NULLINTEGERNOTNULLDEFAULT0CHECK(ATTACHMENTS_WERE_NULLIN(0,1))",
            StringComparison.Ordinal))
            throw new InvalidDataException("The archive has an invalid post attachment presence constraint.");
        using SqliteCommand readable = Command(connection, transaction,
            "SELECT attachments_were_null FROM archive_posts LIMIT 0");
        readable.ExecuteNonQuery();
    }

    // Older read-only/schema-only callers can still project the old shape as non-null. A normal
    // catalog is schema11 by the time ReadThread is reached, so a missing column is not repaired
    // or silently upgraded here.
    internal static string PostAttachmentPresenceColumn(SqliteConnection connection, SqliteTransaction? transaction)
        => GetPragma(connection, "user_version", transaction) >= 11 ? "attachments_were_null" : "0";

    internal static bool ReadPostAttachmentPresence(SqliteDataReader reader, int ordinal,
        PostFieldPresence fields, bool hasAttachmentRows)
    {
        if (reader.GetValue(ordinal) is not long marker || marker is < 0 or > 1)
            throw new InvalidDataException("The archive contains an invalid post attachment presence marker.");
        if (marker == 1)
        {
            if ((fields & PostFieldPresence.Attachments) == 0 || hasAttachmentRows)
                throw new InvalidDataException("The archive contains an inconsistent null attachment presence marker.");
            return true;
        }
        return false;
    }

    internal static void WritePostAttachmentPresence(SqliteConnection connection,
        SqliteTransaction transaction, ResourceId id, CapturedPost post)
    {
        if ((post.Fields & PostFieldPresence.Attachments) == 0) return;
        // A scalar-only merge can carry a null observation alongside retained SQL attachments.
        // It must neither erase those rows nor mark their non-null collection as null.
        using SqliteCommand command = Command(connection, transaction, """
            UPDATE archive_posts SET attachments_were_null=CASE WHEN $null=1 AND NOT EXISTS (
                SELECT 1 FROM archive_attachments
                WHERE source=$source AND board=$board AND thread=$thread AND post=$post
            ) THEN 1 ELSE 0 END
            WHERE source=$source AND board=$board AND thread=$thread AND post=$post
            """);
        AddId(command, id);
        command.Parameters.AddWithValue("$post", post.PostId);
        command.Parameters.AddWithValue("$null", post.Attachments == null ? 1 : 0);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidDataException("The archive post disappeared while writing attachment presence.");
    }

    internal static void ClearPostAttachmentPresence(SqliteConnection connection,
        SqliteTransaction transaction, ResourceId id, string postId)
    {
        using SqliteCommand command = Command(connection, transaction, """
            UPDATE archive_posts SET attachments_were_null=0
            WHERE source=$source AND board=$board AND thread=$thread AND post=$post
            """);
        AddId(command, id);
        command.Parameters.AddWithValue("$post", postId);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidDataException("The archive post disappeared while clearing attachment presence.");
    }
}
