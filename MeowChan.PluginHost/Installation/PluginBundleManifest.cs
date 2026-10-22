using MeowChan.PluginHost.Compilation;
using MeowChan.PluginHost.Contracts;

namespace MeowChan.PluginHost.Installation;

/// <summary>Shipped .plugin.json contract. Paths are relative to this manifest; each resource is copied and verified.</summary>
public sealed record PluginBundleManifest(int SchemaVersion, string AssemblyRelativePath, string AssemblySha256,
    string SdkFingerprint, string CompilerFingerprint, IReadOnlyList<PluginArtifactResource> Resources);

public interface IPluginBundleInstaller
{
    Task<PluginBuildResult> ImportBundleAsync(string manifestPath, CancellationToken cancellationToken = default);
}
