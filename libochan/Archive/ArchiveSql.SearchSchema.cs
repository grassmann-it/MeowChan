using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace oChan.Archive;

internal static partial class ArchiveSql
{
    internal static void RegisterSearchFunctions(SqliteConnection connection)
    {
        connection.CreateFunction<string?, string>("ochan_text", value => ArchiveText.PlainText(value), isDeterministic: true);
    }

    internal static void CreateSearchSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        using SqliteCommand schema = Command(connection, transaction, """
            CREATE VIRTUAL TABLE archive_thread_search USING fts5(search_text, content='archive_threads', content_rowid='rowid', tokenize='trigram');
            CREATE VIRTUAL TABLE archive_post_search USING fts5(search_text, content='archive_posts', content_rowid='rowid', tokenize='trigram');
            CREATE TABLE archive_quotes (
                source TEXT NOT NULL, board TEXT NOT NULL, thread TEXT NOT NULL, post TEXT NOT NULL, position INTEGER NOT NULL,
                target_source TEXT, target_board TEXT, target_thread TEXT, target_post TEXT, label TEXT NOT NULL, url TEXT,
                is_external INTEGER NOT NULL CHECK(is_external IN (0,1)),
                PRIMARY KEY(source,board,thread,post,position),
                FOREIGN KEY(source,board,thread,post) REFERENCES archive_posts(source,board,thread,post) ON DELETE CASCADE);
            CREATE INDEX archive_quote_target ON archive_quotes(target_source,target_board,target_thread,target_post);

            CREATE TRIGGER archive_thread_search_insert AFTER INSERT ON archive_threads BEGIN
                INSERT INTO archive_thread_search(rowid,search_text) VALUES(new.rowid,new.search_text);
            END;
            CREATE TRIGGER archive_thread_search_update AFTER UPDATE OF search_text ON archive_threads WHEN old.search_text IS NOT new.search_text BEGIN
                INSERT INTO archive_thread_search(archive_thread_search,rowid,search_text) VALUES('delete',old.rowid,old.search_text);
                INSERT INTO archive_thread_search(rowid,search_text) VALUES(new.rowid,new.search_text);
            END;
            CREATE TRIGGER archive_thread_search_delete AFTER DELETE ON archive_threads BEGIN
                INSERT INTO archive_thread_search(archive_thread_search,rowid,search_text) VALUES('delete',old.rowid,old.search_text);
            END;

            CREATE TRIGGER archive_post_search_insert AFTER INSERT ON archive_posts BEGIN
                INSERT INTO archive_post_search(rowid,search_text) VALUES(new.rowid,new.search_text);
            END;
            CREATE TRIGGER archive_post_search_update AFTER UPDATE OF search_text ON archive_posts WHEN old.search_text IS NOT new.search_text BEGIN
                INSERT INTO archive_post_search(archive_post_search,rowid,search_text) VALUES('delete',old.rowid,old.search_text);
                INSERT INTO archive_post_search(rowid,search_text) VALUES(new.rowid,new.search_text);
            END;
            CREATE TRIGGER archive_post_search_delete AFTER DELETE ON archive_posts BEGIN
                INSERT INTO archive_post_search(archive_post_search,rowid,search_text) VALUES('delete',old.rowid,old.search_text);
            END;
            """);
        schema.ExecuteNonQuery();
    }

    internal static void MigrateSearchSchema(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        using SqliteCommand columns = Command(connection, transaction, """
            ALTER TABLE archive_threads ADD COLUMN search_text TEXT NOT NULL DEFAULT '';
            ALTER TABLE archive_posts ADD COLUMN search_text TEXT NOT NULL DEFAULT '';
            """);
        columns.ExecuteNonQuery();
        // The old source fields, retained posts, media decisions and capture preferences remain intact.
        using SqliteCommand fill = Command(connection, transaction, """
            UPDATE archive_threads SET search_text=source||char(10)||board||char(10)||thread||char(10)||ochan_text(title);
            UPDATE archive_posts SET search_text=post||char(10)||ochan_text(subject)||char(10)||ochan_text(author)||char(10)||ochan_text(html);
            """);
        fill.ExecuteNonQuery();
        CreateSearchSchema(connection, transaction);
        using SqliteCommand rebuild = Command(connection, transaction, """
            INSERT INTO archive_thread_search(archive_thread_search) VALUES('rebuild');
            INSERT INTO archive_post_search(archive_post_search) VALUES('rebuild');
            """);
        rebuild.ExecuteNonQuery();
        List<ResourceId> ids = new List<ResourceId>();
        using (SqliteCommand list = Command(connection, transaction, "SELECT source,board,thread FROM archive_threads"))
        using (SqliteDataReader rows = list.ExecuteReader())
            while (rows.Read()) ids.Add(new ResourceId(rows.GetString(0), rows.GetString(1), rows.GetString(2)));
        foreach (ResourceId id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArchivedThreadView view = ReadThread(connection, transaction, id)!;
            foreach (CapturedPost post in view.Posts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteQuotes(connection, transaction, view.Summary, post);
            }
        }
        using SqliteCommand version = Command(connection, transaction, $"PRAGMA user_version={SchemaVersion}");
        version.ExecuteNonQuery();
    }

    internal static void WriteQuotes(SqliteConnection connection, SqliteTransaction transaction, ArchiveThreadSummary thread, CapturedPost post)
    {
        using SqliteCommand clear = Command(connection, transaction,
            "DELETE FROM archive_quotes WHERE source=$source AND board=$board AND thread=$thread AND post=$post");
        AddId(clear, thread.Id);
        clear.Parameters.AddWithValue("$post", post.PostId);
        clear.ExecuteNonQuery();
        IReadOnlyList<ArchiveQuote> quotes = ArchiveQuoteParser.Parse(thread.Id, thread.Url, post);
        for (int position = 0; position < quotes.Count; position++)
        {
            ArchiveQuote quote = quotes[position];
            using SqliteCommand insert = Command(connection, transaction, """
                INSERT INTO archive_quotes(source,board,thread,post,position,target_source,target_board,target_thread,target_post,label,url,is_external)
                VALUES($source,$board,$thread,$post,$position,$ts,$tb,$tt,$tp,$label,$url,$external)
                """);
            AddId(insert, thread.Id);
            insert.Parameters.AddWithValue("$post", post.PostId);
            insert.Parameters.AddWithValue("$position", position);
            insert.Parameters.AddWithValue("$ts", (object?)quote.Target?.Thread.Source ?? DBNull.Value);
            insert.Parameters.AddWithValue("$tb", (object?)quote.Target?.Thread.Board ?? DBNull.Value);
            insert.Parameters.AddWithValue("$tt", (object?)quote.Target?.Thread.Thread ?? DBNull.Value);
            insert.Parameters.AddWithValue("$tp", (object?)quote.Target?.PostId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$label", quote.Label);
            insert.Parameters.AddWithValue("$url", (object?)quote.Url ?? DBNull.Value);
            insert.Parameters.AddWithValue("$external", quote.IsExternal ? 1 : 0);
            insert.ExecuteNonQuery();
        }
    }
}
