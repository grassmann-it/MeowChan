using System.Text;
using MeowChan.Archive;
using MeowChan.Gallery;
using MeowChan.Persistence;
using MeowChan.Storage;
using static MeowChan.Tests.MediaLibraryIndexTests;

namespace MeowChan.Tests;

/// <summary>
/// The media index cache file (streamed v4, v3 still read, written only when changed, a limit that says so) and the
/// build that skips manifests whose file identity did not change. Every index here is compared with a plain build.
/// </summary>
public sealed class MediaLibraryIndexCacheTests
{
    private sealed class CountingManifests
    {
        private int _opens;
        internal int Opens => _opens;
        internal void Reset() => _opens = 0;
        internal MediaManifestCache Cache() => new((directory, token) =>
        {
            Interlocked.Increment(ref _opens);
            return MediaAssetReader.OpenAsync(directory, token);
        }, identity: true);
    }

    private static Func<CancellationToken, Task<ArchiveReader>> Reader(string profile) => token => ArchiveReader.OpenAsync(profile, token);

    private static Task<MediaLibrarySnapshot> PlainAsync(string profile, MediaLibrarySnapshot? previous = null) => BuildAsync(profile, previous);

    private static bool IdentitiesAvailable(string profile) =>
        RegularProfileFile.TryGetIdentity(Path.Combine(profile, "media", "a", ".meowchan-manifest.json")) != null;

    private sealed class Collect(List<MediaLibraryProgress> items) : IProgress<MediaLibraryProgress>
    {
        public void Report(MediaLibraryProgress value) => items.Add(value);
    }

    [Fact]
    public async Task UnchangedManifestsAreNotOpenedAgainAndTheIndexStaysTheSame()
    {
        using TempDirectory temp = new();
        await SeedAsync(temp.Path);
        if (!IdentitiesAvailable(temp.Path)) return; // A platform without file identities simply reads every manifest.
        CountingManifests counting = new();
        MediaManifestCache manifests = counting.Cache();

        MediaLibrarySnapshot first = await MediaLibraryIndexBuilder.BuildAsync(Reader(temp.Path), manifests);
        Assert.Equal(2, counting.Opens);
        Assert.Equal((await PlainAsync(temp.Path)).Entries, first.Entries);
        Assert.All(first.Threads.SelectMany(thread => thread.Manifests), state => Assert.NotNull(state.Identity));

        counting.Reset();
        List<MediaLibraryProgress> progress = new();
        MediaLibrarySnapshot second = await MediaLibraryIndexBuilder.BuildAsync(Reader(temp.Path), manifests, first, new Collect(progress));
        Assert.Equal(0, counting.Opens);
        Assert.Equal(new MediaLibraryProgress(2, 2, 2), progress.Last());
        Assert.Equal(first.Entries, second.Entries);
        Assert.True(MediaLibrarySnapshot.SameContent(first, second));

        // A's manifest gets a new file: only A is opened again, and the result equals a plain full read.
        await WriteManifestAsync(Path.Combine(temp.Path, "media", "a"), A, new File_("shared.png", "m1", [1, 2, 3], "100"),
            new File_("loose.webm", "m9", [4, 5, 6, 7]), new File_("later.jpg", "m8", Encoding.UTF8.GetBytes("later")));
        counting.Reset();
        MediaLibrarySnapshot third = await MediaLibraryIndexBuilder.BuildAsync(Reader(temp.Path), manifests, second);
        Assert.Equal(1, counting.Opens);
        Assert.Contains(third.Entries, entry => entry.Thread == A && entry.Name == "later.jpg");
        Assert.Equal((await PlainAsync(temp.Path)).Entries.OrderBy(Key), third.Entries.OrderBy(Key));
        Assert.False(MediaLibrarySnapshot.SameContent(second, third));

        // After a restart the identities come from the cache file: still nothing to open.
        string path = Path.Combine(temp.Path, "cache", MediaLibraryIndexCache.FileName);
        Assert.Equal(MediaLibraryIndexCacheWriteStatus.Written, (await new MediaLibraryIndexCacheFile(path).SaveAsync(third, MediaLibraryIndexCache.DefaultLimitBytes)).Status);
        MediaLibrarySnapshot loaded = (await new MediaLibraryIndexCacheFile(path).LoadAsync())!;
        Assert.True(MediaLibrarySnapshot.SameContent(third, loaded));
        counting.Reset();
        MediaLibrarySnapshot restarted = await MediaLibraryIndexBuilder.BuildAsync(Reader(temp.Path), counting.Cache(), loaded);
        Assert.Equal(0, counting.Opens);
        Assert.Equal(third.Entries, restarted.Entries);

        // A plain opener never trusts identities: everything is read, the reuse by generation is unchanged.
        int plainOpens = 0;
        await MediaLibraryIndexBuilder.BuildAsync(Reader(temp.Path), new MediaManifestCache((d, t) => { plainOpens++; return MediaAssetReader.OpenAsync(d, t); }), loaded);
        Assert.Equal(2, plainOpens);
    }

