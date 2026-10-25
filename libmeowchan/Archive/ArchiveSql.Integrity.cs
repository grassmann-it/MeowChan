using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MeowChan.Storage;
using Microsoft.Data.Sqlite;

namespace MeowChan.Archive;

internal static partial class ArchiveSql
{
    internal static void CreateIntegritySchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        AddColumn("archive_attachments", "expected_sha1");
        AddColumn("archive_attachments", "provider_size_hint_json");
        AddColumn("media_decisions", "expected_sha1");
        AddColumn("media_decisions", "provider_size_hint_json");
        AddColumn("archive_attachments", "expected_sha256");
        AddColumn("archive_attachments", "attachment_revision_key");
        AddColumn("media_decisions", "expected_sha256");
        AddColumn("media_decisions", "acquisition_revision_key");
        AddColumn("media_decisions", "attachment_references_json");

        void AddColumn(string table, string column)
        {
            HashSet<string> existing = new(StringComparer.Ordinal);
            using (SqliteCommand columns = Command(connection, transaction, "PRAGMA table_info(" + table + ")"))
            using (SqliteDataReader reader = columns.ExecuteReader())
                while (reader.Read()) existing.Add(reader.GetString(1));
            if (existing.Contains(column)) return;
            using SqliteCommand add = Command(connection, transaction, "ALTER TABLE " + table + " ADD COLUMN " + column + " TEXT");
            add.ExecuteNonQuery();
        }
    }

    internal static string AttachmentIntegrityColumns(SqliteConnection connection)
        => (GetPragma(connection, "user_version") >= 4 ? ",expected_sha256,attachment_revision_key" : ",NULL,NULL")
            + ExtensionIntegrityColumns(connection);

    internal static string CandidateIntegrityColumns(SqliteConnection connection)
        => (GetPragma(connection, "user_version") >= 4
            ? ",expected_sha256,acquisition_revision_key,attachment_references_json" : ",NULL,NULL,NULL")
            + ExtensionIntegrityColumns(connection);

    private static string ExtensionIntegrityColumns(SqliteConnection connection)
        => GetPragma(connection, "user_version") >= 5 ? ",expected_sha1,provider_size_hint_json" : ",NULL,NULL";

    internal static string? WriteSizeHint(MediaProviderSizeHint? hint)
    {
        MediaIntegrityValidation.ValidateSizeHint(hint);
        return hint == null ? null : JsonSerializer.Serialize(hint);
    }
    internal static MediaProviderSizeHint? ReadSizeHint(string? json)
    {
        if (json == null) return null;
        try
        {
            MediaProviderSizeHint hint = JsonSerializer.Deserialize<MediaProviderSizeHint>(json)
                ?? throw new InvalidDataException("A provider size hint contains null.");
            MediaIntegrityValidation.ValidateSizeHint(hint);
            return hint;
        }
        catch (JsonException error) { throw new InvalidDataException("Invalid provider size hint.", error); }
    }
}
