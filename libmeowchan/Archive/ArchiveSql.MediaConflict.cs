using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace MeowChan.Archive;

internal static partial class ArchiveSql
{
    // Schema 12 adds the durable conflict ledger. The active archive value is never stored here:
    // a row only records that an inactive candidate (identified by digest) differed from the active
    // target, where it came from, and what the user decided. Status values match
    // NativeMediaConflictResolutionStatus (1 open, 2 kept, 4 discarded, 5 promoted).
    private const string MediaConflictTable12 = """
        CREATE TABLE media_conflict (
            identity_sha256 TEXT NOT NULL PRIMARY KEY CHECK(typeof(identity_sha256)='text' AND length(identity_sha256)=64 AND identity_sha256 NOT GLOB '*[^0-9a-f]*'),
            source_id TEXT NOT NULL CHECK(typeof(source_id)='text' AND length(CAST(source_id AS BLOB)) BETWEEN 1 AND 4096),
            board_id TEXT NOT NULL CHECK(typeof(board_id)='text' AND length(CAST(board_id AS BLOB)) BETWEEN 1 AND 4096),
            thread_id TEXT NOT NULL CHECK(typeof(thread_id)='text' AND length(CAST(thread_id AS BLOB)) BETWEEN 1 AND 4096),
            occurrence_sha256 TEXT NOT NULL CHECK(typeof(occurrence_sha256)='text' AND length(occurrence_sha256)=64 AND occurrence_sha256 NOT GLOB '*[^0-9a-f]*'),
            active_sha256 TEXT NOT NULL CHECK(typeof(active_sha256)='text' AND length(active_sha256)=64 AND active_sha256 NOT GLOB '*[^0-9a-f]*'),
            candidate_sha256 TEXT NOT NULL CHECK(typeof(candidate_sha256)='text' AND length(candidate_sha256)=64 AND candidate_sha256 NOT GLOB '*[^0-9a-f]*'),
            active_bytes INTEGER NOT NULL CHECK(typeof(active_bytes)='integer' AND active_bytes BETWEEN 0 AND 2147483648),
            candidate_bytes INTEGER NOT NULL CHECK(typeof(candidate_bytes)='integer' AND candidate_bytes BETWEEN 0 AND 2147483648),
            operation_id TEXT NOT NULL CHECK(typeof(operation_id)='text' AND length(operation_id)=36),
            request_sha256 TEXT NOT NULL CHECK(typeof(request_sha256)='text' AND length(request_sha256)=64 AND request_sha256 NOT GLOB '*[^0-9a-f]*'),
            resource_set_sha256 TEXT NOT NULL CHECK(typeof(resource_set_sha256)='text' AND length(resource_set_sha256)=64 AND resource_set_sha256 NOT GLOB '*[^0-9a-f]*'),
            status INTEGER NOT NULL CHECK(typeof(status)='integer' AND status IN (1,2,4,5)),
            decided_at_utc TEXT CHECK(decided_at_utc IS NULL OR (typeof(decided_at_utc)='text' AND length(CAST(decided_at_utc AS BLOB)) BETWEEN 1 AND 64)),
            CHECK(active_sha256<>candidate_sha256),
            CHECK((status=1 AND decided_at_utc IS NULL) OR (status<>1 AND decided_at_utc IS NOT NULL)))
        """;

    internal static void CreateMediaConflictSchema12(SqliteConnection connection, SqliteTransaction transaction)
    {
        using SqliteCommand create = Command(connection, transaction, MediaConflictTable12);
        create.ExecuteNonQuery();
    }

    internal static void MigrateMediaConflictSchema12(SqliteConnection connection, SqliteTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (!ReferenceEquals(transaction.Connection, connection))
            throw new InvalidOperationException("Schema 12 migration requires the caller's active transaction.");
        cancellationToken.ThrowIfCancellationRequested();
        int version = GetPragma(connection, "user_version", transaction);
        if (version == 12) { ValidateMediaConflictSchema12(connection, transaction); return; }
        if (version != 11)
            throw new InvalidDataException("The media conflict ledger requires schema 11 as its migration source.");
        ValidateTargetSchema(connection, transaction, 11);
        // Fixtures and interrupted tooling may leave a correctly shaped ledger in an older-stamped
        // catalog; adopt it after validation instead of recreating (and never repair a wrong shape).
        using (SqliteCommand exists = Command(connection, transaction,
            "SELECT 1 FROM main.sqlite_master WHERE type='table' AND name='media_conflict'"))
            if (exists.ExecuteScalar() == null) CreateMediaConflictSchema12(connection, transaction);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateMediaConflictSchema12(connection, transaction);
    }

