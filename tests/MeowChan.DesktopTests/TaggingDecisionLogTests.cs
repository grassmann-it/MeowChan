using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Storage;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

// PT3 (PLAN-POWER-TAGGEN-2026-11-14 §1.4, §1.7): the tagging mode's model without UI. The walk pairs the shown gallery list with the queue,
// decides at once and writes in order through the real session host, picks the next open medium, counts the progress, and takes back
// its decisions newest first (state and resume point); one undo key covers queue decisions and favorite/rating steps in their shared order.
[Collection("Localization")]
public sealed class TaggingDecisionLogTests : IDisposable
{
    public TaggingDecisionLogTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static readonly ResourceId Thread = new("source", "board", "42");
    private static readonly DateTimeOffset T0 = new(2026, 11, 14, 8, 0, 0, TimeSpan.Zero);
    private const string Folder = "/media/42";

    private static TagQueueItem Item(int n) => new(Thread, Folder, "asset-" + n, new string((char)('a' + n % 6), 64), 100 + n, "media-" + n,
        "original", null, null, null, null, false, null, "file-" + n + ".png", SavedMediaKind.Image, TagQueueState.Open, T0.AddSeconds(n));

    private static GalleryEntry Entry(int n, string folder = Folder)
    {
        MediaAssetReference asset = new("asset-" + n, "media-" + n, "original", "file-" + n + ".png", Path.Combine(folder, "file-" + n + ".png"), 100 + n,
            new string((char)('a' + n % 6), 64), "https://offline.invalid/" + n, "file-" + n + ".png", true);
        MediaAssetSnapshot snapshot = new(folder, MediaManifestStatus.Available, 3, "fixture", [asset]);
        return new GalleryEntry(new GalleryAssetKey(Thread, folder, asset.AssetId), snapshot, asset, null, null, false, false);
    }

    private static async Task<DesktopSessionHost> StartAsync(string directory)
    {
        DesktopSessionHost host = new(directory, Array.Empty<IImageBoard>());
        await host.StartAsync();
        return host;
    }

    // A host with a queue of `count` open entries and a walk over all of them (every file resolvable), as deep as the host's setting.
    private static async Task<(DesktopSessionHost Host, TaggingWalk Walk)> WalkAsync(string directory, int count, int? undoDepth = null)
    {
        if (undoDepth is { } depth)
            new Config { AutomaticUpdateChecks = false, SaveUrlsOnExit = false, DownloadPath = Path.Combine(directory, "media"), TagQueueUndoDepth = depth }.SaveConfig(directory);
        DesktopSessionHost host = await StartAsync(directory);
        TagQueueItem[] items = Enumerable.Range(1, count).Select(Item).ToArray();
        await host.EnqueueTagQueueAsync(items);
        TagQueueSnapshot queue = await host.LoadTagQueueAsync();
        (IReadOnlyList<TaggingWalkItem> paired, int unavailable) = TaggingWalk.Pair(queue.Items, items.Select((_, i) => Entry(i + 1)).ToArray());
        return (host, new TaggingWalk(paired, unavailable, queue, new HostTaggingQueueWriter(host), TaggingSettings.UndoDepth(host.Config)));
    }

    // ---- pairing ----------------------------------------------------------------------------------------------------

    [Fact]
    public void TheResolvedListIsPairedInOrderAndAQueueEntryWithoutAFileIsCountedNotDropped()
    {
        TagQueueItem[] queued = Enumerable.Range(1, 4).Select(Item).ToArray();
        GalleryEntry[] resolved = { Entry(1), Entry(2), Entry(4) }; // the file of entry 3 is gone
        (IReadOnlyList<TaggingWalkItem> items, int unavailable) = TaggingWalk.Pair(queued, resolved);

        Assert.Equal(1, unavailable);
        Assert.Equal(new[] { "asset-1", "asset-2", "asset-4" }, items.Select(item => item.Key.AssetId));
        Assert.Equal(resolved.Select(entry => entry.EntryKey), items.Select(item => item.Entry));
        Assert.Equal("file-4.png", items[2].Name);
        // Another media folder is another medium: it never pairs with a queue entry of the same asset id.
        Assert.False(TaggingWalk.Matches(queued[0], Entry(1, "/media/other")));
    }

