using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json;
using MeowChan.OfficialSources.FourChan;
using MeowChan.Transfer;

namespace MeowChan.Interop.ThreadArchives;

/// <summary>
/// Local API-JSON metadata only. Source is a dialect label, not authenticated provenance.
/// No URLs, local media bindings, native catalog, current source state or import capability.
/// Limits are conservative local policy, not claimed API limits. See wave16 review.
/// </summary>
internal static class FourChanJsonArchiveParser
{
    internal const int MaxBytes = ThreadArchiveRecognition.MaxPayloadBytes;
    internal const int MaxPosts = ThreadArchiveRecognition.MaxPosts;
    internal const int MaxAttachments = ThreadArchiveRecognition.MaxAttachments;
    internal const string Source = "4chan";
    internal const string Profile = "local-4chan-api-json-metadata-v1";

    internal enum Error { None, InvalidBoard, InvalidInput, UnsupportedField, InvalidSchema, UnsafeText }
    internal enum AttachmentKind { Absent, Deleted, OriginalMetadataOnly }
    internal sealed record Attachment(AttachmentKind Kind, long? Tim = null, string? FileName = null,
        string? Extension = null, long? Size = null, string? Md5 = null,
        long? Width = null, long? Height = null, long? ThumbnailWidth = null, long? ThumbnailHeight = null);
    // Null means absent; explicit null in the wire is rejected. Empty text remains empty.
    // No post-deleted flag exists in this API profile. Missing posts imply nothing.
    internal sealed record Post(long No, long Resto, int Ordinal, long UnixSeconds,
        string Name, string? Subject, string? InertHtml, Attachment Attachment);
    internal sealed record Archive(string Board, long ThreadNo, bool? ArchivedClaim,
        ImmutableArray<Post> Posts, int IgnoredKnownFields)
    {
        internal string SourceId => Source;
        internal string ProfileId => Profile;
    }
    internal sealed record Inspection(Archive? Value, Error Error)
    {
        internal bool Accepted => Value is not null && Error == FourChanJsonArchiveParser.Error.None;
    }
    private sealed class Invalid : Exception
    {
        internal Error Code { get; }
        internal Invalid(Error code) { Code = code; }
    }

