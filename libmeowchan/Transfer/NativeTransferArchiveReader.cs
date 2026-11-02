using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace MeowChan.Archive;

public sealed partial class ArchiveReader
{
    // The transfer-specific materialization bound lives in the SAME read transaction.
    // It does not impose new limits on ordinary archive reading or change the schema.
    internal Task<ArchivedThreadView?> ReadNativeTransferAsync(ResourceId id, CancellationToken token)
    {
        ArchiveValidation.ValidateId(id);
        return ReadAsync((connection, transaction) =>
        {
            foreach (string table in new[] { "archive_posts", "archive_attachments", "archive_media_directories" })
            {
                token.ThrowIfCancellationRequested();
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "SELECT COUNT(*) FROM " + table + " WHERE source=$source AND board=$board AND thread=$thread";
                command.Parameters.AddWithValue("$source", id.Source);
                command.Parameters.AddWithValue("$board", id.Board);
                command.Parameters.AddWithValue("$thread", id.Thread);
                if (Convert.ToInt64(command.ExecuteScalar()) > (table == "archive_media_directories" ? 32 : 100_000))
                    throw new InvalidDataException("The selected archive exceeds native transfer limits.");
            }
            // Bound aggregate stored text INCLUDING nonportable local paths before strings
            // are materialized. Optional integrity columns are discovered read-only so old
            // supported schemas keep working without migration or guessed defaults.
            long textBytes = 0;
            foreach ((string table, string[] candidates) in new (string, string[])[]
            {
                ("archive_threads", ["source", "board", "thread", "url", "title", "media_directory", "first_seen_at", "last_captured_at"]),
                ("archive_posts", ["post", "posted_at", "subject", "author", "html"]),
                ("archive_attachments", ["post", "media_id", "variant", "url", "original_name", "planned_path", "expected_md5", "expected_sha256", "attachment_revision_key", "expected_sha1", "provider_size_hint_json"]),
                ("archive_media_directories", ["directory"])
            })
            {
                token.ThrowIfCancellationRequested();
                HashSet<string> columns = new(StringComparer.Ordinal);
                using (SqliteCommand info = connection.CreateCommand())
                {
                    info.Transaction = transaction; info.CommandText = "PRAGMA table_info(" + table + ")";
                    using SqliteDataReader schema = info.ExecuteReader();
                    while (schema.Read()) columns.Add(schema.GetString(1));
                }
                using SqliteCommand sizes = connection.CreateCommand(); sizes.Transaction = transaction;
                sizes.CommandText = "SELECT COALESCE(SUM(" + string.Join(" + ", candidates.Where(columns.Contains)
                    .Select(column => "COALESCE(length(CAST(" + column + " AS BLOB)),0)"))
                    + "),0) FROM " + table + " WHERE source=$source AND board=$board AND thread=$thread";
                sizes.Parameters.AddWithValue("$source", id.Source); sizes.Parameters.AddWithValue("$board", id.Board); sizes.Parameters.AddWithValue("$thread", id.Thread);
                textBytes = checked(textBytes + Convert.ToInt64(sizes.ExecuteScalar()));
                if (textBytes > 32L * 1024 * 1024) throw new InvalidDataException("The selected archive exceeds native transfer limits.");
            }
            token.ThrowIfCancellationRequested();
            return ArchiveSql.ReadThread(connection, transaction, id);
        }, token);
    }
}
