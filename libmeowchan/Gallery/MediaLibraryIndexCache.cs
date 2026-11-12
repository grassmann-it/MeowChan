using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Persistence;

namespace MeowChan.Gallery;

/// <summary>
/// Disposable file copy of the media index so the media page does not rescan every thread on each
/// open. Any problem (missing, damaged, newer, oversized) means "no cache": the caller rebuilds.
/// The cache never decides anything: a thread is reused only when the builder sees an equal state.
/// </summary>
internal static class MediaLibraryIndexCache
{
    internal const string FileName = "media-index-v1.json";
    internal const long MaximumBytes = 256L * 1024 * 1024;
    internal const int MaximumEntries = 1_000_000;
    internal const int MaximumThreads = 200_000;
    // Version 2 adds the local tag slot of loose entries; version 3 adds the Danbooru post id of downloaded provider media.
    // An older or newer document is simply no cache: rebuild.
    private const int CurrentVersion = 3;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions Options = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 8
    };

    private sealed record Document(int Version, List<ThreadDto> Threads, List<EntryDto> Entries);
    private sealed record ThreadDto(string S, string B, string T, long R, bool C, List<ManifestDto> M);
    private sealed record ManifestDto(string D, string? G);
    private sealed record EntryDto(string S, string B, string T, string Title, long Rev, DateTimeOffset First, DateTimeOffset? Last,
        string? Post, long? Ord, DateTimeOffset? At, int? Pos, string? Key, string? Dir, string? Asset, string? Sha, long? Len,
        string? Mid, string Var, string? Name, SavedMediaKind Kind, bool Missing, bool Loose, bool Old, string? Slot, string? Bp = null);

    /// <summary>Null when there is no usable cache. Never throws for file content problems.</summary>
    internal static MediaLibrarySnapshot? Load(string path)
    {
        try
        {
            using FileStream stream = RegularProfileFile.OpenRead(path);
            if (stream.Length > MaximumBytes) return null;
            byte[] bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            Document? document = JsonSerializer.Deserialize<Document>(StrictUtf8.GetString(bytes), Options);
            if (document is not { Version: CurrentVersion } || document.Threads == null || document.Entries == null
                || document.Threads.Count > MaximumThreads || document.Entries.Count > MaximumEntries) return null;
            List<MediaLibraryThreadState> threads = new(document.Threads.Count);
            HashSet<ResourceId> known = new();
            foreach (ThreadDto thread in document.Threads)
            {
                ResourceId id = Id(thread.S, thread.B, thread.T);
                if (thread.M == null || thread.R < 0 || !known.Add(id)
                    || thread.M.Any(manifest => manifest == null || string.IsNullOrWhiteSpace(manifest.D))) return null;
                threads.Add(new(id, thread.R, thread.M.Select(manifest => new MediaLibraryManifestState(manifest.D, manifest.G)).ToArray(), thread.C));
            }
            List<MediaLibraryEntry> entries = new(document.Entries.Count);
            foreach (EntryDto entry in document.Entries)
            {
                ResourceId id = Id(entry.S, entry.B, entry.T);
                // A downloaded provider post is no archived thread: its identity must be exactly the one its post id names,
                // and it never carries a post binding or a local tag slot.
                if (entry.Bp != null && (!DanbooruPostReference.IsValidPostId(entry.Bp) || id != BooruMediaIdentity.Thread(entry.Bp)
                    || entry.Post != null || entry.Key != null || entry.Slot != null || entry.Pos != null)) return null;
                // Every other entry belongs to a listed thread; a present entry names its file and bytes.
                if (entry.Bp == null && !known.Contains(id) || entry.Title == null || entry.Var == null || !Enum.IsDefined(entry.Kind)
                    || !entry.Missing && (entry.Dir == null || entry.Asset == null || !Sha256(entry.Sha))
                    // A local slot exists only on a present, unbound entry; anything else is a damaged cache.
                    || entry.Slot != null && (entry.Missing || entry.Key != null || entry.Slot.Length == 0)) return null;
                entries.Add(new(id, entry.Title, entry.Rev, entry.First, entry.Last, entry.Post, entry.Ord, entry.At, entry.Pos,
                    entry.Key, entry.Dir, entry.Asset, entry.Sha, entry.Len, entry.Mid, entry.Var, entry.Name, entry.Kind,
                    entry.Missing ? MediaLibraryPresence.Missing : MediaLibraryPresence.Present, entry.Loose, entry.Old, entry.Slot) { BooruPostId = entry.Bp });
            }
            // A cached index is a starting point for reuse, not a complete answer on its own.
            return new MediaLibrarySnapshot(entries, threads, Array.Empty<string>(), complete: threads.All(thread => thread.Complete));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or DecoderFallbackException or ArgumentException or InvalidDataException or NotSupportedException or OverflowException)
        { return null; }
    }

    /// <summary>Atomic replace. Failures are ignored by callers: the index is rebuilt next time.</summary>
    internal static void Save(string path, MediaLibrarySnapshot snapshot, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Entries.Count > MaximumEntries || snapshot.Threads.Count > MaximumThreads) return;
        Document document = new(CurrentVersion,
            snapshot.Threads.Select(thread => new ThreadDto(thread.Thread.Source, thread.Thread.Board, thread.Thread.Thread!,
                thread.Revision, thread.Complete, thread.Manifests.Select(manifest => new ManifestDto(manifest.Directory, manifest.Generation)).ToList())).ToList(),
            snapshot.Entries.Select(entry => new EntryDto(entry.Thread.Source, entry.Thread.Board, entry.Thread.Thread!,
                entry.ThreadTitle, entry.ThreadRevision, entry.FirstSeenAt, entry.LastCapturedAt, entry.PostId, entry.PostOrdinal,
                entry.PostedAt, entry.Position, entry.AttachmentRevisionKey, entry.Directory, entry.AssetId, entry.Sha256, entry.Length,
                entry.MediaId, entry.Variant, entry.Name, entry.Kind, entry.Presence == MediaLibraryPresence.Missing,
                entry.Unassigned, entry.Historical, entry.LocalSlot, entry.BooruPostId)).ToList());
        string json = JsonSerializer.Serialize(document, Options);
        if (StrictUtf8.GetByteCount(json) > MaximumBytes) return;
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
    }

    private static ResourceId Id(string? source, string? board, string? thread)
    {
        ResourceId id = new(source!, board!, thread);
        ArchiveValidation.ValidateId(id);
        return id;
    }

    private static bool Sha256(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
