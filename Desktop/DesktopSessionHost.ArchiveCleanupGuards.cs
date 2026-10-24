using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Favorites;
using MeowChan.Persistence;
using MeowChan.Ratings;

namespace MeowChan.Desktop;

internal sealed record ArchiveCleanupGuards(string ProtectionToken, string WorkToken, bool Reliable,
    IReadOnlyDictionary<ResourceId, IReadOnlyList<ArchiveCleanupReason>> Protections,
    ArchiveCleanupWorkState? Work, IReadOnlyList<string> Errors);

internal sealed partial class DesktopSessionHost
{
    private static readonly UTF8Encoding CleanupUtf8 = new(false, true);

    // Cheap execution guard: validates full protection indexes and the authoritative work journal,
    // without hashing any media or creating optional stores with write-side constructor behavior.
    private async Task<ArchiveCleanupGuards> ReadArchiveCleanupGuardTokensAsync(
        IReadOnlyCollection<ResourceId> openThreadIds, CancellationToken token)
    {
        List<string> errors = new();
        Dictionary<ResourceId, List<ArchiveCleanupReason>> protectedIds = new();
        void Protect(ResourceId id, ArchiveCleanupReason reason)
        {
            if (!protectedIds.TryGetValue(id, out var reasons)) protectedIds[id] = reasons = new();
            if (!reasons.Contains(reason)) reasons.Add(reason);
        }
        string protectionToken;
        try
        {
            token.ThrowIfCancellationRequested();
            string before = ReadCleanupProtectionFiles(token);
            foreach (ResourceId id in new ThreadFavoriteStore(ProfileDirectory, _profileLease).ReadSnapshot().Threads)
                Protect(id, ArchiveCleanupReason.ThreadFavorite);
            foreach (FavoriteEntry favorite in new FavoriteStore(ProfileDirectory, _profileLease).List())
                Protect(favorite.Key.Thread, ArchiveCleanupReason.MediaFavorite);
            foreach (MediaRatingEntry rating in new MediaRatingStore(ProfileDirectory, _profileLease).List())
                Protect(rating.Key.Thread, ArchiveCleanupReason.MediaRating);
            foreach (PostBookmark bookmark in PostBookmarkStore.ReadOnly(ProfileDirectory, _profileLease).Bookmarks)
                Protect(bookmark.Key.Thread, ArchiveCleanupReason.PostBookmark);
            ReaderGroupsLoadResult groups = await new ReaderGroupStore(ProfileDirectory, _profileLease).LoadAsync(token).ConfigureAwait(false);
            if (groups.State != ReaderGroupsLoadState.Ready || groups.Snapshot == null)
                throw new InvalidDataException(groups.Error ?? "Reader groups require recovery.");
            foreach (ResourceId id in groups.Snapshot.Groups.SelectMany(group => group.Members))
                Protect(id, ArchiveCleanupReason.ReaderGroup);
            byte[]? positionBytes = ReadCleanupOptionalFile("reading-position.json", 16384, token);
            if (positionBytes != null)
            {
                LocalReadingPosition position = JsonSerializer.Deserialize(positionBytes, ReadingPositionJsonContext.Default.LocalReadingPosition)
                    ?? throw new InvalidDataException("The reading position is empty.");
                position.Validate(); Protect(position.ThreadId, ArchiveCleanupReason.ReadingPosition);
            }
            foreach (ResourceId id in openThreadIds) { ThreadFavoriteStore.Validate(id); Protect(id, ArchiveCleanupReason.OpenView); }
            string after = ReadCleanupProtectionFiles(token);
            if (before != after) throw new InvalidDataException("Protection indexes changed during inventory.");
            protectionToken = ArchiveCleanupFingerprint.Of(new { Files = before,
                Open = OrderedCleanupIds(openThreadIds) });
        }
        catch (Exception error) when (CleanupEvidenceError(error))
        {
            errors.Add("Protection indexes: " + error.Message);
            protectionToken = "unavailable-" + Guid.NewGuid().ToString("N");
        }
        ArchiveCleanupWorkState? work = null;
        string workToken;
        try
        {
            token.ThrowIfCancellationRequested();
            work = _registry.ReadArchiveCleanupWorkState();
            if (work.IsStopping) throw new InvalidOperationException("The registry is stopping.");
            workToken = ArchiveCleanupFingerprint.Of(new { OutputRoot = Path.GetFullPath(Config.DownloadPath), Work = work });
        }
        catch (Exception error) when (CleanupEvidenceError(error))
        {
            errors.Add("Work journal: " + error.Message);
            workToken = "unavailable-" + Guid.NewGuid().ToString("N");
        }
        return new(protectionToken, workToken, errors.Count == 0,
            new ReadOnlyDictionary<ResourceId, IReadOnlyList<ArchiveCleanupReason>>(protectedIds.ToDictionary(
                pair => pair.Key, pair => (IReadOnlyList<ArchiveCleanupReason>)Array.AsReadOnly(pair.Value.Order().ToArray()))),
            work, Array.AsReadOnly(errors.ToArray()));
    }

