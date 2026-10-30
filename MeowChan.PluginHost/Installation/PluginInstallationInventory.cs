using System.Text;
using System.Text.Json;
using MeowChan.PluginHost.Compilation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.Installation;

/// <summary>Bounded nonrecursive inventory. Unknown and unreadable files remain visible; nothing is loaded or activated.</summary>
public sealed class PluginInstallationInventory(int maximumCandidates = 256, int maximumSourceBytes = 4 * 1024 * 1024) : IPluginInstallationInventory
{
    public Task<PluginInventory> ScanAsync(string directory, CancellationToken cancellationToken = default) =>
        Task.Run(() => ScanAsyncCore(directory, cancellationToken), cancellationToken);

    /// <summary>Called only for the host's shipped inventory after successful import/description.
    /// A local filename or a plugin ID can never establish delivered provenance.</summary>
    public static async Task<DeliveredCandidateProvenance> RecordDeliveredAsync(string shippedDirectory,
        PluginInstallationCandidate candidate, PluginArtifact artifact, PluginDescription description,
        CancellationToken cancellationToken = default)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(shippedDirectory));
        string fullPath = Path.GetFullPath(candidate.Path);
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (candidate.Kind != PluginCandidateKind.PrebuiltManifest || candidate.Diagnostics.Count != 0 ||
            !string.Equals(Path.GetDirectoryName(fullPath), root, comparison) ||
            !string.Equals(CompilerWire.ResolvePath(root, Path.GetFileName(fullPath)), fullPath, comparison) ||
            candidate.CandidateId != CompilerWire.Hash(Encoding.UTF8.GetBytes(OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath)))
            throw new InvalidDataException("Candidate is not an exact validated member of the shipped inventory.");
        byte[] bytes = await CompilerWire.ReadBoundedAsync(fullPath, 1024 * 1024, cancellationToken).ConfigureAwait(false);
        PluginBundleManifest manifest = JsonSerializer.Deserialize<PluginBundleManifest>(bytes, CompilerWire.JsonOptions)
            ?? throw new InvalidDataException("Missing delivered manifest.");
        if (manifest.SchemaVersion != 1 || manifest.Resources is null || manifest.Resources.Count > 128 ||
            manifest.AssemblySha256 != artifact.AssemblySha256 || manifest.SdkFingerprint != artifact.SdkFingerprint ||
            manifest.CompilerFingerprint != artifact.CompilerFingerprint || manifest.Resources.Count != artifact.Resources.Count ||
            manifest.Resources.Any(r => !artifact.Resources.Contains(r)))
            throw new InvalidDataException("Delivered manifest changed since import.");
        string compilation = CompilerWire.HashParts("prebuilt-v1", manifest.AssemblySha256, manifest.SdkFingerprint, manifest.CompilerFingerprint);
        string execution = CompilerWire.HashParts(new[] { "artifact-v1", compilation }.Concat(manifest.Resources
            .OrderBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.Rid, StringComparer.Ordinal)
            .SelectMany(r => new[] { r.Name, r.RelativePath, r.Sha256, r.Rid ?? "" })).ToArray());
        if (artifact.CompilationFingerprint != compilation || artifact.ExecutionRevision != execution)
            throw new InvalidDataException("Delivered execution identity does not match the manifest.");
        if (CompilerWire.Hash(await CompilerWire.ReadBoundedAsync(CompilerWire.ResolvePath(root, manifest.AssemblyRelativePath),
                32 * 1024 * 1024, cancellationToken).ConfigureAwait(false)) != artifact.AssemblySha256)
            throw new InvalidDataException("Delivered assembly changed since import.");
        int remainingResourceBytes = 32 * 1024 * 1024;
        foreach (PluginArtifactResource resource in manifest.Resources)
        {
            byte[] resourceBytes = await CompilerWire.ReadBoundedAsync(CompilerWire.ResolvePath(root, resource.RelativePath),
                remainingResourceBytes, cancellationToken).ConfigureAwait(false);
            remainingResourceBytes -= resourceBytes.Length;
            if (CompilerWire.Hash(resourceBytes) != resource.Sha256) throw new InvalidDataException("Delivered resource changed since import.");
        }
        return new(1, BundledCandidateMigration.Slot(Path.GetFileName(fullPath)), candidate.CandidateId,
            description.PluginId, artifact.ExecutionRevision, fullPath, CompilerWire.Hash(bytes),
            BundledCandidateMigration.DescriptionHash(description), artifact.AssemblySha256,
            artifact.SdkFingerprint, artifact.CompilerFingerprint);
    }

    internal static async Task VerifyRepairTargetAsync(DeliveredCandidateProvenance proof,
        PluginArtifact artifact, PluginDescription description, CancellationToken token)
    {
        DeliveredCandidateProvenance current = await RecordDeliveredAsync(Path.GetDirectoryName(proof.CandidatePath)!,
            new(proof.CandidateId, proof.CandidatePath, PluginCandidateKind.PrebuiltManifest, null, []),
            artifact, description, token).ConfigureAwait(false);
        if (current != proof) throw new InvalidDataException("Delivered repair target changed; rescan before confirming.");
        string cacheRoot = Path.GetDirectoryName(artifact.AssemblyPath)!;
        string assembly = CompilerWire.ResolvePath(cacheRoot, Path.GetFileName(artifact.AssemblyPath));
        if (CompilerWire.Hash(await CompilerWire.ReadBoundedAsync(assembly, 32 * 1024 * 1024, token).ConfigureAwait(false)) != artifact.AssemblySha256)
            throw new InvalidDataException("Cached repair assembly failed integrity validation.");
        int remainingResourceBytes = 32 * 1024 * 1024;
        foreach (PluginArtifactResource resource in artifact.Resources)
        {
            byte[] resourceBytes = await CompilerWire.ReadBoundedAsync(CompilerWire.ResolvePath(artifact.ResourceRootPath ?? cacheRoot,
                resource.RelativePath), remainingResourceBytes, token).ConfigureAwait(false);
            remainingResourceBytes -= resourceBytes.Length;
            if (CompilerWire.Hash(resourceBytes) != resource.Sha256) throw new InvalidDataException("Cached repair resource failed integrity validation.");
        }
    }

    private async Task<PluginInventory> ScanAsyncCore(string directory, CancellationToken cancellationToken)
    {
        if (maximumCandidates is < 1 or > 4096 || maximumSourceBytes is < 1 or > 4 * 1024 * 1024) throw new ArgumentOutOfRangeException();
        List<PluginInstallationCandidate> candidates = new();
        List<PluginBuildDiagnostic> diagnostics = new();
        bool truncated = false;
        string fullDirectory = Path.GetFullPath(directory);
        try
        {
            if (!Directory.Exists(fullDirectory)) return new(candidates, false, [new("MCMISSING", "The plugin directory does not exist.", fullDirectory)]);
            foreach (string path in Directory.EnumerateFileSystemEntries(fullDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (candidates.Count == maximumCandidates) { truncated = true; break; }
                List<PluginBuildDiagnostic> issues = new();
                PluginCandidateKind kind = Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase) ? PluginCandidateKind.Source :
                    Path.GetFileName(path).EndsWith(".plugin.json", StringComparison.OrdinalIgnoreCase) ? PluginCandidateKind.PrebuiltManifest : PluginCandidateKind.Unsupported;
                long? size = null;
                try
                {
                    FileAttributes attributes = File.GetAttributes(path);
                    if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                        issues.Add(new("MCENTRY", "Symbolic links and directories are not plugin source candidates.", path));
                    else
                    {
                        size = new FileInfo(path).Length;
                        if (kind == PluginCandidateKind.Source && size == 0) issues.Add(new("MCEMPTY", "The source file is empty.", path));
                        if (kind == PluginCandidateKind.Source && size > maximumSourceBytes) issues.Add(new("MCLIMIT", $"Source exceeds {maximumSourceBytes} bytes.", path));
                        if (kind == PluginCandidateKind.Unsupported) issues.Add(new("MCUNSUPPORTED", "Expected one .cs source file or a delivered .plugin.json bundle manifest.", path));
                        using (Microsoft.Win32.SafeHandles.SafeFileHandle probe = CompilerWire.OpenRead(path))
                        {
                            _ = RandomAccess.Read(probe, Span<byte>.Empty, 0);
                        }
                        if (kind == PluginCandidateKind.PrebuiltManifest)
                        {
                            if (size > 1024 * 1024) issues.Add(new("MCLIMIT", "Bundle manifest exceeds 1 MiB.", path));
                            else
                            {
                                byte[] bytes = await CompilerWire.ReadBoundedAsync(path, 1024 * 1024, cancellationToken).ConfigureAwait(false);
                                PluginBundleManifest? bundle = JsonSerializer.Deserialize<PluginBundleManifest>(bytes, CompilerWire.JsonOptions);
                                if (bundle is null || bundle.SchemaVersion != 1 || !CompilerWire.IsHash(bundle.AssemblySha256) ||
                                    !CompilerWire.IsHash(bundle.SdkFingerprint) || !CompilerWire.IsHash(bundle.CompilerFingerprint) || bundle.Resources is null || bundle.Resources.Count > 128)
                                    issues.Add(new("MCMANIFEST", "The bundle manifest is unsupported or incomplete.", path));
                            }
                        }
                    }
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or NotSupportedException)
                { issues.Add(new("MCIO", exception.Message, path)); }
                string identityPath = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
                string candidateId = CompilerWire.Hash(Encoding.UTF8.GetBytes(identityPath));
                candidates.Add(new(candidateId, path, kind, size, issues));
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or NotSupportedException)
        { diagnostics.Add(new("MCIO", exception.Message, fullDirectory)); }
        if (truncated) diagnostics.Add(new("MCLIMIT", $"Inventory stopped at {maximumCandidates} candidates.", fullDirectory, Severity: "warning"));
        return new(candidates, truncated, diagnostics);
    }
}
