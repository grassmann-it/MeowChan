using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.FolderSources;
using MeowChan.Tags;

namespace MeowChan.Desktop;

internal enum FolderTagCopyOutcome
{
    /// <summary>The tags of the previous version were added to this version.</summary>
    Copied,
    /// <summary>This version already carries all of them (or there is no earlier tagged version any more).</summary>
    NothingToCopy,
    /// <summary>Nothing was changed: the file changed again, cannot be read, or the tags could not be written.</summary>
    Unavailable
}

/// <summary>What a copy added, so that exactly that can be taken away again.</summary>
internal sealed record FolderTagCopy(MediaTagSubject Subject, IReadOnlyList<TagId> Added);

internal sealed record FolderTagCopyResult(FolderTagCopyOutcome Outcome, FolderTagCopy? Copy);

/// <summary>
/// Tags of a folder file that changed on disk (PLAN-POWER-TAGGEN §2.2: "Tags folgen nicht automatisch, Hinweis"). A tag belongs to
/// the exact hashed version of a file, so a changed file is a new item that starts without the tags of the old one. This finds
/// the earlier tagged version and, only when the person asks, copies its tags to the new version. Nothing here runs by itself and
/// nothing is removed from the previous version.
/// </summary>
internal sealed partial class DesktopSessionHost
{
    /// <summary>
    /// The earlier tagged version of a folder file whose tags did not follow it, or null. Reads the catalog and the tag snapshot
    /// (a catalog read may wait for the profile): call it off the UI thread.
    /// </summary>
    internal FolderFileSuccession? FindFolderFileSuccession(Guid item)
    {
        if (_disposed) return null;
        try
        {
            if (ReadFolderSources() is not { } catalog) return null;
            PrivateTagLoadSnapshot tags = GetPublishedPrivateTagSnapshot();
            if (tags.Snapshot == null) tags = GetPrivateTagSnapshot();
            return tags.Snapshot == null ? null : FolderSourceSuccessions.Find(catalog, item, tags.Snapshot);
        }
        catch (Exception error) when (FolderSourceFailure(error)) { return null; }
    }

    /// <summary>
    /// Adds the tags of the previous version to this version in one write (all or none). The current version is hashed first (a user
    /// action, any size) because a tag names the exact bytes; everything is looked up again after that, so a file that changed
    /// meanwhile is refused instead of tagged wrongly. Never throws for the file's or the store's own state.
    /// </summary>
    internal async Task<FolderTagCopyResult> CopyFolderFileTagsAsync(Guid item, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            FolderSourceHashResult hashed = await HashFolderItemAsync(item, FolderSourceHashPriority.UserAction, token).ConfigureAwait(false);
            if (hashed.Outcome is not (FolderSourceHashOutcome.Hashed or FolderSourceHashOutcome.AlreadyHashed)) return new(FolderTagCopyOutcome.Unavailable, null);
            (FolderFileSuccession? succession, MediaTagSubject? subject) = await Task.Run<(FolderFileSuccession?, MediaTagSubject?)>(() =>
            {
                FolderSourceCatalogSnapshot catalog = FolderSourceCatalog.Read();
                PrivateTagLoadSnapshot tags = GetPrivateTagSnapshot();
                if (tags.Snapshot == null || FolderSourceSuccessions.Find(catalog, item, tags.Snapshot) is not { CurrentHashed: true } found) return (null, null);
                LocalMediaObservation? observation = FolderSourceSuccessions.ObservationOf(catalog.VirtualRoot, found.Root, found.Current);
                return (found, observation == null ? null : (MediaTagSubject)new MediaTagSubject.Local(observation, null));
            }, token).ConfigureAwait(false);
            if (succession == null) return new(FolderTagCopyOutcome.NothingToCopy, null);
            if (subject == null) return new(FolderTagCopyOutcome.Unavailable, null);
            await SetMediaSubjectTagSetAsync(succession.MissingOnCurrent, subject, assigned: true, token: token).ConfigureAwait(false);
            _logger.Information("Tags of a previous folder file version were copied ({Tags} tags).", succession.MissingOnCurrent.Count);
            return new(FolderTagCopyOutcome.Copied, new FolderTagCopy(subject, succession.MissingOnCurrent));
        }
        catch (Exception error) when (error is FolderSourceException or PrivateTagStoreException or IOException or InvalidDataException
            or UnauthorizedAccessException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            _logger.Warning("Tags of a previous folder file version could not be copied: {Error}", Code(error));
            return new(FolderTagCopyOutcome.Unavailable, null);
        }
    }

    /// <summary>Takes away exactly the tags a copy added (never the ones the file had before). False when that failed.</summary>
    internal async Task<bool> UndoFolderFileTagCopyAsync(FolderTagCopy copy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(copy);
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            await SetMediaSubjectTagSetAsync(copy.Added, copy.Subject, assigned: false, token: token).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (error is PrivateTagStoreException or IOException or InvalidDataException or UnauthorizedAccessException
            or InvalidOperationException)
        {
            _logger.Warning("Copied tags of a folder file version could not be removed: {Error}", Code(error));
            return false;
        }
    }
}