    // ---- decisions ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DoneCountsAtOnceGoesToTheNextOpenMediumAndIsDurableWithTheResumePointBehindIt()
    {
        using TempDirectory temp = new();
        (DesktopSessionHost host, TaggingWalk walk) = await WalkAsync(temp.Path, 3);
        await using (host)
        using (walk)
        {
            TaggingDecisionResult result = walk.Decide(0, TagQueueState.Done);
            Assert.Equal(TaggingDecisionOutcome.Decided, result.Outcome);
            Assert.Equal(1, result.Next);
            // Before the write ends the walk already shows the decision.
            Assert.Equal(TagQueueState.Done, walk.StateOf(0));
            Assert.Equal((1, 0, 2), walk.Progress());
            Assert.Equal("Done: “file-1.png”. Next: “file-2.png”.", TaggingModeText.Decided(walk, result, TagQueueState.Done));

            Assert.Equal(TaggingWriteKind.Saved, (await result.Write).Kind);
            TagQueueSnapshot saved = host.GetTagQueue()!;
            Assert.Equal(TagQueueState.Done, saved.Items[0].State);
            Assert.Equal(1, saved.Cursor); // "continue" starts behind the finished medium
            Assert.Equal(1, walk.Steps);
            Assert.Equal("Queue 2 of 3", TaggingModeText.Position(walk, 1));
            Assert.Equal("Done: 1 · Skipped: 0 · Open: 2", TaggingModeText.Progress(walk));
        }
        // After a restart the queue resumes at the next open entry.
        await using DesktopSessionHost again = await StartAsync(temp.Path);
        Assert.Equal(1, (await again.LoadTagQueueAsync()).ResumeIndex());
    }

    [Fact]
    public async Task SkipAndDoneLeaveOnlyOpenMediaNextWrapAroundAndSayWhenTheWalkIsThrough()
    {
        using TempDirectory temp = new();
        (DesktopSessionHost host, TaggingWalk walk) = await WalkAsync(temp.Path, 3);
        await using (host)
        using (walk)
        {
            Assert.Equal(TaggingWriteKind.Saved, (await walk.Decide(1, TagQueueState.Skipped).Write).Kind);
            // From the last medium the next open one lies before it.
            TaggingDecisionResult last = walk.Decide(2, TagQueueState.Done);
            Assert.Equal(0, last.Next);
            await last.Write;
            TaggingDecisionResult through = walk.Decide(0, TagQueueState.Done);
            Assert.Null(through.Next);
            Assert.Equal("Done: “file-1.png”. Every medium of this walk is decided.", TaggingModeText.Decided(walk, through, TagQueueState.Done));
            await through.Write;
            Assert.Equal((2, 1, 0), walk.Progress());
            Assert.Null(walk.NextOpen(0));
            Assert.Equal(3, walk.Steps);
        }
    }

    [Fact]
    public async Task UndoTakesBackTheNewestDecisionFirstWithStateAndResumePointAndNamesItsMedium()
    {
        using TempDirectory temp = new();
        (DesktopSessionHost host, TaggingWalk walk) = await WalkAsync(temp.Path, 3);
        await using (host)
        using (walk)
        {
            await walk.Decide(0, TagQueueState.Done).Write;
            await walk.Decide(1, TagQueueState.Skipped).Write;
            Assert.Equal(2, host.GetTagQueue()!.Cursor);

            TaggingUndoResult? undone = await walk.UndoAsync();
            Assert.NotNull(undone);
            Assert.Equal((1, "file-2.png", TagQueueState.Open), (undone!.Index, undone.Name, undone.State));
            Assert.Equal(TagQueueState.Open, walk.StateOf(1));
            Assert.Equal(1, host.GetTagQueue()!.Cursor); // the resume point is back on the medium
            Assert.Equal("Taken back: “file-2.png” is open again.", TaggingModeText.Undone(undone));

            Assert.Equal(0, (await walk.UndoAsync())!.Index);
            Assert.Equal(TagQueueState.Open, host.GetTagQueue()!.Items[0].State);
            Assert.Null(await walk.UndoAsync()); // nothing left
            Assert.Equal(0, walk.Steps);
        }
    }