    // Explicitly classified API display/special/operational metadata, deliberately lost.
    // These values are never returned, used as authority, or added to an extension bag.
    // Unknown future fields FAIL CLOSED until classified here (no blanket ignore).
    private static readonly HashSet<string> IgnoredStrings = new(StringComparer.Ordinal)
    { "now", "trip", "id", "capcode", "country", "country_name", "board_flag", "flag_name", "semantic_url", "tag" };
    private static readonly HashSet<string> IgnoredNumbers = new(StringComparer.Ordinal)
    { "custom_spoiler", "replies", "images", "unique_ips", "since4pass" };
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal)
    { "filedeleted", "spoiler", "sticky", "closed", "bumplimit", "imagelimit", "archived", "m_img" };
    private static readonly HashSet<string> Core = new(StringComparer.Ordinal)
    { "no", "resto", "time", "name", "sub", "com", "tim", "filename", "ext", "fsize", "md5", "w", "h", "tn_w", "tn_h", "archived_on" };
    private static readonly string[] MediaFields = { "tim", "filename", "ext", "fsize", "md5", "w", "h", "tn_w", "tn_h" };
    private static readonly Attachment Absent = new(AttachmentKind.Absent);

    internal static Inspection Inspect(ReadOnlyMemory<byte> utf8, string trustedBoard)
    {
        if (trustedBoard is null || !FourChanParser.ValidBoard(Source, trustedBoard) ||
            trustedBoard != trustedBoard.ToLowerInvariant()) return new(null, Error.InvalidBoard);
        if (utf8.Length > MaxBytes) return new(null, Error.InvalidInput);
        // Snapshot before BOTH passes: caller mutation cannot swap validated and parsed bytes.
        byte[] snapshot = utf8.ToArray();
        var recognition = ThreadArchiveRecognition.Inspect(snapshot);
        if (!recognition.IsRecognized || recognition.Format != ThreadArchiveRecognitionFormat.FourChanApiJson)
            return new(null, Error.InvalidInput);
        try
        {
            using var document = JsonDocument.Parse(snapshot,
                new JsonDocumentOptions { MaxDepth = ThreadArchiveRecognition.MaxJsonDepth });
            var root = document.RootElement;
            foreach (var field in root.EnumerateObject())
                if (field.Name != "posts") throw new Invalid(Error.UnsupportedField);
            var posts = ImmutableArray.CreateBuilder<Post>();
            var ids = new HashSet<long>();
            long thread = 0;
            int ignored = 0, attachments = 0;
            bool? archived = null;
            foreach (var post in root.GetProperty("posts").EnumerateArray())
            {
                if (posts.Count >= MaxPosts) throw new Invalid(Error.InvalidSchema);
                foreach (var field in post.EnumerateObject())
                {
                    if (IgnoredStrings.Contains(field.Name)) { _ = Text(post, field.Name); ignored++; }
                    else if (IgnoredNumbers.Contains(field.Name)) { _ = Number(post, field.Name); ignored++; }
                    else if (Flags.Contains(field.Name)) { if (Number(post, field.Name) != 1) throw new Invalid(Error.InvalidSchema); }
                    else if (!Core.Contains(field.Name)) throw new Invalid(Error.UnsupportedField);
                }
                long no = Number(post, "no", positive: true), resto = Number(post, "resto");
                if (!ids.Add(no)) throw new Invalid(Error.InvalidSchema);
                bool op = posts.Count == 0;
                if (op) { if (resto != 0) throw new Invalid(Error.InvalidSchema); thread = no; }
                else if (resto != thread || no <= thread) throw new Invalid(Error.InvalidSchema);
                foreach (string key in new[] { "sub", "sticky", "closed", "bumplimit", "imagelimit", "archived", "archived_on", "replies", "images", "unique_ips", "semantic_url" })
                    if (!op && post.TryGetProperty(key, out _)) throw new Invalid(Error.InvalidSchema);
                _ = Text(post, "now"); // required API display timestamp; never parsed as a clock
                long time = Number(post, "time");
                if (time > 253402300799L) throw new Invalid(Error.InvalidSchema);
                if (post.TryGetProperty("archived", out _)) archived = true;
                if (post.TryGetProperty("archived_on", out _))
                {
                    long on = Number(post, "archived_on");
                    if (archived != true || on < time || on > 253402300799L) throw new Invalid(Error.InvalidSchema);
                }
                string name = SafeText(post, "name");
                string? subject = OptionalText(post, "sub"), html = OptionalText(post, "com");
                if (html is not null && !TransferPublicReferences.IsInertHtml(html)) throw new Invalid(Error.UnsafeText);
                var attachment = ReadAttachment(post);
                if (attachment.Kind != AttachmentKind.Absent && ++attachments > MaxAttachments) throw new Invalid(Error.InvalidSchema);
                posts.Add(new(no, resto, posts.Count, time, name, subject, html, attachment));
            }
            return new(new(trustedBoard, thread, archived, posts.ToImmutable(), ignored), Error.None);
        }
        catch (Invalid error) { return new(null, error.Code); }
        catch (JsonException) { return new(null, Error.InvalidInput); }
        catch (InvalidOperationException) { return new(null, Error.InvalidSchema); }
        catch (KeyNotFoundException) { return new(null, Error.InvalidSchema); }
    }

    private static Attachment ReadAttachment(JsonElement post)
    {
        int present = 0;
        foreach (string key in MediaFields) if (post.TryGetProperty(key, out _)) present++;
        bool deleted = post.TryGetProperty("filedeleted", out _);
        if (deleted)
        {
            if (present != 0) throw new Invalid(Error.InvalidSchema); // mixed deletion/original unsupported
            return new(AttachmentKind.Deleted);
        }
        if (present == 0)
        {
            if (post.TryGetProperty("spoiler", out _) || post.TryGetProperty("m_img", out _)) throw new Invalid(Error.InvalidSchema);
            return Absent;
        }
        if (present != MediaFields.Length) throw new Invalid(Error.InvalidSchema);
        long tim = Number(post, "tim", true), size = Number(post, "fsize", true);
        string file = SafeText(post, "filename"), ext = Text(post, "ext"), md5 = Text(post, "md5");
        if (ext is not (".jpg" or ".jpeg" or ".png" or ".gif" or ".webm" or ".mp4")) throw new Invalid(Error.InvalidSchema);
        // Display-only; nevertheless reject path-like names rather than promising filesystem safety.
        if (file.Length == 0 || file.Contains('/') || file.Contains('\\') || file is "." or "..") throw new Invalid(Error.UnsafeText);
        Span<byte> digest = stackalloc byte[16];
        if (md5.Length != 24 || !Convert.TryFromBase64String(md5, digest, out int written) || written != 16 ||
            Convert.ToBase64String(digest) != md5) throw new Invalid(Error.InvalidSchema);
        return new(AttachmentKind.OriginalMetadataOnly, tim, file, ext, size, md5,
            Dimension(post, "w"), Dimension(post, "h"), Dimension(post, "tn_w"), Dimension(post, "tn_h"));
    }
    private static long Dimension(JsonElement post, string key)
    {
        long value = Number(post, key, true);
        if (value > int.MaxValue) throw new Invalid(Error.InvalidSchema);
        return value;
    }
    private static long Number(JsonElement obj, string key, bool positive = false)
    {
        if (!obj.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Number) throw new Invalid(Error.InvalidSchema);
        foreach (char c in value.GetRawText()) if (c < '0' || c > '9') throw new Invalid(Error.InvalidSchema);
        if (!value.TryGetInt64(out long number) || (positive && number == 0)) throw new Invalid(Error.InvalidSchema);
        return number;
    }
    private static string Text(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String) throw new Invalid(Error.InvalidSchema);
        return value.GetString()!;
    }
    private static string SafeText(JsonElement obj, string key)
    {
        string value = Text(obj, key);
        // Narrow v1: no literal URL syntax even in otherwise inert markup/plain text.
        // No query stripping, URL recovery or HTML decoding. Entities remain inert data.
        if (value.Contains("://", StringComparison.Ordinal) || value.Contains('\0')) throw new Invalid(Error.UnsafeText);
        return value;
    }
    private static string? OptionalText(JsonElement obj, string key) => obj.TryGetProperty(key, out _) ? SafeText(obj, key) : null;
}
