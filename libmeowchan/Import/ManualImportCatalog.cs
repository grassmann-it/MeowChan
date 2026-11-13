using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using MeowChan.Gallery;
using MeowChan.Persistence;
using MeowChan.Tags;

namespace MeowChan.Import;

// Contract: docs/architecture/gesamtprogramm-2026-10-31/contracts/manual-import-v1.md (§3 catalog, §6 failure and atomicity).

/// <summary>Where one import batch stands. <see cref="Copying"/> after a stop means "interrupted": finished on the next run.</summary>
internal enum ManualImportBatchState { Copying, Completed }

/// <summary>
/// One file of a batch. <see cref="Planned"/> exists only while the batch is copying. <see cref="Committed"/> is a copied,
/// manifest-published file. <see cref="Duplicate"/> is a selected file whose bytes the library already had: nothing was
/// copied, the entry only links the batch to those bytes. <see cref="Failed"/> was planned but could not be copied.
/// Unsupported, unreadable and too large files are reported by the dialog and never stored.
/// </summary>
internal enum ManualImportItemState { Planned, Committed, Duplicate, Failed }

/// <summary>Stable failure codes of a failed item. Never a path, a file name or an exception text.</summary>
internal static class ManualImportFailure
{
    /// <summary>The source changed (bytes, length or kind) between checking and copying.</summary>
    internal const string Changed = "changed";
    /// <summary>The source could not be opened or read while copying.</summary>
    internal const string Unreadable = "unreadable";
    /// <summary>The disk of the download folder ran out of space.</summary>
    internal const string NoSpace = "no-space";
    /// <summary>The managed folder or its manifest could not be written.</summary>
    internal const string Store = "store";
    /// <summary>The program stopped while the file was being copied; it was not published.</summary>
    internal const string Interrupted = "interrupted";

    internal static bool IsKnown(string? code) => code is Changed or Unreadable or NoSpace or Store or Interrupted;
}

internal sealed record ManualImportItem(Guid Id, ManualImportItemState State, string Sha256, long Length, string Name,
    SavedMediaKind Kind, string Extension, string? AssetId = null, string? Failure = null)
{
    /// <summary>The manifest's requested file name of this item inside its batch folder.</summary>
    internal string RequestedFileName => ManualImportIdentity.RequestedFileName(Id, Extension);
    internal string MediaIdentifier => ManualImportIdentity.MediaIdentifier(Id);
}

/// <summary>
/// One import operation. The folder is stored as the absolute path it was written to (like archived threads and Danbooru
/// originals); no source path is ever stored. <see cref="SourceLabel"/> is at most the name of one selected folder.
/// <see cref="Tags"/> are the names the user chose for the new files of this batch; <see cref="TagsApplied"/> turns true
/// once they were written to the private tag store.
/// </summary>
internal sealed record ManualImportBatch(Guid Id, string Name, DateTimeOffset CreatedAtUtc, DateTimeOffset? CompletedAtUtc,
    ManualImportBatchState State, string Directory, string? SourceLabel, bool OtherFiles, bool Cancelled,
    IReadOnlyList<string> Tags, bool TagsApplied, IReadOnlyList<ManualImportItem> Items)
{
    internal ResourceId Thread => ManualImportIdentity.Thread(Id);
    internal int Count(ManualImportItemState state) => Items.Count(item => item.State == state);
    internal ManualImportItem? Find(Guid item) => Items.FirstOrDefault(candidate => candidate.Id == item);
}

/// <summary>An immutable, validated catalog view.</summary>
internal sealed class ManualImportCatalogSnapshot
{
    internal ManualImportCatalogSnapshot(long revision, IEnumerable<ManualImportBatch> batches)
    {
        Revision = revision;
        Batches = Array.AsReadOnly((batches ?? throw new ArgumentNullException(nameof(batches))).ToArray());
    }

    internal static ManualImportCatalogSnapshot Empty { get; } = new(0, Array.Empty<ManualImportBatch>());

    internal long Revision { get; }
    internal IReadOnlyList<ManualImportBatch> Batches { get; }
    internal int ItemCount => Batches.Sum(batch => batch.Items.Count);
    internal ManualImportBatch? Find(Guid batch) => Batches.FirstOrDefault(candidate => candidate.Id == batch);
}

