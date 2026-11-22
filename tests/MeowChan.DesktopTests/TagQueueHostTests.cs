using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

// PT1: the host commands of the tagging queue write before they complete, publish the committed view, keep the walk's undo history and
// report every outcome as one localized sentence. Nothing here assigns a tag, favorite or rating.
public sealed class TagQueueHostTests
{
    private static readonly ResourceId Thread = new("source", "board", "42");
    private static readonly DateTimeOffset T0 = new(2026, 11, 14, 8, 0, 0, TimeSpan.Zero);

    private static TagQueueItem Item(int n) => new(Thread, "/media/42", "asset-" + n, new string((char)('a' + n % 6), 64), 100 + n, "media-" + n,
        "original", "p" + n, null, null, null, false, null, "file-" + n + ".png", SavedMediaKind.Image, TagQueueState.Open, T0.AddSeconds(n));

    private static IReadOnlyList<TagQueueItem> Items(int from, int count) => Enumerable.Range(from, count).Select(Item).ToArray();

    private static async Task<DesktopSessionHost> StartAsync(string directory)
    {
        DesktopSessionHost host = new(directory, Array.Empty<IImageBoard>());
        await host.StartAsync();
        return host;
    }

    // A profile directory that holds files but no settings is a recovery case; a started and stopped session creates the settings first.
    private static async Task<string> ProfileAsync(TempDirectory temp)
    {
        await using (await StartAsync(temp.Path)) { }
        return temp.Path;
    }

    [Fact]
    public async Task AnAddIsDurableBeforeItCompletesAndSurvivesARestart()
    {
        using TempDirectory temp = new();
        await using (DesktopSessionHost host = await StartAsync(temp.Path))
        {
            Assert.Null(host.GetTagQueue());
            TagQueueAddResult result = await host.EnqueueTagQueueAsync(Items(1, 3));
            Assert.Equal((3, 0, 0), (result.Added, result.AlreadyPresent, result.OverLimit));
            Assert.Equal(3, host.GetTagQueue()!.Count);
            Assert.True(File.Exists(Path.Combine(temp.Path, TagQueueStore.FileName)));
        }
        await using DesktopSessionHost again = await StartAsync(temp.Path);
        TagQueueSnapshot loaded = await again.LoadTagQueueAsync();
        Assert.Equal(new[] { "asset-1", "asset-2", "asset-3" }, loaded.Items.Select(item => item.AssetId));
        Assert.Same(loaded, again.GetTagQueue());
    }

    [Fact]
    public async Task ADecisionAdvancesTheCursorAndUndoRestoresStateAndCursorExactly()
    {
        using TempDirectory temp = new();
        await using DesktopSessionHost host = await StartAsync(temp.Path);
        await host.EnqueueTagQueueAsync(Items(1, 3));
        TagQueueDecision? first = await host.DecideTagQueueAsync(Item(1).Key, TagQueueState.Done);
        TagQueueDecision? second = await host.DecideTagQueueAsync(Item(2).Key, TagQueueState.Skipped);
        Assert.Equal((TagQueueState.Open, TagQueueState.Done, 0, 1), (first!.Before, first.After, first.CursorBefore, first.CursorAfter));
        Assert.Equal((TagQueueState.Open, TagQueueState.Skipped, 1, 2), (second!.Before, second.After, second.CursorBefore, second.CursorAfter));
        Assert.Equal(2, host.TagQueueUndoDepth);
        Assert.Equal(2, host.GetTagQueue()!.Cursor);

        TagQueueDecision? undone = await host.UndoTagQueueDecisionAsync();
        Assert.Equal(second, undone);
        TagQueueSnapshot afterUndo = host.GetTagQueue()!;
        Assert.Equal((1, TagQueueState.Open, TagQueueState.Done), (afterUndo.Cursor, afterUndo.Items[1].State, afterUndo.Items[0].State));
        Assert.Equal(1, host.TagQueueUndoDepth);

        await host.UndoTagQueueDecisionAsync();
        Assert.Null(await host.UndoTagQueueDecisionAsync());
        TagQueueSnapshot start = host.GetTagQueue()!;
        Assert.Equal((0, 3), (start.Cursor, start.Open));
    }

