using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace MeowChan.Desktop;

internal enum LinuxVideoPreflightErrorKind
{
    None,
    SystemBackendUnavailable,
    SystemBackendUnsupportedVersion,
    BundleDamaged,
    InvalidPackageMetadata
}

internal sealed record LinuxVideoPreflightResult(
    LinuxVideoPreflightErrorKind Kind, string? DetectedVersion = null, string? Diagnostic = null)
{
    internal bool Success => Kind == LinuxVideoPreflightErrorKind.None;
    internal string MessageKey => Kind switch
    {
        LinuxVideoPreflightErrorKind.SystemBackendUnavailable => "startup.video.systemUnavailable",
        LinuxVideoPreflightErrorKind.SystemBackendUnsupportedVersion => "startup.video.systemUnsupported",
        LinuxVideoPreflightErrorKind.BundleDamaged => "startup.video.bundleDamaged",
        LinuxVideoPreflightErrorKind.InvalidPackageMetadata => "startup.video.packageInvalid",
        _ => string.Empty
    };
}

internal static class LinuxVideoPreflight
{
    internal const string MinimumVersion = "3.0.21";

    internal static LinuxVideoPreflightResult Check() => !OperatingSystem.IsLinux()
        ? new(LinuxVideoPreflightErrorKind.None)
        : Check(AppContext.BaseDirectory, LinuxVideoRuntimePolicy.CurrentRid,
            NativeVideoRuntime.GetLinuxVersion, NativeVideoRuntime.ProbeLinuxEngine);

    // Dependencies allow policy/integrity checks to be tested without loading native code.
    internal static LinuxVideoPreflightResult Check(string directory, string rid, Func<string> version, Action engine)
    {
        LinuxVideoRuntimePolicy policy;
        try { policy = LinuxVideoRuntimePolicy.Read(directory, rid); }
        catch (Exception e) when (e is not OutOfMemoryException) { return new(LinuxVideoPreflightErrorKind.InvalidPackageMetadata, Diagnostic: e.Message); }
        try
        {
            policy.ValidateBundle();
            string detected = version();
            if (!IsSupportedVersion(detected))
                return new(policy.Bundled ? LinuxVideoPreflightErrorKind.BundleDamaged : LinuxVideoPreflightErrorKind.SystemBackendUnsupportedVersion, detected);
            engine();
            return new(LinuxVideoPreflightErrorKind.None, detected);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new(policy.Bundled ? LinuxVideoPreflightErrorKind.BundleDamaged : LinuxVideoPreflightErrorKind.SystemBackendUnavailable, Diagnostic: e.Message);
        }
    }

    internal static bool IsSupportedVersion(string? text)
    {
        string? number = text?.Split(' ', '-', '+')[0];
        return Version.TryParse(number, out var version) && version.Major == 3 && version >= new Version(MinimumVersion);
    }
}