/// <summary>Why an import step was refused. Messages for the user are chosen from these codes; none carries a path.</summary>
internal enum ManualImportError
{
    /// <summary>The import catalog is damaged; imports are blocked until it is repaired or its backup restored.</summary>
    CatalogDamaged,
    /// <summary>The catalog (or its backup) was written by a newer MeowChan; it is never overwritten.</summary>
    CatalogNewer,
    /// <summary>The catalog changed since it was read (another write in between).</summary>
    Conflict,
    /// <summary>The import would exceed the configured total number of imported files.</summary>
    TooManyImportedFiles,
    /// <summary>The import would exceed the configured number of import batches.</summary>
    TooManyBatches,
    /// <summary>The catalog file would exceed its configured size limit.</summary>
    CatalogTooLarge,
    /// <summary>The selection has more files than one batch may hold.</summary>
    TooManyFiles,
    /// <summary>The selection is larger than one batch may be.</summary>
    BatchTooLarge,
    /// <summary>The download folder's disk does not have enough free space for the new files plus the reserve.</summary>
    NotEnoughSpace,
    /// <summary>The request itself is invalid (no new file, bad batch name, bad tag name).</summary>
    InvalidRequest
}

internal sealed class ManualImportException : Exception
{
    internal ManualImportException(ManualImportError error, Exception? inner = null) : base(Describe(error), inner) => Error = error;
    internal ManualImportError Error { get; }

    private static string Describe(ManualImportError error) => error switch
    {
        ManualImportError.CatalogDamaged => "The import catalog is damaged.",
        ManualImportError.CatalogNewer => "The import catalog was written by a newer version.",
        ManualImportError.Conflict => "The import catalog changed meanwhile.",
        ManualImportError.TooManyImportedFiles => "The configured number of imported files would be exceeded.",
        ManualImportError.TooManyBatches => "The configured number of import batches would be exceeded.",
        ManualImportError.CatalogTooLarge => "The import catalog would exceed its configured size.",
        ManualImportError.TooManyFiles => "The selection has more files than one batch may hold.",
        ManualImportError.BatchTooLarge => "The selection is larger than one batch may be.",
        ManualImportError.NotEnoughSpace => "There is not enough free space for this import.",
        _ => "The import request is invalid."
    };
}

/// <summary>
/// Profile-owned catalog of manual imports (<c>local-imports.json</c>). Separate from the archive, the Booru catalog and
/// the tag store, so older versions open the profile unchanged (they do not know the file). Every mutation is guarded by
/// the exact profile lease and its store-writer admission, reads the file fresh, checks the expected revision and
/// replaces the file atomically with the previous version as <c>.bak</c>. Strict: unknown or duplicate fields, uppercase
/// hex, empty ids or inconsistent states make the whole file invalid. A damaged or newer primary file, or a newer
/// backup, is never overwritten; the store then refuses every write.
/// </summary>
internal sealed class ManualImportCatalogStore
{
    internal const string FileName = "local-imports.json";
    internal const int CurrentVersion = 1;
    /// <summary>Technical ceiling for reading and for the configurable write limit.</summary>
    internal const int MaximumDocumentBytes = 128 * 1024 * 1024;
    internal const int DefaultDocumentLimitBytes = 32 * 1024 * 1024;
    internal const int MinimumDocumentLimitBytes = 1 * 1024 * 1024;
    /// <summary>Technical ceilings; the user settings (defaults below) must stay inside them.</summary>
    internal const int MaximumItemsPerBatch = 20_000;
    internal const int MaximumTotalItemsCeiling = 500_000;
    internal const int DefaultMaximumTotalItems = 50_000;
    /// <summary>Technical ceiling of the batches a catalog may hold (reads and the configurable write limit).</summary>
    internal const int MaximumBatchesCeiling = 1_000_000;
    internal const int DefaultMaximumBatches = 100_000;
    internal const int MinimumMaximumBatches = 1_000;
    internal const int MaximumBatchNameLength = 200;
    internal const int MaximumNameLength = 255;
    internal const int MaximumDirectoryLength = 4096;
    /// <summary>Technical ceiling of the own tags one batch may carry (reads and the configurable write limit).</summary>
    internal const int MaximumTagsCeiling = 256;
    internal const int DefaultMaximumTags = 32;