    [Fact]
    public async Task ADecisionOnAnEntryThatLeftTheQueueChangesNothing()
    {
        using TempDirectory temp = new();
        await using DesktopSessionHost host = await StartAsync(temp.Path);
        await host.EnqueueTagQueueAsync(Items(1, 2));
        long revision = host.GetTagQueue()!.Revision;
        Assert.Null(await host.DecideTagQueueAsync(Item(9).Key, TagQueueState.Done));
        Assert.Equal((revision, 0), (host.GetTagQueue()!.Revision, host.TagQueueUndoDepth));
    }

    [Fact]
    public async Task UndoDropsTheStepsOfRemovedEntriesAndSkipsAnEntryThatLeftTheQueue()
    {
        using TempDirectory temp = new();
        await using DesktopSessionHost host = await StartAsync(temp.Path);
        await host.EnqueueTagQueueAsync(Items(1, 3));
        await host.DecideTagQueueAsync(Item(1).Key, TagQueueState.Done);
        await host.DecideTagQueueAsync(Item(2).Key, TagQueueState.Done);
        await host.RemoveFromTagQueueAsync(new[] { Item(2).Key });
        Assert.Equal(1, host.TagQueueUndoDepth);
        Assert.Equal(Item(1).Key, (await host.UndoTagQueueDecisionAsync())!.Key);
        Assert.Equal(TagQueueState.Open, host.GetTagQueue()!.Items[0].State);
    }

    [Fact]
    public async Task ClearingEverythingForgetsTheWalkAndClearingFinishedEntriesForgetsTheirDecisionsOnly()
    {
        using TempDirectory temp = new();
        await using DesktopSessionHost host = await StartAsync(temp.Path);
        await host.EnqueueTagQueueAsync(Items(1, 3));
        await host.DecideTagQueueAsync(Item(1).Key, TagQueueState.Done);
        await host.DecideTagQueueAsync(Item(2).Key, TagQueueState.Skipped);
        await host.ClearTagQueueAsync(finishedOnly: true);
        Assert.Equal(2, host.GetTagQueue()!.Count);
        Assert.Equal(1, host.TagQueueUndoDepth); // the skipped entry stays, its decision too
        await host.ClearTagQueueAsync();
        Assert.Equal((0, 0), (host.GetTagQueue()!.Count, host.TagQueueUndoDepth));
    }

    // W3-30: after clearing the finished entries the next undo takes back the newest decision at once, not a stale step.
    [Fact]
    public async Task AfterClearingFinishedEntriesTheNextUndoTakesBackTheNewDecisionAtOnce()
    {
        using TempDirectory temp = new();
        await using DesktopSessionHost host = await StartAsync(temp.Path);
        await host.EnqueueTagQueueAsync(Items(1, 3));
        await host.DecideTagQueueAsync(Item(1).Key, TagQueueState.Done);
        await host.ClearTagQueueAsync(finishedOnly: true);
        Assert.Equal(0, host.TagQueueUndoDepth);
        TagQueueDecision? fresh = await host.DecideTagQueueAsync(Item(2).Key, TagQueueState.Done);
        Assert.Same(fresh, await host.UndoTagQueueDecisionAsync());
        Assert.Equal(TagQueueState.Open, host.GetTagQueue()!.Items[0].State);
    }

