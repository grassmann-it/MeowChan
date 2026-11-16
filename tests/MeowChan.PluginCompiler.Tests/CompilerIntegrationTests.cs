using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MeowChan.PluginHost.Compilation;
using MeowChan.PluginHost.Installation;
using MeowChan.TestSupport;

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
        _bundle = Environment.GetEnvironmentVariable("MEOWCHAN_COMPILER_TEST_BUNDLE") ??
            BuiltTestBinaries.AppHostDirectory("MeowChan.PluginCompiler");
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
    public async Task ScopedCleanupRemovesOnlyTheNamedUnretainedBuilds()
    {
        string path = await SourceAsync();
        PluginBuildResult kept = await Service().BuildAsync(path);
        await File.WriteAllTextAsync(path, ValidSource + "\n// retired");
        PluginBuildResult retired = await Service().BuildAsync(path);
        await File.WriteAllTextAsync(path, ValidSource + "\n// unrelated");
        PluginBuildResult unrelated = await Service().BuildAsync(path);
        Success(kept); Success(retired); Success(unrelated);
        HashSet<string> empty = new();
        // A named build that something still retains is never removed.
        PluginCacheCleanupResult guarded = await Service().CleanupOnlyAsync(new HashSet<string> { kept.Artifact!.ExecutionRevision },
            new HashSet<string> { kept.CompilationFingerprint! }, new HashSet<string> { kept.Artifact.ExecutionRevision }, new HashSet<string> { kept.CompilationFingerprint! });
        Assert.Equal(0, guarded.RemovedArtifacts + guarded.RemovedCompilations);
        PluginCacheCleanupResult cleanup = await Service().CleanupOnlyAsync(new HashSet<string> { retired.Artifact!.ExecutionRevision },
            new HashSet<string> { retired.CompilationFingerprint! }, empty, empty);
        Assert.Empty(cleanup.Diagnostics);
        Assert.Equal(1, cleanup.RemovedArtifacts);
        Assert.Equal(1, cleanup.RemovedCompilations);
        Assert.Null(await Service().FindArtifactAsync(retired.Artifact.ExecutionRevision));
        // Builds that were not named stay, even though nothing retains them.
        Assert.NotNull(await Service().FindArtifactAsync(kept.Artifact.ExecutionRevision));
        Assert.NotNull(await Service().FindArtifactAsync(unrelated.Artifact!.ExecutionRevision));
        Assert.Equal(0, (await Service().CleanupOnlyAsync(empty, empty, empty, empty)).RemovedArtifacts);
    }

    [Fact]
    public async Task CancelAndTimeoutKillRealCompilerProcessTreeWithoutPartialPublication()
    {
        if (OperatingSystem.IsWindows()) return; // Linux shell process fixture; Windows native package verification is separate.
        string path = await SourceAsync(ValidSource + "/*" + new string('x', 1024 * 1024) + "*/");
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
        // The timeout has to outlast the shell's start so that its first command (the marker) has run; the sleep that follows
        // keeps the build running until the timeout kills it, however long that takes.
        PluginBuildResult timedOut = await Service(wrapper, TimeSpan.FromSeconds(3)).BuildAsync(path);
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

    private sealed record PrebuiltFixture(string ManifestPath, string AssemblyPath, string ResourcePath, PluginBundleManifest Manifest);

    private async Task<PrebuiltFixture> PrebuiltBundleAsync(string name, string source = ValidSource, PluginCompilationService? compiler = null)
    {
        PluginBuildResult built = await (compiler ?? Service()).BuildAsync(await SourceAsync(source));
        Success(built);
        string package = Path.Combine(_root, name); Directory.CreateDirectory(package);
        string assembly = Path.Combine(package, "official.dll");
        File.Copy(built.Artifact!.AssemblyPath, assembly);
        string resource = Path.Combine(package, "resource.dat");
        byte[] data = [1, 4, 9]; await File.WriteAllBytesAsync(resource, data);
        PluginBundleManifest manifest = new(1, "official.dll", built.Artifact.AssemblySha256, built.Artifact.SdkFingerprint,
            built.Artifact.CompilerFingerprint, [new("table", "resource.dat", Hash(data))]);
        string manifestPath = Path.Combine(package, "official.plugin.json");
        await File.WriteAllTextAsync(manifestPath, System.Text.Json.JsonSerializer.Serialize(manifest));
        return new(manifestPath, assembly, resource, manifest);
    }

    private static string Other(string hash) => (hash[0] == 'a' ? "b" : "a") + hash[1..];

    /// <summary>Every byte the importer relies on: a changed copy is never the verified bundle, a restored copy is again.</summary>
    private static async Task AssertEveryBundleByteIsVerifiedAsync(PrebuiltFixture bundle, Func<string, Task<PluginBuildResult>> import)
    {
        Assert.True((await import(bundle.ManifestPath)).CacheHit);
        string manifestText = await File.ReadAllTextAsync(bundle.ManifestPath);
        (string File, byte[] Bytes)[] sources = [(bundle.AssemblyPath, await File.ReadAllBytesAsync(bundle.AssemblyPath)),
            (bundle.ResourcePath, await File.ReadAllBytesAsync(bundle.ResourcePath))];
        foreach ((string file, byte[] original) in sources)
        {
            // Same name and size, one flipped byte, and a restored modification time: only the bytes can decide.
            DateTime stamp = File.GetLastWriteTimeUtc(file);
            byte[] changed = (byte[])original.Clone(); changed[^1] ^= 1;
            await File.WriteAllBytesAsync(file, changed); File.SetLastWriteTimeUtc(file, stamp);
            PluginBuildResult rejected = await import(bundle.ManifestPath);
            Assert.Equal(PluginBuildStatus.IntegrityError, rejected.Status);
            Assert.Null(rejected.Artifact);
            await File.WriteAllBytesAsync(file, original); File.SetLastWriteTimeUtc(file, stamp);
            Assert.True((await import(bundle.ManifestPath)).CacheHit);
        }
        string[] claims = [bundle.Manifest.AssemblySha256, bundle.Manifest.Resources[0].Sha256, bundle.Manifest.SdkFingerprint];
        foreach (string claim in claims)
        {
            // A manifest that claims other bytes (or another SDK) than the files it ships is not the delivered bundle.
            await File.WriteAllTextAsync(bundle.ManifestPath, manifestText.Replace(claim, Other(claim)));
            Assert.Equal(PluginBuildStatus.IntegrityError, (await import(bundle.ManifestPath)).Status);
            await File.WriteAllTextAsync(bundle.ManifestPath, manifestText);
            Assert.True((await import(bundle.ManifestPath)).CacheHit);
        }
    }

    private static async Task AssertCachedCopyIsVerifiedAsync(PrebuiltFixture bundle, PluginBuildResult imported, Func<string, Task<PluginBuildResult>> import)
    {
        // The cached copy is verified on every cache hit; a damaged copy is reported, not repaired or trusted.
        foreach (string file in new[] { imported.Artifact!.AssemblyPath, Path.Combine(imported.Artifact.ResourceRootPath!, "resource.dat") })
        {
            byte[] original = await File.ReadAllBytesAsync(file);
            byte[] changed = (byte[])original.Clone(); changed[0] ^= 1;
            await File.WriteAllBytesAsync(file, changed);
            PluginBuildResult damaged = await import(bundle.ManifestPath);
            Assert.Equal(PluginBuildStatus.IntegrityError, damaged.Status);
            Assert.Contains(damaged.Diagnostics, d => d.Code == "MCCACHE");
            await File.WriteAllBytesAsync(file, original);
            Assert.True((await import(bundle.ManifestPath)).CacheHit);
        }
    }

    [Fact]
    public async Task UnchangedBundleIsACacheHitAndNoChangedByteIsTreatedAsVerified()
    {
        PrebuiltFixture bundle = await PrebuiltBundleAsync("verified bundle");
        PluginCompilationService importer = Service(executable: Path.Combine(_root, "compiler need not exist"));
        PluginBuildResult first = await importer.ImportBundleAsync(bundle.ManifestPath);
        Success(first);
        Assert.False(first.CacheHit);
        await AssertEveryBundleByteIsVerifiedAsync(bundle, path => importer.ImportBundleAsync(path));
        await AssertCachedCopyIsVerifiedAsync(bundle, first, path => importer.ImportBundleAsync(path));
    }

    [Fact]
    public async Task BatchImportsKeepVerifyingEveryBundleAndItsCachedCopy()
    {
        PluginCompilationService compiler = Service();
        PrebuiltFixture bundle = await PrebuiltBundleAsync("batch bundle", compiler: compiler);
        PrebuiltFixture other = await PrebuiltBundleAsync("batch bundle two", ValidSource.Replace("=> 1", "=> 2"), compiler);
        PluginCompilationService importer = Service(executable: Path.Combine(_root, "compiler need not exist"));
        using (IPluginBundleImportBatch batch = ((IPluginBundleInstaller)importer).BeginImportBatch())
        {
            PluginBuildResult first = await batch.ImportBundleAsync(bundle.ManifestPath);
            Success(first);
            Assert.False(first.CacheHit);
            PluginBuildResult second = await batch.ImportBundleAsync(other.ManifestPath);
            Success(second);
            Assert.NotEqual(first.Artifact!.ExecutionRevision, second.Artifact!.ExecutionRevision);
            // The batch shares the toolchain result only: bundle bytes and cached copies are checked on every import.
            await AssertEveryBundleByteIsVerifiedAsync(bundle, path => batch.ImportBundleAsync(path));
            await AssertCachedCopyIsVerifiedAsync(bundle, first, path => batch.ImportBundleAsync(path));
            Assert.True((await batch.ImportBundleAsync(other.ManifestPath)).CacheHit);
        }
        Assert.True((await importer.ImportBundleAsync(bundle.ManifestPath)).CacheHit);
        IPluginBundleImportBatch disposed = ((IPluginBundleInstaller)importer).BeginImportBatch();
        disposed.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => disposed.ImportBundleAsync(bundle.ManifestPath));
    }

    [Fact]
    public async Task BatchVerifiesTheToolchainOncePerPassAndNeverKeepsFailureOrSuccessBeyondIt()
    {
        PrebuiltFixture bundle = await PrebuiltBundleAsync("toolchain bundle");
        string copy = Path.Combine(_root, "delivered toolchain");
        CopyDirectory(_bundle, copy);
        PluginCompilationService importer = Service(bundle: copy);
        IPluginBundleInstaller installer = importer;
        // Appending bytes preserves a valid managed PE, like a damaged but loadable reference.
        string reference = Path.Combine(copy, "references", "System.Runtime.dll");
        byte[] original = await File.ReadAllBytesAsync(reference);
        async Task DamageAsync() { await using FileStream stream = new(reference, FileMode.Append); await stream.WriteAsync(new byte[] { 42 }); }
        using (IPluginBundleImportBatch pass = installer.BeginImportBatch())
        {
            Success(await pass.ImportBundleAsync(bundle.ManifestPath));
            Assert.True((await pass.ImportBundleAsync(bundle.ManifestPath)).CacheHit);
            // The pass's one verification is reused only while no verified file changed: a change after hashing fails closed.
            await DamageAsync();
            AssertToolchainRejected(await pass.ImportBundleAsync(bundle.ManifestPath));
            await File.WriteAllBytesAsync(reference, original);
            Assert.True((await pass.ImportBundleAsync(bundle.ManifestPath)).CacheHit);
        }
        // A new pass and a plain import always verify the whole toolchain again.
        await DamageAsync();
        using (IPluginBundleImportBatch next = installer.BeginImportBatch())
        {
            AssertToolchainRejected(await next.ImportBundleAsync(bundle.ManifestPath));
            AssertToolchainRejected(await installer.ImportBundleAsync(bundle.ManifestPath));
            // A failed verification is not remembered: once the file is restored the same pass succeeds.
            await File.WriteAllBytesAsync(reference, original);
            Assert.True((await next.ImportBundleAsync(bundle.ManifestPath)).CacheHit);
        }
        Assert.True((await installer.ImportBundleAsync(bundle.ManifestPath)).CacheHit);
    }

    [Fact]
    public async Task ToolchainChangedBetweenHashingAndUseFailsClosed()
    {
        PrebuiltFixture bundle = await PrebuiltBundleAsync("toolchain race bundle");
        string copy = Path.Combine(_root, "raced toolchain");
        CopyDirectory(_bundle, copy);
        PluginCompilationService importer = Service(bundle: copy);
        IPluginBundleInstaller installer = importer;
        Success(await importer.ImportBundleAsync(bundle.ManifestPath));
        string reference = Path.Combine(copy, "references", "System.Runtime.dll");
        string sdk = Path.Combine(copy, "MeowChan.PluginSdk.dll");
        byte[] original = await File.ReadAllBytesAsync(reference);
        byte[] damaged = (byte[])original.Clone();
        damaged[^1] ^= 1;

        // Changed right after the batch began hashing (in the background, outside the build gate): whether the hash saw
        // the old or the new bytes, the import must not accept the toolchain.
        using (IPluginBundleImportBatch pass = installer.BeginImportBatch())
        {
            await File.WriteAllBytesAsync(reference, damaged);
            AssertToolchainRejected(await pass.ImportBundleAsync(bundle.ManifestPath));
        }
        await File.WriteAllBytesAsync(reference, original);

        // Same length, write time set back: only the file identity (status-change time / inode) still shows the write.
        using (IPluginBundleImportBatch pass = installer.BeginImportBatch())
        {
            Assert.True((await pass.ImportBundleAsync(bundle.ManifestPath)).CacheHit);
            DateTime written = File.GetLastWriteTimeUtc(reference);
            await File.WriteAllBytesAsync(reference, damaged);
            File.SetLastWriteTimeUtc(reference, written);
            if (OperatingSystem.IsLinux()) AssertToolchainRejected(await pass.ImportBundleAsync(bundle.ManifestPath));
            await File.WriteAllBytesAsync(reference, original);
            Assert.True((await pass.ImportBundleAsync(bundle.ManifestPath)).CacheHit);
        }

        // A verified file replaced by another file is re-verified before use: other bytes fail, identical bytes pass.
        using (IPluginBundleImportBatch pass = installer.BeginImportBatch())
        {
            Assert.True((await pass.ImportBundleAsync(bundle.ManifestPath)).CacheHit);
            string other = Path.Combine(copy, "replacement.tmp");
            await File.WriteAllBytesAsync(other, damaged);
            File.Move(other, reference, overwrite: true);
            AssertToolchainRejected(await pass.ImportBundleAsync(bundle.ManifestPath));
            await File.WriteAllBytesAsync(other, original);
            File.Move(other, reference, overwrite: true);
            Assert.True((await pass.ImportBundleAsync(bundle.ManifestPath)).CacheHit);
            // A missing verified file is a failure, not a skipped check.
            File.Move(sdk, sdk + ".away");
            PluginBuildResult missing = await pass.ImportBundleAsync(bundle.ManifestPath);
            Assert.False(missing.Succeeded);
            File.Move(sdk + ".away", sdk);
            Assert.True((await pass.ImportBundleAsync(bundle.ManifestPath)).CacheHit);
        }
    }

    [Fact]
    public async Task DisposingDuringTheBackgroundToolchainVerificationLeavesNothingRunning()
    {
        PrebuiltFixture bundle = await PrebuiltBundleAsync("disposed pass bundle");
        PluginCompilationService importer = Service(executable: Path.Combine(_root, "compiler need not exist"));
        IPluginBundleInstaller installer = importer;
        installer.BeginImportBatch().Dispose(); // Cancels its verification at once; nothing observes the outcome.
        IPluginBundleImportBatch pass = installer.BeginImportBatch();
        Task<PluginBuildResult> import = pass.ImportBundleAsync(bundle.ManifestPath);
        await importer.DisposeAsync(); // Waits for the import and for the verification it may still be running.
        PluginBuildResult result = await import;
        Assert.True(result.Succeeded || result.Status == PluginBuildStatus.Cancelled, result.Status.ToString());
        Assert.Equal(PluginBuildStatus.Cancelled, (await pass.ImportBundleAsync(bundle.ManifestPath)).Status);
        pass.Dispose();
        // A service that is stopping starts no verification for a new pass.
        Assert.Equal(PluginBuildStatus.Cancelled, (await installer.BeginImportBatch().ImportBundleAsync(bundle.ManifestPath)).Status);
    }

    private static void AssertToolchainRejected(PluginBuildResult result)
    {
        Assert.Equal(PluginBuildStatus.IntegrityError, result.Status);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("toolchain integrity", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CompilerExitBeforeReadingLargeRequestRetainsCrashDiagnostics()
    {
        if (OperatingSystem.IsWindows()) return;
        string path = await SourceAsync(ValidSource + new string(' ', 256 * 1024));
        string crash = await WrapperAsync("echo 'controlled unread-request crash' >&2\nexit 37\n");
        PluginBuildResult result = await Service(crash).BuildAsync(path);
        Assert.Equal(PluginBuildStatus.CompilerError, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "MCCOMPILER"
            && diagnostic.Message.Contains("37", StringComparison.Ordinal)
            && diagnostic.Message.Contains("controlled unread-request crash", StringComparison.Ordinal));
        Assert.Empty(Directory.GetDirectories(Path.Combine(Cache, "artifacts")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(Cache, "staging")));
    }

    [Fact]
    public async Task CompilerCrashAndBoundedAdmissionRemainExplicitFailures()
    {
        if (OperatingSystem.IsWindows()) return;
        string path = await SourceAsync();
        string crash = await WrapperAsync("echo 'controlled compiler crash' >&2\nexit 37\n");
        PluginBuildResult crashed = await Service(crash).BuildAsync(path);
        Assert.Equal(PluginBuildStatus.CompilerError, crashed.Status);
        Assert.Contains(crashed.Diagnostics, d => d.Message.Contains("37", StringComparison.Ordinal));
        Assert.Empty(Directory.GetDirectories(Path.Combine(Cache, "artifacts")));
        string marker = Path.Combine(_root, "admitted compiler");
        string hanging = await WrapperAsync("echo ready > " + Quote(marker) + "\nsleep 30\n");
        using CancellationTokenSource cancellation = new();
        Task<PluginBuildResult> running = Service(hanging).BuildAsync(path, cancellationToken: cancellation.Token);
        await WaitForFileAsync(marker);
        Task<PluginBuildResult>[] waiting = Enumerable.Range(0, 8).Select(_ => Service(hanging).BuildAsync(path, cancellationToken: cancellation.Token)).ToArray();
        PluginBuildResult rejected = await Service(hanging).BuildAsync(path);
        Assert.Equal(PluginBuildStatus.Busy, rejected.Status);
        cancellation.Cancel();
        Assert.Equal(PluginBuildStatus.Cancelled, (await running).Status);
        Assert.All(await Task.WhenAll(waiting), r => Assert.Equal(PluginBuildStatus.Cancelled, r.Status));
        Success(await Service().BuildAsync(path));
    }

    [Fact]
    public async Task CallerFilePathUsesStableVirtualPathAndMovedSourcesKeepDeterministicBytes()
    {
        string source = "using System.Runtime.CompilerServices; public static class Paths { public static string Name => Get(); public static string Get([CallerFilePath] string path = \"\") => path; }";
        string firstPath = await SourceAsync(source);
        PluginBuildResult first = await Service().BuildAsync(firstPath);
        Success(first);
        string otherPath = Path.Combine(_root, "other path.cs"); await File.WriteAllTextAsync(otherPath, source);
        PluginCompilationService independent = new(new PluginCompilerOptions(Executable, _bundle, Path.Combine(_root, "independent cache")));
        PluginBuildResult other = await independent.BuildAsync(otherPath);
        Success(other);
        Assert.Equal(first.CompilationFingerprint, other.CompilationFingerprint);
        Assert.Equal(first.Artifact!.AssemblySha256, other.Artifact!.AssemblySha256);
    }

    [Fact]
    public async Task ServiceShutdownCancelsAndAwaitsItsOwnChildProcess()
    {
        if (OperatingSystem.IsWindows()) return;
        string path = await SourceAsync();
        string marker = Path.Combine(_root, "shutdown pid");
        string wrapper = await WrapperAsync("echo $$ > " + Quote(marker) + "\nsleep 30\n");
        PluginCompilationService service = Service(wrapper);
        Task<PluginBuildResult> running = service.BuildAsync(path);
        await WaitForFileAsync(marker);
        int pid = int.Parse(await File.ReadAllTextAsync(marker));
        await service.DisposeAsync();
        Assert.Equal(PluginBuildStatus.Cancelled, (await running).Status);
        AssertProcessGone(pid);
        Assert.Equal(PluginBuildStatus.Cancelled, (await service.BuildAsync(path)).Status);
        Assert.Empty(Directory.GetDirectories(Path.Combine(Cache, "artifacts")));
    }

    [Fact]
    public async Task FifoCandidatesCannotTrapInventoryOrSourceReads()
    {
        if (!OperatingSystem.IsLinux()) return;
        string plugins = Path.Combine(_root, "special files"); Directory.CreateDirectory(plugins);
        string sourceFifo = Path.Combine(plugins, "pipe.cs");
        string manifestFifo = Path.Combine(plugins, "pipe.plugin.json");
        foreach (string path in new[] { sourceFifo, manifestFifo })
        {
            ProcessStartInfo start = new("/usr/bin/mkfifo") { UseShellExecute = false }; start.ArgumentList.Add(path);
            using Process process = Process.Start(start)!; await process.WaitForExitAsync(); Assert.Equal(0, process.ExitCode);
        }
        // Reading a FIFO blocks for ever, so the limits only need to tell "never returns" from "returns"; they are no speed bound.
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        PluginInventory inventory = await new PluginInstallationInventory().ScanAsync(plugins, timeout.Token).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(2, inventory.Candidates.Count);
        Assert.All(inventory.Candidates, candidate => Assert.NotEmpty(candidate.Diagnostics));
        PluginBuildResult result = await Service().BuildAsync(sourceFifo, cancellationToken: timeout.Token).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(result.Succeeded);
        Assert.NotEqual(PluginBuildStatus.Cancelled, result.Status);
        Assert.False(timeout.IsCancellationRequested);
    }

    [Fact]
    public async Task ExcessiveCompilerOutputIsStoppedAsProtocolFailure()
    {
        if (!OperatingSystem.IsLinux()) return;
        string path = await SourceAsync();
        string wrapper = await WrapperAsync("head -c 1100000 /dev/zero\nsleep 30\n");
        PluginBuildResult result = await Service(wrapper, TimeSpan.FromSeconds(20)).BuildAsync(path);
        Assert.Equal(PluginBuildStatus.CompilerError, result.Status);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("size limit", StringComparison.Ordinal));
        Assert.Empty(Directory.GetDirectories(Path.Combine(Cache, "artifacts")));
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
