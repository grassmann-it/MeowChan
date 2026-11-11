using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using MeowChan.Archive;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;

namespace MeowChan.Tags;

/// <summary>Constant domain rejections. Values never contain or echo names or identities.</summary>
internal enum PrivateTagStoreError
{
    None,
    StaleRevision,
    InvalidName,
    UnknownTag,
    NameCollision,
    InvalidThread,
    InvalidTarget,
    CapacityExceeded,
    RevisionOverflow
}

/// <summary>A rejected tag operation. Nothing was written; the message is a constant.</summary>
internal sealed class PrivateTagStoreException : Exception
{
    internal PrivateTagStoreException(PrivateTagStoreError error)
        : base("The private tag operation was rejected: " + error + ".") => Error = error;

    internal PrivateTagStoreError Error { get; }
}

/// <summary>
/// One complete immutable view of the profile's own tags: a single shared vocabulary plus
/// separate thread and media assignments. There is no inheritance between the two target kinds.
/// Membership does not assert that a thread or media occurrence exists in the archive.
/// </summary>
internal sealed class PrivateTagStoreSnapshot
{
    internal PrivateTagStoreSnapshot(long revision, string fingerprint, IEnumerable<PrivateTagDefinition> tags,
        IEnumerable<PrivateThreadTagAssignment> threads, IEnumerable<PrivateMediaTagAssignment> media)
    {
        Revision = revision;
        Fingerprint = fingerprint;
        Tags = Array.AsReadOnly(tags.ToArray());
        ThreadAssignments = Array.AsReadOnly(threads.ToArray());
        MediaAssignments = Array.AsReadOnly(media.ToArray());
    }

    internal long Revision { get; }
    /// <summary>SHA-256 of the committed file bytes; the empty store hashes zero bytes.</summary>
    internal string Fingerprint { get; }
    internal IReadOnlyList<PrivateTagDefinition> Tags { get; }
    internal IReadOnlyList<PrivateThreadTagAssignment> ThreadAssignments { get; }
    internal IReadOnlyList<PrivateMediaTagAssignment> MediaAssignments { get; }

    internal PrivateTagDefinition? Find(TagId id) => Tags.FirstOrDefault(item => item.Id == id);

    /// <summary>Tags assigned to exactly this thread, in vocabulary order. Media tags are not included.</summary>
    internal IReadOnlyList<PrivateTagDefinition> TagsOf(ResourceId thread)
    {
        ArgumentNullException.ThrowIfNull(thread);
        var ids = ThreadAssignments.Where(item => item.Thread == thread).Select(item => (TagId)item.TagId).ToHashSet();
        return Tags.Where(item => ids.Contains(item.Id)).ToArray();
    }

    /// <summary>Tags assigned to exactly this media version, in vocabulary order. Thread tags are not included.</summary>
    internal IReadOnlyList<PrivateTagDefinition> TagsOf(PrivateMediaTagTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var ids = MediaAssignments.Where(item => item.Target == target).Select(item => (TagId)item.TagId).ToHashSet();
        return Tags.Where(item => ids.Contains(item.Id)).ToArray();
    }
}

/// <summary>Result of one accepted call. <see cref="Changed"/> is false for an idempotent no-op.</summary>
internal sealed record PrivateTagCommit(bool Changed, TagId Tag, PrivateTagStoreSnapshot Snapshot);

/// <summary>
/// Profile-owned persistence for own tags (contract: private-tag-store-v1). One JSON document,
/// rewritten atomically with a backup, guarded by the profile lease and store-writer admission.
/// Invalid or newer documents are never overwritten. No merge of tags, no transfer, no source
/// tags and no archive-existence checks live here.
/// </summary>
internal sealed class PrivateTagStore
{
    internal const string FileName = "private-tags.json";
    // Defensive work bounds, not promised product quotas.
    internal const int MaximumTags = PrivateTagCatalog.MaximumDefinitions;
    internal const int MaximumThreadAssignments = ThreadTagProjection.MaximumAssignments;
    internal const int MaximumMediaAssignments = MediaTagProjection.MaximumAssignments;
    internal const int MaximumBytes = 32 * 1024 * 1024;
    private const int CurrentVersion = 1;
    private const string Invalid = "The private tag index is invalid.";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ProfileLease _lease;
    private readonly string _path;

