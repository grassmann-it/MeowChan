using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace MeowChan.Interop;

internal enum ThreadArchiveRecognitionStatus
{
    Recognized,
    Unsupported,
    Ambiguous,
    Rejected
}

internal enum ThreadArchiveRecognitionFormat
{
    None,
    FourChanApiJson,
    KurobaExJson,
    YChanExJson
}

internal enum ThreadArchiveRecognitionError
{
    None,
    PayloadTooLarge,
    InvalidUtf8,
    InvalidUnicode,
    BomNotAllowed,
    InvalidJson,
    DuplicateProperty,
    BudgetExceeded,
    NoKnownSignature,
    UnsupportedFormat,
    AmbiguousSignature
}

internal sealed record ThreadArchiveRecognitionCounts(
    int ThreadClaims,
    int PostClaims,
    int AttachmentClaims);

internal sealed record ThreadArchiveRecognitionResult(
    ThreadArchiveRecognitionStatus Status,
    ThreadArchiveRecognitionFormat Format,
    ImmutableArray<ThreadArchiveRecognitionFormat> Candidates,
    ThreadArchiveRecognitionCounts Counts,
    ThreadArchiveRecognitionError Error)
{
    internal bool IsRecognized => Status == ThreadArchiveRecognitionStatus.Recognized;
}

/// <summary>
/// Performs only bounded, structural signature recognition of in-memory JSON text.
/// Results are hints, not format validation, provenance, a selected import profile,
/// an offline completeness claim, media availability, or an Apply capability.
/// Counts refer only to observed metadata claims; no foreign values are returned.
/// It never opens archives, follows URLs, reads media, starts a client, or applies data.
/// Signature evidence: foreign-thread-archive-adapters-sol-02.md, sections 4.1–4.3,
/// and interop/adapter-implementation-map.md. Unknown/extra post data stays inert;
/// no implicit selection or routing into any foreign/native importer is performed.
/// KurobaEx and YChanEx are deliberately identified as known-but-unsupported because
/// their producer wire details are not sufficiently evidenced for an adapter here.
/// </summary>
internal static class ThreadArchiveRecognition
{
    internal const int MaxPayloadBytes = 1024 * 1024;
    internal const int MaxJsonDepth = 8;
    // Conservative local recognition limits, not foreign format/provider limits.
    internal const int MaxPosts = 4096;
    internal const int MaxAttachments = 4096;
    internal const int MaxTokens = 65_536;
    internal const int MaxTokenBytes = 65_536;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private sealed class RecognitionBudgetExceededException : Exception { }
    private sealed class RecognitionInvalidUnicodeException : Exception { }

