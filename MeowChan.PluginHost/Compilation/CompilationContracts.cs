using MeowChan.PluginHost.Contracts;

namespace MeowChan.PluginHost.Compilation;

public enum PluginBuildStatus
{
    Success, SourceError, ReferenceError, CompilerError, TimedOut, Cancelled, IoError, Busy, IntegrityError
}

public sealed record PluginBuildDiagnostic(string Code, string Message, string? FilePath = null,
    int? Line = null, int? Column = null, string Severity = "error");

public sealed record PluginResourceInput(string Name, string SourcePath, string RelativePath, string? Rid = null);

public sealed record PluginBuildResult(PluginBuildStatus Status, PluginArtifact? Artifact, bool CacheHit,
    string? SourceFingerprint, string? CompilationFingerprint, IReadOnlyList<PluginBuildDiagnostic> Diagnostics)
{
    public bool Succeeded => Status == PluginBuildStatus.Success && Artifact is not null;
}

/// <summary>All paths are explicit. CompilerExecutablePath must point at the bundled apphost, never PATH/dotnet.</summary>
public sealed record PluginCompilerOptions(string CompilerExecutablePath, string ToolchainDirectory, string CacheDirectory)
{
    public TimeSpan BuildTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public int MaximumSourceBytes { get; init; } = 4 * 1024 * 1024;
    public int MaximumResourceBytes { get; init; } = 32 * 1024 * 1024;
    public int MaximumTotalResourceBytes { get; init; } = 64 * 1024 * 1024;
}

public interface IPluginCompilationService
{
    Task<PluginBuildResult> BuildAsync(string sourcePath, IReadOnlyList<PluginResourceInput>? resources = null,
        CancellationToken cancellationToken = default);
    Task<PluginArtifact?> FindArtifactAsync(string executionRevision, CancellationToken cancellationToken = default);
    Task<PluginCacheCleanupResult> CleanupAsync(IReadOnlySet<string> retainedExecutionRevisions,
        IReadOnlySet<string> retainedCompilationFingerprints, TimeSpan minimumAge,
        CancellationToken cancellationToken = default);
}

public sealed record PluginCacheCleanupResult(int RemovedArtifacts, int RemovedCompilations,
    IReadOnlyList<PluginBuildDiagnostic> Diagnostics);
