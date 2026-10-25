using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using MeowChan.Interfaces;

namespace MeowChan.Archive;

internal static partial class ArchiveSql
{
    internal const int SchemaVersion = 5;
    internal const int ApplicationId = 0x4F43484E;

    internal static SqliteConnection OpenConnection(string path, SqliteOpenMode mode)
    {
        SqliteConnection connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = mode, Cache = SqliteCacheMode.Private, Pooling = false,
            DefaultTimeout = 5, ForeignKeys = true
        }.ToString());
        try
        {
            connection.Open();
            RegisterSearchFunctions(connection);
            // The local catalog uses SQLite's rollback journal, so passive readers never need
            // to create WAL shared-memory files. All SQLite calls execute on a worker thread.
            using SqliteCommand pragma = connection.CreateCommand();
            pragma.CommandText = mode == SqliteOpenMode.ReadOnly
                ? "PRAGMA busy_timeout=5000;" : "PRAGMA busy_timeout=5000; PRAGMA synchronous=FULL;";
            pragma.ExecuteNonQuery();
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    internal static int GetPragma(SqliteConnection connection, string name)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA " + name;
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    internal static void ValidateSchema(SqliteConnection connection)
    {
        if (GetPragma(connection, "user_version") is not (1 or 2 or 3 or 4 or SchemaVersion) || GetPragma(connection, "application_id") != ApplicationId)
            throw new InvalidDataException("The archive database has an unsupported schema or belongs to another application.");
        using SqliteCommand journal = connection.CreateCommand();
        journal.CommandText = "PRAGMA journal_mode";
        if (!string.Equals(Convert.ToString(journal.ExecuteScalar(), CultureInfo.InvariantCulture), "delete", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The archive database must use its supported local rollback-journal mode.");
        using SqliteCommand columns = connection.CreateCommand();
        columns.CommandText = """
            SELECT source,board,thread,url,title,media_directory,source_state,capture_enabled,first_seen_at,last_captured_at,revision FROM archive_threads LIMIT 0;
            SELECT source,board,thread,post,ordinal,fields,posted_at,subject,author,html,is_deleted FROM archive_posts LIMIT 0;
            SELECT source,board,thread,post,position,media_id,variant,url,original_name,planned_path,expected_size,expected_md5,is_deleted FROM archive_attachments LIMIT 0;
            SELECT source,board,thread,directory FROM archive_media_directories LIMIT 0;
            SELECT source_id,board_id,thread_id,media_id,variant,download_uri,destination_path,expected_size,expected_md5,original_filename,legacy_path,extension,selection_json,decided_at_utc FROM media_decisions LIMIT 0;
            """;
        using SqliteDataReader reader = columns.ExecuteReader();
        while (reader.NextResult()) { }
        reader.Close();
        if (GetPragma(connection, "user_version") >= 2)
        {
            using SqliteCommand indexed = connection.CreateCommand();
            indexed.CommandText = """
                SELECT search_text FROM archive_threads LIMIT 0;
                SELECT search_text FROM archive_posts LIMIT 0;
                SELECT rowid,search_text FROM archive_thread_search LIMIT 0;
                SELECT rowid,search_text FROM archive_post_search LIMIT 0;
                SELECT source,board,thread,post,position,target_source,target_board,target_thread,target_post,label,url,is_external FROM archive_quotes LIMIT 0;
                """;
            using SqliteDataReader indexReader = indexed.ExecuteReader();
            while (indexReader.NextResult()) { }
        }
        if (GetPragma(connection, "user_version") >= 3)
        {
            using SqliteCommand sourceContract = connection.CreateCommand();
            sourceContract.CommandText = "SELECT source,board,thread,decimal_post_order FROM archive_source_contracts LIMIT 0";
            using SqliteDataReader sourceContractReader = sourceContract.ExecuteReader();
        }
        if (GetPragma(connection, "user_version") >= 5)
        {
            using SqliteCommand extended = connection.CreateCommand();
            extended.CommandText = "SELECT expected_sha1,provider_size_hint_json FROM archive_attachments LIMIT 0; "
                + "SELECT expected_sha1,provider_size_hint_json FROM media_decisions LIMIT 0;";
            extended.ExecuteNonQuery();
        }
        if (GetPragma(connection, "user_version") >= 4)
        {
            using SqliteCommand integrity = connection.CreateCommand();
            integrity.CommandText = "SELECT expected_sha256,attachment_revision_key FROM archive_attachments LIMIT 0; "
                + "SELECT expected_sha256,acquisition_revision_key,attachment_references_json FROM media_decisions LIMIT 0;";
            using SqliteDataReader integrityReader = integrity.ExecuteReader();
            while (integrityReader.NextResult()) { }
        }
    }

    internal static void CreateContentSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        using SqliteCommand command = Command(connection, transaction, """
            CREATE TABLE archive_threads (
                source TEXT NOT NULL, board TEXT NOT NULL, thread TEXT NOT NULL,
                url TEXT NOT NULL, title TEXT NOT NULL, media_directory TEXT NOT NULL,
                source_state INTEGER NOT NULL CHECK(source_state BETWEEN 0 AND 2),
                capture_enabled INTEGER NOT NULL CHECK(capture_enabled IN (0,1)),
                first_seen_at TEXT NOT NULL, last_captured_at TEXT, revision INTEGER NOT NULL, search_text TEXT NOT NULL DEFAULT '',
                PRIMARY KEY(source,board,thread));
            CREATE TABLE archive_posts (
                source TEXT NOT NULL, board TEXT NOT NULL, thread TEXT NOT NULL, post TEXT NOT NULL,
                ordinal INTEGER NOT NULL CHECK(ordinal>=0), fields INTEGER NOT NULL,
                posted_at TEXT, subject TEXT, author TEXT, html TEXT, search_text TEXT NOT NULL DEFAULT '',
                is_deleted INTEGER NOT NULL CHECK(is_deleted IN (0,1)),
                PRIMARY KEY(source,board,thread,post),
                FOREIGN KEY(source,board,thread) REFERENCES archive_threads(source,board,thread));
            CREATE TABLE archive_media_directories (
                source TEXT NOT NULL, board TEXT NOT NULL, thread TEXT NOT NULL, directory TEXT NOT NULL,
                PRIMARY KEY(source,board,thread,directory),
                FOREIGN KEY(source,board,thread) REFERENCES archive_threads(source,board,thread));
            CREATE TABLE archive_attachments (
                source TEXT NOT NULL, board TEXT NOT NULL, thread TEXT NOT NULL, post TEXT NOT NULL,
                position INTEGER NOT NULL CHECK(position>=0), media_id TEXT, variant TEXT NOT NULL,
                url TEXT, original_name TEXT, planned_path TEXT, expected_size INTEGER CHECK(expected_size>=0),
                expected_md5 TEXT, is_deleted INTEGER NOT NULL CHECK(is_deleted IN (0,1)),
                PRIMARY KEY(source,board,thread,post,position),
                FOREIGN KEY(source,board,thread,post) REFERENCES archive_posts(source,board,thread,post));
            CREATE INDEX archive_thread_order ON archive_threads(last_captured_at DESC,source,board,thread);
            CREATE INDEX archive_post_order ON archive_posts(source,board,thread,ordinal,post);
            """);
        command.ExecuteNonQuery();
    }

    internal static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    internal static void AddId(SqliteCommand command, ResourceId id)
    {
        command.Parameters.AddWithValue("$source", id.Source);
        command.Parameters.AddWithValue("$board", id.Board);
        command.Parameters.AddWithValue("$thread", id.Thread!);
    }

    internal static string Date(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseDate(string value) => DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture);
    private static string? NullableString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private const string SummaryColumns = """
        t.source,t.board,t.thread,t.url,t.title,t.media_directory,t.source_state,t.capture_enabled,
        t.first_seen_at,t.last_captured_at,t.revision,
        (SELECT count(*) FROM archive_posts p WHERE p.source=t.source AND p.board=t.board AND p.thread=t.thread)
        """;

    internal static ArchiveThreadSummary? ReadSummary(SqliteConnection connection, SqliteTransaction? transaction, ResourceId id)
    {
        using SqliteCommand command = Command(connection, transaction,
            $"SELECT {SummaryColumns} FROM archive_threads t WHERE t.source=$source AND t.board=$board AND t.thread=$thread");
        AddId(command, id);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadSummaryRow(reader) : null;
    }

    internal static ArchiveThreadPage ReadPage(SqliteConnection connection, SqliteTransaction transaction, int offset, int limit)
    {
        using SqliteCommand total = Command(connection, transaction, "SELECT count(*) FROM archive_threads");
        int count = checked(Convert.ToInt32(total.ExecuteScalar(), CultureInfo.InvariantCulture));
        using SqliteCommand command = Command(connection, transaction,
            $"SELECT {SummaryColumns} FROM archive_threads t ORDER BY COALESCE(t.last_captured_at,t.first_seen_at) DESC,t.source,t.board,t.thread LIMIT $limit OFFSET $offset");
        command.Parameters.AddWithValue("$offset", offset);
        command.Parameters.AddWithValue("$limit", limit);
        using SqliteDataReader reader = command.ExecuteReader();
        List<ArchiveThreadSummary> items = new List<ArchiveThreadSummary>();
        while (reader.Read()) items.Add(ReadSummaryRow(reader));
        return new ArchiveThreadPage(Array.AsReadOnly(items.ToArray()), count, offset);
    }

    private static ArchiveThreadSummary ReadSummaryRow(SqliteDataReader reader)
    {
        ThreadSourceState state = (ThreadSourceState)reader.GetInt32(6);
        int enabled = reader.GetInt32(7);
        if (!Enum.IsDefined(state) || enabled is < 0 or > 1)
            throw new InvalidDataException("The archive contains an invalid thread state.");
        return new ArchiveThreadSummary(new ResourceId(reader.GetString(0), reader.GetString(1), reader.GetString(2)),
            reader.GetString(3), reader.GetString(4), reader.GetString(5), state, enabled == 1,
            ParseDate(reader.GetString(8)), reader.IsDBNull(9) ? null : ParseDate(reader.GetString(9)),
            reader.GetInt32(11), reader.GetInt64(10));
    }

    internal static ArchivedThreadView? ReadThread(SqliteConnection connection, SqliteTransaction transaction, ResourceId id)
    {
        ArchiveThreadSummary? summary = ReadSummary(connection, transaction, id);
        if (summary == null) return null;
        Dictionary<string, List<CapturedAttachment>> attachments = new Dictionary<string, List<CapturedAttachment>>(StringComparer.Ordinal);
        using (SqliteCommand command = Command(connection, transaction, """
            SELECT post,position,media_id,variant,url,original_name,planned_path,expected_size,expected_md5,is_deleted
            """ + AttachmentIntegrityColumns(connection) + "\n" + """
            FROM archive_attachments WHERE source=$source AND board=$board AND thread=$thread ORDER BY post,position
            """))
        {
            AddId(command, id);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                string post = reader.GetString(0);
                if (!attachments.TryGetValue(post, out List<CapturedAttachment>? list))
                    attachments.Add(post, list = new List<CapturedAttachment>());
                list.Add(new CapturedAttachment(reader.GetInt32(1), NullableString(reader, 2), reader.GetString(3),
                    NullableString(reader, 4), NullableString(reader, 5), NullableString(reader, 6),
                    reader.IsDBNull(7) ? null : reader.GetInt64(7), NullableString(reader, 8), reader.GetInt32(9) == 1)
                    { ExpectedSha256 = NullableString(reader, 10), AttachmentRevisionKey = NullableString(reader, 11),
                        ExpectedSha1 = NullableString(reader, 12), ProviderSizeHint = ReadSizeHint(NullableString(reader, 13)) });
            }
        }
        using SqliteCommand postsCommand = Command(connection, transaction, """
            SELECT post,ordinal,fields,posted_at,subject,author,html,is_deleted
            FROM archive_posts WHERE source=$source AND board=$board AND thread=$thread ORDER BY ordinal,post
            """);
        AddId(postsCommand, id);
        using SqliteDataReader postsReader = postsCommand.ExecuteReader();
        List<CapturedPost> posts = new List<CapturedPost>();
        while (postsReader.Read())
        {
            string post = postsReader.GetString(0);
            PostFieldPresence fields = (PostFieldPresence)postsReader.GetInt32(2);
            if ((fields & ~PostFieldPresence.All) != 0) throw new InvalidDataException("The archive contains invalid post field flags.");
            posts.Add(new CapturedPost(post, postsReader.GetInt64(1), fields,
                postsReader.IsDBNull(3) ? null : ParseDate(postsReader.GetString(3)), NullableString(postsReader, 4),
                NullableString(postsReader, 5), NullableString(postsReader, 6), postsReader.GetInt32(7) == 1,
                attachments.TryGetValue(post, out List<CapturedAttachment>? list)
                    ? Array.AsReadOnly(list.ToArray()) : Array.Empty<CapturedAttachment>()));
        }
        return new ArchivedThreadView(summary, posts, ReadMediaDirectories(connection, transaction, id),
            ReadPostOrder(connection, transaction, id));
    }

    internal static IReadOnlyList<string> ReadMediaDirectories(SqliteConnection connection, SqliteTransaction transaction, ResourceId id)
    {
        using SqliteCommand command = Command(connection, transaction,
            "SELECT directory FROM archive_media_directories WHERE source=$source AND board=$board AND thread=$thread ORDER BY directory");
        AddId(command, id);
        using SqliteDataReader reader = command.ExecuteReader();
        List<string> directories = new List<string>();
        while (reader.Read()) directories.Add(reader.GetString(0));
        return Array.AsReadOnly(directories.ToArray());
    }
}