    private readonly object _sync = new();
    private readonly ProfileLease _lease;
    private readonly string _path;
    private long _documentLimitBytes = DefaultDocumentLimitBytes;
    private int _maximumTotalItems = DefaultMaximumTotalItems;
    private int _maximumBatches = DefaultMaximumBatches;
    private int _maximumTags = DefaultMaximumTags;

    internal ManualImportCatalogStore(string profileDirectory, ProfileLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lease.EnsureMatches(ProfileLease.NormalizeDirectory(profileDirectory));
        _lease = lease;
        _path = Path.Combine(lease.ProfileDirectory, FileName);
    }

    internal string FilePath => _path;

    /// <summary>Configured write limit of the catalog file in bytes (setting). Lowering it never makes a catalog unreadable.</summary>
    internal long DocumentLimitBytes
    {
        get => Interlocked.Read(ref _documentLimitBytes);
        set
        {
            if (value is < MinimumDocumentLimitBytes or > MaximumDocumentBytes) throw new ArgumentOutOfRangeException(nameof(value));
            Interlocked.Exchange(ref _documentLimitBytes, value);
        }
    }

    /// <summary>Configured maximum of imported files over all batches (setting). Only stops growth; nothing is removed.</summary>
    internal int MaximumTotalItems
    {
        get => Volatile.Read(ref _maximumTotalItems);
        set
        {
            if (value is < 1 or > MaximumTotalItemsCeiling) throw new ArgumentOutOfRangeException(nameof(value));
            Volatile.Write(ref _maximumTotalItems, value);
        }
    }

    /// <summary>Configured maximum of import batches (setting). Only stops a new batch; nothing is removed.</summary>
    internal int MaximumBatchCount
    {
        get => Volatile.Read(ref _maximumBatches);
        set
        {
            if (value is < MinimumMaximumBatches or > MaximumBatchesCeiling) throw new ArgumentOutOfRangeException(nameof(value));
            Volatile.Write(ref _maximumBatches, value);
        }
    }

    /// <summary>Configured maximum of own tags per new batch (setting). Batches already saved keep all their tags.</summary>
    internal int MaximumTagsPerBatch
    {
        get => Volatile.Read(ref _maximumTags);
        set
        {
            if (value is < 1 or > MaximumTagsCeiling) throw new ArgumentOutOfRangeException(nameof(value));
            Volatile.Write(ref _maximumTags, value);
        }
    }

    /// <summary>Reads and validates the catalog. Missing file: empty catalog. Damaged or newer: <see cref="ManualImportException"/>.</summary>
    internal ManualImportCatalogSnapshot Read(CancellationToken token = default) => _lease.RunExclusive(() =>
    {
        lock (_sync) return ReadLocked(token);
    });

    /// <summary>
    /// Inserts or replaces one batch (by id) in one atomic write bound to <paramref name="expectedRevision"/>. Growth is
    /// bounded by the configured total and file size; a write that only changes item states never counts as growth.
    /// </summary>
    internal ManualImportCatalogSnapshot PutBatchAdmitted(ManualImportBatch batch, long expectedRevision,
        ProfileAdmissionToken admission, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(admission);
        return _lease.RunStoreWrite(admission, () =>
        {
            lock (_sync)
            {
                token.ThrowIfCancellationRequested();
                ManualImportCatalogSnapshot current = ReadLocked(token);
                if (current.Revision != expectedRevision) throw new ManualImportException(ManualImportError.Conflict);
                List<ManualImportBatch> batches = current.Batches.ToList();
                int index = batches.FindIndex(candidate => candidate.Id == batch.Id);
                int before = index < 0 ? 0 : batches[index].Items.Count;
                if (index < 0) batches.Add(batch); else batches[index] = batch;
                ManualImportCatalogSnapshot next = new(checked(current.Revision + 1), batches);
                Validate(next);
                if (batch.Items.Count > before && next.ItemCount > MaximumTotalItems)
                    throw new ManualImportException(ManualImportError.TooManyImportedFiles);
                if (index < 0 && next.Batches.Count > MaximumBatchCount)
                    throw new ManualImportException(ManualImportError.TooManyBatches);
                Publish(next, token);
                return next;
            }
        });
    }

