using System.Security.Cryptography;
using MeowChan.Persistence;
using MeowChan.PluginHost.Taggers;
using MeowChan.PluginSdk;
using MeowChan.Tags;

namespace MeowChan.PluginRuntime.Tests;

public sealed class TaggerContractRunnerTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("meowchan-tagger-runner-");
    private readonly ProfileLease _lease;
    private readonly TagSuggestionStore _store;
    private readonly TaggerRegistry _registry = new();

    public TaggerContractRunnerTests()
    {
        _lease = ProfileLease.Acquire(_directory.FullName);
        _store = new TagSuggestionStore(_lease);
    }

    public void Dispose()
    {
        _lease.Dispose();
        _directory.Delete(recursive: true);
    }

    private sealed class Installed : ITaggerModelCatalog
    {
        public bool IsInstalled(TaggerModelRequirement model) => model.ModelId == ReferenceTagger.ModelId;
    }

    private sealed class NullLog : IPluginLog
    {
        internal List<string> Messages { get; } = new();

        public void Write(PluginLogLevel level, string message) => Messages.Add(message);
    }

    private sealed class NoResources : IPluginResources
    {
        public string? ResolvePath(string logicalName) => null;
    }

    private sealed class RecordingHttp : IHostHttp
    {
        internal List<Uri> Requests { get; } = new();

        public Task<HostHttpResult> SendAsync(HostHttpRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request.Url);
            return Task.FromResult<HostHttpResult>(new HostHttpResponse(200, request.Url, new Dictionary<string, string>(), []));
        }
    }

    private sealed class SyncProgress<T> : IProgress<T>
    {
        internal List<T> Values { get; } = new();

        public void Report(T value) => Values.Add(value);
    }

    private TaggerRegistration Register(ReferenceTagger tagger, IHostHttp? consentedHttp = null, NullLog? log = null, TaggerInvoker? invoke = null)
    {
        TaggerHostBinding binding = new(new PluginConfiguration("configuration-1", new SettingsSnapshot("1", []),
            new Dictionary<string, SettingsSnapshot>()), new NoResources(), log ?? new NullLog(), consentedHttp,
            ExecutionRevision: "verified-build-1");
        return Assert.Single(_registry.Register(tagger.Describe(), binding, invoke ?? tagger.TagAsync));
    }

    private TaggerJobRunner Runner(ITaggerModelCatalog? models = null) => new(_registry, _store, models ?? new Installed());

    private static TaggerJobItem Item(byte[] bytes, string mime = "image/png", TaggerMediaKind kind = TaggerMediaKind.Image, long? length = null)
        => new(Convert.ToHexStringLower(SHA256.HashData(bytes)), length ?? bytes.Length, kind, mime,
            (offset, count, _) => Task.FromResult(bytes.AsSpan((int)offset, count).ToArray()));

    private static byte[][] Media(int count, int size = 100_000)
        => Enumerable.Range(0, count).Select(index =>
        {
            byte[] bytes = new byte[size];
            new Random(index + 1).NextBytes(bytes);
            return bytes;
        }).ToArray();

    private static TaggerJob Job(TaggerRegistration registration, IEnumerable<TaggerJobItem> items, bool rerun = false)
        => new(registration, items.ToArray(), new SettingsSnapshot("1", []), rerun);

    [Fact]
    public async Task AJobStoresValidatedPendingSuggestionsWithProvenanceAndReportsProgress()
    {
        ReferenceTagger tagger = new();
        TaggerRegistration registration = Register(tagger);
        _store.Settings = TagSuggestionSettings.Default with { BatchSize = 2 };
        byte[][] media = Media(5);
        SyncProgress<TaggerJobProgress> progress = new();

        TaggerJobResult result = await Runner().RunAsync(Job(registration, media.Select(bytes => Item(bytes))), progress);

        Assert.Equal(new TaggerJobResult(TaggerJobStatus.Completed, 5, 5, 0, 0, 0, 0, 15), result);
        Assert.Equal([2, 2, 1], tagger.BatchSizes);
        Assert.Equal(5, progress.Values[^1].Completed);
        Assert.True(progress.Values.Zip(progress.Values.Skip(1)).All(pair => pair.First.Completed <= pair.Second.Completed));
        TagSuggestionSnapshot snapshot = _store.Load();
        Assert.All(snapshot.Suggestions, value => Assert.Equal(TagSuggestionState.Pending, value.State));
        Assert.All(snapshot.Suggestions, value => Assert.Equal(new TagSuggestionProvenance(ReferenceTagger.PluginId,
            ReferenceTagger.TaggerKey, "1.0.0", ReferenceTagger.ModelId, ReferenceTagger.ModelRevision), value.Provenance));
        string first = Convert.ToHexStringLower(SHA256.HashData(media[0]));
        // Raw scores down to the floor are stored; the 35 % threshold only filters the view.
        Assert.Equal(3, snapshot.Suggestions.Count(value => value.MediaSha256 == first));
        Assert.Equal(ReferenceTagger.Expected(media[0]).Where(value => value.Confidence >= 0.35).Select(value => value.Name).Order(),
            snapshot.PendingFor(first, _store.Settings.ConfidenceThreshold).Select(value => value.Name).Order());
    }

    [Fact]
    public async Task ASecondJobResumesAndAnExplicitRerunTagsAgain()
    {
        ReferenceTagger tagger = new();
        TaggerRegistration registration = Register(tagger);
        TaggerJobItem[] items = Media(3).Select(bytes => Item(bytes)).ToArray();
        await Runner().RunAsync(Job(registration, items));
        int batches = tagger.Batches;

        TaggerJobResult resumed = await Runner().RunAsync(Job(registration, items));
        Assert.Equal((TaggerJobStatus.Completed, 3, 0), (resumed.Status, resumed.Skipped, resumed.Tagged));
        Assert.Equal(batches, tagger.Batches);

        TaggerJobResult rerun = await Runner().RunAsync(Job(registration, items, rerun: true));
        Assert.Equal(3, rerun.Tagged);
        Assert.Equal(9, _store.Load().Suggestions.Count(value => value.State == TagSuggestionState.Pending));
    }

    [Fact]
    public async Task CancellationKeepsFinishedBatchesAndTheNextJobResumesTheRest()
    {
        using CancellationTokenSource cancel = new();
        ReferenceTagger tagger = new() { CancelAfterBatches = 1, CancelJob = cancel };
        TaggerRegistration registration = Register(tagger);
        _store.Settings = TagSuggestionSettings.Default with { BatchSize = 2 };
        TaggerJobItem[] items = Media(6).Select(bytes => Item(bytes)).ToArray();

        TaggerJobResult cancelled = await Runner().RunAsync(Job(registration, items), cancellationToken: cancel.Token);
        Assert.Equal((TaggerJobStatus.Cancelled, 2), (cancelled.Status, cancelled.Tagged));
        Assert.Equal(2, _store.Load().Runs.Count);

        tagger.CancelAfterBatches = -1;
        TaggerJobResult resumed = await Runner().RunAsync(Job(registration, items));
        Assert.Equal((TaggerJobStatus.Completed, 2, 4), (resumed.Status, resumed.Skipped, resumed.Tagged));
    }

    [Fact]
    public async Task FailedOrInvalidBatchesStoreNothingAndTheJobContinues()
    {
        NullLog log = new();
        ReferenceTagger tagger = new() { FailBatch = 0 };
        TaggerRegistration registration = Register(tagger, log: log);
        _store.Settings = TagSuggestionSettings.Default with { BatchSize = 1 };
        TaggerJobItem[] items = Media(3).Select(bytes => Item(bytes)).ToArray();
        tagger.BeforeBatch = batch =>
        {
            tagger.AnswerForeignItem = batch == 1;
            tagger.ReportedRevision = batch == 2 ? "r9" : ReferenceTagger.ModelRevision;
            return Task.CompletedTask;
        };

        TaggerJobResult result = await Runner().RunAsync(Job(registration, items));

        Assert.Equal((TaggerJobStatus.Completed, 0, 3, 2), (result.Status, result.Tagged, result.Failed, result.RejectedBatches));
        Assert.Empty(_store.Load().Suggestions);
        Assert.Empty(_store.Load().Runs);
        Assert.Equal(["A tagger batch failed."], log.Messages);
    }

    [Fact]
    public async Task UnsupportedAndOversizedMediaAreNeverSent()
    {
        ReferenceTagger tagger = new();
        TaggerRegistration registration = Register(tagger);
        _store.Settings = TagSuggestionSettings.Default with { MaximumInputMiB = 1 };
        byte[][] media = Media(4);
        TaggerJobItem[] items =
        [
            Item(media[0]),
            Item(media[1], mime: "image/webp"),
            Item(media[2], kind: TaggerMediaKind.Video),
            Item(media[3], length: 1024 * 1024 + 1)
        ];

        TaggerJobResult result = await Runner().RunAsync(Job(registration, items));

        Assert.Equal((1, 3), (result.Tagged, result.Excluded));
        Assert.Equal([1], tagger.BatchSizes);
    }

    [Fact]
    public async Task MissingModelsBlockTheJobWithoutCallingTheTagger()
    {
        ReferenceTagger tagger = new();
        TaggerRegistration registration = Register(tagger);
        TaggerJobResult result = await Runner(NoInstalledTaggerModels.Instance).RunAsync(Job(registration, Media(2).Select(bytes => Item(bytes))));
        Assert.Equal(TaggerJobStatus.ModelUnavailable, result.Status);
        Assert.Equal(0, tagger.Batches);
        Assert.False(File.Exists(_store.FilePath));
    }

    [Fact]
    public async Task AnOfflineTaggerHasNoNetwork()
    {
        RecordingHttp http = new();
        ReferenceTagger tagger = new() { NetworkProbe = new Uri("https://models.example/check") };
        TaggerRegistration registration = Register(tagger, consentedHttp: http);
        Assert.True(_registry.MayRun(registration));

        await Runner().RunAsync(Job(registration, Media(1).Select(bytes => Item(bytes))));

        HostHttpFailure failure = Assert.IsType<HostHttpFailure>(Assert.Single(tagger.NetworkResults));
        Assert.Equal("tagger_network_denied", failure.Failure.Code);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task NetworkNeedsConsentForTheExactVersionAndHostsAndReachesOnlyThoseHosts()
    {
        RecordingHttp http = new();
        HttpHostDeclaration host = new("models.example", HttpHostPurpose.Metadata);
        ReferenceTagger tagger = new() { NetworkHosts = [host], NetworkProbe = new Uri("https://models.example/check") };
        TaggerRegistration registration = Register(tagger, consentedHttp: http);
        TaggerJobItem[] items = Media(1).Select(bytes => Item(bytes)).ToArray();

        TaggerJobResult blocked = await Runner().RunAsync(Job(registration, items));
        Assert.Equal(TaggerJobStatus.ConsentRequired, blocked.Status);
        Assert.Equal(0, tagger.Batches);

        _registry.GrantNetworkConsent(registration);
        TaggerJobResult allowed = await Runner().RunAsync(Job(registration, items));
        Assert.Equal(TaggerJobStatus.Completed, allowed.Status);
        Assert.Equal([new Uri("https://models.example/check")], http.Requests);

        TaggerHttp scoped = new(registration, _registry.GrantFor(registration)!, 16, CancellationToken.None);
        HostHttpResult other = await scoped.SendAsync(new HostHttpRequest(ReferenceTagger.PluginId, HostHttpMethod.Get, new Uri("https://elsewhere.example/")));
        Assert.IsType<HostHttpFailure>(other);
        Assert.IsType<HostHttpFailure>(await scoped.SendAsync(new HostHttpRequest(ReferenceTagger.PluginId, HostHttpMethod.Get, new Uri("http://models.example/"))));
        Assert.IsType<HostHttpFailure>(await scoped.SendAsync(new HostHttpRequest(ReferenceTagger.PluginId, HostHttpMethod.Get, new Uri("https://models.example:444/"))));
        Assert.IsType<HostHttpFailure>(await scoped.SendAsync(new HostHttpRequest(ReferenceTagger.PluginId, HostHttpMethod.Get, new Uri("https://user@models.example/"))));
        Assert.Single(http.Requests);

        // The same declared version still needs consent after a fresh activation.
        _registry.Unregister(ReferenceTagger.PluginId);
        TaggerRegistration updated = Register(new ReferenceTagger { NetworkHosts = [host] }, consentedHttp: http);
        _registry.GrantNetworkConsent(registration);
        Assert.False(_registry.MayRun(updated));
        _registry.GrantNetworkConsent(updated);
        Assert.True(_registry.MayRun(updated));
        _registry.RevokeNetworkConsent(updated);
        Assert.False(_registry.MayRun(updated));
    }

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task RevocationCancelsInFlightHttpAndOldBatchCannotUseAReplacementGrant()
    {
        BlockingHttp transport = new();
        ReferenceTagger tagger = new() { NetworkHosts = [new("models.example", HttpHostPurpose.Metadata)] };
        TaskCompletionSource<IHostHttp> retained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        HostHttpRequest request = new(ReferenceTagger.PluginId, HostHttpMethod.Get, new("https://models.example/check"));
        TaggerRegistration registration = Register(tagger, transport, invoke: async (batch, context, progress, token) =>
        {
            // The service lifetime must hold even when this caller supplies no cancellation token.
            retained.SetResult(context.Http);
            await context.Http.SendAsync(request, CancellationToken.None);
            return await tagger.TagAsync(batch, context, progress, token);
        });

        _registry.GrantNetworkConsent(registration);
        Task<TaggerJobResult> running = Runner().RunAsync(Job(registration, [Item(Media(1)[0])]));
        IHostHttp oldService = await retained.Task.WaitAsync(Bound);
        CancellationToken sendToken = await transport.Started.Task.WaitAsync(Bound);
        _registry.RevokeNetworkConsent(registration);
        _registry.GrantNetworkConsent(registration);

        Assert.Equal(TaggerJobStatus.ConsentRequired, (await running.WaitAsync(Bound)).Status);
        await transport.Cancelled.Task.WaitAsync(Bound);
        Assert.True(sendToken.IsCancellationRequested);
        Assert.IsType<HostHttpFailure>(await oldService.SendAsync(request));
        transport.Release.TrySetResult(new HostHttpResponse(200, request.Url, new Dictionary<string, string>(), []));
        Assert.Equal(1, transport.Calls);
        Assert.Empty(_store.Load().Runs);
    }

    [Fact]
    public async Task HostDeadlineReturnsBeforeInvokerAndDiscardsALateValidResult()
    {
        ManualClock clock = new();
        ReferenceTagger tagger = new();
        TaskCompletionSource signalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaggerRegistration registration = Register(tagger, invoke: async (request, context, progress, token) =>
        {
            TaggerBatchResult valid = await tagger.TagAsync(request, context, progress, token);
            signalled.SetResult();
            await release.Task;
            finished.SetResult();
            return valid;
        });
        _store.Settings = TagSuggestionSettings.Default with { BatchTimeoutSeconds = 10, BatchSize = 1 };
        TaggerJobRunner runner = new(_registry, _store, new Installed(), clock);

        Task<TaggerJobResult> task = runner.RunAsync(Job(registration, Media(2).Select(bytes => Item(bytes))));
        await signalled.Task.WaitAsync(Bound);
        clock.Advance(_store.Settings.BatchTimeout);
        try
        {
            TaggerJobResult result = await task.WaitAsync(Bound);
            Assert.Equal(TaggerJobStatus.TimedOut, result.Status);
            Assert.Equal(1, result.Failed);
            Assert.Equal(1, tagger.Batches);
        }
        finally { release.TrySetResult(); }
        await finished.Task.WaitAsync(Bound);
        Assert.Empty(_store.Load().Runs);
    }

    [Fact]
    public async Task AReadStillWaitingAtBatchEndCannotDeliverItsBytes()
    {
        ReferenceTagger tagger = new();
        TaskCompletionSource<byte[]> backing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ImageAssetChunk>? read = null;
        TaggerJobItem item = Item(Media(1)[0]) with { Read = (_, _, _) => backing.Task };
        TaggerRegistration registration = Register(tagger, invoke: (request, context, _, _) =>
        {
            read = context.Images.ReadImageAsync(request.Items[0].AssetToken, 0, 1, CancellationToken.None);
            return Task.FromResult(new TaggerBatchResult(request.TaggerKey, request.BatchId,
                new TaggerModelProvenance(ReferenceTagger.ModelId, ReferenceTagger.ModelRevision),
                [new TaggerItemResult(request.Items[0].ItemId, TaggerItemOutcome.Tagged, [])]));
        });

        await Runner().RunAsync(Job(registration, [item]));
        Assert.NotNull(read);
        try { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(Bound)); }
        finally { backing.TrySetResult([7]); }
    }

    [Fact]
    public async Task ReadCountBytesAndConcurrencyAreBatchWide()
    {
        ReferenceTagger tagger = new();
        _store.Settings = TagSuggestionSettings.Default with { BatchReadMiB = 1, BatchReadCount = 17, ConcurrentReads = 1 };
        TaskCompletionSource<byte[]> first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        int chunkSize = SdkLimits.MaximumImageChunkBytes;
        TaggerJobItem item = Item(Media(1)[0], length: 2 * 1024 * 1024) with
        {
            Read = (_, length, _) => Interlocked.Increment(ref calls) == 1 ? first.Task : Task.FromResult(new byte[length])
        };
        int assertions = 0;
        TaggerRegistration registration = Register(tagger, invoke: async (request, context, _, token) =>
        {
            string asset = request.Items[0].AssetToken;
            Task<ImageAssetChunk> pending = context.Images.ReadImageAsync(asset, 0, chunkSize, token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => context.Images.ReadImageAsync(asset, 0, 1, token));
            first.SetResult(new byte[chunkSize]);
            await pending;
            for (int index = 1; index < 16; index++)
                await context.Images.ReadImageAsync(asset, 0, chunkSize, token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => context.Images.ReadImageAsync(asset, 0, 1, token));
            Assert.True((await context.Images.ReadImageAsync(asset, item.Length, 1, token)).IsEnd);
            await Assert.ThrowsAsync<InvalidOperationException>(() => context.Images.ReadImageAsync(asset, item.Length, 1, token));
            assertions++;
            return new TaggerBatchResult(request.TaggerKey, request.BatchId,
                new TaggerModelProvenance(ReferenceTagger.ModelId, ReferenceTagger.ModelRevision),
                [new TaggerItemResult(request.Items[0].ItemId, TaggerItemOutcome.Tagged, [])]);
        });

        await Runner().RunAsync(Job(registration, [item]));
        Assert.Equal(16, calls);
        Assert.Equal(1, assertions);
    }

    [Fact]
    public async Task StoreFailureIsAnExplicitResultAndNeverCountsAsTagged()
    {
        TaggerRegistration registration = Register(new ReferenceTagger());
        File.WriteAllText(_store.FilePath, "{");
        TaggerJobResult result = await Runner().RunAsync(Job(registration, [Item(Media(1)[0])]));
        Assert.Equal((TaggerJobStatus.StoreFailed, 0, 0), (result.Status, result.Tagged, result.Suggestions));
        Assert.Equal("{", File.ReadAllText(_store.FilePath));
    }

    private sealed class BlockingHttp : IHostHttp
    {
        internal TaskCompletionSource<CancellationToken> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<HostHttpResult> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls;

        public async Task<HostHttpResult> SendAsync(HostHttpRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            Started.SetResult(cancellationToken);
            using CancellationTokenRegistration subscription = cancellationToken.Register(() => Cancelled.TrySetResult());
            return await Release.Task;
        }
    }

    [Fact]
    public void RegistrationValidatesTheDescriptionAndRejectsDuplicatesAndTaggerlessPlugins()
    {
        ReferenceTagger tagger = new();
        TaggerRegistration registration = Register(tagger);
        Assert.Equal(registration.Tagger.Key, _registry.Find(ReferenceTagger.PluginId, ReferenceTagger.TaggerKey)!.Tagger.Key);
        TaggerHostBinding binding = registration.Binding;
        Assert.Equal(TaggerRegistrationError.DuplicatePlugin,
            Assert.Throws<TaggerRegistrationException>(() => _registry.Register(tagger.Describe(), binding, tagger.TagAsync)).Error);

        PluginDescription invalid = tagger.Describe() with { PluginId = "other", Taggers = [tagger.Tagger with { ContractVersion = "9.9.9" }] };
        TaggerRegistrationException rejected = Assert.Throws<TaggerRegistrationException>(() => _registry.Register(invalid, binding, tagger.TagAsync));
        Assert.Equal(TaggerRegistrationError.InvalidDescription, rejected.Error);
        Assert.Contains(rejected.Issues, issue => issue.Code == "incompatible_tagger_contract");

        PluginDescription blocked = tagger.Describe() with { PluginId = "blocked", Taggers = null, DeclarationStatus = PluginDeclarationStatus.Blocked };
        Assert.Equal(TaggerRegistrationError.NoTaggers,
            Assert.Throws<TaggerRegistrationException>(() => _registry.Register(blocked, binding, tagger.TagAsync)).Error);
        Assert.Single(_registry.Registrations);
        Assert.True(_registry.Unregister(ReferenceTagger.PluginId));
        Assert.Empty(_registry.Registrations);
    }

    [Fact]
    public async Task TheAssetReaderServesOnlyTheCurrentBatchInBoundedChunks()
    {
        IImageAssetReader? captured = null;
        string? token = null;
        ReferenceTagger tagger = new();
        TaggerHostBinding binding = new(new PluginConfiguration("configuration-1", new SettingsSnapshot("1", []),
            new Dictionary<string, SettingsSnapshot>()), new NoResources(), new NullLog());
        TaggerRegistration registration = Assert.Single(_registry.Register(tagger.Describe(), binding,
            async (request, context, progress, cancellationToken) =>
            {
                captured = context.Images;
                token = request.Items[0].AssetToken;
                await Assert.ThrowsAsync<InvalidOperationException>(() => context.Images.ReadImageAsync("foreign", 0, 1, cancellationToken));
                await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => context.Images.ReadImageAsync(token, 0, SdkLimits.MaximumImageChunkBytes + 1, cancellationToken));
                return await tagger.TagAsync(request, context, progress, cancellationToken);
            }));

        TaggerJobResult result = await Runner().RunAsync(Job(registration, Media(1).Select(bytes => Item(bytes))));

        Assert.Equal(1, result.Tagged);
        await Assert.ThrowsAsync<InvalidOperationException>(() => captured!.ReadImageAsync(token!, 0, 1));
    }
}
