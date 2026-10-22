using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowChan;
using MeowChan.Archive;
using MeowChan.Downloader;
using MeowChan.ExamplePlugins;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginFilters.Fixture;
using System.Diagnostics;
using MeowChan.PluginHost.Filters;
using MeowChan.PluginHost.Runtime;
using MeowChan.PluginSdk;
using MeowChan.Rules;
using MeowChan.Storage;
using MeowChan.Tests;

namespace MeowChan.PluginFilters.Tests;

public sealed class OriginalFilterTests
{
    [Fact]
    public async Task RealWorkerMatchCommitsOneTransferSameBytesAndRestoresOffline()
    {
        await using Harness h = await Harness.StartAsync(Png(8, 4));
        DownloadItem item = await h.ItemAsync(new() { ["minimumWidth"] = "8", ["orientation"] = "landscape" });
        await h.RunAsync(item);
        Assert.Equal(1, h.Transfers);
        Assert.True(h.ImageReads > 0);
        Assert.Equal(h.Bytes, await File.ReadAllBytesAsync(item.DestinationPath));
        Assert.True(item.Thread.IsMediaDownloaded(item.MediaIdentifier));
        Assert.Empty(Directory.GetFiles(Path.Combine(h.Root.Path, "filter-stages")));
        MediaCommitResult? restored = await new MediaStore().TryRestoreAsync(item, CancellationToken.None);
        Assert.NotNull(restored);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(h.Bytes)), restored.Sha256, ignoreCase: true);
        await h.RunAsync(item);
        Assert.Equal(1, h.Transfers);
        await using ArchiveReader offline = await h.Content.OpenReaderAsync();
        Assert.Equal(1, (await offline.ReadThreadAsync(h.Resource))!.PostCount);
        Assert.Equal(MediaSelectionOutcome.Selected, (await h.Content.Catalog.GetDecisionsAsync(h.Resource)).Single().Selection.Outcome);
    }

    [Theory]
    [InlineData("minimumWidth", "9", "below_minimum_dimensions")]
    [InlineData("orientation", "portrait", "orientation_mismatch")]
    [InlineData("aspectRatio", "1", "aspect_ratio_mismatch")]
    public async Task RealWorkerNoMatchRetainsCaptureAndDurableDecisionWithoutFile(string key, string value, string reason)
    {
        await using Harness h = await Harness.StartAsync(Png(8, 4));
        DownloadItem item = await h.ItemAsync(new() { [key] = value });
        MediaFilterOmissionException error = await Assert.ThrowsAsync<MediaFilterOmissionException>(() => h.RunAsync(item));
        Assert.True(error.IsNoMatch); Assert.Equal(reason, error.ReasonCode);
        Assert.False(File.Exists(item.DestinationPath)); Assert.Equal(0, item.Thread.DownloadedMediaCount);
        await using ArchiveReader reader = await h.Content.OpenReaderAsync();
        Assert.Equal(1, (await reader.ReadThreadAsync(h.Resource))!.PostCount);
        CandidateDecision saved = (await h.Content.Catalog.GetDecisionsAsync(h.Resource)).Single();
        Assert.Equal(MediaSelectionOutcome.Excluded, saved.Selection.Outcome);
        Assert.Equal(reason, saved.Selection.FilterReason);
        Assert.Empty(Directory.GetFiles(Path.Combine(h.Root.Path, "filter-stages")));
    }

    [Fact]
    public async Task RevocationDuringActualChunkReadInvalidatesResultAndToken()
    {
        await using Harness h = await Harness.StartAsync(Png(8, 4));
        DownloadItem item = await h.ItemAsync(new());
        h.OnRead = () => h.Current = false;
        MediaFilterOmissionException error = await Assert.ThrowsAsync<MediaFilterOmissionException>(() => h.RunAsync(item));
        Assert.False(error.IsNoMatch);
        Assert.False(File.Exists(item.DestinationPath));
        Assert.True(h.ImageReads > 0);
        Assert.Equal(MediaSelectionOutcome.TechnicalOmission, (await h.Content.Catalog.GetDecisionsAsync(h.Resource)).Single().Selection.Outcome);
        Assert.Empty(Directory.GetFiles(Path.Combine(h.Root.Path, "filter-stages")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.Stage.ReadImageAsync(ImageDimensionsFilter.PluginId, h.LastToken!, 0, 1, h.Operation!, CancellationToken.None));
    }

    [Fact]
    public async Task MetadataDenialAndKnownByteLimitNeverRequestAnOriginal()
    {
        await using Harness h = await Harness.StartAsync(Png(8, 4), new FilterStageOptions { MaximumImageBytes = 16 });
        DownloadItem item = await h.ItemAsync(new());
        MediaFilterOmissionException error = await Assert.ThrowsAsync<MediaFilterOmissionException>(() => h.RunAsync(item));
        Assert.Equal("filter_image_byte_limit", error.ReasonCode); Assert.Equal(0, h.Transfers);
        MediaSelectionResult textOnly = new MediaRuleResolver(new("text-only", new[] {
            new MediaRule(MediaRuleScope.Global, types: new MediaTypeSelection(MediaTypeSelectionMode.Selected, Array.Empty<string>())) {
                ImageFilter = new(MediaImageFilterMode.Plugin, ImageDimensionsFilter.PluginId, ImageDimensionsFilter.FilterKey) }
        })).Evaluate(h.Resource, ".png", h.Bytes.Length);
        Assert.Equal(MediaSelectionOutcome.Excluded, textOnly.Outcome);
    }

    [Fact]
    public async Task PixelLimitAndMalformedHeadersAreTechnicalOmissionsBeforeWorker()
    {
        await using Harness h = await Harness.StartAsync(Png(8, 4), new FilterStageOptions { MaximumDecodedPixels = 16 });
        DownloadItem item = await h.ItemAsync(new());
        MediaFilterOmissionException error = await Assert.ThrowsAsync<MediaFilterOmissionException>(() => h.RunAsync(item));
        Assert.Equal("filter_image_pixel_limit", error.ReasonCode); Assert.Equal(0, h.ImageReads);
        Assert.False(File.Exists(item.DestinationPath));
        await using Harness broken = await Harness.StartAsync(new byte[] {1,2,3,4,5});
        MediaFilterOmissionException badHeader = await Assert.ThrowsAsync<MediaFilterOmissionException>(async () => await broken.RunAsync(await broken.ItemAsync(new())));
        Assert.Equal("filter_image_header", badHeader.ReasonCode); Assert.False(badHeader.IsNoMatch);
    }

    [Fact]
    public async Task StoreRechecksBindingAfterCopyBeforePublishingAndLeavesExistingAsset()
    {
        await using Harness h = await Harness.StartAsync(Png(8, 4));
        DownloadItem item = await h.ItemAsync(new());
        byte[] foreign = Encoding.UTF8.GetBytes("existing foreign media");
        Directory.CreateDirectory(Path.GetDirectoryName(item.DestinationPath)!);
        await File.WriteAllBytesAsync(item.DestinationPath, foreign);
        h.OnResult = () => h.Current = false;
        await Assert.ThrowsAsync<MediaFilterOmissionException>(() => h.RunAsync(item));
        Assert.Equal(foreign, await File.ReadAllBytesAsync(item.DestinationPath));
        Assert.False(item.Thread.IsMediaDownloaded(item.MediaIdentifier));
    }

    [Fact]
    public async Task RestartCleansOnlyJournalOwnedUncommittedStages()
    {
        using TempDirectory root = new(); using ProfileLease profile = ProfileLease.Acquire(root.Path);
        string stages = Path.Combine(root.Path, "filter-stages"); Directory.CreateDirectory(stages);
        string id = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(Path.Combine(stages, "stage-" + id + ".json"), JsonSerializer.Serialize(new {
            Owner = "MeowChan original filter stage v1", Id = id, Resource = "x", MediaId = "x", PluginId = "x", ExecutionRevision = "1", ConfigurationRevision = "1", GrantRevision = "1", Generation = 1, RuleRevision = "1", Sha256 = "ignored", Size = 4
        }));
        await File.WriteAllTextAsync(Path.Combine(stages, "stage-" + id + ".bytes"), "part");
        string foreign = Path.Combine(stages, "foreign.bytes"); await File.WriteAllTextAsync(foreign, "keep");
        string archived = Path.Combine(root.Path, "saved.png"); await File.WriteAllBytesAsync(archived, Png(2, 2));
        await using FilterStageCoordinator coordinator = new(root.Path, profile);
        Assert.Equal(1, coordinator.Snapshot.RecoveredStages); Assert.True(File.Exists(foreign)); Assert.True(File.Exists(archived));
        Assert.False(File.Exists(Path.Combine(stages, "stage-" + id + ".bytes")));
    }

    [Fact]
    public async Task RuleSettingsRoundTripPreservesPerRuleParametersAndScope()
    {
        using TempDirectory root = new(); using ProfileLease lease = ProfileLease.Acquire(root.Path);
        MediaRuleStore store = new(root.Path, lease); await store.LoadAsync();
        await store.SaveAsync(new[] { new MediaRule(MediaRuleScope.ForBoard("stub", "g")) {
            ImageFilter = new(MediaImageFilterMode.Plugin, ImageDimensionsFilter.PluginId, "dimensions", parameters: new Dictionary<string,string> { ["minimumWidth"] = "700" }) } });
        MediaRuleStore restored = new(root.Path, lease); await restored.LoadAsync();
        EffectiveMediaRule effective = restored.Resolve(new ResourceId("stub", "g", "1"));
        Assert.Equal("700", effective.ImageFilter.Parameters["minimumWidth"]);
        Assert.Equal(MediaRuleScope.ForBoard("stub", "g"), effective.ImageFilterOrigin!.Scope);
    }

    [Fact]
    public async Task BundledResourceHelperConsumesTheSameBytesThroughRealWorker()
    {
        await using Harness h = await Harness.StartAsync(Png(8, 4), resourceFilter: true);
        DownloadItem item = await h.ItemAsync(new());
        await h.RunAsync(item);
        Assert.Equal(1, h.Transfers); Assert.Equal(h.Bytes, await File.ReadAllBytesAsync(item.DestinationPath));
        Assert.Equal(0, h.Manager.GetTemporaryBytes());
    }

    [Fact]
    public async Task ResourceHelperTimeoutKillsOwnDescendantsAndCleansBothCopies()
    {
        await using Harness h = await Harness.StartAsync(Png(8, 4), resourceFilter: true,
            workerOptions: new PluginWorkerOptions { OperationTimeout = TimeSpan.FromSeconds(2), CancellationGrace = TimeSpan.FromMilliseconds(250) });
        DownloadItem item = await h.ItemAsync(new() { ["mode"] = "hang" });
        Task run = h.RunAsync(item);
        string workers = Path.Combine(h.Root.Path, "workers"); string? pidFile = null;
        await TestWait.UntilAsync(() => (pidFile = Directory.GetFiles(workers, "helper-pids", SearchOption.AllDirectories).FirstOrDefault()) != null);
        int[] pids = (await File.ReadAllTextAsync(pidFile!)).Split(',').Select(int.Parse).ToArray();
        MediaFilterOmissionException error = await Assert.ThrowsAsync<MediaFilterOmissionException>(() => run);
        Assert.False(error.IsNoMatch); Assert.False(File.Exists(item.DestinationPath));
        await TestWait.UntilAsync(() => pids.All(pid => !Alive(pid)));
        Assert.Empty(Directory.GetFiles(Path.Combine(h.Root.Path, "filter-stages")));
        Assert.Empty(Directory.GetFiles(Path.Combine(h.Root.Path, "workers"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task WorkerCrashLeavesTechnicalDecisionAndNoPublishedBytes()
    {
        await using Harness h = await Harness.StartAsync(Png(8, 4), resourceFilter: true);
        DownloadItem item = await h.ItemAsync(new() { ["mode"] = "crash" });
        MediaFilterOmissionException error = await Assert.ThrowsAsync<MediaFilterOmissionException>(() => h.RunAsync(item));
        Assert.False(error.IsNoMatch); Assert.False(File.Exists(item.DestinationPath));
        Assert.Equal(MediaSelectionOutcome.TechnicalOmission, (await h.Content.Catalog.GetDecisionsAsync(h.Resource)).Single().Selection.Outcome);
        Assert.Empty(Directory.GetFiles(Path.Combine(h.Root.Path, "filter-stages")));
    }

    [Fact]
    public async Task WorkerCopiesCountAgainstTheSharedTemporaryBudget()
    {
        await using Harness h = await Harness.StartAsync(Png(8, 4), new FilterStageOptions { MaximumImageBytes = 1024, MaximumTemporaryBytes = 16384 },
            resourceFilter: true, workerOptions: new PluginWorkerOptions { MaximumTemporaryBytes = 16384, OperationTimeout = TimeSpan.FromSeconds(5) });
        DownloadItem item = await h.ItemAsync(new() { ["mode"] = "temp" });
        MediaFilterOmissionException error = await Assert.ThrowsAsync<MediaFilterOmissionException>(() => h.RunAsync(item));
        Assert.False(error.IsNoMatch); Assert.Equal("temporary-limit", error.ReasonCode);
        Assert.False(File.Exists(item.DestinationPath)); Assert.Equal(0, h.Manager.GetTemporaryBytes());
        Assert.Empty(Directory.GetFiles(Path.Combine(h.Root.Path, "filter-stages")));
    }

    [Fact]
    public async Task TwoCheckSlotsHaveCancellableAdmissionAndDoNotStopNormalOriginals()
    {
        await using Harness h = await Harness.StartAsync(Png(8, 4));
        DownloadItem item = await h.ItemAsync(new());
        await using IOriginalMediaFilterLease first = await item.Filter!.AcquireAsync(item, CancellationToken.None);
        await using IOriginalMediaFilterLease second = await item.Filter.AcquireAsync(item, CancellationToken.None);
        using CancellationTokenSource cancelled = new();
        Task<IOriginalMediaFilterLease> waiting = item.Filter.AcquireAsync(item, cancelled.Token);
        Assert.Equal(2, h.Stage.Snapshot.ActiveChecks); Assert.Equal(1, h.Stage.Snapshot.WaitingChecks);
        DownloadItem ordinary = new(item.DownloadUri, item.DestinationPath + ".normal", item.ImageBoard, item.Thread, "normal", item.ExpectedSize, item.ExpectedMd5);
        await h.RunAsync(ordinary); Assert.True(File.Exists(ordinary.DestinationPath));
        cancelled.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(0, h.Stage.Snapshot.WaitingChecks);
    }

    [Theory]
    [InlineData("rule")]
    [InlineData("watch")]
    public async Task RevisionChangeAfterRealResultNeverPublishes(string changed)
    {
        await using Harness h = await Harness.StartAsync(Png(8, 4));
        DownloadItem item = await h.ItemAsync(new());
        if (changed == "rule") h.BeforeResult = async () => { await h.Content.Rules.SaveAsync(Array.Empty<MediaRule>()); };
        else h.OnResult = () => h.Current = false;
        await Assert.ThrowsAsync<MediaFilterOmissionException>(() => h.RunAsync(item));
        Assert.False(File.Exists(item.DestinationPath));
    }

    [Fact]
    public async Task TokenRejectsWrongPluginOperationOversizedChunksAndEscapedOffsets()
    {
        await using Harness h = await Harness.StartAsync(Png(8, 4));
        DownloadItem item = await h.ItemAsync(new());
        h.CheckReadAuthorization = true;
        await h.RunAsync(item);
        Assert.True(h.ImageReads > 0);
    }

    [Theory]
    [InlineData("crash-before-commit", 91)]
    [InlineData("crash-store-copy", 92)]
    public async Task HostProcessCrashAfterWorkerResultRecoversRealStageUnderNewProfileLease(string mode, int exitCode)
    {
        using TempDirectory probe = new(); string marker = Path.Combine(probe.Path, "state.json");
        string executable = Path.Combine(AppContext.BaseDirectory, "MeowChan.PluginFilters.Tests" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        using Process child = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false, ArgumentList = { mode, marker } })!;
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(exitCode, child.ExitCode);
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(marker));
        string root = document.RootElement.GetProperty("Root").GetString()!, destination = document.RootElement.GetProperty("Destination").GetString()!;
        int worker = document.RootElement.GetProperty("Worker").GetInt32();
        try
        {
            await TestWait.UntilAsync(() => !Alive(worker));
            Assert.False(File.Exists(destination));
            Assert.Single(Directory.GetFiles(Path.Combine(root, "filter-stages"), "*.bytes"));
            if (mode == "crash-store-copy") Assert.Single(Directory.GetFiles(Path.GetDirectoryName(destination)!, ".meowchan-media-*.part"));
            using ProfileLease lease = ProfileLease.Acquire(root);
            await using FilterStageCoordinator restarted = new(root, lease);
            Assert.Equal(1, restarted.Snapshot.RecoveredStages);
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "filter-stages"), "*.bytes"));
            if (Directory.Exists(Path.GetDirectoryName(destination)!)) Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(destination)!, ".meowchan-media-*.part"));
            await using ArchiveReader reader = await ArchiveReader.OpenAsync(root);
            Assert.Equal(1, (await reader.ReadThreadAsync(new("stub", "g", "123")))!.PostCount);
        }
        finally { Directory.Delete(root, true); }
    }

    internal static async Task CrashProbeAsync(string mode, string marker)
    {
        Harness h = await Harness.StartAsync(Png(8, 4));
        DownloadItem item = await h.ItemAsync(new());
        await File.WriteAllTextAsync(marker, JsonSerializer.Serialize(new { Root = h.Root.Path, Destination = item.DestinationPath, Worker = h.Worker.Snapshot.ProcessId }));
        if (mode == "crash-before-commit") h.OnResult = () => Environment.Exit(91);
        await h.RunAsync(item, mode == "crash-store-copy" ? new MediaStore((_, _, _) => Environment.Exit(92)) : null);
        throw new InvalidOperationException("The crash fixture did not reach the worker result boundary.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOmissionPersistenceAlwaysReleasesAdmissionSlot(bool cancelled)
    {
        await using Harness h = await Harness.StartAsync(Png(8, 4), new FilterStageOptions { MaximumImageBytes = 16 });
        DownloadItem item = await h.ItemAsync(new());
        CandidateDecision decision = (await h.Content.Catalog.GetDecisionsAsync(h.Resource)).Single();
        item.Filter = ((IOriginalMediaFilterFactory)h.Stage).Create(item, decision, () => true,
            (_, _, _) => Task.FromException(cancelled ? new OperationCanceledException("fixture cancelled write") : new IOException("fixture failed decision write")));
        if (cancelled) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => item.Filter.AcquireAsync(item, CancellationToken.None));
        else await Assert.ThrowsAsync<IOException>(() => item.Filter.AcquireAsync(item, CancellationToken.None));
        Assert.Equal(0, h.Stage.Snapshot.ActiveChecks); Assert.True(item.Filter.CanStart);
    }

    [Fact]
    public async Task RecoveryDoesNotOpenUnixFifosAndRecoversAtomicPendingJournal()
    {
        if (OperatingSystem.IsWindows()) return;
        using TempDirectory root = new(); using ProfileLease profile = ProfileLease.Acquire(root.Path);
        string stages = Path.Combine(root.Path, "filter-stages"); Directory.CreateDirectory(stages);
        string fifo = Path.Combine(stages, "stage-" + Guid.NewGuid().ToString("N") + ".json");
        Assert.Equal(0, MkFifo(fifo, 384));
        string id = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(Path.Combine(stages, "stage-" + id + ".json.next"), JsonSerializer.Serialize(new { Owner = "MeowChan original filter stage v1", Id = id }));
        FilterStageCoordinator stage = await Task.Run(() => new FilterStageCoordinator(root.Path, profile)).WaitAsync(TimeSpan.FromSeconds(2));
        await using (stage) { Assert.Equal(1, stage.Snapshot.RecoveredStages); Assert.True(File.Exists(fifo)); }
    }

    [Fact]
    public async Task VariantManifestsKeepRawIdsAndRestoreCollidingPairsIndependently()
    {
        using TempDirectory root = new(); using StubImageBoard board = new(); ProbeThread thread = new(board.Board, new("https://stub.test/g/thread/123"));
        string firstPath = StorageLayout.GetMediaPath(root.Path, "stub", "g", "123", "x--preview", ".png", "original");
        string secondPath = StorageLayout.GetMediaPath(root.Path, "stub", "g", "123", "x", ".png", "preview");
        Assert.NotEqual(firstPath, secondPath); Assert.Equal(StorageLayout.GetMediaPath(root.Path, "stub", "g", "123", "x--preview", ".png"), firstPath);
        DownloadItem first = new(new("https://stub.test/a"), firstPath, board, thread, "x--preview", null, null, null, null, "original");
        DownloadItem second = new(new("https://stub.test/b"), secondPath, board, thread, "x", null, null, null, null, "preview");
        MediaStore store = new(); await store.CommitAsync(first, new MemoryStream(new byte[] {1,2}), CancellationToken.None);
        await store.CommitAsync(second, new MemoryStream(new byte[] {3,4}), CancellationToken.None);
        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(Path.GetDirectoryName(firstPath)!);
        Assert.Contains(snapshot.GetPage(0, 10), asset => asset.MediaIdentifier == "x--preview" && asset.Variant == "original");
        Assert.Contains(snapshot.GetPage(0, 10), asset => asset.MediaIdentifier == "x" && asset.Variant == "preview");
        Assert.NotNull(await new MediaStore().TryRestoreAsync(first, CancellationToken.None));
        Assert.NotNull(await new MediaStore().TryRestoreAsync(second, CancellationToken.None));
        Assert.Equal(new byte[] {1,2}, await File.ReadAllBytesAsync(firstPath)); Assert.Equal(new byte[] {3,4}, await File.ReadAllBytesAsync(secondPath));
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MkFifo(string path, uint mode);

    [Fact]
    public async Task ContentQueueNoMatchIsNotDownloadedAndNeverAutomaticallyRetriedAfterRestore()
    {
        await using Harness h = await Harness.StartAsync(Png(8, 4));
        DownloadItem item = await h.ItemAsync(new() { ["orientation"] = "portrait" });
        await h.Content.BindAsync(h.Thread, h.Root.Path, restored: false);
        WorkStateStore work = new(h.Root.Path, h.Profile);
        WorkResource resource = work.CommitIntent(new() { Url = h.Thread.Url, OutputRoot = h.Root.Path, Generation = 1, Intent = WorkIntent.Watching, IsThread = true });
        DownloadQueue queue = new(2, long.MaxValue); queue.ConfigureWorkState(work); queue.BindWork(h.Thread, resource);
        try
        {
            await h.Thread.CaptureAndEnqueueAsync(queue, item);
            await queue.WaitForIdleAsync();
            Assert.Equal(MediaWorkStatus.Excluded, work.LoadMedia(resource.Url, 1).Single().Status);
            Assert.Equal(0, h.Thread.DownloadedMediaCount); Assert.Equal(ThreadActivity.Finished, h.Thread.Activity);
            Assert.Equal(0, queue.RequestMediaRetry(h.Thread));
            await h.Thread.CaptureAndEnqueueAsync(queue, item); await queue.WaitForIdleAsync();
            Assert.Equal(1, h.Transfers);
        }
        finally { await queue.StopAllAsync(); }
        ContentThread restored = new(h.Thread.Board, h.Thread.ThreadUri);
        await h.Content.BindAsync(restored, h.Root.Path, restored: true);
        DownloadQueue restarted = new(2, long.MaxValue); restarted.ConfigureWorkState(new WorkStateStore(h.Root.Path, h.Profile)); restarted.BindWork(restored, resource);
        try { Assert.Empty(restored.MediaFailures); Assert.Equal(0, restarted.RequestMediaRetry(restored)); await restarted.WaitForIdleAsync(); Assert.Equal(1, h.Transfers); }
        finally { await restarted.StopAllAsync(); }
    }

    [Fact]
    public async Task TechnicalFilterRetryAfterSourceEndIsOneRepairWithOriginalDeadline()
    {
        await using Harness h = await Harness.StartAsync(Png(8, 4));
        DownloadItem item = await h.ItemAsync(new());
        await h.Content.BindAsync(h.Thread, h.Root.Path, restored: false);
        WorkStateStore work = new(h.Root.Path, h.Profile); DateTimeOffset end = DateTimeOffset.UtcNow.AddHours(-2);
        WorkResource resource = work.CommitIntent(new() { Url = h.Thread.Url, OutputRoot = h.Root.Path, Generation = 1, Intent = WorkIntent.Repairing, IsThread = true, Source = ThreadSourceStateValue.Archived, EndObservedAt = end });
        h.Thread.RestoreDurableState(resource);
        DownloadQueue queue = new(2, long.MaxValue); queue.ConfigureWorkState(work); queue.BindWork(h.Thread, resource);
        try
        {
            h.Current = false;
            await h.Thread.CaptureAndEnqueueAsync(queue, item); await queue.WaitForIdleAsync();
            Assert.Equal(MediaWorkStatus.NeedsIntervention, work.LoadMedia(resource.Url, 1).Single().Status); Assert.Equal(0, h.Transfers);
            h.Current = true;
            await h.Thread.CaptureAndEnqueueAsync(queue, item); await queue.WaitForIdleAsync(); Assert.Equal(0, h.Transfers);
            Assert.Equal(1, queue.RequestMediaRetry(h.Thread));
            await TestWait.UntilAsync(() => work.LoadMedia(resource.Url, 1).Single().Status == MediaWorkStatus.Succeeded);
            await queue.WaitForIdleAsync(); Assert.Equal(1, h.Transfers); Assert.Equal(end, work.LoadResources().Single().EndObservedAt);
            Assert.Equal(ThreadSourceState.Archived, h.Thread.SourceState); Assert.Equal(1, h.Thread.DownloadedMediaCount);
            Assert.Equal(MediaSelectionOutcome.Selected, (await h.Content.Catalog.GetDecisionsAsync(h.Resource)).Single().Selection.Outcome);
        }
        finally { await queue.StopAllAsync(); }
    }

    private sealed class ContentThread(IBoard board, Uri uri) : ProbeThread(board, uri)
    {
        public override bool SupportsContent => true;
        internal async Task CaptureAndEnqueueAsync(DownloadQueue queue, DownloadItem item)
        {
            await CaptureContentAsync(new(new ResourceId(Board.ImageBoard.Name, Board.BoardCode, ThreadId), Url, "Captured",
                SourceState, DateTimeOffset.UtcNow, Path.GetDirectoryName(item.DestinationPath)!,
                new[] { new CapturedPost("123", 0, PostFieldPresence.All, Html: "Actual capture", Attachments: new[] {
                    new CapturedAttachment(0, item.MediaIdentifier, item.Variant, item.DownloadUri.AbsoluteUri, item.OriginalFileName, item.DestinationPath, item.ExpectedSize, item.ExpectedMd5) }) }), CancellationToken.None);
            await EnqueueKnownMediaAsync(queue, new Dictionary<string, DownloadItem> { [JsonSerializer.Serialize(new[] {item.MediaIdentifier,item.Variant})] = item }, CancellationToken.None);
        }
    }

    [Theory]
    [InlineData("landscape.jpg", "landscape", true)]
    [InlineData("portrait.jpg", "landscape", false)]
    [InlineData("portrait.jpg", "portrait", true)]
    public async Task ActualJpegHeadersDriveTheIndependentWorker(string image, string orientation, bool match)
    {
        await using Harness h = await Harness.StartAsync(await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", image)));
        DownloadItem item = await h.ItemAsync(new() { ["orientation"] = orientation });
        if (match) { await h.RunAsync(item); Assert.Equal(h.Bytes, await File.ReadAllBytesAsync(item.DestinationPath)); }
        else Assert.True((await Assert.ThrowsAsync<MediaFilterOmissionException>(() => h.RunAsync(item))).IsNoMatch);
        Assert.True(h.ImageReads > 1);
    }

    [Fact]
    public async Task StoreIoFailureIsNeverReportedAsStoredAndDoesNotEraseExistingMedia()
    {
        await using Harness h = await Harness.StartAsync(Png(8, 4)); DownloadItem item = await h.ItemAsync(new());
        Directory.CreateDirectory(Path.GetDirectoryName(item.DestinationPath)!);
        string old = Path.Combine(Path.GetDirectoryName(item.DestinationPath)!, "existing.png"); await File.WriteAllTextAsync(old, "keep");
        await Assert.ThrowsAsync<IOException>(() => h.RunAsync(item, new MediaStore((_, _, _) => throw new IOException("fixture store publication denied"))));
        Assert.False(item.Thread.IsMediaDownloaded(item.MediaIdentifier)); Assert.False(File.Exists(item.DestinationPath));
        Assert.Equal("keep", await File.ReadAllTextAsync(old));
        Assert.Empty(Directory.GetFiles(Path.Combine(h.Root.Path, "filter-stages")));
    }

    private static bool Alive(int pid)
    {
        try { using Process process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    internal static byte[] Png(int width, int height)
    {
        using MemoryStream image = new(); image.Write(new byte[] {137,80,78,71,13,10,26,10});
        void Chunk(string kind, byte[] data)
        {
            byte[] length = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(length, data.Length); image.Write(length);
            byte[] name = Encoding.ASCII.GetBytes(kind); image.Write(name); image.Write(data);
            uint crc = 0xffffffff; foreach (byte value in name.Concat(data)) { crc ^= value; for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0); }
            BinaryPrimitives.WriteUInt32BigEndian(length, ~crc); image.Write(length);
        }
        byte[] header = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header, width); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height); header[8] = 8; header[9] = 6;
        Chunk("IHDR", header); using MemoryStream compressed = new();
        using (ZLibStream zip = new(compressed, CompressionLevel.Fastest, true)) { for (int y = 0; y < height; y++) zip.Write(new byte[1 + width * 4]); }
        Chunk("IDAT", compressed.ToArray()); Chunk("IEND", Array.Empty<byte>()); return image.ToArray();
    }

    private sealed class Harness : IAsyncDisposable, IPluginHostServices
    {
        public TempDirectory Root { get; } = new();
        public ProfileLease Profile { get; }
        public ThreadContentRuntime Content { get; }
        public FilterStageCoordinator Stage { get; }
        public PluginWorkerManager Manager { get; }
        private readonly bool _resourceFilter;
        public IPluginWorkerClient Worker { get; private set; } = null!;
        public byte[] Bytes { get; }
        public int Transfers, ImageReads;
        public bool Current = true;
        public Action? OnRead, OnResult;
        public Func<Task>? BeforeResult;
        public bool CheckReadAuthorization;
        public string? LastToken;
        public PluginOperationContext? Operation;
        public ResourceId Resource { get; } = new("stub", "g", "123");
        private readonly StubImageBoard _board;
        private readonly ContentThread _thread;
        public ContentThread Thread => _thread;
        private Harness(byte[] bytes, FilterStageOptions? options, bool resourceFilter, PluginWorkerOptions? workerOptions)
        {
            Bytes = bytes; _resourceFilter = resourceFilter; Profile = ProfileLease.Acquire(Root.Path); Content = new(Root.Path, Profile);
            Manager = new((workerOptions ?? new()) with { ExternalTemporaryBytes = () => Stage?.Snapshot.StagedBytes ?? 0 });
            Stage = new(Root.Path, Profile, (options ?? new()) with { WorkerTemporaryBytes = Manager.GetTemporaryBytes });
            _board = new(new DelegateHandler((_, _) => { Transfers++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes) }); }));
            _thread = new(_board.Board, new Uri("https://stub.test/g/thread/123"));
        }
        public static async Task<Harness> StartAsync(byte[] bytes, FilterStageOptions? options = null, bool resourceFilter = false, PluginWorkerOptions? workerOptions = null)
        {
            Harness h = new(bytes, options, resourceFilter, workerOptions);
            await h.Content.InitializeAsync();
            string dir = StorageLayout.GetThreadDirectory(h.Root.Path, "stub", "g", "123");
            await h.Content.Catalog.RegisterThreadAsync(new(h.Resource, h._thread.Url, "test", dir, true));
            await h.Content.Catalog.CaptureAsync(new(h.Resource, h._thread.Url, "test", ThreadSourceState.Active, DateTimeOffset.UtcNow, dir,
                new[] { new CapturedPost("123", 0, PostFieldPresence.All, Html: "preserve capture") }));
            string root = FindRoot();
            string workerPath = Path.Combine(root, "MeowChan.PluginWorker/bin/Release/net10.0/MeowChan.PluginWorker" + (OperatingSystem.IsWindows() ? ".exe" : ""));
            string assembly = resourceFilter ? typeof(ResourceFilter).Assembly.Location : typeof(ImageDimensionsFilter).Assembly.Location;
            string helperRoot = Path.Combine(root, "tests/MeowChan.PluginFilters.Helper/bin/Release/net10.0");
            List<PluginArtifactResource> resources = new();
            if (resourceFilter) foreach (string file in Directory.EnumerateFiles(helperRoot))
            {
                if (Path.GetExtension(file) == ".pdb") continue;
                string name = Path.GetFileName(file);
                resources.Add(new(name == "MeowChan.PluginFilters.Helper" + (OperatingSystem.IsWindows() ? ".exe" : "") ? "helper" : name,
                    name, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file)))));
            }
            PluginArtifact artifact = new("build", "execution", assembly, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(assembly))), "sdk", "compiler", resources, resourceFilter ? helperRoot : null);
            h.Worker = await h.Manager.StartAsync(new(workerPath, Array.Empty<string>(), Path.Combine(h.Root.Path, "workers")), artifact,
                new("configuration", new("1", Array.Empty<SettingValue>()), new Dictionary<string, SettingsSnapshot>()), h, filterWorker: true);
            h.Stage.Attach(h.Content, (request, _) => {
                h.Operation = new(request.Item.Thread.Url, 1, request.Decision.Selection.EffectiveRule.Revision, "grant-1");
                SettingsSnapshot settings = FilterRuleSettings.Create(request.Decision.Selection.EffectiveRule.ImageFilter, h.Worker.Description.Filters.Single());
                return Task.FromResult<FilterPluginBinding?>(new(h.Worker.Description.PluginId, "execution", "configuration", "grant-1", h.Operation,
                    settings, () => h.Current, async (filterRequest, operation, token) => {
                        h.LastToken = filterRequest.AssetToken;
                        ImageFilterResult result = await h.Worker.EvaluateAsync(filterRequest, operation, token);
                        if (h.BeforeResult != null) await h.BeforeResult();
                        h.OnResult?.Invoke(); return result;
                    }));
            });
            return h;
        }
        public async Task<DownloadItem> ItemAsync(Dictionary<string, string> values)
        {
            MediaImageFilterRule filter = new(MediaImageFilterMode.Plugin, Worker.Description.PluginId, Worker.Description.Filters.Single().Key, parameters: values);
            await Content.Rules.SaveAsync(new[] { new MediaRule(MediaRuleScope.Global) { ImageFilter = filter } });
            string path = StorageLayout.GetMediaPath(Root.Path, "stub", "g", "123", "456", ".png");
            DownloadItem item = new(new Uri("https://stub.test/456.png"), path, _board, _thread, "456", Bytes.Length, Convert.ToBase64String(MD5.HashData(Bytes)), "original.png") { FilterPostId = "123" };
            CandidateDecision decision = await Content.Catalog.GetOrCreateDecisionAsync(new(new(Resource, "456"), item.DownloadUri, path, Bytes.Length, item.ExpectedMd5, "original.png", extension: ".png"), Content.Rules);
            item.Filter = ((IOriginalMediaFilterFactory)Stage).Create(item, decision, () => Current && Content.Rules.Current.Revision == decision.Selection.EffectiveRule.Revision,
                async (noMatch, reason, token) => { await Content.Catalog.RecordFilterOmissionAsync(decision, noMatch, reason, token); });
            return item;
        }
        public Task RunAsync(DownloadItem item, MediaStore? store = null) => new DownloadWorker(item, new BandwidthLimiter(long.MaxValue), store ?? new MediaStore()).ExecuteAsync(CancellationToken.None);
        public Task<HostHttpResult> SendHttpAsync(HostHttpRequest request, PluginOperationContext operation, CancellationToken token) => throw new InvalidOperationException("No filter HTTP allowed.");
        public async Task<ImageAssetChunk> ReadImageAsync(string token, long offset, int count, PluginOperationContext operation, CancellationToken cancellationToken)
        {
            ImageReads++;
            if (CheckReadAuthorization)
            {
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Stage.ReadImageAsync("wrong-plugin", token, offset, count, operation, cancellationToken));
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Stage.ReadImageAsync(Worker.Description.PluginId, token, offset, count, operation with { Generation = operation.Generation + 1 }, cancellationToken));
                await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Stage.ReadImageAsync(Worker.Description.PluginId, token, 0, 65537, operation, cancellationToken));
                await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Stage.ReadImageAsync(Worker.Description.PluginId, token, Bytes.Length + 1, 1, operation, cancellationToken));
            }
            ImageAssetChunk chunk = await Stage.ReadImageAsync(Worker.Description.PluginId, token, offset, count, operation, cancellationToken);
            OnRead?.Invoke(); return chunk;
        }
        public async ValueTask DisposeAsync() { await Manager.DisposeAsync(); await Stage.DisposeAsync(); await Content.DisposeAsync(); _board.Dispose(); Profile.Dispose(); Root.Dispose(); }
        private static string FindRoot()
        {
            string? path = AppContext.BaseDirectory;
            while (path != null && !Directory.Exists(Path.Combine(path, "MeowChan.PluginWorker"))) path = Path.GetDirectoryName(path);
            return path ?? throw new InvalidOperationException("Repository root missing.");
        }
    }
}