    private static string Key(MediaLibraryEntry entry) => entry.Thread + "|" + entry.PostId + "|" + entry.Position + "|" + entry.AssetId;

    [Fact]
    public async Task ManifestViewsOfThePageAreReusedOnlyWhileTheirFileIsUnchanged()
    {
        using TempDirectory temp = new();
        await SeedAsync(temp.Path);
        if (!IdentitiesAvailable(temp.Path)) return;
        CountingManifests counting = new();
        MediaManifestCache cache = counting.Cache();
        string a = Path.Combine(temp.Path, "media", "a");

        MediaAssetSnapshot first = await cache.OpenAsync(a);
        Assert.Same(first, await cache.OpenAsync(a));
        cache.Invalidate();
        Assert.Same(first, await cache.OpenAsync(a)); // Same file identity: the kept view is still valid.
        Assert.Equal(1, counting.Opens);
        // The build looks the view up instead of reading the file.
        Assert.Same(first, (await cache.ReadAsync(a)).Snapshot);
        Assert.Equal(1, counting.Opens);

        await WriteManifestAsync(a, A, new File_("shared.png", "m1", [1, 2, 3], "100"));
        Assert.Same(first, await cache.OpenAsync(a)); // Within one round the page keeps what it showed, as before.
        cache.Invalidate();
        MediaAssetSnapshot changed = await cache.OpenAsync(a);
        Assert.NotSame(first, changed);
        Assert.Equal(1, changed.Count);
        // A build read that the page never asked for is not kept.
        string b = Path.Combine(temp.Path, "media", "b");
        await cache.ReadAsync(b);
        await cache.ReadAsync(b);
        Assert.Equal(4, counting.Opens);
    }

    [Fact]
    public async Task VersionThreeFilesOfTheEarlierCodeStillLoadAndTheFormatIsUnchangedOtherwise()
    {
        using TempDirectory temp = new();
        await SeedAsync(temp.Path);
        MediaLibrarySnapshot built = await PlainAsync(temp.Path);
        string path = Path.Combine(temp.Path, "cache", MediaLibraryIndexCache.FileName);

        // A file the earlier code wrote (version 3) is read as it is: no cold rebuild after the update.
        BaselineMediaLibraryIndexCache.Save(path, built);
        MediaLibrarySnapshot? fromV3 = MediaLibraryIndexCacheSync.Load(path);
        Assert.NotNull(fromV3);
        Assert.Equal(built.Entries, fromV3!.Entries);
        Assert.Equal(built.Threads.Select(thread => (thread.Thread, thread.Revision, thread.Complete, thread.Manifests.Count)),
            fromV3.Threads.Select(thread => (thread.Thread, thread.Revision, thread.Complete, thread.Manifests.Count)));
        Assert.All(fromV3.Threads.SelectMany(thread => thread.Manifests), state => Assert.Null(state.Identity));
        // Version 3 reuse is by generation, exactly as before.
        List<MediaLibraryProgress> progress = new();
        await BuildAsync(temp.Path, fromV3, progress);
        Assert.Equal(2, progress.Last().Reused);

        // Without identities the new document is the old one with another version number: the earlier code reads it too.
        MediaLibraryIndexCacheSync.Save(path, built);
        string written = await File.ReadAllTextAsync(path);
        Assert.Contains("\"Version\":4", written);
        await File.WriteAllTextAsync(path, written.Replace("\"Version\":4", "\"Version\":3"));
        Assert.Equal(built.Entries, BaselineMediaLibraryIndexCache.Load(path)!.Entries);

        // A version 3 document carrying version 4 fields is damaged.
        if (IdentitiesAvailable(temp.Path))
        {
            MediaLibrarySnapshot pinned = await MediaLibraryIndexBuilder.BuildAsync(Reader(temp.Path), new MediaManifestCache());
            MediaLibraryIndexCacheSync.Save(path, pinned);
            string withIdentity = await File.ReadAllTextAsync(path);
            Assert.Contains("\"I\":[", withIdentity);
            Assert.True(MediaLibrarySnapshot.SameContent(pinned, MediaLibraryIndexCacheSync.Load(path)));
            await File.WriteAllTextAsync(path, withIdentity.Replace("\"Version\":4", "\"Version\":3"));
            Assert.Null(MediaLibraryIndexCacheSync.Load(path));
            foreach (string damaged in new[] { withIdentity.Replace("\"I\":[", "\"I\":[1,"), withIdentity.Replace("\"I\":[", "\"I\":\"x\",\"J\":["),
                         withIdentity.Replace("\"D\":", "\"Dx\":") })
            {
                await File.WriteAllTextAsync(path, damaged);
                Assert.Null(MediaLibraryIndexCacheSync.Load(path));
            }
        }
    }

