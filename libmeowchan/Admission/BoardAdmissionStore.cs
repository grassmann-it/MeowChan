using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using MeowChan.Persistence;

namespace MeowChan.Admission;

internal enum BoardAdmissionProblem { Damaged, NewerVersion, SizeLimit }

/// <summary>Always-durable board admission choices, independent of optional saved URL snapshots.</summary>
public sealed class BoardAdmissionStore
{
    internal const int DefaultSizeMiB = 4, MinimumSizeMiB = 1, MaximumSizeMiB = 64;
    private const int CurrentSchemaVersion = 1;
    private const string ProblemKey = "MeowChan.BoardAdmissionProblem";
    internal const string FileName = "board_admission.json";

    private enum FileState { Missing, InvalidJson, Valid }
    private sealed record ReadResult(FileState State, Dictionary<string, BoardAdmissionPolicy>? Policies = null);

    private readonly string _path;
    private readonly ProfileLease _lease;
    private Dictionary<string, BoardAdmissionPolicy> _policies;
    public string? LoadWarning { get; }

    public BoardAdmissionStore(string directory, ProfileLease lease)
    {
        lease.EnsureMatches(directory);
        _lease = lease;
        _path = Path.Combine(lease.ProfileDirectory, FileName);
        var loaded = lease.RunExclusive(() => Load(MaximumBytes));
        _policies = loaded.Policies;
        if (loaded.Recovered) LoadWarning = "Board admission settings were recovered from their backup.";
    }

    private int MaximumBytes => _lease.StoreLimits.BoardAdmissionSizeMiB * 1024 * 1024;

    internal void CheckSizeLimit(int sizeMiB) =>
        _lease.RunExclusive(() => Load(sizeMiB * 1024 * 1024));

    internal static BoardAdmissionProblem Describe(InvalidDataException error) =>
        error.Data[ProblemKey] is BoardAdmissionProblem problem ? problem : BoardAdmissionProblem.Damaged;

    private static InvalidDataException Failure(BoardAdmissionProblem problem, string message)
    {
        InvalidDataException error = new(message);
        error.Data[ProblemKey] = problem;
        return error;
    }

    public BoardAdmissionPolicy Get(string url) => Volatile.Read(ref _policies).GetValueOrDefault(url) ?? BoardAdmissionPolicy.All;
    public void Save(string url, BoardAdmissionPolicy policy)
    {
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter();
        SaveAdmitted(url, policy, admission);
    }
    internal ProfileLease ProfileLease => _lease;
    internal void SaveAdmitted(string url, BoardAdmissionPolicy policy, ProfileAdmissionToken admission)
    {
        _lease.RunStoreWrite(admission, () =>
        {
            int maximumBytes = MaximumBytes;
            // Recheck both files before rotating a backup. A newer file may have appeared
            // since startup, for example after a manual restore.
            var loaded = Load(maximumBytes);
            var next = new Dictionary<string, BoardAdmissionPolicy>(_policies, StringComparer.Ordinal) { [url] = policy };
            BoardAdmissionDocument document = new()
            {
                SchemaVersion = CurrentSchemaVersion,
                Boards = next.ToDictionary(pair => pair.Key, pair => new BoardPolicyDocument
                { Rules = pair.Value.Rules.ToArray(), Exceptions = new(pair.Value.Exceptions, StringComparer.Ordinal) }, StringComparer.Ordinal)
            };
            string json = JsonSerializer.Serialize(document, BoardAdmissionJsonContext.Default.BoardAdmissionDocument);
            if (Encoding.UTF8.GetByteCount(json) > maximumBytes) throw SizeFailure();
            AtomicJsonFile.WriteAllText(_path, json, backupCurrentFile: loaded.PrimaryValid);
            Volatile.Write(ref _policies, next);
            return true;
        });
    }