    internal static void ValidateMediaConflictSchema12(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using SqliteCommand definition = Command(connection, transaction,
            "SELECT sql FROM main.sqlite_master WHERE type='table' AND name='media_conflict'");
        string sql = Convert.ToString(definition.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "";
        if (Normalize(sql) != Normalize(MediaConflictTable12))
            throw new InvalidDataException("The archive has an invalid media conflict ledger.");
        static string Normalize(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();
    }

    internal const int MaximumMediaConflictPage = 100;

    internal static (int Inserted, int AlreadyKnown) InsertMediaConflicts(SqliteConnection connection,
        SqliteTransaction transaction, MeowChan.Transfer.NativeMediaConflictProvenance provenance,
        IEnumerable<MeowChan.Transfer.NativeMediaConflictEntry> entries)
    {
        int inserted = 0, known = 0;
        foreach (MeowChan.Transfer.NativeMediaConflictEntry entry in entries)
        {
            if (entry.Resolution != MeowChan.Transfer.NativeMediaConflictResolutionStatus.Open)
                throw new ArgumentException("Only open conflicts can be recorded.", nameof(entries));
            // A kept, discarded or promoted decision survives a replay: the unique identity
            // makes the insert a no-op, so a discarded candidate never reopens.
            using SqliteCommand insert = Command(connection, transaction, """
                INSERT OR IGNORE INTO media_conflict(identity_sha256,source_id,board_id,thread_id,occurrence_sha256,
                    active_sha256,candidate_sha256,active_bytes,candidate_bytes,operation_id,request_sha256,
                    resource_set_sha256,status,decided_at_utc)
                VALUES($id,$source,$board,$thread,$occurrence,$active,$candidate,$activeBytes,$candidateBytes,
                    $operation,$request,$set,1,NULL)
                """);
            insert.Parameters.AddWithValue("$id", entry.Identity.IdentitySha256);
            insert.Parameters.AddWithValue("$source", entry.Identity.Resource.Source);
            insert.Parameters.AddWithValue("$board", entry.Identity.Resource.Board);
            insert.Parameters.AddWithValue("$thread", entry.Identity.Resource.Thread ?? throw new ArgumentException("A conflict needs a thread."));
            insert.Parameters.AddWithValue("$occurrence", entry.Identity.OccurrenceSha256);
            insert.Parameters.AddWithValue("$active", entry.Identity.ActiveVariantSha256);
            insert.Parameters.AddWithValue("$candidate", entry.Identity.CandidateVariantSha256);
            insert.Parameters.AddWithValue("$activeBytes", entry.Target.ByteLength);
            insert.Parameters.AddWithValue("$candidateBytes", entry.Candidate.ByteLength);
            insert.Parameters.AddWithValue("$operation", provenance.OperationId.ToString("D", CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$request", provenance.RequestSha256);
            insert.Parameters.AddWithValue("$set", provenance.ResourceSetSha256);
            if (insert.ExecuteNonQuery() == 1) inserted++; else known++;
        }
        return (inserted, known);
    }

    internal enum MediaConflictDecisionOutcome { Applied, AlreadyApplied, Missing, NotAllowed }

    // Open may move to any decision; Kept may still be discarded or promoted. Discarded and
    // Promoted are final for the ledger (their bytes/roles have already moved on).
    internal static MediaConflictDecisionOutcome DecideMediaConflict(SqliteConnection connection,
        SqliteTransaction transaction, string identitySha256,
        MeowChan.Transfer.NativeMediaConflictResolutionStatus status, DateTimeOffset decidedAtUtc)
    {
        if (status is not (MeowChan.Transfer.NativeMediaConflictResolutionStatus.Kept
            or MeowChan.Transfer.NativeMediaConflictResolutionStatus.Discarded
            or MeowChan.Transfer.NativeMediaConflictResolutionStatus.Promoted))
            throw new ArgumentOutOfRangeException(nameof(status));
        using SqliteCommand read = Command(connection, transaction,
            "SELECT status FROM media_conflict WHERE identity_sha256=$id");
        read.Parameters.AddWithValue("$id", identitySha256);
        if (read.ExecuteScalar() is not long current) return MediaConflictDecisionOutcome.Missing;
        if (current == (int)status) return MediaConflictDecisionOutcome.AlreadyApplied;
        bool allowed = current == (int)MeowChan.Transfer.NativeMediaConflictResolutionStatus.Open
            || (current == (int)MeowChan.Transfer.NativeMediaConflictResolutionStatus.Kept
                && status != MeowChan.Transfer.NativeMediaConflictResolutionStatus.Kept);
        if (!allowed) return MediaConflictDecisionOutcome.NotAllowed;
        using SqliteCommand update = Command(connection, transaction,
            "UPDATE media_conflict SET status=$status,decided_at_utc=$at WHERE identity_sha256=$id");
        update.Parameters.AddWithValue("$status", (int)status);
        update.Parameters.AddWithValue("$at", decidedAtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        update.Parameters.AddWithValue("$id", identitySha256);
        update.ExecuteNonQuery();
        return MediaConflictDecisionOutcome.Applied;
    }

    internal static (int Total, List<PersistedMediaConflict> Items) ReadMediaConflicts(SqliteConnection connection,
        SqliteTransaction transaction, int offset, int limit)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (limit < 1 || limit > MaximumMediaConflictPage) throw new ArgumentOutOfRangeException(nameof(limit));
        using SqliteCommand count = Command(connection, transaction, "SELECT COUNT(*) FROM media_conflict");
        int total = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);
        using SqliteCommand select = Command(connection, transaction, """
            SELECT identity_sha256,source_id,board_id,thread_id,occurrence_sha256,active_sha256,candidate_sha256,
                active_bytes,candidate_bytes,operation_id,request_sha256,resource_set_sha256,status,decided_at_utc
            FROM media_conflict ORDER BY identity_sha256 LIMIT $limit OFFSET $offset
            """);
        select.Parameters.AddWithValue("$limit", limit);
        select.Parameters.AddWithValue("$offset", offset);
        List<PersistedMediaConflict> items = new();
        using SqliteDataReader reader = select.ExecuteReader();
        while (reader.Read())
        {
            var resource = new ResourceId(reader.GetString(1), reader.GetString(2), reader.GetString(3));
            var identity = new MeowChan.Transfer.NativeMediaConflictIdentity(resource, reader.GetString(4),
                reader.GetString(5), reader.GetString(6));
            if (identity.IdentitySha256 != reader.GetString(0))
                throw new InvalidDataException("The media conflict ledger contains a mismatched identity.");
            var status = (MeowChan.Transfer.NativeMediaConflictResolutionStatus)reader.GetInt32(12);
            var entry = new MeowChan.Transfer.NativeMediaConflictEntry(identity,
                new(identity.ActiveVariantSha256, MeowChan.Transfer.NativeMediaConflictVariantStatus.Active, reader.GetInt64(7)),
                new(identity.CandidateVariantSha256, MeowChan.Transfer.NativeMediaConflictVariantStatus.Inactive, reader.GetInt64(8)),
                status);
            var provenance = new MeowChan.Transfer.NativeMediaConflictProvenance(
                Guid.ParseExact(reader.GetString(9), "D"), reader.GetString(10), reader.GetString(11));
            DateTimeOffset? decided = reader.IsDBNull(13) ? null
                : DateTimeOffset.Parse(reader.GetString(13), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            items.Add(new PersistedMediaConflict(entry, provenance, decided));
        }
        return (total, items);
    }
}

internal sealed record PersistedMediaConflict(MeowChan.Transfer.NativeMediaConflictEntry Entry,
    MeowChan.Transfer.NativeMediaConflictProvenance Provenance, DateTimeOffset? DecidedAtUtc);
