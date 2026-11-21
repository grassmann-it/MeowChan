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
/// <summary>A colour label of one media identity (red, yellow, green or blue), stored beside the ratings in the same file.</summary>
internal sealed record MediaLabelEntry(FavoriteAssetKey Key, string Color);
internal sealed record MediaLabelCommit(FavoriteAssetKey Key, string? Color, bool Changed);

/// <summary>The four colour labels (Lightroom keys 6 to 9) in their stored spelling.</summary>
internal static class MediaLabelColors
{
    internal const string Red = "red", Yellow = "yellow", Green = "green", Blue = "blue";
    internal static readonly IReadOnlyList<string> All = Array.AsReadOnly(new[] { Red, Yellow, Green, Blue });
    internal static bool IsValid(string? color) => color != null && All.Contains(color, StringComparer.Ordinal);
}

/// <summary>Profile-owned ratings for exact committed media versions. Never resolves media bytes.</summary>
/// <summary>A ratings file of a newer version: not readable by this version, but valid data that must be kept.</summary>
internal sealed class NewerRatingsVersionException : Exception { }

public sealed class MediaRatingStore
{
    /// <summary>The default entry limit, not a maximum: the limit is the FavoriteEntryLimit setting (<see cref="ProfileStoreLimits.FavoriteEntries"/>); reads accept up to <see cref="ProfileStoreLimits.FavoriteEntryCeiling"/>.</summary>
    [Obsolete("Use ProfileStoreLimits.DefaultFavoriteEntries; the effective limit is the FavoriteEntryLimit setting.")]
    public const int MaximumEntries = ProfileStoreLimits.DefaultFavoriteEntries;
    /// <summary>The default file limit (8 MiB), not a maximum: the limit is the FavoriteFileLimitMiB setting (<see cref="ProfileStoreLimits.FavoriteFileBytes"/>); reads accept up to <see cref="ProfileStoreLimits.FavoriteFileCeilingBytes"/>.</summary>
    [Obsolete("Use ProfileStoreLimits.DefaultFavoriteFileBytes; the effective limit is the FavoriteFileLimitMiB setting.")]
    public const int MaximumBytes = ProfileStoreLimits.DefaultFavoriteFileBytes;
    /// <summary>Labels is an additive optional list: a file without it (every 1.0.2 / 1.1.0 file) reads as "no labels", and a file without labels is written without the property, byte for byte as before.</summary>
    private sealed record Document(int Version, List<MediaRatingEntry> Entries,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] List<MediaLabelEntry>? Labels = null);
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

    internal IReadOnlyList<MediaLabelEntry> ListLabels() => _lease.RunExclusive(() =>
        (IReadOnlyList<MediaLabelEntry>)Array.AsReadOnly((Read().Labels ?? new()).ToArray()));

    /// <summary>Sets (or with null removes) the colour label of a media identity. One label per identity; ratings are untouched.</summary>
    internal MediaLabelCommit SetLabel(FavoriteAssetKey key, string? color, CancellationToken token = default)
    {
        ValidateKey(key);
        if (color != null && !MediaLabelColors.IsValid(color)) throw new ArgumentOutOfRangeException(nameof(color), "Labels are red, yellow, green or blue.");
        return _lease.RunExclusive(() =>
        {
            token.ThrowIfCancellationRequested();
            Document document = Read();
            List<MediaLabelEntry> labels = document.Labels ?? new();
            int index = labels.FindIndex(entry => entry.Key == key);
            if (index < 0 && color == null || index >= 0 && labels[index].Color == color) return new MediaLabelCommit(key, color, false);
            if (color == null) labels.RemoveAt(index);
            else if (index >= 0) labels[index] = new(key, color);
            else
            {
                if (labels.Count >= _lease.StoreLimits.FavoriteEntries)
                    throw PrivateStoreLimit.Refusal(PrivateStoreLimitKind.FavoriteEntries, _lease.StoreLimits.FavoriteEntries,
                        "The ratings index has reached its entry limit.");
                labels.Add(new(key, color));
            }
            Write(document with { Labels = labels.Count == 0 ? null : labels }, token);
            return new MediaLabelCommit(key, color, true);
        });
    }

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
                if (document.Entries.Count >= _lease.StoreLimits.FavoriteEntries)
                    throw PrivateStoreLimit.Refusal(PrivateStoreLimitKind.FavoriteEntries, _lease.StoreLimits.FavoriteEntries,
                        "The ratings index has reached its entry limit.");
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
            int labelCount = document.Labels?.RemoveAll(entry => ids.Contains(entry.Key.Thread)) ?? 0;
            if (count + labelCount > 0) Write(labelCount > 0 && document.Labels!.Count == 0 ? document with { Labels = null } : document, token);
            return count;
        });
    }
    public static void ValidateKey(FavoriteAssetKey key) => FavoriteStore.Validate(key);
    private void Write(Document document, CancellationToken token)
    {
        string json = JsonSerializer.Serialize(document);
        int written = Encoding.UTF8.GetByteCount(json);
        if (written > _lease.StoreLimits.FavoriteFileBytes && written > ProfileStoreLimits.FileLength(_path))
            throw PrivateStoreLimit.Refusal(PrivateStoreLimitKind.FavoriteFile, _lease.StoreLimits.FavoriteFileBytes,
                "The ratings index has reached its size limit.");
        // The backup is replaced by the current primary (valid: Read succeeded before every write). A damaged backup is set
        // aside as .bad first, so the good primary is never lost and the evidence is kept; one that cannot be opened is not
        // ours to move, so the save is refused.
        string backupPath = _path + ".bak";
        try { ReadDocument(backupPath, reportNewerVersion: true); }
        catch (FileNotFoundException) { }
        catch (NewerRatingsVersionException)
        { throw new ProfileBackupBlockedException(backupPath, ProfileBackupBlockReason.NewerVersion); }
        catch (InvalidDataException)
        {
            if (AtomicJsonFile.SetAsideDamagedBackup(backupPath) == null)
                throw new ProfileBackupBlockedException(backupPath, ProfileBackupBlockReason.Unreadable);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new ProfileBackupBlockedException(backupPath, ProfileBackupBlockReason.Unreadable); }
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
        return ReadDocument(_path);
    }
    private static Document ReadDocument(string path, bool reportNewerVersion = false)
    {
        RejectLink(path);
        if (new FileInfo(path).Length > ProfileStoreLimits.FavoriteFileCeilingBytes) throw new InvalidDataException("The ratings index exceeds its size limit.");
        Document? document;
        try
        {
            // Never replace malformed bytes inside an identity with U+FFFD. Decode before
            // parsing so every property, including otherwise ignored properties, is validated.
            byte[] bytes = File.ReadAllBytes(path);
            string json = StrictUtf8.GetString(bytes);
            // Accept an optional UTF-8 BOM without enabling UTF-16/32 encoding detection.
            if (json.Length > 0 && json[0] == '\uFEFF') json = json[1..];
            // The serializer keeps the last of two equal names: a second "Entries" would silently replace every rating.
            if (JsonDuplicateProperties.Any(bytes)) throw new InvalidDataException("The ratings index repeats a property.");
            document = JsonSerializer.Deserialize<Document>(json);
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException)
        { throw new InvalidDataException("The ratings index is invalid.", error); }
        if (reportNewerVersion && document?.Version > 1) throw new NewerRatingsVersionException();
        if (document?.Version != 1 || document.Entries == null || document.Entries.Count > ProfileStoreLimits.FavoriteEntryCeiling)
            throw new InvalidDataException("The ratings index version or entry count is invalid.");
        if (document.Labels != null)
        {
            if (document.Labels.Count > ProfileStoreLimits.FavoriteEntryCeiling) throw new InvalidDataException("The ratings index label count is invalid.");
            HashSet<FavoriteAssetKey> labelled = new();
            foreach (MediaLabelEntry label in document.Labels)
            {
                if (label == null || !MediaLabelColors.IsValid(label.Color)) throw new InvalidDataException("The ratings index contains an invalid label.");
                try { ValidateKey(label.Key); }
                catch (ArgumentException error) { throw new InvalidDataException("The ratings index contains an invalid identity.", error); }
                if (!labelled.Add(label.Key)) throw new InvalidDataException("The ratings index contains duplicate label identities.");
            }
        }
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
