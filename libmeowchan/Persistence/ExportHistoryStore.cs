using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;

namespace MeowChan.Persistence;

public sealed record ExportHistoryEntry(ResourceId Thread, string Destination, Guid ExportId, int Generation,
    DateTimeOffset ExportedAt, ArchiveHtmlExportOptions Options);

/// <summary>Profile-local destination hints; each destination must still pass marker inspection.</summary>
public sealed class ExportHistoryStore
{
    private readonly ProfileLease _lease;
    private readonly string _path;
    private sealed record Index(int Version, List<ExportHistoryEntry> Entries);

    public ExportHistoryStore(string profileDirectory, ProfileLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lease.EnsureMatches(profileDirectory);
        _lease = lease;
        _path = Path.Combine(lease.ProfileDirectory, "export-history.json");
    }

    public IReadOnlyList<ExportHistoryEntry> List(ResourceId thread) => _lease.RunExclusive(() =>
        (IReadOnlyList<ExportHistoryEntry>)Read().Entries.Where(entry => entry.Thread == thread)
            .OrderByDescending(entry => entry.ExportedAt).ToArray());

    public async Task<IReadOnlyList<(ExportHistoryEntry Entry, ArchiveExportInspection? Inspection)>> ListVerifiedAsync(
        ResourceId thread, CancellationToken token = default)
    {
        List<(ExportHistoryEntry, ArchiveExportInspection?)> entries = new();
        foreach (ExportHistoryEntry entry in List(thread))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                ArchiveExportReplacement.RecoverHistory(entry.Destination, thread, entry.ExportId, entry.Generation);
                ArchiveExportInspection inspection = await ArchiveExportMetadataFile.InspectAsync(entry.Destination, thread, token).ConfigureAwait(false);
                entries.Add((entry, inspection.Metadata.ExportId == entry.ExportId &&
                    inspection.Metadata.Generation == entry.Generation ? inspection : null));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            { entries.Add((entry, null)); }
        }
        return entries;
    }

    /// <summary>Also associates a moved, marker-bearing export after validating the selected thread.</summary>
    public async Task<ExportHistoryEntry> AssociateAsync(string destination, ResourceId thread,
        CancellationToken token = default)
    {
        ArchiveExportInspection inspection = await ArchiveExportMetadataFile.InspectAsync(destination, thread, token).ConfigureAwait(false);
        ExportHistoryEntry entry = new(thread, inspection.Directory, inspection.Metadata.ExportId,
            inspection.Metadata.Generation, inspection.Metadata.ExportedAt, inspection.Metadata.Options);
        _lease.RunExclusive(() => { Commit(entry, token); return true; });
        return entry;
    }

    public async Task<ArchiveExportOperationResult> RecordPublishedAsync(ArchiveHtmlExportResult export,
        CancellationToken token = default)
    {
        if (export.Metadata == null) throw new ArgumentException("The result has no export metadata.", nameof(export));
        try
        {
            await AssociateAsync(export.Directory, export.Metadata.Thread, token).ConfigureAwait(false);
            return new ArchiveExportOperationResult(export, export.Metadata, export.BackupDirectory, true, null);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException)
        {
            return new ArchiveExportOperationResult(export, export.Metadata, export.BackupDirectory, false, ex.Message);
        }
    }

    private Index Read()
    {
        if (!File.Exists(_path)) return new Index(1, new List<ExportHistoryEntry>());
        FileInfo info = new(_path);
        if (info.Length > 4_000_000 || info.LinkTarget != null) throw new InvalidDataException("Export history index is invalid.");
        Index? index;
        try { index = JsonSerializer.Deserialize<Index>(File.ReadAllText(_path)); }
        catch (JsonException ex) { throw new InvalidDataException("Export history JSON is invalid.", ex); }
        if (index == null || index.Version != 1 || index.Entries == null || index.Entries.Count > 20_000)
            throw new InvalidDataException("Export history index version or entries are invalid.");
        return index;
    }

    private void Commit(ExportHistoryEntry entry, CancellationToken token)
    {
        Index index = Read();
        index.Entries.RemoveAll(previous => previous.Destination == entry.Destination ||
            (previous.ExportId == entry.ExportId && previous.Generation <= entry.Generation));
        index.Entries.Add(entry);
        AtomicJsonFile.WriteAllText(_path, JsonSerializer.Serialize(index), backupCurrentFile: true, token);
    }
}
