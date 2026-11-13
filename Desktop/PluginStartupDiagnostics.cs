using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Compilation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Installation;
using MeowChan.Diagnostics;
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

    /// <summary>
    /// The last validated description of the same candidate when its executable identity is unchanged.
    /// Describe only reproduces what that identity declares, so startup need not start one worker per candidate.
    /// Physical cache paths may differ; assembly, SDK, compiler and logical resources may not.
    /// </summary>
    internal static PluginDescription? ReusableDescription(PluginProfileSnapshot previous, string candidateId, PluginArtifact artifact)
    {
        PluginCandidateSnapshot? known = previous.Plugins.SelectMany(plugin => plugin.Candidates)
            .FirstOrDefault(candidate => candidate.CandidateId == candidateId);
        if (known is not { IsPresent: true, Problem: null, Artifact: { } prior, Description: { } description }
            || description.PluginId != known.PluginId || !SameExecutable(prior, artifact)) return null;
        return SdkValidation.ValidateDescription(description).Count == 0 ? description : null;
    }

    private static bool SameExecutable(PluginArtifact left, PluginArtifact right) =>
        left.ExecutionRevision == right.ExecutionRevision && left.CompilationFingerprint == right.CompilationFingerprint
        && left.AssemblySha256 == right.AssemblySha256 && left.SdkFingerprint == right.SdkFingerprint
        && left.CompilerFingerprint == right.CompilerFingerprint && left.Resources is not null && right.Resources is not null
        && LogicalResources(left).SequenceEqual(LogicalResources(right));

    private static IEnumerable<(string, string?, string, string)> LogicalResources(PluginArtifact artifact) =>
        artifact.Resources.Select(r => (r.Name, r.Rid, r.RelativePath, r.Sha256))
            .OrderBy(r => r.Item1, StringComparer.Ordinal).ThenBy(r => r.Item2, StringComparer.Ordinal)
            .ThenBy(r => r.Item3, StringComparer.Ordinal).ThenBy(r => r.Item4, StringComparer.Ordinal);

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
        using PerformanceDiagnostics.Measurement span = Span(phase, candidate);
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

    /// <summary>The same phase as a "plugin.startup.*" span on the report clock. Inert (no allocation) without an active run.</summary>
    internal static PerformanceDiagnostics.Measurement Span(string phase, string? candidate = null) =>
        PerformanceDiagnostics.Enabled ? PerformanceDiagnostics.Measure("plugin.startup." + phase, asset: candidate) : default;

    /// <summary>
    /// Process CPU (own threads, and reaped child processes such as describe workers) and the 1-minute load average
    /// at a phase boundary, as separate "phase.*" events. Only with an active diagnostic run; never affects startup.
    /// Children and load are Linux-only (/proc, USER_HZ 100); a failed sample is simply absent.
    /// </summary>
    internal static void RecordResources(string phase, string outcome)
    {
        if (!PerformanceDiagnostics.Enabled) return;
        try
        {
            using (Process self = Process.GetCurrentProcess())
                PerformanceDiagnostics.Record(phase + ".cpu-self-ms", outcome, value: (long)self.TotalProcessorTime.TotalMilliseconds);
            if (!OperatingSystem.IsLinux()) return;
            string stat = File.ReadAllText("/proc/self/stat");
            // Fields after the parenthesized command start at field 3 (state); cutime/cstime are fields 16/17.
            string[] fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            long children = (long.Parse(fields[13], System.Globalization.CultureInfo.InvariantCulture)
                + long.Parse(fields[14], System.Globalization.CultureInfo.InvariantCulture)) * 10;
            PerformanceDiagnostics.Record(phase + ".cpu-children-ms", outcome, value: children);
            string load = File.ReadAllText("/proc/loadavg").Split(' ')[0];
            PerformanceDiagnostics.Record(phase + ".loadavg1-milli", outcome,
                value: (long)(double.Parse(load, System.Globalization.CultureInfo.InvariantCulture) * 1000));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException
            or IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidOperationException or OverflowException) { }
    }
}
