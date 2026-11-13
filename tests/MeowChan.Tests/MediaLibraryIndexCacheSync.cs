using MeowChan.Gallery;

namespace MeowChan.Tests;

/// <summary>Blocking forms of the cache's async load and save for tests; production code never blocks on them.</summary>
internal static class MediaLibraryIndexCacheSync
{
    internal static MediaLibrarySnapshot? Load(string path) => MediaLibraryIndexCache.LoadAsync(path).GetAwaiter().GetResult()?.Snapshot;

    internal static void Save(string path, MediaLibrarySnapshot snapshot) =>
        MediaLibraryIndexCache.SaveAsync(path, snapshot, MediaLibraryIndexCache.DefaultLimitBytes).GetAwaiter().GetResult();
}
