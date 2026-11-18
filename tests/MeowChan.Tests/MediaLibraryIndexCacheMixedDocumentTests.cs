using System.Text.RegularExpressions;
using MeowChan.Archive;
using MeowChan.Gallery;
using MeowChan.Persistence;
using MeowChan.Storage;
using static MeowChan.Tests.MediaLibraryIndexTests;

namespace MeowChan.Tests;

/// <summary>
/// Version 4 of the media index cache carries the manifest file identities, but not every manifest has one: a folder
/// that could not be read, or a file system without identities, leaves it unknown. Such a mixed document must load,
/// keep what it does know, and let the next build reopen only the manifests it cannot vouch for.
/// MediaLibraryIndexCacheTests covers version 3 files and the all-or-nothing cases.
/// </summary>
public sealed class MediaLibraryIndexCacheMixedDocumentTests
{
    private static readonly Regex FirstIdentity = new(",\"I\":\\[[-0-9,]*\\]");

    private sealed class Counting
    {
        private int _opens;
        internal int Opens => _opens;
        internal MediaManifestCache Cache() => new((directory, token) =>
        {
            Interlocked.Increment(ref _opens);
            return MediaAssetReader.OpenAsync(directory, token);
        }, identity: true);
    }

    private static Func<CancellationToken, Task<ArchiveReader>> Reader(string profile) =>
        token => ArchiveReader.OpenAsync(profile, token);

    private static bool IdentitiesAvailable(string profile) =>
        RegularProfileFile.TryGetIdentity(Path.Combine(profile, "media", "a", ".meowchan-manifest.json")) != null;

    private static int IdentityCount(MediaLibrarySnapshot snapshot) =>
        snapshot.Threads.SelectMany(thread => thread.Manifests).Count(state => state.Identity != null);

    private static int ManifestCount(MediaLibrarySnapshot snapshot) =>
        snapshot.Threads.Sum(thread => thread.Manifests.Count);

    [Theory]
    [InlineData(false)] // the key is missing, as an older writer or an unreadable folder leaves it
    [InlineData(true)]  // the key is there and explicitly null
    public async Task AnIndexWhereOnlyOneManifestKnowsItsIdentityLoadsAndKeepsTheRest(bool explicitNull)
    {
        using TempDirectory temp = new();
        await SeedAsync(temp.Path);
        if (!IdentitiesAvailable(temp.Path)) return; // Without identities every document is the all-unknown case.
        string path = Path.Combine(temp.Path, "cache", MediaLibraryIndexCache.FileName);
        MediaLibrarySnapshot built = await MediaLibraryIndexBuilder.BuildAsync(
            Reader(temp.Path), new Counting().Cache());
        Assert.Equal(2, ManifestCount(built));
        Assert.Equal(2, IdentityCount(built));

        MediaLibraryIndexCacheSync.Save(path, built);
        string document = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, FirstIdentity.Replace(document, explicitNull ? ",\"I\":null" : string.Empty, 1));

        MediaLibrarySnapshot mixed = MediaLibraryIndexCacheSync.Load(path)!;
        Assert.NotNull(mixed);
        Assert.Equal(2, ManifestCount(mixed));
        Assert.Equal(1, IdentityCount(mixed));
        Assert.Equal(built.Entries, mixed.Entries);
        Assert.Equal(built.Threads.Select(thread => (thread.Thread, thread.Revision, thread.Complete)),
            mixed.Threads.Select(thread => (thread.Thread, thread.Revision, thread.Complete)));
    }

    [Fact]
    public async Task TheNextBuildReopensOnlyTheManifestWhoseIdentityIsUnknownAndWritesABoundDocumentAgain()
    {
        using TempDirectory temp = new();
        await SeedAsync(temp.Path);
        if (!IdentitiesAvailable(temp.Path)) return;
        string path = Path.Combine(temp.Path, "cache", MediaLibraryIndexCache.FileName);
        MediaLibrarySnapshot built = await MediaLibraryIndexBuilder.BuildAsync(
            Reader(temp.Path), new Counting().Cache());
        MediaLibraryIndexCacheSync.Save(path, built);
        await File.WriteAllTextAsync(path, FirstIdentity.Replace(await File.ReadAllTextAsync(path), string.Empty, 1));
        MediaLibrarySnapshot mixed = MediaLibraryIndexCacheSync.Load(path)!;

        Counting counting = new();
        MediaLibrarySnapshot next = await MediaLibraryIndexBuilder.BuildAsync(
            Reader(temp.Path), counting.Cache(), mixed);

        Assert.Equal(1, counting.Opens); // The known half is reused, the unknown half is read once.
        Assert.Equal((await BuildAsync(temp.Path)).Entries.OrderBy(Key), next.Entries.OrderBy(Key));
        Assert.Equal(2, IdentityCount(next)); // Both halves are bound again, so the next start opens nothing.

        MediaLibraryIndexCacheSync.Save(path, next);
        Counting again = new();
        await MediaLibraryIndexBuilder.BuildAsync(Reader(temp.Path),
            again.Cache(), MediaLibraryIndexCacheSync.Load(path)!);
        Assert.Equal(0, again.Opens);
    }

    private static string Key(MediaLibraryEntry entry) =>
        entry.Thread + "|" + entry.PostId + "|" + entry.Position + "|" + entry.AssetId;
}
