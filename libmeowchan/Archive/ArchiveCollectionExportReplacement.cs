using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MeowChan.Archive;

internal sealed record ArchiveCollectionExportUpdate(ArchiveCollectionExportInspection Existing, bool ConfirmPreserveBackup);
internal sealed record ArchiveCollectionExportJournal(string BackupName, Guid ExportId, int Generation, bool PreserveBackup,
    string? BackupFingerprint = null);

internal static class ArchiveCollectionExportReplacement
{
    private static string JournalPath(string destination) => Path.Combine(Path.GetDirectoryName(destination)!,
        ".meowchan-collection-export-journal-" + ArchiveExportTargetLease.Key(destination) + ".json");

    internal static void Recover(string destination)
    {
        string journalPath = JournalPath(destination);
        if (!File.Exists(journalPath)) return;
        FileInfo journalInfo = new(journalPath);
        if (journalInfo.LinkTarget != null || journalInfo.Length > 2048) throw new InvalidDataException("Export recovery journal is unsafe.");
        ArchiveCollectionExportJournal? journal;
        try { journal = JsonSerializer.Deserialize<ArchiveCollectionExportJournal>(File.ReadAllText(journalPath)); }
        catch (JsonException ex) { throw new InvalidDataException("Export recovery journal is invalid.", ex); }
        if (journal == null || journal.ExportId == Guid.Empty || journal.Generation < 1 ||
            journal.BackupName == null || !journal.BackupName.StartsWith(".meowchan-collection-export-backup-", StringComparison.Ordinal) ||
            journal.BackupName.Length != ".meowchan-collection-export-backup-".Length + 32 ||
            !Guid.TryParseExact(journal.BackupName[".meowchan-collection-export-backup-".Length..], "N", out _))
            throw new InvalidDataException("Export recovery journal is invalid.");
        string backup = Path.Combine(Path.GetDirectoryName(destination)!, journal.BackupName);
        ArchiveCollectionExportMetadataFile.RejectLinks(backup);
        ArchiveCollectionExportMetadataFile.RejectLinks(destination);
        if (!Directory.Exists(destination) && Directory.Exists(backup))
            Directory.Move(backup, destination);
        if (!Directory.Exists(destination)) throw new IOException("Export recovery could not find either target or fallback.");
        ArchiveCollectionExportInspection current = ArchiveCollectionExportMetadataFile.InspectAsync(destination).GetAwaiter().GetResult();
        bool published = current.Metadata.ExportId == journal.ExportId && current.Metadata.Generation == journal.Generation;
        if (published)
        {
            if (current.RequiresBackup)
                throw new IOException($"The published export has changed or failed verification. Recovery requires manual review; " +
                    $"the target '{destination}', any fallback at '{backup}', and recovery journal have been retained.");
            if (Directory.Exists(backup) && !journal.PreserveBackup && journal.BackupFingerprint != null)
            {
                try
                {
                    ArchiveCollectionExportInspection fallback = ArchiveCollectionExportMetadataFile.InspectAsync(backup).GetAwaiter().GetResult();
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

    internal static async Task<ArchiveExportPublication> PublishAsync(string stage, string destination, ArchiveCollectionExportUpdate update,
        ArchiveCollectionExportMetadata next, CancellationToken token)
    {
        ArchiveCollectionExportInspection current = await ArchiveCollectionExportMetadataFile.InspectAsync(destination,
            update.Existing.Metadata.Members.Select(member => member.Thread).ToArray(), token).ConfigureAwait(false);
        if (current.Metadata.ExportId != update.Existing.Metadata.ExportId ||
            current.Metadata.Generation != update.Existing.Metadata.Generation ||
            current.ObservedFingerprint != update.Existing.ObservedFingerprint ||
            !current.ModifiedPaths.SequenceEqual(update.Existing.ModifiedPaths) ||
            !current.ExtraPaths.SequenceEqual(update.Existing.ExtraPaths))
            throw new IOException("The selected export changed; inspect it again before updating.");
        bool reduction = current.Metadata.Members.Any(old => !next.Members.Any(member => member.Thread == old.Thread && member.PostCount >= old.PostCount)) || current.Metadata.Files.Any(old =>
            old.Path.StartsWith("media/", StringComparison.Ordinal) &&
            !next.Files.Any(file => file.Path == old.Path));
        bool preserve = current.RequiresBackup || reduction;
        if (preserve && !update.ConfirmPreserveBackup)
            throw new IOException("The previous export contains changed, extra, or omitted content. Confirm keeping its backup before replacing.");
        string backupName = ".meowchan-collection-export-backup-" + Guid.NewGuid().ToString("N");
        string backup = Path.Combine(Path.GetDirectoryName(destination)!, backupName);
        string journalPath = JournalPath(destination);
        string pending = journalPath + ".pending-" + Guid.NewGuid().ToString("N");
        token.ThrowIfCancellationRequested();
        try
        {
            await using (FileStream stream = new(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, new ArchiveCollectionExportJournal(backupName, next.ExportId,
                    next.Generation, preserve, current.ObservedFingerprint), cancellationToken: token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                stream.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(pending, journalPath);
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
        // Cancellation is deliberately ignored after the journal is published.
        try
        {
            Directory.Move(destination, backup);
            ArchiveCollectionExportFaults.AfterOldRename?.Invoke();
            Directory.Move(stage, destination);
            ArchiveCollectionExportFaults.AfterNewRename?.Invoke();
            ArchiveCollectionExportInspection published = await ArchiveCollectionExportMetadataFile.InspectAsync(destination,
                next.Members.Select(member => member.Thread).ToArray()).ConfigureAwait(false);
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
                ArchiveCollectionExportInspection fallback = await ArchiveCollectionExportMetadataFile.InspectAsync(backup).ConfigureAwait(false);
                if (fallback.ObservedFingerprint == current.ObservedFingerprint && !fallback.RequiresBackup)
                {
                    ArchiveCollectionExportFaults.BeforeBackupCleanup?.Invoke();
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


internal static class ArchiveCollectionExportFaults
{
    internal static Action? AfterOldRename;
    internal static Action? AfterNewRename;
    internal static Action? BeforeBackupCleanup;
}
