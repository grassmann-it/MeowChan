using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Diagnostics;
using MeowChan.Downloader;
using MeowChan.Persistence;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Runtime;
using MeowChan.PluginSdk;
using MeowChan.Storage;

namespace MeowChan.PluginHost.Filters;

/// <summary>Profile-owned original staging on the existing queue; never downloads or mutates captured posts.</summary>
public sealed class FilterStageCoordinator : IAsyncDisposable, IOriginalMediaFilterFactory
{
    private const string Owner = "MeowChan original filter stage v1";
    private readonly string _directory;
    private readonly ProfileLease _profileLease;
    private readonly FilterStageOptions _options;
    private readonly SemaphoreSlim _slots;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<string, Lease> _assets = new(StringComparer.Ordinal);
    private readonly object _state = new();
    private Func<FilterBindingRequest, CancellationToken, Task<FilterPluginBinding?>>? _resolve;
    private int _active, _waiting, _recovered;
    private readonly FilterTemporaryBudget _budget;
    private readonly FilterStageLedger _ledger;
    private readonly bool _sharedWorkerBudget;
    private readonly object _sampledWorker = new();
    private event Action? CapacityChanged;

    public FilterStageCoordinator(string profileDirectory, ProfileLease profileLease, FilterStageOptions? options = null)
    {
        _options = options ?? new FilterStageOptions();
        if (_options.MaximumConcurrentChecks is < 1 or > 2 || _options.MaximumImageBytes is < 1 or > SdkLimits.MaximumImageBytes
            || _options.MaximumTemporaryBytes < _options.MaximumImageBytes || _options.MaximumTemporaryBytes > 512L * 1024 * 1024
            || _options.MaximumDecodedPixels is < 1 or > SdkLimits.MaximumDecodedPixels) throw new ArgumentOutOfRangeException(nameof(options));
        _profileLease = profileLease;
        _profileLease.EnsureMatches(ProfileLease.NormalizeDirectory(profileDirectory));
        _directory = Path.Combine(ProfileLease.NormalizeDirectory(profileDirectory), "filter-stages");
        Directory.CreateDirectory(_directory);
        if ((File.GetAttributes(_directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Filter stage directory must not be a link.");
        _slots = new SemaphoreSlim(_options.MaximumConcurrentChecks);
        // Existing session wiring supplies Manager.GetTemporaryBytes as a method group.
        // Join that manager's budget, so observations and promises use one atomic owner.
        PluginWorkerManager? manager = _options.WorkerTemporaryBytes.Target as PluginWorkerManager;
        _sharedWorkerBudget = manager != null;
        _budget = manager?.TemporaryBudget ?? new FilterTemporaryBudget(_options.MaximumTemporaryBytes);
        _budget.Limit(_options.MaximumTemporaryBytes);
        _ledger = new(_directory, _budget, _options.MaximumTemporaryBytes);
        RecoverOrphans();
        _ledger.Reconcile();
    }

    public FilterStageSnapshot Snapshot
    {
        get
        {
            int active, waiting;
            lock (_state) { active = _active; waiting = _waiting; }
            lock (_budget.Sync) return new(active, waiting, _ledger.Bytes, _budget.WorkerBytes, _recovered);
        }
    }
    internal bool BudgetIsCurrent => _ledger.IsCurrent;
    internal Action? FileCreatedForTest { get; set; }
    public void Attach(ThreadContentRuntime content, Func<FilterBindingRequest, CancellationToken, Task<FilterPluginBinding?>> resolve)
    {
        ArgumentNullException.ThrowIfNull(content); ArgumentNullException.ThrowIfNull(resolve);
        _resolve = resolve; content.OriginalFilters = this;
    }

    IOriginalMediaFilter IOriginalMediaFilterFactory.Create(DownloadItem item, CandidateDecision decision,
        Func<bool> isCurrent, Func<bool?, string, CancellationToken, Task> recordOmission)
        => new Candidate(this, decision, isCurrent, recordOmission);

    public async Task<ImageAssetChunk> ReadImageAsync(string pluginId, string assetToken, long offset, int count,
        PluginOperationContext operation, CancellationToken cancellationToken)
    {
        if (count is < 1 or > SdkLimits.MaximumImageChunkBytes || offset < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (!_assets.TryGetValue(assetToken, out Lease? lease) || !lease.Readable || lease.Binding.PluginId != pluginId
            || operation != lease.Binding.Operation) throw new UnauthorizedAccessException("Image token is not bound to this live plugin operation.");
        lease.EnsureCurrent(); cancellationToken.ThrowIfCancellationRequested();
        FileStream file = lease.Image ?? throw new UnauthorizedAccessException("Image stage is not readable.");
        if (offset > file.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        byte[] bytes = new byte[(int)Math.Min(count, file.Length - offset)];
        int total = 0;
        while (total < bytes.Length)
        {
            int read = await RandomAccess.ReadAsync(file.SafeFileHandle, bytes.AsMemory(total), offset + total, cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("The bound stage changed.");
            total += read;
        }
        lease.EnsureCurrent();
        return new ImageAssetChunk(offset, bytes, offset + total == file.Length);
    }

    private void RecoverOrphans()
    {
        _profileLease.RunExclusive(() => {
            int inspected = 0;
            foreach (string journal in Directory.EnumerateFiles(_directory, "stage-*.json*"))
            {
                if (++inspected > 1024) throw new InvalidDataException("Filter recovery journal limit exceeded.");
                bool pending = journal.EndsWith(".json.next", StringComparison.Ordinal);
                if (!pending && !journal.EndsWith(".json", StringComparison.Ordinal)) continue;
                string canonical = pending ? journal[..^5] : journal;
                if (pending && File.Exists(canonical)) continue;
                byte[]? bytes = StageRecoveryFile.ReadJournal(journal);
                if (bytes == null) continue;
                StageJournal? record;
                try { record = JsonSerializer.Deserialize<StageJournal>(bytes); } catch (JsonException) { continue; }
                if (record?.Owner != Owner || !Guid.TryParseExact(record.Id, "N", out _)
                    || Path.GetFileName(canonical) != "stage-" + record.Id + ".json") continue;
                string image = Path.Combine(_directory, "stage-" + record.Id + ".bytes");
                try
                {
                    if (File.Exists(image) && (File.GetAttributes(image) & FileAttributes.ReparsePoint) != 0)
                    {
                        if (record.TargetTemporary != null) _ledger.Retain(canonical, _options.MaximumTemporaryBytes + 1);
                        continue;
                    }
                    if (record.TargetTemporary != null && (record.Destination == null
                        || !MediaStore.TryRecoverFilterTemporary(record.Destination, record.TargetTemporary)))
                    {
                        // Do not stat an untrusted journal path. Charge an upper bound
                        // and preserve its journal for the next recovery attempt.
                        _ledger.Retain(canonical, _options.MaximumTemporaryBytes + 1);
                        continue;
                    }
                    File.Delete(image); File.Delete(canonical + ".next"); File.Delete(canonical); _recovered++;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    // The inventory below adopts every undeleted stage file. If the
                    // external target could not be inspected, retain an upper bound.
                    if (record.TargetTemporary != null) _ledger.Retain(canonical, _options.MaximumTemporaryBytes + 1);
                }
            }
            return true;
        });
    }

    private bool CanStart { get { lock (_state) return !_shutdown.IsCancellationRequested && _active < _options.MaximumConcurrentChecks
        ; } }
    private void CheckBudget()
    {
        if (!_sharedWorkerBudget)
        {
            // Compatibility for callers without a manager. Never call user code under
            // the shared lock (it may itself read Snapshot).
            _budget.ObserveWorker(_sampledWorker, Math.Max(0, _options.WorkerTemporaryBytes()));
        }
        _ledger.Check();
    }

    /// <summary>Bounded lifecycle inventories; never proportional to the number of chunks.</summary>
    internal long DirectoryScanCount => _ledger.ScanCount;

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        // Each download lease is disposed by its queue worker. Waiting here also fences profile shutdown.
        for (int i = 0; i < _options.MaximumConcurrentChecks; i++) await _slots.WaitAsync().ConfigureAwait(false);
        _slots.Dispose(); _shutdown.Dispose();
    }

    private sealed class Candidate(FilterStageCoordinator owner, CandidateDecision decision, Func<bool> current,
        Func<bool?, string, CancellationToken, Task> record) : IOriginalMediaFilter
    {
        public bool CanStart => owner.CanStart;
        public event Action? CapacityChanged { add => owner.CapacityChanged += value; remove => owner.CapacityChanged -= value; }
        public async Task<IOriginalMediaFilterLease> AcquireAsync(DownloadItem item, CancellationToken token)
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, owner._shutdown.Token);
            lock (owner._state) owner._waiting++;
            try { await owner._slots.WaitAsync(linked.Token).ConfigureAwait(false); }
            finally { lock (owner._state) owner._waiting--; }
            lock (owner._state) owner._active++;
            try
            {
                if (!current()) throw new MediaFilterOmissionException(false, "filter_rule_changed");
                MeowChan.Rules.EffectiveMediaRule effective = decision.Selection.EffectiveRule;
                if (effective.Types.Mode == MeowChan.Rules.MediaTypeSelectionMode.Selected && !effective.Types.Extensions.Contains(decision.Metadata.Extension)
                    || effective.SizeLimit.Mode == MeowChan.Rules.MediaSizeLimitMode.Limited && item.ExpectedSize > effective.SizeLimit.Bytes)
                    throw new MediaFilterOmissionException(true, "filter_metadata_excluded");
                owner._ledger.Reconcile();
                owner.CheckBudget();
                if (item.FilterPostId == null) throw new MediaFilterOmissionException(false, "filter_candidate_post_missing");
                if (item.ExpectedSize > owner._options.MaximumImageBytes) throw new MediaFilterOmissionException(false, "filter_image_byte_limit");
                string ruleId = FilterRuleIdentity.ForScope(decision.Selection.EffectiveRule.ImageFilterOrigin?.Scope ?? MeowChan.Rules.MediaRuleScope.Global);
                FilterPluginBinding? binding = owner._resolve == null ? null : await owner._resolve(new(item, decision, ruleId), linked.Token).ConfigureAwait(false);
                if (binding == null || !binding.IsCurrent()) throw new MediaFilterOmissionException(false, "filter_plugin_or_grant_unavailable");
                if (binding.PluginId != decision.Selection.EffectiveRule.ImageFilter.PluginId || binding.Operation.RuleRevision != decision.Selection.EffectiveRule.Revision
                    || binding.Operation.GrantRevision != binding.GrantRevision) throw new MediaFilterOmissionException(false, "filter_binding_invalid");
                return new Lease(owner, decision, binding, current, record, token);
            }
            catch (MediaFilterOmissionException exception)
            {
                try { await record(exception.IsNoMatch, exception.ReasonCode, token).ConfigureAwait(false); }
                finally { Release(); }
                throw;
            }
            catch (IOException exception)
            {
                try { await record(false, "filter_stage_io", token).ConfigureAwait(false); }
                finally { Release(); }
                throw new MediaFilterOmissionException(false, "filter_stage_io", exception.Message);
            }
            catch { Release(); throw; }
        }
        private void Release() { lock (owner._state) owner._active--; owner._slots.Release(); owner.CapacityChanged?.Invoke(); }
    }

    private sealed class Lease : IOriginalMediaFilterLease
    {
        private readonly FilterStageCoordinator _owner;
        private readonly CandidateDecision _decision;
        private readonly Func<bool> _current;
        private readonly Func<bool?, string, CancellationToken, Task> _record;
        private readonly CancellationTokenSource _lifetime;
        private readonly string _id = Guid.NewGuid().ToString("N");
        private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        private readonly long _request = PerformanceDiagnostics.NextId();
        private long _size, _reads;
        private string? _targetTemporary;
        private bool _disposed;
        private ISourceMediaIntegrityPolicy? _sourcePolicy;
        private SourceMediaIntegrityPolicySnapshot? _sourcePermission;
        internal FilterPluginBinding Binding { get; }
        internal FileStream? Image { get; private set; }
        internal bool Readable { get; private set; }
        private string ImagePath => Path.Combine(_owner._directory, "stage-" + _id + ".bytes");
        private string JournalPath => Path.Combine(_owner._directory, "stage-" + _id + ".json");

        internal Lease(FilterStageCoordinator owner, CandidateDecision decision, FilterPluginBinding binding, Func<bool> current,
            Func<bool?, string, CancellationToken, Task> record, CancellationToken token)
        {
            _owner = owner; _decision = decision; Binding = binding; _current = current; _record = record;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, owner._shutdown.Token);
        }
        internal void EnsureCurrent()
        {
            EnsureAuthorized();
            _owner.CheckBudget();
        }

        /// <summary>Cancellation, disposal, rule/watch/binding and source-permission checks without a budget scan.</summary>
        private void EnsureAuthorized()
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            if (_disposed || !_current() || !Binding.IsCurrent()) throw new MediaFilterOmissionException(false, "filter_binding_changed");
            if (_sourcePermission != null && _sourcePolicy?.GetSnapshot() != _sourcePermission)
                throw new MediaFilterOmissionException(false, "filter_source_permission_changed");
        }

        public async Task<MediaCommitResult> CommitAsync(DownloadItem item, Stream source, MediaStore store,
            Func<int, CancellationToken, Task> throttle, CancellationToken token)
        {
            using var measurement = PerformanceDiagnostics.Measure("filter.stage", _request,
                watch: _decision.Metadata.Key.Resource.ToString(), asset: _id, epoch: Binding.Operation.Generation);
            bool committingToStore = false;
            try
            {
                EnsureCurrent();
                SourceMediaIntegrityPolicySnapshot? startedPermission = item.MediaIntegrityPolicy?.GetSnapshot();
                await SaveJournalAsync(null, 0, token).ConfigureAwait(false);
                using IncrementalHash sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                using IncrementalHash sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                _owner._ledger.Reserve(ImagePath, 0);
                await using (FileStream file = new(ImagePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                {
                    _owner.FileCreatedForTest?.Invoke();
                    byte[] buffer = new byte[65536]; int read;
                    while ((read = await source.ReadAsync(buffer, _lifetime.Token).ConfigureAwait(false)) > 0)
                    {
                        await throttle(read, _lifetime.Token).ConfigureAwait(false);
                        if (item.MaximumActualBytes is long actualMaximum && _size + read > actualMaximum
                            || _decision.Selection.EffectiveRule.SizeLimit.Mode == MeowChan.Rules.MediaSizeLimitMode.Limited
                                && _size + read > _decision.Selection.EffectiveRule.SizeLimit.Bytes)
                            throw new MediaFilterOmissionException(true, "filter_actual_rule_byte_limit");
                        if (_size + read > _owner._options.MaximumImageBytes) throw new MediaFilterOmissionException(false, "filter_image_byte_limit");
                        EnsureAuthorized(); _owner.CheckBudget();
                        _owner._ledger.Grow(ImagePath, read); _size += read; _reads++;
                        sha.AppendData(buffer, 0, read); md5.AppendData(buffer, 0, read); sha1.AppendData(buffer, 0, read);
                        await file.WriteAsync(buffer.AsMemory(0, read), _lifetime.Token).ConfigureAwait(false);
                    }
                    await file.FlushAsync(_lifetime.Token).ConfigureAwait(false); file.Flush(true);
                }
                string digest = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
                string actualMd5 = Convert.ToBase64String(md5.GetHashAndReset());
                try
                {
                    _sourcePermission = MediaIntegrityValidation.AuthorizeAssessment(item, new MediaActualIntegrity(_size, actualMd5, digest) { Sha1 = Convert.ToBase64String(sha1.GetHashAndReset()) });
                    _sourcePolicy = item.MediaIntegrityPolicy;
                    if (_sourcePermission != null && _sourcePermission != startedPermission)
                        throw new InvalidDataException("Source permission changed during filter staging.");
                }
                catch (InvalidDataException exception)
                { throw new MediaFilterOmissionException(false, "filter_original_integrity", exception.Message); }
                await SaveJournalAsync(digest, _size, _lifetime.Token).ConfigureAwait(false);
                Image = new FileStream(ImagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.RandomAccess);
                (int width, int height) dimensions;
                try { dimensions = await ImageHeader.ReadAsync(Image, _lifetime.Token).ConfigureAwait(false); }
                catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException)
                { throw new MediaFilterOmissionException(false, "filter_image_header", exception.Message); }
                if ((long)dimensions.width * dimensions.height > _owner._options.MaximumDecodedPixels)
                    throw new MediaFilterOmissionException(false, "filter_image_pixel_limit");
                Image.Position = 0;
                EnsureCurrent(); Readable = true; _owner._assets[_token] = this;
                MeowChan.ResourceId resource = _decision.Metadata.Key.Resource;
                string revision = MediaIntegrityValidation.Acquisition(item);
                ImageFilterRequest request = new(_decision.Selection.EffectiveRule.ImageFilter.FilterKey!,
                    new FilterCandidate(new SourceMediaId(new SourceThreadId(resource.Source, resource.Board, resource.Thread!),
                        item.MediaIdentifier, _decision.Metadata.Key.Variant), item.FilterPostId!, item.FilterPosition, item.DownloadUri, item.OriginalFileName),
                    revision, revision, _token, _size, digest, _decision.Selection.EffectiveRule.Revision, Binding.RuleSettings,
                    Binding.ExecutionRevision, Binding.ConfigurationRevision, Binding.GrantRevision);
                PerformanceDiagnostics.Record("filter.stage.bytes", "verified", _request, asset: _id, value: _size);
                PerformanceDiagnostics.Record("filter.stage.reads", "accepted", _request, asset: _id, value: _reads);
                ImageFilterResult result;
                using var evaluation = PerformanceDiagnostics.Measure("filter.evaluate", _request, asset: _id);
                try { result = await Binding.EvaluateAsync(request, Binding.Operation, _lifetime.Token).ConfigureAwait(false); }
                catch (PluginWorkerException exception) { throw new MediaFilterOmissionException(false, exception.Code, exception.Message); }
                catch (TimeoutException exception) { throw new MediaFilterOmissionException(false, "filter_timeout", exception.Message); }
                catch (Exception exception) when (exception is not OperationCanceledException) { throw new MediaFilterOmissionException(false, "filter_worker_error", exception.Message); }
                finally { evaluation.Dispose(); Readable = false; _owner._assets.TryRemove(_token, out _); }
                EnsureCurrent();
                if (result.Outcome != ImageFilterOutcome.Match)
                    throw new MediaFilterOmissionException(result.Outcome == ImageFilterOutcome.NoMatch, result.ReasonCode, result.Message);
                Image.Position = 0;
                // MediaStore copies into the target filesystem before atomic publication. Reserve that own temporary copy too.
                _owner._ledger.Reconcile();
                EnsureCurrent();
                MediaCommitResult committed;
                try { committingToStore = true; committed = await store.CommitVerifiedAsync(item, Image, digest, EnsureCurrent, _lifetime.Token, async (path, cancellation) => {
                    _owner._ledger.Reserve(path, _size);
                    PerformanceDiagnostics.Record("filter.target.bytes", "reserved", _request, asset: _id, value: _size);
                    _targetTemporary = path;
                    try { await SaveJournalAsync(digest, _size, cancellation).ConfigureAwait(false); }
                    catch (IOException exception) { throw new MediaFilterOmissionException(false, "filter_stage_io", exception.Message); }
                }).ConfigureAwait(false); }
                finally
                {
                    // MediaStore has either published, removed its .part, or left it
                    // for recovery. Retain the promise until absence is confirmed.
                    if (_targetTemporary != null && TryConfirmAbsent(_targetTemporary))
                    {
                        _owner._ledger.Deleted(_targetTemporary); _targetTemporary = null;
                    }
                }
                await _record(null, result.ReasonCode, CancellationToken.None).ConfigureAwait(false);
                return committed;
            }
            catch (IOException exception) when (!committingToStore)
            {
                await _record(false, "filter_stage_io", token).ConfigureAwait(false);
                throw new MediaFilterOmissionException(false, "filter_stage_io", exception.Message);
            }
            catch (MediaFilterOmissionException exception)
            {
                await _record(exception.IsNoMatch, exception.ReasonCode, token).ConfigureAwait(false); throw;
            }
        }

        private async Task SaveJournalAsync(string? hash, long size, CancellationToken token)
        {
            StageJournal journal = new(Owner, _id, _decision.Metadata.Key.Resource.ToString(), _decision.Metadata.Key.MediaId,
                Binding.PluginId, Binding.ExecutionRevision, Binding.ConfigurationRevision, Binding.GrantRevision,
                Binding.Operation.Generation, _decision.Selection.EffectiveRule.Revision, hash, size, _decision.Metadata.DestinationPath, _targetTemporary);
            string temporary = JournalPath + ".next";
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(journal);
            if (bytes.Length > 64 * 1024) throw new IOException("Filter recovery journal exceeds its bounded size.");
            _owner.CheckBudget(); _owner._ledger.Reserve(temporary, bytes.Length);
            await using (FileStream file = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                _owner.FileCreatedForTest?.Invoke();
                await file.WriteAsync(bytes, token).ConfigureAwait(false);
                await file.FlushAsync(token).ConfigureAwait(false); file.Flush(true);
            }
            File.Move(temporary, JournalPath, true);
            _owner._ledger.Renamed(temporary, JournalPath);
        }
        private static bool TryConfirmAbsent(string path)
        {
            try { _ = File.GetAttributes(path); return false; }
            catch (FileNotFoundException) { return true; }
            catch (DirectoryNotFoundException) { return true; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        }
        private bool Delete(string path, ref Exception? failure)
        {
            try { File.Delete(path); _owner._ledger.Deleted(path); return true; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failure ??= error; return false; }
        }
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return; _disposed = true; Readable = false; _owner._assets.TryRemove(_token, out _);
            Exception? failure = null;
            try
            {
                try { await _lifetime.CancelAsync().ConfigureAwait(false); }
                finally
                {
                    bool imageClosed = true;
                    if (Image != null)
                    {
                        try { await Image.DisposeAsync().ConfigureAwait(false); }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failure ??= error; imageClosed = false; }
                    }
                    bool targetGone = _targetTemporary == null;
                    if (!targetGone && MediaStore.TryRecoverFilterTemporary(_decision.Metadata.DestinationPath!, _targetTemporary!))
                    {
                        _owner._ledger.Deleted(_targetTemporary!); targetGone = true;
                    }
                    bool imageGone = imageClosed && Delete(ImagePath, ref failure);
                    // Keep the last complete journal when bytes remain. It is the
                    // authority for recovery, especially for a target on another disk.
                    if (targetGone && imageGone)
                    {
                        bool pendingGone = Delete(JournalPath + ".next", ref failure);
                        if (pendingGone) Delete(JournalPath, ref failure);
                    }
                    if (!targetGone) failure ??= new IOException("Filter target temporary could not be removed.");
                }
                PerformanceDiagnostics.Record("filter.cleanup", failure == null ? "removed" : "retained", _request, asset: _id);
                if (failure != null) throw new MediaFilterOmissionException(false, "filter_stage_cleanup_io", failure.Message);
            }
            finally
            {
                _owner._ledger.Reconcile();
                _lifetime.Dispose();
                lock (_owner._state) _owner._active--;
                _owner._slots.Release(); _owner.CapacityChanged?.Invoke();
            }
        }
    }

    private sealed record StageJournal(string Owner, string Id, string Resource, string MediaId, string PluginId,
        string ExecutionRevision, string ConfigurationRevision, string GrantRevision, long Generation,
        string RuleRevision, string? Sha256, long Size, string? Destination = null, string? TargetTemporary = null);
}
