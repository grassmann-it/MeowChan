using System.Text;
using System.Text.Json;
using MeowChan.PluginHost.Compilation;

namespace MeowChan.PluginHost.Installation;

/// <summary>Bounded nonrecursive inventory. Unknown and unreadable files remain visible; nothing is loaded or activated.</summary>
public sealed class PluginInstallationInventory(int maximumCandidates = 256, int maximumSourceBytes = 4 * 1024 * 1024) : IPluginInstallationInventory
{
    public Task<PluginInventory> ScanAsync(string directory, CancellationToken cancellationToken = default) =>
        Task.Run(() => Scan(directory, cancellationToken), cancellationToken);

    private PluginInventory Scan(string directory, CancellationToken cancellationToken)
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
                        using FileStream probe = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        _ = probe.ReadByte();
                        if (kind == PluginCandidateKind.PrebuiltManifest)
                        {
                            if (size > 1024 * 1024) issues.Add(new("MCLIMIT", "Bundle manifest exceeds 1 MiB.", path));
                            else
                            {
                                probe.Position = 0;
                                PluginBundleManifest? bundle = JsonSerializer.Deserialize<PluginBundleManifest>(probe, CompilerWire.JsonOptions);
                                if (bundle is null || bundle.SchemaVersion != 1 || !CompilerWire.IsHash(bundle.AssemblySha256) ||
                                    !CompilerWire.IsHash(bundle.SdkFingerprint) || !CompilerWire.IsHash(bundle.CompilerFingerprint) || bundle.Resources is null || bundle.Resources.Count > 128)
                                    issues.Add(new("MCMANIFEST", "The bundle manifest is unsupported or incomplete.", path));
                            }
                        }
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
                { issues.Add(new("MCIO", exception.Message, path)); }
                string identityPath = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
                string candidateId = CompilerWire.Hash(Encoding.UTF8.GetBytes(identityPath));
                candidates.Add(new(candidateId, path, kind, size, issues));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        { diagnostics.Add(new("MCIO", exception.Message, fullDirectory)); }
        if (truncated) diagnostics.Add(new("MCLIMIT", $"Inventory stopped at {maximumCandidates} candidates.", fullDirectory, Severity: "warning"));
        return new(candidates, truncated, diagnostics);
    }
}
