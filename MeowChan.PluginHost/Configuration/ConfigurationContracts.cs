using MeowChan.PluginHost.Activation;
using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.Configuration;

/// <summary>Exactly one source/board or an explicit rule identity. Nulls are not wildcard grants.</summary>
public sealed record PluginGrantScope(string SourceId, string? BoardId = null, string? RuleId = null);

public sealed record PluginGrantSnapshot(string PluginId, string FilterKey, PluginGrantScope Scope,
    FilterDataAccess Access, long Revision);

public sealed record PluginGrantChange(PluginGrantSnapshot Grant);

public sealed record FilterRuleSettingsSnapshot(string RuleId, string PluginId, string FilterKey,
    string ExecutionRevision, SettingsSnapshot Settings, long Revision);

public sealed record PluginProfileSnapshot(long Revision, IReadOnlyList<PluginActivationSnapshot> Plugins,
    IReadOnlyList<PluginGrantSnapshot> Grants, IReadOnlyList<FilterRuleSettingsSnapshot> FilterRules)
{
    public IReadOnlySet<string> RetainedExecutionRevisions => Releases()
        .Select(release => release.ExecutionRevision).Concat(Plugins.SelectMany(plugin => plugin.Candidates)
            .Where(candidate => candidate.Artifact is not null).Select(candidate => candidate.Artifact!.ExecutionRevision))
        .ToHashSet(StringComparer.Ordinal);

    public IReadOnlySet<string> RetainedCompilationFingerprints => Releases()
        .Select(release => release.CompilationFingerprint).Concat(Plugins.SelectMany(plugin => plugin.Candidates)
            .Where(candidate => candidate.Artifact is not null).Select(candidate => candidate.Artifact!.CompilationFingerprint))
        .ToHashSet(StringComparer.Ordinal);

    private IEnumerable<PluginReleaseSnapshot> Releases() => Plugins.SelectMany(plugin =>
        new[] { plugin.Active, plugin.Pending, plugin.LastGood, plugin.Pinned }.OfType<PluginReleaseSnapshot>());
}

public sealed record ConfigurationMigrationResult(PluginConfiguration? Configuration,
    IReadOnlyList<ValidationIssue> Issues);

public sealed class PluginProfileConflictException(long expected, long actual)
    : InvalidOperationException($"Plugin configuration changed (expected revision {expected}, current {actual}). Reload before saving.")
{
    public long ExpectedRevision { get; } = expected;
    public long ActualRevision { get; } = actual;
}

public sealed class PluginProfileCorruptException(string path, Exception? inner = null)
    : IOException($"Plugin profile store is invalid: {path}. The saved state has been retained; repair or restore it explicitly.", inner);

public sealed class PluginConfigurationValidationException(IReadOnlyList<ValidationIssue> issues)
    : ArgumentException(string.Join("; ", issues.Select(issue => issue.Path + ": " + issue.Message)))
{
    public IReadOnlyList<ValidationIssue> Issues { get; } = issues;
}
