using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using MeowChan.Archive;
using MeowChan.Favorites;
using MeowChan.Persistence;

namespace MeowChan.Transfer;

internal enum NativePrivateMetadataCodecError
{
    None, BudgetExceeded, InvalidUtf8, InvalidJson, InvalidField,
    DuplicateIdentity, OutOfScope, UnsupportedSchema, InvalidPresence
}

internal sealed record NativePrivateMetadataInspection(
    NativePrivateMetadataProjection? Projection, NativePrivateMetadataCodecError Error)
{
    internal bool IsValid => Projection != null && Error == NativePrivateMetadataCodecError.None;
}

/// <summary>Strict pure codec for selected private metadata. It carries no local authority.</summary>
internal static class NativePrivateMetadataCodec
{
    internal const int MaximumDocumentBytes = 16 * 1024 * 1024;
    internal const int MaximumThreads = 1_000;
    internal const int MaximumMediaFavorites = 50_000;
    internal const int MaximumBookmarks = 10_000;
    private const int MaximumIdentifierCodeUnits = 256;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonWriterOptions WriterOptions = new() { SkipValidation = false, Indented = false };

    internal static byte[] Serialize(NativePrivateMetadataProjection value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            Validate(value, cancellationToken);
            using CappedBuffer output = new(cancellationToken, MaximumDocumentBytes);
            using (Utf8JsonWriter writer = new(output, WriterOptions))
            {
                WriteDocument(writer, value, cancellationToken);
                writer.Flush();
            }
            cancellationToken.ThrowIfCancellationRequested();
            return output.ToArray();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or
            InvalidOperationException or OverflowException)
        { throw new InvalidDataException("The private metadata projection is invalid."); }
    }

    internal static NativePrivateMetadataInspection Inspect(ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.Length > MaximumDocumentBytes) return Fail(NativePrivateMetadataCodecError.BudgetExceeded);
        try
        {
            if (bytes.Length >= 3 && bytes.Span[0] == 0xef && bytes.Span[1] == 0xbb && bytes.Span[2] == 0xbf)
                return Fail(NativePrivateMetadataCodecError.InvalidUtf8);
            _ = StrictUtf8.GetCharCount(bytes.Span);
            cancellationToken.ThrowIfCancellationRequested();
            using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12,
                CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
            cancellationToken.ThrowIfCancellationRequested();
            JsonElement root = document.RootElement;
            Fields(root, cancellationToken, "schema", "threads", "threadFavorites", "mediaFavorites", "postBookmarksAndNotes");
            if (Text(root, "schema") != "meowchan-private-v1")
                return Fail(NativePrivateMetadataCodecError.UnsupportedSchema);

            ImmutableArray<ResourceId> threads = ParseThreads(root.GetProperty("threads"), cancellationToken);
            HashSet<ResourceId> scope = threads.ToHashSet();
            (bool threadSelected, ImmutableArray<ResourceId> threadFavorites) = ParseThreadFavorites(
                root.GetProperty("threadFavorites"), scope, cancellationToken);
            (bool mediaSelected, ImmutableArray<FavoriteAssetKey> mediaFavorites) = ParseMediaFavorites(
                root.GetProperty("mediaFavorites"), scope, cancellationToken);
            (bool notesSelected, ImmutableArray<PostBookmark> notes) = ParseBookmarks(
                root.GetProperty("postBookmarksAndNotes"), scope, cancellationToken);
            var projection = new NativePrivateMetadataProjection(threads,
                new(threadSelected, mediaSelected, notesSelected), threadFavorites, mediaFavorites, notes);
            Validate(projection, cancellationToken);
            return new(projection, NativePrivateMetadataCodecError.None);
        }
        catch (OperationCanceledException) { throw; }
        catch (CodecException e) { return Fail(e.Error); }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or
            InvalidDataException or ArgumentException or InvalidOperationException or FormatException or
            OverflowException or KeyNotFoundException or NotSupportedException)
        { return Fail(error is DecoderFallbackException ? NativePrivateMetadataCodecError.InvalidUtf8 : NativePrivateMetadataCodecError.InvalidJson); }
    }

    internal static void Validate(NativePrivateMetadataProjection value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateShape(value);
        // Refuse oversized aggregate output while visiting entries, before the full
        // note/identity/duplicate scan. CountingBuffer retains no encoded document.
        using (CountingBuffer measurement = new(cancellationToken))
        using (Utf8JsonWriter writer = new(measurement, WriterOptions))
        {
            WriteDocument(writer, value, cancellationToken);
            writer.Flush();
        }
        cancellationToken.ThrowIfCancellationRequested();
        HashSet<ResourceId> scope = new();
        foreach (ResourceId thread in value.Threads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateIdentity(thread);
            if (!scope.Add(thread)) throw new InvalidDataException("The private metadata projection contains duplicate thread identities.");
        }

        HashSet<ResourceId> threadFavorites = new();
        foreach (ResourceId thread in value.ThreadFavorites)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateIdentity(thread);
            if (!scope.Contains(thread) || !threadFavorites.Add(thread)) throw new InvalidDataException("A thread favorite is outside its selected scope or duplicated.");
        }
        HashSet<FavoriteAssetKey> mediaFavorites = new();
        foreach (FavoriteAssetKey favorite in value.MediaFavorites)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateMedia(favorite);
            if (!scope.Contains(favorite.Thread) || !mediaFavorites.Add(favorite)) throw new InvalidDataException("A media favorite is outside its selected scope or duplicated.");
        }
        HashSet<PostBookmarkKey> bookmarks = new();
        foreach (PostBookmark bookmark in value.PostBookmarksAndNotes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bookmark is null || bookmark.Key is null || bookmark.Note is null)
                throw new InvalidDataException("The private metadata projection contains an invalid bookmark.");
            ValidateIdentity(bookmark.Key.Thread);
            if (!scope.Contains(bookmark.Key.Thread) || !bookmarks.Add(bookmark.Key)) throw new InvalidDataException("A bookmark is outside its selected scope or duplicated.");
            ValidateText(bookmark.Key.Post, allowEmpty: false);
            if (bookmark.Note.Contains('\r')) throw new InvalidDataException("A transferred note must use canonical line endings.");
            try { _ = PostBookmarkStore.NormalizeNote(bookmark.Note); }
            catch (ArgumentException) { throw new InvalidDataException("A transferred note is invalid."); }
            ValidateText(bookmark.Note, allowEmpty: true);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static void ValidateShape(NativePrivateMetadataProjection value)
    {
        if (value.Threads.IsDefault || value.Threads.Length > MaximumThreads)
            throw new InvalidDataException("The private metadata projection exceeds its thread limit.");
        if (value.ThreadFavorites.IsDefault || value.MediaFavorites.IsDefault || value.PostBookmarksAndNotes.IsDefault)
            throw new InvalidDataException("The private metadata projection has an uninitialized collection.");
        if (!value.Selection.ThreadFavorites && !value.ThreadFavorites.IsEmpty
            || !value.Selection.MediaFavorites && !value.MediaFavorites.IsEmpty
            || !value.Selection.PostBookmarksAndNotes && !value.PostBookmarksAndNotes.IsEmpty)
            throw new InvalidDataException("An unselected private metadata category is not empty.");
        if (value.ThreadFavorites.Length > MaximumThreads || value.MediaFavorites.Length > MaximumMediaFavorites
            || value.PostBookmarksAndNotes.Length > MaximumBookmarks)
            throw new InvalidDataException("The private metadata projection exceeds an entry limit.");
    }

    private static void WriteDocument(Utf8JsonWriter writer, NativePrivateMetadataProjection value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        writer.WriteStartObject();
        writer.WriteString("schema", "meowchan-private-v1");
        WriteThreads(writer, "threads", value.Threads, token);
        WriteThreadFavorites(writer, value, token);
        WriteMediaFavorites(writer, value, token);
        WriteBookmarks(writer, value, token);
        writer.WriteEndObject();
        token.ThrowIfCancellationRequested();
    }

    // Cheap per-field shape bounds precede JsonWriter's reservation/encoding. Full
    // Unicode/control/scalar validation is separate and happens only within the
    // already measured aggregate budget. The counter retains no full document.
    private static void WriteText(Utf8JsonWriter writer, string name, string? value, int maximumCodeUnits)
    {
        if (value is null || value.Length > maximumCodeUnits)
            throw new InvalidDataException("The private metadata text exceeds its limit.");
        writer.WriteString(name, value);
    }

    private static void WriteThreads(Utf8JsonWriter writer, string name, ImmutableArray<ResourceId> values, CancellationToken token)
    {
        writer.WriteStartArray(name);
        foreach (ResourceId value in values) { token.ThrowIfCancellationRequested(); WriteIdentity(writer, value); }
        writer.WriteEndArray();
    }
    private static void WriteThreadFavorites(Utf8JsonWriter writer, NativePrivateMetadataProjection value, CancellationToken token)
    {
        if (!value.Selection.ThreadFavorites) { writer.WriteNull("threadFavorites"); return; }
        WriteThreads(writer, "threadFavorites", value.ThreadFavorites, token);
    }
    private static void WriteMediaFavorites(Utf8JsonWriter writer, NativePrivateMetadataProjection value, CancellationToken token)
    {
        if (!value.Selection.MediaFavorites) { writer.WriteNull("mediaFavorites"); return; }
        writer.WriteStartArray("mediaFavorites");
        foreach (FavoriteAssetKey favorite in value.MediaFavorites)
        {
            token.ThrowIfCancellationRequested();
            if (favorite is null) throw new InvalidDataException("The media favorite is invalid.");
            writer.WriteStartObject(); writer.WritePropertyName("thread"); WriteIdentity(writer, favorite.Thread);
            WriteText(writer, "mediaId", favorite.MediaIdentifier, MaximumIdentifierCodeUnits); WriteText(writer, "variant", favorite.Variant, MaximumIdentifierCodeUnits);
            WriteText(writer, "sha256", favorite.Sha256, 64); writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }
    private static void WriteBookmarks(Utf8JsonWriter writer, NativePrivateMetadataProjection value, CancellationToken token)
    {
        if (!value.Selection.PostBookmarksAndNotes) { writer.WriteNull("postBookmarksAndNotes"); return; }
        writer.WriteStartArray("postBookmarksAndNotes");
        foreach (PostBookmark bookmark in value.PostBookmarksAndNotes)
        {
            token.ThrowIfCancellationRequested();
            if (bookmark is null || bookmark.Key is null)
                throw new InvalidDataException("The bookmark is invalid.");
            writer.WriteStartObject(); writer.WritePropertyName("thread"); WriteIdentity(writer, bookmark.Key.Thread);
            WriteText(writer, "post", bookmark.Key.Post, MaximumIdentifierCodeUnits);
            WriteText(writer, "note", bookmark.Note, PostBookmarkStore.MaximumNoteCodepoints * 2); writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }
    private static void WriteIdentity(Utf8JsonWriter writer, ResourceId value)
    {
        if (value is null) throw new InvalidDataException("The transfer thread is invalid.");
        writer.WriteStartObject(); WriteText(writer, "source", value.Source, MaximumIdentifierCodeUnits);
        WriteText(writer, "board", value.Board, MaximumIdentifierCodeUnits);
        WriteText(writer, "thread", value.Thread, MaximumIdentifierCodeUnits); writer.WriteEndObject();
    }

    private static ImmutableArray<ResourceId> ParseThreads(JsonElement value, CancellationToken token)
    {
        JsonElement array = Array(value, MaximumThreads);
        var result = ImmutableArray.CreateBuilder<ResourceId>(array.GetArrayLength());
        HashSet<ResourceId> seen = new();
        foreach (JsonElement item in array.EnumerateArray())
        {
            token.ThrowIfCancellationRequested(); ResourceId id = ParseIdentity(item, token);
            if (!seen.Add(id)) throw Bad(NativePrivateMetadataCodecError.DuplicateIdentity); result.Add(id);
        }
        return result.ToImmutable();
    }
    private static (bool, ImmutableArray<ResourceId>) ParseThreadFavorites(JsonElement value, HashSet<ResourceId> scope, CancellationToken token)
    {
        if (value.ValueKind == JsonValueKind.Null) return (false, []);
        JsonElement array = Array(value, MaximumThreads); var result = ImmutableArray.CreateBuilder<ResourceId>(array.GetArrayLength());
        HashSet<ResourceId> seen = new();
        foreach (JsonElement item in array.EnumerateArray()) { token.ThrowIfCancellationRequested(); ResourceId id = ParseIdentity(item, token); if (!scope.Contains(id)) throw Bad(NativePrivateMetadataCodecError.OutOfScope); if (!seen.Add(id)) throw Bad(NativePrivateMetadataCodecError.DuplicateIdentity); result.Add(id); }
        return (true, result.ToImmutable());
    }
    private static (bool, ImmutableArray<FavoriteAssetKey>) ParseMediaFavorites(JsonElement value, HashSet<ResourceId> scope, CancellationToken token)
    {
        if (value.ValueKind == JsonValueKind.Null) return (false, []);
        JsonElement array = Array(value, MaximumMediaFavorites); var result = ImmutableArray.CreateBuilder<FavoriteAssetKey>(array.GetArrayLength());
        HashSet<FavoriteAssetKey> seen = new();
        foreach (JsonElement item in array.EnumerateArray())
        {
            token.ThrowIfCancellationRequested(); Fields(item, token, "thread", "mediaId", "variant", "sha256");
            ResourceId thread = ParseIdentity(item.GetProperty("thread"), token); if (!scope.Contains(thread)) throw Bad(NativePrivateMetadataCodecError.OutOfScope);
            var key = new FavoriteAssetKey(thread, Text(item, "mediaId"), Text(item, "variant"), Text(item, "sha256")); ValidateMedia(key);
            if (!seen.Add(key)) throw Bad(NativePrivateMetadataCodecError.DuplicateIdentity); result.Add(key);
        }
        return (true, result.ToImmutable());
    }
    private static (bool, ImmutableArray<PostBookmark>) ParseBookmarks(JsonElement value, HashSet<ResourceId> scope, CancellationToken token)
    {
        if (value.ValueKind == JsonValueKind.Null) return (false, []);
        JsonElement array = Array(value, MaximumBookmarks); var result = ImmutableArray.CreateBuilder<PostBookmark>(array.GetArrayLength());
        HashSet<PostBookmarkKey> seen = new();
        foreach (JsonElement item in array.EnumerateArray())
        {
            token.ThrowIfCancellationRequested(); Fields(item, token, "thread", "post", "note");
            ResourceId thread = ParseIdentity(item.GetProperty("thread"), token); if (!scope.Contains(thread)) throw Bad(NativePrivateMetadataCodecError.OutOfScope);
            var key = new PostBookmarkKey(thread, Text(item, "post")); string note = Text(item, "note");
            if (note.Contains('\r')) throw Bad(NativePrivateMetadataCodecError.InvalidField);
            try { key.Validate(); _ = PostBookmarkStore.NormalizeNote(note); } catch (ArgumentException) { throw Bad(NativePrivateMetadataCodecError.InvalidField); }
            var bookmark = new PostBookmark(key, note); if (!seen.Add(key)) throw Bad(NativePrivateMetadataCodecError.DuplicateIdentity); result.Add(bookmark);
        }
        return (true, result.ToImmutable());
    }
    private static ResourceId ParseIdentity(JsonElement value, CancellationToken token)
    {
        Fields(value, token, "source", "board", "thread"); var id = new ResourceId(Text(value, "source"), Text(value, "board"), Text(value, "thread")); ValidateIdentity(id); return id;
    }
    private static void ValidateIdentity(ResourceId value) { ArchiveValidation.ValidateId(value); ValidateText(value.Source!, false); ValidateText(value.Board!, false); ValidateText(value.Thread!, false); }
    private static void ValidateMedia(FavoriteAssetKey value)
    {
        try { FavoriteStore.Validate(value); } catch (ArgumentException) { throw new InvalidDataException("The media favorite identity is invalid."); }
        ValidateIdentity(value.Thread); ValidateText(value.MediaIdentifier, false); ValidateText(value.Variant, false);
        if (value.Sha256.Length != 64 || !value.Sha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')) throw new InvalidDataException("The media favorite hash is invalid.");
    }
    private static void ValidateText(string value, bool allowEmpty)
    {
        if (!allowEmpty && (string.IsNullOrWhiteSpace(value) || value.Length > MaximumIdentifierCodeUnits) || allowEmpty && value.Length > PostBookmarkStore.MaximumNoteCodepoints * 2)
            throw new InvalidDataException("The private metadata text is invalid.");
        try { _ = StrictUtf8.GetByteCount(value); } catch (EncoderFallbackException) { throw new InvalidDataException("The private metadata text is invalid."); }
        if (!allowEmpty && value.Any(char.IsControl)) throw new InvalidDataException("The private metadata identity is invalid.");
    }
    private static JsonElement Array(JsonElement value, int maximum) { if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > maximum) throw Bad(NativePrivateMetadataCodecError.InvalidField); return value; }
    private static string Text(JsonElement node, string name)
    { JsonElement value = node.GetProperty(name); if (value.ValueKind != JsonValueKind.String || value.GetString() is not string text) throw Bad(NativePrivateMetadataCodecError.InvalidField); return text; }
    private static void Fields(JsonElement node, CancellationToken token, params string[] allowed)
    {
        if (node.ValueKind != JsonValueKind.Object) throw Bad(NativePrivateMetadataCodecError.InvalidField);
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonProperty property in node.EnumerateObject())
        {
            token.ThrowIfCancellationRequested();
            if (!seen.Add(property.Name) || !allowed.Contains(property.Name, StringComparer.Ordinal))
                throw Bad(NativePrivateMetadataCodecError.InvalidField);
        }
        if (seen.Count != allowed.Length) throw Bad(NativePrivateMetadataCodecError.InvalidField);
    }
    private static CodecException Bad(NativePrivateMetadataCodecError error) => new(error);
    private static NativePrivateMetadataInspection Fail(NativePrivateMetadataCodecError error) => new(null, error);
    private sealed class CodecException(NativePrivateMetadataCodecError error) : Exception { internal NativePrivateMetadataCodecError Error => error; }
    private sealed class CountingBuffer(CancellationToken token) : IBufferWriter<byte>, IDisposable
    {
        private long _count;
        private byte[] _scratch = System.Array.Empty<byte>();
        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            token.ThrowIfCancellationRequested();
            if (sizeHint < 0 || sizeHint > 512 * 1024)
                throw new InvalidDataException("The private metadata reservation exceeds its limit.");
            int size = Math.Max(4096, sizeHint);
            if (_scratch.Length < size) _scratch = new byte[size];
            return _scratch;
        }
        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
        public void Advance(int count)
        {
            token.ThrowIfCancellationRequested();
            if (count < 0 || count > _scratch.Length || count > MaximumDocumentBytes - _count)
                throw new InvalidDataException("The private metadata projection exceeds its size limit.");
            _count += count;
        }
        public void Dispose() { }
    }
    private sealed class CappedBuffer(CancellationToken token, int cap) : IBufferWriter<byte>, IDisposable
    {
        private readonly MemoryStream _storage = new(); private byte[] _scratch = System.Array.Empty<byte>();
        public Memory<byte> GetMemory(int sizeHint = 0) { token.ThrowIfCancellationRequested(); if (sizeHint < 0 || sizeHint > cap) throw new InvalidDataException("The private metadata projection exceeds its size limit."); int size = Math.Max(4096, sizeHint); if (_scratch.Length < size) _scratch = new byte[size]; return _scratch; }
        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
        public void Advance(int count) { token.ThrowIfCancellationRequested(); if (count < 0 || count > _scratch.Length || count > cap - _storage.Length) throw new InvalidDataException("The private metadata projection exceeds its size limit."); _storage.Write(_scratch.AsSpan(0, count)); }
        internal byte[] ToArray() => _storage.ToArray(); public void Dispose() => _storage.Dispose();
    }
}
