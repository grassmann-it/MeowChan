using System;
using System.IO;
using System.Threading;

namespace MeowChan.Persistence;

/// <summary>What a refused write ran into; the desktop turns it into a localized sentence that names the setting.</summary>
internal enum PrivateStoreLimitKind
{
    FavoriteEntries, FavoriteFile, Bookmarks, SavedSearches, ReaderGroups
}

/// <summary>
/// A write that would grow a private list past its configured limit is refused with an <see cref="InvalidDataException"/>
/// (the contract the lists always had); this tags the exception with the limit so the desktop can say, in the user's
/// language, which setting to raise. Nothing was written when it is thrown.
/// </summary>
internal static class PrivateStoreLimit
{
    private const string KindKey = "MeowChan.PrivateStoreLimit.Kind";
    private const string LimitKey = "MeowChan.PrivateStoreLimit.Limit";

    internal static InvalidDataException Refusal(PrivateStoreLimitKind kind, long limit, string message)
    {
        InvalidDataException error = new(message);
        error.Data[KindKey] = kind;
        error.Data[LimitKey] = limit;
        return error;
    }

    /// <summary>True when <paramref name="error"/> is a refusal made by <see cref="Refusal"/>.</summary>
    internal static bool TryDescribe(Exception? error, out PrivateStoreLimitKind kind, out long limit)
    {
        kind = default;
        limit = 0;
        if (error?.Data[KindKey] is not PrivateStoreLimitKind found || error.Data[LimitKey] is not long value) return false;
        kind = found;
        limit = value;
        return true;
    }
}

/// <summary>
/// The configurable write limits of the profile's private lists (user settings), shared by every store instance of one
/// profile through <see cref="ProfileLease.StoreLimits"/>. Reads are bounded only by the fixed technical ceilings
/// below, so lowering a limit never makes an existing file unreadable: it only stops further growth. A write that does
/// not grow a list (removals, renames, notes) is always allowed. Thread-safe; a change applies to the next write.
/// The defaults are the values the lists had when they were fixed.
/// </summary>
internal sealed class ProfileStoreLimits
{
    /// <summary>Entries of each favorite and rating list (media favorites, thread favorites, ratings). Default 50,000.</summary>
    internal const int DefaultFavoriteEntries = 50_000;
    internal const int MinimumFavoriteEntries = 10_000;
    /// <summary>Fixed technical ceiling of the entry count a favorite or rating file may hold (reads and writes).</summary>
    internal const int FavoriteEntryCeiling = 500_000;
    /// <summary>Size of each favorite and rating file. Default 8 MiB.</summary>
    internal const int DefaultFavoriteFileBytes = 8 * 1024 * 1024;
    internal const int MinimumFavoriteFileBytes = 8 * 1024 * 1024;
    /// <summary>Fixed technical ceiling of a favorite or rating file (reads and writes).</summary>
    internal const int FavoriteFileCeilingBytes = 128 * 1024 * 1024;

    /// <summary>Post bookmarks. Default 10,000. The 8,000-codepoint note and the 32 MiB file stay fixed.</summary>
    internal const int DefaultBookmarks = 10_000;
    internal const int MinimumBookmarks = 1_000;
    internal const int BookmarkCeiling = 100_000;

    /// <summary>Saved archive searches. Default 500. The 8 MiB file stays fixed.</summary>
    internal const int DefaultSavedSearches = 500;
    internal const int MinimumSavedSearches = 50;
    internal const int SavedSearchCeiling = 5_000;

    /// <summary>Reader groups. Default 500. Members per group (1,000), memberships (20,000) and the 16 MiB file stay fixed.</summary>
    internal const int DefaultReaderGroups = 500;
    internal const int MinimumReaderGroups = 50;
    internal const int ReaderGroupCeiling = 5_000;

    private int _favoriteEntries = DefaultFavoriteEntries;
    private long _favoriteFileBytes = DefaultFavoriteFileBytes;
    private int _bookmarks = DefaultBookmarks;
    private int _savedSearches = DefaultSavedSearches;
    private int _readerGroups = DefaultReaderGroups;

    /// <summary>Most entries a write may leave in a favorite or rating list; a new entry at or above it is refused.</summary>
    internal int FavoriteEntries
    {
        get => Volatile.Read(ref _favoriteEntries);
        set => Volatile.Write(ref _favoriteEntries, Checked(value, MinimumFavoriteEntries, FavoriteEntryCeiling));
    }

    /// <summary>Largest size a write may grow a favorite or rating file to.</summary>
    internal long FavoriteFileBytes
    {
        get => Interlocked.Read(ref _favoriteFileBytes);
        set => Interlocked.Exchange(ref _favoriteFileBytes, Checked(value, MinimumFavoriteFileBytes, FavoriteFileCeilingBytes));
    }

    /// <summary>Most post bookmarks a write may leave.</summary>
    internal int Bookmarks
    {
        get => Volatile.Read(ref _bookmarks);
        set => Volatile.Write(ref _bookmarks, Checked(value, MinimumBookmarks, BookmarkCeiling));
    }

    /// <summary>Most saved archive searches a write may leave.</summary>
    internal int SavedSearches
    {
        get => Volatile.Read(ref _savedSearches);
        set => Volatile.Write(ref _savedSearches, Checked(value, MinimumSavedSearches, SavedSearchCeiling));
    }

    /// <summary>Most reader groups a write may leave.</summary>
    internal int ReaderGroups
    {
        get => Volatile.Read(ref _readerGroups);
        set => Volatile.Write(ref _readerGroups, Checked(value, MinimumReaderGroups, ReaderGroupCeiling));
    }

    /// <summary>Current size of a file, or 0 when it is missing or unreadable; a write that does not grow a file is never refused.</summary>
    internal static long FileLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return 0; }
    }

    private static int Checked(int value, int minimum, int maximum) =>
        value < minimum || value > maximum
            ? throw new ArgumentOutOfRangeException(nameof(value), "The limit is outside its supported range.") : value;

    private static long Checked(long value, long minimum, long maximum) =>
        value < minimum || value > maximum
            ? throw new ArgumentOutOfRangeException(nameof(value), "The limit is outside its supported range.") : value;
}
