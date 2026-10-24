using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Compilation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Installation;
using MeowChan.PluginSdk;

namespace MeowChan.Desktop.Plugins;

internal sealed record PluginCatalogEntry(string Key, string? PluginId, string? CandidateId, string? SourcePath,
    PluginDescription? Description, PluginActivationSnapshot? Activation, PluginCandidateSnapshot? Candidate,
    IReadOnlyList<PluginBuildDiagnostic> Diagnostics, string? InventoryPath = null)
{
    internal string? LoaderPath => InventoryPath ?? SourcePath ?? Candidate?.CandidatePath ?? Diagnostics.FirstOrDefault(diagnostic => diagnostic.FilePath != null)?.FilePath;
    internal string Name => Description?.Name ?? Path.GetFileName(LoaderPath) ?? PluginId ?? Key;
    internal bool IsSource => Description == null ? SourcePath != null : Description.Sources.Count > 0;
    internal bool IsFilter => Description?.Filters.Count > 0;
    internal PluginReleaseSnapshot? EditableRelease => new[] { Activation?.Pending, Activation?.Pinned, Activation?.Active, Activation?.LastGood }
        .FirstOrDefault(release => release != null && release.CandidateId == CandidateId);
    internal PluginReleaseSnapshot? PinnedRelease => Activation?.Pinned is { } pinned && pinned.CandidateId == CandidateId ? pinned : null;
    internal PluginArtifact? ConfigurationArtifact => PinnedRelease?.Artifact ?? Candidate?.Artifact ?? EditableRelease?.Artifact;
}
internal sealed record PluginCatalogSnapshot(PluginProfileSnapshot Profile, IReadOnlyList<PluginCatalogEntry> Entries,
    IReadOnlyList<PluginBuildDiagnostic> Diagnostics);

/// <summary>UI projection over the shared session services. Does not own a second profile or production runtime.</summary>
internal sealed class PluginManagementServices
{
    internal IPluginProfileStore ProfileStore { get; }
    internal IPluginCompilationService Compiler { get; }
    internal IPluginInstallationInventory Inventory { get; }
    internal string PluginDirectory { get; }
    internal Func<IReadOnlyList<PluginWorkerSnapshot>>? GetWorkerSnapshots { get; init; }
    internal Func<IReadOnlyList<PluginProductionWorkerSnapshot>>? GetProductionWorkerSnapshots { get; init; }
    internal Func<IReadOnlyList<MeowChan.Rules.MediaRuleScope>>? GetRuleScopes { get; init; }
    internal Func<PluginArtifact, CancellationToken, Task<PluginDescription>> DescribeTestAsync { get; }
    internal Func<PluginArtifact, PluginConfiguration, CancellationToken, Task<IPluginWorkerClient>> StartTestWorkerAsync { get; }
    internal Func<PluginDescription, string, IPluginWorkerClient, PluginOperationContext, CancellationToken, Task<object>> ProbeAsync { get; }

    internal PluginManagementServices(IPluginProfileStore profileStore, IPluginCompilationService compiler,
        IPluginInstallationInventory inventory, string pluginDirectory,
        Func<PluginArtifact, CancellationToken, Task<PluginDescription>> describeTest,
        Func<PluginArtifact, PluginConfiguration, CancellationToken, Task<IPluginWorkerClient>> startTestWorker,
        Func<PluginDescription, string, IPluginWorkerClient, PluginOperationContext, CancellationToken, Task<object>> probe)
    {
        ProfileStore = profileStore; Compiler = compiler; Inventory = inventory; PluginDirectory = pluginDirectory;
        DescribeTestAsync = describeTest; StartTestWorkerAsync = startTestWorker; ProbeAsync = probe;
    }