    internal PrivateTagStore(string profileDirectory, ProfileLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lease.EnsureMatches(profileDirectory);
        _lease = lease;
        _path = Path.Combine(lease.ProfileDirectory, FileName);
    }

    private sealed class Model
    {
        internal long Revision;
        internal PrivateTagCatalog Catalog = new();
        internal readonly List<PrivateThreadTagAssignment> Threads = new();
        internal readonly HashSet<PrivateThreadTagAssignment> ThreadSet = new();
        internal readonly List<PrivateMediaTagAssignment> Media = new();
        internal readonly HashSet<PrivateMediaTagAssignment> MediaSet = new();

        internal PrivateTagStoreSnapshot Snapshot(byte[] bytes) =>
            new(Revision, Convert.ToHexString(SHA256.HashData(bytes)), Catalog.Definitions, Threads, Media);
    }

    internal PrivateTagStoreSnapshot ReadSnapshot() => _lease.RunExclusive(() =>
    {
        (Model model, byte[] bytes) = Read();
        return model.Snapshot(bytes);
    });

    /// <summary>Creates a tag, or returns the existing tag with the same comparison key unchanged.</summary>
    internal PrivateTagCommit Define(string? name, long? expectedRevision = null, CancellationToken token = default)
    {
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter(token);
        return DefineAdmitted(name, admission, expectedRevision, token);
    }

    internal PrivateTagCommit DefineAdmitted(string? name, ProfileAdmissionToken admission,
        long? expectedRevision = null, CancellationToken token = default)
    {
        PrivateTagName parsed = RequireName(name);
        return Mutate(admission, expectedRevision, token, model =>
        {
            PrivateTagDefinition? existing = model.Catalog.Definitions.FirstOrDefault(item => item.Name == parsed);
            if (existing is not null) return (false, existing.Id);
            TagId id = new(Guid.NewGuid());
            if (!model.Catalog.TryDefine(id, parsed, model.Catalog.Revision, out PrivateTagCatalog next,
                out PrivateTagCatalogError error))
                throw new PrivateTagStoreException(Map(error));
            model.Catalog = next;
            return (true, id);
        });
    }

    /// <summary>Renames in place; the ID and every assignment are retained.</summary>
    internal PrivateTagCommit Rename(TagId tag, string? name, long? expectedRevision = null,
        CancellationToken token = default)
    {
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter(token);
        return RenameAdmitted(tag, name, admission, expectedRevision, token);
    }

    internal PrivateTagCommit RenameAdmitted(TagId tag, string? name, ProfileAdmissionToken admission,
        long? expectedRevision = null, CancellationToken token = default)
    {
        PrivateTagName parsed = RequireName(name);
        return Mutate(admission, expectedRevision, token, model =>
        {
            if (!model.Catalog.TryRename(tag, parsed, model.Catalog.Revision, out PrivateTagCatalog next,
                out PrivateTagCatalogError error))
                throw new PrivateTagStoreException(Map(error));
            bool changed = !ReferenceEquals(next, model.Catalog);
            model.Catalog = next;
            return (changed, tag);
        });
    }

    /// <summary>Idempotent thread membership. Media of that thread are not affected.</summary>
    internal PrivateTagCommit SetThread(TagId tag, ResourceId? thread, bool assigned, long? expectedRevision = null,
        CancellationToken token = default)
    {
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter(token);
        return SetThreadAdmitted(tag, thread, assigned, admission, expectedRevision, token);
    }