    // W3-01 (a): an undo that names a decision whose entry was removed meanwhile writes nothing and never takes back another entry.
    [Fact]
    public async Task AnUndoOfARemovedEntrysDecisionWritesNothingAndLeavesTheOlderDecisionAlone()
    {
        using TempDirectory temp = new();
        await using DesktopSessionHost host = await StartAsync(temp.Path);
        await host.EnqueueTagQueueAsync(Items(1, 3));
        TagQueueDecision a = (await host.DecideTagQueueAsync(Item(1).Key, TagQueueState.Done))!;
        TagQueueDecision b = (await host.DecideTagQueueAsync(Item(2).Key, TagQueueState.Done))!;
        await host.RemoveFromTagQueueAsync(new[] { Item(2).Key });
        long revision = host.GetTagQueue()!.Revision;

        Assert.Null(await host.UndoTagQueueDecisionAsync(b));
        TagQueueSnapshot after = host.GetTagQueue()!;
        Assert.Equal(revision, after.Revision);
        Assert.Equal(TagQueueState.Done, after.Items[0].State); // A stays done
        Assert.Equal(1, host.TagQueueUndoDepth);
        Assert.Same(a, await host.UndoTagQueueDecisionAsync(a));
        Assert.Equal(TagQueueState.Open, host.GetTagQueue()!.Items[0].State);
    }

    // W3-01 (b): a refused undo write keeps the decision; the next try takes back the same entry.
    [Fact]
    public async Task ARefusedUndoWriteKeepsTheDecisionAndTheNextTryTakesBackTheSameEntry()
    {
        using TempDirectory temp = new();
        await using DesktopSessionHost host = await StartAsync(temp.Path);
        await host.EnqueueTagQueueAsync(Items(1, 3));
        TagQueueDecision a = (await host.DecideTagQueueAsync(Item(1).Key, TagQueueState.Done))!;
        TagQueueDecision b = (await host.DecideTagQueueAsync(Item(2).Key, TagQueueState.Skipped))!;
        string backup = Path.Combine(temp.Path, TagQueueStore.FileName + ".bak");
        File.WriteAllText(backup, "{\"Version\":99}"); // a newer backup blocks every write

        await Assert.ThrowsAsync<ProfileBackupBlockedException>(() => host.UndoTagQueueDecisionAsync(b));
        Assert.Equal(2, host.TagQueueUndoDepth);
        Assert.Null(host.TagQueueError); // a refused write is no read error
        Assert.Equal(TagQueueState.Skipped, host.GetTagQueue()!.Items[1].State);

        File.Delete(backup);
        Assert.Same(b, await host.UndoTagQueueDecisionAsync(b));
        TagQueueSnapshot after = host.GetTagQueue()!;
        Assert.Equal((TagQueueState.Done, TagQueueState.Open, 1), (after.Items[0].State, after.Items[1].State, after.Cursor));
        Assert.Same(a, await host.UndoTagQueueDecisionAsync());
    }

    // W3-04: the undo puts the resume point back on the entry it stood on, also after an entry before it was removed.
    [Fact]
    public async Task AnUndoAfterARemovalBeforeTheCursorReturnsToTheDecidedEntry()
    {
        using TempDirectory temp = new();
        await using DesktopSessionHost host = await StartAsync(temp.Path);
        await host.EnqueueTagQueueAsync(Items(1, 7));
        await host.SetTagQueueCursorAsync(5);
        TagQueueDecision decision = (await host.DecideTagQueueAsync(Item(6).Key, TagQueueState.Done))!;
        Assert.Equal((5, 6, Item(6).Key), (decision.CursorBefore, decision.CursorAfter, decision.CursorBeforeKey));
        await host.RemoveFromTagQueueAsync(new[] { Item(3).Key });

        await host.UndoTagQueueDecisionAsync(decision);
        TagQueueSnapshot after = host.GetTagQueue()!;
        Assert.Equal(4, after.Cursor);
        Assert.Equal(Item(6).Key, after.Items[after.Cursor].Key);
    }

