using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MeowChan.PluginHost.Compilation;

internal sealed record ToolchainFile(string Path, string Sha256, string Role);
internal sealed record ToolchainManifest(int SchemaVersion, string TargetFramework, string Options,
    string CompilerFingerprint, string SdkFingerprint, string ReferenceFingerprint, ToolchainFile[] Files);
internal sealed record CompilerRequest(byte[] Source, string OriginalPath, string CompilationFingerprint,
    string ToolchainFingerprint, string OutputPath);
internal sealed record CompilerResponse(bool Success, string? FailureKind, CompilerWireDiagnostic[] Diagnostics);
internal sealed record CompilerWireDiagnostic(string Code, string Message, string? FilePath, int? Line, int? Column, string Severity);

internal static class CompilerWire
{
    internal const int MaximumSourceBytes = 4 * 1024 * 1024;
    internal const int MaximumRequestBytes = 8 * 1024 * 1024;
    internal const int MaximumResponseBytes = 1024 * 1024;
    internal const string Options = "net10.0;csharp14;release;deterministic;nullable=enable;unsafe=false;generators=none;v1";
    internal static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = false };
    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static string HashParts(params string[] parts)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string part in parts)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(part);
            hash.AppendData(BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(bytes.Length)));
            hash.AppendData(bytes);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    internal static bool IsHash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static string FileSetHash(IEnumerable<ToolchainFile> files) => HashParts(files.OrderBy(f => f.Path, StringComparer.Ordinal)
        .SelectMany(f => new[] { f.Role, f.Path, f.Sha256 }).ToArray());
    internal static string ToolchainHash(ToolchainManifest manifest) => HashParts(manifest.TargetFramework, manifest.Options,
        manifest.CompilerFingerprint, manifest.SdkFingerprint, manifest.ReferenceFingerprint);
    internal static string ResolvePath(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Contains('\\') || relativePath.Contains(':') ||
            relativePath.Split('/').Any(p => p is "" or "." or ".."))
            throw new InvalidDataException("A manifest path must be a normalized relative path.");
        string fullRoot = Path.GetFullPath(root);
        string result = fullRoot;
        // Reject symlink components as well as lexical traversal. The install/cache tree is never a symlink shortcut.
        if (Directory.Exists(fullRoot) && (File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The manifest root cannot be a symbolic link.");
        foreach (string component in relativePath.Split('/'))
        {
            result = Path.Combine(result, component);
            if ((File.Exists(result) || Directory.Exists(result)) && (File.GetAttributes(result) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("A manifest path cannot traverse symbolic links.");
        }
        return result;
    }
    internal static async Task<byte[]> ReadBoundedAsync(string path, int maximum, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (stream.Length > maximum) throw new InvalidDataException($"File exceeds {maximum} bytes: {path}");
        using MemoryStream output = new();
        byte[] buffer = new byte[65536];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (output.Length + count > maximum) throw new InvalidDataException($"File exceeds {maximum} bytes: {path}");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
    internal static async Task<ToolchainManifest> ReadToolchainAsync(string directory, CancellationToken cancellationToken)
    {
        byte[] json = await ReadBoundedAsync(ResolvePath(directory, "toolchain.json"), MaximumResponseBytes, cancellationToken);
        ToolchainManifest manifest = JsonSerializer.Deserialize<ToolchainManifest>(json, JsonOptions)
            ?? throw new InvalidDataException("Missing toolchain manifest.");
        if (manifest.SchemaVersion != 1 || manifest.TargetFramework != "net10.0" || manifest.Options != Options ||
            manifest.Files is null || manifest.Files.Length is < 4 or > 512 ||
            manifest.Files.Any(f => f is null || !IsHash(f.Sha256) || f.Role is not ("compiler" or "framework" or "sdk")) ||
            manifest.Files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Length ||
            manifest.Files.Count(f => f.Role == "sdk") != 1 || manifest.Files.Count(f => f.Role == "compiler") < 3 ||
            manifest.Files.Count(f => f.Role == "framework") < 1)
            throw new InvalidDataException("Unsupported or incomplete compiler toolchain manifest.");
        long total = 0;
        foreach (ToolchainFile file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes = await ReadBoundedAsync(ResolvePath(directory, file.Path), 64 * 1024 * 1024, cancellationToken);
            total += bytes.Length;
            if (total > 256 * 1024 * 1024 || Hash(bytes) != file.Sha256)
                throw new InvalidDataException($"Compiler toolchain integrity check failed: {file.Path}");
        }
        if (FileSetHash(manifest.Files.Where(f => f.Role == "compiler")) != manifest.CompilerFingerprint ||
            FileSetHash(manifest.Files.Where(f => f.Role != "compiler")) != manifest.ReferenceFingerprint ||
            manifest.Files.Single(f => f.Role == "sdk").Sha256 != manifest.SdkFingerprint)
            throw new InvalidDataException("Compiler toolchain fingerprint mismatch.");
        return manifest;
    }
}
