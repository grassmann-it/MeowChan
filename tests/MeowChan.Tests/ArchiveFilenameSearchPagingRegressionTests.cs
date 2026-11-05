using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Persistence;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests;

public sealed class ArchiveFilenameSearchPagingRegressionTests
{
    private const string LiteralName = "same%_original.jpg";
    private const string WildcardDecoyName = "sameZXoriginal.jpg";

    [Fact]
    public async Task LargeMetadataCatalogHasExactDisjointOrderedPagesAndLiteralWildcardCounts()
    {
        using TempDirectory directory = new();
        ArchiveFilenameHit[] named = await SeedMetadata(directory.Path);
        ArchiveFilenameHit[] literal = named.Where(hit => hit.OriginalName == LiteralName).ToArray();
        ArchiveFilenameHit[] decoys = named.Where(hit => hit.OriginalName == WildcardDecoyName).ToArray();
        Assert.Equal(1152, named.Length); // 1224 attachments, including 72 unknown original names.
        Assert.Equal(1080, literal.Length); // More than two maximum-size pages, not a one-page fixture.
        Assert.Equal(72, decoys.Length);
        Assert.Equal(3, literal.Select(hit => hit.Thread.Source).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(18, literal.Select(hit => hit.Thread).Distinct().Count());
        Assert.Contains(literal, hit => hit.PostIsDeleted && hit.AttachmentIsDeleted);
        Assert.Contains(literal, hit => !hit.PostIsDeleted && !hit.AttachmentIsDeleted);
        Assert.Contains(literal, hit => hit.AttachmentRevisionKey == null);
        Assert.Contains(literal, hit => hit.AttachmentRevisionKey != null);
        Assert.Contains(literal, hit => hit.Variant == "preview");
        Assert.Contains(literal, hit => hit.Variant == "original");

        await using var reader = await ArchiveReader.OpenAsync(directory.Path);
        // An independent fixture oracle, not the query's own first page, defines the whole result.
        // 500 yields 500/500/80; 137 cuts within posts and at different source/thread boundaries.
        await AssertPages(reader, "SAME%_ORIGINAL", literal, 500);
        await AssertPages(reader, "SAME%_ORIGINAL", literal, 137);
        // Percent and underscore must stay literal even when a SQL-LIKE implementation would
        // expand each search to all named attachments, including the 72 ordinary-name decoys.
        foreach (string text in new[] { "%", "_", "%_" })
            await AssertPages(reader, text, literal, 500);
        await AssertPages(reader, WildcardDecoyName, decoys, 500);
        await AssertPages(reader, "", named, 500);

        // All 1224 planned paths contain this marker AND LiteralName; they are not name evidence.
        var pathOnly = await reader.SearchFilenamesAsync(new("path-only-marker"), limit: 500);
        Assert.Equal(0L, pathOnly.TotalCount);
        Assert.Empty(pathOnly.Items);
    }

    private static async Task AssertPages(ArchiveReader reader, string text,
        ArchiveFilenameHit[] expected, int limit)
    {
        var query = new ArchiveFilenameSearch(text);
        var seen = new HashSet<(string Source, string Board, string Thread, string Post, int Position)>();
        var union = new List<ArchiveFilenameHit>();
        for (int offset = 0; offset < expected.Length; offset += limit)
        {
            ArchiveFilenamePage page = await reader.SearchFilenamesAsync(query, offset, limit);
            Assert.Equal(expected.LongLength, page.TotalCount);
            Assert.Equal(offset, page.Offset);
            Assert.Equal(expected.Skip(offset).Take(limit).ToArray(), page.Items.ToArray());
            foreach (ArchiveFilenameHit hit in page.Items)
            {
                Assert.True(seen.Add(Identity(hit)), "An attachment identity occurred on more than one page.");
                union.Add(hit);
            }
        }
        Assert.Equal(expected.Length, seen.Count);
        Assert.Equal(expected, union.ToArray()); // Count equals the complete, ordered disjoint union.

        ArchiveFilenamePage end = await reader.SearchFilenamesAsync(query, expected.Length, limit);
        Assert.Equal(expected.LongLength, end.TotalCount);
        Assert.Equal(expected.Length, end.Offset);
        Assert.Empty(end.Items);

        int replayOffset = expected.Length > limit ? limit : 0;
        ArchiveFilenamePage replay = await reader.SearchFilenamesAsync(query, replayOffset, limit);
        Assert.Equal(expected.LongLength, replay.TotalCount);
        Assert.Equal(replayOffset, replay.Offset);
        Assert.Equal(expected.Skip(replayOffset).Take(limit).ToArray(), replay.Items.ToArray());
    }

    private static (string Source, string Board, string Thread, string Post, int Position) Identity(ArchiveFilenameHit hit)
        => (hit.Thread.Source, hit.Thread.Board, hit.Thread.Thread!, hit.Post, hit.Position);

    private static async Task<ArchiveFilenameHit[]> SeedMetadata(string profile)
    {
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using (var catalog = await ArchiveCatalog.OpenAsync(profile, lease)) { }
        using SqliteConnection connection = ArchiveSql.OpenConnection(Path.Combine(profile, "archive.db"), SqliteOpenMode.ReadWrite);
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand thread = ArchiveSql.Command(connection, transaction, """
            INSERT INTO archive_threads(source,board,thread,url,title,media_directory,source_state,capture_enabled,first_seen_at,revision)
            VALUES($source,$board,$thread,'https://example.org/thread/1','synthetic metadata',$media,2,0,'2026-11-05T00:00:00.0000000+00:00',$revision)
            """);
        thread.Parameters.AddWithValue("$source", "");
        thread.Parameters.AddWithValue("$board", "");
        thread.Parameters.AddWithValue("$thread", "");
        thread.Parameters.AddWithValue("$media", Path.Combine(profile, "absent-media"));
        thread.Parameters.AddWithValue("$revision", 0L);
        using SqliteCommand post = ArchiveSql.Command(connection, transaction, """
            INSERT INTO archive_posts(source,board,thread,post,ordinal,fields,is_deleted)
            VALUES($source,$board,$thread,$post,$ordinal,0,$deleted)
            """);
        post.Parameters.AddWithValue("$source", "");
        post.Parameters.AddWithValue("$board", "");
        post.Parameters.AddWithValue("$thread", "");
        post.Parameters.AddWithValue("$post", "");
        post.Parameters.AddWithValue("$ordinal", 0);
        post.Parameters.AddWithValue("$deleted", 0);
        using SqliteCommand attachment = ArchiveSql.Command(connection, transaction, """
            INSERT INTO archive_attachments(source,board,thread,post,position,variant,original_name,planned_path,is_deleted,attachment_revision_key)
            VALUES($source,$board,$thread,$post,$position,$variant,$name,$path,$deleted,$revision)
            """);
        attachment.Parameters.AddWithValue("$source", "");
        attachment.Parameters.AddWithValue("$board", "");
        attachment.Parameters.AddWithValue("$thread", "");
        attachment.Parameters.AddWithValue("$post", "");
        attachment.Parameters.AddWithValue("$position", 0);
        attachment.Parameters.AddWithValue("$variant", "");
        attachment.Parameters.Add("$name", SqliteType.Text);
        attachment.Parameters.AddWithValue("$path", Path.Combine(profile, "absent-media", "path-only-marker-" + LiteralName));
        attachment.Parameters.AddWithValue("$deleted", 0);
        attachment.Parameters.Add("$revision", SqliteType.Text);

        var named = new List<ArchiveFilenameHit>();
        long revision = 10;
        // Deliberately not BINARY/numeric order. The same post and thread labels recur in
        // different scopes; post ordinal and insertion order are NOT the search sort key.
        foreach (string source in new[] { "a", "B", "A" })
        foreach (string board in new[] { "g", "G" })
        foreach (string threadId in new[] { "2", "10", "1" })
        {
            var id = new ResourceId(source, board, threadId);
            revision++;
            SetThread(thread, id);
            thread.Parameters["$revision"].Value = revision;
            thread.ExecuteNonQuery();
            string[] postIds = { "3", "11", "2", "10" };
            for (int postIndex = 0; postIndex < postIds.Length; postIndex++)
            {
                string postId = postIds[postIndex];
                bool postDeleted = postIndex % 2 == 0;
                SetThread(post, id);
                post.Parameters["$post"].Value = postId;
                post.Parameters["$ordinal"].Value = postIndex;
                post.Parameters["$deleted"].Value = postDeleted ? 1 : 0;
                post.ExecuteNonQuery();
                for (int position = 16; position >= 0; position--)
                {
                    string? originalName = position == 16 ? null : position == 15 ? WildcardDecoyName : LiteralName;
                    string variant = position % 2 == 0 ? "original" : "preview";
                    bool attachmentDeleted = position % 4 == 0;
                    string? revisionKey = position % 3 == 0 ? null : "catalog-observation";
                    SetThread(attachment, id);
                    attachment.Parameters["$post"].Value = postId;
                    attachment.Parameters["$position"].Value = position;
                    attachment.Parameters["$variant"].Value = variant;
                    attachment.Parameters["$name"].Value = (object?)originalName ?? DBNull.Value;
                    attachment.Parameters["$deleted"].Value = attachmentDeleted ? 1 : 0;
                    attachment.Parameters["$revision"].Value = (object?)revisionKey ?? DBNull.Value;
                    attachment.ExecuteNonQuery();
                    if (originalName != null)
                        named.Add(new(id, postId, position, revisionKey, variant, originalName,
                            postDeleted, attachmentDeleted, revision));
                }
            }
        }
        transaction.Commit();
        // No media file, measured digest or expected-byte claim is used to construct this oracle.
        return named.OrderBy(hit => hit.Thread.Source, StringComparer.Ordinal)
            .ThenBy(hit => hit.Thread.Board, StringComparer.Ordinal)
            .ThenBy(hit => hit.Thread.Thread, StringComparer.Ordinal)
            .ThenBy(hit => hit.Post, StringComparer.Ordinal)
            .ThenBy(hit => hit.Position).ToArray();
    }

    private static void SetThread(SqliteCommand command, ResourceId id)
    {
        command.Parameters["$source"].Value = id.Source;
        command.Parameters["$board"].Value = id.Board;
        command.Parameters["$thread"].Value = id.Thread!;
    }
}
