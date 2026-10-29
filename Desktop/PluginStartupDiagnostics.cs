using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Compilation;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Installation;
using MeowChan.PluginSdk;
using Serilog;

namespace MeowChan.Desktop;

/// <summary>Startup candidate probing and phase evidence only; does not choose or activate releases.</summary>
internal static class PluginStartupDiagnostics
{
    internal static async Task<PluginCandidateObservation> ObserveAsync(PluginInstallationCandidate candidate,
        PluginActivationSnapshot? previous, Func<CancellationToken, Task<PluginBuildResult>> load,
        Func<PluginArtifact, CancellationToken, Task<PluginDescription>> describe, ILogger logger, CancellationToken token)
    {
        string phase = "candidate-validation";
        string name = Path.GetFileName(candidate.Path);
        PluginBuildResult? buildResult = null;
        try
        {
            token.ThrowIfCancellationRequested();
            if (candidate.Diagnostics.Count != 0)
                throw new InvalidDataException(string.Join("; ", candidate.Diagnostics.Select(item => item.Message)));
            phase = candidate.Kind == PluginCandidateKind.PrebuiltManifest ? "import" : "build";
            PluginBuildResult build = await MeasureAsync(logger, phase, name, async () =>
            {
                PluginBuildResult result = await load(token).ConfigureAwait(false);
                buildResult = result;
                // A completed compiler/import request is not necessarily a successful build.
                if (!result.Succeeded) throw new InvalidDataException(string.Join("; ", result.Diagnostics.Select(item => item.Message)));
                return result;
            }).ConfigureAwait(false);
            phase = "describe";
            PluginDescription description = await MeasureAsync(logger, phase, name,
                () => describe(build.Artifact!, token)).ConfigureAwait(false);
            return new(candidate.CandidateId, description.PluginId, build.Artifact, description, CandidatePath: candidate.Path);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            // Preserve the existing UI/profile problem text; structured evidence supplies context.
            string problem = name + ": " + exception.Message;
            logger.Warning(exception,
                "Plugin startup candidate failed in {PluginStartupPhase} candidate {PluginStartupCandidate} id {PluginStartupCandidateId} plugin {PluginStartupPluginId} previously declared sources {PluginStartupSourceIds} error {PluginStartupErrorType} worker code {PluginStartupWorkerCode} build status {PluginStartupBuildStatus} diagnostic codes {PluginStartupDiagnosticCodes}",
                phase, name, candidate.CandidateId, previous?.PluginId,
                (previous?.Active?.Description ?? previous?.LastGood?.Description)?.Sources.Select(source => source.SourceId).ToArray() ?? Array.Empty<string>(),
                exception.GetType().FullName, (exception as PluginWorkerException)?.Code, buildResult?.Status.ToString(),
                (buildResult?.Diagnostics ?? candidate.Diagnostics).Select(item => item.Code).ToArray());
            return new(candidate.CandidateId, previous?.PluginId ?? "unavailable-" + candidate.CandidateId,
                null, null, problem, candidate.Path);
        }
    }

    internal static void LogActivationProblem(ILogger logger, PluginActivationSnapshot activation)
    {
        if (activation.Problem is null) return;
        logger.ForContext("PluginStartupPhase", "activation").Warning("Plugin startup activation advisory candidate {PluginStartupCandidateId} plugin {PluginStartupPluginId} declared sources {PluginStartupSourceIds} activation available {PluginStartupActivationAvailable}: {PluginStartupProblem}",
            activation.SelectedCandidateId, activation.PluginId,
            (activation.Active?.Description ?? activation.LastGood?.Description)?.Sources.Select(source => source.SourceId).ToArray() ?? Array.Empty<string>(),
            activation.Enabled && activation.Active is not null, activation.Problem);
    }

    internal static async Task<T> MeasureAsync<T>(ILogger logger, string phase, string? candidate, Func<Task<T>> operation)
    {
        long started = Stopwatch.GetTimestamp();
        bool succeeded = false;
        try
        {
            T result = await operation().ConfigureAwait(false);
            succeeded = true;
            return result;
        }
        finally { LogPhase(logger, phase, candidate, started, succeeded); }
    }

    internal static void LogPhase(ILogger logger, string phase, string? candidate, long started, bool succeeded) =>
        logger.Debug("Plugin startup phase {PluginStartupPhase} candidate {PluginStartupCandidate} elapsed {PluginStartupElapsedMs} ms completed {PluginStartupCompleted}",
            phase, candidate, Stopwatch.GetElapsedTime(started).TotalMilliseconds, succeeded);
}
