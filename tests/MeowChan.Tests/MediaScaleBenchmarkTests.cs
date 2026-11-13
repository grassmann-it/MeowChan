using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Gallery;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;
using MeowChan.Tests.Shared;
using Xunit.Abstractions;

namespace MeowChan.Tests;

/// <summary>
/// Skipped unless MEOWCHAN_MEDIA_SCALE_MICRO=1, so the normal suites never pay for a large synthetic collection.
/// Run: MEOWCHAN_MEDIA_SCALE_MICRO=1 MEOWCHAN_MEDIA_SCALE=S|M|L dotnet test tests/MeowChan.Tests --filter Category=MediaScaleBenchmark
/// </summary>
public sealed class MediaScaleBenchmarkFactAttribute : FactAttribute
{
    public MediaScaleBenchmarkFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MEOWCHAN_MEDIA_SCALE_MICRO") != "1")
            Skip = "Benchmark only: set MEOWCHAN_MEDIA_SCALE_MICRO=1 (tier: MEOWCHAN_MEDIA_SCALE=S|M|L).";
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MediaScaleBenchmarkCollection
{
    public const string Name = "Media scale benchmark";
}

/// <summary>
/// GUI-free before/after measurements for the large-collection audit (PERFORMANCE-GROSSE-SAMMLUNG-2026-11-13, 4.3):
/// private tag parse and one write, media index cache save/load, cold and warm index build and the page's
/// filter/group/sort pipeline. Timings are observations, never assertions; only functional facts are asserted.
/// Each test prints one "MEDIA_SCALE_MICRO {json}" line and, with MEOWCHAN_MEDIA_SCALE_REPORT=dir, writes it there.
/// </summary>
[Collection(MediaScaleBenchmarkCollection.Name)]
public sealed class MediaScaleBenchmarkTests(ITestOutputHelper output)
{
    [MediaScaleBenchmarkFact]
    [Trait("Category", "MediaScaleBenchmark")]
    public void PrivateTagStoreParseAndOneWrite()
    {
        MediaScaleTier tier = MediaScaleTier.FromEnvironment();
        using TempDirectory directory = new();
        Stopwatch generate = Stopwatch.StartNew();
        (int threads, int media) = MediaScaleFixture.WritePrivateTags(Path.Combine(directory.Path, PrivateTagStore.FileName), tier);
        double generated = generate.Elapsed.TotalMilliseconds;
        long fileBytes = new FileInfo(Path.Combine(directory.Path, PrivateTagStore.FileName)).Length;
        List<PrivateTagWritePhase> phases = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        // The own-tag count is a setting (default 1024); the benchmark measures the technical ceiling, so it raises it.
        PrivateTagStore store = new(directory.Path, lease, new PrivateTagLimits { TagLimit = PrivateTagLimits.MaximumTagLimit }) { WritePhaseForTests = phases.Add };

        // First read includes JIT; the second is the steady state the app pays on every snapshot read.
        Sample first = Measure(() => store.ReadSnapshot());
        Sample second = Measure(() => store.ReadSnapshot());
        PrivateTagStoreSnapshot snapshot = store.ReadSnapshot();
        Assert.Equal(tier.Tags, snapshot.Tags.Count);
        Assert.Equal(threads, snapshot.ThreadAssignments.Count);
        Assert.Equal(media, snapshot.MediaAssignments.Count);

        // One user write: Mutate = parse primary + parse .bak check + serialize + fsync replace. A full vocabulary
        // (the store's definition limit) cannot take a new tag, so it renames one instead: the same write path.
        bool full = snapshot.Tags.Count >= store.Limits.TagLimit;
        Sample define = Measure(() => _ = full ? store.Rename(snapshot.Tags[^1].Id, "bench-renamed-tag") : store.Define("bench-new-tag"));
        int definePhases = phases.Count;
        Sample assign = Measure(() => store.SetThread(snapshot.Tags[0].Id, MediaScaleFixture.ThreadId(1 % tier.Threads), true));
        long afterBytes = new FileInfo(Path.Combine(directory.Path, PrivateTagStore.FileName)).Length;
        Report("private-tags", tier, new
        {
            requestedTags = tier.RequestedTags, tags = tier.Tags, tagLimit = store.Limits.TagLimit, firstWrite = full ? "rename" : "define",
            threadAssignments = threads, mediaAssignments = media, generateMs = generated, fileBytes, afterBytes,
            readSnapshotFirst = first, readSnapshotSteady = second, defineWrite = define, writePhasesPerDefine = definePhases,
            setThreadWrite = assign, backupExists = File.Exists(Path.Combine(directory.Path, PrivateTagStore.FileName + ".bak"))
        });
    }

