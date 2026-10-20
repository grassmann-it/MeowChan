using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MeowChan.Archive;

internal sealed record ArchiveExportUpdate(ArchiveExportInspection Existing, bool ConfirmPreserveBackup);
internal sealed record ArchiveExportPublication(string? BackupDirectory, string? Warning);
internal sealed record ArchiveExportJournal(string BackupName, Guid ExportId, int Generation, bool PreserveBackup,
    string? BackupFingerprint = null);

/// <summary>Cross-process lock for a destination. The stable lock file is intentionally retained.</summary>
internal sealed class ArchiveExportTargetLease : IDisposable
{
    private readonly FileStream _stream;
    private ArchiveExportTargetLease(FileStream stream) => _stream = stream;
    public static ArchiveExportTargetLease Acquire(string destination)
    {
        ArchiveExportMetadataFile.RejectLinks(Path.GetDirectoryName(destination)!);
        string lockPath = Path.Combine(Path.GetDirectoryName(destination)!, ".meowchan-export-lock-" + Key(destination));
        return new ArchiveExportTargetLease(new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
    }
    internal static string Key(string destination) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        OperatingSystem.IsWindows() ? destination.ToUpperInvariant() : destination)))[..24];
    public void Dispose() => _stream.Dispose();
}

internal static class ArchiveExportReplacement
{
    private static string JournalPath(string destination) => Path.Combine(Path.GetDirectoryName(destination)!,
        ".meowchan-export-journal-" + ArchiveExportTargetLease.Key(destination) + ".json");

