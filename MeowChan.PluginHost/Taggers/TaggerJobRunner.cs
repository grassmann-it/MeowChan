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

internal enum TaggerJobStatus { Completed, Cancelled, ConsentRequired, ModelUnavailable }

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
/// finished batches; a later job resumes. A failed, timed-out or invalid batch stores nothing and the job goes on.
/// </summary>
internal sealed class TaggerJobRunner(TaggerRegistry registry, TagSuggestionStore store, ITaggerModelCatalog models)
{
    internal async Task<TaggerJobResult> RunAsync(TaggerJob job, IProgress<TaggerJobProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        TaggerRegistration registration = job.Tagger;
        TaggerDescription tagger = registration.Tagger;
        int total = job.Items.Count;
        if (!registry.MayRun(registration)) return new(TaggerJobStatus.ConsentRequired, total, 0, 0, 0, 0, 0, 0);
        if (tagger.Models.Any(model => !models.IsInstalled(model))) return new(TaggerJobStatus.ModelUnavailable, total, 0, 0, 0, 0, 0, 0);

        TagSuggestionSettings settings = store.Settings;
        TagSuggestionSnapshot snapshot = store.Load();
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
            TaggerJobItem[] batch = work.Skip(start).Take(batchSize).ToArray();
            BatchOutcome outcome = await RunBatchAsync(registration, batch, job.TaggerSettings, settings,
                value => Report(value), cancellationToken).ConfigureAwait(false);
            if (outcome.Cancelled) return Result(TaggerJobStatus.Cancelled);
            if (outcome.Runs is null) { failed += batch.Length; rejected += outcome.Invalid ? 1 : 0; }
            else
            {
                store.CommitRuns(outcome.Runs, cancellationToken);
                foreach (TagSuggestionRunInput run in outcome.Runs)
                {
                    if (run.Outcome == TagSuggestionStore.TaggedOutcome) { tagged++; suggestions += run.Candidates.Count; }
                    else failed++;
                }
            }
            completed += batch.Length;
            Report();
        }
        return Result(TaggerJobStatus.Completed);

        void Report(int inBatch = 0) => progress?.Report(new TaggerJobProgress(total, skipped + excluded + completed + inBatch, failed, skipped));

        TaggerJobResult Result(TaggerJobStatus status) => new(status, total, tagged, skipped, excluded, failed, rejected, suggestions);
    }

    private sealed record BatchOutcome(IReadOnlyList<TagSuggestionRunInput>? Runs, bool Cancelled = false, bool Invalid = false);

    private static async Task<BatchOutcome> RunBatchAsync(TaggerRegistration registration, TaggerJobItem[] batch,
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

        using BatchAssetReader reader = new(byToken);
        IHostHttp http = registration.NeedsNetwork ? new TaggerHttp(tagger.NetworkHosts, registration.Binding.ConsentedHttp) : TaggerHttp.Offline;
        PluginCallContext context = new(registration.Binding.Configuration, http, reader, registration.Binding.Resources, registration.Binding.Log);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.BatchTimeout);
        TaggerBatchResult result;
        try
        {
            result = await registration.Invoke(request, context, new BatchProgress(batch.Length, report), timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new BatchOutcome(null, Cancelled: true);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Timeout, worker or plugin failure: a technical omission, never an empty result.
            registration.Binding.Log.Write(PluginLogLevel.Warning, "A tagger batch failed: " + error.GetType().Name + ".");
            return new BatchOutcome(null);
        }
        finally
        {
            reader.Close();
        }
        if (cancellationToken.IsCancellationRequested) return new BatchOutcome(null, Cancelled: true);
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

    /// <summary>Forwards in-batch progress synchronously and bounded; a tagger cannot report more items than the batch holds.</summary>
    private sealed class BatchProgress(int count, Action<int> report) : IProgress<TaggerBatchProgress>
    {
        public void Report(TaggerBatchProgress value)
        {
            if (value is null) return;
            report(Math.Clamp(value.CompletedItems, 0, count));
        }
    }

    /// <summary>Serves only this batch's tokens, in SDK-bounded chunks, and only until the batch ends.</summary>
    private sealed class BatchAssetReader(IReadOnlyDictionary<string, TaggerJobItem> items) : IImageAssetReader, IDisposable
    {
        private volatile bool _closed;

        internal void Close() => _closed = true;

        public void Dispose() => Close();

        public async Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count, CancellationToken cancellationToken = default)
        {
            if (_closed || assetToken is null || !items.TryGetValue(assetToken, out TaggerJobItem? item))
                throw new InvalidOperationException("The asset is not part of the current tagger batch.");
            // Image reads keep the SDK image budget; larger media (video) use the same chunk bound with the tagger ceiling.
            if (offset < 0 || offset > item.Length || count <= 0 || count > SdkLimits.MaximumImageChunkBytes)
                throw new ArgumentOutOfRangeException(nameof(count), "The read is outside the asset or the chunk limit.");
            int length = (int)Math.Min(count, item.Length - offset);
            byte[] bytes = length == 0 ? Array.Empty<byte>() : await item.Read(offset, length, cancellationToken).ConfigureAwait(false);
            if (bytes.Length != length) throw new InvalidDataException("The media source returned a different number of bytes.");
            return new ImageAssetChunk(offset, bytes, offset + length >= item.Length);
        }
    }
}
