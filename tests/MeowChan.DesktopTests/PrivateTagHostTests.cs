using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Storage;
using MeowChan.Tags;
using MeowChan.Transfer;

namespace MeowChan.DesktopTests;

public sealed class PrivateTagHostTests
{
    private const string FileName = "private-tags.json";
    private static ResourceId Thread => new("source", "board", "42");

    private static PrivateMediaTagTarget Media(ResourceId? thread = null)
    {
        Assert.True(PrivateMediaTagTarget.TryCreate(
            new AttachmentRevisionReference(thread ?? Thread, "100", 0, "att-v2:" + new string('a', 64)),
            "media-1", "original", TransferMediaRole.Original, new string('c', 64),
            out PrivateMediaTagTarget? target, out _));
        return target!;
    }

    [Fact]
    public async Task ThreadAndMediaTagsSurviveRestartAndLeaveOtherPrivateStoresUntouched()
    {
        using TempDirectory temp = new();
        ResourceId otherSource = Thread with { Source = "other" };
        string fingerprint;
        TagId cat, dog;
        byte[] favorites;
        await using (DesktopSessionHost host = new(temp.Path, Array.Empty<IImageBoard>()))
        {
            await host.StartAsync();
            PrivateTagLoadSnapshot empty = host.GetPrivateTagSnapshot();
            Assert.Equal(PrivateTagLoadState.Ready, empty.State);
            Assert.Empty(empty.Snapshot!.Tags);
            await host.SetThreadFavoriteAsync(Thread, true);
            favorites = File.ReadAllBytes(Path.Combine(temp.Path, "thread-favorites.json"));

            cat = (await host.DefinePrivateTagAsync("/rpt/")).Tag;
            dog = (await host.DefinePrivateTagAsync("dog")).Tag;
            Assert.Equal(cat, (await host.DefinePrivateTagAsync("/RPT/")).Tag);
            Assert.True((await host.SetThreadTagAsync(cat, Thread, true)).Changed);
            Assert.False((await host.SetThreadTagAsync(cat, Thread, true)).Changed);
            Assert.True((await host.SetThreadTagAsync(dog, otherSource, true)).Changed);
            PrivateTagCommit last = await host.SetMediaTagAsync(dog, Media(), true);
            fingerprint = last.Snapshot.Fingerprint;
            Assert.Equal(fingerprint, host.GetPublishedPrivateTagSnapshot().Snapshot!.Fingerprint);
            Assert.Equal(favorites, File.ReadAllBytes(Path.Combine(temp.Path, "thread-favorites.json")));
            Assert.Equal(Thread, Assert.Single(host.GetThreadFavorites()));
        }

        await using (DesktopSessionHost restored = new(temp.Path, Array.Empty<IImageBoard>()))
        {
            await restored.StartAsync();
            PrivateTagStoreSnapshot snapshot = restored.GetPrivateTagSnapshot().Snapshot!;
            Assert.Equal(fingerprint, snapshot.Fingerprint);
            Assert.Equal(new[] { "/rpt/", "dog" }, snapshot.Tags.Select(tag => tag.Name.Display));
            Assert.Equal(cat, Assert.Single(snapshot.TagsOf(Thread)).Id);
            Assert.Equal(dog, Assert.Single(snapshot.TagsOf(otherSource)).Id);
            Assert.Equal(dog, Assert.Single(snapshot.TagsOf(Media())).Id);
            Assert.Empty(snapshot.TagsOf(Media(otherSource)));
            Assert.Equal(favorites, File.ReadAllBytes(Path.Combine(temp.Path, "thread-favorites.json")));
        }

        using TempDirectory other = new();
        await using DesktopSessionHost separate = new(other.Path, Array.Empty<IImageBoard>());
        await separate.StartAsync();
        Assert.Empty(separate.GetPrivateTagSnapshot().Snapshot!.Tags);
    }

