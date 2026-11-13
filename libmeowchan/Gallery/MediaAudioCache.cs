using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using MeowChan.Persistence;

namespace MeowChan.Gallery;

/// <summary>
/// What the probe learned per video, keyed like the page's tile grouping (SHA-256 ignoring case, plus length;
/// equal bytes are one answer, see <see cref="KeyOf"/>). Immutable.
/// An entry records that the file was probed at the current <see cref="MediaAudioProbe.Version"/>; its
/// state may still be Unknown when the headers could not decide. A hash without an entry has not been probed.
/// </summary>
internal sealed class MediaAudioMap
{
    private readonly Dictionary<string, MediaAudio> _entries;

    private MediaAudioMap(Dictionary<string, MediaAudio> entries) => _entries = entries;

    internal static MediaAudioMap Empty { get; } = new(new Dictionary<string, MediaAudio>(StringComparer.Ordinal));

    internal int Count => _entries.Count;
    internal IEnumerable<KeyValuePair<string, MediaAudio>> Entries => _entries;

    /// <summary>The bytes identity the page groups by; null for an occurrence without a saved file.</summary>
    internal static string? KeyOf(MediaLibraryEntry entry) => MediaLibraryGrouping.ByteKey(entry);

    internal static string KeyOf(string sha256, long length) => sha256.ToLowerInvariant() + ":" + length;

    /// <summary>The recorded state; Unknown for bytes that have no entry or whose probe was undecided.</summary>
    internal MediaAudio Get(string? key) => key != null && _entries.TryGetValue(key, out MediaAudio state) ? state : MediaAudio.Unknown;

    internal MediaAudio Get(MediaLibraryEntry entry) => Get(KeyOf(entry));

    internal bool WasProbed(string? key) => key != null && _entries.ContainsKey(key);

    internal MediaAudioMap With(IEnumerable<KeyValuePair<string, MediaAudio>> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        Dictionary<string, MediaAudio> next = new(_entries, StringComparer.Ordinal);
        foreach ((string sha, MediaAudio state) in results) next[sha] = state;
        return new MediaAudioMap(next);
    }

