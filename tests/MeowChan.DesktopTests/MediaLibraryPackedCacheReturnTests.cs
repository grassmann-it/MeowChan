using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Gallery;
using MeowChan.Persistence;
using MeowChan.Storage;
using Xunit.Abstractions;

namespace MeowChan.DesktopTests;

public sealed class MediaLibraryPackedCacheReturnTests(ITestOutputHelper output)
{
    private const int PerThread = 100;
    private const long Budget = 256L * 1024 * 1024;

    [Legacy113WriterFact]
    public async Task HalfMillionOccurrencesFitTheStandardBudgetAndReopenWithoutWriting()
    {
        using TempDirectory temp = new();
        string path = Path.Combine(temp.Path, "packed", MediaLibraryIndexCache.FileName);
        MediaLibrarySnapshot fixture = Synthetic(500_000);
        await MeasureLegacyRefusalsAsync(temp.Path, fixture, Budget);
        MediaLibraryIndexCacheFile writer = new(path);
        var watch = Stopwatch.StartNew();
        MediaLibraryIndexCacheWrite result = await writer.SaveAsync(fixture, Budget);
        output.WriteLine("Packed save {0:F1} ms, {1:F2} MiB", watch.Elapsed.TotalMilliseconds, result.Bytes / 1048576.0);
        Assert.Equal(MediaLibraryIndexCacheWriteStatus.Written, result.Status);
        Assert.False(File.Exists(path));
        Assert.Equal(result.Bytes, new FileInfo(path + ".gz").Length);
        Assert.InRange(result.Bytes, 1, 128L * 1024 * 1024);
        RegularProfileFileIdentity? identity = RegularProfileFile.TryGetIdentity(path + ".gz");

        double[] warm = new double[5];
        for (int run = 0; run <= warm.Length; run++)
        {
            MediaLibraryIndexCacheFile reopened = new(path);
            watch.Restart();
            MediaLibrarySnapshot read = Assert.IsType<MediaLibrarySnapshot>(await reopened.LoadAsync());
            double elapsed = watch.Elapsed.TotalMilliseconds;
            if (run == 0) output.WriteLine("First packed load {0:F1} ms", elapsed);
            else warm[run - 1] = elapsed;
            Assert.True(MediaLibrarySnapshot.SameContent(fixture, read));
            Assert.Equal(MediaLibraryIndexCacheWriteStatus.Unchanged, (await reopened.SaveAsync(read, Budget)).Status);
            Assert.Equal((0, 1), (reopened.Writes, reopened.SkippedWrites));
            Assert.Equal(identity, RegularProfileFile.TryGetIdentity(path + ".gz"));
        }
        output.WriteLine("Warm packed loads (ms) {0}, median {1:F1}", string.Join(", ", warm), warm.Order().ElementAt(2));
    }

    private async Task MeasureLegacyRefusalsAsync(string directory, MediaLibrarySnapshot fixture, long budget)
    {
        using Legacy113ProfileWriter historical = new();
        Assembly library = historical.Assemblies.Single(assembly => assembly.GetName().Name == "libmeowchan");
        Type cache = library.GetType("MeowChan.Gallery.MediaLibraryIndexCache", throwOnError: true)!;
        string uncompressed = Path.Combine(directory, "fixture", MediaLibraryIndexCache.FileName);
        Assert.Equal(MediaLibraryIndexCacheWriteStatus.Written,
            (await MediaLibraryIndexCache.SaveAsync(uncompressed, fixture, MediaLibraryIndexCache.MaximumBytes)).Status);
        string refused = Path.Combine(directory, "legacy", MediaLibraryIndexCache.FileName);
        Task load = (Task)Invoke(cache, null, "LoadAsync", uncompressed, CancellationToken.None)!;
        await load;
        object oldSnapshot = Property(Property(load, "Result")!, "Snapshot")!;
        double[] warm = new double[5];
        for (int run = 0; run <= warm.Length; run++)
        {
            var watch = Stopwatch.StartNew();
            Task save = (Task)Invoke(cache, null, "SaveAsync", refused, oldSnapshot, budget, CancellationToken.None)!;
            await save;
            double elapsed = watch.Elapsed.TotalMilliseconds;
            Assert.Equal("TooLarge", Property(Property(save, "Result")!, "Status")?.ToString());
            Assert.False(File.Exists(refused));
            if (run == 0) output.WriteLine("First legacy refusal {0:F1} ms", elapsed);
            else warm[run - 1] = elapsed;
        }
        output.WriteLine("Warm legacy refusals (ms) {0}, median {1:F1}", string.Join(", ", warm), warm.Order().ElementAt(2));
    }

