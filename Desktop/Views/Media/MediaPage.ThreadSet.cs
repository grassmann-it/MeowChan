using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan;

// The thread set of the Media page (MediaFilterField.Threads): the media of exactly the threads another page handed over, the results of
// an Archive search or the threads picked in a list. It is runtime state in the term set, so it shows as a chip ("12 threads from
// Archive search"), can be removed with the chip's mark, narrows together with every other filter and the in: scope, and has no query text.
public sealed partial class MediaPage
{
    /// <summary>
    /// How many thread titles the chip's tip lists before it says "+N more". A presentation limit of the tip only: the filter itself holds every
    /// thread of the set, however many (it is a set lookup), so this decides how much text a tip shows, not what is filtered.
    /// </summary>
    internal const int ThreadSetTipTitles = 10;

    // The title of every thread of the index, built once per snapshot when a tip first asks for it.
    private MediaLibrarySnapshot? _threadTitlesFor;
    private Dictionary<ResourceId, string>? _threadTitles;

    /// <summary>
    /// "Open in Media" / "Show in Media" of several threads: the thread set becomes the only filter (a clean view of these threads' media),
    /// like <see cref="ShowOnlyThread"/> does for one thread. An empty set is allowed and shows nothing, which the page then explains like any
    /// filter without a match.
    /// </summary>
    internal void ShowOnlyThreads(MediaThreadSet threads)
    {
        ArgumentNullException.ThrowIfNull(threads);
        ResetFilters();
        EditTerms(terms => MediaFilterTermSet.With(terms, MediaFilterTerm.InThreads(threads)));
    }

    /// <summary>The chip text of a thread set: "12 threads from Archive search" or "3 selected threads" (the plural forms of the language).</summary>
    internal static string ThreadSetChipText(MediaThreadSet threads) => threads.Origin == MediaThreadSetOrigin.ArchiveSearch
        ? Texts.Count("media.filter.threadsChip.archive", threads.Count) : Texts.Count("media.filter.threadsChip.selection", threads.Count);

    /// <summary>
    /// The tip of the chip: the titles of the first <see cref="ThreadSetTipTitles"/> threads of the set (the number of a thread the index has
    /// no title for), then "+N more" for the rest. Null for an empty set.
    /// </summary>
    internal string? ThreadSetTip(MediaThreadSet threads)
    {
        if (threads.Count == 0) return null;
        MediaLibrarySnapshot? snapshot = _snapshot;
        if (!ReferenceEquals(_threadTitlesFor, snapshot) || _threadTitles == null)
        {
            Dictionary<ResourceId, string> titles = new();
            foreach (MediaLibraryEntry entry in snapshot?.Entries ?? Array.Empty<MediaLibraryEntry>())
                if (!string.IsNullOrWhiteSpace(entry.ThreadTitle)) titles.TryAdd(entry.Thread, entry.ThreadTitle);
            _threadTitles = titles;
            _threadTitlesFor = snapshot;
        }
        string[] lines = [.. threads.Ordered.Take(ThreadSetTipTitles)
            .Select(thread => _threadTitles.TryGetValue(thread, out string? title) ? title : "#" + thread.Thread)];
        int more = threads.Count - lines.Length;
        return string.Join('\n', more > 0 ? lines.Append(Texts.T("media.filter.threadsMore", more)) : lines);
    }
}
