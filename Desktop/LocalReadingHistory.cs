using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeowChan.Desktop;

/// <summary>One place in the reading history: the anchor of a thread and when it was last read there (UTC).</summary>
internal sealed record LocalReadingHistoryEntry(LocalReadingPosition Position, DateTimeOffset At)
{
    /// <summary>The post this thread was last read at (the anchor of its position).</summary>
    [JsonIgnore]
    internal string LastReadPostId => Position.PostId;

    internal void Validate()
    {
        Position?.Validate();
        if (Position == null || At.Year < 2000 || At.Year > 9000)
            throw new InvalidDataException("The saved reading history is invalid.");
    }
}

/// <summary>
/// The recent reading places, newest first, one entry per thread (<c>reading-history.json</c>, beside <c>reading-position.json</c>).
/// Local identities and a short title snapshot only. The list length is the <c>ReadingHistoryLimit</c> setting; the file size is bounded.
/// </summary>
internal sealed record LocalReadingHistory(LocalReadingHistoryEntry[] Entries)
{
    /// <summary>The fixed file ceiling (documented, not a setting): the entry limit is the user-facing bound.</summary>
    internal const int MaximumFileBytes = 64 * 1024;

    internal static LocalReadingHistory Empty { get; } = new(Array.Empty<LocalReadingHistoryEntry>());

    internal void Validate()
    {
        if (Entries == null) throw new InvalidDataException("The saved reading history is invalid.");
        foreach (LocalReadingHistoryEntry entry in Entries)
        {
            if (entry == null) throw new InvalidDataException("The saved reading history is invalid.");
            entry.Validate();
        }
    }

    /// <summary>
    /// The history with <paramref name="position"/> as the newest entry: an earlier entry of the same thread is replaced, the list is
    /// cut to <paramref name="limit"/> (the oldest fall off) and, should the file ceiling still be exceeded, to what fits.
    /// </summary>
    internal static LocalReadingHistory Add(LocalReadingHistory? current, LocalReadingPosition position, DateTimeOffset at, int limit)
    {
        position.Validate();
        limit = Math.Clamp(limit, Config.ReadingHistoryLimitMinimum, Config.ReadingHistoryLimitMaximum);
        List<LocalReadingHistoryEntry> entries = new() { new LocalReadingHistoryEntry(position, at) };
        foreach (LocalReadingHistoryEntry entry in current?.Entries ?? Array.Empty<LocalReadingHistoryEntry>())
            if (entry.Position.ThreadId != position.ThreadId) entries.Add(entry);
        LocalReadingHistory result = new(entries.Take(limit).ToArray());
        while (result.Entries.Length > 1 && result.Serialize().Length > MaximumFileBytes)
            result = new(result.Entries[..^1]);
        return result;
    }

    /// <summary>The newest <paramref name="limit"/> entries (a lowered limit takes effect on what the menu shows at once).</summary>
    internal IReadOnlyList<LocalReadingHistoryEntry> Newest(int limit) =>
        Entries.Take(Math.Clamp(limit, Config.ReadingHistoryLimitMinimum, Config.ReadingHistoryLimitMaximum)).ToArray();

    /// <summary>The newest entry of <paramref name="thread"/>, or null when this thread has no saved place.</summary>
    internal LocalReadingHistoryEntry? Find(ResourceId thread) => Entries.FirstOrDefault(entry => entry.Position.ThreadId == thread);

    internal byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this, ReadingHistoryJsonContext.Default.LocalReadingHistory);

    internal static LocalReadingHistory Parse(byte[] bytes)
    {
        if (bytes.Length > MaximumFileBytes) throw new InvalidDataException("The saved reading history is too large.");
        LocalReadingHistory history = JsonSerializer.Deserialize(bytes, ReadingHistoryJsonContext.Default.LocalReadingHistory)
            ?? throw new InvalidDataException("The saved reading history is empty.");
        history.Validate();
        return history;
    }

    /// <summary>
    /// Reads a history file the way an upgrade has to: every entry is judged on its own, so one damaged or unknown entry (a newer
    /// version's, a hand-edited one) never costs the others. Entries come out newest first, one per thread (the newest wins); the
    /// count of dropped entries is reported. A file that is no history at all (not JSON, no entry list, too large) still throws, so
    /// the caller can set it aside instead of overwriting it silently.
    /// </summary>
    internal static LocalReadingHistory ParseTolerant(byte[] bytes, out int dropped)
    {
        if (bytes.Length > MaximumFileBytes) throw new InvalidDataException("The saved reading history is too large.");
        using JsonDocument document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty(nameof(Entries), out JsonElement list) || list.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The saved reading history is invalid.");
        List<LocalReadingHistoryEntry> kept = new();
        dropped = 0;
        foreach (JsonElement element in list.EnumerateArray())
        {
            try
            {
                LocalReadingHistoryEntry? entry = element.Deserialize(ReadingHistoryJsonContext.Default.LocalReadingHistoryEntry);
                if (entry == null) throw new InvalidDataException("The saved reading history entry is empty.");
                entry.Validate();
                kept.Add(entry);
            }
            catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException)
            { dropped++; }
        }
        // Newest first (a stable sort keeps the file order of equal times); an older entry of the same thread is a duplicate.
        HashSet<ResourceId> seen = new();
        List<LocalReadingHistoryEntry> ordered = new();
        foreach (LocalReadingHistoryEntry entry in kept.OrderByDescending(entry => entry.At))
        {
            if (seen.Add(entry.Position.ThreadId)) ordered.Add(entry);
            else dropped++;
        }
        return new LocalReadingHistory(ordered.ToArray());
    }
}

[JsonSerializable(typeof(LocalReadingHistory))]
[JsonSerializable(typeof(LocalReadingHistoryEntry))]
internal partial class ReadingHistoryJsonContext : JsonSerializerContext { }
