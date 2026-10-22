using MeowChan.PluginHost.Contracts;
using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.Activation;

/// <summary>One observation from a consistent startup scan, or an isolated test build. An observation never activates code.</summary>
public sealed record PluginCandidateObservation(string CandidateId, string PluginId,
    PluginArtifact? Artifact, PluginDescription? Description, string? Problem = null, string? CandidatePath = null);

public sealed record PluginCandidateSnapshot(string CandidateId, string PluginId,
    PluginArtifact? Artifact, PluginDescription? Description, bool IsPresent, string? Problem, string? CandidatePath = null);

/// <summary>A build and its matching complete, validated settings are always selected together.</summary>
public sealed record PluginReleaseSnapshot(string CandidateId, PluginArtifact Artifact,
    PluginDescription Description, PluginConfiguration Configuration)
{
    public string ExecutionRevision => Artifact.ExecutionRevision;
    public string CompilationFingerprint => Artifact.CompilationFingerprint;
}

public sealed record PluginActivationSnapshot(string PluginId, bool Enabled, long LifecycleRevision,
    string? SelectedCandidateId, PluginReleaseSnapshot? Active, PluginReleaseSnapshot? Pending,
    PluginReleaseSnapshot? LastGood, PluginReleaseSnapshot? Pinned,
    IReadOnlyList<PluginCandidateSnapshot> Candidates, string? Problem);

public sealed record PluginLifecycleChange(string PluginId, bool Enabled, long LifecycleRevision);