    internal PrivateTagCommit SetThreadAdmitted(TagId tag, ResourceId? thread, bool assigned,
        ProfileAdmissionToken admission, long? expectedRevision = null, CancellationToken token = default)
    {
        if (!IsThread(thread)) throw new PrivateTagStoreException(PrivateTagStoreError.InvalidThread);
        var assignment = new PrivateThreadTagAssignment(tag, thread!);
        return Mutate(admission, expectedRevision, token, model =>
        {
            RequireKnown(model, tag);
            if (model.ThreadSet.Contains(assignment) == assigned) return (false, tag);
            if (assigned)
            {
                if (model.Threads.Count >= MaximumThreadAssignments)
                    throw new PrivateTagStoreException(PrivateTagStoreError.CapacityExceeded);
                model.Threads.Add(assignment);
            }
            else model.Threads.Remove(assignment);
            return (true, tag);
        });
    }

    /// <summary>Idempotent membership of one exact media version. No thread inheritance or hash fan-out.</summary>
    internal PrivateTagCommit SetMedia(TagId tag, PrivateMediaTagTarget? target, bool assigned,
        long? expectedRevision = null, CancellationToken token = default)
    {
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter(token);
        return SetMediaAdmitted(tag, target, assigned, admission, expectedRevision, token);
    }

    internal PrivateTagCommit SetMediaAdmitted(TagId tag, PrivateMediaTagTarget? target, bool assigned,
        ProfileAdmissionToken admission, long? expectedRevision = null, CancellationToken token = default)
    {
        if (!IsTarget(target)) throw new PrivateTagStoreException(PrivateTagStoreError.InvalidTarget);
        var assignment = new PrivateMediaTagAssignment(tag, target!);
        return Mutate(admission, expectedRevision, token, model =>
        {
            RequireKnown(model, tag);
            if (model.MediaSet.Contains(assignment) == assigned) return (false, tag);
            if (assigned)
            {
                if (model.Media.Count >= MaximumMediaAssignments)
                    throw new PrivateTagStoreException(PrivateTagStoreError.CapacityExceeded);
                model.Media.Add(assignment);
            }
            else model.Media.Remove(assignment);
            return (true, tag);
        });
    }

    /// <summary>
    /// One tag on several exact media versions in one write: all of them or none. Each target is
    /// idempotent like <see cref="SetMedia"/>; a target that already has (or lacks) the tag changes nothing.
    /// An unknown tag, an invalid target or the capacity limit rejects the whole batch.
    /// </summary>
    internal PrivateTagCommit SetMediaBatch(TagId tag, IReadOnlyCollection<PrivateMediaTagTarget?> targets, bool assigned,
        long? expectedRevision = null, CancellationToken token = default)
    {
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter(token);
        return SetMediaBatchAdmitted(tag, targets, assigned, admission, expectedRevision, token);
    }

