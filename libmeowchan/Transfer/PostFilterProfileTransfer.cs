using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using MeowChan.Archive;
using MeowChan.Persistence;

namespace MeowChan.Transfer;

/// <summary>Reasons why a post-filter image cannot cross a profile boundary.</summary>
internal enum PostFilterTransferError
{
    None,
    StoreUnavailable,
    UnsupportedVersion,
    InvalidDocument,
    Conflict
}

/// <summary>
/// A detached, validated post-filter image. It contains values only: no profile path,
/// lease, file handle, download instruction or executable definition crosses the boundary.
/// </summary>
internal sealed record PostFilterTransferSnapshot(
    long SourceRevision,
    HiddenPostsAction HiddenPostsAction,
    ImmutableArray<PostFilterRule> Rules,
    ImmutableArray<HiddenPost> HiddenPosts)
{
    internal PostFilterSnapshot AsStoreSnapshot() => new(SourceRevision, HiddenPostsAction, Rules, HiddenPosts,
        PostFilterStoreState.Ready);
}

internal sealed record PostFilterTransferExport(byte[]? Bytes, PostFilterTransferError Error,
    PostFilterTransferSnapshot? Snapshot = null)
{
    internal bool IsExported => Bytes != null && Error == PostFilterTransferError.None && Snapshot != null;
}

internal sealed record PostFilterTransferInspection(PostFilterTransferSnapshot? Snapshot,
    PostFilterTransferError Error)
{
    internal bool IsValid => Snapshot != null && Error == PostFilterTransferError.None;
}

internal sealed record PostFilterTransferImport(PostFilterSnapshot? Snapshot, PostFilterTransferError Error)
{
    internal bool IsImported => Snapshot != null && Error == PostFilterTransferError.None;
}

/// <summary>
/// Strict profile-transfer codec for the P2B post-filter store. This is deliberately
/// separate from native media transfer: post-filter rules are profile policy, not media
/// metadata, and must never be inferred from a thread/package identity.
/// </summary>
internal static class PostFilterProfileTransfer
{
    private const string Format = "meowchan-post-filters-transfer";
    private const int Version = 1;
    private const int MaximumDocumentBytes = PostFilterStore.MaximumFileBytes;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// Exports a ready store, including an explicitly empty missing/old profile store.
    /// Unsupported, damaged or recovery-pending data returns an error instead of becoming
    /// an empty transfer. This is the no-silent-loss boundary.
    /// </summary>
    internal static PostFilterTransferExport Export(PostFilterStore store,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        cancellationToken.ThrowIfCancellationRequested();
        PostFilterSnapshot source = store.Load();
        if (!source.CanWrite)
        {
            return new(null, source.State == PostFilterStoreState.Unsupported
                ? PostFilterTransferError.UnsupportedVersion
                : PostFilterTransferError.StoreUnavailable);
        }

        try
        {
            PostFilterTransferSnapshot snapshot = FromStore(source, cancellationToken);
            return new(Serialize(snapshot, cancellationToken), PostFilterTransferError.None, snapshot);
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidDataException) { return new(null, PostFilterTransferError.InvalidDocument); }
    }

