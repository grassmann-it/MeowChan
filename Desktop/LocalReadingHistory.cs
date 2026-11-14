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

    internal byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this, ReadingHistoryJsonContext.Default.LocalReadingHistory);

    internal static LocalReadingHistory Parse(byte[] bytes)
    {
        if (bytes.Length > MaximumFileBytes) throw new InvalidDataException("The saved reading history is too large.");
        LocalReadingHistory history = JsonSerializer.Deserialize(bytes, ReadingHistoryJsonContext.Default.LocalReadingHistory)
            ?? throw new InvalidDataException("The saved reading history is empty.");
        history.Validate();
        return history;
    }
}

[JsonSerializable(typeof(LocalReadingHistory))]
internal partial class ReadingHistoryJsonContext : JsonSerializerContext { }
