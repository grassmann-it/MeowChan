using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Http;
using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.Sources;

/// <summary>Internal final-result projection, not another retry or SDK contract.</summary>
internal static class PluginSourceDiagnostics
{
    internal static Exception FinalFailure(PluginHostHttp.MetadataHistory history, FailureDetail final)
    {
        FailureInfo? host = history.MatchFinalFailure(final);
        if (host == null) return PluginWorkerException.FromFailure(final); // Existing SDK-only message contract.
        // Exact code/message/status echo identifies the returned HostHttpFailure. A
        // reported SDK retry may be added without reclassifying the typed host facts.
        return new CorrelatedFailure(host with { DiagnosticCode = final.Code, RetryAt = final.RetryAt });
    }

    internal static Exception? DeferredFailure(PluginHostHttp.MetadataHistory history,
        string reason, int? status, DateTimeOffset reportedRetry)
    {
        // Keep the existing post-failure-deferred path and neutral initial waits.
        if (history.Failure != null) return history.Failure;
        if (history.InitialNoSend || status == null) return null;
        // SDK Identity validation is not a privacy boundary. Even an alphanumeric
        // token may be a secret: publish only the finite existing policy vocabulary.
        string code = reason is "admission" or "cooldown" ? reason : "source_check_deferred";
        return PluginWorkerException.FromFailure(new FailureDetail(code,
            "Source check deferred with a reported HTTP status.", status, reportedRetry));
    }

    private sealed class CorrelatedFailure(FailureInfo facts) : Exception(facts.Message), IFailureInfoProvider
    {
        public FailureInfo FailureInfo { get; } = facts;
    }
}
