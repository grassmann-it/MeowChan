using System;
using System.Collections.Generic;
using System.Net;
using Newtonsoft.Json.Linq;
using oChan.Downloader;

namespace oChan.Archive;

internal static class ProviderPostCapture
{
    internal static CapturedPost? Parse(JToken post, int ordinal, string threadId, IReadOnlyList<CapturedAttachment> attachments)
    {
        string? id = post.Value<string>("no") ?? (ordinal == 0 ? threadId : null);
        if (string.IsNullOrWhiteSpace(id)) return null;
        PostFieldPresence fields = PostFieldPresence.None;
        if (post["time"] != null) fields |= PostFieldPresence.PostedAt;
        if (post["sub"] != null) fields |= PostFieldPresence.Subject;
        if (post["name"] != null) fields |= PostFieldPresence.Author;
        if (post["com"] != null) fields |= PostFieldPresence.Html;
        if (post["deleted"] != null) fields |= PostFieldPresence.IsDeleted;
        if (attachments.Count != 0 || post["extra_files"] != null) fields |= PostFieldPresence.Attachments;
        long? seconds = post.Value<long?>("time");
        return new CapturedPost(id, ordinal, fields,
            seconds.HasValue ? DateTimeOffset.FromUnixTimeSeconds(seconds.Value) : null,
            WebUtility.HtmlDecode(post.Value<string>("sub")), WebUtility.HtmlDecode(post.Value<string>("name")),
            post.Value<string>("com"), post.Value<int?>("deleted") == 1, attachments);
    }

    internal static CapturedAttachment Attachment(DownloadItem item, int position) => new CapturedAttachment(position,
        item.MediaIdentifier, Url: item.DownloadUri.AbsoluteUri, OriginalFileName: item.OriginalFileName,
        PlannedPath: item.DestinationPath, ExpectedSize: item.ExpectedSize, ExpectedMd5: item.ExpectedMd5);
}
