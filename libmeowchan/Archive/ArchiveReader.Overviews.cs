using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace MeowChan.Archive;

/// <summary>Catalog-only metadata for an archive list row. An attachment does not imply that its file exists.</summary>
public sealed record ArchiveThreadPreview(ResourceId Id, string? FirstPostId,
    string FirstCapturedText, CapturedAttachment? FirstAttachment);

public sealed partial class ArchiveReader
{
    private const int MaximumPreviewCount = 100;
    private const int MaximumPreviewTextLength = 400;

    /// <summary>
    /// Reads at most 100 list previews from one local catalog snapshot. Missing thread IDs are omitted.
    /// This never reads media directories, manifests, files or the network.
    /// </summary>
    public Task<IReadOnlyDictionary<ResourceId, ArchiveThreadPreview>> ReadThreadPreviewsAsync(
        IReadOnlyList<ResourceId> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count > MaximumPreviewCount) throw new ArgumentOutOfRangeException(nameof(ids));
        foreach (ResourceId id in ids) ArchiveValidation.ValidateId(id);
        return ReadAsync((connection, transaction) => ReadPreviews(connection, transaction, ids, cancellationToken), cancellationToken);
    }

    private static IReadOnlyDictionary<ResourceId, ArchiveThreadPreview> ReadPreviews(
        SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<ResourceId> ids, CancellationToken cancellationToken)
    {
        Dictionary<ResourceId, ArchiveThreadPreview> previews = new();
        using SqliteCommand postCommand = ArchiveSql.Command(connection, transaction, """
            SELECT p.post, substr(p.html,1,8192)
            FROM archive_threads t
            LEFT JOIN archive_posts p ON p.source=t.source AND p.board=t.board AND p.thread=t.thread
                AND p.post=(SELECT first.post FROM archive_posts first
                    WHERE first.source=t.source AND first.board=t.board AND first.thread=t.thread
                    ORDER BY first.ordinal,first.post LIMIT 1)
            WHERE t.source=$source AND t.board=$board AND t.thread=$thread
            """);
        postCommand.Parameters.AddWithValue("$source", "");
        postCommand.Parameters.AddWithValue("$board", "");
        postCommand.Parameters.AddWithValue("$thread", "");
        using SqliteCommand attachmentCommand = ArchiveSql.Command(connection, transaction, """
            SELECT position,media_id,variant,url,original_name,planned_path,expected_size,expected_md5,is_deleted
            """ + ArchiveSql.AttachmentIntegrityColumns(connection) + "\n" + """
            FROM archive_attachments
            WHERE source=$source AND board=$board AND thread=$thread AND post=$post
            ORDER BY position LIMIT 1
            """);
        attachmentCommand.Parameters.AddWithValue("$source", "");
        attachmentCommand.Parameters.AddWithValue("$board", "");
        attachmentCommand.Parameters.AddWithValue("$thread", "");
        attachmentCommand.Parameters.AddWithValue("$post", "");
        foreach (ResourceId id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetId(postCommand, id);
            using SqliteDataReader post = postCommand.ExecuteReader();
            if (!post.Read()) continue;
            string? postId = post.IsDBNull(0) ? null : post.GetString(0);
            string text = post.IsDBNull(1) ? "" : ArchiveText.PlainText(post.GetString(1)).Trim();
            if (text.Length > MaximumPreviewTextLength)
                text = text[..MaximumPreviewTextLength].TrimEnd();
            post.Close();

            CapturedAttachment? attachment = null;
            if (postId != null)
            {
                SetId(attachmentCommand, id);
                attachmentCommand.Parameters["$post"].Value = postId;
                using SqliteDataReader media = attachmentCommand.ExecuteReader();
                if (media.Read())
                    attachment = new CapturedAttachment(media.GetInt32(0), media.IsDBNull(1) ? null : media.GetString(1),
                        media.GetString(2), media.IsDBNull(3) ? null : media.GetString(3),
                        media.IsDBNull(4) ? null : media.GetString(4), media.IsDBNull(5) ? null : media.GetString(5),
                        media.IsDBNull(6) ? null : media.GetInt64(6), media.IsDBNull(7) ? null : media.GetString(7),
                        media.GetInt32(8) != 0)
                        { ExpectedSha256 = media.IsDBNull(9) ? null : media.GetString(9),
                            AttachmentRevisionKey = media.IsDBNull(10) ? null : media.GetString(10),
                            ExpectedSha1 = media.IsDBNull(11) ? null : media.GetString(11),
                            ProviderSizeHint = ArchiveSql.ReadSizeHint(media.IsDBNull(12) ? null : media.GetString(12)) };
            }
            previews[id] = new ArchiveThreadPreview(id, postId, text, attachment);
        }
        return new ReadOnlyDictionary<ResourceId, ArchiveThreadPreview>(previews);
    }

    private static void SetId(SqliteCommand command, ResourceId id)
    {
        command.Parameters["$source"].Value = id.Source;
        command.Parameters["$board"].Value = id.Board;
        command.Parameters["$thread"].Value = id.Thread!;
    }
}