    [Fact]
    public async Task LoadedIndexSharesRepeatedValues()
    {
        using TempDirectory temp = new();
        string path = Path.Combine(temp.Path, MediaLibraryIndexCache.FileName);
        MediaLibrarySnapshot synthetic = MediaLibraryIndexBenchmark.Synthetic(1_000);
        MediaLibraryIndexCacheSync.Save(path, synthetic);
        MediaLibrarySnapshot loaded = MediaLibraryIndexCacheSync.Load(path)!;
        Assert.Equal(synthetic.Entries, loaded.Entries);
        foreach (IGrouping<ResourceId, MediaLibraryEntry> thread in loaded.Entries.GroupBy(entry => entry.Thread))
        {
            MediaLibraryEntry first = thread.First();
            MediaLibraryThreadState state = loaded.Threads.Single(item => item.Thread == thread.Key);
            Assert.All(thread, entry =>
            {
                Assert.Same(state.Thread, entry.Thread);
                Assert.Same(first.ThreadTitle, entry.ThreadTitle);
                Assert.Same(first.Variant, entry.Variant);
                if (entry.Directory != null) Assert.Same(state.Manifests[0].Directory, entry.Directory);
            });
        }
        Assert.Single(loaded.Entries.Select(entry => entry.Thread.Source).Distinct(ReferenceEqualityComparer.Instance));
    }

    [Fact]
    public async Task AStringValueLongerThanACacheMayHoldIsADamagedDocumentAndNeverApplied()
    {
        using TempDirectory temp = new();
        string path = Path.Combine(temp.Path, MediaLibraryIndexCache.FileName);
        MediaLibrarySnapshot synthetic = MediaLibraryIndexBenchmark.Synthetic(10);
        MediaLibraryIndexCacheSync.Save(path, synthetic);
        string document = await File.ReadAllTextAsync(path);
        string title = synthetic.Entries[0].ThreadTitle;
        int max = MediaLibraryIndexCache.MaximumStringChars;

        // Exactly the maximum still loads, with the title unchanged except for its filler.
        await File.WriteAllTextAsync(path, document.Replace("\"Title\":\"" + title, "\"Title\":\"" + new string('x', max - title.Length) + title));
        MediaLibrarySnapshot loaded = MediaLibraryIndexCacheSync.Load(path)!;
        Assert.NotNull(loaded);
        Assert.Equal(max, loaded.Entries[0].ThreadTitle.Length);

        // One char more in any string field makes the whole document damaged: nothing is applied partially.
        foreach (string field in new[] { "Title", "Dir", "Key", "Name", "Var", "Asset", "Mid", "D" })
        {
            string needle = "\"" + field + "\":\"";
            Assert.Contains(needle, document);
            int at = document.IndexOf(needle, StringComparison.Ordinal);
            string longer = document.Insert(at + needle.Length, new string('x', max + 1));
            await File.WriteAllTextAsync(path, longer);
            Assert.Null(MediaLibraryIndexCacheSync.Load(path));
            // Even escaped (six bytes per char) and far over the cap it is refused, not read.
            await File.WriteAllTextAsync(path, document.Insert(at + needle.Length, string.Concat(Enumerable.Repeat("\\u00e4", max * 2))));
            Assert.Null(MediaLibraryIndexCacheSync.Load(path));
        }
        // The valid document still loads, so the failures above are the length alone.
        await File.WriteAllTextAsync(path, document);
        Assert.NotNull(MediaLibraryIndexCacheSync.Load(path));
    }

