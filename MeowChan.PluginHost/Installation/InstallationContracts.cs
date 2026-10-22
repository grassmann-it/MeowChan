using MeowChan.PluginHost.Compilation;

namespace MeowChan.PluginHost.Installation;

public enum PluginCandidateKind { Source, PrebuiltManifest, Unsupported }
public sealed record PluginInstallationCandidate(string CandidateId, string Path, PluginCandidateKind Kind,
    long? Size, IReadOnlyList<PluginBuildDiagnostic> Diagnostics);
public sealed record PluginInventory(IReadOnlyList<PluginInstallationCandidate> Candidates, bool Truncated,
    IReadOnlyList<PluginBuildDiagnostic> Diagnostics);

public interface IPluginInstallationInventory
{
    Task<PluginInventory> ScanAsync(string directory, CancellationToken cancellationToken = default);
}
