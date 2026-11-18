using MeowChan.PluginHost.Compilation;
using MeowChan.PluginHost.Contracts;

namespace MeowChan.PluginHost.Installation;

/// <summary>Shipped .plugin.json contract. Paths are relative to this manifest; each resource is copied and verified.</summary>
public sealed record PluginBundleManifest(int SchemaVersion, string AssemblyRelativePath, string AssemblySha256,
    string SdkFingerprint, string CompilerFingerprint, IReadOnlyList<PluginArtifactResource> Resources);

public interface IPluginBundleInstaller
{
    Task<PluginBuildResult> ImportBundleAsync(string manifestPath, CancellationToken cancellationToken = default);

    /// <summary>One pass over many bundles (host startup). The batch starts verifying the delivered toolchain at once and
    /// shares that one verification with its imports instead of hashing it once per bundle; each import still checks that
    /// no verified toolchain file changed before it uses the result, and every bundle and cached copy is still verified
    /// byte for byte on its own. Disposing the batch ends the pass and cancels a verification still running.</summary>
    IPluginBundleImportBatch BeginImportBatch();
}

public interface IPluginBundleImportBatch : IDisposable
{
    Task<PluginBuildResult> ImportBundleAsync(string manifestPath, CancellationToken cancellationToken = default);
}
