using System.Text;
using System.Text.Json.Nodes;
using MeowChan.Gallery;
using MeowChan.Persistence;
using Xunit.Abstractions;

namespace MeowChan.Tests.PersistenceCorruption;

/// <summary>
/// media-index-v1.json is a disposable cache: any damage means "no cache" (the caller rebuilds) and the next save replaces it.
/// Version 4 is written, version 3 is still read, and a document that mixes the two is no cache.
/// </summary>
internal sealed class MediaIndexCacheAdapter : StoreAdapter
{
    private static readonly Lazy<MediaLibrarySnapshot> Snapshot = new(() =>
    {
        // The index is built once from a throw-away profile; the snapshot itself no longer needs it.
        using TempDirectory temp = new();
        MediaLibraryIndexTests.SeedAsync(temp.Path).GetAwaiter().GetResult();
        return MediaLibraryIndexTests.BuildAsync(temp.Path).GetAwaiter().GetResult();
    });

    internal static MediaLibrarySnapshot Good => Snapshot.Value;

    internal override string Name => "media-index-cache";
    internal override string PrimaryName => MediaLibraryIndexCache.FileName;
    internal override string VersionProperty => "Version";
    internal override long ReadCeilingBytes => MediaLibraryIndexCache.MaximumBytes;
    internal override bool HasBackup => false;
    internal override StorePolicy Policy => StorePolicy.Rebuild;
    // A rebuildable cache of public media metadata: a failed save reports the plain I/O error (with its path) to the log,
    // and nothing of it is shown to the user.
    internal override bool HidesPaths => false;
    // Documented in MediaLibraryIndexCache.Properties: as with the serializer before, a repeated member keeps its last value.
    internal override bool ToleratesChangedData(string variant) => variant == "duplicate-array-property-different-value";

    internal override void Seed(string directory, ProfileLease lease)
    {
        MediaLibraryIndexCacheWrite written = new MediaLibraryIndexCacheFile(PrimaryPath(directory))
            .SaveAsync(Good, MediaLibraryIndexCache.DefaultLimitBytes).GetAwaiter().GetResult();
        Assert.Equal(MediaLibraryIndexCacheWriteStatus.Written, written.Status);
    }

    internal static string Fingerprint(MediaLibrarySnapshot snapshot) =>
        snapshot.Threads.Count + ":" + string.Join("|", snapshot.Entries.Select(entry => entry.Thread + "/" + entry.PostId + "/" + entry.Position + "/" + entry.AssetId + "/" + entry.Name).Order());

    internal override ReadResult Read(string directory, ProfileLease lease)
    {
        try
        {
            MediaLibrarySnapshot? loaded = new MediaLibraryIndexCacheFile(PrimaryPath(directory)).LoadAsync().GetAwaiter().GetResult();
            return loaded == null ? ReadResult.Unusable() : ReadResult.Of(Fingerprint(loaded));
        }
        catch (Exception error) { return ReadResult.Unusable(error); }
    }

    internal override void Mutate(string directory, ProfileLease lease) =>
        new MediaLibraryIndexCacheFile(PrimaryPath(directory)).SaveAsync(Good, MediaLibraryIndexCache.DefaultLimitBytes).GetAwaiter().GetResult();
}

public sealed class MediaIndexCacheCorruptionTests(ITestOutputHelper log) : StoreCorruptionTests(log)
{
    internal override StoreAdapter Adapter { get; } = new MediaIndexCacheAdapter();

    private static byte[] WrittenV4(string directory)
    {
        MediaIndexCacheAdapter adapter = new();
        Directory.CreateDirectory(directory);
        adapter.Seed(directory, null!);
        return File.ReadAllBytes(adapter.PrimaryPath(directory));
    }

    private static MediaLibrarySnapshot? Load(string directory) =>
        new MediaLibraryIndexCacheFile(Path.Combine(directory, MediaLibraryIndexCache.FileName)).LoadAsync().GetAwaiter().GetResult();