    private ManualImportCatalogSnapshot ReadLocked(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ManualImportCatalogSnapshot? read = ReadFile(_path, out bool newer);
        if (newer) throw new ManualImportException(ManualImportError.CatalogNewer);
        return read ?? ManualImportCatalogSnapshot.Empty;
    }

    private void Publish(ManualImportCatalogSnapshot snapshot, CancellationToken token)
    {
        byte[] bytes = Serialize(snapshot);
        if (bytes.Length > DocumentLimitBytes) throw new ManualImportException(ManualImportError.CatalogTooLarge);
        // The previous version becomes the backup; a backup written by a newer program is never replaced.
        if (File.Exists(_path + ".bak"))
        {
            try { ReadFile(_path + ".bak", out bool newerBackup); if (newerBackup) throw new ManualImportException(ManualImportError.CatalogNewer); }
            catch (ManualImportException error) when (error.Error == ManualImportError.CatalogDamaged) { /* A damaged backup may be replaced. */ }
        }
        string json = Encoding.UTF8.GetString(bytes);
        try { AtomicJsonFile.WriteAllText(_path, json, backupCurrentFile: true, token); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Distinguish an already published write from a failure before the replacement; never apply twice.
            try
            {
                if (ReadFile(_path, out _) is { } committed && committed.Revision == snapshot.Revision
                    && Serialize(committed).AsSpan().SequenceEqual(bytes)) return;
            }
            catch (Exception readError) when (readError is IOException or UnauthorizedAccessException or ManualImportException) { }
            throw;
        }
    }

    // ---- Wire format (version 1) ----

    private static readonly string[] RootFields = { "Version", "Revision", "Batches" };
    private static readonly string[] BatchFields = { "Id", "Name", "CreatedAtUtc", "CompletedAtUtc", "State", "Directory", "SourceLabel",
        "OtherFiles", "Cancelled", "Tags", "TagsApplied", "Items" };
    private static readonly string[] ItemFields = { "Id", "State", "Sha256", "Length", "Name", "Kind", "Extension", "AssetId", "Failure" };