    /// <summary>Only the entries whose key is in <paramref name="keep"/>; drops results of media that left the index.</summary>
    internal MediaAudioMap Retain(IReadOnlySet<string> keep)
    {
        ArgumentNullException.ThrowIfNull(keep);
        return new MediaAudioMap(_entries.Where(pair => keep.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
    }
}

/// <summary>
/// What a cache write may hold: the file size and the number of entries (both settings, Settings, Storage and limits).
/// A write above either limit keeps only the entries that fit; it never makes an existing file unreadable.
/// </summary>
internal readonly record struct MediaAudioCacheLimits(long Bytes, int Entries)
{
    internal static MediaAudioCacheLimits Default { get; } = new(MediaAudioCache.DefaultLimitBytes, MediaAudioCache.DefaultEntryLimit);
}

/// <summary>
/// Disposable file copy of the audio results, next to the media index cache. It holds no user data and no
/// index rows. Any problem (missing, damaged, newer, oversized, unknown field) means "no cache": the caller
/// probes again. An entry made by another probe version is ignored, not trusted, so a parser change re-probes.
/// </summary>
internal static class MediaAudioCache
{
    internal const string FileName = "media-audio-v1.json";
    /// <summary>
    /// Fixed technical ceiling of the file: a larger file is never read and nothing larger is ever written. The user's write
    /// limits (settings) are at most the ceilings, so lowering a setting never makes an existing file unreadable; the next
    /// write only makes it smaller.
    /// </summary>
    internal const long MaximumBytes = 128L * 1024 * 1024;
    /// <summary>Entries a file may describe, at most (about 110 bytes each, so this fits <see cref="MaximumBytes"/>).</summary>
    internal const int EntryCeiling = 1_000_000;
    /// <summary>Default write limits: the former fixed limits, so default behavior does not change.</summary>
    internal const long DefaultLimitBytes = 64L * 1024 * 1024;
    internal const long MinimumLimitBytes = 16L * 1024 * 1024;
    internal const int DefaultEntryLimit = 400_000;
    internal const int MinimumEntryLimit = 50_000;
    private const int CurrentVersion = 1;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions Options = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 4
    };

    private sealed record Document(int Version, List<EntryDto> Entries);
    // Sha and Len identify the bytes; P: probe version that made the answer; R: "sound", "silent" or "unknown".
    private sealed record EntryDto(string Sha, long Len, int P, string R);

    /// <summary>Null when there is no usable cache. Never throws for file content problems.</summary>
    internal static MediaAudioMap? Load(string path)
    {
        try
        {
            using FileStream stream = RegularProfileFile.OpenRead(path);
            if (stream.Length > MaximumBytes) return null;
            byte[] bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            Document? document = JsonSerializer.Deserialize<Document>(StrictUtf8.GetString(bytes), Options);
            if (document is not { Version: CurrentVersion } || document.Entries == null || document.Entries.Count > EntryCeiling) return null;
            HashSet<string> seen = new(StringComparer.Ordinal);
            List<KeyValuePair<string, MediaAudio>> current = new();
            foreach (EntryDto entry in document.Entries)
            {
                if (entry == null || !Sha256(entry.Sha) || entry.Len < 0 || !seen.Add(MediaAudioMap.KeyOf(entry.Sha, entry.Len)) || entry.P < 1
                    || !TryParse(entry.R, out MediaAudio state)) return null;
                if (entry.P == MediaAudioProbe.Version) current.Add(new(MediaAudioMap.KeyOf(entry.Sha, entry.Len), state));
            }
            return MediaAudioMap.Empty.With(current);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or DecoderFallbackException or ArgumentException or InvalidDataException or NotSupportedException or OverflowException)
        { return null; }
    }

    /// <summary>
    /// Atomic replace. A map above <paramref name="limits"/> (default: the default limits) is written with the first entries in
    /// key order that fit, never above the technical ceilings; the rest is probed again when needed. Returns false when nothing
    /// was written (not even an empty document fits); IO failures throw for the caller to ignore.
    /// </summary>
    internal static bool Save(string path, MediaAudioMap map, MediaAudioCacheLimits? limits = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(map);
        MediaAudioCacheLimits effective = limits ?? MediaAudioCacheLimits.Default;
        long maximumBytes = Math.Min(Math.Max(0, effective.Bytes), MaximumBytes);
        int keep = Math.Min(Math.Max(0, effective.Entries), EntryCeiling);
        List<EntryDto> all = map.Entries.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new EntryDto(pair.Key[..64], long.Parse(pair.Key[65..], System.Globalization.CultureInfo.InvariantCulture), MediaAudioProbe.Version, Name(pair.Value))).ToList();
        keep = Math.Min(keep, all.Count);
        string json = Serialize(all, keep);
        long size = StrictUtf8.GetByteCount(json);
        while (size > maximumBytes && keep > 0)
        {
            // Entries are about the same size: scale the count, always at least one entry less.
            keep = Math.Min(keep - 1, (int)(keep * (double)maximumBytes / size));
            json = Serialize(all, keep);
            size = StrictUtf8.GetByteCount(json);
        }
        if (size > maximumBytes) return false;
        // A cache needs neither a backup nor a preserved copy of the previous version: plain atomic replace.
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(StrictUtf8.GetBytes(json));
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return true;
    }

    private static string Serialize(List<EntryDto> all, int count) =>
        JsonSerializer.Serialize(new Document(CurrentVersion, count == all.Count ? all : all.GetRange(0, count)), Options);

    private static string Name(MediaAudio state) => state switch { MediaAudio.HasSound => "sound", MediaAudio.Silent => "silent", _ => "unknown" };

    private static bool TryParse(string? value, out MediaAudio state)
    {
        state = value switch { "sound" => MediaAudio.HasSound, "silent" => MediaAudio.Silent, _ => MediaAudio.Unknown };
        return value is "sound" or "silent" or "unknown";
    }

    private static bool Sha256(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
