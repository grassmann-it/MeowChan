namespace MeowChan.PluginHost.Contracts;

public sealed record PluginArtifactResource(string Name, string RelativePath, string Sha256, string? Rid = null);

/// <summary>An immutable, verified compilation and resource revision. Never activates itself.</summary>
public sealed record PluginArtifact(string CompilationFingerprint, string ExecutionRevision,
    string AssemblyPath, string AssemblySha256, string SdkFingerprint, string CompilerFingerprint,
    IReadOnlyList<PluginArtifactResource> Resources, string? ResourceRootPath = null);
