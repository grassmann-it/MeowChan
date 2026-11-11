using System.Text.Json;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Installation;

namespace MeowChan.PluginHost.Compilation;

public sealed partial class PluginCompilationService : IPluginBundleInstaller
{
    public Task<PluginBuildResult> ImportBundleAsync(string manifestPath, CancellationToken cancellationToken = default) =>
        ScheduleOperation(token => ImportBundleCoreAsync(manifestPath, null, token), manifestPath, cancellationToken);

    public IPluginBundleImportBatch BeginImportBatch() => new ImportBatch(this);

    /// <summary>Carries the one full toolchain verification of a pass. It lives and dies with the batch: a later pass,
    /// restart or service always verifies the toolchain again, and a failed verification is never kept.</summary>
    private sealed class ImportBatch(PluginCompilationService owner) : IPluginBundleImportBatch
    {
        private ToolchainManifest? _toolchain;
        private bool _disposed;
        internal ToolchainManifest? Toolchain { get => Volatile.Read(ref _toolchain); set => Volatile.Write(ref _toolchain, value); }

        public Task<PluginBuildResult> ImportBundleAsync(string manifestPath, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
            return owner.ScheduleOperation(token => owner.ImportBundleCoreAsync(manifestPath, this, token), manifestPath, cancellationToken);
        }

        public void Dispose()
        {
            Volatile.Write(ref _disposed, true);
            Toolchain = null;
        }
    }

    private async Task<PluginBuildResult> ImportBundleCoreAsync(string manifestPath, ImportBatch? batch, CancellationToken cancellationToken)
    {
        string? stage = null;
        bool acquired = false;
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
            // The gate serializes imports, so the first one of a batch verifies every toolchain file and the rest reuse that result.
            ToolchainManifest toolchain = batch?.Toolchain
                ?? await CompilerWire.ReadToolchainAsync(_options.ToolchainDirectory, cancellationToken).ConfigureAwait(false);
            if (batch is not null) batch.Toolchain = toolchain;
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
        }
    }
}
