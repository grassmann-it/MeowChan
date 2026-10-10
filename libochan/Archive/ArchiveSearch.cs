using System;
using oChan.Interfaces;

namespace oChan.Archive;

public enum ArchiveThreadSort { LatestCapture, OldestFirst, Title }
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