    internal static byte[] Serialize(ManualImportCatalogSnapshot snapshot)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("Version", CurrentVersion);
            writer.WriteNumber("Revision", snapshot.Revision);
            writer.WriteStartArray("Batches");
            foreach (ManualImportBatch batch in snapshot.Batches)
            {
                writer.WriteStartObject();
                writer.WriteString("Id", ManualImportIdentity.Format(batch.Id));
                writer.WriteString("Name", batch.Name);
                writer.WriteString("CreatedAtUtc", Time(batch.CreatedAtUtc));
                if (batch.CompletedAtUtc is { } completed) writer.WriteString("CompletedAtUtc", Time(completed));
                else writer.WriteNull("CompletedAtUtc");
                writer.WriteString("State", batch.State.ToString());
                writer.WriteString("Directory", batch.Directory);
                if (batch.SourceLabel != null) writer.WriteString("SourceLabel", batch.SourceLabel); else writer.WriteNull("SourceLabel");
                writer.WriteBoolean("OtherFiles", batch.OtherFiles);
                writer.WriteBoolean("Cancelled", batch.Cancelled);
                writer.WriteStartArray("Tags");
                foreach (string tag in batch.Tags) writer.WriteStringValue(tag);
                writer.WriteEndArray();
                writer.WriteBoolean("TagsApplied", batch.TagsApplied);
                writer.WriteStartArray("Items");
                foreach (ManualImportItem item in batch.Items)
                {
                    writer.WriteStartObject();
                    writer.WriteString("Id", ManualImportIdentity.Format(item.Id));
                    writer.WriteString("State", item.State.ToString());
                    writer.WriteString("Sha256", item.Sha256);
                    writer.WriteNumber("Length", item.Length);
                    writer.WriteString("Name", item.Name);
                    writer.WriteString("Kind", item.Kind.ToString());
                    writer.WriteString("Extension", item.Extension);
                    if (item.AssetId != null) writer.WriteString("AssetId", item.AssetId); else writer.WriteNull("AssetId");
                    if (item.Failure != null) writer.WriteString("Failure", item.Failure); else writer.WriteNull("Failure");
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static string Time(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    /// <summary>Null when the file is missing. <paramref name="newer"/>: a valid newer version that must not be touched.</summary>
    internal static ManualImportCatalogSnapshot? ReadFile(string path, out bool newer)
    {
        newer = false;
        byte[] bytes;
        try
        {
            using FileStream stream = RegularProfileFile.OpenRead(path);
            if (stream.Length > MaximumDocumentBytes) throw new ManualImportException(ManualImportError.CatalogDamaged);
            bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Version", out JsonElement version)
                || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int schema))
                throw Damaged();
            if (schema > CurrentVersion) { newer = true; return null; }
            if (schema != CurrentVersion) throw Damaged();
            Dictionary<string, JsonElement> fields = Fields(root, RootFields);
            long revision = Long(fields["Revision"]);
            if (revision < 0) throw Damaged();
            JsonElement batches = fields["Batches"];
            if (batches.ValueKind != JsonValueKind.Array || batches.GetArrayLength() > MaximumBatchesCeiling) throw Damaged();
            ManualImportCatalogSnapshot snapshot = new(revision, batches.EnumerateArray().Select(ParseBatch).ToArray());
            Validate(snapshot);
            return snapshot;
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or OverflowException
            or ArgumentException or DecoderFallbackException)
        { throw new ManualImportException(ManualImportError.CatalogDamaged, error); }
    }

    private static ManualImportBatch ParseBatch(JsonElement element)
    {
        Dictionary<string, JsonElement> fields = Fields(element, BatchFields);
        JsonElement tags = fields["Tags"], items = fields["Items"];
        if (tags.ValueKind != JsonValueKind.Array || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > MaximumItemsPerBatch)
            throw Damaged();
        return new ManualImportBatch(Id(fields["Id"]), Text(fields["Name"]), Instant(fields["CreatedAtUtc"]),
            fields["CompletedAtUtc"].ValueKind == JsonValueKind.Null ? null : Instant(fields["CompletedAtUtc"]),
            State<ManualImportBatchState>(fields["State"]), Text(fields["Directory"]), OptionalText(fields["SourceLabel"]),
            Bool(fields["OtherFiles"]), Bool(fields["Cancelled"]), tags.EnumerateArray().Select(Text).ToArray(),
            Bool(fields["TagsApplied"]), items.EnumerateArray().Select(ParseItem).ToArray());
    }

    private static ManualImportItem ParseItem(JsonElement element)
    {
        Dictionary<string, JsonElement> fields = Fields(element, ItemFields);
        return new ManualImportItem(Id(fields["Id"]), State<ManualImportItemState>(fields["State"]), Text(fields["Sha256"]),
            Long(fields["Length"]), Text(fields["Name"]), State<SavedMediaKind>(fields["Kind"]), Text(fields["Extension"]),
            OptionalText(fields["AssetId"]), OptionalText(fields["Failure"]));
    }

    // Exactly the known fields, each once: unknown, missing or repeated fields make the file invalid.
    private static Dictionary<string, JsonElement> Fields(JsonElement element, string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Damaged();
        Dictionary<string, JsonElement> fields = new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
            if (Array.IndexOf(names, property.Name) < 0 || !fields.TryAdd(property.Name, property.Value)) throw Damaged();
        if (fields.Count != names.Length) throw Damaged();
        return fields;
    }

    private static Guid Id(JsonElement element) =>
        element.ValueKind == JsonValueKind.String && ManualImportIdentity.TryParseId(element.GetString(), out Guid id) ? id : throw Damaged();

    private static string Text(JsonElement element) => element.ValueKind == JsonValueKind.String ? element.GetString()! : throw Damaged();

    private static string? OptionalText(JsonElement element) => element.ValueKind == JsonValueKind.Null ? null : Text(element);

    private static bool Bool(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw Damaged()
    };

    private static long Long(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out long value) ? value : throw Damaged();

    private static DateTimeOffset Instant(JsonElement element)
    {
        string text = Text(element);
        if (!DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset value)
            || value.Offset != TimeSpan.Zero) throw Damaged();
        return value;
    }

