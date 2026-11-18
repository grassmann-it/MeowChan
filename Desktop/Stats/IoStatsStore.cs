using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using MeowChan.Persistence;

namespace MeowChan.Desktop.Stats;

/// <summary>How a load ended: <see cref="Ok"/> includes a missing file; the other states always start from the data given back. <see cref="Unreadable"/> (locked, no permission) leaves the file untouched.</summary>
internal enum IoStatsLoadStatus { Ok, RestoredFromBackup, Damaged, Unreadable }

internal sealed record IoStatsLoadResult(IoStatsLoadStatus Status, IoDays Days, string? Notice, string? SetAsidePath);

/// <summary>
/// The per-day network and disk totals of the profile (<c>io-stats.json</c>): per local day and source the six raw counters.
/// Writes are atomic (temporary file, replace, <c>.bak</c>) under the profile lease. Reading is bounded, rejects duplicate
/// properties and never throws for bad content: a damaged file is set aside as <c>.bad</c>, the backup is tried, and
/// otherwise the statistics start empty. Statistics are derived data, so a failed write is reported and retried, never fatal.
/// </summary>
internal sealed class IoStatsStore
{
    internal const string FileName = "io-stats.json";
    /// <summary>Fixed technical ceiling when reading; far above 365 days of 256 sources of realistic size.</summary>
    internal const long MaximumFileBytes = 16L * 1024 * 1024;
    // Additive v1: "up" is optional like the other counters. Missing/unknown historical upload is zero.
    // Write it only when nonzero, preserving the down-only v1 serialization shape and exact fixtures.
    internal const int FormatVersion = 1;
    private const int MaximumDays = 400;
    private const int MaximumSourcesPerDay = 4096;

    private readonly ProfileLease _lease;
    private readonly string _path;

    internal IoStatsStore(string profileDirectory, ProfileLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lease.EnsureMatches(profileDirectory);
        _lease = lease;
        _path = Path.Combine(lease.ProfileDirectory, FileName);
    }

    internal string FilePath => _path;