    internal static void Recover(string destination)
    {
        string journalPath = JournalPath(destination);
        if (!File.Exists(journalPath)) return;
        FileInfo journalInfo = new(journalPath);
        if (journalInfo.LinkTarget != null || journalInfo.Length > 2048) throw new InvalidDataException("Export recovery journal is unsafe.");
        ArchiveExportJournal? journal;
        try { journal = JsonSerializer.Deserialize<ArchiveExportJournal>(File.ReadAllText(journalPath)); }
        catch (JsonException ex) { throw new InvalidDataException("Export recovery journal is invalid.", ex); }
        if (journal == null || journal.ExportId == Guid.Empty || journal.Generation < 1 ||
            !journal.BackupName.StartsWith(".meowchan-export-backup-", StringComparison.Ordinal) ||
            journal.BackupName.Length != ".meowchan-export-backup-".Length + 32 ||
            !Guid.TryParseExact(journal.BackupName[".meowchan-export-backup-".Length..], "N", out _))
            throw new InvalidDataException("Export recovery journal is invalid.");
        string backup = Path.Combine(Path.GetDirectoryName(destination)!, journal.BackupName);
        ArchiveExportMetadataFile.RejectLinks(backup);
        ArchiveExportMetadataFile.RejectLinks(destination);
        if (!Directory.Exists(destination) && Directory.Exists(backup))
            Directory.Move(backup, destination);
        if (!Directory.Exists(destination)) throw new IOException("Export recovery could not find either target or fallback.");
        ArchiveExportInspection current = ArchiveExportMetadataFile.InspectAsync(destination).GetAwaiter().GetResult();
        bool published = current.Metadata.ExportId == journal.ExportId && current.Metadata.Generation == journal.Generation;
        if (published)
        {
            if (Directory.Exists(backup) && !journal.PreserveBackup && journal.BackupFingerprint != null)
            {
                try
                {
                    ArchiveExportInspection fallback = ArchiveExportMetadataFile.InspectAsync(backup).GetAwaiter().GetResult();
                    if (fallback.ObservedFingerprint == journal.BackupFingerprint && !fallback.RequiresBackup)
                        Directory.Delete(backup, true);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                { /* Keep an uncertain fallback for manual review. */ }
            }
        }
        else if (Directory.Exists(backup))
            throw new IOException("Export recovery found two divergent folders; manual review is required.");
        File.Delete(journalPath);
    }

    internal static async Task<ArchiveExportPublication> PublishAsync(string stage, string destination, ArchiveExportUpdate update,
        ArchiveExportMetadata next, CancellationToken token)
    {
        ArchiveExportInspection current = await ArchiveExportMetadataFile.InspectAsync(destination,
            update.Existing.Metadata.Thread, token).ConfigureAwait(false);
        if (current.Metadata.ExportId != update.Existing.Metadata.ExportId ||
            current.Metadata.Generation != update.Existing.Metadata.Generation ||
            current.ObservedFingerprint != update.Existing.ObservedFingerprint ||
            !current.ModifiedPaths.SequenceEqual(update.Existing.ModifiedPaths) ||
            !current.ExtraPaths.SequenceEqual(update.Existing.ExtraPaths))
            throw new IOException("The selected export changed; inspect it again before updating.");
        bool reduction = current.Metadata.PostCount > next.PostCount || current.Metadata.Files.Any(old =>
            old.Path.StartsWith("media/", StringComparison.Ordinal) &&
            !next.Files.Any(file => file.Path == old.Path));
        bool preserve = current.RequiresBackup || reduction || current.Metadata.AdoptedLegacy;
        if (preserve && !update.ConfirmPreserveBackup)
            throw new IOException("The previous export contains changed, extra, or omitted content. Confirm keeping its backup before replacing.");
        string backupName = ".meowchan-export-backup-" + Guid.NewGuid().ToString("N");
        string backup = Path.Combine(Path.GetDirectoryName(destination)!, backupName);
        string journalPath = JournalPath(destination);
        string pending = journalPath + ".pending-" + Guid.NewGuid().ToString("N");
        token.ThrowIfCancellationRequested();
        await using (FileStream stream = new(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, new ArchiveExportJournal(backupName, next.ExportId,
                next.Generation, preserve, current.ObservedFingerprint), cancellationToken: token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
            stream.Flush(true);
        }
        File.Move(pending, journalPath);
        // Cancellation is deliberately ignored after the journal is published.
        try
        {
            Directory.Move(destination, backup);
            ArchiveExportFaults.AfterOldRename?.Invoke();
            Directory.Move(stage, destination);
            ArchiveExportFaults.AfterNewRename?.Invoke();
            ArchiveExportInspection published = await ArchiveExportMetadataFile.InspectAsync(destination,
                next.Thread).ConfigureAwait(false);
            if (published.Metadata.ExportId != next.ExportId || published.Metadata.Generation != next.Generation || published.RequiresBackup)
                throw new IOException("The published export failed verification.");
        }
        catch (Exception failure)
        {
            try
            {
                if (Directory.Exists(destination) && Directory.Exists(backup))
                    Directory.Move(destination, destination + ".failed-" + Guid.NewGuid().ToString("N"));
                if (!Directory.Exists(destination) && Directory.Exists(backup)) Directory.Move(backup, destination);
                if (Directory.Exists(destination)) File.Delete(journalPath);
            }
            catch (Exception recoveryFailure)
            {
                throw new IOException($"The export replacement failed and could not restore the target. " +
                    $"The fallback remains at '{backup}' (target '{destination}').", new AggregateException(failure, recoveryFailure));
            }
            if (!Directory.Exists(destination))
                throw new IOException($"The export replacement failed; retained fallback: '{backup}'.", failure);
            throw;
        }
        // The verified new target is authoritative from here. Cleanup cannot roll it back.
        string? warning = null;
        if (!preserve)
        {
            try
            {
                ArchiveExportInspection fallback = await ArchiveExportMetadataFile.InspectAsync(backup).ConfigureAwait(false);
                if (fallback.ObservedFingerprint == current.ObservedFingerprint && !fallback.RequiresBackup)
                {
                    ArchiveExportFaults.BeforeBackupCleanup?.Invoke();
                    Directory.Delete(backup, true);
                }
                else
                    warning = "The previous export changed during cleanup; its remaining backup was kept.";
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            { warning = "The new export is published, but its previous backup could not be completely removed: " + ex.Message; }
        }
        try { File.Delete(journalPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { warning = "The new export is published, but its recovery journal could not be removed: " + ex.Message; }
        return new ArchiveExportPublication(Directory.Exists(backup) ? backup : null, warning);
    }
}

internal static class ArchiveExportFaults
{
    internal static Action? AfterOldRename;
    internal static Action? AfterNewRename;
    internal static Action? BeforeBackupCleanup;
}
