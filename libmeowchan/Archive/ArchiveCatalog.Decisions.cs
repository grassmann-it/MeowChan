using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using MeowChan.Rules;

namespace MeowChan.Archive;

public sealed partial class ArchiveCatalog
{
    /// <summary>
    /// Atomically retains first intent, before queue admission. Polls, retries, capture mode, and
    /// changed rules do not reconsider existing decisions. Only acquisition metadata is refreshed.
    /// previouslySaved is caller-supplied proof of a previous successful media commit, such as a
    /// manifest entry. It preserves prior intent; it never declares the current file verified or complete.
    /// </summary>
    public Task<CandidateDecision> GetOrCreateDecisionAsync(CandidateMetadata candidate, MediaRuleStore rules,
        bool previouslySaved = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(rules);
        return WriteAsync((SqliteConnection connection, SqliteTransaction transaction) =>
        {
            CandidateDecision? existing = FindDecision(connection, transaction, candidate.Key);
            if (existing != null)
            {
                CandidateMetadata updated = new CandidateMetadata(candidate.Key, candidate.DownloadUri,
                    existing.Metadata.DestinationPath, candidate.ExpectedSize, candidate.ExpectedMd5,
                    candidate.OriginalFileName, existing.Metadata.LegacyPath, candidate.Extension);
                if (updated == existing.Metadata) return existing;
                using SqliteCommand update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE media_decisions SET download_uri=$uri, expected_size=$size, expected_md5=$md5,
                        original_filename=$name, extension=$extension
                    WHERE source_id=$source AND board_id=$board AND thread_id=$thread AND media_id=$media AND variant=$variant;
                    """;
                AddCandidateKey(update, candidate.Key);
                AddObservedMetadata(update, updated);
                update.ExecuteNonQuery();
                return existing with { Metadata = updated };
            }

            MediaSelectionResult selection = previouslySaved
                ? new MediaSelectionResult(MediaSelectionOutcome.Selected, MediaSelectionReason.PreviouslySaved,
                    rules.Resolve(candidate.Key.Resource), candidate.Extension, candidate.ExpectedSize)
                : rules.Evaluate(candidate.Key.Resource, candidate.Extension, candidate.ExpectedSize);
            CandidateDecision created = new CandidateDecision(candidate, selection, DateTimeOffset.UtcNow);
            using SqliteCommand insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO media_decisions (source_id, board_id, thread_id, media_id, variant,
                    download_uri, destination_path, expected_size, expected_md5, original_filename,
                    legacy_path, extension, selection_json, decided_at_utc)
                VALUES ($source,$board,$thread,$media,$variant,$uri,$destination,$size,$md5,$name,
                    $legacy,$extension,$selection,$decided);
                """;
            AddCandidateKey(insert, candidate.Key);
            AddObservedMetadata(insert, candidate);
            insert.Parameters.AddWithValue("$destination", candidate.DestinationPath);
            insert.Parameters.AddWithValue("$legacy", (object?)candidate.LegacyPath ?? DBNull.Value);
            insert.Parameters.AddWithValue("$selection", SerializeSelection(selection));
            insert.Parameters.AddWithValue("$decided", created.DecidedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            insert.ExecuteNonQuery();
            return created;
        }, cancellationToken);
    }

    /// <summary>Reads durable acquisition intent for one thread, including after its source disappears.</summary>
    public Task<IReadOnlyList<CandidateDecision>> GetDecisionsAsync(ResourceId resource, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        _ = MediaRuleScope.ForThread(resource.Source, resource.Board, resource.Thread!);
        return ReadAsync(connection => ReadDecisions(connection, null, resource), cancellationToken);
    }

    internal static IReadOnlyList<CandidateDecision> ReadDecisions(SqliteConnection connection,
        SqliteTransaction? transaction, ResourceId resource)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT media_id,variant,download_uri,destination_path,expected_size,expected_md5,
                original_filename,legacy_path,extension,selection_json,decided_at_utc
            FROM media_decisions WHERE source_id=$source AND board_id=$board AND thread_id=$thread
            ORDER BY media_id,variant;
            """;
        command.Parameters.AddWithValue("$source", resource.Source);
        command.Parameters.AddWithValue("$board", resource.Board);
        command.Parameters.AddWithValue("$thread", resource.Thread!);
        List<CandidateDecision> decisions = new List<CandidateDecision>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read()) decisions.Add(ReadDecision(reader, resource));
        return new ReadOnlyCollection<CandidateDecision>(decisions);
    }

    internal static void CreateDecisionSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE media_decisions (
                source_id TEXT NOT NULL, board_id TEXT NOT NULL, thread_id TEXT NOT NULL,
                media_id TEXT NOT NULL, variant TEXT NOT NULL, download_uri TEXT NOT NULL,
                destination_path TEXT NOT NULL, expected_size INTEGER CHECK(expected_size IS NULL OR expected_size >= 0),
                expected_md5 TEXT, original_filename TEXT, legacy_path TEXT, extension TEXT,
                selection_json TEXT NOT NULL, decided_at_utc TEXT NOT NULL,
                PRIMARY KEY(source_id,board_id,thread_id,media_id,variant)
            );
            """;
        command.ExecuteNonQuery();
    }

    private static CandidateDecision? FindDecision(SqliteConnection connection, SqliteTransaction transaction, CandidateKey key)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT media_id,variant,download_uri,destination_path,expected_size,expected_md5,
                original_filename,legacy_path,extension,selection_json,decided_at_utc
            FROM media_decisions WHERE source_id=$source AND board_id=$board AND thread_id=$thread
                AND media_id=$media AND variant=$variant;
            """;
        AddCandidateKey(command, key);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadDecision(reader, key.Resource) : null;
    }

    private static CandidateDecision ReadDecision(SqliteDataReader reader, ResourceId resource)
    {
        try
        {
            CandidateMetadata metadata = new CandidateMetadata(
                new CandidateKey(resource, reader.GetString(0), reader.GetString(1)), new Uri(reader.GetString(2)),
                reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetInt64(4), CandidateOptionalString(reader, 5),
                CandidateOptionalString(reader, 6), CandidateOptionalString(reader, 7), CandidateOptionalString(reader, 8));
            MediaSelectionResult selection = DeserializeSelection(reader.GetString(9));
            DateTimeOffset decidedAt = DateTimeOffset.ParseExact(reader.GetString(10), "O", CultureInfo.InvariantCulture);
            return new CandidateDecision(metadata, selection, decidedAt);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException or InvalidCastException)
        {
            throw new InvalidDataException("A saved candidate decision contains invalid metadata. Its first decision was preserved.", exception);
        }
    }

    private static string? CandidateOptionalString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static void AddCandidateKey(SqliteCommand command, CandidateKey key)
    {
        command.Parameters.AddWithValue("$source", key.Resource.Source);
        command.Parameters.AddWithValue("$board", key.Resource.Board);
        command.Parameters.AddWithValue("$thread", key.Resource.Thread!);
        command.Parameters.AddWithValue("$media", key.MediaId);
        command.Parameters.AddWithValue("$variant", key.Variant);
    }

    private static void AddObservedMetadata(SqliteCommand command, CandidateMetadata candidate)
    {
        command.Parameters.AddWithValue("$uri", candidate.DownloadUri.AbsoluteUri);
        command.Parameters.AddWithValue("$size", (object?)candidate.ExpectedSize ?? DBNull.Value);
        command.Parameters.AddWithValue("$md5", (object?)candidate.ExpectedMd5 ?? DBNull.Value);
        command.Parameters.AddWithValue("$name", (object?)candidate.OriginalFileName ?? DBNull.Value);
        command.Parameters.AddWithValue("$extension", (object?)candidate.Extension ?? DBNull.Value);
    }

    private static string SerializeSelection(MediaSelectionResult selection)
    {
        EffectiveMediaRule effective = selection.EffectiveRule;
        CandidateSelectionDocument document = new CandidateSelectionDocument
        {
            Version = 1, Outcome = selection.Outcome, Reason = selection.Reason, Revision = effective.Revision,
            TypesMode = effective.Types.Mode, Extensions = new List<string>(effective.Types.Extensions).ToArray(),
            SizeMode = effective.SizeLimit.Mode, MaximumBytes = effective.SizeLimit.Bytes,
            TypesOrigin = ToOriginDocument(effective.TypesOrigin), SizeOrigin = ToOriginDocument(effective.SizeLimitOrigin),
            ObservedExtension = selection.Extension, ObservedSize = selection.SizeBytes
        };
        return JsonSerializer.Serialize(document, CandidateSelectionJsonContext.Default.CandidateSelectionDocument);
    }

    private static MediaSelectionResult DeserializeSelection(string json)
    {
        try
        {
            CandidateSelectionDocument document = JsonSerializer.Deserialize(json,
                CandidateSelectionJsonContext.Default.CandidateSelectionDocument)
                ?? throw new InvalidDataException("The saved candidate selection contains null.");
            if (document.Version != 1) throw new InvalidDataException("Unsupported saved candidate selection version.");
            if (!Enum.IsDefined(document.Outcome) || !Enum.IsDefined(document.Reason) || string.IsNullOrWhiteSpace(document.Revision) ||
                document.Extensions == null || document.TypesMode == MediaTypeSelectionMode.Inherit ||
                document.SizeMode == MediaSizeLimitMode.Inherit || document.ObservedSize < 0)
                throw new InvalidDataException("The saved candidate selection contains invalid values.");
            bool excluded = document.Reason is MediaSelectionReason.TypeExcluded or MediaSelectionReason.SizeExceeded;
            if (excluded != (document.Outcome == MediaSelectionOutcome.Excluded))
                throw new InvalidDataException("The saved candidate selection outcome and reason conflict.");
            EffectiveMediaRule effective = new EffectiveMediaRule(document.Revision,
                new MediaTypeSelection(document.TypesMode, document.Extensions), new MediaSizeLimit(document.SizeMode, document.MaximumBytes),
                FromOriginDocument(document.TypesOrigin), FromOriginDocument(document.SizeOrigin));
            string? extension = document.ObservedExtension == null ? null : MediaTypeSelection.NormalizeExtension(document.ObservedExtension);
            if (effective.TypesOrigin.Revision != effective.Revision || effective.SizeLimitOrigin.Revision != effective.Revision)
                throw new InvalidDataException("The saved rule origin revisions conflict.");
            MediaSelectionReason expectedReason = effective.Types.Mode == MediaTypeSelectionMode.Selected &&
                (extension == null || !Array.Exists(document.Extensions, value =>
                    string.Equals(MediaTypeSelection.NormalizeExtension(value), extension, StringComparison.Ordinal)))
                ? MediaSelectionReason.TypeExcluded
                : effective.SizeLimit.Mode == MediaSizeLimitMode.Limited && document.ObservedSize > effective.SizeLimit.Bytes
                    ? MediaSelectionReason.SizeExceeded
                    : document.ObservedSize.HasValue ? MediaSelectionReason.Allowed : MediaSelectionReason.AllowedUnknownSize;
            if (document.Reason != MediaSelectionReason.PreviouslySaved && document.Reason != expectedReason)
                throw new InvalidDataException("The saved candidate reason conflicts with its original effective values.");
            return new MediaSelectionResult(document.Outcome, document.Reason, effective, extension, document.ObservedSize);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new InvalidDataException("A saved candidate selection is damaged; it was not reevaluated.", exception);
        }
    }

    private static CandidateRuleOriginDocument ToOriginDocument(MediaRuleOrigin origin)
        => new CandidateRuleOriginDocument
        {
            Revision = origin.Revision, Kind = origin.Scope?.Kind, Source = origin.Scope?.Source,
            Board = origin.Scope?.Board, Thread = origin.Scope?.Thread
        };

    private static MediaRuleOrigin FromOriginDocument(CandidateRuleOriginDocument document)
    {
        if (document == null || string.IsNullOrWhiteSpace(document.Revision))
            throw new InvalidDataException("The saved rule origin is missing.");
        if (document.Kind == null && (document.Source != null || document.Board != null || document.Thread != null))
            throw new InvalidDataException("A built-in rule origin cannot have scope identities.");
        return new MediaRuleOrigin(document.Kind == null ? null :
            new MediaRuleScope(document.Kind.Value, document.Source, document.Board, document.Thread), document.Revision);
    }
}

internal sealed class CandidateSelectionDocument
{
    [JsonRequired] public int Version { get; set; }
    [JsonRequired] public MediaSelectionOutcome Outcome { get; set; }
    [JsonRequired] public MediaSelectionReason Reason { get; set; }
    [JsonRequired] public string Revision { get; set; } = string.Empty;
    [JsonRequired] public MediaTypeSelectionMode TypesMode { get; set; }
    [JsonRequired] public string[] Extensions { get; set; } = Array.Empty<string>();
    [JsonRequired] public MediaSizeLimitMode SizeMode { get; set; }
    [JsonRequired] public long? MaximumBytes { get; set; }
    [JsonRequired] public CandidateRuleOriginDocument TypesOrigin { get; set; } = new CandidateRuleOriginDocument();
    [JsonRequired] public CandidateRuleOriginDocument SizeOrigin { get; set; } = new CandidateRuleOriginDocument();
    [JsonRequired] public string? ObservedExtension { get; set; }
    [JsonRequired] public long? ObservedSize { get; set; }
}

internal sealed class CandidateRuleOriginDocument
{
    [JsonRequired] public string Revision { get; set; } = string.Empty;
    [JsonRequired] public MediaRuleScopeKind? Kind { get; set; }
    public string? Source { get; set; }
    public string? Board { get; set; }
    public string? Thread { get; set; }
}

[JsonSerializable(typeof(CandidateSelectionDocument))]
internal partial class CandidateSelectionJsonContext : JsonSerializerContext
{
}
