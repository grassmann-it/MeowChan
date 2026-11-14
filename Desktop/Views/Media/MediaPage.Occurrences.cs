using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using System.IO;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan;

// Details of a tile of equal bytes: all occurrences with their own tags. Grouping is a query-time step;
// tags, favorites and ratings stay with each occurrence and are read per occurrence here.
public sealed partial class MediaPage
{
    private MediaOccurrencesWindow? _occurrences;

    internal Window? OccurrencesWindow => _occurrences;

    /// <summary>Opens (or replaces) the list of every occurrence of the tile's bytes.</summary>
    internal void ShowOccurrences(MediaLibraryTile tile)
    {
        ArgumentNullException.ThrowIfNull(tile);
        if (_lifetime.IsCancellationRequested) return;
        _occurrences?.Close();
        // One catalog read per window; each provider occurrence lists the findings that brought it here.
        BooruCatalogSnapshot? catalog = tile.Occurrences.Any(entry => entry.BooruPostId != null) ? SafeReadBooruCatalog() : null;
        MediaOccurrencesWindow window = new(tile, OccurrenceTags, entry => entry.PostId != null && ShowInThread != null,
            entry => _ = ShowOccurrenceInThreadAsync(entry), entry => ProviderFindings(catalog, entry), FolderVersionNoticeOf);
        _occurrences = window;
        window.Closed += (_, _) => { if (ReferenceEquals(_occurrences, window)) _occurrences = null; };
        if (_owner != null) window.Show(_owner); else window.Show();
    }

    private BooruCatalogSnapshot? SafeReadBooruCatalog()
    {
        try { return ReadBooruCatalog?.Invoke(); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ObjectDisposedException) { return null; }
    }

    // "Findings: Found by a search; Found by a watcher ×2". Several claims on one saved file are listed, never merged away.
    private static string? ProviderFindings(BooruCatalogSnapshot? catalog, MediaLibraryEntry entry)
    {
        if (catalog == null || entry.BooruPostId == null || BooruMediaProjection.Find(catalog, entry.BooruPostId) is not { } details) return null;
        string[] kinds = details.Findings.GroupBy(finding => finding.Kind).OrderBy(group => group.Key)
            .Select(group => BooruMessages.Finding(group.Key) + (group.Count() > 1 ? " ×" + group.Count() : string.Empty)).ToArray();
        return kinds.Length == 0 ? null : Texts.T("booru.media.occurrence.findings", string.Join("; ", kinds));
    }

    private async Task ShowOccurrenceInThreadAsync(MediaLibraryEntry entry)
    {
        if (entry.PostId == null || ShowInThread == null) return;
        await ShowInThread(entry);
        _occurrences?.Close(); // The archive is now in front; the list would only cover it.
    }

    // The occurrence's own media tags and the tags of its thread, as the filter sees them.
    private string OccurrenceTags(MediaLibraryEntry entry)
    {
        try
        {
            string[] names = _userData.TagsOf(entry).Select(TagLabel).Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
            return names.Length == 0 ? Texts.T("media.occurrence.noTags") : Texts.T("media.occurrence.tags", string.Join(", ", names));
        }
        catch (MediaLibraryFilterException) { return Texts.T("media.occurrence.tagsUnavailable"); }
    }
}
