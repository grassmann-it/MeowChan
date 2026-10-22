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
    Task<PluginProfileSnapshot> SelectCandidateAsync(string pluginId, string candidateId, long expectedRevision, CancellationToken cancellationToken = default);
    Task<PluginProfileSnapshot> SaveConfigurationAsync(string pluginId, string candidateId, PluginConfiguration configuration, long expectedRevision, CancellationToken cancellationToken = default);
    Task<PluginProfileSnapshot> SelectRollbackAsync(string pluginId, long expectedRevision, CancellationToken cancellationToken = default);
    Task<PluginProfileSnapshot> SetEnabledAsync(string pluginId, bool enabled, long expectedRevision, CancellationToken cancellationToken = default);
    Task<PluginProfileSnapshot> SetGrantAsync(string pluginId, string filterKey, PluginGrantScope scope, FilterDataAccess access, long expectedRevision, CancellationToken cancellationToken = default);
    Task<PluginProfileSnapshot> ReportActivationFailureAsync(string pluginId, string executionRevision, string problem, long expectedRevision, CancellationToken cancellationToken = default);
}
