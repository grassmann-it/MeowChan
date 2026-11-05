using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MeowChan.Interop.ThreadArchives;

/// <summary>
/// Partial, loss-aware selected-byte inspector, not a complete YChanEx parser/importer.
/// Wire evidence: murrty/YChanEx 00b16bfa300ea91dac303b411d817a583a779874,
/// ThreadData/GenericPost/GenericFile DataMembers and ProgramSettings.JsonSerialize writer.
/// No foreign CLR deserialization, path resolution, media lookup, or acquisition authority.
/// </summary>
internal static class YChanExArchiveParser
{
    // Local caller-selected dialect revision; not an upstream wire version/authentication claim.
    internal const string Profile = "ychanex-00b16bfa300ea91dac303b411d817a583a779874-partial-v1";
    internal const int MaxBytes = 1024 * 1024;
    internal const int MaxDepth = 16;
    internal const int MaxPosts = 4096;
    internal const int MaxAttachments = 4096;
    internal const int MaxTokens = 65536;
    internal const int MaxTokenBytes = 65536;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal enum Error { None, UnsupportedProfile, TooLarge, InvalidUtf8, Bom, InvalidJson,
        DuplicateKey, InvalidUnicode, Budget, InvalidSchema, IdentityMismatch }
    [Flags]
    internal enum Omission
    {
        None = 0, AcquisitionUrls = 1, UnsupportedDimensions = 2, UnsupportedTime = 4,
        UnsafeName = 8, UnknownFields = 16, OperationalOrPrivateFields = 32
    }
    internal enum Presence { Missing, Null, Value, Omitted }
    internal enum BytePresence { Unknown }
    internal sealed record Text(Presence Presence, string? Value);
    // All names are untrusted display-only leaf strings, never local file references.
    internal sealed record Attachment(Text FileId, Text OriginalName, Text GeneratedName,
        Text Extension, Text ThumbnailName, Text ThumbnailExtension, Text SavedOriginalName,
        Text SavedThumbnailName, Text SavedOriginalDisplayName, Text SavedThumbnailDisplayName,
        Omission Omissions)
    {
        internal BytePresence OriginalBytes => BytePresence.Unknown;
        internal BytePresence ThumbnailBytes => BytePresence.Unknown;
    }
    internal sealed record Post(ulong Id, Text Author, Text Tripcode, Text Capcode,
        Text PosterId, Text Subject, Text Message, Presence FilesPresence, ImmutableArray<Attachment> Attachments,
        Omission Omissions);
    internal sealed record Archive(string Profile, string Source, string Board, string ThreadId,
        string PageUrl, ImmutableArray<Post> Posts, Omission Omissions);
    internal sealed record Result(Error Error, Archive? Archive)
    {
        internal bool Success => Error == YChanExArchiveParser.Error.None && Archive != null;
    }
    private sealed class Failure(Error error) : Exception
    {
        internal Error Code { get; } = error;
    }

