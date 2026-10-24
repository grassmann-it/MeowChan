using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.Desktop.Localization;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Contracts;

namespace MeowChan.Desktop.Plugins;

internal sealed record PluginTechnicalDetail(string Key, string Label, string Value);

/// <summary>Metadata only. Never reads settings, editor arguments, probe content or the filesystem.</summary>
internal static class PluginTechnicalDetails
{
    internal static IReadOnlyList<PluginTechnicalDetail> Create(PluginCatalogEntry entry, PluginWorkerSnapshot? worker = null)
    {
        List<PluginTechnicalDetail> rows = new();
        string Value(string? value) => string.IsNullOrEmpty(value) ? Texts.T("plugins.detail.unavailable") : value;
        void Row(string key, string? value) => rows.Add(new(key, Texts.T("plugins.detail." + key), Value(value)));
        string Release(PluginReleaseSnapshot? release) => release == null ? Value(null) : string.Join(Environment.NewLine,
            release.Description.PluginVersion + " · SDK " + release.Description.SdkVersion,
            Texts.T("plugins.detail.candidate") + ": " + release.CandidateId,
            Texts.T("plugins.detail.executionRevision") + ": " + release.ExecutionRevision,
            Texts.T("plugins.detail.compilationFingerprint") + ": " + release.CompilationFingerprint,
            Texts.T("plugins.detail.configurationRevision") + ": " + release.Configuration.Revision);

        Row("id", entry.PluginId ?? entry.Description?.PluginId);
        Row("candidate", entry.CandidateId);
        // A last-known description is never evidence for this candidate's version or SDK.
        Row("version", entry.Candidate?.Description?.PluginVersion);
        Row("sdk", entry.Candidate?.Description?.SdkVersion);
        Row("path", entry.LoaderPath);
        Row("active", Release(entry.Activation?.Active));
        Row("pending", Release(entry.Activation?.Pending));
        Row("pinned", Release(entry.Activation?.Pinned));
        Row("lastGood", Release(entry.Activation?.LastGood));
        Row("executionRevision", entry.Candidate?.Artifact?.ExecutionRevision);
        Row("compilationFingerprint", entry.Candidate?.Artifact?.CompilationFingerprint);
        Row("assemblySha256", entry.Candidate?.Artifact?.AssemblySha256);
        Row("sdkFingerprint", entry.Candidate?.Artifact?.SdkFingerprint);
        Row("compilerFingerprint", entry.Candidate?.Artifact?.CompilerFingerprint);
        Row("artifactPath", entry.Candidate?.Artifact?.AssemblyPath);
        Row("activationProblem", entry.Activation?.Problem);
        Row("candidateProblem", entry.Candidate?.Problem);
        Row("worker", worker == null ? null : string.Join(Environment.NewLine,
            new[] { worker.State, worker.FailureCode, worker.FailureMessage }.Where(value => !string.IsNullOrEmpty(value))));
        return rows;
    }

    internal static string Format(IReadOnlyList<PluginTechnicalDetail> rows) =>
        string.Join(Environment.NewLine + Environment.NewLine,
            rows.Select(row => row.Label + ": " + row.Value));
}