    /// <summary>Loads the days that are still inside the retention window of <paramref name="today"/>.</summary>
    internal IoStatsLoadResult Load(DateOnly today, int retentionDays, int maxSources)
    {
        try { return _lease.RunExclusive(() => LoadCore(today, retentionDays, maxSources)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // Unreadable is not damaged: leave the file alone and start empty for this session.
            return new IoStatsLoadResult(IoStatsLoadStatus.Unreadable, new IoDays(), "The saved statistics could not be read: " + ex.Message, null);
        }
    }

    private IoStatsLoadResult LoadCore(DateOnly today, int retentionDays, int maxSources)
    {
        if (!File.Exists(_path) && !File.Exists(_path + ".bak"))
            return new IoStatsLoadResult(IoStatsLoadStatus.Ok, new IoDays(), null, null);
        if (File.Exists(_path))
        {
            if (TryReadFile(_path, today, retentionDays, maxSources, out IoDays? days, out string? error))
                return new IoStatsLoadResult(IoStatsLoadStatus.Ok, days!, null, null);
            string? setAside = SetAside(_path);
            if (TryReadFile(_path + ".bak", today, retentionDays, maxSources, out days, out _))
                return new IoStatsLoadResult(IoStatsLoadStatus.RestoredFromBackup, days!,
                    "The saved statistics were damaged (" + error + "); the previous copy was used.", setAside);
            return new IoStatsLoadResult(IoStatsLoadStatus.Damaged, new IoDays(),
                "The saved statistics were damaged (" + error + ") and were set aside; the statistics start empty.", setAside);
        }
        if (TryReadFile(_path + ".bak", today, retentionDays, maxSources, out IoDays? backup, out _))
            return new IoStatsLoadResult(IoStatsLoadStatus.RestoredFromBackup, backup!, "The saved statistics were missing; the previous copy was used.", null);
        return new IoStatsLoadResult(IoStatsLoadStatus.Ok, new IoDays(), null, null);
    }

    private static string? SetAside(string path)
    {
        string target = path + ".bad";
        try { File.Move(path, target, overwrite: true); return target; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool TryReadFile(string path, DateOnly today, int retentionDays, int maxSources, out IoDays? days, out string? error)
    {
        days = null;
        error = null;
        FileInfo info = new(path);
        if (!info.Exists) { error = "file missing"; return false; }
        if (info.LinkTarget != null) { error = "not a regular file"; return false; }
        if (info.Length > MaximumFileBytes) { error = "file larger than " + MaximumFileBytes / (1024 * 1024) + " MiB"; return false; }
        byte[] bytes;
        using (FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            if (stream.Length > MaximumFileBytes) { error = "file larger than " + MaximumFileBytes / (1024 * 1024) + " MiB"; return false; }
            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }
        try
        {
            days = Parse(bytes, today, retentionDays, maxSources);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or FormatException or OverflowException)
        {
            error = ex.Message;
            return false;
        }
    }

    internal static IoDays Parse(ReadOnlySpan<byte> utf8, DateOnly today, int retentionDays, int maxSources)
    {
        JsonReaderOptions strict = new() { MaxDepth = 8, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false };
        CheckNoDuplicateProperties(utf8, strict);
        using JsonDocument document = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The statistics document is not an object.");
        if (!root.TryGetProperty("version", out JsonElement version) || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out int number) || number != FormatVersion)
            throw new InvalidDataException("Unsupported statistics version.");
        if (!root.TryGetProperty("days", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The statistics have no day list.");
        if (list.GetArrayLength() > MaximumDays) throw new InvalidDataException("The statistics hold too many days.");

        DateOnly oldestKept = today.AddDays(-(retentionDays - 1));
        DateOnly newestKept = today.AddDays(2); // time zone travel moves "today" by a day or so; beyond that a day is noise
        HashSet<DateOnly> seen = [];
        IoDays result = new();
        foreach (JsonElement entry in list.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) throw new InvalidDataException("A statistics day is not an object.");
            if (!entry.TryGetProperty("day", out JsonElement dayText) || dayText.ValueKind != JsonValueKind.String
                || !DateOnly.TryParseExact(dayText.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly day))
                throw new InvalidDataException("A statistics day has no valid date.");
            if (!seen.Add(day)) throw new InvalidDataException("A statistics day appears twice.");
            if (!entry.TryGetProperty("sources", out JsonElement sources) || sources.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("A statistics day has no sources.");
            int count = 0;
            Dictionary<string, IoCounts> parsed = new(StringComparer.Ordinal);
            foreach (JsonProperty source in sources.EnumerateObject())
            {
                if (++count > MaximumSourcesPerDay) throw new InvalidDataException("A statistics day holds too many sources.");
                string key = source.Name;
                if (key.Length is 0 or > IoStatsKeys.MaximumKeyLength || ContainsControl(key))
                    throw new InvalidDataException("A statistics source key is invalid.");
                if (source.Value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("A statistics source is not an object.");
                parsed[key] = new IoCounts(NetDown: Counter(source.Value, "down"), NetUp: Counter(source.Value, "up"),
                    DiskWritten: Counter(source.Value, "diskWrite"), DiskRead: Counter(source.Value, "diskRead"),
                    Requests: Counter(source.Value, "requests"), Errors: Counter(source.Value, "errors"));
            }
            if (day < oldestKept || day > newestKept) continue;
            result.Merge(day, parsed, maxSources);
        }
        return result;
    }

    private static bool ContainsControl(string value)
    {
        foreach (char c in value) if (char.IsControl(c)) return true;
        return false;
    }

    private static long Counter(JsonElement source, string name)
    {
        if (!source.TryGetProperty(name, out JsonElement value)) return 0;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long number) || number < 0)
            throw new InvalidDataException("A statistics counter is invalid.");
        return number;
    }

    /// <summary>Duplicate property names at any depth make the document ambiguous; they count as damage.</summary>
    private static void CheckNoDuplicateProperties(ReadOnlySpan<byte> utf8, JsonReaderOptions options)
    {
        Utf8JsonReader reader = new(utf8, options);
        Stack<HashSet<string>> objects = new();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject: objects.Push(new HashSet<string>(StringComparer.Ordinal)); break;
                case JsonTokenType.EndObject: objects.Pop(); break;
                case JsonTokenType.PropertyName:
                    if (!objects.Peek().Add(reader.GetString() ?? string.Empty))
                        throw new InvalidDataException("The statistics repeat a property name.");
                    break;
            }
        }
    }

    /// <summary>
    /// Replaces the file with a document made by <see cref="Serialize"/>. Returns false (and leaves the file alone) when the
    /// write failed; the caller keeps its data and tries again later.
    /// </summary>
    internal bool TrySave(string json, out string? error)
    {
        error = null;
        try
        {
            _lease.RunExclusive(() => { AtomicJsonFile.WriteAllText(_path, json, backupCurrentFile: true); return true; });
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException or NotSupportedException)
        {
            error = ex.Message;
            return false;
        }
    }

    internal bool Delete()
    {
        try
        {
            return _lease.RunExclusive(() =>
            {
                foreach (string path in new[] { _path, _path + ".bak", _path + ".bad" })
                    if (File.Exists(path)) File.Delete(path);
                return true;
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException) { return false; }
    }

    /// <summary>Every day inside the retention window, oldest first. If the document would pass the reading ceiling, the oldest days are left out.</summary>
    internal static string Serialize(IoDays days, DateOnly today, int retentionDays)
    {
        DateOnly oldestKept = today.AddDays(-(retentionDays - 1));
        List<KeyValuePair<DateOnly, Dictionary<string, IoCounts>>> kept = [];
        foreach (KeyValuePair<DateOnly, Dictionary<string, IoCounts>> day in days.Days)
            if (day.Key >= oldestKept && day.Value.Count > 0) kept.Add(day);
        for (int skip = 0; ; skip++)
        {
            string json = Write(kept, skip);
            if (Encoding.UTF8.GetByteCount(json) <= MaximumFileBytes || skip >= kept.Count) return json;
        }
    }

    private static string Write(List<KeyValuePair<DateOnly, Dictionary<string, IoCounts>>> days, int skip)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", FormatVersion);
            writer.WriteStartArray("days");
            for (int i = skip; i < days.Count; i++)
            {
                writer.WriteStartObject();
                writer.WriteString("day", days[i].Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                writer.WriteStartObject("sources");
                foreach ((string key, IoCounts c) in days[i].Value)
                {
                    writer.WriteStartObject(key);
                    writer.WriteNumber("down", c.NetDown);
                    if (c.NetUp != 0) writer.WriteNumber("up", c.NetUp);
                    writer.WriteNumber("diskWrite", c.DiskWritten);
                    writer.WriteNumber("diskRead", c.DiskRead);
                    writer.WriteNumber("requests", c.Requests);
                    writer.WriteNumber("errors", c.Errors);
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }
}
