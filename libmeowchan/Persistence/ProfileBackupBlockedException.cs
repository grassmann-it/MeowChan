using System;

namespace MeowChan.Persistence;

internal enum ProfileBackupBlockReason
{
    /// <summary>The backup was written by a newer version; it is valid data of that version and stays where it is.</summary>
    NewerVersion,
    /// <summary>The backup could not be opened (access, lock); whether it is damaged is unknown, so it stays where it is.</summary>
    Unreadable
}

/// <summary>
/// A save would rotate the backup away, but the backup is not a damaged copy that can be set aside. The save is refused before
/// any file changes; the window shows its own localized text for <see cref="Reason"/> naming <see cref="FileName"/>.
/// </summary>
internal sealed class ProfileBackupBlockedException : InvalidOperationException
{
    /// <summary>The file name of the backup, never its path.</summary>
    public string FileName { get; }
    public ProfileBackupBlockReason Reason { get; }

    public ProfileBackupBlockedException(string backupPath, ProfileBackupBlockReason reason)
        : base(Describe(System.IO.Path.GetFileName(backupPath), reason))
    {
        FileName = System.IO.Path.GetFileName(backupPath);
        Reason = reason;
    }

    private static string Describe(string fileName, ProfileBackupBlockReason reason) => reason == ProfileBackupBlockReason.NewerVersion
        ? $"The backup {fileName} was written by a newer version. No files were changed; use that version or move {fileName} out of the profile folder."
        : $"The backup {fileName} could not be read. No files were changed; check access to {fileName} or move it out of the profile folder.";
}
