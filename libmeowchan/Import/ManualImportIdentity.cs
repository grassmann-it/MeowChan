using System;
using System.IO;
using System.Linq;
using MeowChan.Archive;
using MeowChan.Storage;

namespace MeowChan.Import;

// Contract: docs/architecture/gesamtprogramm-2026-10-31/contracts/manual-import-v1.md
/// <summary>
/// How a manually imported file is named inside the media library. An import batch is no archived chan thread: it
/// only borrows the <see cref="ResourceId"/> shape (source, board, thread) so favorites, ratings, the media index, the
/// gallery and loose-file tags can key it, exactly like downloaded Danbooru posts do. The identity is never written
/// into the archive catalog. Files live in <c>&lt;download folder&gt;/meowchan-import/&lt;batch&gt;/</c> under names
/// made from their item id, never from the original name or path.
/// </summary>
internal static class ManualImportIdentity
{
    internal const string Source = "meowchan-import";
    internal const string Board = "import";
    /// <summary>The folder below the download folder that holds every import batch.</summary>
    internal const string FolderName = "meowchan-import";
    /// <summary>Variant of every imported file: imports have no derived representations.</summary>
    internal const string Variant = "original";

    internal static ResourceId Thread(Guid batch) => new(Source, Board, Format(batch));

    internal static string Format(Guid id) => id.ToString("N");

    /// <summary>Strict id syntax: 32 lowercase hex digits, never the empty GUID.</summary>
    internal static bool TryParseId(string? text, out Guid id)
    {
        id = Guid.Empty;
        return text is { Length: 32 } && text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
            && Guid.TryParseExact(text, "N", out id) && id != Guid.Empty;
    }

    internal static bool IsImportThread(ResourceId? thread) => TryGetBatchId(thread, out _);

    internal static bool TryGetBatchId(ResourceId? thread, out Guid batch)
    {
        batch = Guid.Empty;
        return thread is { Source: Source, Board: Board } && TryParseId(thread.Thread, out batch);
    }

    /// <summary>The managed folder of one batch below <paramref name="downloadRoot"/>.</summary>
    internal static string BatchDirectory(string downloadRoot, Guid batch) =>
        StorageLayout.GetBoardDirectory(downloadRoot, FolderName, Format(batch));

    /// <summary>The folder of all batches below <paramref name="downloadRoot"/>.</summary>
    internal static string ImportRoot(string downloadRoot) => Path.Combine(Path.GetFullPath(downloadRoot), FolderName);

    /// <summary>The manifest's requested file name of one item: its id plus its lowercase extension.</summary>
    internal static string RequestedFileName(Guid item, string extension) => Format(item) + extension;

    /// <summary>The manifest media identifier of one item.</summary>
    internal static string MediaIdentifier(Guid item) => Format(item);
}