    /// <summary>Parses a transfer/backup without touching a profile.</summary>
    internal static PostFilterTransferInspection Inspect(ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.Length == 0 || bytes.Length > MaximumDocumentBytes)
            return new(null, PostFilterTransferError.InvalidDocument);
        try
        {
            if (bytes.Length >= 3 && bytes.Span[..3].SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }))
                return new(null, PostFilterTransferError.InvalidDocument);
            _ = StrictUtf8.GetCharCount(bytes.Span);
            using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                MaxDepth = 12,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false
            });
            JsonElement root = document.RootElement;
            Fields(root, cancellationToken, "format", "version", "sourceRevision", "hiddenPostsAction", "rules", "hiddenPosts");
            Required(root, cancellationToken, "format", "version", "sourceRevision", "hiddenPostsAction", "rules", "hiddenPosts");
            if (Text(root, "format", cancellationToken) != Format)
                return new(null, PostFilterTransferError.InvalidDocument);
            if (Integer(root, "version", cancellationToken) != Version)
                return new(null, PostFilterTransferError.UnsupportedVersion);
            long sourceRevision = Integer64(root, "sourceRevision", cancellationToken);
            if (sourceRevision < 0) return new(null, PostFilterTransferError.InvalidDocument);
            HiddenPostsAction action = ParseAction(root.GetProperty("hiddenPostsAction"), cancellationToken);
            ImmutableArray<PostFilterRule> rules = ParseRules(root.GetProperty("rules"), cancellationToken);
            ImmutableArray<HiddenPost> hidden = ParseHiddenPosts(root.GetProperty("hiddenPosts"), cancellationToken);
            PostFilterTransferSnapshot result = new(sourceRevision, action, rules, hidden);
            ValidateSnapshot(result, cancellationToken);
            return new(result, PostFilterTransferError.None);
        }
        catch (OperationCanceledException) { throw; }
        catch (UnsupportedTransferVersionException) { return new(null, PostFilterTransferError.UnsupportedVersion); }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or InvalidDataException
            or ArgumentException or InvalidOperationException or FormatException or OverflowException
            or KeyNotFoundException or NotSupportedException)
        { return new(null, PostFilterTransferError.InvalidDocument); }
    }

    /// <summary>
    /// Imports a complete image with the target store's normal lease, revision,
    /// backup and admission semantics. No merge is attempted; an expected-revision
    /// conflict leaves the target untouched.
    /// </summary>
    internal static PostFilterTransferImport Import(PostFilterStore target, ReadOnlyMemory<byte> bytes,
        long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        PostFilterTransferInspection inspected = Inspect(bytes, cancellationToken);
        if (!inspected.IsValid)
            return new(null, inspected.Error);
        return ImportSnapshot(target, inspected.Snapshot!, expectedRevision, cancellationToken);
    }

    internal static PostFilterTransferImport ImportAdmitted(PostFilterStore target, ReadOnlyMemory<byte> bytes,
        long expectedRevision, ProfileAdmissionToken admission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        PostFilterTransferInspection inspected = Inspect(bytes, cancellationToken);
        if (!inspected.IsValid)
            return new(null, inspected.Error);
        return ImportAdmitted(target, inspected.Snapshot!, expectedRevision, admission, cancellationToken);
    }

    internal static PostFilterTransferImport ImportSnapshot(PostFilterStore target,
        PostFilterTransferSnapshot snapshot, long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(snapshot);
        try
        {
            PostFilterSnapshot restored = target.RestoreSnapshot(snapshot.AsStoreSnapshot(), expectedRevision, cancellationToken);
            return new(restored, PostFilterTransferError.None);
        }
        catch (PostFilterRevisionException error) { return new(null, RevisionError(error.Current)); }
        catch (InvalidDataException) { return new(null, PostFilterTransferError.InvalidDocument); }
        catch (ArgumentException) { return new(null, PostFilterTransferError.InvalidDocument); }
    }

    internal static PostFilterTransferImport ImportAdmitted(PostFilterStore target,
        PostFilterTransferSnapshot snapshot, long expectedRevision, ProfileAdmissionToken admission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(snapshot);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateSnapshot(snapshot, cancellationToken);
            PostFilterSnapshot restored = target.RestoreSnapshotAdmitted(snapshot.AsStoreSnapshot(), expectedRevision,
                admission, cancellationToken);
            return new(restored, PostFilterTransferError.None);
        }
        catch (PostFilterRevisionException error) { return new(null, RevisionError(error.Current)); }
        catch (InvalidDataException) { return new(null, PostFilterTransferError.InvalidDocument); }
        catch (ArgumentException) { return new(null, PostFilterTransferError.InvalidDocument); }
    }

    // The explicit backup API uses the same bounded image and restore path. Keeping it
    // as a separate domain type prevents callers from treating a byte backup as a
    // generic filesystem copy or importing unrelated profile files.
    internal static PostFilterTransferExport Backup(PostFilterStore store,
        CancellationToken cancellationToken = default) => Export(store, cancellationToken);

    internal static PostFilterTransferImport RestoreBackup(PostFilterStore target, ReadOnlyMemory<byte> bytes,
        long expectedRevision, CancellationToken cancellationToken = default) =>
        Import(target, bytes, expectedRevision, cancellationToken);

    internal static PostFilterTransferImport RestoreBackupAdmitted(PostFilterStore target, ReadOnlyMemory<byte> bytes,
        long expectedRevision, ProfileAdmissionToken admission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        PostFilterTransferInspection inspected = Inspect(bytes, cancellationToken);
        if (!inspected.IsValid)
            return new(null, inspected.Error);
        return ImportAdmitted(target, inspected.Snapshot!, expectedRevision, admission, cancellationToken);
    }

    private static PostFilterTransferError RevisionError(PostFilterSnapshot current) =>
        current.State == PostFilterStoreState.Unsupported ? PostFilterTransferError.UnsupportedVersion
        : !current.CanWrite ? PostFilterTransferError.StoreUnavailable
        : PostFilterTransferError.Conflict;

    private static PostFilterTransferSnapshot FromStore(PostFilterSnapshot source, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (source.State != PostFilterStoreState.Ready || source.Error != null)
            throw new InvalidDataException("The post filter store is not exportable.");
        var rules = ImmutableArray.CreateBuilder<PostFilterRule>(source.Rules.Count);
        foreach (PostFilterRule rule in source.Rules)
        {
            token.ThrowIfCancellationRequested();
            // Unsupported rules retain raw JSON for local forward compatibility, but
            // this release cannot safely assign them to another profile.
            if (!rule.CanApply || rule.RawDefinition != null)
                throw new InvalidDataException("The post filter store contains an unsupported rule.");
            rules.Add(rule);
        }
        var hidden = ImmutableArray.CreateBuilder<HiddenPost>(source.HiddenPosts.Count);
        foreach (HiddenPost entry in source.HiddenPosts)
        {
            token.ThrowIfCancellationRequested();
            hidden.Add(entry);
        }
        PostFilterTransferSnapshot result = new(source.Revision, source.HiddenPostsAction,
            rules.ToImmutable(), hidden.ToImmutable());
        ValidateSnapshot(result, token);
        return result;
    }

    private static byte[] Serialize(PostFilterTransferSnapshot value, CancellationToken token)
    {
        ValidateSnapshot(value, token);
        using MemoryStream output = new();
        using (Utf8JsonWriter writer = new(output, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("format", Format);
            writer.WriteNumber("version", Version);
            writer.WriteNumber("sourceRevision", value.SourceRevision);
            writer.WriteString("hiddenPostsAction", value.HiddenPostsAction == HiddenPostsAction.Hide ? "hide" : "stub");
            writer.WriteStartArray("rules");
            foreach (PostFilterRule rule in value.Rules)
            {
                token.ThrowIfCancellationRequested();
                writer.WriteStartObject();
                writer.WriteString("id", rule.Id);
                writer.WriteBoolean("enabled", rule.Enabled);
                writer.WriteString("field", Field(rule.Field));
                writer.WriteString("match", rule.Match == PostFilterMatch.Exact ? "exact" : "contains");
                writer.WriteString("pattern", rule.Pattern);
                writer.WriteString("action", Action(rule.Action));
                writer.WriteBoolean("replies", rule.Replies);
                if (rule.Source != null) writer.WriteString("source", rule.Source);
                if (rule.Board != null) writer.WriteString("board", rule.Board);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("hiddenPosts");
            foreach (HiddenPost entry in value.HiddenPosts)
            {
                token.ThrowIfCancellationRequested();
                writer.WriteStartObject();
                writer.WriteString("source", entry.Thread.Source);
                writer.WriteString("board", entry.Thread.Board);
                writer.WriteString("thread", entry.Thread.Thread);
                writer.WriteString("post", entry.Post);
                writer.WriteBoolean("replies", entry.Replies);
                writer.WriteString("added", PostFilterRules.FormatAdded(entry.Added));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
        }
        token.ThrowIfCancellationRequested();
        if (output.Length > MaximumDocumentBytes)
            throw new InvalidDataException("The post filter transfer is too large.");
        return output.ToArray();
    }

    private static ImmutableArray<PostFilterRule> ParseRules(JsonElement value, CancellationToken token)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > ProfileStoreLimits.PostFilterRuleCeiling)
            throw new InvalidDataException("Invalid post filter transfer rules.");
        var result = ImmutableArray.CreateBuilder<PostFilterRule>(value.GetArrayLength());
        HashSet<Guid> ids = new();
        foreach (JsonElement item in value.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            Fields(item, token, "id", "enabled", "field", "match", "pattern", "action", "replies", "source", "board");
            Required(item, token, "id", "enabled", "field", "match", "pattern", "action", "replies");
            Guid id = Guid.Parse(Text(item, "id", token));
            if (id == Guid.Empty || !ids.Add(id)) throw new InvalidDataException("Duplicate post filter rule.");
            PostFilterField field = ParseField(item.GetProperty("field"), token);
            PostFilterMatch match = ParseMatch(item.GetProperty("match"), token);
            PostFilterAction action = ParseRuleAction(item.GetProperty("action"), token);
            string pattern = Text(item, "pattern", token);
            bool replies = Boolean(item, "replies", token);
            string? source = OptionalScope(item, "source", token);
            string? board = OptionalScope(item, "board", token);
            result.Add(new(id, Boolean(item, "enabled", token), field, match, pattern, action, replies, source, board));
        }
        return result.ToImmutable();
    }

    private static ImmutableArray<HiddenPost> ParseHiddenPosts(JsonElement value, CancellationToken token)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > ProfileStoreLimits.HiddenPostCeiling)
            throw new InvalidDataException("Invalid post filter transfer hidden posts.");
        var result = ImmutableArray.CreateBuilder<HiddenPost>(value.GetArrayLength());
        HashSet<(ResourceId Thread, string Post)> keys = new();
        foreach (JsonElement item in value.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            Fields(item, token, "source", "board", "thread", "post", "replies", "added");
            ResourceId thread = new(Text(item, "source", token), Text(item, "board", token), Text(item, "thread", token));
            string post = Text(item, "post", token);
            PostFilterRules.ValidateHiddenPost(thread, post);
            HiddenPost hidden = new(thread, post, Boolean(item, "replies", token),
                DateTimeOffset.Parse(Text(item, "added", token), null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime());
            if (!keys.Add(hidden.Key)) throw new InvalidDataException("Duplicate hidden post.");
            result.Add(hidden);
        }
        return result.ToImmutable();
    }

    private static void ValidateSnapshot(PostFilterTransferSnapshot value, CancellationToken token)
    {
        if (value.SourceRevision < 0 || !Enum.IsDefined(value.HiddenPostsAction)
            || value.Rules.IsDefault || value.HiddenPosts.IsDefault
            || value.Rules.Length > ProfileStoreLimits.PostFilterRuleCeiling
            || value.HiddenPosts.Length > ProfileStoreLimits.HiddenPostCeiling)
            throw new InvalidDataException("The post filter transfer snapshot is invalid.");
        HashSet<Guid> ids = new();
        HashSet<string> duplicateRules = new(StringComparer.Ordinal);
        foreach (PostFilterRule rule in value.Rules)
        {
            token.ThrowIfCancellationRequested();
            if (rule == null || rule.Id == Guid.Empty || !rule.CanApply || rule.RawDefinition != null || !ids.Add(rule.Id))
                throw new InvalidDataException("The post filter transfer snapshot contains an invalid rule.");
            PostFilterRuleDraft normalized;
            try
            {
                normalized = PostFilterRules.Normalize(new(rule.Field, rule.Match, rule.Pattern,
                    rule.Action, rule.Replies, rule.Source, rule.Board, rule.Enabled));
            }
            catch (ArgumentException error)
            {
                throw new InvalidDataException("The post filter transfer snapshot contains an invalid rule.", error);
            }
            if (normalized.Field != rule.Field || normalized.Match != rule.Match || normalized.Pattern != rule.Pattern
                || normalized.Action != rule.Action || normalized.Source != rule.Source || normalized.Board != rule.Board
                || !duplicateRules.Add(rule.DuplicateKey))
                throw new InvalidDataException("The post filter transfer snapshot contains an invalid rule.");
        }
        HashSet<(ResourceId Thread, string Post)> hidden = new();
        foreach (HiddenPost entry in value.HiddenPosts)
        {
            token.ThrowIfCancellationRequested();
            if (entry == null) throw new InvalidDataException("The post filter transfer snapshot contains an invalid hidden post.");
            try { PostFilterRules.ValidateHiddenPost(entry.Thread, entry.Post); }
            catch (ArgumentException error)
            {
                throw new InvalidDataException("The post filter transfer snapshot contains an invalid hidden post.", error);
            }
            if (!hidden.Add(entry.Key)) throw new InvalidDataException("Duplicate hidden post.");
        }
    }

    private static void Fields(JsonElement value, CancellationToken token, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected a post filter transfer object.");
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            token.ThrowIfCancellationRequested();
            if (!names.Add(property.Name) || !allowed.Contains(property.Name, StringComparer.Ordinal))
                throw new InvalidDataException("The post filter transfer contains an unknown member.");
        }
    }

    private static void Required(JsonElement value, CancellationToken token, params string[] names)
    {
        foreach (string name in names)
        {
            token.ThrowIfCancellationRequested();
            if (!value.TryGetProperty(name, out _)) throw new InvalidDataException("The post filter transfer lacks a required member.");
        }
    }

    private static string Text(JsonElement value, string name, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        JsonElement element = value.GetProperty(name);
        if (element.ValueKind != JsonValueKind.String || element.GetString() is not string text)
            throw new InvalidDataException("Invalid post filter transfer text.");
        return text;
    }
    private static bool Boolean(JsonElement value, string name, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        JsonElement element = value.GetProperty(name);
        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException("Invalid post filter transfer boolean.");
        return element.GetBoolean();
    }
    private static int Integer(JsonElement value, string name, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!value.GetProperty(name).TryGetInt32(out int result)) throw new InvalidDataException("Invalid post filter transfer version.");
        return result;
    }
    private static long Integer64(JsonElement value, string name, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!value.GetProperty(name).TryGetInt64(out long result)) throw new InvalidDataException("Invalid post filter transfer revision.");
        return result;
    }
    private static string? OptionalScope(JsonElement value, string name, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!value.TryGetProperty(name, out JsonElement element) || element.ValueKind == JsonValueKind.Null)
            return null;
        if (element.ValueKind != JsonValueKind.String || element.GetString() is not string text)
            throw new InvalidDataException("Invalid post filter transfer scope.");
        return text.Length == 0 ? null : text;
    }
    private static HiddenPostsAction ParseAction(JsonElement value, CancellationToken token) =>
        TextValue(value, token) switch { "stub" => HiddenPostsAction.Stub, "hide" => HiddenPostsAction.Hide, _ => throw new InvalidDataException("Invalid hidden posts action.") };
    private static PostFilterField ParseField(JsonElement value, CancellationToken token) =>
        TextValue(value, token) switch { "text" => PostFilterField.Text, "subject" => PostFilterField.Subject, "filename" => PostFilterField.Filename, "md5" => PostFilterField.Md5, _ => throw new InvalidDataException("Invalid post filter field.") };
    private static PostFilterMatch ParseMatch(JsonElement value, CancellationToken token) =>
        TextValue(value, token) switch { "contains" => PostFilterMatch.Contains, "exact" => PostFilterMatch.Exact, _ => throw new InvalidDataException("Invalid post filter match.") };
    private static PostFilterAction ParseRuleAction(JsonElement value, CancellationToken token) =>
        TextValue(value, token) switch { "hide" => PostFilterAction.Hide, "stub" => PostFilterAction.Stub, "highlight" => PostFilterAction.Highlight, _ => throw new InvalidDataException("Invalid post filter action.") };
    private static string TextValue(JsonElement value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not string text) throw new InvalidDataException("Invalid post filter transfer value.");
        return text;
    }
    private static string Field(PostFilterField field) => field switch { PostFilterField.Subject => "subject", PostFilterField.Filename => "filename", PostFilterField.Md5 => "md5", _ => "text" };
    private static string Action(PostFilterAction action) => action switch { PostFilterAction.Stub => "stub", PostFilterAction.Highlight => "highlight", _ => "hide" };
    private sealed class UnsupportedTransferVersionException : Exception;
}
