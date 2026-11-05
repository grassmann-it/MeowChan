using System;
using System.Collections.Generic;

namespace MeowChan.Archive;

/// <summary>Literal original-name query; no trimming, normalization, path fallback or query language.</summary>
internal sealed record ArchiveFilenameSearch
{
    internal ArchiveFilenameSearch(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 512 || text.Contains('\0'))
            throw new ArgumentException("Filename search permits at most 512 UTF-16 code units and no NUL.", nameof(text));
        Text = text;
    }

    internal string Text { get; }
}

/// <summary>
/// Captured catalog metadata, potentially stale, never evidence of current or verified media bytes.
/// Deleted observations remain searchable. A null revision key is unknown, not reconstructed.
/// </summary>
internal sealed record ArchiveFilenameHit(ResourceId Thread, string Post, int Position,
    string? AttachmentRevisionKey, string Variant, string OriginalName,
    bool PostIsDeleted, bool AttachmentIsDeleted, long CatalogThreadRevision);

/// <summary>Count and items share one read snapshot; separate calls may observe different catalogs.</summary>
internal sealed record ArchiveFilenamePage(IReadOnlyList<ArchiveFilenameHit> Items, long TotalCount, int Offset);