    private static T State<T>(JsonElement element) where T : struct, Enum
    {
        string text = Text(element);
        // Exact names only: no numbers, no other casing.
        return Enum.GetNames<T>().Contains(text, StringComparer.Ordinal) ? Enum.Parse<T>(text) : throw Damaged();
    }

    private static ManualImportException Damaged() => new(ManualImportError.CatalogDamaged);

    /// <summary>Semantic rules every stored and every written catalog satisfies.</summary>
    internal static void Validate(ManualImportCatalogSnapshot snapshot)
    {
        if (snapshot.Revision < 0 || snapshot.Batches.Count > MaximumBatchesCeiling || snapshot.ItemCount > MaximumTotalItemsCeiling) throw Damaged();
        HashSet<Guid> batchIds = new(), itemIds = new();
        foreach (ManualImportBatch batch in snapshot.Batches)
        {
            if (batch.Id == Guid.Empty || !batchIds.Add(batch.Id) || !ValidName(batch.Name, MaximumBatchNameLength)
                || batch.CreatedAtUtc.Offset != TimeSpan.Zero
                || !Enum.IsDefined(batch.State)
                || (batch.State == ManualImportBatchState.Completed) != (batch.CompletedAtUtc != null)
                || batch.CompletedAtUtc is { } completed && (completed.Offset != TimeSpan.Zero || completed < batch.CreatedAtUtc)
                || !ValidDirectory(batch.Directory)
                || batch.SourceLabel != null && (!ValidName(batch.SourceLabel, MaximumNameLength) || batch.SourceLabel.IndexOfAny(new[] { '/', '\\' }) >= 0)
                || batch.Tags == null || batch.Tags.Count > MaximumTagsCeiling || batch.Items == null || batch.Items.Count > MaximumItemsPerBatch)
                throw Damaged();
            HashSet<string> tagKeys = new(StringComparer.Ordinal);
            foreach (string tag in batch.Tags)
                if (!PrivateTagName.TryCreate(tag, out PrivateTagName? name, out _) || name == null || name.Display != tag || !tagKeys.Add(name.Key))
                    throw Damaged();
            foreach (ManualImportItem item in batch.Items)
            {
                if (item == null || item.Id == Guid.Empty || !itemIds.Add(item.Id) || !Enum.IsDefined(item.State)
                    || item.State == ManualImportItemState.Planned && batch.State != ManualImportBatchState.Copying
                    || !CanonicalHex(item.Sha256) || item.Length < 0 || !ValidName(item.Name, MaximumNameLength)
                    || item.Name.IndexOfAny(new[] { '/', '\\' }) >= 0 || item.Name is "." or ".."
                    || item.Kind is not (SavedMediaKind.Image or SavedMediaKind.Video or SavedMediaKind.Other)
                    || !ValidExtension(item.Extension) || SavedMediaTypes.Classify("x" + item.Extension) != item.Kind
                    || (item.State == ManualImportItemState.Committed) != (item.AssetId != null)
                    || item.AssetId != null && !CanonicalHex(item.AssetId)
                    || (item.State == ManualImportItemState.Failed) != (item.Failure != null)
                    || item.Failure != null && !ManualImportFailure.IsKnown(item.Failure))
                    throw Damaged();
            }
        }
    }

    internal static bool CanonicalHex(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static bool ValidExtension(string? value) => value is { Length: >= 2 and <= 13 } && value[0] == '.'
        && value.Skip(1).All(c => c is >= '0' and <= '9' or >= 'a' and <= 'z');

    internal static bool ValidName(string? value, int maximum) =>
        LocalMediaObservation.ValidText(value, maximum) && value!.Trim().Length == value.Length;

    private static bool ValidDirectory(string? value) =>
        LocalMediaObservation.ValidText(value, MaximumDirectoryLength) && Path.IsPathFullyQualified(value!);
}
