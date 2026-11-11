using MeowChan.PluginHost.Compilation;
using MeowChan.PluginHost.Contracts;

namespace MeowChan.PluginHost.Installation;

/// <summary>Shipped .plugin.json contract. Paths are relative to this manifest; each resource is copied and verified.</summary>
public sealed record PluginBundleManifest(int SchemaVersion, string AssemblyRelativePath, string AssemblySha256,
    string SdkFingerprint, string CompilerFingerprint, IReadOnlyList<PluginArtifactResource> Resources);

public interface IPluginBundleInstaller
{
    Task<PluginBuildResult> ImportBundleAsync(string manifestPath, CancellationToken cancellationToken = default);

    /// <summary>One pass over many bundles (host startup). Imports through the batch verify the delivered toolchain once
    /// instead of once per bundle; every bundle and cached copy is still verified byte for byte on its own.</summary>
    IPluginBundleImportBatch BeginImportBatch();
}

public interface IPluginBundleImportBatch : IDisposable
{
    Task<PluginBuildResult> ImportBundleAsync(string manifestPath, CancellationToken cancellationToken = default);
}