    internal static ThreadArchiveRecognitionResult Inspect(ReadOnlyMemory<byte> utf8)
    {
        if (utf8.Length > MaxPayloadBytes)
            return Rejected(ThreadArchiveRecognitionError.PayloadTooLarge);

        // Do not validate one caller-owned image and parse another on a later pass.
        byte[] snapshot = utf8.ToArray();
        if (snapshot.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
            return Rejected(ThreadArchiveRecognitionError.BomNotAllowed);

        try
        {
            _ = StrictUtf8.GetCharCount(snapshot);
        }
        catch (DecoderFallbackException)
        {
            return Rejected(ThreadArchiveRecognitionError.InvalidUtf8);
        }

        try
        {
            if (ContainsDuplicateProperty(snapshot))
                return Rejected(ThreadArchiveRecognitionError.DuplicateProperty);

            using JsonDocument document = JsonDocument.Parse(snapshot,
                new JsonDocumentOptions { MaxDepth = MaxJsonDepth });

            var candidates = ImmutableArray.CreateBuilder<ThreadArchiveRecognitionFormat>();
            var counts = new Dictionary<ThreadArchiveRecognitionFormat, ThreadArchiveRecognitionCounts>();

            if (TryRecognizeFourChan(document.RootElement, out ThreadArchiveRecognitionCounts fourChan))
            {
                candidates.Add(ThreadArchiveRecognitionFormat.FourChanApiJson);
                counts[ThreadArchiveRecognitionFormat.FourChanApiJson] = fourChan;
            }

            if (TryRecognizeKuroba(document.RootElement, out ThreadArchiveRecognitionCounts kuroba))
            {
                candidates.Add(ThreadArchiveRecognitionFormat.KurobaExJson);
                counts[ThreadArchiveRecognitionFormat.KurobaExJson] = kuroba;
            }

            if (TryRecognizeYChan(document.RootElement, out ThreadArchiveRecognitionCounts yChan))
            {
                candidates.Add(ThreadArchiveRecognitionFormat.YChanExJson);
                counts[ThreadArchiveRecognitionFormat.YChanExJson] = yChan;
            }

            ImmutableArray<ThreadArchiveRecognitionFormat> candidateArray = candidates.ToImmutable();
            if (candidateArray.Length == 0)
                return new(ThreadArchiveRecognitionStatus.Unsupported, ThreadArchiveRecognitionFormat.None,
                    candidateArray, EmptyCounts(), ThreadArchiveRecognitionError.NoKnownSignature);

            if (candidateArray.Length != 1)
                return new(ThreadArchiveRecognitionStatus.Ambiguous, ThreadArchiveRecognitionFormat.None,
                    candidateArray, EmptyCounts(), ThreadArchiveRecognitionError.AmbiguousSignature);

            ThreadArchiveRecognitionFormat format = candidateArray[0];
            ThreadArchiveRecognitionCounts recognizedCounts = counts[format];
            if (format != ThreadArchiveRecognitionFormat.FourChanApiJson)
            {
                return new(ThreadArchiveRecognitionStatus.Unsupported, format, candidateArray,
                    recognizedCounts, ThreadArchiveRecognitionError.UnsupportedFormat);
            }

            return new(ThreadArchiveRecognitionStatus.Recognized, format, candidateArray,
                recognizedCounts, ThreadArchiveRecognitionError.None);
        }
        catch (RecognitionInvalidUnicodeException)
        {
            return Rejected(ThreadArchiveRecognitionError.InvalidUnicode);
        }
        catch (RecognitionBudgetExceededException)
        {
            return Rejected(ThreadArchiveRecognitionError.BudgetExceeded);
        }
        catch (JsonException)
        {
            return Rejected(ThreadArchiveRecognitionError.InvalidJson);
        }
        catch (InvalidOperationException)
        {
            // Never surface raw foreign values or runtime exception text.
            return Rejected(ThreadArchiveRecognitionError.InvalidJson);
        }
    }

    private static bool TryRecognizeFourChan(JsonElement root, out ThreadArchiveRecognitionCounts counts)
    {
        counts = EmptyCounts();
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("posts", out JsonElement posts) ||
            posts.ValueKind != JsonValueKind.Array || posts.GetArrayLength() == 0)
            return false;
        if (posts.GetArrayLength() > MaxPosts)
            throw new RecognitionBudgetExceededException();

        int attachments = 0;
        ulong threadNo = 0;
        var postIds = new HashSet<ulong>();
        foreach (JsonElement post in posts.EnumerateArray())
        {
            if (post.ValueKind != JsonValueKind.Object ||
                !TryUnsignedInteger(post, "no", out ulong no) || no == 0 ||
                !TryUnsignedInteger(post, "resto", out ulong resto) || !postIds.Add(no))
                return false;

            // A catalog, reply-only fragment or mixed threads is not this single-thread signature.
            if (threadNo == 0)
            {
                if (resto != 0) return false;
                threadNo = no;
            }
            else if (resto != threadNo)
                return false;

            bool hasAttachment = false;
            if (post.TryGetProperty("tim", out _))
            {
                if (!TryUnsignedInteger(post, "tim", out ulong tim) || tim == 0) return false;
                hasAttachment = true;
            }
            if (post.TryGetProperty("filedeleted", out _))
            {
                if (!TryUnsignedInteger(post, "filedeleted", out ulong deleted) || deleted > 1) return false;
                hasAttachment |= deleted == 1;
            }
            if (hasAttachment && ++attachments > MaxAttachments)
                throw new RecognitionBudgetExceededException();
        }

        counts = new(1, posts.GetArrayLength(), attachments);
        return true;
    }

