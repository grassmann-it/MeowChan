using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Desktop.Localization;
using MeowChan.FolderSources;

namespace MeowChan.Desktop;

internal enum StartupMediaFolderStatus
{
    /// <summary>The folder is now a read-only folder source (its first look-through has started).</summary>
    Added,
    /// <summary>The folder already is a source of this profile; nothing changed and nothing needs saying.</summary>
    AlreadyAdded,
    /// <summary>The folder was not added; <see cref="StartupMediaFolderResult.MessageKey"/> explains why. The start went on.</summary>
    Refused
}

/// <summary>The outcome of the media folder start argument. <see cref="MessageKey"/> is the text to show (null for an unchanged state);
/// <see cref="Name"/> is the name of the folder source that was added.</summary>
internal sealed record StartupMediaFolderResult(StartupMediaFolderStatus Status, string? MessageKey, string? Name = null, string? ReasonKey = null);

/// <summary>
/// Registers the media folder that the "New profile" dialog handed to this process (<c>--media-folder</c>) as a read-only folder
/// source on its first start. The folder is validated again here, by the only process that knows its own download folder and
/// folder sources, with the same rules as "Add folder…" plus the locations that must stay out of a user's folder. It is never
/// created, written or changed; a problem is explained and never blocks the start.
/// </summary>
internal static class StartupMediaFolderRegistration
{
    internal static Task<StartupMediaFolderResult> RegisterAsync(DesktopSessionHost session, StartupMediaFolderArgument argument,
        IReadOnlyList<string> otherProfileFolders, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(argument);
        if (argument.Problem == StartupMediaFolderProblem.Malformed) return Task.FromResult(Refused("profile.startMedia.badArgument"));
        if (argument.Problem == StartupMediaFolderProblem.NeedsProfile) return Task.FromResult(Refused("profile.startMedia.needsProfile"));
        // Every catalog and file system check runs on a worker: the folder may be on a slow or sleeping disk (W3-12).
        return Task.Run(() => RegisterCoreAsync(session, argument.Path ?? string.Empty, otherProfileFolders, token), token);
    }

    private static async Task<StartupMediaFolderResult> RegisterCoreAsync(DesktopSessionHost session, string folder,
        IReadOnlyList<string> otherProfileFolders, CancellationToken token)
    {
        try
        {
            // The roots alone: the per-source counts of ListFolderSources are not needed to see whether the folder is already added.
            IReadOnlyList<FolderSourceRoot> roots = session.ReadFolderSourceRoots();
            // Already added (for example a restart that still carries the argument): nothing to do, nothing to say.
            if (roots.Any(root => string.Equals(root.Path, folder, Comparison))) return new(StartupMediaFolderStatus.AlreadyAdded, null);
            FolderSourceError error = session.CheckFolderSource(folder);
            if (error == FolderSourceError.None)
                error = StartupMediaFolderPolicy.Check(folder, session.ProfileDirectory, Path.GetFullPath(session.Config.DownloadPath), otherProfileFolders, roots);
            if (error != FolderSourceError.None) return Refused(ReasonKey(error));
            string name = NameOf(folder);
            FolderSourceRoot root = await session.AddFolderSourceAsync(folder, name, includeSubfolders: true, token).ConfigureAwait(false);
            return new(StartupMediaFolderStatus.Added, "profile.startMedia.added", root.Name);
        }
        catch (FolderSourceException error) { return Refused(ReasonKey(error.Error)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException
            or Microsoft.Data.Sqlite.SqliteException)
        {
            FolderSourceErrorText.Log(error, "startup media folder");
            return Refused(FolderSourceErrorText.Key(error));
        }
    }

    private static StringComparison Comparison => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StartupMediaFolderResult Refused(string reasonKey) => new(StartupMediaFolderStatus.Refused, "profile.startMedia.refused", null, reasonKey);

    /// <summary>The sentence that explains why a folder was not added. Never names a path.</summary>
    internal static string ReasonKey(FolderSourceError error) => error switch
    {
        FolderSourceError.FolderUnavailable => "profile.error.mediaUnavailable",
        FolderSourceError.FolderIsProfile or FolderSourceError.FolderIsDownloads => "profile.error.mediaUnsafe",
        _ => FolderSourceDialog.ErrorKey(error)
    };

    /// <summary>The folder's own name, or a generic one for a drive root; never longer than the catalog allows.</summary>
    internal static string NameOf(string folder)
    {
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)).Trim();
        if (name.Length == 0 || name.Any(char.IsControl)) name = Texts.T("folderSource.section.title");
        return name.Length > FolderSourceCatalog.MaximumNameLength ? name[..FolderSourceCatalog.MaximumNameLength] : name;
    }

    /// <summary>The sentence to show for a result: the reason (when refused) in the message, the folder name for an added one.</summary>
    internal static string Message(StartupMediaFolderResult result) => result.MessageKey == null ? string.Empty
        : result.Status == StartupMediaFolderStatus.Added ? Texts.T(result.MessageKey, result.Name ?? string.Empty)
        : Texts.T(result.MessageKey, result.ReasonKey == null ? string.Empty : Texts.T(result.ReasonKey));
}