    // The benchmark measures the writer itself, so it runs with the technical ceilings instead of the user's limits.
    private static async Task SaveCacheAsync(string path, MediaLibrarySnapshot snapshot)
    {
        MediaLibraryIndexCacheWrite write = await MediaLibraryIndexCache.SaveAsync(path, snapshot, MediaLibraryIndexCache.MaximumBytes,
            MediaLibraryIndexCache.EntryCeiling, MediaLibraryIndexCache.ThreadCeiling);
        Assert.True(write.Status is MediaLibraryIndexCacheWriteStatus.Written or MediaLibraryIndexCacheWriteStatus.Unchanged, write.Status.ToString());
    }

    [MediaScaleBenchmarkFact]
    [Trait("Category", "MediaScaleBenchmark")]
    public async Task MediaIndexCacheSaveAndLoad()
    {
        MediaScaleTier tier = MediaScaleTier.FromEnvironment();
        using TempDirectory directory = new();
        Stopwatch build = Stopwatch.StartNew();
        MediaLibrarySnapshot snapshot = SyntheticSnapshot(directory.Path, tier);
        double synthesized = build.Elapsed.TotalMilliseconds;
        string path = Path.Combine(directory.Path, "cache", MediaLibraryIndexCache.FileName);
        Sample saveFirst = await MeasureAsync(() => SaveCacheAsync(path, snapshot));
        long bytes = new FileInfo(path).Length;
        Sample saveSecond = await MeasureAsync(() => SaveCacheAsync(path, snapshot));
        MediaLibrarySnapshot? loaded = null;
        Sample loadFirst = await MeasureAsync(async () => loaded = (await MediaLibraryIndexCache.LoadAsync(path))?.Snapshot);
        Sample loadSecond = await MeasureAsync(async () => loaded = (await MediaLibraryIndexCache.LoadAsync(path))?.Snapshot);
        Assert.NotNull(loaded);
        Assert.Equal(snapshot.Entries.Count, loaded!.Entries.Count);
        Report("index-cache", tier, new
        {
            entries = snapshot.Entries.Count, threads = snapshot.Threads.Count, synthesizeMs = synthesized, fileBytes = bytes,
            maximumBytes = MediaLibraryIndexCache.MaximumBytes, saveFirst, saveUnchanged = saveSecond, loadFirst, loadSteady = loadSecond
        });
    }

