using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using MeowChan.Storage;
using MeowChan.Transfer;
using Microsoft.Data.Sqlite;

namespace MeowChan.Archive;

internal static partial class ArchiveSql
{
    private const int PersistedMergeMaximumRows = 32;
    private const long PersistedMergeMaximumBytes = 32L * 1024 * 1024;

    // These readers return persistence evidence only. They do not authorize recovery,
    // decode the opaque catalog, inspect candidate files, or relax the old v1 APIs.
    internal static NativeMediaPersistedMergeOperation? ReadPersistedMediaMergeOperation(
        SqliteConnection connection, SqliteTransaction? transaction, Guid operation, string request,
        CancellationToken ct = default)
        => ReadPersistedMediaMergeOperation(connection, transaction, operation, request, ct, v2Only: false);

    // Explicit v2 single-row lookup. The plan-version boundary stays in SQL so a legacy row
    // with the same operation id can never be decoded through the v2 inspection surface.
    internal static NativeMediaPersistedMergeOperation? ReadPersistedMediaMergeV2Operation(
        SqliteConnection connection, SqliteTransaction? transaction, Guid operation, string request,
        CancellationToken ct = default)
        => ReadPersistedMediaMergeOperation(connection, transaction, operation, request, ct, v2Only: true);

    private static NativeMediaPersistedMergeOperation? ReadPersistedMediaMergeOperation(
        SqliteConnection connection, SqliteTransaction? transaction, Guid operation, string request,
        CancellationToken ct, bool v2Only)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(connection);
        ValidateMediaMergeIdentity(operation, request);
        if (transaction is null)
        {
            using SqliteTransaction owned = connection.BeginTransaction(deferred: true);
            return ReadPersistedMediaMergeOperation(connection, owned, operation, request, ct, v2Only);
        }
        RequirePersistedMediaSchema(connection, transaction);
        string versionFilter = v2Only ? " AND plan_version=2" : string.Empty;
        using SqliteCommand command = Command(connection, transaction,
            $"SELECT {PersistedMediaProjection(merge: true)} FROM archive_media_merge_operations WHERE operation_id=$op{versionFilter}");
        command.Parameters.AddWithValue("$op", operation.ToString("D"));
        ct.ThrowIfCancellationRequested();
        using SqliteDataReader reader = command.ExecuteReader();
        bool present = reader.Read();
        ct.ThrowIfCancellationRequested();
        if (!present) return null;
        // The shared helper validates individual columns only. Enforce the single-read
        // combined limit here, before any JSON/catalog document is materialized.
        long rowBytes = checked(PersistedMediaRowBytes(reader, merge: true));
        if (rowBytes < 0 || rowBytes > PersistedMergeMaximumBytes)
            throw InvalidPersistedMedia();
        NativeMediaPersistedMergeOperation result = ReadPersistedMergeOperationRow(reader, ct);
        if (result.Identity.OperationId != operation || result.Identity.RequestSha256 != request)
            throw new InvalidDataException("The media merge operation has different input.");
        ct.ThrowIfCancellationRequested();
        return result;
    }

    internal static NativeMediaPersistedPage<NativeMediaPersistedMergeOperation> ReadPersistedMediaMergeOperationsPage(
        SqliteConnection connection, Guid? after, CancellationToken ct = default)
        => ReadPersistedMediaMergeOperationsPage(connection, transaction: null, after, ct, v2Only: false);

    // Explicit v2 paging keeps the version boundary in SQL. The optional caller
    // transaction is required by the ArchiveCatalog facade when receipts are read
    // alongside operation rows.
    internal static NativeMediaPersistedPage<NativeMediaPersistedMergeOperation> ReadPersistedMediaMergeV2OperationsPage(
        SqliteConnection connection, SqliteTransaction? transaction, Guid? after, CancellationToken ct = default)
        => ReadPersistedMediaMergeOperationsPage(connection, transaction, after, ct, v2Only: true);

    private static NativeMediaPersistedPage<NativeMediaPersistedMergeOperation> ReadPersistedMediaMergeOperationsPage(
        SqliteConnection connection, SqliteTransaction? transaction, Guid? after, CancellationToken ct, bool v2Only)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(connection);
        if (after == Guid.Empty) throw new ArgumentException("Invalid media merge cursor.", nameof(after));
        if (transaction is null)
        {
            using SqliteTransaction owned = connection.BeginTransaction(deferred: true);
            return ReadPersistedMediaMergeOperationsPage(connection, owned, after, ct, v2Only);
        }
        RequirePersistedMediaSchema(connection, transaction);
        string versionFilter = v2Only ? " AND plan_version=2" : string.Empty;
        using SqliteCommand command = Command(connection, transaction,
            $"SELECT {PersistedMediaProjection(merge: true)} FROM archive_media_merge_operations "
            + $"WHERE operation_id>$after COLLATE BINARY{versionFilter} ORDER BY operation_id COLLATE BINARY LIMIT 33");
        command.Parameters.AddWithValue("$after", after?.ToString("D") ?? string.Empty);
        ct.ThrowIfCancellationRequested();
        using SqliteDataReader reader = command.ExecuteReader();
        var items = ImmutableArray.CreateBuilder<NativeMediaPersistedMergeOperation>(PersistedMergeMaximumRows);
        long bytes = 0;
        Guid? next = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            bool present = reader.Read();
            ct.ThrowIfCancellationRequested();
            if (!present) break;
            if (items.Count == PersistedMergeMaximumRows)
            {
                next = items[items.Count - 1].Identity.OperationId;
                break;
            }
            long rowBytes = checked(PersistedMediaRowBytes(reader, merge: true));
            if (rowBytes < 0) throw InvalidPersistedMedia();
            long combined = checked(bytes + rowBytes);
            if (combined > PersistedMergeMaximumBytes)
            {
                if (items.Count == 0) throw InvalidPersistedMedia();
                next = items[items.Count - 1].Identity.OperationId;
                break;
            }
            items.Add(ReadPersistedMergeOperationRow(reader, ct));
            bytes = combined;
        }
        ct.ThrowIfCancellationRequested();
        return new NativeMediaPersistedPage<NativeMediaPersistedMergeOperation>(items.ToImmutable(), next);
    }

    // Both entrypoints have charged all serialized documents before reaching this method.
    private static NativeMediaPersistedMergeOperation ReadPersistedMergeOperationRow(
        SqliteDataReader reader, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        NativeMediaPersistedIdentity identity = ReadPersistedMediaIdentity(reader);
        NativeMediaImportOperationState state = ReadPersistedMediaState(reader);
        string rootsJson = ReadPersistedMediaText(reader, "roots_json", maximumBytes: 8 * 1024 * 1024)
            ?? throw new InvalidDataException("A media merge roots document is missing.");
        ImmutableArray<NativeMediaMergeRoot> roots = ImmutableArray.CreateRange(ReadMergeRoots(rootsJson));
        NativeMediaPersistedRoot full = ReadPersistedMediaRoot(reader, identity, state, merge: true, safe: false);
        ct.ThrowIfCancellationRequested();
        NativeMediaPersistedRoot safe = ReadPersistedMediaRoot(reader, identity, state, merge: true, safe: true);
        NativeMediaMergeCounts counts = new(
            ReadPersistedMediaCount(reader, "new_posts"),
            ReadPersistedMediaCount(reader, "updated_posts"),
            ReadPersistedMediaCount(reader, "new_attachments"),
            ReadPersistedMediaCount(reader, "new_bindings"),
            ReadPersistedMediaCount(reader, "conflicts"),
            ReadPersistedMediaCount(reader, "evidence_additions"),
            ReadPersistedMediaCount(reader, "derived_descriptor_additions"),
            ReadPersistedMediaCount(reader, "contained_derived_additions"));
        if (counts.ContainedDerivedAdditions > counts.DerivedDescriptorAdditions)
            throw new InvalidDataException("A media merge operation has inconsistent derived counters.");
        ValidatePersistedMergeSelectionsAndCounts(identity, full, safe, counts, ct);
        ValidatePersistedMergeAlias(full, safe, counts.Conflicts);
        ct.ThrowIfCancellationRequested();
        ImmutableArray<byte> catalog = ReadPersistedMediaCatalog(reader, optional: false);
        if (identity.PlanVersion == 1 && catalog.IsEmpty)
            throw new InvalidDataException("A v1 media merge operation has an empty catalog.");
        ct.ThrowIfCancellationRequested();
        return new NativeMediaPersistedMergeOperation(identity, state, rootsJson, roots,
            full, safe, catalog, counts);
    }

    private static void ValidatePersistedMergeSelectionsAndCounts(NativeMediaPersistedIdentity identity,
        NativeMediaPersistedRoot full, NativeMediaPersistedRoot safe, NativeMediaMergeCounts counts,
        CancellationToken ct)
    {
        // Selection and counter invariants apply in Preparing too, not just ready phases.
        if (identity.PlanVersion == 1)
        {
            HashSet<string> keys = full.LegacyKeys.ToHashSet(StringComparer.Ordinal);
            if (full.LegacyKeys.IsEmpty || safe.LegacyKeys.Any(key => !keys.Contains(key)))
                throw new InvalidDataException("A media merge operation has invalid key subset data.");
            if (counts.NewBindings != safe.LegacyKeys.Length || counts.EvidenceAdditions != 0
                || counts.DerivedDescriptorAdditions != 0 || counts.ContainedDerivedAdditions != 0)
                throw new InvalidDataException("A v1 media merge operation has inconsistent counters.");
            return;
        }
        if (identity.PlanVersion != 2 || full.Selection is not { IsEmpty: false } fullSelection)
            throw new InvalidDataException("A media merge operation has invalid versioned full evidence.");
        NativeMediaCandidateInventory? safeInventory = null;
        if (safe.Selection is { } safeSelection)
        {
            fullSelection.ValidateSubset(safeSelection);
            ct.ThrowIfCancellationRequested();
            safeInventory = safeSelection.CreateInventory(identity.OperationId,
                identity.PackageFingerprint, identity.ImportedAtUtc);
            ct.ThrowIfCancellationRequested();
        }
        // Additions are deltas. They may be smaller than the inventory, and a nonempty
        // Safe may legitimately have evidence/derived members but no original bindings.
        if (counts.NewBindings > (safeInventory?.Bindings.Length ?? 0)
            || counts.EvidenceAdditions > (safeInventory?.OriginalEvidence.Length ?? 0)
            || counts.DerivedDescriptorAdditions > (safeInventory?.DerivedRepresentations.Length ?? 0)
            || counts.ContainedDerivedAdditions > (safeInventory?.DerivedRepresentations.Count(
                derived => derived.IncomingStatus == MediaDerivedStatus.Contained) ?? 0))
            throw new InvalidDataException("A media merge operation exceeds its safe inventory.");
    }

    private static void ValidatePersistedMergeAlias(NativeMediaPersistedRoot full,
        NativeMediaPersistedRoot safe, int conflicts)
    {
        StringComparison paths = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (full.Directory == null || safe.Directory == null
            || !string.Equals(full.Directory, safe.Directory, paths)) return;
        if (conflicts != 0
            || !full.LegacyKeys.SequenceEqual(safe.LegacyKeys, StringComparer.Ordinal)
            || full.LegacyBindingsSha256 != safe.LegacyBindingsSha256
            || full.Selection?.CanonicalJson != safe.Selection?.CanonicalJson
            || full.SelectionSha256 != safe.SelectionSha256
            || full.MemberCount != safe.MemberCount
            || full.InventorySha256 != safe.InventorySha256
            || full.ManifestGeneration != safe.ManifestGeneration)
            throw new InvalidDataException("A media merge operation reuses an unsafe full root.");
    }
}
