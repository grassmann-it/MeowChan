using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Downloader;
using MeowChan.Persistence;
using MeowChan.PluginHost.Contracts;
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
    private long _bytes;
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
        RecoverOrphans();
    }

    public FilterStageSnapshot Snapshot { get { lock (_state) return new(_active, _waiting, _bytes, _options.WorkerTemporaryBytes(), _recovered); } }
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
            foreach (string journal in Directory.EnumerateFiles(_directory, "stage-*.json"))
            {
                if ((File.GetAttributes(journal) & FileAttributes.ReparsePoint) != 0 || new FileInfo(journal).Length > 64 * 1024) continue;
                StageJournal? record;
                try { record = JsonSerializer.Deserialize<StageJournal>(File.ReadAllBytes(journal)); } catch (JsonException) { continue; }
                if (record?.Owner != Owner || !Guid.TryParseExact(record.Id, "N", out _)
                    || Path.GetFileName(journal) != "stage-" + record.Id + ".json") continue;
                string image = Path.Combine(_directory, "stage-" + record.Id + ".bytes");
                if (File.Exists(image) && (File.GetAttributes(image) & FileAttributes.ReparsePoint) != 0) continue;
                File.Delete(image); File.Delete(journal + ".next"); File.Delete(journal); _recovered++;
            }
            return true;
        });
    }

    private bool CanStart { get { lock (_state) return !_shutdown.IsCancellationRequested && _active < _options.MaximumConcurrentChecks
        ; } }
    private void CheckBudget(long add = 0)
    {
        lock (_state)
        {
            if (_bytes + add + _options.WorkerTemporaryBytes() > _options.MaximumTemporaryBytes)
                throw new MediaFilterOmissionException(false, "filter_temp_limit", "Filter temporary data limit exceeded.");
            _bytes += add;
        }
    }

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
                await record(exception.IsNoMatch, exception.ReasonCode, token).ConfigureAwait(false); Release(); throw;
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
        private long _size;
        private bool _disposed;
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
            _lifetime.Token.ThrowIfCancellationRequested();
            if (_disposed || !_current() || !Binding.IsCurrent()) throw new MediaFilterOmissionException(false, "filter_binding_changed");
            _owner.CheckBudget();
        }

        public async Task<MediaCommitResult> CommitAsync(DownloadItem item, Stream source, MediaStore store,
            Func<int, CancellationToken, Task> throttle, CancellationToken token)
        {
            try
            {
                EnsureCurrent();
                await SaveJournalAsync(null, 0, token).ConfigureAwait(false);
                using IncrementalHash sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                await using (FileStream file = new(ImagePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                {
                    byte[] buffer = new byte[65536]; int read;
                    while ((read = await source.ReadAsync(buffer, _lifetime.Token).ConfigureAwait(false)) > 0)
                    {
                        await throttle(read, _lifetime.Token).ConfigureAwait(false);
                        if (_size + read > _owner._options.MaximumImageBytes) throw new MediaFilterOmissionException(false, "filter_image_byte_limit");
                        EnsureCurrent(); _owner.CheckBudget(read); _size += read;
                        sha.AppendData(buffer, 0, read); md5.AppendData(buffer, 0, read);
                        await file.WriteAsync(buffer.AsMemory(0, read), _lifetime.Token).ConfigureAwait(false);
                    }
                    await file.FlushAsync(_lifetime.Token).ConfigureAwait(false); file.Flush(true);
                }
                string digest = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
                string actualMd5 = Convert.ToBase64String(md5.GetHashAndReset());
                if (item.ExpectedSize is long expected && expected != _size || item.ExpectedMd5 is string expectedMd5 && expectedMd5 != actualMd5)
                    throw new InvalidDataException("Filter original does not match provider integrity metadata.");
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
                string revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new object?[] {
                    item.DownloadUri.AbsoluteUri, item.ExpectedSize, item.ExpectedMd5, item.OriginalFileName })))).ToLowerInvariant();
                ImageFilterRequest request = new(_decision.Selection.EffectiveRule.ImageFilter.FilterKey!,
                    new FilterCandidate(new SourceMediaId(new SourceThreadId(resource.Source, resource.Board, resource.Thread!),
                        item.MediaIdentifier, _decision.Metadata.Key.Variant), item.FilterPostId!, item.FilterPosition, item.DownloadUri, item.OriginalFileName),
                    revision, revision, _token, _size, digest, _decision.Selection.EffectiveRule.Revision, Binding.RuleSettings,
                    Binding.ExecutionRevision, Binding.ConfigurationRevision, Binding.GrantRevision);
                ImageFilterResult result;
                try { result = await Binding.EvaluateAsync(request, Binding.Operation, _lifetime.Token).ConfigureAwait(false); }
                catch (PluginWorkerException exception) { throw new MediaFilterOmissionException(false, exception.Code, exception.Message); }
                catch (TimeoutException exception) { throw new MediaFilterOmissionException(false, "filter_timeout", exception.Message); }
                catch (Exception exception) when (exception is not OperationCanceledException) { throw new MediaFilterOmissionException(false, "filter_worker_error", exception.Message); }
                finally { Readable = false; _owner._assets.TryRemove(_token, out _); }
                EnsureCurrent();
                if (result.Outcome != ImageFilterOutcome.Match)
                    throw new MediaFilterOmissionException(result.Outcome == ImageFilterOutcome.NoMatch, result.ReasonCode, result.Message);
                Image.Position = 0;
                // MediaStore copies into the target filesystem before atomic publication. Reserve that own temporary copy too.
                _owner.CheckBudget(_size);
                MediaCommitResult committed;
                try { committed = await store.CommitVerifiedAsync(item, Image, digest, EnsureCurrent, _lifetime.Token).ConfigureAwait(false); }
                finally { lock (_owner._state) _owner._bytes -= _size; }
                await _record(null, result.ReasonCode, CancellationToken.None).ConfigureAwait(false);
                return committed;
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
                Binding.Operation.Generation, _decision.Selection.EffectiveRule.Revision, hash, size);
            string temporary = JournalPath + ".next";
            await using (FileStream file = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(file, journal, cancellationToken: token).ConfigureAwait(false);
                await file.FlushAsync(token).ConfigureAwait(false); file.Flush(true);
            }
            File.Move(temporary, JournalPath, true);
        }
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return; _disposed = true; Readable = false; _owner._assets.TryRemove(_token, out _);
            await _lifetime.CancelAsync().ConfigureAwait(false);
            if (Image != null) await Image.DisposeAsync().ConfigureAwait(false);
            try { File.Delete(ImagePath); File.Delete(JournalPath + ".next"); File.Delete(JournalPath); }
            finally
            {
                _lifetime.Dispose();
                lock (_owner._state) { _owner._bytes -= _size; _owner._active--; }
                _owner._slots.Release(); _owner.CapacityChanged?.Invoke();
            }
        }
    }

    private sealed record StageJournal(string Owner, string Id, string Resource, string MediaId, string PluginId,
        string ExecutionRevision, string ConfigurationRevision, string GrantRevision, long Generation,
        string RuleRevision, string? Sha256, long Size);
}
