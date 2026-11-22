using System.Security.Cryptography;
using MeowChan.PluginSdk;
using MeowChan.Tags;

namespace MeowChan.PluginHost.Taggers;

/// <summary>Reads a range of one medium's verified bytes. The host media layer owns paths and integrity.</summary>
internal delegate Task<byte[]> TaggerMediaReader(long offset, int count, CancellationToken cancellationToken);

/// <summary>One medium to tag, identified by content. No path or archive identity reaches the tagger.</summary>
internal sealed record TaggerJobItem(string MediaSha256, long Length, TaggerMediaKind Kind, string MimeType,
    TaggerMediaReader Read, int? PixelWidth = null, int? PixelHeight = null, long? DurationMilliseconds = null);

/// <summary>
/// A tagging job. <see cref="Rerun"/> also tags media that this tagger and model revision already tagged (explicit
/// user action, for example after a new model revision); without it a job resumes where an earlier one stopped.
/// </summary>
internal sealed record TaggerJob(TaggerRegistration Tagger, IReadOnlyList<TaggerJobItem> Items, SettingsSnapshot TaggerSettings,
    bool Rerun = false);

internal enum TaggerJobStatus { Completed, Cancelled, ConsentRequired, ModelUnavailable, TimedOut, StoreFailed }

internal sealed record TaggerJobProgress(int Total, int Completed, int Failed, int Skipped);

/// <summary>Counts per job. Skipped: already tagged (resume). Excluded: unsupported kind/type or above the size limits.</summary>
internal sealed record TaggerJobResult(TaggerJobStatus Status, int Total, int Tagged, int Skipped, int Excluded, int Failed,
    int RejectedBatches, int Suggestions);

/// <summary>Which models are installed. Models are never bundled and never downloaded by a tagger or this runner.</summary>
internal interface ITaggerModelCatalog
{
    bool IsInstalled(TaggerModelRequirement model);
}

internal sealed class NoInstalledTaggerModels : ITaggerModelCatalog
{
    internal static NoInstalledTaggerModels Instance { get; } = new();

    public bool IsInstalled(TaggerModelRequirement model) => false;
}

