using MeowChan.PluginHost.Activation;
using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.Configuration;

/// <summary>Single profile owner. Commands use the last observed profile revision; stale writes fail rather than overwrite.</summary>
public interface IPluginProfileStore
{
    event Action<PluginLifecycleChange>? LifecycleChanged;
    event Action<PluginGrantChange>? GrantChanged;
    Task<PluginProfileSnapshot> ReadAsync(CancellationToken cancellationToken = default);
    Task<PluginProfileSnapshot> ObserveCandidateAsync(PluginCandidateObservation candidate, long expectedRevision, CancellationToken cancellationToken = default);
    Task<PluginProfileSnapshot> ApplyStartupAsync(IReadOnlyList<PluginCandidateObservation> candidates, long expectedRevision, CancellationToken cancellationToken = default);
    // Defaults keep older projections/adapters source-compatible and fail closed for repair writes.
    Task<PluginProfileSnapshot> ApplyBundledStartupAsync(IReadOnlyList<PluginCandidateObservation> candidates,
        IReadOnlyList<DeliveredCandidateProvenance> delivered, IReadOnlyList<string> restrictedSourceIds,
        long expectedRevision, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Delivered provenance is not supported by this profile store.");
    Task<BundledCandidateRepairPreview> PreviewBundledRepairsAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Bundled repair is not supported by this profile store.");
    Task<PluginProfileSnapshot> ConfirmBundledRepairAsync(BundledCandidateRepair repair, long expectedRevision,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Bundled repair is not supported by this profile store.");
    Task<PluginProfileSnapshot> SelectCandidateAsync(string pluginId, string candidateId, long expectedRevision, CancellationToken cancellationToken = default);
    Task<PluginProfileSnapshot> SaveConfigurationAsync(string pluginId, string candidateId, PluginConfiguration configuration, long expectedRevision, CancellationToken cancellationToken = default);
    Task<PluginProfileSnapshot> SelectRollbackAsync(string pluginId, long expectedRevision, CancellationToken cancellationToken = default);
    Task<PluginProfileSnapshot> SetEnabledAsync(string pluginId, bool enabled, long expectedRevision, CancellationToken cancellationToken = default);
    Task<PluginProfileSnapshot> SetGrantAsync(string pluginId, string filterKey, PluginGrantScope scope, FilterDataAccess access, long expectedRevision, CancellationToken cancellationToken = default);
    Task<PluginProfileSnapshot> ReportActivationFailureAsync(string pluginId, string executionRevision, string problem, long expectedRevision, CancellationToken cancellationToken = default);
}
