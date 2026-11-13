using System.Diagnostics;
using System.Text;
using MeowChan.Archive;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;

namespace MeowChan.Tests;

/// <summary>
/// Opt-in measurement of the media index cache and the index build on a synthetic library (no pass/fail, no GUI).
/// Runs only with MEOWCHAN_MEDIA_INDEX_BENCHMARK_OUT set to a result file; sizes via MEOWCHAN_MEDIA_INDEX_BENCHMARK_SIZES
/// (default "20000,100000" entries, 100 per thread).
/// </summary>
public sealed class MediaLibraryIndexBenchmark
{
    private const int PerThread = 100;

    [Fact]
    public async Task MeasureCacheAndBuild()
    {
        string? output = Environment.GetEnvironmentVariable("MEOWCHAN_MEDIA_INDEX_BENCHMARK_OUT");
        if (string.IsNullOrEmpty(output)) return;
        int[] sizes = (Environment.GetEnvironmentVariable("MEOWCHAN_MEDIA_INDEX_BENCHMARK_SIZES") ?? "20000,100000")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).ToArray();
        StringBuilder report = new();
        report.AppendLine("# media index benchmark " + DateTimeOffset.Now.ToString("O") + " loadavg " + LoadAverage());
        foreach (int size in sizes)
        {
            Cache(size, report);
            await BuildAsync(size, report);
        }
        File.AppendAllText(output, report.ToString());
    }

    private static string LoadAverage()
    {
        try { return File.ReadAllText("/proc/loadavg").Trim(); }
        catch (IOException) { return "n/a"; }
    }

    private static void Line(StringBuilder report, int size, string name, object value) =>
        report.AppendLine(size + "\t" + name + "\t" + value);

    private static double Ms(Stopwatch watch) => Math.Round(watch.Elapsed.TotalMilliseconds, 1);

    private static string Hex(int seed, int salt) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        BitConverter.GetBytes(((long)seed << 20) ^ salt))).ToLowerInvariant();

    /// <summary>A realistic synthetic index: 100 occurrences per thread, ISO times, profile paths, hashes and keys.</summary>
    internal static MediaLibrarySnapshot Synthetic(int size)
    {
        int threads = Math.Max(1, size / PerThread);
        List<MediaLibraryEntry> entries = new(size);
        List<MediaLibraryThreadState> states = new(threads);
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (int t = 0; t < threads; t++)
        {
            ResourceId id = new("4chan", t % 3 == 0 ? "g" : t % 3 == 1 ? "wg" : "w", (100_000_000 + t).ToString());
            string title = "Synthetic thread number " + t + " about wallpapers and other things";
            string directory = "/home/user/.oChan/media/4chan/" + id.Board + "/" + id.Thread;
            states.Add(new(id, 10 + t, new[] { new MediaLibraryManifestState(directory, Hex(t, -1)) }, true));
            for (int n = 0; n < PerThread && entries.Count < size; n++)
            {
                string media = (1_731_000_000_000L + t * 1000L + n).ToString();
                bool missing = n % 50 == 49;
                entries.Add(new MediaLibraryEntry(id, title, 10 + t, start.AddHours(t), start.AddHours(t + 1),
                    (500_000_000 + t * 1000 + n).ToString(), n, start.AddHours(t).AddMinutes(n), 0, "rk1:" + Hex(t, n),
                    missing ? null : directory, missing ? null : Hex(t, n + 100_000), missing ? null : Hex(t, n + 200_000),
                    missing ? null : 100_000 + n, media, "original", media + (n % 7 == 0 ? ".webm" : ".jpg"),
                    n % 7 == 0 ? SavedMediaKind.Video : SavedMediaKind.Image,
                    missing ? MediaLibraryPresence.Missing : MediaLibraryPresence.Present, false, false));
            }
        }
        return new MediaLibrarySnapshot(entries, states, Array.Empty<string>(), complete: true);
    }

    private static double MiB(long bytes) => Math.Round(bytes / 1048576.0, 1);

    private static void Settle()
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    }

    // Load: wall time (for the old page this was UI-thread time), allocations and what the loaded index keeps alive.
    private static MediaLibrarySnapshot? MeasureLoad(int size, StringBuilder report, string name, Func<MediaLibrarySnapshot?> load)
    {
        Settle();
        long before = GC.GetTotalMemory(forceFullCollection: true);
        long allocated = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch watch = Stopwatch.StartNew();
        MediaLibrarySnapshot? loaded = load();
        Line(report, size, name + ".load.ms", Ms(watch));
        Line(report, size, name + ".load.allocMiB", MiB(GC.GetTotalAllocatedBytes(precise: true) - allocated));
        Line(report, size, name + ".load.retainedMiB", MiB(GC.GetTotalMemory(forceFullCollection: true) - before));
        return loaded;
    }

    private static void MeasureSave(int size, StringBuilder report, string name, Action save)
    {
        Settle();
        long allocated = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch watch = Stopwatch.StartNew();
        save();
        Line(report, size, name + ".ms", Ms(watch));
        Line(report, size, name + ".allocMiB", MiB(GC.GetTotalAllocatedBytes(precise: true) - allocated));
    }

    private static void Cache(int size, StringBuilder report)
    {
        using TempDirectory temp = new();
        string before = Path.Combine(temp.Path, "before", MediaLibraryIndexCache.FileName);
        string after = Path.Combine(temp.Path, "after", MediaLibraryIndexCache.FileName);
        MediaLibrarySnapshot snapshot = Synthetic(size);

        // Before: one string for the whole document on save and on load; every visit of the media page saved again.
        MeasureSave(size, report, "before.save", () => BaselineMediaLibraryIndexCache.Save(before, snapshot));
        Line(report, size, "before.file.MiB", Math.Round(new FileInfo(before).Length / 1048576.0, 2));
        MediaLibrarySnapshot? old = MeasureLoad(size, report, "before", () => BaselineMediaLibraryIndexCache.Load(before));
        Assert.Equal(snapshot.Entries, old!.Entries);
        MeasureSave(size, report, "before.saveAgain", () => BaselineMediaLibraryIndexCache.Save(before, old));
        old = null;

        // After: streamed save and load, a page's store skips writing an unchanged index.
        MediaLibraryIndexCacheFile store = new(after);
        MeasureSave(size, report, "after.save", () => store.SaveAsync(snapshot, MediaLibraryIndexCache.DefaultLimitBytes).GetAwaiter().GetResult());
        Line(report, size, "after.file.MiB", Math.Round(new FileInfo(after).Length / 1048576.0, 2));
        MediaLibraryIndexCacheFile reopened = new(after);
        MediaLibrarySnapshot? loaded = MeasureLoad(size, report, "after", () => reopened.LoadAsync().GetAwaiter().GetResult());
        Assert.Equal(snapshot.Entries, loaded!.Entries);
        MediaLibraryIndexCacheWrite again = default!;
        MeasureSave(size, report, "after.saveAgain", () => again = reopened.SaveAsync(loaded, MediaLibraryIndexCache.DefaultLimitBytes).GetAwaiter().GetResult());
        Line(report, size, "after.saveAgain.status", again.Status);
        // The new reader also reads the file the earlier code wrote (version 3).
        Assert.Equal(snapshot.Entries, MediaLibraryIndexCacheSync.Load(before)!.Entries);
        GC.KeepAlive(loaded);
    }

    private static async Task BuildAsync(int size, StringBuilder report)
    {
        using TempDirectory temp = new();
        int threads = Math.Max(1, size / PerThread);
        Stopwatch watch = Stopwatch.StartNew();
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(temp.Path, lease))
            for (int index = 0; index < threads; index++)
            {
                ResourceId id = new("scale", "b", (1000 + index).ToString());
                string directory = Path.Combine(temp.Path, "media", id.Thread!);
                byte[] Bytes(int n) => Encoding.UTF8.GetBytes(id.Thread + "-" + n);
                // Nine of ten attachments are saved, the rest only captured.
                const int saved = PerThread * 9 / 10;
                MediaLibraryIndexTests.File_[] files = Enumerable.Range(0, saved)
                    .Select(n => new MediaLibraryIndexTests.File_("f" + n + ".jpg", "m" + n, Bytes(n), "p" + n)).ToArray();
                await MediaLibraryIndexTests.WriteManifestAsync(directory, id, files);
                CapturedPost[] posts = Enumerable.Range(0, PerThread).Select(n => new CapturedPost("p" + n, n, PostFieldPresence.All,
                    Html: "x", Attachments: [MediaLibraryIndexTests.Attachment(0, "m" + n, "f" + n + ".jpg", n < saved ? Bytes(n) : null)])).ToArray();
                await catalog.CaptureAsync(new CapturedThreadSnapshot(id, "https://offline.invalid/" + id.Thread, "Synthetic thread " + index,
                    ThreadSourceState.Archived, DateTimeOffset.UnixEpoch, directory, posts));
            }
        Line(report, size, "fixture.seed.ms", Ms(watch));

        int opens = 0, reports = 0;
        Task<MediaAssetSnapshot> Open(string directory, CancellationToken token)
        {
            Interlocked.Increment(ref opens);
            return MediaAssetReader.OpenAsync(directory, token);
        }
        Progress progress = new(() => reports++);
        Func<CancellationToken, Task<ArchiveReader>> reader = t => ArchiveReader.OpenAsync(temp.Path, t);

        watch.Restart();
        MediaLibrarySnapshot cold = await MediaLibraryIndexBuilder.BuildAsync(reader, Open, null, progress);
        Line(report, size, "build.cold.ms", Ms(watch));
        Line(report, size, "build.cold.manifestOpens", opens);
        Assert.Equal(size, cold.Entries.Count);

        // Before: every open of the media page reads and hashes every manifest again and reports once per thread.
        opens = reports = 0;
        watch.Restart();
        await MediaLibraryIndexBuilder.BuildAsync(reader, Open, cold, progress);
        Line(report, size, "before.build.warm.ms", Ms(watch));
        Line(report, size, "before.build.warm.manifestOpens", opens);
        Line(report, size, "before.build.warm.statusUpdates", reports);

        // After: the first build records manifest identities; the next ones skip unchanged manifests, the status is throttled.
        MediaManifestCache manifests = new(Open, identity: true);
        opens = 0;
        watch.Restart();
        MediaLibrarySnapshot pinned = await MediaLibraryIndexBuilder.BuildAsync(reader, manifests, cold);
        Line(report, size, "after.build.firstWithIdentity.ms", Ms(watch));
        Line(report, size, "after.build.firstWithIdentity.manifestOpens", opens);
        Assert.Equal(cold.Entries, pinned.Entries);

        opens = reports = 0;
        ThrottledProgress<MediaLibraryProgress> throttled = new(progress, TimeSpan.FromMilliseconds(100), value => value.ThreadsDone >= value.ThreadsTotal);
        watch.Restart();
        MediaLibrarySnapshot warm = await MediaLibraryIndexBuilder.BuildAsync(reader, manifests, pinned, throttled);
        Line(report, size, "after.build.warm.ms", Ms(watch));
        Line(report, size, "after.build.warm.manifestOpens", opens);
        Line(report, size, "after.build.warm.statusUpdates", reports);
        Assert.Equal(cold.Entries, warm.Entries);

        // After a restart: the identities come from the saved index.
        string path = Path.Combine(temp.Path, "cache", MediaLibraryIndexCache.FileName);
        await new MediaLibraryIndexCacheFile(path).SaveAsync(warm, MediaLibraryIndexCache.DefaultLimitBytes);
        MediaLibrarySnapshot restarted = (await new MediaLibraryIndexCacheFile(path).LoadAsync())!;
        opens = 0;
        watch.Restart();
        MediaLibrarySnapshot afterRestart = await MediaLibraryIndexBuilder.BuildAsync(reader, new MediaManifestCache(Open, identity: true), restarted);
        Line(report, size, "after.build.afterRestart.ms", Ms(watch));
        Line(report, size, "after.build.afterRestart.manifestOpens", opens);
        Assert.Equal(cold.Entries, afterRestart.Entries);
    }

    private sealed class Progress(Action report) : IProgress<MediaLibraryProgress>
    {
        public void Report(MediaLibraryProgress value) => report();
    }
}