    private string ReadCleanupProtectionFiles(CancellationToken token)
    {
        var definitions = new[]
        {
            ("thread-favorites.json", "Version", "Threads", 8_000_000, true),
            ("media-favorites.json", "Version", "Entries", 8_000_000, true),
            ("media-ratings.json", "Version", "Entries", 8_000_000, true),
            ("post-bookmarks.json", "schemaVersion", "bookmarks", 32 * 1024 * 1024, false),
            ("reader-groups.json", "schemaVersion", "groups", 16 * 1024 * 1024, true),
            ("reading-position.json", "", "", 16384, false)
        };
        List<string> proofs = new();
        foreach (var (name, version, entries, maximum, backupSupported) in definitions)
        {
            byte[]? primary = ReadCleanupOptionalFile(name, maximum, token);
            byte[]? backup = ReadCleanupOptionalFile(name + ".bak", maximum, token);
            if (primary == null && backup != null || !backupSupported && backup != null)
                throw new InvalidDataException(name + " has unexpected recovery data.");
            foreach (byte[] bytes in new[] { primary, backup }.OfType<byte[]>())
            {
                using JsonDocument document = JsonDocument.Parse(CleanupUtf8.GetString(bytes));
                RejectCleanupDuplicateFields(document.RootElement);
                if (version.Length > 0 && (document.RootElement.GetProperty(version).GetInt32() != 1
                    || document.RootElement.GetProperty(entries).ValueKind != JsonValueKind.Array))
                    throw new InvalidDataException(name + " has an unsupported or invalid schema.");
            }
            proofs.Add(name + ":" + (primary == null ? "missing" : Convert.ToHexString(SHA256.HashData(primary)))
                + ":" + (backup == null ? "missing" : Convert.ToHexString(SHA256.HashData(backup))));
        }
        return ArchiveCleanupFingerprint.Of(proofs);
    }

    private byte[]? ReadCleanupOptionalFile(string name, int maximum, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string path = Path.Combine(ProfileDirectory, name);
        if (new FileInfo(path).LinkTarget != null) throw new InvalidDataException(name + " must not be a symbolic link.");
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > maximum) throw new InvalidDataException(name + " exceeds its size limit.");
            byte[] bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
            return bytes;
        }
        catch (FileNotFoundException) { return null; }
    }

    private static void RejectCleanupDuplicateFields(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("A protection document has duplicate fields.");
                RejectCleanupDuplicateFields(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in element.EnumerateArray()) RejectCleanupDuplicateFields(item);
    }

    private static ResourceId[] OrderedCleanupIds(IEnumerable<ResourceId> ids) => ids.Distinct()
        .OrderBy(id => id.Source, StringComparer.Ordinal).ThenBy(id => id.Board, StringComparer.Ordinal)
        .ThenBy(id => id.Thread, StringComparer.Ordinal).ToArray();

    private static bool CleanupEvidenceError(Exception error) => error is IOException or InvalidDataException
        or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException
        or OverflowException or FormatException or KeyNotFoundException or NotSupportedException or Microsoft.Data.Sqlite.SqliteException;
}