    internal static Result Inspect(ReadOnlyMemory<byte> selectedJson, string? profile)
    {
        if (!string.Equals(profile, Profile, StringComparison.Ordinal)) return new(Error.UnsupportedProfile, null);
        if (selectedJson.Length > MaxBytes) return new(Error.TooLarge, null);
        byte[] snapshot = selectedJson.ToArray(); // One bounded caller-input image for all passes.
        if (snapshot.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) return new(Error.Bom, null);
        try
        {
            _ = StrictUtf8.GetCharCount(snapshot);
            CheckTokens(snapshot);
            using JsonDocument doc = JsonDocument.Parse(snapshot, new JsonDocumentOptions { MaxDepth = MaxDepth });
            JsonElement root = doc.RootElement;
            Object(root);
            string id = RequiredString(root, "Id");
            if (!CanonicalId(id, out ulong threadId)) throw new Failure(Error.InvalidSchema);
            string board = RequiredString(root, "Board");
            if (board.Length > 32) throw new Failure(Error.IdentityMismatch);
            foreach (char c in board)
                if (!(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9')) throw new Failure(Error.IdentityMismatch);
            string page = RequiredString(root, "Url");
            // Exact public grammar, not Uri normalization, host guessing or query stripping.
            if (page != $"https://boards.4chan.org/{board}/thread/{id}") throw new Failure(Error.IdentityMismatch);
            JsonElement posts = RequiredArray(root, "ThreadPosts");
            if (posts.GetArrayLength() == 0) throw new Failure(Error.InvalidSchema);
            if (posts.GetArrayLength() > MaxPosts) throw new Failure(Error.Budget);
            var result = ImmutableArray.CreateBuilder<Post>();
            var ids = new HashSet<ulong>();
            int attachments = 0, opCount = 0;
            Omission total = Classify(root, "Id Board Url ThreadPosts", "LastModified", "", "");
            foreach (JsonElement post in posts.EnumerateArray())
            {
                Object(post);
                ulong postId = Unsigned(post, "post_id");
                if (!ids.Add(postId)) throw new Failure(Error.InvalidSchema);
                bool first = false;
                if (post.TryGetProperty("first", out JsonElement op))
                {
                    if (op.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new Failure(Error.InvalidSchema);
                    first = op.GetBoolean();
                }
                if (first)
                {
                    opCount++;
                    if (postId != threadId || result.Count != 0) throw new Failure(Error.IdentityMismatch);
                }
                Omission losses = Classify(post,
                    "post_id first files poster_name poster_tripcode poster_capcode poster_id post_subject post_message",
                    "post_time", "", "");
                var files = ImmutableArray.CreateBuilder<Attachment>();
                if (post.TryGetProperty("files", out JsonElement fileArray))
                {
                    if (fileArray.ValueKind != JsonValueKind.Array) throw new Failure(Error.InvalidSchema);
                    foreach (JsonElement file in fileArray.EnumerateArray())
                    {
                        if (++attachments > MaxAttachments) throw new Failure(Error.Budget);
                        Object(file);
                        Omission fl = Classify(file,
                            "file_id original_file_name generated_file_name file_extension thumbnail_name thumbnail_extension saved_file saved_thumbnail_file saved_file_name saved_thumbnail_file_name",
                            "", "file_dimensions thumbnail_dimensions", "file_url thumbnail_file_url");
                        Text fid = Leaf(file, "file_id", ref fl);
                        Text original = Leaf(file, "original_file_name", ref fl);
                        Text generated = Leaf(file, "generated_file_name", ref fl);
                        Text ext = Leaf(file, "file_extension", ref fl);
                        Text thumb = Leaf(file, "thumbnail_name", ref fl);
                        Text thumbExt = Leaf(file, "thumbnail_extension", ref fl);
                        Text saved = Leaf(file, "saved_file", ref fl);
                        Text savedThumb = Leaf(file, "saved_thumbnail_file", ref fl);
                        Text display = Leaf(file, "saved_file_name", ref fl);
                        Text thumbDisplay = Leaf(file, "saved_thumbnail_file_name", ref fl);
                        files.Add(new(fid, original, generated, ext, thumb, thumbExt, saved, savedThumb,
                            display, thumbDisplay, fl));
                        losses |= fl;
                    }
                }
                result.Add(new(postId, ReadText(post, "poster_name"), ReadText(post, "poster_tripcode"),
                    ReadText(post, "poster_capcode"), ReadText(post, "poster_id"), ReadText(post, "post_subject"),
                    ReadText(post, "post_message"), post.TryGetProperty("files", out _) ? Presence.Value : Presence.Missing, files.ToImmutable(), losses));
                total |= losses;
            }
            if (opCount != 1) throw new Failure(Error.IdentityMismatch);
            return new(Error.None, new(Profile, "4chan", board, id, page, result.ToImmutable(), total));
        }
        catch (Failure e) { return new(e.Code, null); }
        catch (DecoderFallbackException) { return new(Error.InvalidUtf8, null); }
        catch (JsonException) { return new(Error.InvalidJson, null); }
        catch (InvalidOperationException) { return new(Error.InvalidSchema, null); }
        // No exception text, input values, paths, property names or URLs in diagnostics.
    }

    private static void Object(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new Failure(Error.InvalidSchema);
    }
    private static string RequiredString(JsonElement obj, string key)
    {
        Text text = ReadText(obj, key);
        if (text.Presence != Presence.Value || string.IsNullOrEmpty(text.Value)) throw new Failure(Error.InvalidSchema);
        return text.Value;
    }
    private static JsonElement RequiredArray(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
            throw new Failure(Error.InvalidSchema);
        return array;
    }
    private static Text ReadText(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out JsonElement value)) return new(Presence.Missing, null);
        if (value.ValueKind == JsonValueKind.Null) return new(Presence.Null, null);
        if (value.ValueKind != JsonValueKind.String) throw new Failure(Error.InvalidSchema);
        return new(Presence.Value, value.GetString());
    }
    private static Text Leaf(JsonElement obj, string key, ref Omission omissions)
    {
        Text text = ReadText(obj, key);
        if (text.Presence != Presence.Value) return text;
        string s = text.Value!;
        bool safe = s.Length <= 255 && s != "." && s != ".." && !s.EndsWith(' ') && !s.EndsWith('.');
        foreach (char c in s)
            if (char.IsControl(c) || "/\\:<>\"|?*".Contains(c)) safe = false;
        if (safe) return text;
        omissions |= Omission.UnsafeName;
        // Value was present but is omitted, distinct from missing or explicit null.
        return new(Presence.Omitted, null);
    }
    private static bool CanonicalId(string s, out ulong id)
    {
        id = 0;
        if (s.Length == 0 || s[0] == '0') return false;
        foreach (char c in s) if (c < '0' || c > '9') return false;
        return ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
    }
    private static ulong Unsigned(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out JsonElement value) || value.ValueKind != JsonValueKind.Number ||
            !CanonicalId(value.GetRawText(), out ulong id)) throw new Failure(Error.InvalidSchema);
        return id;
    }
    private static bool Has(string keys, string key) => key.Length != 0 && key.IndexOf(' ') < 0 &&
        (" " + keys + " ").Contains(" " + key + " ", StringComparison.Ordinal);
    private static Omission Classify(JsonElement obj, string retained, string times, string dimensions, string urls)
    {
        Omission result = Omission.None;
        foreach (JsonProperty p in obj.EnumerateObject())
        {
            if (Has(retained, p.Name)) continue;
            if (Has(times, p.Name)) result |= Omission.UnsupportedTime;
            else if (Has(dimensions, p.Name)) result |= Omission.UnsupportedDimensions;
            else if (Has(urls, p.Name)) result |= Omission.AcquisitionUrls;
            else if (Has("ThreadState ThreadArchived ParsedPostIds DuplicateNames EstimatedSize ThreadImagesCount DownloadedImagesCount ThreadPostsCount BoardName ThreadName CustomThreadName BoardSubtitle UrlHost tags quotes quoted_by file_hash file_size thumbnail_spoiled status", p.Name))
                result |= Omission.OperationalOrPrivateFields;
            else result |= Omission.UnknownFields;
        }
        return result;
    }
    private static void CheckTokens(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = MaxDepth });
        var objects = new Stack<HashSet<string>>();
        int tokens = 0;
        while (reader.Read())
        {
            if (++tokens > MaxTokens || reader.ValueSpan.Length > MaxTokenBytes) throw new Failure(Error.Budget);
            if ((reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName) && !ValidEscapedUnicode(reader.ValueSpan))
                throw new Failure(Error.InvalidUnicode);
            if (reader.TokenType == JsonTokenType.StartObject) objects.Push(new(StringComparer.Ordinal));
            else if (reader.TokenType == JsonTokenType.EndObject) objects.Pop();
            else if (reader.TokenType == JsonTokenType.PropertyName && !objects.Peek().Add(reader.GetString()!))
                throw new Failure(Error.DuplicateKey);
        }
    }
    private static bool ValidEscapedUnicode(ReadOnlySpan<byte> token)
    {
        for (int i = 0; i < token.Length; i++)
        {
            if (token[i] != (byte)'\\') continue;
            if (++i >= token.Length) return false;
            if (token[i] != (byte)'u') continue; // Skip escaped backslashes, not a second escape.
            int unit = HexUnit(token, i + 1);
            if (unit < 0) return false;
            i += 4;
            if (unit >= 0xdc00 && unit <= 0xdfff) return false;
            if (unit < 0xd800 || unit > 0xdbff) continue;
            if (i + 6 >= token.Length || token[i + 1] != (byte)'\\' || token[i + 2] != (byte)'u') return false;
            int low = HexUnit(token, i + 3);
            if (low < 0xdc00 || low > 0xdfff) return false;
            i += 6;
        }
        return true;
    }

    private static int HexUnit(ReadOnlySpan<byte> token, int start)
    {
        if (start + 4 > token.Length) return -1;
        int value = 0;
        for (int i = start; i < start + 4; i++)
        {
            int b = token[i];
            int digit = b >= '0' && b <= '9' ? b - '0' : b >= 'a' && b <= 'f' ? b - 'a' + 10 :
                b >= 'A' && b <= 'F' ? b - 'A' + 10 : -1;
            if (digit < 0) return -1;
            value = value * 16 + digit;
        }
        return value;
    }

}
