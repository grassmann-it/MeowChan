using MeowChan.Archive;
using MeowChan.Gallery;
using MeowChan.Storage;

namespace MeowChan.Tests;

public sealed class MediaLibraryAudioTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    // Streams the probe understands: a movie box with a sound track, one with a video track only, and plain junk.
    private static readonly byte[] WithSound = File.ReadAllBytes(Fixture("release102", "playback-h264-audio.mp4"));
    private static readonly byte[] Silent = File.ReadAllBytes(Fixture("release102", "playback-h264-silent.mp4"));
    private static readonly byte[] Junk = new byte[2000];

    private static string Fixture(params string[] parts)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root != null && !Directory.Exists(Path.Combine(root.FullName, "tests", "Fixtures"))) root = root.Parent;
        Assert.NotNull(root);
        return Path.Combine([root!.FullName, "tests", "Fixtures", .. parts]);
    }

    private static string Hash(char c) => new(c, 64);
    private static string Key(char c) => MediaAudioMap.KeyOf(Hash(c), 1);

    private static MediaLibraryEntry Entry(string thread, char hash, string name = "clip.mp4", SavedMediaKind kind = SavedMediaKind.Video, bool missing = false) =>
        new(new ResourceId("s", "b", thread), "T" + thread, 1, Day, Day, "1", 1, null, 0, "att-v1:" + Hash(hash),
            missing ? null : "/dir/" + thread, missing ? null : "asset-" + thread, missing ? null : Hash(hash), missing ? null : 1,
            "m" + thread, "original", name, kind, missing ? MediaLibraryPresence.Missing : MediaLibraryPresence.Present, false, false);

    private static MediaLibrarySnapshot Snapshot(bool complete, params MediaLibraryEntry[] entries) =>
        new(entries, Array.Empty<MediaLibraryThreadState>(), Array.Empty<string>(), complete);

    // ---- cache and map ----------------------------------------------------------------------------------

    [Fact]
    public void CacheRoundTripsAndAnyDamageMeansProbeAgain()
    {
        using TempDirectory temp = new();
        string path = Path.Combine(temp.Path, "cache", MediaAudioCache.FileName);
        MediaAudioMap map = MediaAudioMap.Empty.With([new(Key('a'), MediaAudio.HasSound), new(Key('b'), MediaAudio.Silent), new(Key('c'), MediaAudio.Unknown)]);

        Assert.True(MediaAudioCache.Save(path, map));
        MediaAudioMap? loaded = MediaAudioCache.Load(path);

        Assert.NotNull(loaded);
        Assert.Equal(MediaAudio.HasSound, loaded!.Get(Key('a')));
        Assert.Equal(MediaAudio.Silent, loaded.Get(Key('b')));
        // Undecided is remembered as probed, but still reads as unknown.
        Assert.Equal(MediaAudio.Unknown, loaded.Get(Key('c')));
        Assert.True(loaded.WasProbed(Key('c')));
        Assert.False(loaded.WasProbed(Key('d')));
        Assert.Equal(new[] { path }, Directory.GetFiles(Path.GetDirectoryName(path)!));

        string valid = File.ReadAllText(path);
        foreach (string damaged in new[] { "{bad", "", valid.Replace("\"Version\":1", "\"Version\":2"), valid.Replace("\"Entries\":[", "\"Extra\":1,\"Entries\":["),
                     valid.Replace(Hash('a'), "nothex"), valid.Replace("\"sound\"", "\"loud\""), valid.Replace("\"P\":1", "\"P\":0") })
        {
            File.WriteAllText(path, damaged);
            Assert.Null(MediaAudioCache.Load(path));
        }
        File.Delete(path);
        Assert.Null(MediaAudioCache.Load(path));
        // Saving again leaves no backups or temporary files.
        MediaAudioCache.Save(path, map);
        MediaAudioCache.Save(path, map);
        Assert.Equal(new[] { path }, Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public void AnswersOfAnotherProbeVersionAreIgnoredSoTheFileIsProbedAgain()
    {
        using TempDirectory temp = new();
        string path = Path.Combine(temp.Path, MediaAudioCache.FileName);
        MediaAudioCache.Save(path, MediaAudioMap.Empty.With([new(Key('a'), MediaAudio.Silent)]));
        // The cache was written by a parser of another version.
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"P\":" + MediaAudioProbe.Version, "\"P\":" + (MediaAudioProbe.Version + 1)));

        MediaAudioMap? loaded = MediaAudioCache.Load(path);
        Assert.NotNull(loaded);
        Assert.False(loaded!.WasProbed(Key('a')));

        int opened = 0;
        MediaAudioMap rescanned = MediaLibraryAudioScanner.Scan(Snapshot(true, Entry("1", 'a')), loaded, _ => { opened++; return new MemoryStream(WithSound); });
        Assert.Equal(1, opened);
        Assert.Equal(MediaAudio.HasSound, rescanned.Get(Key('a')));
    }

    // ---- scanner ----------------------------------------------------------------------------------------

    [Fact]
    public void ScanProbesEachVideoOnceByHashAndSkipsEverythingThatIsNotASavedVideo()
    {
        using TempDirectory temp = new();
        string path = Path.Combine(temp.Path, MediaAudioCache.FileName);
        MediaLibraryEntry sound1 = Entry("1", 'a'), sound2 = Entry("2", 'a'), silent = Entry("3", 'b'), junk = Entry("4", 'c', "x.mkv");
        MediaLibraryEntry image = Entry("5", 'd', "p.png", SavedMediaKind.Image), missing = Entry("6", 'e', missing: true);
        MediaLibrarySnapshot snapshot = Snapshot(true, sound1, sound2, silent, junk, image, missing);
        Dictionary<string, byte[]> files = new() { [Hash('a')] = WithSound, [Hash('b')] = Silent, [Hash('c')] = Junk, [Hash('d')] = Junk };
        List<string> opened = new();
        List<MediaAudioScanProgress> progress = new();

        MediaAudioMap result = MediaLibraryAudioScanner.Scan(snapshot, MediaAudioMap.Empty, entry => { opened.Add(entry.Thread.Thread!); return new MemoryStream(files[entry.Sha256!]); },
            (_, value) => progress.Add(value), path);

        Assert.Equal(new[] { "1", "3", "4" }, opened); // thread 2 has the same bytes as 1; image and missing file are not videos to probe
        Assert.Equal(MediaAudio.HasSound, result.Get(Key('a')));
        Assert.Equal(MediaAudio.Silent, result.Get(Key('b')));
        Assert.Equal(MediaAudio.Unknown, result.Get(Key('c')));
        Assert.True(result.WasProbed(Key('c')));
        Assert.False(result.WasProbed(Key('d')));
        Assert.False(result.WasProbed(Key('e')));
        Assert.Equal(new MediaAudioScanProgress(0, 3), progress.First());
        Assert.Equal(new MediaAudioScanProgress(3, 3), progress.Last());

        // A later scan, also after a restart from the cache file, has nothing left to do.
        MediaAudioMap restored = MediaAudioCache.Load(path)!;
        MediaLibraryAudioScanner.Scan(snapshot, restored, _ => throw new InvalidOperationException("Nothing should be opened."));
        Assert.Equal(MediaAudio.Silent, restored.Get(Key('b')));
    }

    [Fact]
    public void AnUnavailableFileStaysUnknownIsNotRecordedAndTheNextOccurrenceIsTried()
    {
        MediaLibraryEntry first = Entry("1", 'a'), second = Entry("2", 'a'), gone = Entry("3", 'b'), broken = Entry("4", 'c');
        MediaLibrarySnapshot snapshot = Snapshot(true, first, second, gone, broken);
        List<string> opened = new();

        MediaAudioMap result = MediaLibraryAudioScanner.Scan(snapshot, MediaAudioMap.Empty, entry =>
        {
            opened.Add(entry.Thread.Thread!);
            if (entry.Thread.Thread == "1" || entry.Thread.Thread == "3") return null; // file changed or missing
            if (entry.Thread.Thread == "4") throw new IOException("unreadable");
            return new MemoryStream(WithSound);
        });

        Assert.Equal(new[] { "1", "2", "3", "4" }, opened);
        Assert.Equal(MediaAudio.HasSound, result.Get(Key('a')));
        Assert.False(result.WasProbed(Key('b')));
        Assert.False(result.WasProbed(Key('c')));
        Assert.Equal(MediaAudio.Unknown, result.Get(Key('b')));
    }

    [Fact]
    public void CancellingKeepsWhatWasLearnedAndThrows()
    {
        using TempDirectory temp = new();
        string path = Path.Combine(temp.Path, MediaAudioCache.FileName);
        MediaLibrarySnapshot snapshot = Snapshot(true, Entry("1", 'a'), Entry("2", 'b'), Entry("3", 'c'), Entry("4", 'd'));
        using CancellationTokenSource cancel = new();
        int opened = 0;

        Assert.ThrowsAny<OperationCanceledException>(() => MediaLibraryAudioScanner.Scan(snapshot, MediaAudioMap.Empty,
            _ => { if (++opened == 2) cancel.Cancel(); return new MemoryStream(Silent); }, null, path, cancel.Token));

        Assert.Equal(2, opened);
        MediaAudioMap saved = MediaAudioCache.Load(path)!;
        Assert.Equal(2, saved.Count);
        Assert.Equal(MediaAudio.Silent, saved.Get(Key('a')));
    }

    [Fact]
    public void BytesAreIdentifiedLikeTheTilesDoIgnoringHashCaseAndKeepingTheLength()
    {
        MediaLibraryEntry lower = Entry("1", 'a'), upper = lower with { Sha256 = Hash('a').ToUpperInvariant() }, longer = lower with { Length = 2 };
        Assert.Equal(MediaAudioMap.KeyOf(lower), MediaAudioMap.KeyOf(upper));
        Assert.NotEqual(MediaAudioMap.KeyOf(lower), MediaAudioMap.KeyOf(longer));
        Assert.Null(MediaAudioMap.KeyOf(lower with { Length = null }));
        Assert.Null(MediaAudioMap.KeyOf(Entry("2", 'a', missing: true)));

        MediaAudioMap audio = MediaAudioMap.Empty.With([new(MediaAudioMap.KeyOf(lower)!, MediaAudio.HasSound)]);
        Assert.Equal(MediaAudio.HasSound, audio.Get(upper));
        Assert.Equal(MediaAudio.Unknown, audio.Get(longer)); // same hash, other length: other bytes, not judged by this answer
        // Saved and loaded again the key survives.
        using TempDirectory temp = new();
        string path = Path.Combine(temp.Path, MediaAudioCache.FileName);
        MediaAudioCache.Save(path, audio);
        Assert.Equal(MediaAudio.HasSound, MediaAudioCache.Load(path)!.Get(upper));
        // One video with an upper-case hash is probed once together with its lower-case twin.
        int opened = 0;
        MediaLibraryAudioScanner.Scan(Snapshot(true, lower, upper with { Thread = new ResourceId("s", "b", "9") }), MediaAudioMap.Empty,
            _ => { opened++; return new MemoryStream(Silent); });
        Assert.Equal(1, opened);
    }

    [Fact]
    public void ResultsOfMediaThatLeftACompleteIndexAreDroppedButNotFromAnIncompleteOne()
    {
        MediaAudioMap known = MediaAudioMap.Empty.With([new(Key('a'), MediaAudio.Silent), new(Key('z'), MediaAudio.HasSound)]);
        MediaLibraryEntry only = Entry("1", 'a');
        Func<MediaLibraryEntry, Stream?> none = _ => throw new InvalidOperationException();

        Assert.False(MediaLibraryAudioScanner.Scan(Snapshot(true, only), known, none).WasProbed(Key('z')));
        Assert.True(MediaLibraryAudioScanner.Scan(Snapshot(false, only), known, none).WasProbed(Key('z')));
    }

    [Fact]
    public async Task OpenForProbeOnlyOpensTheFileTheIndexRecorded()
    {
        using TempDirectory temp = new();
        string file = Path.Combine(temp.Path, "clip.mp4");
        await File.WriteAllBytesAsync(file, Silent);
        MediaAssetReference Asset(long length, string sha) => new("a", "m", "original", "clip.mp4", file, length, sha, "https://offline.invalid/clip.mp4", "clip.mp4", true);

        using (FileStream stream = MediaLibraryAudioScanner.OpenForProbe(Asset(Silent.Length, Hash('A')), Hash('a'))!)
            Assert.Equal(MediaAudio.Silent, MediaAudioProbe.Probe(stream));
        Assert.Null(MediaLibraryAudioScanner.OpenForProbe(Asset(Silent.Length, Hash('a')), Hash('b'))); // manifest hash is not the indexed one
        Assert.Null(MediaLibraryAudioScanner.OpenForProbe(Asset(Silent.Length + 1, Hash('a')), Hash('a'))); // file has another length
        Assert.Null(MediaLibraryAudioScanner.OpenForProbe(Asset(1, Hash('a')) with { Path = Path.Combine(temp.Path, "gone.mp4") }, Hash('a')));
        Assert.Null(MediaLibraryAudioScanner.OpenForProbe(null, Hash('a')));
    }

    // ---- filter ----------------------------------------------------------------------------------------

    private static string[] Threads(IEnumerable<MediaLibraryEntry> entries) => entries.Select(entry => entry.Thread.Thread!).ToArray();

    [Fact]
    public void SoundFilterShowsOnlyKnownVideosAndCountsTheUnknownOnesInsteadOfCallingThemSilent()
    {
        MediaLibraryEntry loud = Entry("1", 'a'), quiet = Entry("2", 'b'), undecided = Entry("3", 'c'), unprobed = Entry("4", 'd'),
            image = Entry("5", 'e', "p.png", SavedMediaKind.Image), gif = Entry("6", 'f', "g.gif", SavedMediaKind.Image), missing = Entry("7", 'g', missing: true);
        MediaLibraryEntry[] all = [loud, quiet, undecided, unprobed, image, gif, missing];
        MediaAudioMap audio = MediaAudioMap.Empty.With([new(Key('a'), MediaAudio.HasSound), new(Key('b'), MediaAudio.Silent), new(Key('c'), MediaAudio.Unknown)]);
        MediaLibraryUserData data = MediaLibraryUserData.Unavailable;

        MediaLibraryFilterResult with = MediaLibraryFilterEngine.Evaluate(all, new(sound: MediaLibrarySoundFilter.WithSound), data, audio);
        MediaLibraryFilterResult without = MediaLibraryFilterEngine.Evaluate(all, new(sound: MediaLibrarySoundFilter.WithoutSound), data, audio);

        Assert.Equal(new[] { "1" }, Threads(with.Passing));
        Assert.Equal(new[] { "2" }, Threads(without.Passing));
        // Undecided and not yet probed are in neither answer, and are counted; images, GIFs and unsaved videos are not videos to judge.
        Assert.Equal(2, with.UnknownSoundVideos);
        Assert.Equal(2, without.UnknownSoundVideos);
        // Without a sound filter the type filter and everything else stay as they were, and nothing is counted.
        MediaLibraryFilterResult videos = MediaLibraryFilterEngine.Evaluate(all, new(kind: MediaLibraryKindFilter.Video), data, audio);
        Assert.Equal(new[] { "1", "2", "3", "4", "7" }, Threads(videos.Passing));
        Assert.Equal(0, videos.UnknownSoundVideos);
        // Without any audio results every video is unknown.
        MediaLibraryFilterResult none = MediaLibraryFilterEngine.Evaluate(all, new(sound: MediaLibrarySoundFilter.WithSound), data);
        Assert.Empty(none.Passing);
        Assert.Equal(4, none.UnknownSoundVideos);
        Assert.Equal(new[] { "1" }, Threads(MediaLibraryFilterEngine.Apply(all, new(sound: MediaLibrarySoundFilter.WithSound), data, audio)));
    }

    [Fact]
    public void SoundCombinesWithTheOtherFiltersAndImpliesVideos()
    {
        MediaLibraryEntry loudInA = Entry("1", 'a', "cat.webm"), loudInB = Entry("2", 'b', "dog.mp4"), unknownInA = Entry("1", 'c', "new.mp4");
        loudInA = loudInA with { Thread = new ResourceId("s", "b", "1"), PostId = "1" };
        unknownInA = unknownInA with { PostId = "2", AssetId = "asset-x" };
        MediaAudioMap audio = MediaAudioMap.Empty.With([new(Key('a'), MediaAudio.HasSound), new(Key('b'), MediaAudio.HasSound)]);
        MediaLibraryUserData data = MediaLibraryUserData.Unavailable;

        MediaLibraryFilterResult result = MediaLibraryFilterEngine.Evaluate([loudInA, loudInB, unknownInA],
            new(nameContains: "cat", sound: MediaLibrarySoundFilter.WithSound), data, audio);
        Assert.Equal(new[] { "cat.webm" }, result.Passing.Select(entry => entry.Name));
        Assert.Equal(0, result.UnknownSoundVideos); // an unknown video that fails the name filter anyway is not "unchecked"

        MediaLibraryFilterResult thread = MediaLibraryFilterEngine.Evaluate([loudInA, loudInB, unknownInA],
            new(thread: loudInA.Thread, sound: MediaLibrarySoundFilter.WithSound), data, audio);
        Assert.Equal(new[] { "cat.webm" }, thread.Passing.Select(entry => entry.Name));
        Assert.Equal(1, thread.UnknownSoundVideos);

        Assert.Throws<ArgumentException>(() => new MediaLibraryFilter(kind: MediaLibraryKindFilter.Image, sound: MediaLibrarySoundFilter.WithSound));
        Assert.Throws<ArgumentException>(() => new MediaLibraryFilter(kind: MediaLibraryKindFilter.Gif, sound: MediaLibrarySoundFilter.WithoutSound));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MediaLibraryFilter(sound: (MediaLibrarySoundFilter)9));
        Assert.False(new MediaLibraryFilter(kind: MediaLibraryKindFilter.Video, sound: MediaLibrarySoundFilter.WithSound).IsEmpty);
        Assert.False(new MediaLibraryFilter(sound: MediaLibrarySoundFilter.WithoutSound).IsEmpty);
        Assert.True(new MediaLibraryFilter(sound: MediaLibrarySoundFilter.Any).IsEmpty);
    }

    [Fact]
    public void ATileMatchesTheSoundFilterWhenAnyOccurrenceDoesAndTheyShareTheAnswer()
    {
        MediaLibraryEntry in1 = Entry("1", 'a', "one.mp4"), in2 = Entry("2", 'a', "two.mp4"), other = Entry("3", 'b');
        MediaLibraryEntry[] all = [in1, in2, other];
        MediaAudioMap audio = MediaAudioMap.Empty.With([new(Key('a'), MediaAudio.HasSound), new(Key('b'), MediaAudio.Silent)]);

        IReadOnlyList<MediaLibraryEntry> passing = MediaLibraryFilterEngine.Apply(all, new(sound: MediaLibrarySoundFilter.WithSound), MediaLibraryUserData.Unavailable, audio);
        IReadOnlyList<MediaLibraryTile> tiles = MediaLibraryGrouping.Build(all, passing, MediaLibrarySort.ByThread, includeMissing: false);

        Assert.Equal(2, passing.Count); // both occurrences share the bytes, so the same answer
        MediaLibraryTile tile = Assert.Single(tiles);
        Assert.Equal(2, tile.Occurrences.Count);
    }
}
