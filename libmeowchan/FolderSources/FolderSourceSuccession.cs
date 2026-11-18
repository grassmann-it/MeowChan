using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MeowChan.Tags;

namespace MeowChan.FolderSources;

/// <summary>
/// A file of a read-only folder source that changed on disk after it was tagged: the scan gave the changed file a new item (tags
/// belong to the exact, hashed version of a file, so they did not move) and the old item stayed behind as a missing version
/// that still carries the tags. Pure facts from the shadow catalog and the tag snapshot; nothing is read from the user's folder.
/// </summary>
/// <param name="Root">The folder source containing the file.</param>
/// <param name="Current">The file as it is now.</param>
/// <param name="Previous">The most recent earlier version of the same path that carries tags.</param>
/// <param name="PreviousTags">The tags of <paramref name="Previous"/>.</param>
/// <param name="MissingOnCurrent">Those of them <paramref name="Current"/> does not carry yet (never empty).</param>
internal sealed record FolderFileSuccession(FolderSourceRoot Root, FolderSourceItem Current, FolderSourceItem Previous,
    IReadOnlyList<TagId> PreviousTags, IReadOnlyList<TagId> MissingOnCurrent)
{
    /// <summary>False until the current version is hashed: copying then reads the file once first (a user action).</summary>
    internal bool CurrentHashed => Current.State == FolderSourceItemState.Hashed;
}

internal static class FolderSourceSuccessions
{
    /// <summary>
    /// The tag observation of a hashed folder item: the same locator (its virtual folder and slot) and asset record every view
    /// of that file builds, so it resolves to exactly the registration the tags were written under. Null for an unhashed item.
    /// </summary>
    internal static LocalMediaObservation? ObservationOf(string virtualRoot, FolderSourceRoot root, FolderSourceItem item)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(item);
        if (item.Sha256 == null) return null;
        string directory = Path.Combine(virtualRoot, FolderSourceIdentity.Format(root.Id), FolderSourceIdentity.Format(item.Id));
        return LocalMediaObservation.TryCreate(root.Thread, directory, item.RequestedFileName, item.AssetId, item.Length, item.Sha256,
            out LocalMediaObservation? observation) ? observation : null;
    }

    /// <summary>
    /// The earlier tagged version of an active folder file whose tags did not follow it, or null when there is none (no earlier
    /// hashed version at this path, none of them carries tags, or the current version already carries all of the tags of the
    /// latest tagged one). An item that is missing itself has no successor to hint at.
    /// </summary>
    internal static FolderFileSuccession? Find(FolderSourceCatalogSnapshot catalog, Guid itemId, PrivateTagStoreSnapshot tags)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(tags);
        if (catalog.Item(itemId) is not { State: not FolderSourceItemState.Missing } current || catalog.Root(current.RootId) is not { } root) return null;
        IReadOnlyList<FolderSourceItem> versions = catalog.PreviousVersions(current.RootId, current.RelativePath);
        if (versions.Count == 0) return null;
        HashSet<TagId> onCurrent = TagsOf(catalog.VirtualRoot, root, current, tags);
        foreach (FolderSourceItem previous in versions)
        {
            if (previous.Id == current.Id) continue;
            HashSet<TagId> carried = TagsOf(catalog.VirtualRoot, root, previous, tags);
            if (carried.Count == 0) continue;
            // The latest tagged version is the one the person remembers; an older one is not offered instead.
            TagId[] missing = carried.Where(tag => !onCurrent.Contains(tag)).OrderBy(tag => tag.Value).ToArray();
            return missing.Length == 0 ? null
                : new FolderFileSuccession(root, current, previous, carried.OrderBy(tag => tag.Value).ToArray(), missing);
        }
        return null;
    }

    private static HashSet<TagId> TagsOf(string virtualRoot, FolderSourceRoot root, FolderSourceItem item, PrivateTagStoreSnapshot tags)
    {
        if (ObservationOf(virtualRoot, root, item) is not { } observation) return new();
        return tags.ResolveLocal(observation) is { IsRegistered: true, Target: { } target }
            ? tags.TagsOf(target).Select(tag => tag.Id).ToHashSet() : new();
    }
}
