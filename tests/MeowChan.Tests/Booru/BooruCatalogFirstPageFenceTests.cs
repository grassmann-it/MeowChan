using System;
using System.IO;
using System.Text.Json.Nodes;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Persistence;

namespace MeowChan.Tests.Booru;

/// <summary>The first requested page cannot have advanced beyond the fence captured by BeginCheck.</summary>
public sealed class BooruCatalogFirstPageFenceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-14T00:00:00Z");

    [Theory]
    [InlineData(null)]
    [InlineData("99")]
    public void AFirstPageWithALostOrLoweredFenceCannotReplaceTheDurableRecord(string? fence)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore store = new(directory.Path, lease);
        BooruWatcherRecord valid = FirstPage("populated");
        store.UpsertWatcher(valid);
        byte[] before = File.ReadAllBytes(store.FilePath);
        long writes = store.Counters.Writes;

        Assert.Throws<ArgumentException>(() => store.UpsertWatcher(valid with { CheckStartHighWaterMark = fence }));

        Assert.Equal(writes, store.Counters.Writes);
        Assert.Equal(before, File.ReadAllBytes(store.FilePath));
        Assert.Equal("100", Assert.Single(new BooruCatalogStore(directory.Path, lease).Read().Watchers).CheckStartHighWaterMark);
        Assert.Single(Directory.GetFiles(directory.Path, BooruCatalogStore.FileName));
        Assert.Empty(Directory.GetFiles(directory.Path, "." + BooruCatalogStore.FileName + ".*.tmp"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("lower")]
    public void AnOfflineFirstPageWithAnInconsistentFenceFailsClosedAndIsNotOverwritten(string damage)
    {
        using TempDirectory directory = new();
        string path = System.IO.Path.Combine(directory.Path, BooruCatalogStore.FileName);
        using (ProfileLease lease = ProfileLease.Acquire(directory.Path))
            new BooruCatalogStore(directory.Path, lease).UpsertWatcher(FirstPage("populated"));

        JsonObject root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        JsonObject watcher = root["watchers"]![0]!.AsObject();
        if (damage == "missing")
            Assert.True(watcher.Remove("checkStartHighWaterMark"));
        else
            watcher["checkStartHighWaterMark"] = damage == "null" ? null : JsonValue.Create("99");
        File.WriteAllText(path, root.ToJsonString());
        byte[] damaged = File.ReadAllBytes(path);

        using ProfileLease reopened = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore store = new(directory.Path, reopened);
        Assert.Throws<InvalidDataException>(() => store.Read());
        Assert.Throws<InvalidDataException>(() => store.UpsertPost(new("danbooru", "200", BooruPostClaimPresence.None), Now));
        Assert.Equal(0L, store.Counters.Writes);
        Assert.Equal(damaged, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("new")]
    [InlineData("empty")]
    [InlineData("populated")]
    public void ARealFirstPageKeepsItsExactFenceAcrossRestartAndAnUnchangedWrite(string baseline)
    {
        using TempDirectory directory = new();
        BooruWatcherRecord expected = FirstPage(baseline);
        byte[] before;
        using (ProfileLease lease = ProfileLease.Acquire(directory.Path))
        {
            BooruCatalogStore store = new(directory.Path, lease);
            store.UpsertWatcher(expected);
            before = File.ReadAllBytes(store.FilePath);
        }

        using ProfileLease reopened = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore restarted = new(directory.Path, reopened);
        BooruWatcherRecord actual = Assert.Single(restarted.Read().Watchers);
        Assert.Equal(1, actual.NextPage);
        Assert.True(actual.CheckStartHighWaterMarkCaptured);
        Assert.Equal(baseline == "populated" ? "100" : null, actual.HighWaterMark);
        Assert.Equal(actual.HighWaterMark, actual.CheckStartHighWaterMark);
        Assert.Equal(expected.BaselineEstablished, actual.BaselineEstablished);
        restarted.UpsertWatcher(actual);
        Assert.Equal(before, File.ReadAllBytes(restarted.FilePath));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("populated")]
    public void AContinuationStillAllowsAnOlderOrNullCapturedFence(string baseline)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore store = new(directory.Path, lease);
        DanbooruWatcher checking = DanbooruWatcherRecordMapper.FromRecord(FirstPage(baseline));
        DanbooruWatcherTransition applied = DanbooruWatcher.ApplyPage(checking,
            new("danbooru", checking.CanonicalQuery, 1, ["120", "119"], false), Now);
        Assert.True(applied.Accepted);
        BooruWatcherRecord continuation = DanbooruWatcherRecordMapper.ToRecord(applied.Watcher);
        Assert.Equal(2, continuation.NextPage);
        Assert.Equal("120", continuation.HighWaterMark);
        Assert.Equal(baseline == "populated" ? "100" : null, continuation.CheckStartHighWaterMark);

        store.UpsertWatcher(continuation);
        BooruWatcherRecord actual = Assert.Single(new BooruCatalogStore(directory.Path, lease).Read().Watchers);
        Assert.Equal(continuation.CheckStartHighWaterMark, actual.CheckStartHighWaterMark);
        Assert.Equal("119", actual.CursorBeforePostId);
    }

    private static BooruWatcherRecord FirstPage(string baseline)
    {
        Assert.True(DanbooruWatcher.TryCreate("w-first-page", "cat blue_eyes", Now,
            out DanbooruWatcher? created, out _, pageSize: 2, pageBudget: 2));
        DanbooruWatcher watcher = created!;
        if (baseline != "new")
        {
            watcher = DanbooruWatcher.BeginCheck(watcher, Now).Watcher;
            DanbooruWatcherTransition established = DanbooruWatcher.ApplyPage(watcher,
                new("danbooru", watcher.CanonicalQuery, 1, baseline == "empty" ? [] : ["100"], true), Now);
            Assert.True(established.Accepted);
            watcher = established.Watcher;
        }
        DanbooruWatcherTransition started = DanbooruWatcher.BeginCheck(watcher, Now.AddMinutes(1));
        Assert.True(started.Accepted);
        return DanbooruWatcherRecordMapper.ToRecord(started.Watcher);
    }

    private sealed class TempDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "meowchan-booru-first-page-fence-" + Guid.NewGuid().ToString("N"));

        internal TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