    [MediaScaleBenchmarkFact]
    [Trait("Category", "MediaScaleBenchmark")]
    public async Task MediaIndexBuildColdWarmAndPresentPipeline()
    {
        MediaScaleTier tier = MediaScaleTier.FromEnvironment();
        using TempDirectory directory = new();
        MediaScaleFixtureSummary fixture = await MediaScaleFixture.WriteAsync(directory.Path, tier, line => output.WriteLine(line));
        int opened = 0;
        Task<MediaAssetSnapshot> OpenManifest(string path, CancellationToken token)
        {
            Interlocked.Increment(ref opened);
            return MediaAssetReader.OpenAsync(path, token);
        }
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore booru = new(directory.Path, lease);
        int ticks = 0;
        SynchronousProgress progress = new(() => Interlocked.Increment(ref ticks));
        Task<ArchiveReader> OpenReader(CancellationToken token) => ArchiveReader.OpenAsync(directory.Path, token);

        MediaLibrarySnapshot? cold = null, warm = null;
        Sample coldBuild = await MeasureAsync(async () => cold = await MediaLibraryIndexBuilder.BuildAsync(OpenReader, OpenManifest,
            null, progress, CancellationToken.None, booru.Read));
        int coldManifests = opened, coldTicks = ticks;
        opened = 0; ticks = 0;
        Sample warmBuild = await MeasureAsync(async () => warm = await MediaLibraryIndexBuilder.BuildAsync(OpenReader, OpenManifest,
            cold, progress, CancellationToken.None, booru.Read));
        Assert.True(cold!.Complete && warm!.Complete, string.Join("; ", cold.Warnings.Concat(warm!.Warnings).Take(5)));
        Assert.Equal(fixture.MediaFiles + fixture.MissingAttachments + tier.BooruPosts, cold.Entries.Count);
        Assert.Equal(cold.Entries.Count, warm.Entries.Count);
        int warmManifests = opened, warmTicks = ticks;
        BooruCatalogStoreCounters counters = booru.Counters;

        // MediaPage.Present without the UI: filter, group equal bytes and order, per sort and with a name filter.
        IReadOnlyList<MediaLibraryEntry> all = warm.Entries;
        MediaLibraryUserData data = new(new PrivateTagStore(directory.Path, lease).ReadSnapshot(), [], []);
        Sample userData = Measure(() => new MediaLibraryUserData(new PrivateTagStore(directory.Path, lease).ReadSnapshot(), [], []));
        Dictionary<string, Sample> present = new(StringComparer.Ordinal);
        foreach (MediaLibrarySort sort in Enum.GetValues<MediaLibrarySort>())
            present["sort." + sort] = Measure(() => MediaLibraryGrouping.Build(all,
                MediaLibraryFilterEngine.Evaluate(all, new MediaLibraryFilter(), data).Passing, sort, false, 17, data.IsFavorite));
        foreach (string text in new[] { "i", "img-0", "img-00", "img-001" })
            present["name." + text] = Measure(() => MediaLibraryGrouping.Build(all,
                MediaLibraryFilterEngine.Evaluate(all, new MediaLibraryFilter(nameContains: text), data).Passing,
                MediaLibrarySort.ByThread, false, 17, data.IsFavorite));
        present["countTiles"] = Measure(() => MediaLibraryGrouping.CountTiles(all, false));
        Report("index-build", tier, new
        {
            fixture, entries = cold.Entries.Count, coldBuild, coldManifestsOpened = coldManifests, coldProgressReports = coldTicks,
            warmBuild, warmManifestsOpened = warmManifests, warmProgressReports = warmTicks,
            warmReusedThreads = warm.Threads.Count, booruCatalog = counters, userDataConstruction = userData, present
        });
    }