    private (Dictionary<string, BoardAdmissionPolicy> Policies, bool PrimaryValid, bool Recovered) Load(int maximumBytes)
    {
        ReadResult primary = Read(_path, maximumBytes);
        ReadResult backup = Read(_path + ".bak", maximumBytes);
        if (primary.Policies is { } current) return (current, true, false);
        if (backup.Policies is { } recovered) return (recovered, false, true);
        if (primary.State == FileState.Missing && backup.State == FileState.Missing)
            return (new(StringComparer.Ordinal), false, false);
        throw Damaged();
    }

    private static ReadResult Read(string path, int maximumBytes)
    {
        string? json;
        try { json = RegularProfileFile.ReadBoundedText(path, maximumBytes); }
        catch (ProfileDocumentTooLargeException) { throw SizeFailure(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { throw Damaged(); }
        if (json == null) return new(FileState.Missing);

        JsonDocument parsed;
        try { parsed = JsonDocument.Parse(json); }
        catch (JsonException) { return new(FileState.InvalidJson); }
        using (parsed)
            return new(FileState.Valid, ReadPolicies(parsed.RootElement));
    }

    private static Dictionary<string, BoardAdmissionPolicy> ReadPolicies(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(nameof(BoardAdmissionDocument.SchemaVersion), out JsonElement schema) ||
            schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int version))
            throw Damaged();
        if (version > CurrentSchemaVersion)
            throw Failure(BoardAdmissionProblem.NewerVersion, "Board admission settings use a newer schema. Existing files were preserved.");
        if (version != CurrentSchemaVersion ||
            !root.TryGetProperty(nameof(BoardAdmissionDocument.Boards), out JsonElement boards) ||
            boards.ValueKind != JsonValueKind.Object)
            throw Damaged();

        Dictionary<string, BoardAdmissionPolicy> result = new(StringComparer.Ordinal);
        foreach (JsonProperty board in boards.EnumerateObject())
        {
            if (!Uri.TryCreate(board.Name, UriKind.Absolute, out _) || board.Value.ValueKind != JsonValueKind.Object ||
                !board.Value.TryGetProperty(nameof(BoardPolicyDocument.Rules), out JsonElement rules) || rules.ValueKind != JsonValueKind.Array ||
                !board.Value.TryGetProperty(nameof(BoardPolicyDocument.Exceptions), out JsonElement exceptions) || exceptions.ValueKind != JsonValueKind.Object)
                throw Damaged();
            foreach (JsonElement rule in rules.EnumerateArray())
                if (rule.ValueKind != JsonValueKind.Object || !rule.TryGetProperty(nameof(ThreadTextRule.Field), out _) ||
                    !rule.TryGetProperty(nameof(ThreadTextRule.Action), out _) || !rule.TryGetProperty(nameof(ThreadTextRule.Contains), out _))
                    throw Damaged();
        }
        try
        {
            var document = root.Deserialize(BoardAdmissionJsonContext.Default.BoardAdmissionDocument)!;
            foreach (var pair in document.Boards)
                result.Add(pair.Key, new BoardAdmissionPolicy(pair.Value.Rules, pair.Value.Exceptions));
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException) { throw Damaged(); }
        return result;
    }

    private static InvalidDataException Damaged() => Failure(BoardAdmissionProblem.Damaged,
        "Board admission settings could not be read. Existing files were preserved.");

    private static InvalidDataException SizeFailure() => Failure(BoardAdmissionProblem.SizeLimit,
        "Board admission settings exceed the configured size limit.");
}
internal sealed class BoardAdmissionDocument
{
    public required int SchemaVersion { get; init; }
    public required Dictionary<string, BoardPolicyDocument> Boards { get; init; }
}
internal sealed class BoardPolicyDocument
{
    public required ThreadTextRule[] Rules { get; init; }
    public required Dictionary<string, ThreadAdmissionException> Exceptions { get; init; }
}
[JsonSourceGenerationOptions(AllowDuplicateProperties = false)]
[JsonSerializable(typeof(BoardAdmissionDocument))]
internal partial class BoardAdmissionJsonContext : JsonSerializerContext { }
