using System;
using System.IO;
using MeowChan.Archive;
using MeowChan.Persistence;
using Serilog;

namespace MeowChan.Desktop;

internal static class TemporaryProfileDiscard
{
    internal const string Prefix = "MeowChan-folder-";
    private const string DiscardSuffix = ".discard-";

    internal static bool IsQuarantined(string path, ProfileDirectoryIdentity identity) =>
        path.EndsWith(DiscardSuffix + identity.DiscardToken, StringComparison.Ordinal);

    internal static string Quarantine(string path, ProfileDirectoryIdentity identity, Action? beforeMark = null)
    {
        ArchiveExportMetadataFile.RejectLinks(path);
        identity.EnsureMatches(path);
        using (ProfileLease.AcquireDiscardLock(path))
        {
            identity.EnsureMatches(path);
            beforeMark?.Invoke();
            string marker = MarkerPath(path);
            if (!File.Exists(marker))
                AtomicJsonFile.WriteAllText(marker, identity.DiscardToken, backupCurrentFile: false);
            RequireMarker(path, identity);
        }

        // Windows can refuse directory renames while a child handle is open. The
        // durable marker now fences out profile opens even after the lock is released.
        string quarantine = path + DiscardSuffix + identity.DiscardToken;
        identity.EnsureMatches(path);
        Directory.Move(path, quarantine);
        return quarantine;
    }

    internal static void RemoveQuarantine(string path, ProfileDirectoryIdentity identity)
    {
        ArchiveExportMetadataFile.RejectLinks(path);
        identity.EnsureMatches(path);
        string marker = MarkerPath(path);
        if (File.Exists(marker))
        {
            ArchiveExportMetadataFile.RejectLinks(marker);
            using FileStream claim = ProfileLease.AcquireDiscardMarker(marker);
            RequireIdentity(claim, identity);
            foreach (string entry in Directory.EnumerateFileSystemEntries(path))
            {
                if (entry == marker) continue;
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch (FileNotFoundException) { continue; }
                // Directory.Delete removes link entries without following them.
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    try { Directory.Delete(entry, recursive: true); }
                    catch (DirectoryNotFoundException) { }
                }
                else File.Delete(entry);
            }
            // Retain the open fence through the last recursive deletion, including
            // if a retry has to finish what this process started.
            identity.EnsureMatches(path);
            File.Delete(marker);
        }
        Directory.Delete(path);
    }

    internal static void RetryPending(string tempParent)
    {
        try
        {
            foreach (string path in Directory.EnumerateDirectories(tempParent, Prefix + "*"))
            {
                try { RetryOne(path); }
                catch (DirectoryNotFoundException) { } // Another process finished the same discard.
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
                { Log.Warning(error, "A previously discarded folder profile could not be removed."); }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Log.Warning(error, "Pending folder profile discards could not be read."); }
    }

    private static void RetryOne(string path)
    {
        bool moved = Path.GetFileName(path).Contains(DiscardSuffix, StringComparison.Ordinal);
        if (!moved && !File.Exists(MarkerPath(path))) return;
        ArchiveExportMetadataFile.RejectLinks(path);
        if (!ProfileDirectoryIdentity.IsOwnedByCurrentUser(path)) return;
        ProfileDirectoryIdentity identity = ProfileDirectoryIdentity.Capture(path);
        if (!moved)
        {
            RequireMarker(path, identity);
            path = Quarantine(path, identity);
        }
        else if (!IsQuarantined(path, identity)) return;
        RemoveQuarantine(path, identity);
    }

    private static string MarkerPath(string path) => Path.Combine(path, ProfileLease.DiscardMarkerName);

    private static void RequireMarker(string path, ProfileDirectoryIdentity identity)
    {
        string marker = MarkerPath(path);
        ArchiveExportMetadataFile.RejectLinks(marker);
        using FileStream claim = ProfileLease.AcquireDiscardMarker(marker);
        RequireIdentity(claim, identity);
    }

    private static void RequireIdentity(FileStream claim, ProfileDirectoryIdentity identity)
    {
        using StreamReader reader = new(claim, System.Text.Encoding.UTF8, leaveOpen: true);
        if (claim.Length != identity.DiscardToken.Length || reader.ReadToEnd() != identity.DiscardToken)
            throw new InvalidDataException("The discard marker does not match the temporary profile.");
    }
}