    private static void Rewrite(string directory, Action<JsonObject> change)
    {
        string path = Path.Combine(directory, MediaLibraryIndexCache.FileName);
        JsonObject root = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        change(root);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(root.ToJsonString()));
    }

    /// <summary>What version 3 wrote: no manifest identities, no provider manifests, no imported files.</summary>
    private static void StripToVersion3(JsonObject root)
    {
        root["Version"] = 3;
        root.Remove("Pm");
        foreach (JsonNode? thread in root["Threads"]!.AsArray())
            foreach (JsonNode? manifest in thread!["M"]!.AsArray())
                manifest!.AsObject().Remove("I");
        foreach (JsonNode? entry in root["Entries"]!.AsArray())
            entry!.AsObject().Remove("Im");
    }

    [Fact]
    public void AVersion4DocumentIsWrittenAndRead()
    {
        using TempDirectory temp = new();
        WrittenV4(temp.Path);
        MediaLibrarySnapshot? loaded = Load(temp.Path);
        Assert.NotNull(loaded);
        Assert.Equal(MediaIndexCacheAdapter.Fingerprint(MediaIndexCacheAdapter.Good), MediaIndexCacheAdapter.Fingerprint(loaded!));
        Assert.Equal(4, JsonNode.Parse(File.ReadAllBytes(Path.Combine(temp.Path, MediaLibraryIndexCache.FileName)))!["Version"]!.GetValue<int>());
    }

    [Fact]
    public void AVersion3DocumentIsStillReadWithoutAColdRebuild()
    {
        using TempDirectory temp = new();
        WrittenV4(temp.Path);
        Rewrite(temp.Path, StripToVersion3);
        MediaLibrarySnapshot? loaded = Load(temp.Path);
        Assert.NotNull(loaded);
        Assert.Equal(MediaIndexCacheAdapter.Fingerprint(MediaIndexCacheAdapter.Good), MediaIndexCacheAdapter.Fingerprint(loaded!));
        Assert.All(loaded!.Threads.SelectMany(thread => thread.Manifests), manifest => Assert.Null(manifest.Identity));
    }

    /// <summary>The manifest file identity version 4 added: {"I": [device, inode, size, mtime, mtime ns, ctime, ctime ns, inode high]}.</summary>
    private static void GiveTheFirstManifestAnIdentity(JsonObject root) =>
        root["Threads"]!.AsArray()[0]!["M"]!.AsArray()[0]!.AsObject()["I"] = new JsonArray(1, 2, 3, 4, 5, 6, 7, 0);

    /// <summary>The item id of a manually imported file, which only version 4 writes.</summary>
    private static void GiveTheFirstEntryAnImportId(JsonObject root) =>
        root["Entries"]!.AsArray()[0]!.AsObject()["Im"] = Guid.Parse("00000000-0000-0000-0000-0000000000aa").ToString();

    [Fact]
    public void AVersion3DocumentWithVersion4ManifestIdentitiesIsNoCache()
    {
        using TempDirectory temp = new();
        WrittenV4(temp.Path);
        // The fixture itself has no stored identities, so the version-4 member is added explicitly: a version 3 document
        // that carries it is a mixture, and a mixture is no cache.
        Rewrite(temp.Path, root => { StripToVersion3(root); GiveTheFirstManifestAnIdentity(root); root["Version"] = 3; });
        Assert.Null(Load(temp.Path));
    }

    [Fact]
    public void AVersion3DocumentWithAnImportedFileIdIsNoCache()
    {
        using TempDirectory temp = new();
        WrittenV4(temp.Path);
        Rewrite(temp.Path, root => { StripToVersion3(root); GiveTheFirstEntryAnImportId(root); root["Version"] = 3; });
        Assert.Null(Load(temp.Path));
    }

    [Fact]
    public void AVersion3DocumentWithProviderManifestsIsNoCache()
    {
        using TempDirectory temp = new();
        WrittenV4(temp.Path);
        Rewrite(temp.Path, root =>
        {
            StripToVersion3(root);
            root["Pm"] = new JsonArray(new JsonObject { ["D"] = "/media/booru" });
        });
        Assert.Null(Load(temp.Path));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(99)]
    public void OlderAndNewerVersionsAreNoCache(int version)
    {
        using TempDirectory temp = new();
        WrittenV4(temp.Path);
        Rewrite(temp.Path, root => root["Version"] = version);
        Assert.Null(Load(temp.Path));
    }

    [Fact]
    public void ADamagedVersion3ThenAVersion4SaveReplacesItAtomically()
    {
        using TempDirectory temp = new();
        WrittenV4(temp.Path);
        Rewrite(temp.Path, root => { StripToVersion3(root); GiveTheFirstManifestAnIdentity(root); root["Version"] = 3; });
        string path = Path.Combine(temp.Path, MediaLibraryIndexCache.FileName);
        MediaLibraryIndexCacheFile file = new(path);
        Assert.Null(file.LoadAsync().GetAwaiter().GetResult());
        Assert.Equal(MediaLibraryIndexCacheWriteStatus.Written, file.SaveAsync(MediaIndexCacheAdapter.Good, MediaLibraryIndexCache.DefaultLimitBytes).GetAwaiter().GetResult().Status);
        Assert.NotNull(Load(temp.Path));
        Assert.Equal(4, JsonNode.Parse(File.ReadAllBytes(path))!["Version"]!.GetValue<int>());
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path, "*.tmp"));
    }

    [Theory]
    [InlineData("Entries")]
    [InlineData("Threads")]
    public void AMemberMissingFromTheDocumentIsNoCache(string member)
    {
        using TempDirectory temp = new();
        WrittenV4(temp.Path);
        Rewrite(temp.Path, root => root.Remove(member));
        Assert.Null(Load(temp.Path));
    }

    [Fact]
    public void AnEntryOfAnUnlistedThreadIsNoCache()
    {
        using TempDirectory temp = new();
        WrittenV4(temp.Path);
        Rewrite(temp.Path, root => root["Threads"]!.AsArray().Clear());
        Assert.Null(Load(temp.Path));
    }
}
