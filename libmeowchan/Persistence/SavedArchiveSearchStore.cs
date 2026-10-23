using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Threading;
using MeowChan.Archive;

namespace MeowChan.Persistence;

internal enum SavedSearchStoreState { Ready, Unsupported, RecoveryAvailable, Failed }
internal sealed record SavedArchiveSearch(Guid Id, string Name, ArchiveSearchSpec? Definition, string RawDefinition, string? UnsupportedReason)
{
    public bool CanApply => Definition != null;
}
internal sealed record SavedArchiveSearchSnapshot(long Revision, IReadOnlyList<SavedArchiveSearch> Searches,
    SavedSearchStoreState State, string? Error = null)
{
    public bool CanWrite => State == SavedSearchStoreState.Ready;
}
internal sealed class SavedSearchRevisionException(SavedArchiveSearchSnapshot current)
    : IOException("Saved searches changed. Review the current list and explicitly retry.")
{
    public SavedArchiveSearchSnapshot Current { get; } = current;
}

/// <summary>Passive optional profile store. Only the existing session host owns its lease.</summary>
internal sealed class SavedArchiveSearchStore
{
    internal const int MaximumSearches = 500;
    internal const int MaximumFileBytes = 8 * 1024 * 1024;
    private readonly ProfileLease _lease;
    private readonly string _path;
    private SavedArchiveSearchSnapshot? _current;
    private string? _recoverySource;
    private string? _recoveryIdentity;
    public SavedArchiveSearchStore(string directory, ProfileLease lease)
    {
        lease.EnsureMatches(directory);
        _lease = lease;
        _path = Path.Combine(lease.ProfileDirectory, "archive-searches.json");
    }

    public SavedArchiveSearchSnapshot Load() => _lease.RunExclusive(() =>
    {
        _recoverySource = null; _recoveryIdentity = null;
        try { return _current = Read(_path, missingIsEmpty: true); }
        catch (Exception error) when (IsReadError(error))
        {
            try
            {
                SavedArchiveSearchSnapshot backup = Read(_path + ".bak", missingIsEmpty: false);
                if (backup.CanWrite)
                {
                    _recoverySource = ReadBounded(_path + ".bak");
                    _recoveryIdentity = FileIdentity(_path);
                    return _current = backup with { State = SavedSearchStoreState.RecoveryAvailable, Error = error.Message };
                }
            }
            catch (Exception backupError) when (IsReadError(backupError)) { }
            return _current = new(0, Array.Empty<SavedArchiveSearch>(), SavedSearchStoreState.Failed, error.Message);
        }
    });

    public SavedArchiveSearchSnapshot Recover(long expectedRevision, CancellationToken token = default) => _lease.RunExclusive(() =>
    {
        SavedArchiveSearchSnapshot current = _current ?? Load();
        if (current.Revision != expectedRevision) throw new SavedSearchRevisionException(current);
        if (current.State != SavedSearchStoreState.RecoveryAvailable || _recoverySource == null)
            throw new InvalidOperationException("No supported recovery snapshot is available.");
        // A changed primary or backup requires another review, never an implicit restore.
        string reviewed = _recoverySource;
        string? identity = _recoveryIdentity;
        SavedArchiveSearchSnapshot disk = Load();
        if (disk.State != SavedSearchStoreState.RecoveryAvailable || _recoverySource != reviewed || _recoveryIdentity != identity)
            throw new SavedSearchRevisionException(disk);
        long revision = checked(disk.Revision + 1);
        string json = SerializeForCommit(revision, disk.Searches);
        AtomicJsonFile.WriteAllText(_path, json, backupCurrentFile: false, token);
        _recoverySource = null;
        return _current = new(revision, disk.Searches, SavedSearchStoreState.Ready);
    });

    public SavedArchiveSearchSnapshot Create(Guid id, string name, ArchiveSearchSpec definition, long expectedRevision, CancellationToken token = default)
    {
        SavedArchiveSearch frozen = Entry(id, name, definition);
        return Mutate(expectedRevision, list => { if (list.Any(x => x.Id == id)) throw new ArgumentException("Duplicate saved search ID."); list.Add(frozen); }, token);
    }
    public SavedArchiveSearchSnapshot Rename(Guid id, string name, long expectedRevision, CancellationToken token = default)
    {
        string frozen = ValidateName(name);
        return Mutate(expectedRevision, list => { int index = Find(list, id); list[index] = list[index] with { Name = frozen }; }, token);
    }
    public SavedArchiveSearchSnapshot Replace(Guid id, ArchiveSearchSpec definition, long expectedRevision, CancellationToken token = default)
    {
        string json = ArchiveSearchSpecJson.Serialize(definition);
        ArchiveSearchSpec frozen = ArchiveSearchSpecJson.Deserialize(json);
        return Mutate(expectedRevision, list => { int index = Find(list, id); list[index] = list[index] with { Definition = frozen, RawDefinition = json }; }, token);
    }
    public SavedArchiveSearchSnapshot Delete(Guid id, long expectedRevision, CancellationToken token = default) =>
        Mutate(expectedRevision, list => list.RemoveAt(Find(list, id)), token);

