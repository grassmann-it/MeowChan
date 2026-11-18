using System;
using System.IO;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Storage;

namespace MeowChan.Desktop;

/// <summary>
/// MB1b (archive): what the archive's thumbnails tell the shared media badges (<see cref="MediaBadgeView"/>): the saved-media tiles
/// (<see cref="ArchiveMediaTileView"/>) and the thread list's rows and reader posts (<see cref="ArchiveAttachmentView"/>). Pure functions:
/// the state of a medium from its row, nothing drawn here. The archive knows the favourite, the kind (a still, a GIF, a video, from the saved
/// name alone, no file is read) and whether the file is gone; it does not record a video's length (<c>duration</c> stays open until a
/// source for it exists). The tag count, queue, rating and booru marks belong to other surfaces and are not set here.
/// </summary>
internal static class ArchiveMediaBadges
{
    // The video extensions of SavedMediaTypes.Classify, read without allocating (a test keeps both lists together): a recycled tile asks for
    // its state on every refresh, and the string work of Classify would allocate each time.
    private static readonly string[] VideoExtensions = [".webm", ".mp4", ".mov", ".mkv", ".avi"];

    /// <summary>Image, video or animated image (GIF) by the file name alone (nothing is allocated).</summary>
    internal static MediaBadgeKind KindOf(string? fileName)
    {
        ReadOnlySpan<char> extension = Path.GetExtension(fileName.AsSpan());
        if (MemoryExtensions.Equals(extension, ".gif", StringComparison.OrdinalIgnoreCase)) return MediaBadgeKind.Gif;
        foreach (string video in VideoExtensions)
            if (MemoryExtensions.Equals(extension, video, StringComparison.OrdinalIgnoreCase)) return MediaBadgeKind.Video;
        return MediaBadgeKind.Image;
    }

    /// <summary>A favourite only when favourites are available at all (a closed page or an unsaved attachment shows none).</summary>
    internal static bool IsFavorite(Func<bool>? available, Func<bool>? marked) => available?.Invoke() == true && marked?.Invoke() == true;

    /// <summary>The badges of a saved-media tile: favourite, kind, the length when known, and a file that is missing.</summary>
    internal static MediaBadgeState ForAsset(ArchiveAssetRow row, bool missing = false, TimeSpan? duration = null) =>
        new(Favorite: IsFavorite(row.FavoriteAvailable, row.IsFavorite), Kind: KindOf(row.Asset.OriginalFileName ?? row.Asset.Path), Duration: duration,
            Missing: missing || row.Manifest.Status == MediaManifestStatus.Missing);

    /// <summary>
    /// The badges of a thread-list row or a reader post's attachment. Neither carries a favourite badge: a compact list row has no file
    /// star (the thread's own star is on the right), and a reader post already shows the file's star, the toggle on its file line, so a
    /// second round star over the picture only duplicated it (Tom, 16.11.2026). The favourite badge stays on the media tiles, the Media
    /// page, the tagging tiles and the gallery filmstrip (<see cref="ForAsset"/> and their own views).
    /// </summary>
    internal static MediaBadgeState ForAttachment(ArchiveAttachmentRow row, bool missing = false, TimeSpan? duration = null) =>
        new(Kind: KindOf(row.Shown.OriginalFileName), Duration: duration, Missing: missing);

    /// <summary>A load that failed because the file is not (or no longer) there, as opposed to one that is merely not decodable.</summary>
    internal static bool IsMissingFile(Exception? error)
    {
        for (int depth = 0; error != null && depth < 8; depth++, error = error.InnerException)
            if (error is FileNotFoundException or DirectoryNotFoundException) return true;
        return false;
    }

    /// <summary>
    /// <paramref name="text"/> and the badge sentence <paramref name="description"/> as one name: the sentence goes behind the text,
    /// separated by <paramref name="separator"/>; either may be empty.
    /// </summary>
    internal static string Append(string? text, string description, string separator) =>
        string.IsNullOrEmpty(description) ? text ?? string.Empty : string.IsNullOrEmpty(text) ? description : text + separator + description;
}