    // W3-18: the host's decision history is as deep as the setting.
    [Fact]
    public async Task TheDecisionHistoryFollowsTheUndoDepthSetting()
    {
        using TempDirectory temp = new();
        new Config { AutomaticUpdateChecks = false, SaveUrlsOnExit = false, DownloadPath = Path.Combine(temp.Path, "media") }.SaveConfig(temp.Path);
        await using DesktopSessionHost host = await StartAsync(temp.Path);
        Config draft = host.Config.Clone();
        draft.TagQueueUndoDepth = 5;
        await host.ApplySettingsAsync(draft).WaitAsync(TimeSpan.FromSeconds(15));
        await host.EnqueueTagQueueAsync(Items(1, 6));
        for (int n = 1; n <= 6; n++) await host.DecideTagQueueAsync(Item(n).Key, TagQueueState.Done);
        Assert.Equal(5, host.TagQueueUndoDepth);
        for (int n = 6; n >= 2; n--) Assert.Equal(Item(n).Key, (await host.UndoTagQueueDecisionAsync())!.Key);
        Assert.Null(await host.UndoTagQueueDecisionAsync()); // the sixth: nothing more
        Assert.Equal(TagQueueState.Done, host.GetTagQueue()!.Items[0].State);
    }

    // W3-21: an unreadable file sets the read error by its type.
    [Fact]
    public async Task AFileThatCannotBeOpenedIsReportedAsUnreadable()
    {
        if (OperatingSystem.IsWindows()) return;
        using TempDirectory temp = new();
        await using DesktopSessionHost host = await StartAsync(temp.Path);
        await host.EnqueueTagQueueAsync(Items(1, 1));
        string file = Path.Combine(temp.Path, TagQueueStore.FileName);
        File.SetUnixFileMode(file, UnixFileMode.None);
        try
        {
            try { using (File.OpenRead(file)) { } return; } // rights that ignore the mode: nothing to check
            catch (UnauthorizedAccessException) { }
            await Assert.ThrowsAsync<TagQueueReadException>(() => host.LoadTagQueueAsync());
            Assert.IsType<TagQueueReadException>(host.TagQueueError);
        }
        finally { File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
    }

    [Fact]
    public async Task ASettingChangeAppliesToTheNextAddAndNeverDropsEntries()
    {
        using TempDirectory temp = new();
        await using DesktopSessionHost host = await StartAsync(temp.Path);
        host.StoreLimits.TagQueueEntries = ProfileStoreLimits.MinimumTagQueueEntries;
        TagQueueAddResult result = await host.EnqueueTagQueueAsync(Items(1, 55));
        Assert.Equal((50, 5), (result.Added, result.OverLimit));
        string sentence = TagQueueActions.Describe(result, 0);
        Assert.Equal(Texts.T("queue.result.limit", 50, 0, 5, Texts.T("settings.limits.tagQueue"), Texts.T("settings.storageLimits")), sentence);
        Assert.Contains(Texts.T("settings.limits.tagQueue"), sentence, StringComparison.Ordinal);
        host.StoreLimits.TagQueueEntries = ProfileStoreLimits.MinimumTagQueueEntries;
        Assert.Equal(50, host.GetTagQueue()!.Count);
    }

    [Fact]
    public async Task AnUnreadableFileIsReportedNotEmptiedAndCanBeClearedExplicitly()
    {
        using TempDirectory temp = new();
        string file = Path.Combine(await ProfileAsync(temp), TagQueueStore.FileName);
        await File.WriteAllTextAsync(file, "damaged");
        await using DesktopSessionHost host = await StartAsync(temp.Path);
        await Assert.ThrowsAsync<InvalidDataException>(() => host.LoadTagQueueAsync());
        Assert.Throws<InvalidDataException>(() => host.GetTagQueue());
        Assert.NotNull(host.TagQueueError);
        await Assert.ThrowsAsync<InvalidDataException>(() => host.EnqueueTagQueueAsync(Items(1, 1)));
        Assert.Equal("damaged", await File.ReadAllTextAsync(file));
        string sentence = await TagQueueActions.AddAsync(host, Items(1, 1), 0);
        Assert.Equal(Texts.T("queue.error.unreadable"), sentence);

        await host.ClearTagQueueAsync();
        Assert.Null(host.TagQueueError);
        Assert.Equal(0, host.GetTagQueue()!.Count);
        Assert.Contains(Directory.GetFiles(temp.Path, "tag-queue*"), path => File.ReadAllText(path) == "damaged");
    }

    [Fact]
    public async Task ADogfoodQueueRemainsUntouchedUntilTheHostWritesVersionTwo()
    {
        using TempDirectory temp = new();
        string path = Path.Combine(await ProfileAsync(temp), TagQueueStore.FileName);
        const string original = """{"Version":3,"Revision":7,"Cursor":0,"Stamp":["cat"],"Groups":[],"Items":[]}""";
        await File.WriteAllTextAsync(path, original);
        await using DesktopSessionHost host = await StartAsync(temp.Path);
        Assert.Equal(new[] { "cat" }, (await host.LoadTagQueueAsync()).Stamp);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.False(File.Exists(path + ".bak"));

        Assert.Equal(1, (await host.EnqueueTagQueueAsync(Items(1, 1))).Added);
        Assert.Contains("\"Version\":2", await File.ReadAllTextAsync(path));
        Assert.Contains("\"Version\":2", await File.ReadAllTextAsync(path + ".bak"));
    }

    [Fact]
    public async Task AFileOfANewerVersionIsShownEmptyAndEveryAddSaysSoWithoutWriting()
    {
        using TempDirectory temp = new();
        string file = Path.Combine(await ProfileAsync(temp), TagQueueStore.FileName);
        await File.WriteAllTextAsync(file, "{\"Version\":4}");
        await using DesktopSessionHost host = await StartAsync(temp.Path);
        TagQueueSnapshot snapshot = await host.LoadTagQueueAsync();
        Assert.Equal((TagQueueLoadState.NewerVersion, 0), (snapshot.State, snapshot.Count));
        Assert.Equal(Texts.T("queue.error.newer"), await TagQueueActions.AddAsync(host, Items(1, 1), 0));
        Assert.Equal("{\"Version\":4}", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task AFullFileSaysWhichSettingToRaiseAndWritesNothing()
    {
        using TempDirectory temp = new();
        await using DesktopSessionHost host = await StartAsync(temp.Path);
        host.StoreLimits.TagQueueFileBytes = ProfileStoreLimits.MinimumTagQueueFileBytes;
        string directory = "/" + new string('d', 1_000);
        TagQueueItem Big(int n) => Item(n) with { Directory = directory, Name = new string('n', 200) };
        await host.EnqueueTagQueueAsync(new[] { Big(0) });
        byte[] before = await File.ReadAllBytesAsync(Path.Combine(temp.Path, TagQueueStore.FileName));
        TagQueueItem[] many = Enumerable.Range(1, 900).Select(Big).ToArray();
        InvalidDataException refusal = await Assert.ThrowsAsync<InvalidDataException>(() => host.EnqueueTagQueueAsync(many));
        // The host localizes the refusal; the store's own error stays inner.
        Assert.True(PrivateStoreLimit.TryDescribe(refusal.InnerException, out PrivateStoreLimitKind kind, out _), refusal.Message);
        Assert.Equal(PrivateStoreLimitKind.TagQueueFile, kind);
        string sentence = await TagQueueActions.AddAsync(host, many, 0);
        Assert.Contains(Texts.T("settings.limits.tagQueueFile"), sentence, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(temp.Path, TagQueueStore.FileName)));
    }

    [Fact]
    public void TheSentenceCountsEveryInputExactlyOnce()
    {
        TagQueueSnapshot snapshot = TagQueueSnapshot.Empty;
        Assert.Equal(Texts.T("queue.result", 3, 2), TagQueueActions.Describe(new TagQueueAddResult(snapshot, 3, 2, 0), 0));
        Assert.Equal(Texts.T("queue.result", 3, 2) + " " + Texts.T("queue.result.skipped", 4), TagQueueActions.Describe(new TagQueueAddResult(snapshot, 3, 2, 0), 4));
        Assert.Equal(Texts.T("queue.result.none"), TagQueueActions.Describe(null, 0));
        Assert.Equal(Texts.T("queue.result.none") + " " + Texts.T("queue.result.skipped", 2), TagQueueActions.Describe(null, 2));
    }

    [Fact]
    public void OnlySavedNonProviderMediaBecomeQueueEntries()
    {
        MediaLibraryEntry present = Item(1).ToLibraryEntry();
        MediaLibraryEntry missing = present with { Presence = MediaLibraryPresence.Missing };
        MediaLibraryEntry provider = Item(2).ToLibraryEntry() with { BooruPostId = "99" };
        (IReadOnlyList<TagQueueItem> items, int skipped) = TagQueueActions.ItemsOf(new[] { present, missing, provider }, T0);
        Assert.Equal((1, 2), (items.Count, skipped));
        Assert.Equal(Item(1).Key, items[0].Key);
        Assert.Equal(TagQueueState.Open, items[0].State);
    }

    [Fact]
    public void TheMenuReasonNamesTheCauseAndIsNullForQueueableMedia()
    {
        Assert.Null(TagQueueActions.BlockedReason(saved: true, providerMedia: false));
        Assert.Equal(Texts.T("media.menu.reason.notSaved"), TagQueueActions.BlockedReason(saved: false, providerMedia: false));
        Assert.Equal(Texts.T("booru.media.tags.unavailable"), TagQueueActions.BlockedReason(saved: true, providerMedia: true));
    }

    [Fact]
    public async Task ConcurrentAddsAreSerializedAndNeverLoseOrDuplicateEntries()
    {
        using TempDirectory temp = new();
        await using DesktopSessionHost host = await StartAsync(temp.Path);
        Task<TagQueueAddResult>[] adds = Enumerable.Range(0, 8).Select(slice => host.EnqueueTagQueueAsync(Items(slice * 10, 15))).ToArray();
        await Task.WhenAll(adds);
        TagQueueSnapshot queue = await host.LoadTagQueueAsync();
        Assert.Equal(queue.Items.Select(item => item.Key).Distinct().Count(), queue.Count);
        Assert.Equal(Enumerable.Range(0, 85).Count(), queue.Count);
        Assert.Equal(adds.Sum(add => add.Result.Added), queue.Count);
    }

    [Fact]
    public void TheFourSettingsHaveDefaultMinimumAndMaximumAndAreValidated()
    {
        Config config = new();
        Assert.Equal((2_000, 8, 500, 200), (config.TagQueueLimit, config.TagQueueFileLimitMiB, config.TagQueueHistory, config.TagQueueConfirmAbove));
        Assert.Equal((50, 50_000), (Config.TagQueueLimitMinimum, Config.TagQueueLimitMaximum));
        Assert.Equal((1, 64), (Config.TagQueueFileLimitMiBMinimum, Config.TagQueueFileLimitMiBMaximum));
        Assert.Equal((0, 20_000), (Config.TagQueueHistoryMinimum, Config.TagQueueHistoryMaximum));
        Assert.Equal((10, 10_000), (Config.TagQueueConfirmAboveMinimum, Config.TagQueueConfirmAboveMaximum));
        config.Validate();
        foreach (Action<Config> breakIt in new Action<Config>[]
        {
            c => c.TagQueueLimit = Config.TagQueueLimitMinimum - 1, c => c.TagQueueLimit = Config.TagQueueLimitMaximum + 1,
            c => c.TagQueueFileLimitMiB = 0, c => c.TagQueueFileLimitMiB = Config.TagQueueFileLimitMiBMaximum + 1,
            c => c.TagQueueHistory = -1, c => c.TagQueueHistory = Config.TagQueueHistoryMaximum + 1,
            c => c.TagQueueConfirmAbove = 9, c => c.TagQueueConfirmAbove = Config.TagQueueConfirmAboveMaximum + 1,
        })
        {
            Config bad = new();
            breakIt(bad);
            Assert.Throws<ArgumentOutOfRangeException>(bad.Validate);
        }
    }
}