    [Fact]
    public async Task ARefusedWriteShowsTheStoredStateAgainAndKeepsNoStepAndAMissingEntryIsReported()
    {
        FakeWriter writer = new() { Fail = true };
        TagQueueItem[] items = Enumerable.Range(1, 2).Select(Item).ToArray();
        TagQueueSnapshot queue = new(1, 0, Array.Empty<string>(), items, TagQueueLoadState.Ready);
        writer.Snapshot = queue;
        (IReadOnlyList<TaggingWalkItem> paired, _) = TaggingWalk.Pair(items, new[] { Entry(1), Entry(2) });
        using TaggingWalk walk = new(paired, 0, queue, writer);

        TaggingDecisionResult refused = walk.Decide(0, TagQueueState.Done);
        TaggingWriteResult written = await refused.Write;
        Assert.Equal(TaggingWriteKind.Failed, written.Kind);
        Assert.False(string.IsNullOrWhiteSpace(written.Message));
        Assert.Equal(TagQueueState.Open, walk.StateOf(0)); // what the store kept
        Assert.Equal(0, walk.Steps);

        writer.Fail = false;
        writer.Missing = true;
        Assert.Equal(TaggingWriteKind.Missing, (await walk.Decide(1, TagQueueState.Done).Write).Kind);
        Assert.Equal(0, walk.Steps);
        Assert.Equal(TaggingDecisionOutcome.NotInWalk, walk.Decide(5, TagQueueState.Done).Outcome);
    }

    [Fact]
    public async Task TheUndoStackIsAsDeepAsTheHostsDecisionLog()
    {
        FakeWriter writer = new();
        TagQueueItem[] items = Enumerable.Range(1, 5).Select(Item).ToArray();
        TagQueueSnapshot queue = new(1, 0, Array.Empty<string>(), items, TagQueueLoadState.Ready);
        writer.Snapshot = queue;
        (IReadOnlyList<TaggingWalkItem> paired, _) = TaggingWalk.Pair(items, Enumerable.Range(1, 5).Select(n => Entry(n)).ToArray());
        using TaggingWalk walk = new(paired, 0, queue, writer, depth: 2);
        for (int i = 0; i < 5; i++) await walk.Decide(i, TagQueueState.Done).Write;
        Assert.Equal(2, walk.Steps);
        Assert.Equal(TagQueueDecisionLog.DefaultDepth, new TaggingWalk(paired, 0, queue, writer).Depth);
    }

    // W3-01 (a): an entry removed on the tagging page during the walk: its step goes, the next undo takes back the newest decision that
    // still exists (and names it), and no other entry is ever taken back in its place.
    [Fact]
    public async Task AnEntryRemovedDuringTheWalkDropsItsStepAndTheUndoTakesBackTheRightEntry()
    {
        using TempDirectory temp = new();
        (DesktopSessionHost host, TaggingWalk walk) = await WalkAsync(temp.Path, 3);
        await using (host)
        using (walk)
        {
            await walk.Decide(0, TagQueueState.Done).Write;
            await walk.Decide(1, TagQueueState.Done).Write;
            await host.RemoveFromTagQueueAsync(new[] { walk.Items[1].Key });
            Assert.Equal(1, host.TagQueueUndoDepth);

            TaggingUndoResult? undone = await walk.UndoAsync();
            Assert.Equal((0, "file-1.png", TagQueueState.Open), (undone!.Index, undone.Name, undone.State));
            TagQueueSnapshot saved = host.GetTagQueue()!;
            Assert.Equal(2, saved.Count);
            Assert.Equal(TagQueueState.Open, saved.Items[0].State);
            Assert.Equal((0, 0), (walk.Steps, host.TagQueueUndoDepth));
            Assert.Null(await walk.UndoAsync());
        }
    }