    internal async Task<PluginCatalogSnapshot> ReadCatalogAsync(CancellationToken cancellationToken)
    {
        PluginProfileSnapshot profile = await ProfileStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        PluginInventory inventory = await Inventory.ScanAsync(PluginDirectory, cancellationToken).ConfigureAwait(false);
        List<PluginCatalogEntry> entries = new();
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (PluginActivationSnapshot plugin in profile.Plugins)
        {
            foreach (PluginCandidateSnapshot candidate in plugin.Candidates)
            {
                PluginInstallationCandidate? installed = inventory.Candidates.FirstOrDefault(item => item.CandidateId == candidate.CandidateId);
                entries.Add(new(candidate.CandidateId, plugin.PluginId, candidate.CandidateId,
                    installed?.Kind == PluginCandidateKind.Source ? installed.Path : candidate.CandidatePath is { } candidatePath && Path.GetExtension(candidatePath).Equals(".cs", StringComparison.OrdinalIgnoreCase) ? candidatePath : null,
                    candidate.Description ?? plugin.Active?.Description ?? plugin.LastGood?.Description,
                    plugin, candidate, installed?.Diagnostics ?? Array.Empty<PluginBuildDiagnostic>(), installed?.Path));
                seen.Add(candidate.CandidateId);
            }
            if (plugin.Candidates.Count == 0)
                entries.Add(new("plugin:" + plugin.PluginId, plugin.PluginId, plugin.SelectedCandidateId, null,
                    plugin.Active?.Description ?? plugin.LastGood?.Description, plugin, null, Array.Empty<PluginBuildDiagnostic>()));
        }
        foreach (PluginInstallationCandidate candidate in inventory.Candidates.Where(item => !seen.Contains(item.CandidateId)))
            entries.Add(new(candidate.CandidateId, null, candidate.CandidateId,
                candidate.Kind == PluginCandidateKind.Source ? candidate.Path : null, null, null, null, candidate.Diagnostics, candidate.Path));
        return new(profile, entries.OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(entry => entry.Key, StringComparer.Ordinal).ToArray(), inventory.Diagnostics);
    }
}

internal sealed class PluginConfigurationDraft
{
    internal string CandidateId { get; }
    internal long ExpectedRevision { get; }
    internal PluginDescription Description { get; }
    internal PluginSettingsDraft Plugin { get; }
    internal IReadOnlyDictionary<string, PluginSettingsDraft> Sources { get; }
    private bool _migrationAcknowledged;
    internal IReadOnlyList<ValidationIssue> RemovedSettings { get; }
    internal bool NeedsMigrationAcknowledgement => RemovedSettings.Count > 0 && !_migrationAcknowledged;
    internal void AcknowledgeMigration()
    {
        _migrationAcknowledged = true; Plugin.AcknowledgeRemovedFields();
        foreach (PluginSettingsDraft draft in Sources.Values) draft.AcknowledgeRemovedFields();
    }
    internal long EditGeneration => Plugin.EditGeneration + Sources.Values.Sum(draft => draft.EditGeneration);
    internal bool HasDiscardableChanges => _migrationAcknowledged || Plugin.HasDiscardableChanges || Sources.Values.Any(draft => draft.HasDiscardableChanges);
    internal bool Dirty => _migrationAcknowledged || Plugin.Dirty || Sources.Values.Any(draft => draft.Dirty);
    internal PluginConfigurationDraft(PluginCatalogEntry entry, long revision)
    {
        CandidateId = entry.CandidateId!; ExpectedRevision = revision; Description = entry.PinnedRelease?.Description ?? entry.Description!;
        PluginConfiguration? saved = entry.EditableRelease?.Configuration;
        RemovedSettings = PluginSettingsMigration.Migrate(entry.EditableRelease, Description).Issues
            .Where(issue => issue.Code is "removed_setting" or "removed_source").ToArray();
        Plugin = new(Description.Settings, SettingScope.Plugin, saved?.PluginSettings);
        Sources = Description.Sources.ToDictionary(source => source.SourceId,
            source => new PluginSettingsDraft(source.Settings, SettingScope.Source, saved?.SourceSettings.GetValueOrDefault(source.SourceId)), StringComparer.Ordinal);
    }
    internal PluginConfiguration Configuration(out IReadOnlyList<PluginConfigurationFieldError> errors)
    {
        SettingsSnapshot plugin = Plugin.Snapshot(out IReadOnlyList<FieldValidationError> pluginErrors);
        List<PluginConfigurationFieldError> all = new(pluginErrors.Select(error => new PluginConfigurationFieldError(string.Empty, error)));
        if (NeedsMigrationAcknowledgement) all.AddRange(RemovedSettings.Select(issue => new PluginConfigurationFieldError(string.Empty, new FieldValidationError(string.Empty, issue.Code, issue.Path + ": " + issue.Message))));
        Dictionary<string, SettingsSnapshot> sources = new(StringComparer.Ordinal);
        foreach ((string source, PluginSettingsDraft draft) in Sources)
        {
            SettingsSnapshot settings = draft.Snapshot(out IReadOnlyList<FieldValidationError> sourceErrors);
            sources[source] = PluginSettingsMigration.WithDefaults(draft.Schema, settings);
            all.AddRange(sourceErrors.Select(error => new PluginConfigurationFieldError(source, error)));
        }
        errors = all;
        // The worker must validate the same effective values that the profile store persists.
        return new("draft-" + Guid.NewGuid().ToString("N"), PluginSettingsMigration.WithDefaults(Description.Settings, plugin), sources);
    }
}
