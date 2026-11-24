using System.Reflection;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Persistence;
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
    private static async Task<(DesktopSessionHost Host, TaggingWalk Walk)> WalkAsync(string directory, int count, int? undoDepth = null,
        Func<ITaggingQueueWriter, ITaggingQueueWriter>? wrapWriter = null)
    {
        if (undoDepth is { } depth)
            new Config { AutomaticUpdateChecks = false, SaveUrlsOnExit = false, DownloadPath = Path.Combine(directory, "media"), TagQueueUndoDepth = depth }.SaveConfig(directory);
        DesktopSessionHost host = await StartAsync(directory);
        TagQueueItem[] items = Enumerable.Range(1, count).Select(Item).ToArray();
        await host.EnqueueTagQueueAsync(items);
        TagQueueSnapshot queue = await host.LoadTagQueueAsync();
        (IReadOnlyList<TaggingWalkItem> paired, int unavailable) = TaggingWalk.Pair(queue.Items, items.Select((_, i) => Entry(i + 1)).ToArray());
        ITaggingQueueWriter writer = new HostTaggingQueueWriter(host);
        return (host, new TaggingWalk(paired, unavailable, queue, wrapWriter?.Invoke(writer) ?? writer, TaggingSettings.UndoDepth(host.Config)));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DoneCountsAtOnceGoesToTheNextOpenMediumAndIsDurableWithTheResumePointBehindIt(bool holdAcrossYield)
    {
        using TempDirectory temp = new();
        PausedHostWriter writer = null!;
        (DesktopSessionHost host, TaggingWalk walk) = await WalkAsync(temp.Path, 3,
            wrapWriter: real => writer = new PausedHostWriter(real));
        await using (host)
        using (walk)
        {
            TaggingDecisionResult result = walk.Decide(0, TagQueueState.Done);
            try
            {
                await writer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (holdAcrossYield) await Task.Yield();
                // No host decision has started: these checks must observe the pending
                // optimistic state, not race the UI-thread-only walk's completion.
                Assert.False(result.Write.IsCompleted);
                Assert.Equal(TagQueueState.Open, host.GetTagQueue()!.Items[0].State);
                Assert.Equal(0, host.GetTagQueue()!.Cursor);
                Assert.Equal(TaggingDecisionOutcome.Decided, result.Outcome);
                Assert.Equal(1, result.Next);
                Assert.Equal(TagQueueState.Done, walk.StateOf(0));
                Assert.Equal((1, 0, 2), walk.Progress());
                Assert.Equal("Done: “file-1.png”. Next: “file-2.png”.", TaggingModeText.Decided(walk, result, TagQueueState.Done));
                Assert.False(result.Write.IsCompleted);
            }
            finally
            {
                // Also release/drain on assertion failure before disposing walk/host.
                writer.Release();
                await result.Write;
            }
            Assert.Equal(TaggingWriteKind.Saved, (await result.Write).Kind);
            TagQueueSnapshot saved = host.GetTagQueue()!;
            Assert.Equal(TagQueueState.Done, saved.Items[0].State);
            Assert.Equal(1, saved.Cursor); // "continue" starts behind the finished medium
            Assert.Equal(1, walk.Steps);
            Assert.Equal("Queue 2 of 3", TaggingModeText.Position(walk, 1));
            Assert.Equal("Open: 2 · Done: 1 · Skipped: 0", TaggingModeText.Progress(walk));
        }
        // After a restart the queue resumes at the next open entry.
        await using DesktopSessionHost again = await StartAsync(temp.Path);
        Assert.Equal(1, (await again.LoadTagQueueAsync()).ResumeIndex());
    }

    [Fact]
    public async Task ClosingTheWalkStillDrainsBothAdmittedWrites()
    {
        using TempDirectory temp = new();
        PausedHostWriter writer = null!;
        (DesktopSessionHost host, TaggingWalk walk) = await WalkAsync(temp.Path, 2,
            wrapWriter: real => writer = new PausedHostWriter(real));
        await using (host)
        using (walk)
        {
            Task<TaggingWriteResult> first = walk.Decide(0, TagQueueState.Done).Write;
            Task<TaggingWriteResult> second = walk.Decide(1, TagQueueState.Skipped).Write;
            try
            {
                Assert.True(walk.Busy);
                walk.Dispose();
                Assert.Null(await walk.UndoAsync());
            }
            finally
            {
                writer.Release();
                await Task.WhenAll(first, second);
            }
            Assert.Equal(TaggingWriteKind.Saved, (await first).Kind);
            Assert.Equal(TaggingWriteKind.Saved, (await second).Kind);
            Assert.Equal(new[] { TagQueueState.Done, TagQueueState.Skipped }, host.GetTagQueue()!.Items.Select(item => item.State));
        }
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
        using TaggingWalk defaults = new(paired, 0, queue, writer);
        Assert.Equal(TagQueueDecisionLog.DefaultDepth, defaults.Depth);
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
    public void BackButtonAndCardDescribeTheActionThatActuallyRuns() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        using TempDirectory queueProfile = new();
        using TempDirectory tagProfile = new();
        using ProfileLease lease = ProfileLease.Acquire(tagProfile.Path);
        CardProfileOwner owner = new(GalleryRatingKeyTests.LayoutOwner(new PrivateTagStore(tagProfile.Path, lease)));
        GalleryEntry shown = owner.Entries[0];
        TagQueueItem[] items = owner.Entries.Select((entry, index) => Item(index + 1) with
        {
            Thread = entry.Key.Thread, Directory = entry.Manifest.Directory, AssetId = entry.Asset.AssetId, PostId = entry.Post?.PostId
        }).ToArray();
        BooruComponentDispatcher.Wait(ExerciseAsync());

        async Task ExerciseAsync()
        {
            await using DesktopSessionHost host = await StartAsync(queueProfile.Path);
            await host.EnqueueTagQueueAsync(items);
            TagQueueSnapshot queue = await host.LoadTagQueueAsync();
            var (paired, unavailable) = TaggingWalk.Pair(queue.Items, owner.Entries);
            Assert.Equal(owner.Entries.Count, paired.Count);
            PausedHostWriter writer = new(new HostTaggingQueueWriter(host));
            using TaggingWalk walk = new(paired, unavailable, queue, writer);
            await using GalleryViewer viewer = new();
            typeof(GalleryViewer).GetField("_entry", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewer, shown);
            viewer.SetPosition("1 / 3", false, true);
            Window window = new() { Width = 1100, Height = 720, Content = viewer };
            window.Show();
            try
            {
                viewer.StartTaggingMode(walk, owner);
                Assert.Equal(new[] { "GalleryTaggingBack", "GalleryTaggingSkip", "GalleryTaggingFavorite", "GalleryTaggingStamp", "GalleryTaggingDone" },
                    Assert.IsType<WrapPanel>(viewer.TaggingBackButton.Parent).Children.Select(child => child.Name));
                Check("command.gallery.previous", enabled: false);
                GalleryTagPanelController controller = viewer.TagPanelController!;
                await controller.Session.AddAsync("tagging-flow-test");
                Check("command.tags.quick.undo");
                ClickBack();
                await BooruTestSupport.WaitAsync(() => controller.Session.UndoSteps.Count == 0 && !controller.Session.Busy,
                    "the real tag step is undone");

                await controller.Decisions!.SetRatingAsync([shown], 5);
                Check("tagging.back.undoRating");
                ClickBack();
                await BooruTestSupport.WaitAsync(() => controller.Decisions.UndoSteps.Count == 0 && !controller.Decisions.Busy,
                    "the real rating step is undone");
                Assert.Equal(3, owner.Read(shown)?.Rating);

                await controller.Decisions.SetFavoriteAsync([shown], true);
                Check("tagging.back.undoFavorite");
                ClickBack();
                await BooruTestSupport.WaitAsync(() => controller.Decisions.UndoSteps.Count == 0 && !controller.Decisions.Busy,
                    "the favorite step is undone");
                Assert.False(owner.Read(shown)?.Favorite);

                Task<TaggingWriteResult> pending = walk.Decide(0, TagQueueState.Skipped).Write;
                try
                {
                    await writer.Entered.Task;
                    Check("gallery.saving", enabled: false);
                    ClickBack();
                    Assert.Equal(TagQueueState.Open, host.GetTagQueue()!.Items[0].State);
                }
                finally
                {
                    writer.Release();
                    await pending;
                }
                Check("tagging.back.undoSkip");
                viewer.SetCardMode(true, persist: false);
                Assert.Equal(new[] { "done", "back", "rate-4", "skip" }, viewer.TaggingCards.Select(card => card.Id));
                ShortcutService.Publish(new ShortcutBindingsSection()
                    .Set(TaggingCardCommands.Card(1), [])
                    .Set(TaggingCardCommands.Card(2), [ShortcutGesture.Parse("F9")]));
                Dispatcher.UIThread.RunJobs();
                Button backCard = viewer.TaggingCardButtons[1];
                Assert.Equal(Texts.T("cards.name", Texts.T("tagging.back.undoSkip"), "F9"), AutomationProperties.GetName(backCard));
                Assert.Equal("F9", AutomationProperties.GetAcceleratorKey(backCard));
                StackPanel cardContent = Assert.IsType<StackPanel>(backCard.Content);
                Assert.NotNull(Assert.IsType<Border>(Assert.IsType<Grid>(cardContent.Children[0]).Children[0]).OpacityMask);
                Assert.Equal("F9", Assert.IsType<TextBlock>(Assert.IsType<Border>(cardContent.Children[1]).Child).Text);
                Assert.False(Assert.IsType<Border>(Assert.IsType<StackPanel>(viewer.TaggingCardButtons[0].Content).Children[1]).IsVisible);
                window.Activate();
                viewer.Focus();
                KeyEventArgs backKey = new() { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F9, Source = viewer };
                viewer.RaiseEvent(backKey);
                Assert.True(backKey.Handled);
                await BooruTestSupport.WaitAsync(() => !walk.Busy && host.GetTagQueue()!.Items[0].State == TagQueueState.Open,
                    "the remapped card key undoes the skip");

                viewer.SetCardMode(false, persist: false);
                Assert.Equal(TaggingWriteKind.Saved, (await walk.Decide(0, TagQueueState.Done).Write).Kind);
                Check("tagging.back.undoDone");
                ClickBack();
                await BooruTestSupport.WaitAsync(() => !walk.Busy && host.GetTagQueue()!.Items[0].State == TagQueueState.Open,
                    "the back button undoes the done decision");
                viewer.SetPosition("2 / 3", true, true);
                Check("command.gallery.previous");
                List<int> moves = [];
                viewer.MoveRequested += moves.Add;
                ClickBack();
                Assert.Equal(-1, Assert.Single(moves));
                Assert.Equal(TagQueueState.Open, host.GetTagQueue()!.Items[0].State);
            }
            finally { window.Close(); }

            void ClickBack() => viewer.TaggingBackButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            void Check(string key, bool enabled = true)
            {
                Assert.Equal(Texts.T(key), AutomationProperties.GetName(viewer.TaggingBackButton));
                Assert.StartsWith(Texts.T(key), Assert.IsType<string>(ToolTip.GetTip(viewer.TaggingBackButton)));
                Assert.Equal(enabled, viewer.TaggingBackButton.IsEnabled);
            }
        }
    });

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
        // The announcement uses the same platform keycaps as shortcut help, not fixed PC key names.
        bool mac = OperatingSystem.IsMacOS();
        string enter = mac ? "⌘↩" : "Ctrl+Enter", space = "S", back = mac ? "⌫" : "Backspace";
        string start = TaggingModeText.Start(walk);
        Assert.Equal($"Tagging mode: {enter} finishes the medium, {space} skips it, {back} takes back the last decision. "
            + "Not shown because the file is missing right now (they stay open in the queue): 2.", start);
        Assert.DoesNotContain(mac ? "Enter" : "↩", start);
        Assert.DoesNotContain(mac ? "Space" : "␣", start);
        Assert.DoesNotContain(mac ? "Backspace" : "⌫", start);
        Assert.Equal(2, walk.OpenOutsideWalk());
        Texts.SetLanguage("de");
        Assert.StartsWith($"Tagging-Modus: {(mac ? "⌘↩" : "Strg+Enter")} schließt das Medium ab, S überspringt es, "
            + $"{(mac ? "⌫" : "Rücktaste")} nimmt die letzte Entscheidung zurück.", TaggingModeText.Start(walk));
        Assert.Equal("Warteschlange 1 von 3", TaggingModeText.Position(walk, 0));
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData(ShortcutPlatform.Windows, "Ctrl+Enter", "S", "Backspace")]
    [InlineData(ShortcutPlatform.Linux, "Ctrl+Enter", "S", "Backspace")]
    [InlineData(ShortcutPlatform.MacOS, "⌘↩", "S", "⌫")]
    public void TheDecisionKeysUseExactPlatformKeycaps(ShortcutPlatform platform, string enter, string space, string back)
    {
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(platform: platform);
        Assert.Equal(enter, bindings.Display("tags.queue.done-next"));
        Assert.Equal(space, bindings.Display("tags.queue.skip"));
        Assert.Equal(back, bindings.Display("tags.queue.back"));
        foreach (string id in new[] { "tags.queue.done-next", "tags.queue.skip", "tags.queue.back" })
        {
            var gesture = Assert.Single(bindings.For(id)).Resolve(platform);
            Assert.Equal(id == "tags.queue.done-next"
                ? platform == ShortcutPlatform.MacOS ? Avalonia.Input.KeyModifiers.Meta : Avalonia.Input.KeyModifiers.Control
                : Avalonia.Input.KeyModifiers.None, gesture.KeyModifiers);
            Assert.True(bindings.Matches(new Avalonia.Input.KeyEventArgs { Key = gesture.Key, KeyModifiers = gesture.KeyModifiers }, id, "gallery-tagging"));
            Assert.False(bindings.Matches(new Avalonia.Input.KeyEventArgs { Key = gesture.Key, KeyModifiers = gesture.KeyModifiers | Avalonia.Input.KeyModifiers.Shift }, id, "gallery-tagging"));
        }
        Texts.SetLanguage("de");
        Assert.Equal(platform == ShortcutPlatform.MacOS ? "⌘↩" : "Strg+Enter", bindings.Display("tags.queue.done-next"));
        Assert.Equal("S", bindings.Display("tags.queue.skip"));
        Assert.Equal(platform == ShortcutPlatform.MacOS ? "⌫" : "Rücktaste", bindings.Display("tags.queue.back"));
    }

    // Gate only admission to the actual host write; do not fake its decision,
    // committed snapshot, durability, or undo behavior.
    private sealed class PausedHostWriter(ITaggingQueueWriter real) : ITaggingQueueWriter
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Release() => _release.TrySetResult();
        public async Task<TagQueueDecision?> DecideAsync(TagQueueKey key, TagQueueState state, CancellationToken token)
        {
            Entered.TrySetResult();
            await _release.Task.WaitAsync(token);
            return await real.DecideAsync(key, state, token);
        }
        public Task<TagQueueDecision?> UndoAsync(TagQueueDecision decision, CancellationToken token) =>
            real.UndoAsync(decision, token);
        public TagQueueSnapshot? Current => real.Current;
    }

    internal sealed class FakeWriter : ITaggingQueueWriter
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

    private sealed class CardProfileOwner(IGalleryTagPanelOwner inner) : IGalleryTagPanelOwner, IGalleryDecisionOwner, ITaggingCardsOwner
    {
        private readonly IGalleryDecisionOwner _decisions = (IGalleryDecisionOwner)inner;
        public TaggingCardSettings Cards { get; } = TaggingCardSettings.From(false, ["done", "back", "rate-4", "skip"], 4);
        public void SaveCardsActive(bool active) { }
        public IQuickTagTarget? OpenTarget(GalleryEntry entry, out string? unavailableReason) => inner.OpenTarget(entry, out unavailableReason);
        public MediaTagSubject? SubjectOf(GalleryEntry entry, out string? unavailableReason) => inner.SubjectOf(entry, out unavailableReason);
        public IQuickTagTarget? OpenSelectionTarget(IReadOnlyList<MediaTagSubject> subjects, out string? unavailableReason) =>
            inner.OpenSelectionTarget(subjects, out unavailableReason);
        public IReadOnlyList<GalleryEntry> Entries => inner.Entries;
        public GalleryTagPanelPosition? Position => inner.Position;
        public TagRecentList Recent => inner.Recent;
        public QuickTagOptions Options => inner.Options;
        public TagContextOptions ContextOptions => inner.ContextOptions;
        public BooruCatalogSnapshot? ReadSourceTags() => inner.ReadSourceTags();
        public GalleryThreadTagModel OpenThreadTags(ResourceId thread) => inner.OpenThreadTags(thread);
        public void OpenAdvanced(GalleryEntry entry) => inner.OpenAdvanced(entry);
        public GalleryDecisionState? Read(GalleryEntry entry) => _decisions.Read(entry);
        public Task SetFavoriteAsync(GalleryEntry entry, bool selected, CancellationToken token) => _decisions.SetFavoriteAsync(entry, selected, token);
        public Task SetRatingAsync(GalleryEntry entry, int? value, CancellationToken token) => _decisions.SetRatingAsync(entry, value, token);
    }
}