/// <summary>
/// Runs one tagger over media in batches: checks consent and models first, skips media already tagged by the same
/// tagger and model revision, applies the size and type limits, validates every request and result against the SDK
/// contract and stores each accepted batch as pending suggestions before the next batch starts. Cancellation keeps
/// finished batches; a later job resumes. Failures and invalid results count against the current batch. A deadline or lost grant ends the job.
/// </summary>
internal sealed class TaggerJobRunner(TaggerRegistry registry, TagSuggestionStore store, ITaggerModelCatalog models,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    internal async Task<TaggerJobResult> RunAsync(TaggerJob job, IProgress<TaggerJobProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        TaggerRegistration registration = job.Tagger;
        TaggerDescription tagger = registration.Tagger;
        int total = job.Items.Count;
        TaggerGrant? grant = registry.GrantFor(registration);
        if (grant is null) return new(TaggerJobStatus.ConsentRequired, total, 0, 0, 0, 0, 0, 0);
        if (tagger.Models.Any(model => !models.IsInstalled(model))) return new(TaggerJobStatus.ModelUnavailable, total, 0, 0, 0, 0, 0, 0);

        TagSuggestionSettings settings = store.Settings;
        TagSuggestionSnapshot snapshot = store.Load();
        if (!snapshot.CanWrite) return new(TaggerJobStatus.StoreFailed, total, 0, 0, 0, 0, 0, 0);
        List<TaggerJobItem> work = new();
        int skipped = 0, excluded = 0;
        foreach (TaggerJobItem item in job.Items)
        {
            if (!job.Rerun && AlreadyTagged(snapshot, registration, item.MediaSha256)) skipped++;
            else if (!Accepts(tagger, settings, item)) excluded++;
            else work.Add(item);
        }

        int completed = 0, failed = 0, tagged = 0, rejected = 0, suggestions = 0;
        int batchSize = Math.Min(settings.BatchSize, tagger.MaximumBatchItems);
        Report();
        for (int start = 0; start < work.Count; start += batchSize)
        {
            if (cancellationToken.IsCancellationRequested) return Result(TaggerJobStatus.Cancelled);
            if (grant.Token.IsCancellationRequested) return Result(TaggerJobStatus.ConsentRequired);
            TaggerJobItem[] batch = work.Skip(start).Take(batchSize).ToArray();
            long started = _clock.GetTimestamp();
            using CancellationTokenSource deadline = new(settings.BatchTimeout, _clock);
            using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, deadline.Token, grant.Token);
            try
            {
                BatchOutcome outcome = await RunBatchAsync(registration, grant, batch, job.TaggerSettings, settings,
                    value => Report(value), lifetime.Token).ConfigureAwait(false);
                EnsureCurrent();
                if (outcome.Runs is null)
                {
                    failed += batch.Length;
                    rejected += outcome.Invalid ? 1 : 0;
                }
                else
                {
                    store.CommitRuns(outcome.Runs, EnsureCurrent, lifetime.Token);
                    foreach (TagSuggestionRunInput run in outcome.Runs)
                    {
                        if (run.Outcome == TagSuggestionStore.TaggedOutcome) { tagged++; suggestions += run.Candidates.Count; }
                        else failed++;
                    }
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested || grant.Token.IsCancellationRequested
                || cancellationToken.IsCancellationRequested || Expired())
            {
                if (cancellationToken.IsCancellationRequested) return Result(TaggerJobStatus.Cancelled);
                if (grant.Token.IsCancellationRequested) return Result(TaggerJobStatus.ConsentRequired);
                failed += batch.Length;
                return Result(TaggerJobStatus.TimedOut);
            }
            catch (Exception error) when (error is TagSuggestionStoreException or IOException or UnauthorizedAccessException or OverflowException)
            {
                failed += batch.Length;
                return Result(TaggerJobStatus.StoreFailed);
            }
            completed += batch.Length;
            Report();

            bool Expired() => _clock.GetElapsedTime(started) >= settings.BatchTimeout;

            void EnsureCurrent()
            {
                cancellationToken.ThrowIfCancellationRequested();
                grant.Token.ThrowIfCancellationRequested();
                lifetime.Token.ThrowIfCancellationRequested();
                if (Expired()) throw new OperationCanceledException("The tagger batch deadline expired.");
            }
        }
        return Result(TaggerJobStatus.Completed);

        void Report(int inBatch = 0) => progress?.Report(new TaggerJobProgress(total, skipped + excluded + completed + inBatch, failed, skipped));

        TaggerJobResult Result(TaggerJobStatus status) => new(status, total, tagged, skipped, excluded, failed, rejected, suggestions);
    }

    private sealed record BatchOutcome(IReadOnlyList<TagSuggestionRunInput>? Runs, bool Invalid = false);

    private static async Task<BatchOutcome> RunBatchAsync(TaggerRegistration registration, TaggerGrant grant, TaggerJobItem[] batch,
        SettingsSnapshot taggerSettings, TagSuggestionSettings settings, Action<int> report,
        CancellationToken cancellationToken)
    {
        TaggerDescription tagger = registration.Tagger;
        TaggerMediaInput[] inputs = new TaggerMediaInput[batch.Length];
        Dictionary<string, TaggerJobItem> byToken = new(StringComparer.Ordinal);
        for (int index = 0; index < batch.Length; index++)
        {
            TaggerJobItem item = batch[index];
            string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            byToken.Add(token, item);
            inputs[index] = new TaggerMediaInput("item-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture), item.Kind,
                item.MimeType, item.Length, item.MediaSha256, token, item.PixelWidth, item.PixelHeight, item.DurationMilliseconds);
        }
        TaggerBatchRequest request = new(tagger.Key, Guid.NewGuid().ToString("N"), inputs, TagSuggestionSettings.ConfidenceFloor,
            settings.MaximumSuggestionsPerItem, taggerSettings, registration.Binding.Configuration.Revision);
        SdkValidation.ThrowIfInvalid(SdkValidation.ValidateTaggerRequest(request, tagger));

        using BatchServicesLifetime services = new(cancellationToken);
        BatchAssetReader reader = new(byToken, settings, grant, services.Token);
        IHostHttp http = new TaggerHttp(registration, grant, settings.BatchHttpRequests, services.Token);
        BatchResources resources = new(registration.Binding.Resources, grant, services.Token);
        PluginCallContext context = new(registration.Binding.Configuration, http, reader, resources, QuietLog.Instance);
        BatchProgress progress = new(batch.Length, report, grant, services.Token);
        Task<TaggerBatchResult> invocation = Task.Run(() =>
        {
            grant.Token.ThrowIfCancellationRequested();
            return registration.Invoke(request, context, progress, services.Token);
        }, cancellationToken);
        TaggerBatchResult result;
        try
        {
            result = await invocation.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ObserveLateFailure(invocation);
            throw;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            registration.Binding.Log.Write(PluginLogLevel.Warning, "A tagger batch failed.");
            return new BatchOutcome(null);
        }
        finally
        {
            services.Close();
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (SdkValidation.ValidateTaggerResult(request, result, tagger).Count != 0) return new BatchOutcome(null, Invalid: true);

        TagSuggestionProvenance provenance = new(registration.PluginId, tagger.Key, registration.PluginVersion,
            result.Provenance.ModelId, result.Provenance.ModelRevision, result.Provenance.ModelSha256?.ToLowerInvariant());
        Dictionary<string, TaggerJobItem> byItem = new(StringComparer.Ordinal);
        for (int index = 0; index < inputs.Length; index++) byItem.Add(inputs[index].ItemId, batch[index]);
        List<TagSuggestionRunInput> runs = new(result.Items.Count);
        foreach (TaggerItemResult item in result.Items)
        {
            TaggerJobItem media = byItem[item.ItemId];
            runs.Add(new TagSuggestionRunInput(media.MediaSha256, media.Length, provenance, item.Outcome.ToString(),
                item.Suggestions.Select(value => new TagSuggestionCandidate(value.Name, value.Category.ToString(), value.Confidence,
                    value.Range?.StartMilliseconds, value.Range?.EndMilliseconds)).ToArray()));
        }
        return new BatchOutcome(runs);
    }

    /// <summary>Same tagger and a declared model revision (or, without models, the same plugin version) already tagged these bytes.</summary>
    private static bool AlreadyTagged(TagSuggestionSnapshot snapshot, TaggerRegistration registration, string mediaSha256)
    {
        TaggerDescription tagger = registration.Tagger;
        if (tagger.Models.Count == 0)
            return snapshot.Runs.Any(run => run.Outcome == TagSuggestionStore.TaggedOutcome
                && string.Equals(run.MediaSha256, mediaSha256, StringComparison.Ordinal) && run.Provenance.PluginId == registration.PluginId
                && run.Provenance.TaggerKey == tagger.Key && run.Provenance.PluginVersion == registration.PluginVersion);
        return tagger.Models.Any(model => snapshot.HasCompletedRun(mediaSha256, new TagSuggestionProvenance(registration.PluginId,
            tagger.Key, registration.PluginVersion, model.ModelId, model.Revision)));
    }

    private static bool Accepts(TaggerDescription tagger, TagSuggestionSettings settings, TaggerJobItem item)
    {
        if (item.Kind is not (TaggerMediaKind.Image or TaggerMediaKind.Animation or TaggerMediaKind.Video or TaggerMediaKind.Audio)
            || !tagger.SupportedMedia.HasFlag(item.Kind)
            || !tagger.SupportedMimeTypes.Contains(item.MimeType, StringComparer.OrdinalIgnoreCase)) return false;
        long ceiling = Math.Min(Math.Min(settings.MaximumInputBytes, tagger.MaximumInputBytes), TaggerLimits.MaximumInputBytes);
        if (item.Kind is TaggerMediaKind.Image or TaggerMediaKind.Animation) ceiling = Math.Min(ceiling, SdkLimits.MaximumImageBytes);
        return item.Length > 0 && item.Length <= ceiling;
    }

    internal static void ObserveLateFailure(Task task) =>
        _ = task.ContinueWith(static completed => _ = completed.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private sealed class QuietLog : IPluginLog
    {
        internal static QuietLog Instance { get; } = new();

        public void Write(PluginLogLevel level, string message) { }
    }

    // Cancellation callbacks from the invoker cannot block the runner's own deadline.
    private sealed class BatchServicesLifetime : IDisposable
    {
        private readonly CancellationTokenSource _services = new();
        private readonly CancellationTokenRegistration _relay;
        private Task? _shutdown;

        internal BatchServicesLifetime(CancellationToken lifetime)
        {
            Token = _services.Token;
            _relay = lifetime.UnsafeRegister(static state => ((BatchServicesLifetime)state!).Close(), this);
        }

        internal CancellationToken Token { get; }

        internal void Close()
        {
            lock (_services) _shutdown ??= _services.CancelAsync();
        }

        public void Dispose()
        {
            _relay.Dispose();
            Close();
            ObserveLateFailure(DisposeSourceAsync());
        }

        private async Task DisposeSourceAsync()
        {
            using (_services)
                await _shutdown!.ConfigureAwait(false);
        }
    }

    private sealed class BatchResources(IPluginResources inner, TaggerGrant grant, CancellationToken lifetime) : IPluginResources
    {
        public string? ResolvePath(string logicalName)
        {
            if (!IsLive()) return null;
            string? path = inner.ResolvePath(logicalName);
            return IsLive() ? path : null;
        }

        private bool IsLive() => !lifetime.IsCancellationRequested && !grant.Token.IsCancellationRequested;
    }

    private sealed class BatchProgress(int count, Action<int> report, TaggerGrant grant, CancellationToken lifetime) : IProgress<TaggerBatchProgress>
    {
        private readonly object _gate = new();
        private int _last;

        public void Report(TaggerBatchProgress value)
        {
            if (value is null) return;
            lock (_gate)
            {
                int completed = Math.Clamp(value.CompletedItems, 0, count);
                if (lifetime.IsCancellationRequested || grant.Token.IsCancellationRequested || completed <= _last) return;
                _last = completed;
                report(completed);
            }
        }
    }

    /// <summary>Serves current batch tokens using host-verified readers and a shared admission budget.</summary>
    private sealed class BatchAssetReader(IReadOnlyDictionary<string, TaggerJobItem> items,
        TagSuggestionSettings settings, TaggerGrant grant, CancellationToken lifetime) : IImageAssetReader
    {
        private readonly object _gate = new();
        private int _reads, _active;
        private long _bytes;

        public async Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count, CancellationToken cancellationToken = default)
        {
            if (lifetime.IsCancellationRequested || grant.Token.IsCancellationRequested || assetToken is null || !items.TryGetValue(assetToken, out TaggerJobItem? item))
                throw new InvalidOperationException("The asset is not part of the current tagger batch.");
            if (offset < 0 || offset > item.Length || count <= 0 || count > SdkLimits.MaximumImageChunkBytes)
                throw new ArgumentOutOfRangeException(nameof(count), "The read is outside the asset or the chunk limit.");
            int length = (int)Math.Min(count, item.Length - offset);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, grant.Token, cancellationToken);
            Reserve(length, linked.Token);
            Task<byte[]> read = ReadReservedAsync(item, offset, length, linked.Token);
            byte[] bytes;
            try
            {
                bytes = await read.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                ObserveLateFailure(read);
                throw;
            }
            if (bytes.Length != length) throw new InvalidDataException("The media source returned a different number of bytes.");
            grant.Token.ThrowIfCancellationRequested();
            linked.Token.ThrowIfCancellationRequested();
            return new ImageAssetChunk(offset, bytes, offset + length >= item.Length);
        }

        private void Reserve(int length, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_reads >= settings.BatchReadCount || length > settings.BatchReadBytes - _bytes || _active >= settings.ConcurrentReads)
                    throw new InvalidOperationException("The tagger asset read budget is exhausted.");
                _reads++;
                _bytes += length;
                _active++;
            }
        }

        private async Task<byte[]> ReadReservedAsync(TaggerJobItem item, long offset, int length, CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return length == 0 ? [] : await item.Read(offset, length, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                lock (_gate) _active--;
            }
        }
    }
}
