using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Desktop.Localization;
using MeowChan.FolderSources;
using MeowChan.Persistence;
using Serilog;

namespace MeowChan.Desktop;

/// <summary>Owns the isolated profile of a positional folder start until the desktop lifetime ends.</summary>
internal sealed class StartupFolderSession : IDisposable
{
    private (string Path, ProfileDirectoryIdentity Identity)? _temporaryProfile;

    internal string Source { get; }
    internal string? SelectedProfile { get; set; }
    internal ShutdownWatchdog? WindowShutdownWatchdog { get; set; }

    internal StartupFolderSession(string source) => Source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));

    internal static string? ReadPositionalPath(string[] args)
    {
        if (args is not [string folder] || string.IsNullOrWhiteSpace(folder) || folder[0] == '-' || folder.Any(char.IsControl))
            return null;
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
    }

    internal StartupMediaFolderArgument Registration => new(Source, StartupMediaFolderProblem.None, OpenMediaView: true);

    internal string? SourceProblem(IEnumerable<string> protectedFolders)
    {
        if (!IsPlainPath(Source) || !Directory.Exists(Source) || Source.Length > FolderSourceCatalog.MaximumPathLength)
            return "profile.error.mediaUnavailable";
        if (protectedFolders.Any(folder => FolderSourceValidation.Overlaps(Source, folder)))
            return "profile.error.mediaUnsafe";
        try
        {
            using IEnumerator<string> probe = Directory.EnumerateFileSystemEntries(Source).GetEnumerator();
            probe.MoveNext();
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return "profile.error.mediaUnavailable";
        }
    }

    internal string? ProfileProblem(string profile, IEnumerable<string> protectedFolders)
    {
        if (SourceProblem(protectedFolders.Append(profile)) is { } problem) return problem;
        if (!IsPlainPath(profile) || ProfileOpenPolicy.Classify(profile) != ProfileFolderKind.Existing)
            return "profile.status.notProfile";
        if (ProfileLease.IsInUse(profile)) return "profile.status.inUse";
        Config config = Config.LoadConfig(profile);
        if (!config.AllowsOrdinaryWrites) throw SettingsRecoveryRequiredException.For(config, profile);
        if (!IsPlainPath(config.DownloadPath) || FolderSourceValidation.Overlaps(Source, config.DownloadPath))
            return "profile.error.mediaUnsafe";
        return null;
    }

    internal string PrepareTemporaryProfile(string language, IEnumerable<string> protectedFolders)
    {
        if (_temporaryProfile is not null) throw new InvalidOperationException("A temporary profile already exists for this start.");
        string tempParent = TempParent();
        if (protectedFolders.Append(Source).Any(folder => FolderSourceValidation.SameOrBelow(tempParent, folder)))
            throw new IOException("The temporary directory is inside a protected folder.");

        // The OS creates a unique directory with private permissions on Unix. Use its physical
        // path so the profile can also open under macOS's /var -> /private/var alias.
        DirectoryInfo created = Directory.CreateTempSubdirectory(TemporaryProfileDiscard.Prefix);
        string directory = Path.Combine(tempParent, created.Name);
        ArchiveExportMetadataFile.RejectLinks(directory);
        _temporaryProfile = (directory, ProfileDirectoryIdentity.Capture(directory));
        using ProfileLease lease = ProfileLease.Acquire(directory);
        Config config = Config.LoadConfig(lease.ProfileDirectory, lease);
        config.DownloadPath = Path.Combine(lease.ProfileDirectory, "downloads");
        config.FirstRunSetupVersion = 1;
        config.AutomaticUpdateChecks = false;
        config.Language = language;
        config.SaveConfig(lease.ProfileDirectory);
        return lease.ProfileDirectory;
    }

    internal static string TempParent() => ResolveTempPath(Path.GetTempPath());

    private static string ResolveTempPath(string path)
    {
        DirectoryInfo directory = new(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
        if (directory.Parent is not { } parent) return directory.FullName;
        DirectoryInfo child = new(Path.Combine(ResolveTempPath(parent.FullName), directory.Name));
        return child.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? child.FullName;
    }

    internal bool RemoveTemporaryProfile(Action? beforeMark = null)
    {
        if (_temporaryProfile is not { } owned) return true;
        try
        {
            ArchiveExportMetadataFile.RejectLinks(owned.Path);
            if (Directory.Exists(owned.Path))
            {
                if (!TemporaryProfileDiscard.IsQuarantined(owned.Path, owned.Identity))
                {
                    owned.Path = TemporaryProfileDiscard.Quarantine(owned.Path, owned.Identity, beforeMark);
                    _temporaryProfile = owned;
                }
                TemporaryProfileDiscard.RemoveQuarantine(owned.Path, owned.Identity);
            }
            else if (File.Exists(owned.Path))
            {
                throw new IOException("The temporary profile directory has been replaced.");
            }
            _temporaryProfile = null;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Warning(error, "The temporary folder profile could not be removed.");
            return false;
        }
    }

    public void Dispose() => RemoveTemporaryProfile();

    internal static bool IsPlainPath(string path)
    {
        try
        {
            ArchiveExportMetadataFile.RejectLinks(path);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }
}

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
        return Task.Run(() => RegisterCoreAsync(session, argument, otherProfileFolders, token), token);
    }

    private static async Task<StartupMediaFolderResult> RegisterCoreAsync(DesktopSessionHost session, StartupMediaFolderArgument argument,
        IReadOnlyList<string> otherProfileFolders, CancellationToken token)
    {
        try
        {
            string folder = argument.Path ?? string.Empty;
            if (argument.OpenMediaView && !StartupFolderSession.IsPlainPath(folder))
                return Refused("profile.error.mediaUnavailable");
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
