namespace MeowChan.Testing;

using System;
using System.IO;

/// <summary>Physical paths for caller-owned test/demo workspaces, including system temp aliases on macOS.
/// This helper is linked only into tests and the smoke harness; export target guards stay strict.</summary>
internal static class TemporaryWorkspacePaths
{
    internal static string TempRoot => PhysicalDirectory(Path.GetTempPath());

    internal static string CreateDirectory(string prefix) =>
        PhysicalDirectory(Directory.CreateTempSubdirectory(prefix).FullName);

    internal static string PhysicalDirectory(string directory)
    {
        string full = Path.GetFullPath(directory);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException("The temporary workspace directory must exist.");
        string root = Path.GetPathRoot(full)!;
        string physical = root;
        foreach (string component in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            physical = Path.Combine(physical, component);
            FileSystemInfo? target = new DirectoryInfo(physical).ResolveLinkTarget(returnFinalTarget: true);
            if (target != null) physical = target.FullName;
        }
        return Path.TrimEndingDirectorySeparator(physical);
    }
}
