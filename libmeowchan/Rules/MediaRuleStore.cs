using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Persistence;

namespace MeowChan.Rules;

public sealed record MediaRuleLoadResult(MediaRuleSet RuleSet, string? Warning, bool Recovered);

/// <summary>
/// Profile-owned rules in media_rules.json. Load before use. Invalid unrecoverable data and
/// unsupported versions block activation; a valid backup is returned with an explicit warning.
/// Rule activation follows durable publication. Candidate decisions belong to a separate store.
/// </summary>
public sealed class MediaRuleStore
{
    private readonly string _filePath;
    private readonly ProfileLease _lease;
    private ActiveRules? _active;

    private ActiveRules Active => Volatile.Read(ref _active)
        ?? throw new InvalidOperationException("Load the media rules before reading or changing them.");
    public MediaRuleSet Current => Active.RuleSet;

    public MediaRuleStore(string profileDirectory, ProfileLease lease)
    {
        _lease = lease ?? throw new ArgumentNullException(nameof(lease));
        string directory = ProfileLease.NormalizeDirectory(profileDirectory);
        lease.EnsureMatches(directory);
        _filePath = Path.Combine(directory, "media_rules.json");
    }

    public Task<MediaRuleLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() => _lease.RunExclusive(() =>
        {
            MediaRuleLoadResult loaded = LoadCore(cancellationToken);
            Volatile.Write(ref _active, new ActiveRules(loaded.RuleSet, new MediaRuleResolver(loaded.RuleSet)));
            return loaded;
        }), cancellationToken);
    }

    /// <summary>
    /// Replaces the rule catalog. Pass the editor's revision to reject stale drafts.
    /// Input collections are captured before this method returns its task.
    /// </summary>
    public Task<MediaRuleSet> SaveAsync(IEnumerable<MediaRule> rules, string? expectedRevision = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MediaRuleSet captured = new MediaRuleSet(Guid.NewGuid().ToString("N"), rules);
        return Task.Run(() => _lease.RunExclusive(() =>
        {
            MediaRuleSet active = Current;
            if (expectedRevision != null && !string.Equals(expectedRevision, active.Revision, StringComparison.Ordinal))
                throw new InvalidOperationException("The media rules changed since this draft was created.");
            MediaRuleLoadResult disk = LoadCore(cancellationToken);
            if (!string.Equals(active.Revision, disk.RuleSet.Revision, StringComparison.Ordinal))
                throw new InvalidOperationException("The saved media rules changed. Reload before saving this draft.");
            ActiveRules next = new ActiveRules(captured, new MediaRuleResolver(captured));
            string json = JsonSerializer.Serialize(ToDocument(captured), MediaRulesJsonContext.Default.MediaRulesDocument);
            ReadResult primary = TryRead(_filePath);
            if (primary.Status == ReadStatus.Valid && TryRead(_filePath + ".bak").Status == ReadStatus.Invalid)
                AtomicJsonFile.PreserveFile(_filePath + ".bak");
            AtomicJsonFile.WriteAllText(_filePath, json, primary.Status == ReadStatus.Valid, cancellationToken);
            // Never observe cancellation after a successful replacement: the new rules are now durable.
            Volatile.Write(ref _active, next);
            return captured;
        }), cancellationToken);
    }

    public EffectiveMediaRule Resolve(ResourceId resource) => Active.Resolver.Resolve(resource);
    public EffectiveMediaRule Resolve(MediaRuleScope scope) => Active.Resolver.Resolve(scope);
    public MediaSelectionResult Evaluate(ResourceId resource, string? extension, long? sizeBytes)
        => Active.Resolver.Evaluate(resource, extension, sizeBytes);

    private MediaRuleLoadResult LoadCore(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadResult primary = TryRead(_filePath);
        if (primary.Status == ReadStatus.Valid) return new MediaRuleLoadResult(primary.RuleSet!, null, false);
        cancellationToken.ThrowIfCancellationRequested();
        ReadResult backup = TryRead(_filePath + ".bak");
        if (backup.Status == ReadStatus.Valid)
            return new MediaRuleLoadResult(backup.RuleSet!,
                $"Media rules could not be loaded from '{_filePath}' ({primary.Error ?? "file missing"}). " +
                "A valid backup was recovered. The original files were preserved.", true);
        if (primary.Status == ReadStatus.Missing && backup.Status == ReadStatus.Missing)
            return new MediaRuleLoadResult(MediaRuleSet.Default, null, false);
        throw new InvalidDataException($"Media rules at '{_filePath}' could not be loaded " +
            $"({primary.Error ?? "file missing"}); no valid backup is available ({backup.Error ?? "file missing"}). " +
            "The original files were preserved; media rules were not activated.");
    }

    private static ReadResult TryRead(string path)
    {
        string json;
        try { json = File.ReadAllText(path); }
        catch (FileNotFoundException) { return new ReadResult(ReadStatus.Missing); }
        catch (DirectoryNotFoundException) { return new ReadResult(ReadStatus.Missing); }
        try
        {
            // Inspect the version before deserializing the current shape. A newer shape may
            // omit today's required properties and must still never be treated as corrupt v1.
            using JsonDocument header = JsonDocument.Parse(json);
            if (header.RootElement.ValueKind == JsonValueKind.Object &&
                header.RootElement.TryGetProperty("SchemaVersion", out JsonElement version) &&
                version.ValueKind == JsonValueKind.Number &&
                version.TryGetInt32(out int schema) && schema != 1)
                throw new NotSupportedException($"Unsupported media rule schema version {schema} in '{path}'. The file was preserved.");
            MediaRulesDocument document = JsonSerializer.Deserialize(json, MediaRulesJsonContext.Default.MediaRulesDocument)
                ?? throw new InvalidDataException("The media rules document contains null.");
            if (document.SchemaVersion != 1) throw new InvalidDataException("The media rules schema version is missing or invalid.");
            if (document.Rules == null) throw new InvalidDataException("The media rule collection must not be null.");
            MediaRule[] rules = document.Rules.Select(FromDocument).ToArray();
            return new ReadResult(ReadStatus.Valid, new MediaRuleSet(document.Revision, rules));
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException)
        {
            return new ReadResult(ReadStatus.Invalid, Error: exception.Message);
        }
    }

    private static MediaRule FromDocument(MediaRuleDocument document)
    {
        if (document == null || document.Scope == null || document.Types == null || document.SizeLimit == null || document.Types.Extensions == null)
            throw new InvalidDataException("Media rule fields must not be null.");
        MediaRuleScope scope = new MediaRuleScope(document.Scope.Kind, document.Scope.Source, document.Scope.Board, document.Scope.Thread);
        return new MediaRule(scope, document.Enabled,
            new MediaTypeSelection(document.Types.Mode, document.Types.Extensions),
            new MediaSizeLimit(document.SizeLimit.Mode, document.SizeLimit.Bytes)) { ImageFilter = document.ImageFilter ?? MediaImageFilterRule.Inherit };
    }

    private static MediaRulesDocument ToDocument(MediaRuleSet snapshot)
        => new MediaRulesDocument
        {
            SchemaVersion = 1,
            Revision = snapshot.Revision,
            Rules = snapshot.Rules.Select(rule => new MediaRuleDocument
            {
                Scope = new MediaRuleScopeDocument { Kind = rule.Scope.Kind, Source = rule.Scope.Source, Board = rule.Scope.Board, Thread = rule.Scope.Thread },
                Enabled = rule.Enabled,
                ImageFilter = rule.ImageFilter,
                Types = new MediaTypeSelectionDocument { Mode = rule.Types.Mode, Extensions = rule.Types.Extensions.ToArray() },
                SizeLimit = new MediaSizeLimitDocument { Mode = rule.SizeLimit.Mode, Bytes = rule.SizeLimit.Bytes }
            }).ToArray()
        };

    private enum ReadStatus { Missing, Valid, Invalid }
    private sealed record ReadResult(ReadStatus Status, MediaRuleSet? RuleSet = null, string? Error = null);
    private sealed record ActiveRules(MediaRuleSet RuleSet, MediaRuleResolver Resolver);
}

