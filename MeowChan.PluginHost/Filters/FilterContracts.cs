using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Downloader;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginSdk;
using MeowChan.Rules;

namespace MeowChan.PluginHost.Filters;

public static class FilterRuleIdentity
{
    public static string ForScope(MediaRuleScope scope) => JsonSerializer.Serialize(new object?[]
        { (int)scope.Kind, scope.Source, scope.Board, scope.Thread });
}

public sealed record FilterBindingRequest(DownloadItem Item, CandidateDecision Decision, string RuleId);

/// <summary>The resolver uses the session's active worker and revisioned grants, never a second worker pool.</summary>
public sealed record FilterPluginBinding(string PluginId, string ExecutionRevision, string ConfigurationRevision,
    string GrantRevision, PluginOperationContext Operation, SettingsSnapshot RuleSettings,
    Func<bool> IsCurrent,
    Func<ImageFilterRequest, PluginOperationContext, CancellationToken, Task<ImageFilterResult>> EvaluateAsync);

public sealed record FilterStageOptions
{
    public int MaximumConcurrentChecks { get; init; } = 2;
    public long MaximumImageBytes { get; init; } = SdkLimits.MaximumImageBytes;
    public long MaximumTemporaryBytes { get; init; } = 512L * 1024 * 1024;
    public long MaximumDecodedPixels { get; init; } = SdkLimits.MaximumDecodedPixels;
    // Supply the session manager's GetTemporaryBytes method group directly to share
    // its atomic budget owner. Other callbacks retain sampled compatibility behavior.
    public Func<long> WorkerTemporaryBytes { get; init; } = () => 0;
}

public sealed record FilterStageSnapshot(int ActiveChecks, int WaitingChecks, long StagedBytes,
    long WorkerTemporaryBytes, int RecoveredStages);