    [Fact]
    public async Task RejectedChangesSurfaceTheirReasonAndDoNotReplaceThePublishedView()
    {
        using TempDirectory temp = new();
        await using DesktopSessionHost host = new(temp.Path, Array.Empty<IImageBoard>());
        await host.StartAsync();
        PrivateTagCommit cat = await host.DefinePrivateTagAsync("cat");
        PrivateTagCommit dog = await host.DefinePrivateTagAsync("dog");
        byte[] before = File.ReadAllBytes(Path.Combine(temp.Path, FileName));

        async Task<PrivateTagStoreError> Rejected(Func<Task> action) =>
            (await Assert.ThrowsAsync<PrivateTagStoreException>(action)).Error;
        Assert.Equal(PrivateTagStoreError.NameCollision, await Rejected(() => host.RenamePrivateTagAsync(dog.Tag, "CAT", dog.Snapshot.Revision)));
        Assert.Equal(PrivateTagStoreError.StaleRevision, await Rejected(() => host.RenamePrivateTagAsync(dog.Tag, "bird", cat.Snapshot.Revision)));
        Assert.Equal(PrivateTagStoreError.InvalidName, await Rejected(() => host.DefinePrivateTagAsync("line\nbreak")));
        Assert.Equal(PrivateTagStoreError.UnknownTag, await Rejected(() => host.SetThreadTagAsync(new TagId(Guid.NewGuid()), Thread, true)));
        Assert.Equal(PrivateTagStoreError.InvalidThread, await Rejected(() => host.SetThreadTagAsync(cat.Tag, new ResourceId("source", "board"), true)));

        Assert.Equal(before, File.ReadAllBytes(Path.Combine(temp.Path, FileName)));
        Assert.Equal(dog.Snapshot.Fingerprint, host.GetPublishedPrivateTagSnapshot().Snapshot!.Fingerprint);
        PrivateTagCommit renamed = await host.RenamePrivateTagAsync(dog.Tag, "bird", dog.Snapshot.Revision);
        Assert.True(renamed.Changed);
        Assert.Equal("bird", renamed.Snapshot.Find(dog.Tag)!.Name.Display);
    }

    [Fact]
    public async Task DamagedOrNewerIndexIsUnavailableAndNeverOverwritten()
    {
        using TempDirectory temp = new();
        await using DesktopSessionHost host = new(temp.Path, Array.Empty<IImageBoard>());
        await host.StartAsync();
        TagId cat = (await host.DefinePrivateTagAsync("cat")).Tag;
        string file = Path.Combine(temp.Path, FileName);
        byte[] valid = File.ReadAllBytes(file);
        foreach (string damaged in new[] { "{bad", "{\"Version\":99,\"Revision\":2,\"Tags\":[],\"Threads\":[],\"Media\":[]}" })
        {
            File.WriteAllText(file, damaged);
            PrivateTagLoadSnapshot unavailable = host.GetPrivateTagSnapshot();
            Assert.Equal(PrivateTagLoadState.Unavailable, unavailable.State);
            Assert.Null(unavailable.Snapshot);
            Assert.False(string.IsNullOrWhiteSpace(unavailable.Error));
            await Assert.ThrowsAsync<InvalidDataException>(() => host.SetThreadTagAsync(cat, Thread, true));
            await Assert.ThrowsAsync<InvalidDataException>(() => host.DefinePrivateTagAsync("dog"));
            Assert.Equal(damaged, File.ReadAllText(file));
        }
        File.WriteAllBytes(file, valid);
        Assert.Equal(PrivateTagLoadState.Ready, host.GetPrivateTagSnapshot().State);
        Assert.True((await host.SetThreadTagAsync(cat, Thread, true)).Changed);
    }

    [Fact]
    public async Task CanceledAndStoppedHostCannotChangeTags()
    {
        using TempDirectory temp = new();
        await using DesktopSessionHost host = new(temp.Path, Array.Empty<IImageBoard>());
        await host.StartAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.DefinePrivateTagAsync("cat", new CancellationToken(true)));
        Assert.Empty(host.GetPrivateTagSnapshot().Snapshot!.Tags);
        TagId cat = (await host.DefinePrivateTagAsync("cat")).Tag;
        await host.StopAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.DefinePrivateTagAsync("dog"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.SetThreadTagAsync(cat, Thread, true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.SetMediaTagAsync(cat, Media(), true));
        Assert.False(File.Exists(Path.Combine(temp.Path, FileName + ".bak")));
    }
}
