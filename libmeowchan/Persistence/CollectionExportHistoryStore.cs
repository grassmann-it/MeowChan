using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;

namespace MeowChan.Persistence;

public sealed record CollectionExportHistoryEntry(IReadOnlyList<ResourceId> Members, string Destination, Guid ExportId, int Generation,
    DateTimeOffset ExportedAt, ArchiveHtmlExportOptions Options);

/// <summary>Profile-local destination hints; each destination must still pass marker inspection.</summary>
public sealed class CollectionExportHistoryStore
{
    private readonly ProfileLease _lease;
    private readonly string _path;
    private sealed record Index(int Version, List<CollectionExportHistoryEntry> Entries);

    public CollectionExportHistoryStore(string profileDirectory, ProfileLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lease.EnsureMatches(profileDirectory);
        _lease = lease;
        _path = Path.Combine(lease.ProfileDirectory, "collection-export-history.json");
    }

    public IReadOnlyList<CollectionExportHistoryEntry> List(IReadOnlyList<ResourceId> members)
    {
        ResourceId[] frozen = ArchiveCollectionHtmlExporter.FreezeMembers(members);
        return _lease.RunExclusive(() => (IReadOnlyList<CollectionExportHistoryEntry>)Read().Entries
            .Where(entry => entry.Members.SequenceEqual(frozen)).OrderByDescending(entry => entry.ExportedAt).ToArray());
    }

    public async Task<IReadOnlyList<(CollectionExportHistoryEntry Entry, ArchiveCollectionExportInspection? Inspection)>> ListVerifiedAsync(
        IReadOnlyList<ResourceId> members, CancellationToken token = default)
    {
        members = ArchiveCollectionHtmlExporter.FreezeMembers(members);
        List<(CollectionExportHistoryEntry, ArchiveCollectionExportInspection?)> entries = new();
        foreach (CollectionExportHistoryEntry entry in List(members))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                ArchiveCollectionExportReplacement.RecoverHistory(entry.Destination, members, entry.ExportId, entry.Generation);
                ArchiveCollectionExportInspection inspection = await ArchiveCollectionExportMetadataFile.InspectAsync(entry.Destination, members, token).ConfigureAwait(false);
                entries.Add((entry, inspection.Metadata.ExportId == entry.ExportId &&
                    inspection.Metadata.Generation == entry.Generation ? inspection : null));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            { entries.Add((entry, null)); }
        }
        return entries;
    }

    /// <summary>Also associates a moved, marker-bearing export after validating the frozen ordered collection members.</summary>
    public async Task<CollectionExportHistoryEntry> AssociateAsync(string destination, IReadOnlyList<ResourceId> members,
        CancellationToken token = default)
    {
        ArchiveCollectionExportInspection inspection = await ArchiveCollectionExportMetadataFile.InspectAsync(destination, members, token).ConfigureAwait(false);
        CollectionExportHistoryEntry entry = new(inspection.Metadata.Members.Select(member => member.Thread).ToArray(), inspection.Directory, inspection.Metadata.ExportId,
            inspection.Metadata.Generation, inspection.Metadata.ExportedAt, inspection.Metadata.Options);
        _lease.RunExclusive(() => { Commit(entry, token); return true; });
        return entry;
    }

    public async Task<ArchiveCollectionExportOperationResult> RecordPublishedAsync(ArchiveCollectionHtmlExportResult export,
        CancellationToken token = default)
    {
        if (export.Metadata == null) throw new ArgumentException("The result has no export metadata.", nameof(export));
        try
        {
            ArchiveCollectionExportInspection inspection = await ArchiveCollectionExportMetadataFile.InspectAsync(export.Directory,
                export.Metadata.Members.Select(member => member.Thread).ToArray(), token).ConfigureAwait(false);
            if (inspection.Metadata.ExportId != export.Metadata.ExportId || inspection.Metadata.Generation != export.Metadata.Generation)
                throw new IOException("The published collection changed before its destination hint could be recorded.");
            CollectionExportHistoryEntry entry = new(inspection.Metadata.Members.Select(member => member.Thread).ToArray(),
                inspection.Directory, inspection.Metadata.ExportId, inspection.Metadata.Generation,
                inspection.Metadata.ExportedAt, inspection.Metadata.Options);
            _lease.RunExclusive(() => { Commit(entry, token); return true; });
            return new ArchiveCollectionExportOperationResult(export, export.Metadata, export.BackupDirectory, true, null);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException)
        {
            return new ArchiveCollectionExportOperationResult(export, export.Metadata, export.BackupDirectory, false, ex.Message);
        }
    }

    private Index Read()
    {
        if (!File.Exists(_path)) return new Index(1, new List<CollectionExportHistoryEntry>());
        FileInfo info = new(_path);
        if (info.Length > 4_000_000 || info.LinkTarget != null) throw new InvalidDataException("Export history index is invalid.");
        Index? index;
        try { index = JsonSerializer.Deserialize<Index>(File.ReadAllText(_path)); }
        catch (JsonException ex) { throw new InvalidDataException("Export history JSON is invalid.", ex); }
        if (index == null || index.Version != 1 || index.Entries == null || index.Entries.Count > 20_000)
            throw new InvalidDataException("Export history index version or entries are invalid.");
        foreach (CollectionExportHistoryEntry entry in index.Entries)
        {
            if (entry == null || entry.Members == null || entry.Members.Count == 0 ||
                string.IsNullOrWhiteSpace(entry.Destination) || !Path.IsPathFullyQualified(entry.Destination) ||
                entry.ExportId == Guid.Empty || entry.Generation < 1 || entry.Options == null)
                throw new InvalidDataException("Collection export history entry is invalid.");
            try
            {
                if (ArchiveCollectionHtmlExporter.FreezeMembers(entry.Members).Length != entry.Members.Count)
                    throw new InvalidDataException("Collection export history has duplicate members.");
            }
            catch (ArgumentException ex) { throw new InvalidDataException("Collection export history members are invalid.", ex); }
        }
        return index;
    }

    private void Commit(CollectionExportHistoryEntry entry, CancellationToken token)
    {
        Index index = Read();
        index.Entries.RemoveAll(previous => previous.Destination == entry.Destination ||
            (previous.ExportId == entry.ExportId && previous.Generation <= entry.Generation));
        index.Entries.Add(entry);
        AtomicJsonFile.WriteAllText(_path, JsonSerializer.Serialize(index), backupCurrentFile: true, token);
    }
}
