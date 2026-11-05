using System;
using System.Collections.Generic;
using System.Threading;
using MeowChan.Archive;

namespace MeowChan.Desktop;

internal enum ReaderFindField { Subject, Body }
[Flags]
internal enum ReaderFindTruncation { None = 0, Posts = 1, TextBytes = 2, Hits = 4 }
internal sealed record ReaderFindLimits(int MaxPosts = 10000, int MaxHits = 10000,
    long MaxTextBytes = 16 * 1024 * 1024);
internal sealed record ReaderFindText(string PostId, int PostIndex, ReaderFindField Field, string Text);
internal sealed record ReaderFindHit(int TextIndex, int Offset, int Length);

/// <summary>
/// Inert, revision-bound derivation of one immutable archive read. No UI or IO ownership.
/// Offsets address only Texts[hit.TextIndex].Text, never source HTML or a native control.
/// </summary>
internal sealed class ReaderThreadFindIndex
{
    private ReaderThreadFindIndex(ResourceId id, long revision, string query,
        List<ReaderFindText> texts, List<ReaderFindHit> hits, ReaderFindTruncation truncation,
        int postsExamined, long textBytes)
    {
        Id = id; Revision = revision; Query = query;
        Texts = texts.AsReadOnly(); Hits = hits.AsReadOnly(); Truncation = truncation;
        PostsExamined = postsExamined; TextBytes = textBytes;
    }
    internal ResourceId Id { get; }
    internal long Revision { get; }
    internal string Query { get; }
    internal IReadOnlyList<ReaderFindText> Texts { get; }
    internal IReadOnlyList<ReaderFindHit> Hits { get; }
    internal ReaderFindTruncation Truncation { get; }
    internal int PostsExamined { get; }
    // Conservative UTF-16 payload bytes: admitted raw fields PLUS retained plaintext.
    // Not a CLR heap-size measurement, UTF-8 count, or persistent storage size.
    internal long TextBytes { get; }
    internal bool Matches(ResourceId id, long revision) => Id == id && Revision == revision;

    internal static ReaderThreadFindIndex Create(ArchivedThreadView thread, string query,
        ReaderFindLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(query);
        if (query.Length > 512) throw new ArgumentOutOfRangeException(nameof(query));
        limits ??= new();
        if (limits.MaxPosts is < 1 or > 100000 || limits.MaxHits is < 1 or > 100000
            || limits.MaxTextBytes is < 1 or > 64 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(limits));
        cancellationToken.ThrowIfCancellationRequested();
        List<ReaderFindText> texts = new();
        List<ReaderFindHit> hits = new();
        ReaderFindTruncation truncated = ReaderFindTruncation.None;
        int examined = 0;
        long bytes = 0;
        // Empty input is an explicit no-search, not a match at every character boundary.
        if (query.Length != 0)
        {
            int count = Math.Min(thread.PostCount, limits.MaxPosts);
            if (count < thread.PostCount) truncated |= ReaderFindTruncation.Posts;
            // ArchivedThreadView already applies the persisted provider ordering contract.
            for (int page = 0; page < count; page += 500)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var posts = thread.GetPosts(page, Math.Min(500, count - page));
                for (int p = 0; p < posts.Count; p++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    CapturedPost post = posts[p];
                    examined++;
                    if (!Add(post.Subject, ReaderFindField.Subject, post, page + p)
                        || !Add(post.Html, ReaderFindField.Body, post, page + p)) goto Finished;
                }
            }
        }
    Finished:
        cancellationToken.ThrowIfCancellationRequested();
        return new(thread.Summary.Id, thread.Summary.Revision, query, texts, hits, truncated, examined, bytes);

        bool Add(string? raw, ReaderFindField field, CapturedPost post, int postIndex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            raw ??= string.Empty;
            long rawBytes = 2L * raw.Length;
            // Never cut HTML before parsing: a prefix could expose an incomplete active tag.
            if (rawBytes > limits.MaxTextBytes - bytes)
            { truncated |= ReaderFindTruncation.TextBytes; return false; }
            bytes += rawBytes;
            // Matches ArchivePage's Subject and Body projection, excluding display-limit notices.
            string text = field == ReaderFindField.Body ? ArchiveText.PlainText(raw) : raw;
            cancellationToken.ThrowIfCancellationRequested();
            long projectedBytes = 2L * text.Length;
            if (projectedBytes > limits.MaxTextBytes - bytes)
            { truncated |= ReaderFindTruncation.TextBytes; return false; }
            bytes += projectedBytes;
            int textIndex = texts.Count;
            texts.Add(new(post.PostId, postIndex, field, text));
            // Overlapping literal occurrences are intentional ("aaa" / "aa" => 0,1).
            for (int start = 0; start <= text.Length - query.Length;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int offset = text.IndexOf(query, start, StringComparison.OrdinalIgnoreCase);
                if (offset < 0) break;
                if (hits.Count == limits.MaxHits)
                { truncated |= ReaderFindTruncation.Hits; return false; }
                hits.Add(new(textIndex, offset, query.Length));
                start = offset + 1;
            }
            return true;
        }
    }

    // -1 means no current selection/no result. Other out-of-bounds cursors are errors.
    internal static int Next(int current, int count, bool wrap = true) => Move(current, count, true, wrap);
    internal static int Previous(int current, int count, bool wrap = true) => Move(current, count, false, wrap);
    private static int Move(int current, int count, bool forward, bool wrap)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (current < -1 || current >= count && current != -1)
            throw new ArgumentOutOfRangeException(nameof(current));
        if (count == 0) return -1;
        if (current == -1) return forward ? 0 : count - 1;
        if (forward) return current < count - 1 ? current + 1 : wrap ? 0 : current;
        return current > 0 ? current - 1 : wrap ? count - 1 : current;
    }
}
