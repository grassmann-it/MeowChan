using System;
using MeowChan.Interfaces;

namespace MeowChan.Archive;

/// <summary>
/// Thread order. <see cref="Relevance"/> groups the matches of a search text by where the text was found
/// (<see cref="ArchiveSearchMatchTier"/>), newest capture first within each group; without a text it is
/// <see cref="LatestCapture"/>. Appended last, so persisted and UI indices of the older orders keep their meaning.
/// </summary>
public enum ArchiveThreadSort { LatestCapture, OldestFirst, Title, Relevance }

/// <summary>
/// Where a search text has to occur. <see cref="Title"/> is the thread record (title, source, board and thread number);
/// the opening post is the thread's first retained post (by provider order, then post id), which is also the post the
/// list preview shows. Without a search text the scope has no effect.
/// </summary>
public enum ArchiveSearchScope { AllPosts, TitleAndOpeningPost, Title }

/// <summary>The best place a relevance-sorted match was found: in the title, in the opening post, or only in other posts.</summary>
public enum ArchiveSearchMatchTier { Title, OpeningPost, OtherPosts }
/// <summary>Literal case-insensitive substring search of identities, titles and inert retained post text.</summary>
public sealed record ArchiveThreadQuery(string Text = "", string? Source = null, string? Board = null,
    ThreadSourceState? SourceState = null, bool? CaptureEnabled = null, bool? HasPosts = null,
    ArchiveThreadSort Sort = ArchiveThreadSort.LatestCapture)
{
    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Text);
        if (Text.Contains('\0')) throw new ArgumentException("Search text cannot contain NUL.", nameof(Text));
        if (Text.Length > 512) throw new ArgumentException("Search text may contain at most 512 characters.", nameof(Text));
        if (Source != null && string.IsNullOrWhiteSpace(Source)) throw new ArgumentException("Source must be an identity or null.", nameof(Source));
        if (Board != null && string.IsNullOrWhiteSpace(Board)) throw new ArgumentException("Board must be an identity or null.", nameof(Board));
        if (SourceState.HasValue && !Enum.IsDefined(SourceState.Value)) throw new ArgumentOutOfRangeException(nameof(SourceState));
        if (!Enum.IsDefined(Sort)) throw new ArgumentOutOfRangeException(nameof(Sort));
    }
}