    private SavedArchiveSearchSnapshot Mutate(long revision, Action<List<SavedArchiveSearch>> change, CancellationToken token) => _lease.RunExclusive(() =>
    {
        token.ThrowIfCancellationRequested();
        SavedArchiveSearchSnapshot current = _current ?? Load();
        SavedArchiveSearchSnapshot disk = Load();
        // Compare content too: an external writer must not reuse an existing revision.
        if (current.Revision != revision || disk.Revision != revision ||
            !disk.CanWrite || !current.CanWrite || Serialize(current.Revision, current.Searches) != Serialize(disk.Revision, disk.Searches))
            throw new SavedSearchRevisionException(disk);
        List<SavedArchiveSearch> candidate = disk.Searches.ToList();
        change(candidate);
        if (candidate.Count > MaximumSearches) throw new InvalidDataException("At most 500 saved searches are supported.");
        long next = checked(revision + 1);
        string json = SerializeForCommit(next, candidate);
        AtomicJsonFile.WriteAllText(_path, json, backupCurrentFile: true, token);
        return _current = new(next, Array.AsReadOnly(candidate.ToArray()), SavedSearchStoreState.Ready);
    });

    internal static string ValidateName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        name = name.Trim();
        int count = 0;
        foreach (var rune in name.EnumerateRunes()) { if (System.Text.Rune.IsControl(rune)) throw new ArgumentException("The name contains a control character."); count++; }
        if (count is < 1 or > 120) throw new ArgumentException("The name must contain 1–120 characters.");
        return name;
    }
    private static SavedArchiveSearch Entry(Guid id, string name, ArchiveSearchSpec definition)
    {
        if (id == Guid.Empty) throw new ArgumentException("A saved search needs a nonempty ID.");
        string json = ArchiveSearchSpecJson.Serialize(definition);
        return new(id, ValidateName(name), ArchiveSearchSpecJson.Deserialize(json), json, null);
    }
    private static int Find(List<SavedArchiveSearch> list, Guid id)
    {
        int index = list.FindIndex(x => x.Id == id);
        if (index < 0) throw new InvalidOperationException("The selected saved search no longer exists.");
        return index;
    }
    // InvalidDataException derives from SystemException, not IOException.
    private static bool IsReadError(Exception error) => error is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or OverflowException or FormatException or KeyNotFoundException or DecoderFallbackException or NotSupportedException;
    private static string FileIdentity(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static string ReadBounded(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumFileBytes) throw new InvalidDataException("Saved searches exceed 8 MiB.");
        using MemoryStream contents = new();
        byte[] chunk = new byte[8192];
        int read;
        while ((read = stream.Read(chunk)) != 0)
        {
            if (contents.Length + read > MaximumFileBytes) throw new InvalidDataException("Saved searches exceed 8 MiB.");
            contents.Write(chunk, 0, read);
        }
        string text = new UTF8Encoding(false, true).GetString(contents.GetBuffer(), 0, checked((int)contents.Length));
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }
    private static SavedArchiveSearchSnapshot Read(string path, bool missingIsEmpty)
    {
        string json;
        try { json = ReadBounded(path); }
        catch (FileNotFoundException) when (missingIsEmpty) { return new(0, Array.Empty<SavedArchiveSearch>(), SavedSearchStoreState.Ready); }
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid saved searches envelope.");
        CheckDuplicates(root);
        int schema = root.GetProperty("schemaVersion").GetInt32();
        if (schema != 1) return new(0, Array.Empty<SavedArchiveSearch>(), SavedSearchStoreState.Unsupported, "Unsupported saved searches schema: " + schema);
        long revision = root.GetProperty("revision").GetInt64();
        if (revision < 0) throw new InvalidDataException("Invalid saved searches revision.");
        bool unknownEnvelopeFields = root.EnumerateObject().Any(x => x.Name is not ("schemaVersion" or "revision" or "searches"));
        bool unsupported = unknownEnvelopeFields;
        JsonElement array = root.GetProperty("searches");
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > MaximumSearches) throw new InvalidDataException("Invalid saved searches list.");
        List<SavedArchiveSearch> entries = new();
        HashSet<Guid> ids = new();
        foreach (JsonElement item in array.EnumerateArray())
        {
            CheckDuplicates(item);
            Guid id = item.GetProperty("id").GetGuid();
            if (id == Guid.Empty || !ids.Add(id)) throw new InvalidDataException("Duplicate or empty saved search ID.");
            string name = ValidateName(item.GetProperty("name").GetString()!);
            string raw = item.GetProperty("definition").GetRawText();
            ArchiveSearchSpec? definition = null;
            string? reason = null;
            try { definition = ArchiveSearchSpecJson.Deserialize(raw); }
            catch (Exception error) when (IsReadError(error))
            {
                if (!IsUnsupportedDefinition(item.GetProperty("definition"), error))
                    throw new InvalidDataException("Invalid saved search definition.", error);
                reason = error.Message; unsupported = true;
            }
            if (item.EnumerateObject().Any(x => x.Name is not ("id" or "name" or "definition"))) { unsupported = true; definition = null; reason ??= "Unsupported saved search fields."; }
            if (unknownEnvelopeFields) { definition = null; reason = "Unsupported saved searches envelope fields."; }
            entries.Add(new(id, name, definition, raw, reason));
        }
        return new(revision, Array.AsReadOnly(entries.ToArray()), unsupported ? SavedSearchStoreState.Unsupported : SavedSearchStoreState.Ready,
            unsupported ? "Unsupported saved search data. The file is read-only and has been preserved." : null);
    }
    private static bool IsUnsupportedDefinition(JsonElement definition, Exception error)
    {
        static bool UnknownFields(JsonElement element, params string[] known) => element.ValueKind == JsonValueKind.Object &&
            element.EnumerateObject().Any(property => !known.Contains(property.Name, StringComparer.Ordinal));
        static bool UnknownKey(JsonElement element, string field, params string[] known) => element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(field, out JsonElement value) && value.ValueKind == JsonValueKind.String && !known.Contains(value.GetString(), StringComparer.Ordinal);
        if (UnknownFields(definition, "schemaVersion", "text", "sources", "boards", "sourceStates", "captureEnabled", "hasPosts", "sort", "workFilter", "dateRange")) return true;
        if (definition.ValueKind != JsonValueKind.Object) return false;
        if (definition.TryGetProperty("schemaVersion", out var schema) && schema.ValueKind == JsonValueKind.Number && schema.TryGetInt32(out int version) && version != 1) return true;
        if (UnknownKey(definition, "sort", "latest-capture", "oldest-first", "title") ||
            UnknownKey(definition, "workFilter", "all", "watching", "followup", "intervention", "completed", "stopped")) return true;
        if (definition.TryGetProperty("sourceStates", out var states) && states.ValueKind == JsonValueKind.Array &&
            states.EnumerateArray().Any(state => state.ValueKind == JsonValueKind.String && state.GetString() is not ("active" or "archived" or "not-found"))) return true;
        if (definition.TryGetProperty("boards", out var boards) && boards.ValueKind == JsonValueKind.Array &&
            boards.EnumerateArray().Any(board => UnknownFields(board, "source", "board"))) return true;
        if (definition.TryGetProperty("dateRange", out var date) &&
            (UnknownFields(date, "basis", "startDate", "endDate", "timeZoneId") || UnknownKey(date, "basis", "first-seen"))) return true;
        return error is ArgumentException { ParamName: "id" };
    }
    private static void CheckDuplicates(JsonElement value)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
            if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate JSON property: " + property.Name);
    }
    private static string SerializeForCommit(long revision, IReadOnlyList<SavedArchiveSearch> searches)
    {
        string json = Serialize(revision, searches);
        if (Encoding.UTF8.GetByteCount(json) > MaximumFileBytes)
            throw new InvalidDataException("Saved searches exceed 8 MiB.");
        return json;
    }
    private static string Serialize(long revision, IReadOnlyList<SavedArchiveSearch> searches)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject(); writer.WriteNumber("schemaVersion", 1); writer.WriteNumber("revision", revision); writer.WriteStartArray("searches");
            foreach (SavedArchiveSearch entry in searches)
            {
                writer.WriteStartObject(); writer.WriteString("id", entry.Id); writer.WriteString("name", entry.Name); writer.WritePropertyName("definition");
                writer.WriteRawValue(entry.RawDefinition); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
