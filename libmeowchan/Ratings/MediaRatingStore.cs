using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using MeowChan.Favorites;
using MeowChan.Persistence;

namespace MeowChan.Ratings;

public sealed record MediaRatingEntry(FavoriteAssetKey Key, int Value);
public sealed record MediaRatingCommit(FavoriteAssetKey Key, int? Value, bool Changed);

/// <summary>Profile-owned ratings for exact committed media versions. Never resolves media bytes.</summary>
public sealed class MediaRatingStore
{
    public const int MaximumEntries = 50_000;
    public const int MaximumBytes = 8_000_000;
    private sealed record Document(int Version, List<MediaRatingEntry> Entries);
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly ProfileLease _lease;
    private readonly string _path;
    public MediaRatingStore(string profileDirectory, ProfileLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lease.EnsureMatches(profileDirectory);
        _lease = lease;
        _path = Path.Combine(lease.ProfileDirectory, "media-ratings.json");
    }
    public IReadOnlyList<MediaRatingEntry> List() => _lease.RunExclusive(() =>
        (IReadOnlyList<MediaRatingEntry>)Array.AsReadOnly(Read().Entries.ToArray()));

    public MediaRatingCommit Set(FavoriteAssetKey key, int? value, CancellationToken token = default)
    {
        ValidateKey(key);
        if (value is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(value), "Ratings must be whole values from 1 to 5.");
        return _lease.RunExclusive(() =>
        {
            token.ThrowIfCancellationRequested();
            Document document = Read();
            int index = document.Entries.FindIndex(entry => entry.Key == key);
            if (index < 0 && value == null || index >= 0 && document.Entries[index].Value == value)
                return new MediaRatingCommit(key, value, false);
            if (value == null) document.Entries.RemoveAt(index);
            else if (index >= 0) document.Entries[index] = new(key, value.Value);
            else
            {
                if (document.Entries.Count >= MaximumEntries) throw new InvalidDataException("The ratings index has reached its entry limit.");
                document.Entries.Add(new(key, value.Value));
            }
            Write(document, token);
            return new MediaRatingCommit(key, value, true);
        });
    }
    public int RemoveThreads(IReadOnlyCollection<ResourceId> threads, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(threads);
        HashSet<ResourceId> ids = threads.ToHashSet();
        return _lease.RunExclusive(() =>
        {
            token.ThrowIfCancellationRequested();
            Document document = Read();
            int count = document.Entries.RemoveAll(entry => ids.Contains(entry.Key.Thread));
            if (count > 0) Write(document, token);
            return count;
        });
    }
    public static void ValidateKey(FavoriteAssetKey key) => FavoriteStore.Validate(key);
    private void Write(Document document, CancellationToken token)
    {
        string json = JsonSerializer.Serialize(document);
        if (Encoding.UTF8.GetByteCount(json) > MaximumBytes) throw new InvalidDataException("The ratings index has reached its size limit.");
        RejectLink(_path + ".bak");
        AtomicJsonFile.WriteAllText(_path, json, backupCurrentFile: true, token);
    }
    private static void RejectLink(string path)
    {
        if (new FileInfo(path).LinkTarget != null || Directory.Exists(path))
            throw new InvalidDataException("The ratings index path is not a regular file.");
    }
    private Document Read()
    {
        RejectLink(_path);
        if (!File.Exists(_path))
        {
            RejectLink(_path + ".bak");
            if (File.Exists(_path + ".bak")) throw new InvalidDataException("The ratings index is missing but a backup exists. Explicit validated recovery is required; deleted ratings will not be restored automatically.");
            return new(1, new());
        }
        if (new FileInfo(_path).Length > MaximumBytes) throw new InvalidDataException("The ratings index exceeds its size limit.");
        Document? document;
        try
        {
            // Never replace malformed bytes inside an identity with U+FFFD. Decode before
            // parsing so every property, including otherwise ignored properties, is validated.
            string json = StrictUtf8.GetString(File.ReadAllBytes(_path));
            // Accept an optional UTF-8 BOM without enabling UTF-16/32 encoding detection.
            if (json.Length > 0 && json[0] == '\uFEFF') json = json[1..];
            document = JsonSerializer.Deserialize<Document>(json);
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException)
        { throw new InvalidDataException("The ratings index is invalid.", error); }
        if (document?.Version != 1 || document.Entries == null || document.Entries.Count > MaximumEntries)
            throw new InvalidDataException("The ratings index version or entry count is invalid.");
        HashSet<FavoriteAssetKey> seen = new();
        foreach (MediaRatingEntry entry in document.Entries)
        {
            if (entry == null || entry.Value is < 1 or > 5) throw new InvalidDataException("The ratings index contains an invalid rating.");
            try { ValidateKey(entry.Key); }
            catch (ArgumentException error) { throw new InvalidDataException("The ratings index contains an invalid identity.", error); }
            if (!seen.Add(entry.Key)) throw new InvalidDataException("The ratings index contains duplicate identities.");
        }
        return document;
    }
}