    internal PrivateTagCommit SetMediaBatchAdmitted(TagId tag, IReadOnlyCollection<PrivateMediaTagTarget?> targets, bool assigned,
        ProfileAdmissionToken admission, long? expectedRevision = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Any(target => !IsTarget(target))) throw new PrivateTagStoreException(PrivateTagStoreError.InvalidTarget);
        List<PrivateMediaTagAssignment> wanted = targets.Select(target => new PrivateMediaTagAssignment(tag, target!)).Distinct().ToList();
        return Mutate(admission, expectedRevision, token, model =>
        {
            RequireKnown(model, tag);
            if (assigned)
            {
                List<PrivateMediaTagAssignment> fresh = wanted.Where(item => !model.MediaSet.Contains(item)).ToList();
                if (fresh.Count == 0) return (false, tag);
                if (model.Media.Count + fresh.Count > MaximumMediaAssignments)
                    throw new PrivateTagStoreException(PrivateTagStoreError.CapacityExceeded);
                model.Media.AddRange(fresh);
            }
            else
            {
                HashSet<PrivateMediaTagAssignment> present = wanted.Where(model.MediaSet.Contains).ToHashSet();
                if (present.Count == 0) return (false, tag);
                model.Media.RemoveAll(present.Contains);
            }
            return (true, tag);
        });
    }

    /// <summary>
    /// Adds, for each handover, the media assignments of its earlier revision key to the new key, in one write.
    /// Only an assignment whose target names the same thread, post, position and bytes moves; the earlier
    /// assignments stay. Existing assignments are not duplicated, so repeating a handover changes nothing.
    /// </summary>
    internal PrivateTagCommit CarryMediaAssignmentsAdmitted(IReadOnlyCollection<MediaTagKeyHandover> handovers,
        ProfileAdmissionToken admission, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(handovers);
        return Mutate(admission, null, token, model =>
        {
            List<PrivateMediaTagAssignment> fresh = new();
            HashSet<PrivateMediaTagAssignment> seen = new();
            foreach (PrivateMediaTagAssignment assignment in model.Media)
            {
                PrivateMediaTagTarget old = assignment.Target;
                foreach (MediaTagKeyHandover handover in handovers)
                {
                    if (old.Reference.RevisionKey != handover.FromRevisionKey || old.Reference.Resource != handover.To.Resource
                        || old.Reference.PostId != handover.To.PostId || old.Reference.Position != handover.To.Position
                        || old.Sha256 != handover.Sha256) continue;
                    if (!PrivateMediaTagTarget.TryCreate(handover.To, old.MediaId, old.Variant, old.Role, old.Sha256,
                        out PrivateMediaTagTarget? moved, out _)) continue;
                    PrivateMediaTagAssignment next = new(assignment.TagId, moved!);
                    if (!model.MediaSet.Contains(next) && seen.Add(next)) fresh.Add(next);
                }
            }
            if (fresh.Count == 0) return (false, default);
            if (model.Media.Count + fresh.Count > MaximumMediaAssignments)
                throw new PrivateTagStoreException(PrivateTagStoreError.CapacityExceeded);
            model.Media.AddRange(fresh);
            return (true, default);
        });
    }

    /// <summary>
    /// Deletes one tag together with every thread and media assignment that uses it, in one write.
    /// An unknown tag is rejected rather than treated as already deleted.
    /// </summary>
    internal PrivateTagCommit Delete(TagId tag, long? expectedRevision = null, CancellationToken token = default)
    {
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter(token);
        return DeleteAdmitted(tag, admission, expectedRevision, token);
    }

    internal PrivateTagCommit DeleteAdmitted(TagId tag, ProfileAdmissionToken admission,
        long? expectedRevision = null, CancellationToken token = default)
        => Mutate(admission, expectedRevision, token, model =>
        {
            RequireKnown(model, tag);
            // The vocabulary type has no delete; rebuild it from the surviving definitions in order.
            PrivateTagCatalog rebuilt = new();
            foreach (PrivateTagDefinition item in model.Catalog.Definitions)
            {
                if (item.Id == tag) continue;
                if (!rebuilt.TryDefine(item.Id, item.Name, rebuilt.Revision, out PrivateTagCatalog next, out _))
                    throw new InvalidDataException(Invalid);
                rebuilt = next;
            }
            model.Catalog = rebuilt;
            model.Threads.RemoveAll(item => (TagId)item.TagId == tag);
            model.Media.RemoveAll(item => (TagId)item.TagId == tag);
            return (true, tag);
        });

    /// <summary>
    /// Drops every thread assignment and every media assignment that belongs to the given threads.
    /// Tags themselves stay in the vocabulary. Used after a thread was permanently deleted.
    /// The returned commit carries no tag.
    /// </summary>
    internal PrivateTagCommit RemoveThreads(IReadOnlyCollection<ResourceId> threads, CancellationToken token = default)
    {
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter(token);
        return RemoveThreadsAdmitted(threads, admission, token);
    }

    internal PrivateTagCommit RemoveThreadsAdmitted(IReadOnlyCollection<ResourceId> threads,
        ProfileAdmissionToken admission, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(threads);
        HashSet<ResourceId> selected = threads.Where(thread => thread is not null).ToHashSet();
        return Mutate(admission, null, token, model =>
        {
            int removed = model.Threads.RemoveAll(item => selected.Contains(item.Thread))
                + model.Media.RemoveAll(item => selected.Contains(item.Target.Reference.Resource));
            return (removed > 0, default);
        });
    }

    private PrivateTagCommit Mutate(ProfileAdmissionToken admission, long? expectedRevision, CancellationToken token,
        Func<Model, (bool Changed, TagId Tag)> change)
    {
        ArgumentNullException.ThrowIfNull(admission);
        return _lease.RunStoreWrite(admission, () =>
        {
            token.ThrowIfCancellationRequested();
            (Model model, byte[] bytes) = Read();
            if (expectedRevision is long expected && expected != model.Revision)
                throw new PrivateTagStoreException(PrivateTagStoreError.StaleRevision);
            (bool changed, TagId tag) = change(model);
            if (!changed) return new PrivateTagCommit(false, tag, model.Snapshot(bytes));
            if (model.Revision == long.MaxValue)
                throw new PrivateTagStoreException(PrivateTagStoreError.RevisionOverflow);
            // A newer or damaged backup must never be rotated away by this writer.
            try { ReadDocument(_path + ".bak"); }
            catch (FileNotFoundException) { }
            model.Revision++;
            byte[] next = Serialize(model);
            if (next.Length > MaximumBytes)
                throw new PrivateTagStoreException(PrivateTagStoreError.CapacityExceeded);
            AtomicJsonFile.WriteAllText(_path, StrictUtf8.GetString(next), backupCurrentFile: true, token);
            return new PrivateTagCommit(true, tag, model.Snapshot(next));
        });
    }

    private static PrivateTagName RequireName(string? name) =>
        PrivateTagName.TryCreate(name, out PrivateTagName? parsed, out _)
            ? parsed! : throw new PrivateTagStoreException(PrivateTagStoreError.InvalidName);

    private static void RequireKnown(Model model, TagId tag)
    {
        if (tag.Value == Guid.Empty || !model.Catalog.Contains(tag))
            throw new PrivateTagStoreException(PrivateTagStoreError.UnknownTag);
    }

    private static PrivateTagStoreError Map(PrivateTagCatalogError error) => error switch
    {
        PrivateTagCatalogError.InvalidTagId or PrivateTagCatalogError.UnknownTag => PrivateTagStoreError.UnknownTag,
        PrivateTagCatalogError.NameCollision or PrivateTagCatalogError.DefinitionConflict => PrivateTagStoreError.NameCollision,
        PrivateTagCatalogError.CapacityExceeded => PrivateTagStoreError.CapacityExceeded,
        PrivateTagCatalogError.RevisionOverflow => PrivateTagStoreError.RevisionOverflow,
        PrivateTagCatalogError.InvalidName => PrivateTagStoreError.InvalidName,
        _ => PrivateTagStoreError.StaleRevision
    };

    private static bool IsThread(ResourceId? thread)
    {
        if (thread is null) return false;
        try { ArchiveValidation.ValidateId(thread); }
        catch (ArgumentException) { return false; }
        return WellFormed(thread.Source) && WellFormed(thread.Board) && WellFormed(thread.Thread);
    }

    // Targets are created by their own syntax validator; persistence additionally needs
    // well-formed UTF-16 so the document can always be written and read back exactly.
    private static bool IsTarget(PrivateMediaTagTarget? target) => target is not null
        && IsThread(target.Reference.Resource) && WellFormed(target.Reference.PostId)
        && WellFormed(target.MediaId) && WellFormed(target.Variant);

    private static bool WellFormed(string? value)
    {
        if (value is null) return false;
        try { StrictUtf8.GetByteCount(value); return true; }
        catch (EncoderFallbackException) { return false; }
    }

    private (Model Model, byte[] Bytes) Read()
    {
        try { return ReadDocument(_path); }
        catch (FileNotFoundException)
        {
            // Only an explicitly missing primary AND backup establish a new, empty index.
            try { ReadDocument(_path + ".bak"); }
            catch (FileNotFoundException) { return (new Model(), Array.Empty<byte>()); }
            throw new InvalidDataException("The private tag index is missing but a backup exists. Explicit recovery is required.");
        }
    }

    private static (Model Model, byte[] Bytes) ReadDocument(string path)
    {
        try { return ReadDocumentCore(path); }
        // Paths and parser messages can expose private profile values; never attach raw exceptions.
        catch (FileNotFoundException) { throw new FileNotFoundException("The private tag index file is missing."); }
        catch (UnauthorizedAccessException) { throw new UnauthorizedAccessException("The private tag index could not be read."); }
        catch (InvalidDataException) { throw; }
        catch (IOException) { throw new IOException("The private tag index could not be read."); }
    }

    private static (Model Model, byte[] Bytes) ReadDocumentCore(string path)
    {
        using FileStream stream = RegularProfileFile.OpenRead(path);
        long length = stream.Length;
        if (length > MaximumBytes) throw new InvalidDataException("The private tag index exceeds its size limit.");
        byte[] bytes = new byte[checked((int)length)];
        try { stream.ReadExactly(bytes); }
        catch (EndOfStreamException) { throw new InvalidDataException("The private tag index changed while reading."); }
        if (stream.ReadByte() != -1 || stream.Length != length)
            throw new InvalidDataException("The private tag index changed while reading.");
        return (Parse(bytes), bytes);
    }

    private static Model Parse(byte[] bytes)
    {
        try
        {
            string json = StrictUtf8.GetString(bytes);
            if (json.Length > 0 && json[0] == '﻿') json = json[1..];
            using JsonDocument parsed = JsonDocument.Parse(json);
            JsonElement root = parsed.RootElement;
            CheckObject(root, ["Version", "Revision", "Tags", "Threads", "Media"]);
            if (root.GetProperty("Version").GetInt32() != CurrentVersion) throw new InvalidDataException(Invalid);
            Model model = new() { Revision = root.GetProperty("Revision").GetInt64() };
            if (model.Revision < 0) throw new InvalidDataException(Invalid);

            foreach (JsonElement item in Items(root.GetProperty("Tags"), MaximumTags))
            {
                CheckObject(item, ["Id", "Name"]);
                TagId id = ReadTagId(item.GetProperty("Id"));
                string text = Text(item.GetProperty("Name"));
                // Stored names are canonical: they must round-trip through the name rule unchanged.
                if (!PrivateTagName.TryCreate(text, out PrivateTagName? name, out _)
                    || !string.Equals(name!.Display, text, StringComparison.Ordinal)
                    || model.Catalog.Contains(id)
                    || !model.Catalog.TryDefine(id, name, model.Catalog.Revision, out PrivateTagCatalog next, out _))
                    throw new InvalidDataException(Invalid);
                model.Catalog = next;
            }

            foreach (JsonElement item in Items(root.GetProperty("Threads"), MaximumThreadAssignments))
            {
                CheckObject(item, ["Tag", "Source", "Board", "Thread"]);
                TagId tag = ReadTagId(item.GetProperty("Tag"));
                ResourceId thread = ReadThread(item);
                var assignment = new PrivateThreadTagAssignment(tag, thread);
                if (!model.Catalog.Contains(tag) || !IsThread(thread) || !model.ThreadSet.Add(assignment))
                    throw new InvalidDataException(Invalid);
                model.Threads.Add(assignment);
            }

            foreach (JsonElement item in Items(root.GetProperty("Media"), MaximumMediaAssignments))
            {
                CheckObject(item, ["Tag", "Source", "Board", "Thread", "Post", "Position", "RevisionKey",
                    "MediaId", "Variant", "Role", "Sha256"]);
                TagId tag = ReadTagId(item.GetProperty("Tag"));
                TransferMediaRole role = Text(item.GetProperty("Role")) switch
                {
                    "original" => TransferMediaRole.Original,
                    "preview" => TransferMediaRole.Preview,
                    _ => throw new InvalidDataException(Invalid)
                };
                var reference = new AttachmentRevisionReference(ReadThread(item), Text(item.GetProperty("Post")),
                    item.GetProperty("Position").GetInt32(), Text(item.GetProperty("RevisionKey")));
                if (!PrivateMediaTagTarget.TryCreate(reference, Text(item.GetProperty("MediaId")),
                    Text(item.GetProperty("Variant")), role, Text(item.GetProperty("Sha256")),
                    out PrivateMediaTagTarget? target, out _) || !IsTarget(target))
                    throw new InvalidDataException(Invalid);
                var assignment = new PrivateMediaTagAssignment(tag, target!);
                if (!model.Catalog.Contains(tag) || !model.MediaSet.Add(assignment))
                    throw new InvalidDataException(Invalid);
                model.Media.Add(assignment);
            }
            return model;
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or ArgumentException
            or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException)
        { throw new InvalidDataException(Invalid); }
    }

    private static IEnumerable<JsonElement> Items(JsonElement array, int maximum)
    {
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > maximum)
            throw new InvalidDataException(Invalid);
        return array.EnumerateArray();
    }

    private static TagId ReadTagId(JsonElement value)
    {
        string text = Text(value);
        // Exactly the lowercase hyphenated form this store writes; no alternative spellings.
        if (!Guid.TryParseExact(text, "D", out Guid id) || id == Guid.Empty
            || !string.Equals(id.ToString("D"), text, StringComparison.Ordinal))
            throw new InvalidDataException(Invalid);
        return new TagId(id);
    }

    private static ResourceId ReadThread(JsonElement item) => new(Text(item.GetProperty("Source")),
        Text(item.GetProperty("Board")), Text(item.GetProperty("Thread")));

    private static string Text(JsonElement value) =>
        value.ValueKind == JsonValueKind.String && value.GetString() is string text
            ? text : throw new InvalidDataException(Invalid);

    private static void CheckObject(JsonElement value, string[] required)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException(Invalid);
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
            if (!names.Add(property.Name) || !required.Contains(property.Name, StringComparer.Ordinal))
                throw new InvalidDataException(Invalid);
        if (names.Count != required.Length) throw new InvalidDataException(Invalid);
    }

    private static byte[] Serialize(Model model)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("Version", CurrentVersion);
            writer.WriteNumber("Revision", model.Revision);
            writer.WriteStartArray("Tags");
            foreach (PrivateTagDefinition tag in model.Catalog.Definitions)
            {
                writer.WriteStartObject();
                writer.WriteString("Id", tag.Id.Value.ToString("D"));
                writer.WriteString("Name", tag.Name.Display);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("Threads");
            foreach (PrivateThreadTagAssignment item in model.Threads)
            {
                writer.WriteStartObject();
                writer.WriteString("Tag", item.TagId.Value.ToString("D"));
                WriteThread(writer, item.Thread);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("Media");
            foreach (PrivateMediaTagAssignment item in model.Media)
            {
                PrivateMediaTagTarget target = item.Target;
                writer.WriteStartObject();
                writer.WriteString("Tag", item.TagId.Value.ToString("D"));
                WriteThread(writer, target.Reference.Resource);
                writer.WriteString("Post", target.Reference.PostId);
                writer.WriteNumber("Position", target.Reference.Position);
                writer.WriteString("RevisionKey", target.Reference.RevisionKey);
                writer.WriteString("MediaId", target.MediaId);
                writer.WriteString("Variant", target.Variant);
                writer.WriteString("Role", target.Role == TransferMediaRole.Original ? "original" : "preview");
                writer.WriteString("Sha256", target.Sha256);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteThread(Utf8JsonWriter writer, ResourceId thread)
    {
        writer.WriteString("Source", thread.Source);
        writer.WriteString("Board", thread.Board);
        writer.WriteString("Thread", thread.Thread);
    }
}
