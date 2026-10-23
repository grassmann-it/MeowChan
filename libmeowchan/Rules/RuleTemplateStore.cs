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

/// <summary>Independent profile-owned catalog. No active rule or worker is changed by a template write.</summary>
public sealed class RuleTemplateStore
{
    private readonly string _path;
    private readonly ProfileLease _lease;
    private RuleTemplateSet? _current;
    public RuleTemplateSet Current => Volatile.Read(ref _current) ?? throw new InvalidOperationException("Load rule templates first.");
    public RuleTemplateStore(string profileDirectory, ProfileLease lease)
    {
        _lease = lease ?? throw new ArgumentNullException(nameof(lease));
        string directory = ProfileLease.NormalizeDirectory(profileDirectory); lease.EnsureMatches(directory);
        _path = Path.Combine(directory, "rule_templates.json");
    }
    public Task<RuleTemplateLoadResult> LoadAsync(CancellationToken cancellationToken = default) => Task.Run(() => _lease.RunExclusive(() =>
    {
        RuleTemplateLoadResult result = LoadCore(cancellationToken); Volatile.Write(ref _current, result.Catalog); return result;
    }), cancellationToken);
    public Task<RuleTemplateSet> SaveAsync(IEnumerable<RuleTemplate> templates, string expectedRevision, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RuleTemplateSet captured = new(Guid.NewGuid().ToString("N"), templates);
        return Task.Run(() => _lease.RunExclusive(() =>
        {
            RuleTemplateSet active = Current;
            if (active.Revision != expectedRevision || LoadCore(cancellationToken).Catalog.Revision != active.Revision)
                throw new InvalidOperationException("Rule templates changed. Reload before saving this draft.");
            TemplateDocument document = new() { SchemaVersion = 1, Revision = captured.Revision, Templates = captured.Templates.Select(ToDocument).ToArray() };
            string json = JsonSerializer.Serialize(document, RuleTemplateJsonContext.Default.TemplateDocument);
            ReadResult primary = Read(_path);
            if (primary.Catalog != null && Read(_path + ".bak").Invalid) AtomicJsonFile.PreserveFile(_path + ".bak");
            AtomicJsonFile.WriteAllText(_path, json, primary.Catalog != null, cancellationToken);
            // Publication is durable; do not observe cancellation after replacement.
            Volatile.Write(ref _current, captured); return captured;
        }), cancellationToken);
    }
    private RuleTemplateLoadResult LoadCore(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ReadResult primary = Read(_path);
        if (primary.Catalog != null) return new(primary.Catalog, null, false);
        token.ThrowIfCancellationRequested(); ReadResult backup = Read(_path + ".bak");
        if (backup.Catalog != null) return new(backup.Catalog, "Rule templates were recovered from backup. Original files were preserved.", true);
        if (!primary.Invalid && !backup.Invalid) return new(RuleTemplateSet.Empty, null, false);
        throw new InvalidDataException("Rule templates could not be loaded. Original files were preserved. " + primary.Error);
    }
    private static ReadResult Read(string path)
    {
        string json;
        try { json = File.ReadAllText(path); }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
        try
        {
            using JsonDocument header = JsonDocument.Parse(json);
            if (header.RootElement.ValueKind == JsonValueKind.Object && header.RootElement.TryGetProperty("schemaVersion", out JsonElement v)
                && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int schema) && schema != 1)
                throw new NotSupportedException($"Unsupported rule template schema {schema}. The files were preserved.");
            TemplateDocument document = JsonSerializer.Deserialize(json, RuleTemplateJsonContext.Default.TemplateDocument) ?? throw new InvalidDataException("Null template catalog.");
            if (document.SchemaVersion != 1 || document.Templates == null) throw new InvalidDataException("Invalid template catalog.");
            return new(new RuleTemplateSet(document.Revision, document.Templates.Select(FromDocument)));
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidDataException)
        { return new(Invalid: true, Error: exception.Message); }
    }
    private static TemplateValueDocument ToDocument(RuleTemplate template) => new()
    {
        Id = template.Id, Name = template.Name,
        Types = template.Types == null ? null : new() { Mode = template.Types.Mode, Extensions = template.Types.Extensions.ToArray() },
        SizeLimit = template.SizeLimit == null ? null : new() { Mode = template.SizeLimit.Mode, Bytes = template.SizeLimit.Bytes },
        ImageFilter = template.ImageFilter == null ? null : new() { Mode = template.ImageFilter.Mode, PluginId = template.ImageFilter.PluginId,
            FilterKey = template.ImageFilter.FilterKey, SchemaVersion = template.ImageFilter.SchemaVersion, Parameters = new(template.ImageFilter.Parameters, StringComparer.Ordinal) }
    };
    private static RuleTemplate FromDocument(TemplateValueDocument value)
    {
        if (value == null) throw new InvalidDataException("Null template.");
        return new(value.Id, value.Name,
            value.Types == null ? null : new MediaTypeSelection(value.Types.Mode, value.Types.Extensions ?? throw new InvalidDataException("Null extensions.")),
            value.SizeLimit == null ? null : new MediaSizeLimit(value.SizeLimit.Mode, value.SizeLimit.Bytes),
            value.ImageFilter == null ? null : new MediaImageFilterRule(value.ImageFilter.Mode, value.ImageFilter.PluginId, value.ImageFilter.FilterKey,
                value.ImageFilter.SchemaVersion, value.ImageFilter.Parameters ?? throw new InvalidDataException("Null parameters.")));
    }
    private sealed record ReadResult(RuleTemplateSet? Catalog = null, bool Invalid = false, string? Error = null);
}
internal sealed class TemplateDocument
{
    [JsonRequired] public int SchemaVersion { get; set; }
    [JsonRequired] public string Revision { get; set; } = string.Empty;
    [JsonRequired] public TemplateValueDocument[] Templates { get; set; } = Array.Empty<TemplateValueDocument>();
}
internal sealed class TemplateValueDocument
{
    [JsonRequired] public Guid Id { get; set; }
    [JsonRequired] public string Name { get; set; } = string.Empty;
    public TemplateTypesDocument? Types { get; set; }
    public TemplateSizeDocument? SizeLimit { get; set; }
    public TemplateFilterDocument? ImageFilter { get; set; }
}
internal sealed class TemplateTypesDocument
{
    [JsonRequired] public MediaTypeSelectionMode Mode { get; set; }
    [JsonRequired] public string[] Extensions { get; set; } = Array.Empty<string>();
}
internal sealed class TemplateSizeDocument
{
    [JsonRequired] public MediaSizeLimitMode Mode { get; set; }
    public long? Bytes { get; set; }
}
internal sealed class TemplateFilterDocument
{
    [JsonRequired] public MediaImageFilterMode Mode { get; set; }
    public string? PluginId { get; set; }
    public string? FilterKey { get; set; }
    [JsonRequired] public string SchemaVersion { get; set; } = "1";
    [JsonRequired] public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.Ordinal);
}
[JsonSerializable(typeof(TemplateDocument))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class RuleTemplateJsonContext : JsonSerializerContext { }