    [Fact]
    public async Task TheWriterKeepsTheSameMaximumSoEveryWrittenIndexLoadsAndALongerValueIsNotSaved()
    {
        using TempDirectory temp = new();
        string path = Path.Combine(temp.Path, "cache", MediaLibraryIndexCache.FileName);
        MediaLibrarySnapshot synthetic = MediaLibraryIndexBenchmark.Synthetic(10);
        int max = MediaLibraryIndexCache.MaximumStringChars;
        MediaLibrarySnapshot With(string title, string? name = null) => new(
            synthetic.Entries.Select((entry, index) => index == 0 ? entry with { ThreadTitle = title, Name = name ?? entry.Name } : entry).ToArray(),
            synthetic.Threads, Array.Empty<string>(), complete: true);

        // The longest value in its widest JSON form (every char escaped) is written and read back unchanged.
        foreach (string longest in new[] { new string('t', max), new string('\u732b', max), string.Concat(Enumerable.Repeat("\U0001F408", max / 2)) })
        {
            MediaLibrarySnapshot snapshot = With(longest, longest);
            Assert.Equal(MediaLibraryIndexCacheWriteStatus.Written, (await MediaLibraryIndexCache.SaveAsync(path, snapshot, MediaLibraryIndexCache.MaximumBytes)).Status);
            MediaLibrarySnapshot? loaded = MediaLibraryIndexCacheSync.Load(path);
            Assert.NotNull(loaded);
            Assert.Equal(snapshot.Entries, loaded!.Entries);
        }

        // A longer value is not saved: no file is created and an existing cache stays as it was.
        string other = Path.Combine(temp.Path, "none", MediaLibraryIndexCache.FileName);
        Assert.Equal(MediaLibraryIndexCacheWriteStatus.TooLarge,
            (await MediaLibraryIndexCache.SaveAsync(other, With(new string('t', max + 1)), MediaLibraryIndexCache.MaximumBytes)).Status);
        Assert.False(File.Exists(other));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(other)!));
        byte[] before = await File.ReadAllBytesAsync(path);
        Assert.Equal(MediaLibraryIndexCacheWriteStatus.TooLarge,
            (await MediaLibraryIndexCache.SaveAsync(path, With("ok", new string('n', max + 1)), MediaLibraryIndexCache.MaximumBytes)).Status);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task AnUnchangedIndexIsNotWrittenAgainButAReplacedOrRemovedFileIs()
    {
        using TempDirectory temp = new();
        await SeedAsync(temp.Path);
        MediaLibrarySnapshot built = await PlainAsync(temp.Path);
        string path = Path.Combine(temp.Path, "cache", MediaLibraryIndexCache.FileName);
        MediaLibraryIndexCacheFile store = new(path);

        Assert.Equal(MediaLibraryIndexCacheWriteStatus.Written, (await store.SaveAsync(built, MediaLibraryIndexCache.DefaultLimitBytes)).Status);
        RegularProfileFileIdentity? identity = RegularProfileFile.TryGetIdentity(path);
        // The next build gives an equal index (a new instance): no write.
        MediaLibrarySnapshot again = await PlainAsync(temp.Path, built);
        MediaLibraryIndexCacheWrite unchanged = await store.SaveAsync(again, MediaLibraryIndexCache.DefaultLimitBytes);
        Assert.Equal(MediaLibraryIndexCacheWriteStatus.Unchanged, unchanged.Status);
        Assert.Equal(new FileInfo(path).Length, unchanged.Bytes);
        Assert.Equal((1, 1), (store.Writes, store.SkippedWrites));
        Assert.Equal(identity, RegularProfileFile.TryGetIdentity(path));

        // A new page that loaded the file does not write it back either.
        MediaLibraryIndexCacheFile reopened = new(path);
        MediaLibrarySnapshot loaded = (await reopened.LoadAsync())!;
        Assert.Equal(MediaLibraryIndexCacheWriteStatus.Unchanged, (await reopened.SaveAsync(loaded, MediaLibraryIndexCache.DefaultLimitBytes)).Status);
        Assert.Equal(0, reopened.Writes);

        // Removed or replaced behind the page's back: written again.
        File.Delete(path);
        Assert.Equal(MediaLibraryIndexCacheWriteStatus.Written, (await store.SaveAsync(again, MediaLibraryIndexCache.DefaultLimitBytes)).Status);
        Assert.True(File.Exists(path));
        await WriteManifestAsync(Path.Combine(temp.Path, "media", "b"), B, new File_("copy.png", "x1", [9, 9], "200"));
        MediaLibrarySnapshot changed = await PlainAsync(temp.Path, again);
        Assert.Equal(MediaLibraryIndexCacheWriteStatus.Written, (await store.SaveAsync(changed, MediaLibraryIndexCache.DefaultLimitBytes)).Status);
        Assert.Equal(changed.Entries, MediaLibraryIndexCacheSync.Load(path)!.Entries);
        Assert.Equal(new[] { path }, Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public async Task TheEntryAndThreadLimitsAreSettingsThatOnlyStopTheSaveAndNeverMakeASavedIndexUnreadable()
    {
        Assert.Equal((1_000_000, 100_000, 5_000_000, 200_000, 20_000, 1_000_000), (MediaLibraryIndexCache.DefaultEntryLimit, MediaLibraryIndexCache.MinimumEntryLimit,
            MediaLibraryIndexCache.EntryCeiling, MediaLibraryIndexCache.DefaultThreadLimit, MediaLibraryIndexCache.MinimumThreadLimit, MediaLibraryIndexCache.ThreadCeiling));
        using TempDirectory temp = new();
        string path = Path.Combine(temp.Path, "cache", MediaLibraryIndexCache.FileName);
        MediaLibrarySnapshot index = MediaLibraryIndexBenchmark.Synthetic(2_000);
        MediaLibraryIndexCacheFile store = new(path);
        // Over a (test-sized) entry limit nothing is written, and the page is told it is the count that stopped the save.
        MediaLibraryIndexCacheWrite refused = await store.SaveAsync(index, MediaLibraryIndexCache.DefaultLimitBytes, 1_000, MediaLibraryIndexCache.DefaultThreadLimit);
        Assert.Equal(MediaLibraryIndexCacheWriteStatus.TooManyEntries, refused.Status);
        Assert.False(File.Exists(path));
        // The same refusal is remembered while the counts still exceed the limit, and not once the limit was raised.
        Assert.Same(refused, await store.SaveAsync(index, MediaLibraryIndexCache.DefaultLimitBytes, 1_000, MediaLibraryIndexCache.DefaultThreadLimit));
        Assert.Equal(MediaLibraryIndexCacheWriteStatus.TooManyEntries,
            (await store.SaveAsync(index, MediaLibraryIndexCache.DefaultLimitBytes, MediaLibraryIndexCache.DefaultEntryLimit, 1)).Status);
        Assert.Equal(MediaLibraryIndexCacheWriteStatus.Written, (await store.SaveAsync(index, MediaLibraryIndexCache.DefaultLimitBytes)).Status);
        // A saved index larger than a lowered limit stays readable: reads accept up to the technical ceilings.
        MediaLibrarySnapshot read = (await new MediaLibraryIndexCacheFile(path).LoadAsync())!;
        Assert.Equal(index.Entries, read.Entries);
        Assert.True(MediaLibraryIndexCache.ExceedsCountLimits(read, 1_000, MediaLibraryIndexCache.DefaultThreadLimit));
        Assert.False(MediaLibraryIndexCache.ExceedsCountLimits(read, MediaLibraryIndexCache.DefaultEntryLimit, MediaLibraryIndexCache.DefaultThreadLimit));
    }

    [Fact]
    public async Task TheWriteLimitStopsTheSaveSaysSoAndNeverMakesTheSavedIndexUnreadable()
    {
        using TempDirectory temp = new();
        string path = Path.Combine(temp.Path, "cache", MediaLibraryIndexCache.FileName);
        MediaLibrarySnapshot small = MediaLibraryIndexBenchmark.Synthetic(200), large = MediaLibraryIndexBenchmark.Synthetic(2_000);
        MediaLibraryIndexCacheFile store = new(path);
        MediaLibraryIndexCacheWrite first = await store.SaveAsync(small, MediaLibraryIndexCache.DefaultLimitBytes);
        Assert.Equal(MediaLibraryIndexCacheWriteStatus.Written, first.Status);
        Assert.False(first.NearOrOverLimit);
        long smallBytes = new FileInfo(path).Length;

        // Over the limit: nothing beyond it is written, the saved (smaller) index stays as it was and readable.
        long limit = smallBytes * 3;
        MediaLibraryIndexCacheWrite refused = await store.SaveAsync(large, limit);
        Assert.Equal(MediaLibraryIndexCacheWriteStatus.TooLarge, refused.Status);
        Assert.True(refused.NearOrOverLimit);
        Assert.InRange(refused.Bytes, limit + 1, limit + 1024 * 1024);
        Assert.Equal(small.Entries, MediaLibraryIndexCacheSync.Load(path)!.Entries);
        Assert.Equal(new[] { path }, Directory.GetFiles(Path.GetDirectoryName(path)!));
        // The same refused index is not written up to the limit on every visit.
        Assert.Same(refused, await store.SaveAsync(large, limit));

        // Near the limit: saved, and the page is told.
        MediaLibraryIndexCacheWrite near = await store.SaveAsync(large, MediaLibraryIndexCache.DefaultLimitBytes);
        long largeBytes = near.Bytes;
        near = await new MediaLibraryIndexCacheFile(path).SaveAsync(large, (long)(largeBytes / 0.8));
        Assert.True(near.NearOrOverLimit);

        // A lowered limit keeps the larger saved index readable; reading never depends on the setting.
        MediaLibraryIndexCacheFile lowered = new(path);
        MediaLibrarySnapshot read = (await lowered.LoadAsync())!;
        Assert.Equal(large.Entries, read.Entries);
        MediaLibraryIndexCacheWrite unchanged = await lowered.SaveAsync(read, MediaLibraryIndexCache.MinimumLimitBytes / 1024);
        Assert.Equal(MediaLibraryIndexCacheWriteStatus.Unchanged, unchanged.Status);
        Assert.True(unchanged.NearOrOverLimit);
        Assert.Equal(large.Entries, MediaLibraryIndexCacheSync.Load(path)!.Entries);
    }

    [Fact]
    public async Task LoadingAndBuildingStopWhenCancelled()
    {
        using TempDirectory temp = new();
        await SeedAsync(temp.Path);
        string path = Path.Combine(temp.Path, "cache", MediaLibraryIndexCache.FileName);
        MediaLibrarySnapshot built = await MediaLibraryIndexBuilder.BuildAsync(Reader(temp.Path), new MediaManifestCache());
        MediaLibraryIndexCacheSync.Save(path, built);
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MediaLibraryIndexCacheFile(path).LoadAsync(cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MediaLibraryIndexCache.LoadAsync(path, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MediaLibraryIndexBuilder.BuildAsync(Reader(temp.Path), new MediaManifestCache(), built,
            cancellationToken: cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MediaLibraryIndexCacheFile(path).SaveAsync(built, MediaLibraryIndexCache.DefaultLimitBytes, cancelled.Token));
        Assert.Equal(built.Entries, MediaLibraryIndexCacheSync.Load(path)!.Entries);
    }

    private sealed class ManualTime : TimeProvider
    {
        internal long Now;
        public override long GetTimestamp() => Now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    [Fact]
    public void ProgressIsForwardedAtMostOncePerIntervalPlusTheFirstAndTheLast()
    {
        ManualTime time = new();
        List<MediaLibraryProgress> forwarded = new();
        ThrottledProgress<MediaLibraryProgress> throttled = new(new Collect(forwarded), TimeSpan.FromMilliseconds(100),
            value => value.ThreadsDone >= value.ThreadsTotal, time);
        for (int done = 0; done <= 2_000; done++)
        {
            throttled.Report(new(done, 2_000, 0));
            time.Now += TimeSpan.FromMilliseconds(1).Ticks; // 2,000 threads in two seconds
        }
        Assert.Equal(new MediaLibraryProgress(0, 2_000, 0), forwarded[0]);
        Assert.Equal(new MediaLibraryProgress(2_000, 2_000, 0), forwarded[^1]);
        Assert.InRange(forwarded.Count, 20, 23); // about ten per second instead of 2,001
        Assert.Equal(forwarded.Count, throttled.Forwarded);
    }
}
