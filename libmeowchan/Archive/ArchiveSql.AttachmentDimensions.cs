using System;
using System.IO;
using System.Threading;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace MeowChan.Archive;

internal static partial class ArchiveSql
{
    // Schema 13 stores the provider's optional pixel dimensions next to the captured
    // attachment metadata. Null is intentional for old captures and non-image media.
    internal static void MigrateAttachmentDimensionsSchema13(SqliteConnection connection,
        SqliteTransaction transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (!ReferenceEquals(transaction.Connection, connection))
            throw new InvalidOperationException("Schema 13 migration requires the caller's active transaction.");
        cancellationToken.ThrowIfCancellationRequested();
        int version = GetPragma(connection, "user_version", transaction);
        if (version == 13)
        {
            ValidateAttachmentDimensionsSchema13(connection, transaction);
            return;
        }
        if (version != 12)
            throw new InvalidDataException("Attachment dimensions require schema 12 as their migration source.");
        ValidateTargetSchema(connection, transaction, 12);
        cancellationToken.ThrowIfCancellationRequested();
        if (!HasColumn(connection, transaction, "width") && !HasColumn(connection, transaction, "height"))
        {
            using SqliteCommand alter = Command(connection, transaction, """
                ALTER TABLE main.archive_attachments
                ADD COLUMN width INTEGER CHECK(width IS NULL OR width > 0);
                ALTER TABLE main.archive_attachments
                ADD COLUMN height INTEGER CHECK(height IS NULL OR height > 0);
                """);
            alter.ExecuteNonQuery();
        }
        else if (!HasColumn(connection, transaction, "width") || !HasColumn(connection, transaction, "height"))
            throw new InvalidDataException("The archive has only one attachment dimension column.");
        cancellationToken.ThrowIfCancellationRequested();
        ValidateAttachmentDimensionsSchema13(connection, transaction);
    }

    private static bool HasColumn(SqliteConnection connection, SqliteTransaction transaction, string name)
    {
        using SqliteCommand columns = Command(connection, transaction, "PRAGMA main.table_info(archive_attachments)");
        using SqliteDataReader reader = columns.ExecuteReader();
        while (reader.Read()) if (reader.GetString(1) == name) return true;
        return false;
    }

    internal static void ValidateAttachmentDimensionsSchema13(SqliteConnection connection,
        SqliteTransaction? transaction = null)
    {
        using SqliteCommand columns = Command(connection, transaction, "PRAGMA main.table_info(archive_attachments)");
        bool width = false, height = false;
        using (SqliteDataReader reader = columns.ExecuteReader())
        {
            while (reader.Read())
            {
                string name = reader.GetString(1);
                if (name is not ("width" or "height")) continue;
                if (reader.GetString(2) != "INTEGER" || reader.GetInt32(3) != 0 || reader.GetInt32(5) != 0)
                    throw new InvalidDataException("The archive has an invalid attachment dimension column.");
                if (name == "width") width = true; else height = true;
            }
        }
        if (!width || !height)
            throw new InvalidDataException("The archive is missing attachment dimension columns.");
        using SqliteCommand definition = Command(connection, transaction,
            "SELECT sql FROM main.sqlite_master WHERE type='table' AND name='archive_attachments'");
        string sql = Convert.ToString(definition.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? "";
        string normalized = string.Concat(sql.Where(character => !char.IsWhiteSpace(character))).ToUpperInvariant();
        if (!normalized.Contains("WIDTHINTEGERCHECK(WIDTHISNULLORWIDTH>0)", StringComparison.Ordinal)
            || !normalized.Contains("HEIGHTINTEGERCHECK(HEIGHTISNULLORHEIGHT>0)", StringComparison.Ordinal))
            throw new InvalidDataException("The archive has invalid attachment dimension constraints.");
        using SqliteCommand readable = Command(connection, transaction,
            "SELECT width,height FROM archive_attachments LIMIT 0");
        readable.ExecuteNonQuery();
    }
}
