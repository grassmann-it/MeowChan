using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MeowChan.PluginHost.Compilation;
using MeowChan.PluginHost.Installation;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace MeowChan.PluginCompiler.Tests;

public sealed class CompilerIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "meowchan compiler tests " + Guid.NewGuid().ToString("N"));
    private readonly string _bundle;
    private string Executable => Path.Combine(_bundle, OperatingSystem.IsWindows() ? "MeowChan.PluginCompiler.exe" : "MeowChan.PluginCompiler");
    private string Cache => Path.Combine(_root, "cache with spaces");
    private const string ValidSource = "public sealed class LocalPlugin { public int Value => 1; }";

    public CompilerIntegrationTests()
    {
        Directory.CreateDirectory(_root);
        _bundle = Environment.GetEnvironmentVariable("MEOWCHAN_COMPILER_TEST_BUNDLE") ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../MeowChan.PluginCompiler/bin/Release/net10.0"));
        Assert.True(File.Exists(Executable), "A built/published bundled compiler apphost is required: " + Executable);
    }
    private PluginCompilationService Service(string? executable = null, TimeSpan? timeout = null, string? bundle = null) =>
        new(new PluginCompilerOptions(executable ?? Executable, bundle ?? _bundle, Cache) { BuildTimeout = timeout ?? TimeSpan.FromSeconds(60) });
    private async Task<string> SourceAsync(string text = ValidSource)
    {
        string path = Path.Combine(_root, "editable plugin.cs");
        await File.WriteAllTextAsync(path, text);
        return path;
    }
    private static void Success(PluginBuildResult result) => Assert.True(result.Succeeded,
        result.Status + ": " + string.Join("; ", result.Diagnostics.Select(d => d.Code + " " + d.Message)));

    [Fact]
    public async Task ByteChangesInvalidateButTimestampAndRestartDoNot()
    {
        string path = await SourceAsync();
        PluginBuildResult first = await Service().BuildAsync(path);
        Success(first);
        Assert.False(first.CacheHit);
        DateTime stamp = File.GetLastWriteTimeUtc(path);
        File.SetLastWriteTimeUtc(path, stamp.AddDays(2));
        PluginBuildResult second = await Service().BuildAsync(path);
        Success(second);
        Assert.True(second.CacheHit);
        Assert.Equal(first.CompilationFingerprint, second.CompilationFingerprint);
        await File.WriteAllTextAsync(path, ValidSource.Replace("=> 1", "=> 2"));
        File.SetLastWriteTimeUtc(path, stamp.AddDays(2));
        PluginBuildResult changed = await Service().BuildAsync(path);
        Success(changed);
        Assert.False(changed.CacheHit);
        Assert.NotEqual(first.CompilationFingerprint, changed.CompilationFingerprint);
        Assert.NotNull(await Service().FindArtifactAsync(first.Artifact!.ExecutionRevision));
    }

    [Fact]
    public async Task SyntaxDiagnosticsRetainOriginalSpacedPathLineAndSnapshotHash()
    {
        string path = await SourceAsync("public class Broken\n{\n    public int Value => ;\n}\n");
        PluginBuildResult result = await Service().BuildAsync(path);
        Assert.Equal(PluginBuildStatus.SourceError, result.Status);
        Assert.Equal(Hash(await File.ReadAllBytesAsync(path)), result.SourceFingerprint);
        Assert.Contains(result.Diagnostics, d => d.FilePath == path && d.Line == 3 && d.Column > 0 && d.Code.StartsWith("CS", StringComparison.Ordinal));
        Assert.Empty(Directory.GetDirectories(Path.Combine(Cache, "artifacts")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(Cache, "staging")));
    }

    [Fact]
    public async Task CompilerNeverExecutesPluginModuleInitializers()
    {
        string marker = Path.Combine(_root, "must not exist");
        string path = await SourceAsync("using System.Runtime.CompilerServices; public static class Danger { [ModuleInitializer] public static void Start() => System.IO.File.WriteAllText(" +
            System.Text.Json.JsonSerializer.Serialize(marker) + ", \"executed\"); }");
        Success(await Service().BuildAsync(path));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task ResourceChangesReuseCompilationAndEveryResourceIsVerified()
    {
        string path = await SourceAsync();
        string resource = Path.Combine(_root, "data.bin");
        await File.WriteAllBytesAsync(resource, [1, 2, 3]);
        PluginResourceInput[] inputs = [new("dictionary", resource, "resources/data.bin")];
        PluginBuildResult first = await Service().BuildAsync(path, inputs);
        Success(first);
        await File.WriteAllBytesAsync(resource, [4, 5, 6]);
        PluginBuildResult second = await Service().BuildAsync(path, inputs);
        Success(second);
        Assert.True(second.CacheHit);
        Assert.Equal(first.CompilationFingerprint, second.CompilationFingerprint);
        Assert.NotEqual(first.Artifact!.ExecutionRevision, second.Artifact!.ExecutionRevision);
        await File.WriteAllBytesAsync(Path.Combine(second.Artifact.ResourceRootPath!, "resources/data.bin"), [7, 8, 9]);
        Assert.Null(await Service().FindArtifactAsync(second.Artifact.ExecutionRevision));
        Assert.NotNull(await Service().FindArtifactAsync(first.Artifact.ExecutionRevision));
        PluginBuildResult traversal = await Service().BuildAsync(path, [new("escape", resource, "../escape.bin")]);
        Assert.Equal(PluginBuildStatus.IntegrityError, traversal.Status);
    }

    [Fact]
    public async Task CorruptDllAndMetadataDoNotBecomeSuccessfulCandidates()
    {
        string path = await SourceAsync();
        PluginBuildResult first = await Service().BuildAsync(path);
        Success(first);
        await File.WriteAllBytesAsync(first.Artifact!.AssemblyPath, [1, 2, 3]);
        PluginBuildResult corrupted = await Service().BuildAsync(path);
        Assert.Equal(PluginBuildStatus.IntegrityError, corrupted.Status);
        Assert.Null(await Service().FindArtifactAsync(first.Artifact.ExecutionRevision));
        // A separate new source remains usable while the corrupt active/pinned directory remains untouched.
        await File.WriteAllTextAsync(path, ValidSource + "\n// changed\n");
        PluginBuildResult next = await Service().BuildAsync(path);
        Success(next);
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(next.Artifact!.AssemblyPath)!, "artifact.json"), "{");
        Assert.Null(await Service().FindArtifactAsync(next.Artifact.ExecutionRevision));
        Assert.True(File.Exists(first.Artifact.AssemblyPath));
    }

    [Fact]
    public async Task CleanupPreservesActivePinnedAndCompilationProtectedRevisions()
    {
        string path = await SourceAsync();
        PluginBuildResult active = await Service().BuildAsync(path);
        await File.WriteAllTextAsync(path, ValidSource + "\n// pinned");
        PluginBuildResult pinned = await Service().BuildAsync(path);
        await File.WriteAllTextAsync(path, ValidSource + "\n// disposable");
        PluginBuildResult disposable = await Service().BuildAsync(path);
        Success(active); Success(pinned); Success(disposable);
        PluginCacheCleanupResult cleanup = await Service().CleanupAsync(new HashSet<string> { active.Artifact!.ExecutionRevision },
            new HashSet<string> { active.CompilationFingerprint!, pinned.CompilationFingerprint! }, TimeSpan.Zero);
        Assert.Empty(cleanup.Diagnostics);
        Assert.Equal(1, cleanup.RemovedArtifacts);
        Assert.NotNull(await Service().FindArtifactAsync(active.Artifact.ExecutionRevision));
        Assert.NotNull(await Service().FindArtifactAsync(pinned.Artifact!.ExecutionRevision));
        Assert.Null(await Service().FindArtifactAsync(disposable.Artifact!.ExecutionRevision));
    }

    [Fact]
    public async Task CancelAndTimeoutKillRealCompilerProcessTreeWithoutPartialPublication()
    {
        if (OperatingSystem.IsWindows()) return; // Linux shell process fixture; Windows native package verification is separate.
        string path = await SourceAsync();
        string marker = Path.Combine(_root, "compiler pid");
        string wrapper = await WrapperAsync("echo $$ > " + Quote(marker) + "\nsleep 30\n");
        using CancellationTokenSource cancel = new();
        Task<PluginBuildResult> pending = Service(wrapper).BuildAsync(path, cancellationToken: cancel.Token);
        await WaitForFileAsync(marker);
        int pid = int.Parse(await File.ReadAllTextAsync(marker));
        cancel.Cancel();
        PluginBuildResult cancelled = await pending;
        Assert.Equal(PluginBuildStatus.Cancelled, cancelled.Status);
        AssertProcessGone(pid);
        File.Delete(marker);
        PluginBuildResult timedOut = await Service(wrapper, TimeSpan.FromMilliseconds(300)).BuildAsync(path);
        Assert.Equal(PluginBuildStatus.TimedOut, timedOut.Status);
        pid = int.Parse(await File.ReadAllTextAsync(marker));
        AssertProcessGone(pid);
        Assert.Empty(Directory.GetDirectories(Path.Combine(Cache, "artifacts")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(Cache, "staging")));
    }

    [Fact]
    public async Task SourceEditDuringChildStartupDoesNotChangeTheFrozenBuild()
    {
        if (OperatingSystem.IsWindows()) return;
        string path = await SourceAsync();
        string originalHash = Hash(await File.ReadAllBytesAsync(path));
        string marker = Path.Combine(_root, "snapshot ready");
        string wrapper = await WrapperAsync("echo ready > " + Quote(marker) + "\nsleep 0.4\nexec " + Quote(Executable) + " \"$@\"\n");
        Task<PluginBuildResult> pending = Service(wrapper).BuildAsync(path);
        await WaitForFileAsync(marker);
        await File.WriteAllTextAsync(path, "invalid new source !");
        PluginBuildResult frozen = await pending;
        Success(frozen);
        Assert.Equal(originalHash, frozen.SourceFingerprint);
        Assert.Equal(PluginBuildStatus.SourceError, (await Service().BuildAsync(path)).Status);
    }

    [Fact]
    public async Task MissingAndChangedReferencesAreExplainedAndSdkChangeInvalidatesCache()
    {
        string path = await SourceAsync();
        string copy = Path.Combine(_root, "delivered compiler");
        CopyDirectory(_bundle, copy);
        string reference = Path.Combine(copy, "references", "System.Runtime.dll");
        File.Delete(reference);
        PluginBuildResult missing = await Service(bundle: copy).BuildAsync(path);
        Assert.Equal(PluginBuildStatus.ReferenceError, missing.Status);
        File.Copy(Path.Combine(_bundle, "references", "System.Runtime.dll"), reference);
        PluginBuildResult first = await Service(bundle: copy).BuildAsync(path);
        Success(first);
        // Appending bytes preserves a valid managed PE; legitimate package publication recalculates the reference manifest.
        string sdk = Path.Combine(copy, "MeowChan.PluginSdk.dll");
        await using (FileStream stream = new(sdk, FileMode.Append)) { await stream.WriteAsync(new byte[] { 42 }); }
        Assert.Equal(PluginBuildStatus.ReferenceError, (await Service(bundle: copy).BuildAsync(path)).Status);
        await RegenerateManifestAsync(copy);
        PluginBuildResult changed = await Service(bundle: copy).BuildAsync(path);
        Success(changed);
        Assert.False(changed.CacheHit);
        Assert.NotEqual(first.CompilationFingerprint, changed.CompilationFingerprint);
    }

    [Fact]
    public async Task EmptyOversizeAndUnknownCandidatesStayVisibleInBoundedInventory()
    {
        string plugins = Path.Combine(_root, "plugins"); Directory.CreateDirectory(plugins);
        await File.WriteAllTextAsync(Path.Combine(plugins, "empty.cs"), "");
        await File.WriteAllBytesAsync(Path.Combine(plugins, "large.cs"), new byte[33]);
        await File.WriteAllTextAsync(Path.Combine(plugins, "unknown.dll"), "unknown");
        PluginInventory inventory = await new PluginInstallationInventory(10, 32).ScanAsync(plugins);
        Assert.Equal(3, inventory.Candidates.Count);
        Assert.Contains(inventory.Candidates, c => c.Diagnostics.Any(d => d.Code == "MCEMPTY"));
        Assert.Contains(inventory.Candidates, c => c.Diagnostics.Any(d => d.Code == "MCLIMIT"));
        Assert.Contains(inventory.Candidates, c => c.Kind == PluginCandidateKind.Unsupported);
        Assert.True((await new PluginInstallationInventory(2).ScanAsync(plugins)).Truncated);
        Assert.Equal(PluginBuildStatus.SourceError, (await Service().BuildAsync(Path.Combine(plugins, "empty.cs"))).Status);
    }

    [Fact]
    public async Task PrebuiltBundleImportVerifiesBytesAndCopiesResourcesWithoutCompilerStart()
    {
        string path = await SourceAsync();
        PluginBuildResult source = await Service().BuildAsync(path);
        Success(source);
        string package = Path.Combine(_root, "prebuilt bundle"); Directory.CreateDirectory(package);
        File.Copy(source.Artifact!.AssemblyPath, Path.Combine(package, "official.dll"));
        byte[] data = [1, 4, 9]; await File.WriteAllBytesAsync(Path.Combine(package, "resource.dat"), data);
        PluginBundleManifest manifest = new(1, "official.dll", source.Artifact.AssemblySha256, source.Artifact.SdkFingerprint,
            source.Artifact.CompilerFingerprint, [new("table", "resource.dat", Hash(data))]);
        string manifestPath = Path.Combine(package, "official.plugin.json");
        await File.WriteAllTextAsync(manifestPath, System.Text.Json.JsonSerializer.Serialize(manifest));
        PluginCompilationService importer = Service(executable: Path.Combine(_root, "compiler need not exist"));
        PluginBuildResult imported = await importer.ImportBundleAsync(manifestPath);
        Success(imported);
        Assert.NotEqual(source.Artifact.ExecutionRevision, imported.Artifact!.ExecutionRevision);
        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(imported.Artifact.ResourceRootPath!, "resource.dat")));
        Assert.True((await importer.ImportBundleAsync(manifestPath)).CacheHit);
        await File.WriteAllBytesAsync(Path.Combine(package, "official.dll"), [9, 8]);
        Assert.Equal(PluginBuildStatus.IntegrityError, (await importer.ImportBundleAsync(manifestPath)).Status);
        Assert.NotNull(await importer.FindArtifactAsync(imported.Artifact.ExecutionRevision));
    }

    private async Task<string> WrapperAsync(string contents)
    {
        string path = Path.Combine(_root, "compiler fixture.sh");
        await File.WriteAllTextAsync(path, "#!/bin/sh\n" + contents);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
    private static string Quote(string text) => "'" + text.Replace("'", "'\"'\"'") + "'";
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void AssertProcessGone(int pid) => Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
    private static async Task WaitForFileAsync(string marker)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (!File.Exists(marker)) await Task.Delay(10, timeout.Token);
    }
    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)));
    }
    private static async Task RegenerateManifestAsync(string directory)
    {
        ProcessStartInfo start = new(Path.Combine(directory, OperatingSystem.IsWindows() ? "MeowChan.PluginCompiler.exe" : "MeowChan.PluginCompiler")) { UseShellExecute = false };
        start.ArgumentList.Add("--write-toolchain-manifest"); start.ArgumentList.Add(directory);
        using Process process = Process.Start(start)!; await process.WaitForExitAsync(); Assert.Equal(0, process.ExitCode);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