    [Fact]
    public async Task AStepWhoseEntryLeftTheQueueIsNotTheNewestStepOfTheSharedUndoKey()
    {
        using TempDirectory temp = new();
        (DesktopSessionHost host, TaggingWalk walk) = await WalkAsync(temp.Path, 3);
        await using (host)
        using (walk)
        using (GalleryDecisionSession decisions = new(new DecisionOwner()))
        {
            using TaggingUndo undo = new(walk, decisions, () => 0, _ => { });
            await decisions.SetRatingAsync(new[] { Entry(1) }, 4);   // older: a rating
            await walk.Decide(1, TagQueueState.Done).Write;           // newer: medium 2 is done, then removed on the tagging page
            await host.RemoveFromTagQueueAsync(new[] { walk.Items[1].Key });
            Assert.Equal("Undone: rating change.", await undo.UndoAsync());
            Assert.Equal(0, undo.Steps);
        }
    }

    // W3-01 (b): a refused undo write keeps the step and the host's decision; the next undo takes back the same entry.
    [Fact]
    public async Task ARefusedUndoKeepsBothStacksAndTheNextUndoTakesBackTheSameEntry()
    {
        using TempDirectory temp = new();
        (DesktopSessionHost host, TaggingWalk walk) = await WalkAsync(temp.Path, 3);
        await using (host)
        using (walk)
        {
            await walk.Decide(0, TagQueueState.Done).Write;
            await walk.Decide(1, TagQueueState.Skipped).Write;
            string backup = Path.Combine(temp.Path, TagQueueStore.FileName + ".bak");
            File.WriteAllText(backup, "{\"Version\":99}");

            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => walk.UndoAsync());
            Assert.Equal((2, 2), (walk.Steps, host.TagQueueUndoDepth));
            Assert.False(walk.Busy);

            File.Delete(backup);
            Assert.Equal(1, (await walk.UndoAsync())!.Index);
            Assert.Equal(TagQueueState.Done, host.GetTagQueue()!.Items[0].State);
            Assert.Equal(0, (await walk.UndoAsync())!.Index);
            Assert.Equal((0, 0), (walk.Steps, host.TagQueueUndoDepth));
        }
    }

    // W3-18: with the setting at 5, six decisions leave five undos; the sixth says there is nothing more.
    [Fact]
    public async Task TheUndoDepthSettingLimitsTheWalkAndTheHostAlike()
    {
        using TempDirectory temp = new();
        (DesktopSessionHost host, TaggingWalk walk) = await WalkAsync(temp.Path, 6, undoDepth: 5);
        await using (host)
        using (walk)
        {
            Assert.Equal(5, walk.Depth);
            for (int i = 0; i < 6; i++) await walk.Decide(i, TagQueueState.Done).Write;
            Assert.Equal((5, 5), (walk.Steps, host.TagQueueUndoDepth));
            for (int i = 5; i >= 1; i--) Assert.Equal(i, (await walk.UndoAsync())!.Index);
            Assert.Null(await walk.UndoAsync());
            Assert.Equal(TagQueueState.Done, host.GetTagQueue()!.Items[0].State);
        }
    }

    // ---- one undo key over queue decisions and favorite/rating steps ----------------------------------------------

    [Fact]
    public async Task TheNewestOfQueueDecisionAndFavoriteOrRatingStepIsUndoneFirstAndAQueueUndoReturnsToItsMedium()
    {
        using TempDirectory temp = new();
        (DesktopSessionHost host, TaggingWalk walk) = await WalkAsync(temp.Path, 3);
        await using (host)
        using (walk)
        using (GalleryDecisionSession decisions = new(new DecisionOwner()))
        {
            List<TaggingUndoResult> returned = new();
            long tagStamp = 0;
            using TaggingUndo undo = new(walk, decisions, () => tagStamp, returned.Add);
            Assert.False(undo.IsNewest);

            await decisions.SetRatingAsync(new[] { Entry(1) }, 4);   // older: the rating of medium 1
            await walk.Decide(0, TagQueueState.Done).Write;           // newer: medium 1 is done
            Assert.Equal(2, undo.Steps);
            Assert.True(undo.IsNewest);
            Assert.True(undo.QueueIsNewest);

            Assert.Equal("Taken back: “file-1.png” is open again.", await undo.UndoAsync());
            Assert.Equal(0, Assert.Single(returned).Index);
            Assert.Equal(TagQueueState.Open, walk.StateOf(0));
            Assert.False(undo.QueueIsNewest);
            Assert.Equal("Undone: rating change.", await undo.UndoAsync());
            Assert.Equal(0, undo.Steps);

            // A tag step newer than everything here goes first (the surface undoes it before asking this stack).
            await walk.Decide(1, TagQueueState.Done).Write;
            tagStamp = GalleryDecisionSession.NextStamp();
            Assert.False(undo.IsNewest);
        }
    }

    [Fact]
    public void AGroupWalkUsesTheGroupPositionProgressAndAnnouncement()
    {
        TagQueueGroup group = new("group", "Review", 0, new[] { "common" });
        TagQueueItem[] items = { Item(1) with { State = TagQueueState.Done, DoneAtUtc = T0 }, Item(2) with { GroupId = group.Id }, Item(3) };
        TagQueueSnapshot queue = new(1, 1, Array.Empty<string>(), items, TagQueueLoadState.Ready, new[] { group });
        (IReadOnlyList<TaggingWalkItem> paired, int unavailable) = TaggingWalk.Pair(new[] { items[1] }, new[] { Entry(2) });
        using TaggingWalk walk = new(paired, unavailable, queue, new FakeWriter(), groupId: group.Id, groupName: group.Name);
        Assert.Equal((0, 0, 1), walk.Progress());
        Assert.Equal((1, 1), walk.QueuePosition(0));
        Assert.Equal(0, walk.OpenOutsideWalk());
        Assert.Contains("Group “Review”.", TaggingModeText.Start(walk));
    }

    // ---- texts --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheStartLineNamesTheEffectiveKeysAndTheMediaThatCouldNotBeShown()
    {
        FakeWriter writer = new();
        TagQueueItem[] items = Enumerable.Range(1, 3).Select(Item).ToArray();
        TagQueueSnapshot queue = new(1, 0, Array.Empty<string>(), items, TagQueueLoadState.Ready);
        (IReadOnlyList<TaggingWalkItem> paired, int unavailable) = TaggingWalk.Pair(items, new[] { Entry(1) });
        using TaggingWalk walk = new(paired, unavailable, queue, writer);
        Assert.Equal("Tagging mode: Enter finishes the medium, Space skips it, Backspace takes back the last decision. "
            + "Not shown because the file is missing right now (they stay open in the queue): 2.", TaggingModeText.Start(walk));
        Assert.Equal(2, walk.OpenOutsideWalk());
        Texts.SetLanguage("de");
        Assert.StartsWith("Tagging-Modus: Enter schließt das Medium ab", TaggingModeText.Start(walk));
        Assert.Equal("Warteschlange 1 von 3", TaggingModeText.Position(walk, 0));
        await Task.CompletedTask;
    }

    private sealed class FakeWriter : ITaggingQueueWriter
    {
        internal bool Fail, Missing;
        internal TagQueueSnapshot? Snapshot;
        public Task<TagQueueDecision?> DecideAsync(TagQueueKey key, TagQueueState state, CancellationToken token)
        {
            if (Fail) throw new IOException("The tagging queue could not be written.");
            if (Missing) return Task.FromResult<TagQueueDecision?>(null);
            return Task.FromResult<TagQueueDecision?>(new TagQueueDecision(key, TagQueueState.Open, state, 0, 1));
        }
        public Task<TagQueueDecision?> UndoAsync(TagQueueDecision decision, CancellationToken token) => Task.FromResult<TagQueueDecision?>(null);
        public TagQueueSnapshot? Current => Snapshot;
    }

    private sealed class DecisionOwner : IGalleryDecisionOwner
    {
        private readonly Dictionary<GalleryEntryKey, GalleryDecisionState> _states = new();
        public GalleryDecisionState? Read(GalleryEntry entry) => _states.GetValueOrDefault(entry.EntryKey);
        public Task SetFavoriteAsync(GalleryEntry entry, bool selected, CancellationToken token)
        { _states[entry.EntryKey] = _states.GetValueOrDefault(entry.EntryKey) with { Favorite = selected }; return Task.CompletedTask; }
        public Task SetRatingAsync(GalleryEntry entry, int? value, CancellationToken token)
        { _states[entry.EntryKey] = _states.GetValueOrDefault(entry.EntryKey) with { Rating = value }; return Task.CompletedTask; }
    }
}
