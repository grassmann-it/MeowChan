using System.Text.Json;
using MeowChan.Diagnostics;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Installation;

namespace MeowChan.PluginHost.Compilation;

public sealed partial class PluginCompilationService : IPluginBundleInstaller
{
    public Task<PluginBuildResult> ImportBundleAsync(string manifestPath, CancellationToken cancellationToken = default)
    {
        // A single import is a pass of its own: it always verifies the toolchain, and ending it ends that verification.
        ImportBatch single = new(this, eager: false);
        return ScheduleOperation(async token =>
        {
            try { return await ImportBundleCoreAsync(manifestPath, single, token).ConfigureAwait(false); }
            finally { single.Dispose(); }
        }, manifestPath, cancellationToken);
    }

    /// <summary>Starts verifying the delivered toolchain at once, off the build gate, while the caller still prepares the pass.</summary>
    public IPluginBundleImportBatch BeginImportBatch() => new ImportBatch(this, eager: true);

    /// <summary>Carries the one full toolchain verification of a pass. It lives and dies with the batch: a later pass,
    /// restart or service always verifies the toolchain again, and a failed verification is never kept. The hashing runs
    /// outside the build gate; every import re-checks inside the gate, right before it uses the result, that no verified
    /// file changed since it was hashed, and verifies everything again (fail closed) if anything did.</summary>
    private sealed class ImportBatch : IPluginBundleImportBatch
    {
        private readonly PluginCompilationService _owner;
        private readonly CancellationTokenSource _cancel = new();
        private readonly object _sync = new();
        private Task<ToolchainVerification>? _verification;
        private bool _disposed;

        internal ImportBatch(PluginCompilationService owner, bool eager)
        {
            _owner = owner;
            if (eager) _verification = owner.StartToolchainVerification(_cancel.Token);
        }

        internal bool Cancelled => _cancel.IsCancellationRequested;

        public Task<PluginBuildResult> ImportBundleAsync(string manifestPath, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
            return _owner.ScheduleOperation(token => _owner.ImportBundleCoreAsync(manifestPath, this, token), manifestPath, cancellationToken);
        }

        /// <summary>The pass's verification, started now if there is none (first use, or the last one failed or went stale).</summary>
        internal async Task<(Task<ToolchainVerification> Source, ToolchainVerification Result)> AcquireAsync(CancellationToken cancellationToken)
        {
            Task<ToolchainVerification> source;
            lock (_sync) source = _verification ??= _owner.StartToolchainVerification(_cancel.Token);
            try { return (source, await source.WaitAsync(cancellationToken).ConfigureAwait(false)); }
            catch when (source.IsCompleted && !source.IsCompletedSuccessfully) { Forget(source); throw; }
        }

        /// <summary>A failed or stale result is dropped, so the next import verifies again.</summary>
        internal void Forget(Task<ToolchainVerification> source)
        {
            lock (_sync) if (ReferenceEquals(_verification, source)) _verification = null;
        }

        internal void Offer(ToolchainVerification fresh)
        {
            lock (_sync) if (!_disposed) _verification ??= Task.FromResult(fresh);
        }

        public void Dispose()
        {
            lock (_sync)
            {
                Volatile.Write(ref _disposed, true);
                _verification = null;
            }
            _cancel.Cancel();
        }
    }

