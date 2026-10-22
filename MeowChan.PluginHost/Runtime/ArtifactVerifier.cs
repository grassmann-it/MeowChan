using System.Security.Cryptography;
using MeowChan.PluginHost.Contracts;

namespace MeowChan.PluginHost.Runtime;

internal static class ArtifactVerifier
{
    public static async Task<Dictionary<string, string>> VerifyAsync(PluginArtifact artifact, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(artifact.ExecutionRevision) || string.IsNullOrWhiteSpace(artifact.CompilationFingerprint))
            throw new PluginWorkerException("artifact", "An artifact must identify its compilation and complete execution revision.");
        string assembly = Path.GetFullPath(artifact.AssemblyPath);
        await VerifyHashAsync(assembly, artifact.AssemblySha256, token).ConfigureAwait(false);
        string root = Path.GetFullPath(artifact.ResourceRootPath ?? Path.GetDirectoryName(assembly)!);
        string prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        Dictionary<string, string> resources = new(StringComparer.Ordinal);
        foreach (PluginArtifactResource resource in artifact.Resources)
        {
            if (resource.Rid != null && resource.Rid != System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier) continue;
            if (string.IsNullOrWhiteSpace(resource.Name) || Path.IsPathRooted(resource.RelativePath))
                throw new PluginWorkerException("resource", "Invalid resource name or rooted resource path.");
            string path = Path.GetFullPath(Path.Combine(root, resource.RelativePath));
            if (!path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new PluginWorkerException("resource", "A resource path escapes its verified bundle.");
            for (string? current = path; current != null && current.Length >= root.Length; current = Path.GetDirectoryName(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new PluginWorkerException("resource", "Linked resource paths are not supported.");
            await VerifyHashAsync(path, resource.Sha256, token).ConfigureAwait(false);
            if (!resources.TryAdd(resource.Name, path)) throw new PluginWorkerException("resource", "Duplicate applicable resource name.");
        }
        return resources;
    }
    private static async Task VerifyHashAsync(string path, string expected, CancellationToken token)
    {
        if (expected.Length != 64 || !expected.All(Uri.IsHexDigit)) throw new PluginWorkerException("artifact", "Missing or malformed SHA-256.");
        await using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        string actual = Convert.ToHexString(await SHA256.HashDataAsync(file, token).ConfigureAwait(false));
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            throw new PluginWorkerException("artifact", "The plugin artifact or resource hash does not match its immutable manifest.");
    }
}
