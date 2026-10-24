using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Contracts;

namespace MeowChan.Desktop.Plugins;

/// <summary>Value identities survive inventory refreshes without accepting another build at the same path.</summary>
internal sealed record PluginActionBuild(string ExecutionRevision, string CompilationFingerprint,
    string AssemblySha256, string SdkFingerprint, string CompilerFingerprint)
{
    internal static PluginActionBuild? From(PluginArtifact? artifact) => artifact == null ? null : new(
        artifact.ExecutionRevision, artifact.CompilationFingerprint, artifact.AssemblySha256,
        artifact.SdkFingerprint, artifact.CompilerFingerprint);
}

internal sealed record PluginActionRelease(string CandidateId, PluginActionBuild Build, string ConfigurationRevision)
{
    internal static PluginActionRelease? From(PluginReleaseSnapshot? release) => release == null ? null :
        new(release.CandidateId, PluginActionBuild.From(release.Artifact)!, release.Configuration.Revision);
}

/// <summary>
/// A menu/button holds an immutable intent and these identities. A changed revision requires
/// reopening; no UI retry, candidate substitution, pin release or draft rebase is implicit.
/// </summary>
internal sealed record PluginActionTarget(string Key, string? PluginId, string? CandidateId,
    long ProfileRevision, bool? Enabled, long? LifecycleRevision, string? SelectedCandidateId,
    PluginActionBuild? CandidateBuild, bool CandidatePresent, string? CandidateProblem,
    PluginActionRelease? Active, PluginActionRelease? Pending, PluginActionRelease? Pinned,
    PluginActionRelease? LastGood)
{
    internal static PluginActionTarget Capture(PluginCatalogEntry entry, long profileRevision) => new(
        entry.Key, entry.PluginId, entry.CandidateId, profileRevision,
        entry.Activation?.Enabled, entry.Activation?.LifecycleRevision, entry.Activation?.SelectedCandidateId,
        PluginActionBuild.From(entry.Candidate?.Artifact), entry.Candidate?.IsPresent == true,
        entry.Candidate?.Problem, PluginActionRelease.From(entry.Activation?.Active),
        PluginActionRelease.From(entry.Activation?.Pending), PluginActionRelease.From(entry.Activation?.Pinned),
        PluginActionRelease.From(entry.Activation?.LastGood));

    internal bool Matches(PluginCatalogEntry entry, long profileRevision) => this == Capture(entry, profileRevision);
}