internal sealed class MediaRulesDocument
{
    [JsonRequired] public int SchemaVersion { get; set; }
    [JsonRequired] public string Revision { get; set; } = string.Empty;
    [JsonRequired] public MediaRuleDocument[] Rules { get; set; } = Array.Empty<MediaRuleDocument>();
}

internal sealed class MediaRuleDocument
{
    public MediaImageFilterRule? ImageFilter { get; set; }
    [JsonRequired] public MediaRuleScopeDocument Scope { get; set; } = new MediaRuleScopeDocument();
    [JsonRequired] public bool Enabled { get; set; }
    [JsonRequired] public MediaTypeSelectionDocument Types { get; set; } = new MediaTypeSelectionDocument();
    [JsonRequired] public MediaSizeLimitDocument SizeLimit { get; set; } = new MediaSizeLimitDocument();
}

internal sealed class MediaRuleScopeDocument
{
    [JsonRequired] public MediaRuleScopeKind Kind { get; set; }
    public string? Source { get; set; }
    public string? Board { get; set; }
    public string? Thread { get; set; }
}

internal sealed class MediaTypeSelectionDocument
{
    [JsonRequired] public MediaTypeSelectionMode Mode { get; set; }
    [JsonRequired] public string[] Extensions { get; set; } = Array.Empty<string>();
}

internal sealed class MediaSizeLimitDocument
{
    [JsonRequired] public MediaSizeLimitMode Mode { get; set; }
    public long? Bytes { get; set; }
}

[JsonSerializable(typeof(MediaRulesDocument))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class MediaRulesJsonContext : JsonSerializerContext
{
}