    private static bool TryRecognizeKuroba(JsonElement root, out ThreadArchiveRecognitionCounts counts)
    {
        counts = EmptyCounts();
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            return false;
        if (root.GetArrayLength() > MaxPosts)
            throw new RecognitionBudgetExceededException();

        int attachments = 0;
        var threads = new HashSet<(string Site, string Board, ulong Thread)>();
        foreach (JsonElement post in root.EnumerateArray())
        {
            if (post.ValueKind != JsonValueKind.Object ||
                !post.TryGetProperty("postDescriptor", out JsonElement descriptor) ||
                descriptor.ValueKind != JsonValueKind.Object ||
                !TryUnsignedInteger(descriptor, "postNo", out ulong postNo) || postNo == 0 || postNo > long.MaxValue ||
                !TryUnsignedInteger(descriptor, "postSubNo", out ulong subNo) || subNo > long.MaxValue ||
                !descriptor.TryGetProperty("descriptor", out JsonElement threadDescriptor) ||
                threadDescriptor.ValueKind != JsonValueKind.Object ||
                !TryUnsignedInteger(threadDescriptor, "threadNo", out ulong threadNo) || threadNo == 0 || threadNo > long.MaxValue ||
                !threadDescriptor.TryGetProperty("boardDescriptor", out JsonElement board) ||
                board.ValueKind != JsonValueKind.Object ||
                !TryNonEmptyString(board, "boardCode") ||
                !board.TryGetProperty("siteDescriptor", out JsonElement site) ||
                site.ValueKind != JsonValueKind.Object ||
                !TryNonEmptyString(site, "site_name") ||
                !post.TryGetProperty("_postImages", out JsonElement images) ||
                images.ValueKind != JsonValueKind.Array)
                return false;

            threads.Add((site.GetProperty("site_name").GetString()!, board.GetProperty("boardCode").GetString()!, threadNo));
            attachments += images.GetArrayLength();
            if (attachments > MaxAttachments)
                throw new RecognitionBudgetExceededException();
        }

        counts = new(threads.Count, root.GetArrayLength(), attachments);
        return true;
    }

    private static bool TryRecognizeYChan(JsonElement root, out ThreadArchiveRecognitionCounts counts)
    {
        counts = EmptyCounts();
        if (root.ValueKind != JsonValueKind.Object ||
            !TryNonEmptyString(root, "Id") || !TryNonEmptyString(root, "Board") ||
            !TryNonEmptyString(root, "Url") ||
            !root.TryGetProperty("ThreadPosts", out JsonElement posts) ||
            posts.ValueKind != JsonValueKind.Array || posts.GetArrayLength() == 0)
            return false;
        if (posts.GetArrayLength() > MaxPosts)
            throw new RecognitionBudgetExceededException();

        int attachments = 0;
        foreach (JsonElement post in posts.EnumerateArray())
        {
            if (post.ValueKind != JsonValueKind.Object ||
                !TryUnsignedInteger(post, "post_id", out ulong postId) || postId == 0)
                return false;

            if (post.TryGetProperty("files", out JsonElement files))
            {
                if (files.ValueKind != JsonValueKind.Array)
                    return false;
                attachments += files.GetArrayLength();
                if (attachments > MaxAttachments)
                    throw new RecognitionBudgetExceededException();
            }
        }

        counts = new(1, posts.GetArrayLength(), attachments);
        return true;
    }

    // Exact canonical UInt64 tokens: never accept strings, fractions, exponents or -0.
    // This is a local bound, not a claimed upper bound of the 4chan API.
    private static bool TryUnsignedInteger(JsonElement objectElement, string propertyName, out ulong number)
    {
        number = 0;
        if (!objectElement.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Number) return false;
        foreach (char c in value.GetRawText())
            if (c < '0' || c > '9') return false;
        return value.TryGetUInt64(out number);
    }

    private static bool TryNonEmptyString(JsonElement objectElement, string propertyName)
    {
        return objectElement.TryGetProperty(propertyName, out JsonElement value) &&
            value.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(value.GetString());
    }

    private static bool ContainsDuplicateProperty(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { MaxDepth = MaxJsonDepth });
        var scopes = new Stack<HashSet<string>>();
        int tokens = 0;
        while (reader.Read())
        {
            if (++tokens > MaxTokens || reader.ValueSpan.Length > MaxTokenBytes)
                throw new RecognitionBudgetExceededException();
            if ((reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName) &&
                !ValidEscapedUnicode(reader.ValueSpan))
                throw new RecognitionInvalidUnicodeException();
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                scopes.Push(new(StringComparer.Ordinal));
            }
            else if (reader.TokenType == JsonTokenType.EndObject)
            {
                scopes.Pop();
            }
            else if (reader.TokenType == JsonTokenType.PropertyName)
            {
                if (scopes.Count == 0 || !scopes.Peek().Add(reader.GetString()!))
                    return true;
            }
        }

        return false;
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

    private static ThreadArchiveRecognitionCounts EmptyCounts() => new(0, 0, 0);

    private static ThreadArchiveRecognitionResult Rejected(ThreadArchiveRecognitionError error) =>
        new(ThreadArchiveRecognitionStatus.Rejected, ThreadArchiveRecognitionFormat.None,
            ImmutableArray<ThreadArchiveRecognitionFormat>.Empty, EmptyCounts(), error);
}
