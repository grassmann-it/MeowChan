using System.Text.Json;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Installation;

namespace MeowChan.PluginHost.Compilation;

public sealed partial class PluginCompilationService : IPluginBundleInstaller
{
    public async Task<PluginBuildResult> ImportBundleAsync(string manifestPath, CancellationToken cancellationToken = default)
    {
        string? stage = null;
        bool acquired = false;
        if (Interlocked.Increment(ref _admittedBuilds) > 9)
        {
            Interlocked.Decrement(ref _admittedBuilds);
            return Failure(PluginBuildStatus.Busy, "MCBUSY", "The compiler/importer is busy.", manifestPath);
        }
        try
        {
            await BuildGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            string fullPath = Path.GetFullPath(manifestPath);
            string root = Path.GetDirectoryName(fullPath)!;
            PluginBundleManifest bundle = await ReadJsonAsync<PluginBundleManifest>(fullPath, cancellationToken).ConfigureAwait(false);
            if (bundle.SchemaVersion != 1 || !CompilerWire.IsHash(bundle.AssemblySha256) || !CompilerWire.IsHash(bundle.SdkFingerprint) ||
                !CompilerWire.IsHash(bundle.CompilerFingerprint) || bundle.Resources is null || bundle.Resources.Count > 128 ||
                bundle.Resources.Any(r => r is null || !CompilerWire.IsHash(r.Sha256)))
                throw new InvalidDataException("Unsupported or incomplete plugin bundle manifest.");
            ToolchainManifest toolchain = await CompilerWire.ReadToolchainAsync(_options.ToolchainDirectory, cancellationToken).ConfigureAwait(false);
            if (bundle.SdkFingerprint != toolchain.SdkFingerprint) throw new InvalidDataException("The prebuilt bundle does not match the delivered SDK fingerprint.");
            byte[] assembly = await CompilerWire.ReadBoundedAsync(CompilerWire.ResolvePath(root, bundle.AssemblyRelativePath), 32 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
            if (CompilerWire.Hash(assembly) != bundle.AssemblySha256) throw new InvalidDataException("The prebuilt assembly hash does not match its manifest.");
            List<ResourceSnapshot> snapshots = await ReadResourcesAsync(bundle.Resources.Select(r => new PluginResourceInput(r.Name,
                CompilerWire.ResolvePath(root, r.RelativePath), r.RelativePath, r.Rid)).ToArray(), cancellationToken).ConfigureAwait(false);
            foreach (ResourceSnapshot snapshot in snapshots)
            {
                PluginArtifactResource expected = bundle.Resources.Single(r => r.Name == snapshot.Resource.Name && r.Rid == snapshot.Resource.Rid);
                if (expected.Sha256 != snapshot.Resource.Sha256) throw new InvalidDataException($"The prebuilt resource hash does not match: {expected.RelativePath}");
            }
            PluginArtifactResource[] resources = snapshots.Select(s => s.Resource).ToArray();
            string fingerprint = CompilerWire.HashParts("prebuilt-v1", bundle.AssemblySha256, bundle.SdkFingerprint, bundle.CompilerFingerprint);
            string revision = ExecutionRevision(fingerprint, resources);
            Directory.CreateDirectory(_cache);
            string artifactRoot = CompilerWire.ResolvePath(_cache, "artifacts");
            string stagingRoot = CompilerWire.ResolvePath(_cache, "staging");
            Directory.CreateDirectory(artifactRoot);
            Directory.CreateDirectory(stagingRoot);
            string destination = CompilerWire.ResolvePath(artifactRoot, revision);
            if (Directory.Exists(destination))
            {
                PluginArtifact? cached = await FindArtifactAsync(revision, cancellationToken).ConfigureAwait(false);
                return cached is null ? Failure(PluginBuildStatus.IntegrityError, "MCCACHE", "The cached prebuilt artifact failed verification.", fullPath) :
                    new(PluginBuildStatus.Success, cached, true, bundle.AssemblySha256, fingerprint, []);
            }
            stage = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            await WriteBytesAsync(Path.Combine(stage, "plugin.dll"), assembly, cancellationToken).ConfigureAwait(false);
            foreach (ResourceSnapshot snapshot in snapshots)
            {
                string target = CompilerWire.ResolvePath(stage, snapshot.Resource.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await WriteBytesAsync(target, snapshot.Bytes, cancellationToken).ConfigureAwait(false);
            }
            CompilationEntry compilation = new(1, fingerprint, bundle.AssemblySha256, bundle.AssemblySha256, bundle.SdkFingerprint, bundle.CompilerFingerprint, []);
            await WriteJsonAsync(Path.Combine(stage, "artifact.json"), new ArtifactEntry(1, revision, compilation, resources), cancellationToken).ConfigureAwait(false);
            if (await ValidateArtifactAsync(stage, revision, cancellationToken).ConfigureAwait(false) is null)
                throw new InvalidDataException("The prepared prebuilt artifact failed verification.");
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(stage, destination);
            stage = null;
            PluginArtifact? artifact = await FindArtifactAsync(revision, cancellationToken).ConfigureAwait(false);
            return artifact is null ? Failure(PluginBuildStatus.IntegrityError, "MCCACHE", "The imported bundle failed verification.", fullPath) :
                new(PluginBuildStatus.Success, artifact, false, bundle.AssemblySha256, fingerprint, []);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return Failure(PluginBuildStatus.Cancelled, "MCCANCEL", "Bundle import was cancelled.", manifestPath); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return Failure(exception is InvalidDataException ? PluginBuildStatus.IntegrityError : PluginBuildStatus.IoError, "MCBUNDLE", exception.Message, manifestPath); }
        finally
        {
            if (stage is not null) TryDelete(stage);
            if (acquired) BuildGate.Release();
            Interlocked.Decrement(ref _admittedBuilds);
        }
    }
}
