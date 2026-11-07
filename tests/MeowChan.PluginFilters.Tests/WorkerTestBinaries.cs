namespace MeowChan.PluginFilters.Tests;

// Kept test-local: project files/shared build wiring are intentionally unchanged.
internal static class WorkerTestBinaries
{
    internal static string WorkerExecutable() => ResolveExecutable("MeowChan.PluginWorker", appHost: true);

    internal static string ResourceHelperDirectory() => Path.GetDirectoryName(
        ResolveExecutable(Path.Combine("tests", "MeowChan.PluginFilters.Helper"), appHost: true))!;

    private static string ResolveExecutable(string projectDirectory, bool appHost)
    {
        string assembly = typeof(WorkerTestBinaries).Assembly.Location;
        if (string.IsNullOrEmpty(assembly))
            throw new InvalidOperationException("Worker discovery requires a physical test assembly location.");

        DirectoryInfo output = new(Path.GetDirectoryName(assembly)!);
        string testProject = typeof(WorkerTestBinaries).Assembly.GetName().Name!;
        DirectoryInfo? root = output;
        while (root != null && !File.Exists(Path.Combine(root.FullName, "MeowChan.sln")))
            root = root.Parent;
        if (root == null)
            throw new InvalidOperationException($"Repository root missing above test assembly '{assembly}'.");

        string project = Path.GetFileName(projectDirectory);
        RequireFile(Path.Combine(root.FullName, "tests", testProject, testProject + ".csproj"), assembly);
        RequireFile(Path.Combine(root.FullName, projectDirectory, project + ".csproj"), assembly);

        string directory;
        // --artifacts-path <any in-repository path>: bin/<test project>/<pivot>.
        // Preserve the exact bin root and pivot; never search another build tree.
        if (output.Parent?.Name == testProject && output.Parent.Parent?.Name == "bin")
        {
            directory = Path.Combine(output.Parent.Parent.FullName, project, output.Name);
        }
        else
        {
            // Ordinary SDK output: tests/<test project>/bin/<configuration>/net10.0.
            // These test, worker and helper projects all target net10.0 only.
            string expectedBin = Path.Combine(root.FullName, "tests", testProject, "bin");
            StringComparison comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (output.Name != "net10.0" || output.Parent?.Parent == null ||
                !string.Equals(output.Parent.Parent.FullName, expectedBin, comparison))
                throw new InvalidOperationException($"Unsupported test output layout at '{assembly}'; refusing alternate worker outputs.");
            directory = Path.Combine(root.FullName, projectDirectory, "bin", output.Parent.Name, output.Name);
        }

        // Check the selected executable tree before process startup/resource enumeration.
        // Existence is not freshness: the root runner still owns the dependency rebuild.
        RequireFile(Path.Combine(directory, project + ".dll"), assembly);
        RequireFile(Path.Combine(directory, project + ".deps.json"), assembly);
        RequireFile(Path.Combine(directory, project + ".runtimeconfig.json"), assembly);
        string binary = Path.Combine(directory, project + (appHost
            ? (OperatingSystem.IsWindows() ? ".exe" : string.Empty) : ".dll"));
        RequireFile(binary, assembly);
        return binary;
    }

    private static void RequireFile(string path, string assembly)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Required current-build file '{path}' is missing for test assembly '{assembly}'. No alternate output will be used.", path);
    }
}
