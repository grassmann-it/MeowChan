using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace MeowChan.FolderSources;

/// <summary>
/// How a file of a read-only folder source (PLAN-POWER-TAGGEN §2.2, E3) is named inside the media library. A folder
/// source is no archived thread: like a manual import batch it only borrows the <see cref="ResourceId"/> shape
/// (source <c>meowchan-folder</c>, board and thread = the root id) so favorites, ratings, the media index, the gallery
/// and loose-file tags can key it. The identity is never written into the archive catalog, and nothing is ever written
/// into the user's folder: the shadow catalog lives in the profile.
/// <para>
/// Every item has a virtual media folder <c>&lt;profile&gt;/folder-sources/&lt;root&gt;/&lt;item&gt;</c> that never
/// exists on disk. Its manifest is answered from the shadow catalog (<see cref="FolderSourceManifests"/>), so every view
/// that opens manifests (thumbnails, gallery, export, revalidation) reads the user's file through the same verified path.
/// </para>
/// </summary>
internal static class FolderSourceIdentity
{
    internal const string Source = "meowchan-folder";
    /// <summary>The folder below the profile that names the virtual media folders (never created).</summary>
    internal const string VirtualFolderName = "folder-sources";
    /// <summary>Variant of every folder item: the user's file itself.</summary>
    internal const string Variant = "original";

    internal static ResourceId Thread(Guid root)
    {
        string id = Format(root);
        return new ResourceId(Source, id, id);
    }

    internal static string Format(Guid id) => id.ToString("N");

    /// <summary>Strict id syntax: 32 lowercase hex digits, never the empty GUID.</summary>
    internal static bool TryParseId(string? text, out Guid id)
    {
        id = Guid.Empty;
        return text is { Length: 32 } && text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
            && Guid.TryParseExact(text, "N", out id) && id != Guid.Empty;
    }

    internal static bool IsFolderThread(ResourceId? thread) => TryGetRootId(thread, out _);

    internal static bool TryGetRootId(ResourceId? thread, out Guid root)
    {
        root = Guid.Empty;
        return thread is { Source: Source } && thread.Board == thread.Thread && TryParseId(thread.Board, out root);
    }

    /// <summary>
    /// The asset id of an item in its virtual manifest: 64 lowercase hex digits derived from the item id only, so it is
    /// known before the bytes are hashed and never names a path.
    /// </summary>
    internal static string AssetId(Guid item) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("meowchan-folder-item/" + Format(item)))).ToLowerInvariant();

    /// <summary>The media identifier of an item.</summary>
    internal static string MediaIdentifier(Guid item) => Format(item);

    /// <summary>The requested (slot) file name of an item: its id plus the lowercase extension of the user's file.</summary>
    internal static string RequestedFileName(Guid item, string relativePath) =>
        Format(item) + Path.GetExtension(relativePath).ToLowerInvariant();

    /// <summary>The root of all virtual item folders of a profile.</summary>
    internal static string VirtualRoot(string profileDirectory) =>
        Path.Combine(Path.TrimEndingDirectorySeparator(Path.GetFullPath(profileDirectory)), VirtualFolderName);

    /// <summary>The virtual media folder of one item (never created on disk).</summary>
    internal static string VirtualDirectory(string profileDirectory, Guid root, Guid item) =>
        Path.Combine(VirtualRoot(profileDirectory), Format(root), Format(item));

    /// <summary>Parses a virtual item folder of <paramref name="virtualRoot"/>; false for every other path.</summary>
    internal static bool TryParseVirtualDirectory(string virtualRoot, string? directory, out Guid root, out Guid item)
    {
        root = item = Guid.Empty;
        if (string.IsNullOrEmpty(directory)) return false;
        string prefix = virtualRoot + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!directory.StartsWith(prefix, comparison)) return false;
        string rest = directory.Substring(prefix.Length);
        string[] parts = rest.Split(Path.DirectorySeparatorChar);
        return parts.Length == 2 && TryParseId(parts[0], out root) && TryParseId(parts[1], out item);
    }

    /// <summary>The relative path as stored: forward slashes, no leading or trailing separator, no "." or ".." segment.</summary>
    internal static bool TryNormalizeRelative(string relative, out string normalized)
    {
        normalized = relative.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0 || normalized.Length > FolderSourceCatalog.MaximumPathLength || normalized.Any(char.IsControl)) return false;
        foreach (string segment in normalized.Split('/'))
            if (segment.Length == 0 || segment == "." || segment == "..") return false;
        return true;
    }

    /// <summary>The absolute path of an item below its root folder.</summary>
    internal static string AbsolutePath(string rootPath, string relativePath) =>
        Path.Combine(rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
}