    // Tracked like an operation, so DisposeAsync cancels it and waits until no file is open any more.
    private Task<ToolchainVerification> StartToolchainVerification(CancellationToken pass)
    {
        lock (_operationsGate)
        {
            if (_disposeTask is not null) return Task.FromCanceled<ToolchainVerification>(new CancellationToken(true));
            CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(pass, _lifetime.Token);
            Task<ToolchainVerification> task = RunAsync();
            // The tracked continuation never faults (DisposeAsync awaits it) and observes the verification's failure.
            Task tracked = task.ContinueWith(completed => { _ = completed.Exception; linked.Dispose(); }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            _operations.Add(tracked);
            _ = tracked.ContinueWith(completed => { lock (_operationsGate) _operations.Remove(completed); },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;

            async Task<ToolchainVerification> RunAsync()
            {
                using (PerformanceDiagnostics.Measure("plugin.import.toolchain"))
                    return await ToolchainVerification.VerifyAsync(_options.ToolchainDirectory, linked.Token).ConfigureAwait(false);
            }
        }
    }

    private async Task<PluginBuildResult> ImportBundleCoreAsync(string manifestPath, ImportBatch batch, CancellationToken cancellationToken)
    {
        string? stage = null;
        bool acquired = false;
        // Opt-in startup evidence: sub-phase spans on the report clock, no allocation without an active run.
        string? trace = PerformanceDiagnostics.Enabled ? Path.GetFileName(manifestPath) : null;
        PerformanceDiagnostics.Measurement span = default;
        try
        {
            // Reading the shipped manifest and waiting for the toolchain need no gate: the gate guards the cache only.
            string fullPath = Path.GetFullPath(manifestPath);
            string root = Path.GetDirectoryName(fullPath)!;
            span = PerformanceDiagnostics.Measure("plugin.import.manifest", asset: trace);
            PluginBundleManifest bundle = await ReadJsonAsync<PluginBundleManifest>(fullPath, cancellationToken).ConfigureAwait(false);
            span.Dispose();
            if (bundle.SchemaVersion != 1 || !CompilerWire.IsHash(bundle.AssemblySha256) || !CompilerWire.IsHash(bundle.SdkFingerprint) ||
                !CompilerWire.IsHash(bundle.CompilerFingerprint) || bundle.Resources is null || bundle.Resources.Count > 128 ||
                bundle.Resources.Any(r => r is null || !CompilerWire.IsHash(r.Sha256)))
                throw new InvalidDataException("Unsupported or incomplete plugin bundle manifest.");
            (Task<ToolchainVerification> source, ToolchainVerification toolchain) verified;
            using (PerformanceDiagnostics.Measure("plugin.import.toolchain-wait", asset: trace))
                verified = await batch.AcquireAsync(cancellationToken).ConfigureAwait(false);
            using (PerformanceDiagnostics.Measure("plugin.import.gate-wait", asset: trace))
                await BuildGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            // The use: the verified result must still describe the files on disk now. Any changed, replaced or
            // unreadable file means a full verification again, under the gate, whose failure fails this import.
            ToolchainVerification toolchain = verified.toolchain;
            using (PerformanceDiagnostics.Measure("plugin.import.toolchain-recheck", asset: trace))
                if (!toolchain.Unchanged())
                {
                    PerformanceDiagnostics.Record("plugin.import.toolchain-changed", asset: trace);
                    batch.Forget(verified.source);
                    toolchain = await ToolchainVerification.VerifyAsync(_options.ToolchainDirectory, cancellationToken).ConfigureAwait(false);
                    batch.Offer(toolchain);
                }
            if (bundle.SdkFingerprint != toolchain.Manifest.SdkFingerprint) throw new InvalidDataException("The prebuilt bundle does not match the delivered SDK fingerprint.");
            span = PerformanceDiagnostics.Measure("plugin.import.delivered", asset: trace);
            byte[] assembly = await CompilerWire.ReadBoundedAsync(CompilerWire.ResolvePath(root, bundle.AssemblyRelativePath), 32 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
            if (CompilerWire.Hash(assembly) != bundle.AssemblySha256) throw new InvalidDataException("The prebuilt assembly hash does not match its manifest.");
            List<ResourceSnapshot> snapshots = await ReadResourcesAsync(bundle.Resources.Select(r => new PluginResourceInput(r.Name,
                CompilerWire.ResolvePath(root, r.RelativePath), r.RelativePath, r.Rid)).ToArray(), cancellationToken).ConfigureAwait(false);
            foreach (ResourceSnapshot snapshot in snapshots)
            {
                PluginArtifactResource expected = bundle.Resources.Single(r => r.Name == snapshot.Resource.Name && r.Rid == snapshot.Resource.Rid);
                if (expected.Sha256 != snapshot.Resource.Sha256) throw new InvalidDataException($"The prebuilt resource hash does not match: {expected.RelativePath}");
            }
            span.Dispose();
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
                PluginArtifact? cached;
                using (PerformanceDiagnostics.Measure("plugin.import.cache-validate", asset: trace))
                    cached = await FindArtifactAsync(revision, cancellationToken).ConfigureAwait(false);
                PerformanceDiagnostics.Record("plugin.import.result", cached is null ? "cache-rejected" : "cache-hit", asset: trace);
                return cached is null ? Failure(PluginBuildStatus.IntegrityError, "MCCACHE", "The cached prebuilt artifact failed verification.", fullPath) :
                    new(PluginBuildStatus.Success, cached, true, bundle.AssemblySha256, fingerprint, []);
            }
            stage = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
            span = PerformanceDiagnostics.Measure("plugin.import.stage-write", asset: trace);
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
            span.Dispose();
            using (PerformanceDiagnostics.Measure("plugin.import.stage-validate", asset: trace))
                if (await ValidateArtifactAsync(stage, revision, cancellationToken).ConfigureAwait(false) is null)
                    throw new InvalidDataException("The prepared prebuilt artifact failed verification.");
            cancellationToken.ThrowIfCancellationRequested();
            using (PerformanceDiagnostics.Measure("plugin.import.move", asset: trace))
                Directory.Move(stage, destination);
            stage = null;
            PluginArtifact? artifact;
            using (PerformanceDiagnostics.Measure("plugin.import.publish-validate", asset: trace))
                artifact = await FindArtifactAsync(revision, cancellationToken).ConfigureAwait(false);
            PerformanceDiagnostics.Record("plugin.import.result", artifact is null ? "publish-rejected" : "imported", asset: trace);
            return artifact is null ? Failure(PluginBuildStatus.IntegrityError, "MCCACHE", "The imported bundle failed verification.", fullPath) :
                new(PluginBuildStatus.Success, artifact, false, bundle.AssemblySha256, fingerprint, []);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || batch.Cancelled)
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
