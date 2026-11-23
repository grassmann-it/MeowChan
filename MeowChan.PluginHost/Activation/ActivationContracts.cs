using System.Text.Json.Serialization;
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

/// <summary>Durable, exact evidence that the active build crossed the one-time SDK bridge automatically.
/// The optional explicit review acknowledges this exact execution without changing code or grants.
/// </summary>
public sealed record PluginSdkTransitionSnapshot(string FromSdkVersion, string ToSdkVersion,
    string ExecutionRevision, bool ExplicitlyApproved = false);

public sealed record PluginActivationSnapshot(string PluginId, bool Enabled, long LifecycleRevision,
    string? SelectedCandidateId, PluginReleaseSnapshot? Active, PluginReleaseSnapshot? Pending,
    PluginReleaseSnapshot? LastGood, PluginReleaseSnapshot? Pinned,
    IReadOnlyList<PluginCandidateSnapshot> Candidates, string? Problem)
{
    /// <summary>Set only for the exact active build that was automatically accepted via the
    /// supported SDK 1.0.0 to 1.1.0 transition. It is not an executable or permission grant.
    /// </summary>
    public PluginSdkTransitionSnapshot? AutomaticSdkTransition { get; init; }

    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal PluginCandidateSnapshot? ExplicitlySelectedBuild { get; init; }

    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal PluginReleaseSnapshot? InstalledBuildDraft { get; init; }

    // Retain the original executable while a first local install still has no validated configuration.
    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal PluginArtifact? UnconfiguredInstalledBuild { get; init; }
}

public sealed record PluginLifecycleChange(string PluginId, bool Enabled, long LifecycleRevision);