    // Entries as the builder produces them for the fixture layout, without writing any file.
    private static MediaLibrarySnapshot SyntheticSnapshot(string profile, MediaScaleTier tier)
    {
        List<MediaLibraryEntry> entries = new(tier.Media + tier.BooruPosts);
        List<MediaLibraryThreadState> threads = new(tier.Threads);
        for (int thread = 0; thread < tier.Threads; thread++)
        {
            ResourceId id = MediaScaleFixture.ThreadId(thread);
            string directory = MediaScaleFixture.ThreadDirectory(profile, thread);
            DateTimeOffset captured = MediaScaleFixture.CapturedAt(thread);
            string title = "Scale thread " + thread.ToString("D4");
            threads.Add(new(id, 1, [new(directory, MediaScaleFixture.Sha256Of(System.Text.Encoding.UTF8.GetBytes(directory)))], true));
            foreach (MediaScaleFile file in MediaScaleFixture.FilesOf(tier, thread))
            {
                byte[] bytes = file.Bytes;
                string sha = MediaScaleFixture.Sha256Of(bytes);
                entries.Add(file.Missing
                    ? new(id, title, 1, captured, captured, file.PostId, file.Index + 1, captured.AddMinutes(file.Index + 1), 0, null,
                        null, null, null, null, file.MediaId, "original", file.Name, SavedMediaKind.Image, MediaLibraryPresence.Missing, false, false)
                    : new(id, title, 1, captured, captured, file.PostId, file.Index + 1, captured.AddMinutes(file.Index + 1), 0,
                        MediaScaleFixture.RevisionKeyOf(file, bytes), directory, MediaScaleFixture.AssetIdOf(file.Name, file.MediaId, file.Name, sha),
                        sha, bytes.Length, file.MediaId, "original", file.Name, SavedMediaKind.Image, MediaLibraryPresence.Present, false, false));
            }
        }
        for (int post = 0; post < tier.BooruPosts; post++)
        {
            string id = MediaScaleFixture.BooruPostId(post), name = id + ".png";
            string sha = MediaScaleFixture.Sha256Of(System.Text.Encoding.UTF8.GetBytes(id));
            entries.Add(new MediaLibraryEntry(BooruMediaIdentity.Thread(id), "Danbooru #" + id, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
                null, null, null, null, null, Path.Combine(profile, "danbooru", id), MediaScaleFixture.AssetIdOf(name, BooruMediaIdentity.MediaIdentifier(id), name, sha),
                sha, 100, BooruMediaIdentity.MediaIdentifier(id), "original", name, SavedMediaKind.Image, MediaLibraryPresence.Present, true, false)
                { BooruPostId = id });
        }
        return new MediaLibrarySnapshot(entries, threads, [], complete: true);
    }

    internal sealed record Sample(double Milliseconds, double AllocatedMegabytes, int Gen2Collections, double ManagedHeapAfterMegabytes);

    private static Sample Measure(Action action)
    {
        int gen2 = GC.CollectionCount(2);
        long allocated = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch watch = Stopwatch.StartNew();
        action();
        double elapsed = watch.Elapsed.TotalMilliseconds;
        return new(elapsed, (GC.GetTotalAllocatedBytes(precise: true) - allocated) / 1048576.0, GC.CollectionCount(2) - gen2,
            GC.GetTotalMemory(forceFullCollection: false) / 1048576.0);
    }

    private static async Task<Sample> MeasureAsync(Func<Task> action)
    {
        int gen2 = GC.CollectionCount(2);
        long allocated = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch watch = Stopwatch.StartNew();
        await action();
        double elapsed = watch.Elapsed.TotalMilliseconds;
        return new(elapsed, (GC.GetTotalAllocatedBytes(precise: true) - allocated) / 1048576.0, GC.CollectionCount(2) - gen2,
            GC.GetTotalMemory(forceFullCollection: false) / 1048576.0);
    }

    private void Report(string name, MediaScaleTier tier, object result)
    {
        string json = JsonSerializer.Serialize(new
        {
            benchmark = name, tier = tier.Name, at = DateTimeOffset.UtcNow, runtime = RuntimeInformation.FrameworkDescription,
            processors = Environment.ProcessorCount, loadAverage = LoadAverage(), result
        });
        output.WriteLine("MEDIA_SCALE_MICRO " + json);
        if (Environment.GetEnvironmentVariable("MEOWCHAN_MEDIA_SCALE_REPORT") is { Length: > 0 } target)
        {
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "micro-" + name + "-" + tier.Name + ".json"), json + Environment.NewLine);
        }
    }

    private static string? LoadAverage()
    {
        try { return OperatingSystem.IsLinux() ? File.ReadAllText("/proc/loadavg").Trim() : null; }
        catch (IOException) { return null; }
    }

    // Progress<T> would post to a thread-pool context; the builder's reports are counted where they happen.
    private sealed class SynchronousProgress(Action report) : IProgress<MediaLibraryProgress>
    {
        public void Report(MediaLibraryProgress value) => report();
    }
}