    private static string Hex(int seed, int salt) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        BitConverter.GetBytes(((long)seed << 20) ^ salt))).ToLowerInvariant();

    // Same deterministic 500k inputs as MediaLibraryIndexBenchmark.Synthetic in the core test project.
    private static MediaLibrarySnapshot Synthetic(int size)
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

    [Legacy113WriterFact]
    public async Task ReleasedWriterReopensAndEditsTheRestoredProfileAlongsideAnIgnoredPackedCache()
    {
        using TempDirectory root = new();
        using Legacy113ProfileWriter historical = new();
        Assembly library = historical.Assemblies.Single(assembly => assembly.GetName().Name == "libmeowchan");
        string source = Path.Combine(root.Path, "original");
        string downloads = Path.Combine(source, "downloads");
        const string name = "original.png";
        byte[] bytes = BooruUiFixtures.Png(5);
        await historical.WriteAsync(source, downloads, name);
        Directory.CreateDirectory(downloads);
        await File.WriteAllBytesAsync(Path.Combine(downloads, name), bytes);
        await historical.WriteTabsAsync(source, draft: false);
        Guid searchId = Guid.NewGuid();
        WriteSearch(library, source, searchId);

        string working;
        string packed;
        byte[] packedBytes;
        await using (DesktopSessionHost host = new(source, providers: [], admitProfileUpgrade: true))
        {
            await host.StartAsync();
            Assert.Equal(DesktopSessionState.Ready, host.State);
            working = host.ProfileDirectory;
            Assert.NotEqual(source, working);
            var search = Assert.Single((await host.LoadSavedArchiveSearchesAsync()).Searches);
            Assert.Equal(searchId, search.Id);
            Assert.Equal("fixture phrase", search.Definition?.Text);

            string path = Path.Combine(working, "cache", MediaLibraryIndexCache.FileName);
            packed = Path.Combine(working, "cache", MediaLibraryIndexCache.PackedFileName);
            Task<MediaLibrarySnapshot> Build(MediaLibrarySnapshot? previous = null) =>
                MediaLibraryIndexBuilder.BuildAsync(host.OpenArchiveReaderAsync, new MediaManifestCache(), previous, readBooruCatalog: host.ReadBooruCatalog);
            MediaLibrarySnapshot cold = await Build();
            Assert.NotEmpty(cold.Entries);
            MediaLibraryIndexCacheWrite plain = await MediaLibraryIndexCache.SaveAsync(path, cold, MediaLibraryIndexCache.DefaultLimitBytes);
            File.Delete(path);
            MediaLibraryIndexCacheFile store = new(path);
            Assert.Equal(MediaLibraryIndexCacheWriteStatus.Written, (await store.SaveAsync(cold, plain.Bytes - 1)).Status);
            Assert.True(File.Exists(packed));
            Assert.False(File.Exists(path));
            MediaLibrarySnapshot loaded = Assert.IsType<MediaLibrarySnapshot>(await store.LoadAsync());
            Assert.True(MediaLibrarySnapshot.SameContent(cold, loaded));
            Assert.True(MediaLibrarySnapshot.SameContent(cold, await Build(loaded)));
            packedBytes = await File.ReadAllBytesAsync(packed);
            File.Delete(packed);
            Assert.Null(await store.LoadAsync());
            Assert.True(MediaLibrarySnapshot.SameContent(cold, await Build()));
            Assert.Equal(MediaLibraryIndexCacheWriteStatus.Written, (await store.SaveAsync(cold, plain.Bytes - 1)).Status);
        }

        string backup = Assert.IsType<string>(ProfileUpgrade.SnapshotFor(working));
        string restored = Path.Combine(root.Path, "restored");
        ProfileSnapshot.Read(backup).Restore(backup, restored);
        string restoredDownloads = Path.Combine(restored, "downloads");
        Assert.Equal(SHA256.HashData(bytes), SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(restoredDownloads, name))));

        Type legacyCache = library.GetType("MeowChan.Gallery.MediaLibraryIndexCache", throwOnError: true)!;
        string legacyName = Assert.IsType<string>(legacyCache.GetField("FileName", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue());
        string restoredCache = Directory.CreateDirectory(Path.Combine(restored, "cache")).FullName;
        string legacyPath = Path.Combine(restoredCache, legacyName);
        string ignored = Path.Combine(restoredCache, MediaLibraryIndexCache.PackedFileName);
        await File.WriteAllBytesAsync(ignored, packedBytes);
        Assert.NotNull(await new MediaLibraryIndexCacheFile(legacyPath).LoadAsync());
        Task legacyLoad = (Task)Invoke(legacyCache, null, "LoadAsync", legacyPath, CancellationToken.None)!;
        await legacyLoad;
        Assert.Null(Property(legacyLoad, "Result"));

        historical.CheckAndEdit(restored, restoredDownloads, name, edit: true);
        historical.CheckAndEdit(restored, restoredDownloads, name, edit: false);
        CheckSearch(library, restored, searchId, edit: true);
        CheckSearch(library, restored, searchId, edit: false);
        Assert.Equal(SHA256.HashData(bytes), SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(restoredDownloads, name))));
        Assert.Equal(packedBytes, await File.ReadAllBytesAsync(ignored));
        Assert.True(File.Exists(packed));
    }

    private static void WriteSearch(Assembly library, string profile, Guid id)
    {
        using IDisposable lease = OpenLegacyLease(library, profile);
        Type storeType = library.GetType("MeowChan.Persistence.SavedArchiveSearchStore", throwOnError: true)!;
        object store = Activator.CreateInstance(storeType, profile, lease)!;
        Type definitionType = library.GetType("MeowChan.Archive.ArchiveSearchSpec", throwOnError: true)!;
        ConstructorInfo constructor = Assert.Single(definitionType.GetConstructors());
        object definition = constructor.Invoke(constructor.GetParameters()
            .Select(parameter => parameter.Name == "text" ? "fixture phrase" : parameter.DefaultValue).ToArray());
        Invoke(storeType, store, "Create", id, "Oracle", definition, 0L, CancellationToken.None);
    }

    private static void CheckSearch(Assembly library, string profile, Guid id, bool edit)
    {
        using IDisposable lease = OpenLegacyLease(library, profile);
        Type storeType = library.GetType("MeowChan.Persistence.SavedArchiveSearchStore", throwOnError: true)!;
        object store = Activator.CreateInstance(storeType, profile, lease)!;
        object snapshot = Invoke(storeType, store, "Load")!;
        object search = Assert.Single(Assert.IsAssignableFrom<IEnumerable>(Property(snapshot, "Searches")).Cast<object>());
        Assert.Equal(id, Property(search, "Id"));
        Assert.Equal(edit ? "Oracle" : "Renamed oracle", Property(search, "Name"));
        Assert.Equal("fixture phrase", Property(Property(search, "Definition")!, "Text"));
        if (edit) Invoke(storeType, store, "Rename", id, "Renamed oracle", Property(snapshot, "Revision"), CancellationToken.None);
    }

    private static IDisposable OpenLegacyLease(Assembly library, string profile)
    {
        Type leaseType = library.GetType("MeowChan.Persistence.ProfileLease", throwOnError: true)!;
        IDisposable lease = (IDisposable)Invoke(leaseType, null, "Acquire", profile)!;
        object admission = Property(lease, "Admission")!;
        Invoke(admission.GetType(), admission, "CompleteStartup");
        return lease;
    }

    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static object? Invoke(Type type, object? instance, string name, params object?[] args) =>
        type.GetMethods(Members).Single(method => method.Name == name && method.GetParameters().Length == args.Length).Invoke(instance, args);

    private static object? Property(object instance, string name) => instance.GetType().GetProperty(name, Members)!.GetValue(instance);
}
