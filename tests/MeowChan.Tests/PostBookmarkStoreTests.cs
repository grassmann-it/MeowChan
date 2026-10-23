using System.Text;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.Tests;

public sealed class PostBookmarkStoreTests
{
    private static PostBookmarkKey Key(string source = "4chan", string thread = "123", string post = "124") => new(new(source, "g", thread), post);

    [Fact]
    public void ExplicitCreateEditRemoveRoundtripKeepsOrderAndNoNoteHistory()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var store = new PostBookmarkStore(temp.Path, lease);
        Assert.Empty(store.Read().Bookmarks); Assert.False(File.Exists(store.Path));
        var one = store.Upsert(Key(), "private old note\r\n🙂\t", 0, false);
        Assert.Equal(1, one.Revision);
        Assert.Equal("private old note\n🙂\t", Assert.Single(one.Bookmarks).Note);
        var two = store.Upsert(Key("other"), "", one.Revision, false);
        var edited = store.Upsert(Key(), "replacement", two.Revision, true);
        Assert.Equal(new[] { Key(), Key("other") }, edited.Bookmarks.Select(entry => entry.Key));
        Assert.Equal("replacement", new PostBookmarkStore(temp.Path, lease).Read().Bookmarks[0].Note);
        var removed = store.Remove(Key(), edited.Revision);
        Assert.Equal(Key("other"), Assert.Single(removed.Bookmarks).Key);
        string[] ownFiles = Directory.GetFiles(temp.Path, "post-bookmarks*");
        Assert.Equal(new[] { store.Path }, ownFiles);
        Assert.DoesNotContain("replacement", File.ReadAllText(store.Path));
        Assert.DoesNotContain("private old note", File.ReadAllText(store.Path));
        Assert.DoesNotContain("title", File.ReadAllText(store.Path));
        Assert.DoesNotContain("url", File.ReadAllText(store.Path));
    }

    [Fact]
    public void ConflictingEditorsAndOldSaveAfterRemoveCannotOverwriteOrRecreate()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var first = new PostBookmarkStore(temp.Path, lease); var other = new PostBookmarkStore(temp.Path, lease);
        first.Upsert(Key(), "first", 0, false);
        Assert.Throws<PostBookmarkConflictException>(() => other.Upsert(Key(), "old draft", 0, false));
        var updated = other.Upsert(Key(), "winner", 1, true);
        first.Remove(Key(), updated.Revision);
        Assert.Throws<PostBookmarkConflictException>(() => other.Upsert(Key(), "old draft", updated.Revision, true));
        Assert.Throws<PostBookmarkConflictException>(() => other.Upsert(Key(), "old draft", updated.Revision + 1, true));
        Assert.Empty(first.Read().Bookmarks);
    }

    [Fact]
    public void UnicodeLimitCountsCodepointsAndPreservesWhitespace()
    {
        string maximum = string.Concat(Enumerable.Repeat("🙂", 8000));
        Assert.Equal(maximum, PostBookmarkStore.NormalizeNote(maximum));
        Assert.Throws<ArgumentException>(() => PostBookmarkStore.NormalizeNote(maximum + "a"));
        Assert.Equal(" \t\n ", PostBookmarkStore.NormalizeNote(" \t\r\n "));
        Assert.Throws<ArgumentException>(() => PostBookmarkStore.NormalizeNote("a\0b"));
        Assert.Throws<ArgumentException>(() => PostBookmarkStore.NormalizeNote("\uD800"));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"schemaVersion\":2,\"revision\":0,\"bookmarks\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"revision\":-1,\"bookmarks\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"revision\":0,\"bookmarks\":[],\"unexpected\":true}")]
    [InlineData("{\"schemaVersion\":1,\"revision\":0,\"revision\":1,\"bookmarks\":[]}")]
    public void InvalidOrFutureDocumentsAreNeverReplaced(string raw)
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var store = new PostBookmarkStore(temp.Path, lease); File.WriteAllText(store.Path, raw);
        Assert.Throws<InvalidDataException>(() => store.Read());
        Assert.Throws<InvalidDataException>(() => store.Upsert(Key(), "new", 0, false));
        Assert.Equal(raw, File.ReadAllText(store.Path));
        Assert.Single(Directory.GetFiles(temp.Path, "post-bookmarks*"));
    }

    [Fact]
    public void DuplicateAndInvalidIdentitiesFailAsOneDocument()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var store = new PostBookmarkStore(temp.Path, lease);
        string entry = "{\"source\":\"4chan\",\"board\":\"g\",\"thread\":\"123\",\"post\":\"124\",\"note\":\"work\"}";
        File.WriteAllText(store.Path, "{\"schemaVersion\":1,\"revision\":1,\"bookmarks\":[" + entry + "," + entry + "]}");
        Assert.Throws<InvalidDataException>(() => store.Read());
        File.WriteAllText(store.Path, "{\"schemaVersion\":1,\"revision\":1,\"bookmarks\":[" + entry.Replace("\"g\"", "\"\"") + "]}");
        Assert.Throws<InvalidDataException>(() => store.Read());
    }

    [Fact]
    public void CancellationAndDestinationFailurePreserveDataAndCleanTemps()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var store = new PostBookmarkStore(temp.Path, lease); store.Upsert(Key(), "kept", 0, false);
        byte[] original = File.ReadAllBytes(store.Path);
        using CancellationTokenSource cancelled = new(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => store.Upsert(Key(), "lost", 1, true, cancelled.Token));
        Assert.Equal(original, File.ReadAllBytes(store.Path));
        File.Delete(store.Path); Directory.CreateDirectory(store.Path);
        Exception failure = Assert.ThrowsAny<Exception>(() => store.Upsert(Key(), "new", 0, false));
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Empty(Directory.GetFiles(temp.Path, "post-bookmarks.json.pending-*"));
    }

    [Fact]
    public void NextOwnerCleansOnlyExactOwnGuidTemps()
    {
        using TempDirectory temp = new();
        string own = Path.Combine(temp.Path, "post-bookmarks.json.pending-" + Guid.NewGuid().ToString("N"));
        string unrelated = Path.Combine(temp.Path, "post-bookmarks.json.pending-user-work");
        File.WriteAllText(own, "old note"); File.WriteAllText(unrelated, "retained");
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var store = new PostBookmarkStore(temp.Path, lease);
        Assert.False(File.Exists(own)); Assert.True(File.Exists(unrelated)); Assert.False(File.Exists(store.Path));
    }

    [Fact]
    public void EntryAndByteLimitsRejectWholeMutation()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var store = new PostBookmarkStore(temp.Path, lease);
        var entries = Enumerable.Range(0, 10000).Select(index => new { source = "4chan", board = "g", thread = "123", post = index.ToString(), note = "" }).ToArray();
        File.WriteAllText(store.Path, JsonSerializer.Serialize(new { schemaVersion = 1, revision = 1, bookmarks = entries }));
        Assert.Equal(10000, store.Read().Bookmarks.Count);
        Assert.Throws<InvalidDataException>(() => store.Upsert(Key(post: "extra"), "", 1, false));
        Assert.Equal(10000, store.Read().Bookmarks.Count);
        // UTF-8 serialized notes, rather than UTF-16 string length, enforce the document boundary.
        string large = new('\u00A0', 8000);
        var largeEntries = Enumerable.Range(0, 698).Select(index => new { source = "4chan", board = "g", thread = "123", post = index.ToString(), note = large }).ToArray();
        byte[] before = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, revision = 1, bookmarks = largeEntries });
        File.WriteAllBytes(store.Path, before);
        Assert.True(before.Length < PostBookmarkStore.MaximumDocumentBytes);
        Assert.Throws<InvalidDataException>(() => store.Upsert(Key(post: "extra"), large, 1, false));
        Assert.Equal(before, File.ReadAllBytes(store.Path));
        File.WriteAllBytes(store.Path, new byte[PostBookmarkStore.MaximumDocumentBytes + 1]);
        Assert.Throws<InvalidDataException>(() => store.Read());
    }

    [Fact]
    public async Task ExactLocalResolutionAndArchiveDeletionRetainReferenceAndNote()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(temp.Path, lease);
        var key = Key();
        await catalog.CaptureAsync(new CapturedThreadSnapshot(key.Thread, "https://example.test/g/thread/123", "Local title",
            ThreadSourceState.Active, DateTimeOffset.UtcNow, temp.Path, new[] { new CapturedPost("first", 0, PostFieldPresence.Html, Html: "first"), new CapturedPost("124", 1, PostFieldPresence.Html, Html: "target") }));
        var store = new PostBookmarkStore(temp.Path, lease); store.Upsert(key, "independent user work", 0, false);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(temp.Path);
        var found = await PostBookmarkTarget.ResolveAsync(reader, key, default);
        Assert.Equal(1, found!.PostIndex); Assert.Equal(key.Post, found.View.GetPosts(found.PostIndex, 1).Single().PostId);
        Assert.Null(await PostBookmarkTarget.ResolveAsync(reader, Key(post: "missing"), default));
        Assert.Null(await PostBookmarkTarget.ResolveAsync(reader, Key(source: "other"), default));
        await catalog.DeleteThreadsAsync(new[] { key.Thread });
        Assert.Null(await PostBookmarkTarget.ResolveAsync(reader, key, default));
        Assert.Equal("independent user work", Assert.Single(store.Read().Bookmarks).Note);
    }
}
