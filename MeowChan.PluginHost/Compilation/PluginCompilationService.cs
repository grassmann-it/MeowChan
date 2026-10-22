using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MeowChan.PluginHost.Contracts;

namespace MeowChan.PluginHost.Compilation;

/// <summary>Builds an immutable candidate with the shipped compiler. Owns no activation or configuration state.</summary>
public sealed partial class PluginCompilationService : IPluginCompilationService
{
    private static readonly SemaphoreSlim BuildGate = new(1, 1);
    private static int _admittedBuilds;
    private readonly PluginCompilerOptions _options;
    private readonly string _cache;
    private sealed record CompilationEntry(int SchemaVersion, string CompilationFingerprint, string SourceFingerprint,
        string AssemblySha256, string SdkFingerprint, string CompilerFingerprint, PluginBuildDiagnostic[] Diagnostics);
    private sealed record ArtifactEntry(int SchemaVersion, string ExecutionRevision, CompilationEntry Compilation, PluginArtifactResource[] Resources);
    private sealed record ResourceSnapshot(PluginArtifactResource Resource, byte[] Bytes);

    public PluginCompilationService(PluginCompilerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Path.IsPathFullyQualified(options.CompilerExecutablePath) || !Path.IsPathFullyQualified(options.ToolchainDirectory) ||
            !Path.IsPathFullyQualified(options.CacheDirectory)) throw new ArgumentException("Compiler, toolchain and cache paths must be absolute.", nameof(options));
        if (options.BuildTimeout <= TimeSpan.Zero || options.BuildTimeout > TimeSpan.FromMinutes(5) ||
            options.MaximumSourceBytes is < 1 or > CompilerWire.MaximumSourceBytes || options.MaximumResourceBytes is < 1 or > 32 * 1024 * 1024 ||
            options.MaximumTotalResourceBytes < options.MaximumResourceBytes || options.MaximumTotalResourceBytes > 64 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(options));
        _options = options;
        _cache = Path.GetFullPath(options.CacheDirectory);
    }

    public async Task<PluginBuildResult> BuildAsync(string sourcePath, IReadOnlyList<PluginResourceInput>? resources = null,
        CancellationToken cancellationToken = default)
    {
        string? sourceFingerprint = null;
        string? compilationFingerprint = null;
        bool acquired = false;
        string? stage = null;
        List<PluginBuildDiagnostic> notices = new();
        if (Interlocked.Increment(ref _admittedBuilds) > 9)
        {
            Interlocked.Decrement(ref _admittedBuilds);
            return Failure(PluginBuildStatus.Busy, "MCBUSY", "The compiler is busy (one build and eight waiting requests).", sourcePath);
        }
        try
        {
            await BuildGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            string originalPath = Path.GetFullPath(sourcePath);
            if (!string.Equals(Path.GetExtension(originalPath), ".cs", StringComparison.OrdinalIgnoreCase))
                return Failure(PluginBuildStatus.SourceError, "MCSOURCE", "A plugin source must be one .cs file.", originalPath);
            byte[] source = await CompilerWire.ReadBoundedAsync(originalPath, _options.MaximumSourceBytes, cancellationToken).ConfigureAwait(false);
            sourceFingerprint = CompilerWire.Hash(source);
            if (source.Length == 0) return Failure(PluginBuildStatus.SourceError, "MCEMPTY", "The source file is empty.", originalPath, sourceFingerprint);
            ToolchainManifest toolchain;
            try { toolchain = await CompilerWire.ReadToolchainAsync(_options.ToolchainDirectory, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            { return Failure(PluginBuildStatus.ReferenceError, "MCREF", exception.Message, originalPath, sourceFingerprint); }
            string toolchainFingerprint = CompilerWire.ToolchainHash(toolchain);
            compilationFingerprint = CompilerWire.HashParts(sourceFingerprint, toolchainFingerprint);
            List<ResourceSnapshot> snapshots = await ReadResourcesAsync(resources, cancellationToken).ConfigureAwait(false);
            PluginArtifactResource[] resourceManifest = snapshots.Select(s => s.Resource).ToArray();
            string executionRevision = ExecutionRevision(compilationFingerprint, resourceManifest);
            Directory.CreateDirectory(_cache);
            string artifactsRoot = CompilerWire.ResolvePath(_cache, "artifacts");
            string codeRoot = CompilerWire.ResolvePath(_cache, "compilations");
            string stagingRoot = CompilerWire.ResolvePath(_cache, "staging");
            Directory.CreateDirectory(artifactsRoot);
            Directory.CreateDirectory(codeRoot);
            Directory.CreateDirectory(stagingRoot);
            string artifactDirectory = CompilerWire.ResolvePath(artifactsRoot, executionRevision);
            if (Directory.Exists(artifactDirectory))
            {
                PluginArtifact? existing = await FindArtifactAsync(executionRevision, cancellationToken).ConfigureAwait(false);
                if (existing is not null)
                {
                    ArtifactEntry entry = await ReadJsonAsync<ArtifactEntry>(Path.Combine(artifactDirectory, "artifact.json"), cancellationToken);
                    return new(PluginBuildStatus.Success, existing, true, sourceFingerprint, compilationFingerprint, RemapDiagnostics(entry.Compilation.Diagnostics, originalPath));
                }
                // A known revision may still be active/pinned. Never repair or overwrite it in place.
                return Failure(PluginBuildStatus.IntegrityError, "MCCACHE", "The cached plugin revision failed its integrity check. Remove the damaged candidate explicitly before rebuilding.", originalPath, sourceFingerprint, compilationFingerprint);
            }
            string codeDirectory = CompilerWire.ResolvePath(codeRoot, compilationFingerprint);
            CompilationEntry? code = await ReadCompilationAsync(codeDirectory, compilationFingerprint, cancellationToken).ConfigureAwait(false);
            bool cacheHit = code is not null;
            if (code is null && Directory.Exists(codeDirectory))
                notices.Add(new("MCCACHE", "A damaged compilation cache entry was discarded and will be rebuilt.", originalPath, Severity: "warning"));
            stage = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            if (code is null)
            {
                string buildStage = Path.Combine(stage, "code");
                Directory.CreateDirectory(buildStage);
                CompilerRequest request = new(source, originalPath, compilationFingerprint, toolchainFingerprint, Path.Combine(buildStage, "plugin.dll"));
                CompilerResponse response;
                try { response = await RunCompilerAsync(request, cancellationToken).ConfigureAwait(false); }
                catch (TimeoutException exception) { return Failure(PluginBuildStatus.TimedOut, "MCTIMEOUT", exception.Message, originalPath, sourceFingerprint, compilationFingerprint); }
                catch (CompilerProcessException exception) { return Failure(PluginBuildStatus.CompilerError, "MCCOMPILER", exception.Message, originalPath, sourceFingerprint, compilationFingerprint); }
                PluginBuildDiagnostic[] diagnostics = response.Diagnostics.Select(d => new PluginBuildDiagnostic(d.Code, d.Message,
                    d.FilePath is null ? null : originalPath, d.Line, d.Column, d.Severity)).ToArray();
                if (!response.Success) return new(response.FailureKind == "ReferenceError" ? PluginBuildStatus.ReferenceError : PluginBuildStatus.SourceError,
                    null, false, sourceFingerprint, compilationFingerprint, diagnostics);
                byte[] assembly = await CompilerWire.ReadBoundedAsync(request.OutputPath, 32 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
                code = new(1, compilationFingerprint, sourceFingerprint, CompilerWire.Hash(assembly), toolchain.SdkFingerprint, toolchain.CompilerFingerprint, diagnostics);
                await WriteJsonAsync(Path.Combine(buildStage, "compilation.json"), code, cancellationToken).ConfigureAwait(false);
                if (Directory.Exists(codeDirectory)) Directory.Delete(codeDirectory, true);
                cancellationToken.ThrowIfCancellationRequested();
                Directory.Move(buildStage, codeDirectory);
            }
            string artifactStage = Path.Combine(stage, "artifact");
            Directory.CreateDirectory(artifactStage);
            byte[] publishedAssembly = await CompilerWire.ReadBoundedAsync(Path.Combine(codeDirectory, "plugin.dll"), 32 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
            await WriteBytesAsync(Path.Combine(artifactStage, "plugin.dll"), publishedAssembly, cancellationToken).ConfigureAwait(false);
            foreach (ResourceSnapshot snapshot in snapshots)
            {
                string target = CompilerWire.ResolvePath(artifactStage, snapshot.Resource.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await WriteBytesAsync(target, snapshot.Bytes, cancellationToken).ConfigureAwait(false);
            }
            ArtifactEntry artifactEntry = new(1, executionRevision, code, resourceManifest);
            await WriteJsonAsync(Path.Combine(artifactStage, "artifact.json"), artifactEntry, cancellationToken).ConfigureAwait(false);
            if (await ValidateArtifactAsync(artifactStage, executionRevision, cancellationToken).ConfigureAwait(false) is null)
                return Failure(PluginBuildStatus.IntegrityError, "MCCACHE", "The completed artifact failed verification before publication.", originalPath, sourceFingerprint, compilationFingerprint);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(artifactStage, artifactDirectory);
            PluginArtifact? artifact = await FindArtifactAsync(executionRevision, cancellationToken).ConfigureAwait(false);
            if (artifact is null) return Failure(PluginBuildStatus.IntegrityError, "MCCACHE", "The published artifact failed verification.", originalPath, sourceFingerprint, compilationFingerprint);
            notices.AddRange(RemapDiagnostics(code.Diagnostics, originalPath));
            return new(PluginBuildStatus.Success, artifact, cacheHit, sourceFingerprint, compilationFingerprint, notices);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return Failure(PluginBuildStatus.Cancelled, "MCCANCEL", "Compilation was cancelled.", sourcePath, sourceFingerprint, compilationFingerprint); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or System.ComponentModel.Win32Exception)
        { return Failure(exception is InvalidDataException ? PluginBuildStatus.IntegrityError : PluginBuildStatus.IoError, "MCIO", exception.Message, sourcePath, sourceFingerprint, compilationFingerprint); }
        finally
        {
            if (stage is not null) TryDelete(stage);
            if (acquired) BuildGate.Release();
            Interlocked.Decrement(ref _admittedBuilds);
        }
    }

    public async Task<PluginArtifact?> FindArtifactAsync(string executionRevision, CancellationToken cancellationToken = default)
    {
        if (!CompilerWire.IsHash(executionRevision)) return null;
        try
        {
            return await ValidateArtifactAsync(CompilerWire.ResolvePath(_cache, "artifacts/" + executionRevision), executionRevision, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException) { return null; }
    }

    public async Task<PluginCacheCleanupResult> CleanupAsync(IReadOnlySet<string> retainedExecutionRevisions,
        IReadOnlySet<string> retainedCompilationFingerprints, TimeSpan minimumAge, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(retainedExecutionRevisions);
        ArgumentNullException.ThrowIfNull(retainedCompilationFingerprints);
        if (minimumAge < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(minimumAge));
        await BuildGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        int artifacts = 0, compilations = 0;
        List<PluginBuildDiagnostic> diagnostics = new();
        try
        {
            DateTime cutoff = DateTime.UtcNow - minimumAge;
            foreach (string kind in new[] { "artifacts", "compilations", "staging" })
            {
                string root = CompilerWire.ResolvePath(_cache, kind);
                if (!Directory.Exists(root)) continue;
                int visited = 0;
                foreach (string directory in Directory.EnumerateDirectories(root))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++visited > 4096) { diagnostics.Add(new("MCLIMIT", "Cache cleanup stopped after 4096 entries.", root, Severity: "warning")); break; }
                    string id = Path.GetFileName(directory);
                    if (retainedExecutionRevisions.Contains(id) || retainedCompilationFingerprints.Contains(id)) continue;
                    try
                    {
                        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 || Directory.GetLastWriteTimeUtc(directory) > cutoff) continue;
                        if (kind == "artifacts")
                        {
                            ArtifactEntry entry = await ReadJsonAsync<ArtifactEntry>(CompilerWire.ResolvePath(directory, "artifact.json"), cancellationToken).ConfigureAwait(false);
                            if (entry.Compilation is null || retainedCompilationFingerprints.Contains(entry.Compilation.CompilationFingerprint)) continue;
                        }
                        Directory.Delete(directory, true);
                        if (kind == "artifacts") artifacts++;
                        if (kind == "compilations") compilations++;
                    }
                    catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
                    { diagnostics.Add(new("MCCLEANUP", exception.Message, directory, Severity: "warning")); }
                }
            }
        }
        finally { BuildGate.Release(); }
        return new(artifacts, compilations, diagnostics);
    }

    private async Task<List<ResourceSnapshot>> ReadResourcesAsync(IReadOnlyList<PluginResourceInput>? inputs, CancellationToken cancellationToken)
    {
        List<ResourceSnapshot> snapshots = new();
        if (inputs is null) return snapshots;
        if (inputs.Count > 128 || inputs.Any(i => i is null)) throw new InvalidDataException("A bundle may contain at most 128 resources.");
        HashSet<string> identities = new(StringComparer.Ordinal);
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase) { "plugin.dll", "artifact.json" };
        long total = 0;
        foreach (PluginResourceInput input in inputs.OrderBy(i => i.Name, StringComparer.Ordinal).ThenBy(i => i.Rid, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 128 || (input.Rid?.Length ?? 0) > 64 ||
                !identities.Add(input.Name + "\n" + input.Rid) || !paths.Add(input.RelativePath))
                throw new InvalidDataException("Resource names/RIDs and paths must be unique and bounded.");
            _ = CompilerWire.ResolvePath(_cache, input.RelativePath);
            byte[] bytes = await CompilerWire.ReadBoundedAsync(Path.GetFullPath(input.SourcePath), _options.MaximumResourceBytes, cancellationToken).ConfigureAwait(false);
            total += bytes.Length;
            if (total > _options.MaximumTotalResourceBytes) throw new InvalidDataException("The bundle exceeds the total resource size limit.");
            snapshots.Add(new(new(input.Name, input.RelativePath, CompilerWire.Hash(bytes), input.Rid), bytes));
        }
        return snapshots;
    }

    private static string ExecutionRevision(string compilationFingerprint, IReadOnlyList<PluginArtifactResource> resources) =>
        CompilerWire.HashParts(new[] { "artifact-v1", compilationFingerprint }.Concat(resources.OrderBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.Rid, StringComparer.Ordinal)
            .SelectMany(r => new[] { r.Name, r.RelativePath, r.Sha256, r.Rid ?? "" })).ToArray());

    private static async Task<CompilationEntry?> ReadCompilationAsync(string directory, string fingerprint, CancellationToken cancellationToken)
    {
        try
        {
            CompilationEntry entry = await ReadJsonAsync<CompilationEntry>(CompilerWire.ResolvePath(directory, "compilation.json"), cancellationToken).ConfigureAwait(false);
            if (!ValidCompilation(entry) || entry.CompilationFingerprint != fingerprint) return null;
            byte[] assembly = await CompilerWire.ReadBoundedAsync(CompilerWire.ResolvePath(directory, "plugin.dll"), 32 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
            return CompilerWire.Hash(assembly) == entry.AssemblySha256 ? entry : null;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static bool ValidCompilation(CompilationEntry entry) => entry.SchemaVersion == 1 &&
        CompilerWire.IsHash(entry.CompilationFingerprint) && CompilerWire.IsHash(entry.SourceFingerprint) && CompilerWire.IsHash(entry.AssemblySha256) &&
        CompilerWire.IsHash(entry.SdkFingerprint) && CompilerWire.IsHash(entry.CompilerFingerprint) && entry.Diagnostics is { Length: <= 100 } &&
        entry.Diagnostics.All(d => d is not null && !string.IsNullOrWhiteSpace(d.Code) && d.Message is not null);

    private static async Task<PluginArtifact?> ValidateArtifactAsync(string directory, string revision, CancellationToken cancellationToken)
    {
        try
        {
            ArtifactEntry entry = await ReadJsonAsync<ArtifactEntry>(CompilerWire.ResolvePath(directory, "artifact.json"), cancellationToken).ConfigureAwait(false);
            if (entry.SchemaVersion != 1 || entry.ExecutionRevision != revision || entry.Compilation is null || !ValidCompilation(entry.Compilation) ||
                entry.Resources is null || entry.Resources.Length > 128 || entry.Resources.Any(r => r is null || !CompilerWire.IsHash(r.Sha256) || string.IsNullOrWhiteSpace(r.Name)) ||
                entry.Resources.Select(r => r.Name + "\n" + r.Rid).Distinct(StringComparer.Ordinal).Count() != entry.Resources.Length ||
                entry.Resources.Select(r => r.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entry.Resources.Length ||
                ExecutionRevision(entry.Compilation.CompilationFingerprint, entry.Resources) != revision) return null;
            string assemblyPath = CompilerWire.ResolvePath(directory, "plugin.dll");
            byte[] assembly = await CompilerWire.ReadBoundedAsync(assemblyPath, 32 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
            if (CompilerWire.Hash(assembly) != entry.Compilation.AssemblySha256) return null;
            long total = 0;
            foreach (PluginArtifactResource resource in entry.Resources)
            {
                if (resource.RelativePath.Equals("plugin.dll", StringComparison.OrdinalIgnoreCase) || resource.RelativePath.Equals("artifact.json", StringComparison.OrdinalIgnoreCase)) return null;
                byte[] bytes = await CompilerWire.ReadBoundedAsync(CompilerWire.ResolvePath(directory, resource.RelativePath), 32 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
                total += bytes.Length;
                if (total > 64 * 1024 * 1024 || CompilerWire.Hash(bytes) != resource.Sha256) return null;
            }
            return new(entry.Compilation.CompilationFingerprint, revision, assemblyPath, entry.Compilation.AssemblySha256,
                entry.Compilation.SdkFingerprint, entry.Compilation.CompilerFingerprint, Array.AsReadOnly(entry.Resources), directory);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }

    private async Task<CompilerResponse> RunCompilerAsync(CompilerRequest request, CancellationToken cancellationToken)
    {
        ProcessStartInfo start = new(_options.CompilerExecutablePath)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(request.OutputPath)!
        };
        start.ArgumentList.Add("--compile");
        start.ArgumentList.Add(_options.ToolchainDirectory);
        start.Environment["DOTNET_GCHeapHardLimit"] = "10000000"; // 256 MiB of managed heap; process memory remains an OS concern.
        start.Environment["DOTNET_GCConserveMemory"] = "9";
        using Process process = new() { StartInfo = start };
        if (!process.Start()) throw new CompilerProcessException("The compiler process could not start.");
        using CancellationTokenSource timeout = new(_options.BuildTimeout);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        Task<byte[]> outputTask = ReadProcessOutputAsync(process.StandardOutput.BaseStream, CompilerWire.MaximumResponseBytes, linked.Token);
        Task<byte[]> errorTask = ReadProcessOutputAsync(process.StandardError.BaseStream, 65536, linked.Token, discardExcess: true);
        try
        {
            byte[] requestJson = JsonSerializer.SerializeToUtf8Bytes(request, CompilerWire.JsonOptions);
            await process.StandardInput.BaseStream.WriteAsync(requestJson, linked.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            byte[] output = await outputTask.ConfigureAwait(false);
            byte[] stderr = await errorTask.ConfigureAwait(false);
            if (output.Length == 0) throw new CompilerProcessException($"Compiler exited with code {process.ExitCode}: {Encoding.UTF8.GetString(stderr)}");
            CompilerResponse response = JsonSerializer.Deserialize<CompilerResponse>(output, CompilerWire.JsonOptions)
                ?? throw new CompilerProcessException("The compiler returned an empty response.");
            if (response.Diagnostics is null || response.Diagnostics.Length > 100 || (response.Success && process.ExitCode != 0))
                throw new CompilerProcessException("The compiler returned an invalid response.");
            return response;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        { throw new TimeoutException($"Compilation exceeded {_options.BuildTimeout.TotalSeconds:g} seconds and the compiler process was stopped."); }
        catch (JsonException exception) { throw new CompilerProcessException("Invalid compiler response: " + exception.Message); }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch (InvalidOperationException) { }
            await linked.CancelAsync();
            try { await outputTask.ConfigureAwait(false); } catch (Exception exception) when (exception is OperationCanceledException or IOException) { }
            try { await errorTask.ConfigureAwait(false); } catch (Exception exception) when (exception is OperationCanceledException or IOException) { }
        }
    }

    private static async Task<byte[]> ReadProcessOutputAsync(Stream stream, int maximum, CancellationToken cancellationToken, bool discardExcess = false)
    {
        using MemoryStream result = new();
        byte[] buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (result.Length + count > maximum && !discardExcess) throw new CompilerProcessException("Compiler output exceeded its size limit.");
            int take = Math.Min(count, maximum - (int)result.Length);
            if (take > 0) result.Write(buffer, 0, take);
        }
        return result.ToArray();
    }

    private static PluginBuildDiagnostic[] RemapDiagnostics(IEnumerable<PluginBuildDiagnostic> diagnostics, string originalPath) =>
        diagnostics.Select(d => d.FilePath is null ? d : d with { FilePath = originalPath }).ToArray();
    private static PluginBuildResult Failure(PluginBuildStatus status, string code, string message, string? path,
        string? source = null, string? compilation = null) => new(status, null, false, source, compilation, [new(code, message, path)]);
    private static async Task<T> ReadJsonAsync<T>(string path, CancellationToken token) => JsonSerializer.Deserialize<T>(
        await CompilerWire.ReadBoundedAsync(path, CompilerWire.MaximumResponseBytes, token).ConfigureAwait(false), CompilerWire.JsonOptions)
        ?? throw new InvalidDataException("Empty cache manifest.");
    private static Task WriteJsonAsync<T>(string path, T value, CancellationToken token) => WriteBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(value, CompilerWire.JsonOptions), token);
    private static async Task WriteBytesAsync(string path, byte[] bytes, CancellationToken token)
    {
        await using FileStream output = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await output.WriteAsync(bytes, token).ConfigureAwait(false);
        await output.FlushAsync(token).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
    }
    private static void TryDelete(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    private sealed class CompilerProcessException(string message) : IOException(message);
}
