using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace MeowChan.Archive;

internal static partial class ArchiveSql
{
    internal static void CreateIntegritySchema(SqliteConnection connection, SqliteTransaction transaction)
    {
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
        => GetPragma(connection, "user_version") >= 4 ? ",expected_sha256,attachment_revision_key" : ",NULL,NULL";

    internal static string CandidateIntegrityColumns(SqliteConnection connection)
        => GetPragma(connection, "user_version") >= 4
            ? ",expected_sha256,acquisition_revision_key,attachment_references_json" : ",NULL,NULL,NULL";
}
