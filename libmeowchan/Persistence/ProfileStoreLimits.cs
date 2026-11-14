using System;
using System.IO;
using System.Threading;

namespace MeowChan.Persistence;

/// <summary>What a refused write ran into; the desktop turns it into a localized sentence that names the setting.</summary>
internal enum PrivateStoreLimitKind
{
    FavoriteEntries, FavoriteFile, Bookmarks, SavedSearches, ReaderGroups, PostFilterRules, HiddenPosts, TagQueueEntries, TagQueueFile
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
    /// <summary>A write that does not grow the file is always allowed, so a small limit only stops growth.</summary>
    internal const int MinimumFavoriteFileBytes = 1024 * 1024;
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

    /// <summary>Post filter rules. Default 200. The 512-codepoint pattern and the 32 MiB file stay fixed.</summary>
    internal const int DefaultPostFilterRules = 200;
    internal const int MinimumPostFilterRules = 20;
    internal const int PostFilterRuleCeiling = 2_000;

    /// <summary>Individually hidden posts. Default 10,000. The 32 MiB file stays fixed.</summary>
    internal const int DefaultHiddenPosts = 10_000;
    internal const int MinimumHiddenPosts = 1_000;
    internal const int HiddenPostCeiling = 100_000;

    /// <summary>Entries of the tagging queue. Default 2,000. The 64 MiB file and 50,000 entries are the fixed technical ceilings of a read.</summary>
    internal const int DefaultTagQueueEntries = 2_000;
    internal const int MinimumTagQueueEntries = 50;
    internal const int TagQueueEntryCeiling = 50_000;
    /// <summary>Size of the tagging queue file. Default 8 MiB; a write that does not grow the file is always allowed.</summary>
    internal const int DefaultTagQueueFileBytes = 8 * 1024 * 1024;
    internal const int MinimumTagQueueFileBytes = 1024 * 1024;
    internal const int TagQueueFileCeilingBytes = 64 * 1024 * 1024;
    /// <summary>Finished entries the tagging queue keeps (older finished entries drop out). Default 500, 0 keeps none.</summary>
    internal const int DefaultTagQueueHistory = 500;
    internal const int MinimumTagQueueHistory = 0;
    internal const int TagQueueHistoryCeiling = 20_000;

    private int _favoriteEntries = DefaultFavoriteEntries;
    private long _favoriteFileBytes = DefaultFavoriteFileBytes;
    private int _bookmarks = DefaultBookmarks;
    private int _savedSearches = DefaultSavedSearches;
    private int _readerGroups = DefaultReaderGroups;
    private int _postFilterRules = DefaultPostFilterRules;
    private int _hiddenPosts = DefaultHiddenPosts;
    private int _tagQueueEntries = DefaultTagQueueEntries;
    private long _tagQueueFileBytes = DefaultTagQueueFileBytes;
    private int _tagQueueHistory = DefaultTagQueueHistory;

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

    /// <summary>Most post filter rules a write may leave.</summary>
    internal int PostFilterRules
    {
        get => Volatile.Read(ref _postFilterRules);
        set => Volatile.Write(ref _postFilterRules, Checked(value, MinimumPostFilterRules, PostFilterRuleCeiling));
    }

    /// <summary>Most individually hidden posts a write may leave.</summary>
    internal int HiddenPosts
    {
        get => Volatile.Read(ref _hiddenPosts);
        set => Volatile.Write(ref _hiddenPosts, Checked(value, MinimumHiddenPosts, HiddenPostCeiling));
    }

    /// <summary>Most entries a write may leave in the tagging queue; adding is refused at it, removing and state changes always work.</summary>
    internal int TagQueueEntries
    {
        get => Volatile.Read(ref _tagQueueEntries);
        set => Volatile.Write(ref _tagQueueEntries, Checked(value, MinimumTagQueueEntries, TagQueueEntryCeiling));
    }

    /// <summary>Largest size a write may grow the tagging queue file to.</summary>
    internal long TagQueueFileBytes
    {
        get => Interlocked.Read(ref _tagQueueFileBytes);
        set => Interlocked.Exchange(ref _tagQueueFileBytes, Checked(value, MinimumTagQueueFileBytes, TagQueueFileCeilingBytes));
    }

    /// <summary>How many finished entries the tagging queue keeps; older finished entries drop out of the queue.</summary>
    internal int TagQueueHistory
    {
        get => Volatile.Read(ref _tagQueueHistory);
        set => Volatile.Write(ref _tagQueueHistory, Checked(value, MinimumTagQueueHistory, TagQueueHistoryCeiling));
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