/// <summary>Local payload integrity metadata, not a signature or authenticity guarantee.</summary>
internal sealed class LinuxVideoRuntimePolicy
{
    internal static string CurrentRid => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "linux-x64",
        Architecture.Arm64 => "linux-arm64",
        _ => throw new PlatformNotSupportedException("Linux video requires x64 or ARM64.")
    };
    internal bool Bundled { get; }
    internal string BundleRoot { get; }
    private readonly Dictionary<string, string> _files;
    private readonly string _canonicalRoot;

    private LinuxVideoRuntimePolicy(string directory, string rid, bool bundled, Dictionary<string, string> files)
    {
        Bundled = bundled;
        BundleRoot = Path.GetFullPath(Path.Combine(directory, "libvlc", rid));
        _files = files;
        _canonicalRoot = CanonicalPath(BundleRoot);
    }

    internal static LinuxVideoRuntimePolicy Read(string directory, string rid)
    {
        string marker = Path.Combine(directory, "linux-video-runtime.json");
        if (!File.Exists(marker))
        {
            if (Directory.Exists(Path.Combine(directory, "libvlc", rid)))
                throw new InvalidDataException("The Linux VLC bundle is missing its package metadata.");
            return new(directory, rid, false, new());
        }
        using var json = JsonDocument.Parse(File.ReadAllText(marker));
        var root = json.RootElement;
        if (root.GetProperty("SchemaVersion").GetInt32() != 1 ||
            root.GetProperty("RuntimeIdentifier").GetString() != rid || rid is not ("linux-x64" or "linux-arm64"))
            throw new InvalidDataException("Unsupported Linux video package schema or architecture.");
        string? mode = root.GetProperty("Mode").GetString();
        if (mode is not ("bundled" or "system")) throw new InvalidDataException("Unknown Linux video runtime mode.");
        if (mode == "system" && Directory.Exists(Path.Combine(directory, "libvlc", rid)))
            throw new InvalidDataException("The system VLC package unexpectedly contains a private runtime.");
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        if (mode == "bundled")
        {
            foreach (var item in root.GetProperty("Files").EnumerateObject())
            {
                string path = item.Name;
                string? hash = item.Value.GetString();
                if (Path.IsPathRooted(path) || path.Contains('\\') || path.Split('/').Any(p => p is "" or "." or "..") ||
                    hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit) || !files.TryAdd(path, hash))
                    throw new InvalidDataException("Invalid Linux video payload entry.");
            }
        }
        return new(directory, rid, mode == "bundled", files);
    }

    internal void ValidateBundle()
    {
        if (!Bundled) return;
        if (!_files.ContainsKey("lib/libvlc.so.5") || !_files.ContainsKey("lib/libvlccore.so.9") ||
            !_files.Keys.Any(p => p.StartsWith("lib/vlc/plugins/", StringComparison.Ordinal) && p.EndsWith(".so", StringComparison.Ordinal)))
            throw new InvalidDataException("The bundled VLC libraries or plugins are missing from the manifest.");
        EnsureConfined(BundleRoot);
        if (!Directory.Exists(Path.Combine(BundleRoot, "lib/vlc/share"))) throw new InvalidDataException("The bundled VLC data directory is missing.");
        foreach (var entry in _files)
        {
            string path = Path.Combine(BundleRoot, entry.Key);
            EnsureConfined(path);
            using var stream = File.OpenRead(path);
            string actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!actual.Equals(entry.Value, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Bundled VLC payload checksum mismatch: {entry.Key}");
        }
        InspectDirectory(BundleRoot, new HashSet<string>(StringComparer.Ordinal));
    }

    private void InspectDirectory(string directory, HashSet<string> visited)
    {
        string resolved = EnsureConfined(directory);
        if (!visited.Add(resolved)) return;
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            EnsureConfined(path);
            if (Directory.Exists(path)) InspectDirectory(path, visited);
            else if (!_files.ContainsKey(Path.GetRelativePath(BundleRoot, path).Replace(Path.DirectorySeparatorChar, '/')))
                throw new InvalidDataException("An unlisted file exists in the bundled VLC payload.");
        }
    }

    private string EnsureConfined(string path)
    {
        string relative = Path.GetRelativePath(BundleRoot, Path.GetFullPath(path));
        if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new InvalidDataException("A VLC payload path escapes its bundle.");
        string current = _canonicalRoot;
        if (relative == ".") return current;
        // Canonicalize the installation parent separately: aliases above the bundle
        // are legitimate, but every link within the bundle must stay inside it.
        foreach (string part in relative.Split(Path.DirectorySeparatorChar))
        {
            current = CanonicalPath(Path.Combine(current, part));
            if (!current.StartsWith(_canonicalRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException("A VLC payload symlink escapes its bundle.");
        }
        return current;
    }

    private static string CanonicalPath(string path, int depth = 0)
    {
        if (depth > 40) throw new InvalidDataException("A VLC payload symlink cycle was detected.");
        string full = Path.GetFullPath(path);
        string current = Path.GetPathRoot(full)!;
        foreach (string part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null)
            {
                string target = info.ResolveLinkTarget(false)?.FullName ?? throw new InvalidDataException("Broken VLC payload link.");
                current = CanonicalPath(target, depth + 1);
            }
        }
        return current;
    }
}
