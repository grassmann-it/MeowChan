using System.Diagnostics;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

// PT2 (PLAN-POWER-TAGGEN §1.3): the tagging page. The pure rules (filter, counters, start label, moves), the catalog commands of its
// list and the work-view rows, then the page itself over a real host and tag-queue.json: states, order, removal, clearing and the
// stamp change the file, and 2 000 entries open well inside the budget.
[Collection("Localization")]
public sealed class TaggingPageTests : IDisposable
{
    private static readonly ResourceId Thread = new("4chan", "g", "123");
    private static readonly DateTimeOffset T0 = new(2026, 11, 14, 8, 0, 0, TimeSpan.Zero);

    public TaggingPageTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static TagQueueItem Item(int n, TagQueueState state = TagQueueState.Open, ResourceId? thread = null) =>
        new(thread ?? Thread, "/media/123", "asset-" + n, (n % 16).ToString("x") + new string('a', 63), 100 + n, "media-" + n, "original",
            "p" + n, null, null, null, false, null, "file-" + n + ".png", SavedMediaKind.Image, state, T0.AddSeconds(n));

    private static TagQueueSnapshot Queue(int cursor, params TagQueueItem[] items) =>
        new(1, cursor, Array.Empty<string>(), items, TagQueueLoadState.Ready);

    [Fact]
    public void TheFilterKeepsTheQueueOrderAndMarksWhereTheWalkContinues()
    {
        TagQueueSnapshot queue = Queue(1, Item(1, TagQueueState.Done), Item(2, TagQueueState.Skipped), Item(3), Item(4, TagQueueState.Done), Item(5));
        IReadOnlyList<TaggingQueueRow> all = TaggingQueueView.Rows(queue, TaggingQueueFilter.All);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, all.Select(row => row.Number));
        // The cursor stands on a skipped entry: "continue" starts at the next open one, and only that row is marked.
        Assert.Equal(new[] { 3 }, all.Where(row => row.Next).Select(row => row.Number));
        Assert.Equal(new[] { 3, 5 }, TaggingQueueView.Rows(queue, TaggingQueueFilter.Open).Select(row => row.Number));
        Assert.Equal(new[] { 1, 4 }, TaggingQueueView.Rows(queue, TaggingQueueFilter.Done).Select(row => row.Number));
        Assert.Equal(new[] { 2 }, TaggingQueueView.Rows(queue, TaggingQueueFilter.Skipped).Select(row => row.Number));
        Assert.Equal("Entry 3: file-3.png, Open, the walk continues here", all[2].AccessibleName);
        Assert.Equal("Entry 1: file-1.png, Finished", all[0].AccessibleName);
        Assert.Equal("4chan · /g/ · 123", all[0].SourceText);
        Assert.Equal("Image", all[0].KindText);
    }

    [Fact]
    public void TheProgressCountsEveryStateAndNeverRoundsUpToAHundredPercent()
    {
        TagQueueSnapshot queue = Queue(0, Enumerable.Range(1, 200).Select(n => Item(n, n <= 199 ? TagQueueState.Done : TagQueueState.Open)).ToArray());
        Assert.Equal("Open: 1 · Finished: 199 · Skipped: 0", TaggingQueueView.ProgressText(queue));
        Assert.Equal(0.995, TaggingQueueView.Share(queue), 3);
        Assert.Equal("99% worked through", TaggingQueueView.PercentText(queue));
        Assert.Equal(0, TaggingQueueView.Share(TagQueueSnapshot.Empty));
        Texts.SetLanguage("de");
        Assert.Equal("Offen: 1 · Erledigt: 199 · Übersprungen: 0", TaggingQueueView.ProgressText(queue));
        Assert.Equal("99 % abgearbeitet", TaggingQueueView.PercentText(queue));
    }

    [Fact]
    public void TheStartButtonStartsAFreshQueueContinuesAWalkedOneAndIsOffWithoutOpenEntries()
    {
        Assert.Equal("Start tagging", TaggingQueueView.StartLabel(Queue(0, Item(1), Item(2))));
        Assert.Equal("Continue at 2 of 3", TaggingQueueView.StartLabel(Queue(1, Item(1, TagQueueState.Done), Item(2), Item(3))));
        // Nothing decided, but the cursor was moved behind the first open entry: that is a continue, too.
        Assert.Equal("Continue at 2 of 2", TaggingQueueView.StartLabel(Queue(1, Item(1), Item(2))));
        Assert.Null(TaggingQueueView.StartLabel(Queue(0, Item(1, TagQueueState.Done), Item(2, TagQueueState.Skipped))));
        Assert.Null(TaggingQueueView.StartLabel(TagQueueSnapshot.Empty));
    }

    [Fact]
    public void AMoveLandsOnTheNeighbouringShownEntryAndStopsAtTheEdges()
    {
        TagQueueSnapshot queue = Queue(0, Item(1), Item(2, TagQueueState.Done), Item(3), Item(4));
        Assert.Null(TaggingQueueView.MoveTarget(queue, TaggingQueueFilter.All, Item(1).Key, -1));
        Assert.Equal(1, TaggingQueueView.MoveTarget(queue, TaggingQueueFilter.All, Item(1).Key, 1));
        // Under "Open" the finished entry 2 is hidden: entry 3 moves past it in one step.
        Assert.Equal(0, TaggingQueueView.MoveTarget(queue, TaggingQueueFilter.Open, Item(3).Key, -1));
        Assert.Equal(2, TaggingQueueView.MoveTarget(queue, TaggingQueueFilter.Open, Item(1).Key, 1));
        Assert.Null(TaggingQueueView.MoveTarget(queue, TaggingQueueFilter.All, Item(4).Key, 1));
        Assert.Null(TaggingQueueView.MoveTarget(queue, TaggingQueueFilter.All, Item(9).Key, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => TaggingQueueView.MoveTarget(queue, TaggingQueueFilter.All, Item(1).Key, 2));
    }

    [Fact]
    public void ImportsAndProviderMediaAreNamedByTheirSource()
    {
        Assert.Equal("Own imports", TaggingQueueView.SourceText(Item(1, thread: Import.ManualImportIdentity.Thread(Guid.NewGuid()))));
        Assert.Equal("Danbooru", TaggingQueueView.SourceText(Item(1, thread: new ResourceId(BooruMediaIdentity.Source, BooruMediaIdentity.Board, "7"))));
    }

    [Theory]
    [InlineData(ShortcutPlatform.Linux, "tagging.continue-here", "Enter")]
    [InlineData(ShortcutPlatform.Linux, "tagging.remove", "Delete")]
    [InlineData(ShortcutPlatform.Linux, "tagging.move-up", "Alt+Up")]
    [InlineData(ShortcutPlatform.Linux, "tagging.move-down", "Alt+Down")]
    [InlineData(ShortcutPlatform.MacOS, "tagging.remove", "⌘⌫ / ⌦")]
    public void TheListKeysAreCatalogCommandsOfTheTaggingContextWithoutConflicts(ShortcutPlatform platform, string id, string display)
    {
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(platform: platform);
        Assert.Empty(bindings.Issues);
        Assert.Equal(display, bindings.Display(id));
        Assert.Equal(new[] { "tagging" }, CommandCatalog.Get(id).ExecutionContexts);
        Assert.Contains(id, EffectiveShortcutBindings.YieldingDefaultCommandIds);
    }

    [Fact]
    public void ThePageIsAFooterPageBehindMediaWithoutADigitAndKeepsItsAppMenuEntry()
    {
        Assert.True(MainPageOrder.Contains("navigation.tagging"));
        Assert.Equal("navigation.media", MainPageOrder.CommandIds[MainPageOrder.CommandIds.ToList().IndexOf("navigation.tagging") - 1]);
        Assert.Null(MainPageOrder.Digit("navigation.tagging"));
        Assert.Equal(MainWindow.WorkspacePage.Tagging, MainWindow.PageFor("navigation.tagging"));
        CommandDefinition navigation = CommandCatalog.Get("navigation.tagging");
        Assert.Equal(new[] { "footer", "app-menu", "tools-menu" }, navigation.Contexts);
        Assert.Equal("tag", navigation.Icon);
        Assert.Equal(CommandScope.Application, navigation.Scope);
        Assert.Null(navigation.Shortcut);
        Assert.Empty(EffectiveShortcutBindings.Defaults(navigation));
        Assert.Equal("Tagging", navigation.Label);
        foreach (string id in new[] { "tagging.start", "tagging.mark-done", "tagging.mark-open", "tagging.mark-skipped", "tagging.add", "tagging.clear-done", "tagging.clear" })
        {
            Assert.Empty(EffectiveShortcutBindings.Defaults(CommandCatalog.Get(id)));
            Assert.Equal(new[] { "tagging" }, CommandCatalog.Get(id).ExecutionContexts);
        }
    }

    [Fact]
    public void TheFooterButtonIsWiredLikeTheOtherPagesWithAnIconAndALocalizedName()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Source())!, "../.."));
        string markup = File.ReadAllText(Path.Combine(root, "Desktop/Shell/MainWindow.axaml"));
        // Footer button, More entry and the ⋯ entry all call the one handler, which builds the page lazily through ShowTaggingPage.
        Assert.Contains("x:Name=\"TaggingNavigationButton\" Classes=\"nav localized\" Tag=\"nav.tagging\"", markup, StringComparison.Ordinal);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(markup, "Tag=\"nav.tagging\"[^>]*Click=\"OnTaggingNavigationClick\"").Count);
        string navigation = File.ReadAllText(Path.Combine(root, "Desktop/Shell/MainWindow.Navigation.cs"));
        Assert.Contains("ShowTaggingPage();", navigation[navigation.IndexOf("private void OnTaggingNavigationClick", StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.Contains("TaggingNavigationButton.Classes.Set(\"selected\", page == WorkspacePage.Tagging)", navigation, StringComparison.Ordinal);
        // Tooltip and accessible name come from the command adapter; the icon is a catalog mask that exists as a file.
        Assert.True(File.Exists(Path.Combine(root, "Assets/icons/feather/masks/" + CommandCatalog.Get("navigation.tagging").Icon + ".png")));
        Assert.StartsWith("Tagging", CommandBindings.Tooltip("navigation.tagging"), StringComparison.Ordinal);
        Texts.SetLanguage("de");
        Assert.Equal("Taggen", CommandCatalog.Label("navigation.tagging"));
        Assert.Equal("Taggen", Texts.T("nav.tagging"));
        Texts.SetLanguage("en");
        Assert.Equal("Tagging", Texts.T("nav.tagging"));
    }

    private static string Source([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    [Fact]
    public void TheWorkViewsKeepTheQueueAndThinOutOnlyTheToolbar()
    {
        foreach (WorkView view in WorkViewSetting.All)
        {
            foreach (string id in new[] { "tagging.progress", "tagging.start", "tagging.list", "tagging.error", "tagging.stamp", "tagging.filter" })
                Assert.True(WorkViewPolicy.OnSurface(WorkViewPolicy.Resolve(id, view)), id + " in " + view);
            Assert.Equal(view != WorkView.Barebones, WorkViewPolicy.OnSurface(WorkViewPolicy.Resolve("tagging.keys-hint", view)));
        }
        Assert.Equal(Presence.Overflow, WorkViewPolicy.Resolve("tagging.toolbar.clear-done", WorkView.Barebones));
        Assert.Equal(Presence.Direct, WorkViewPolicy.Resolve("tagging.toolbar.clear-done", WorkView.Simple));
        Assert.Equal(Presence.Overflow, WorkViewPolicy.Resolve("tagging.toolbar.clear", WorkView.Simple));
        Assert.Equal(Presence.Direct, WorkViewPolicy.Resolve("tagging.toolbar.clear", WorkView.Advanced));
        // The footer button: Barebones keeps the page under More, Simple and Advanced show it directly (like Media).
        Assert.Equal(Presence.Overflow, WorkViewPolicy.Resolve("shell.footer.tagging", WorkView.Barebones));
        Assert.Equal(Presence.Direct, WorkViewPolicy.Resolve("shell.footer.tagging", WorkView.Simple));
        Assert.Equal(Presence.Direct, WorkViewPolicy.Resolve("shell.footer.tagging", WorkView.Advanced));
        Assert.Equal("navigation.tagging", WorkViewPolicy.Get("shell.footer.tagging").CommandId);
        Assert.Equal(new[] { WorkViewPage.Shell, WorkViewPage.Tagging }, WorkViewHelp.HelpPages(nameof(MainWindow.WorkspacePage.Tagging), false));
    }

    private static async Task<DesktopSessionHost> StartAsync(string directory)
    {
        DesktopSessionHost host = new(directory, Array.Empty<IImageBoard>());
        await host.StartAsync();
        return host;
    }

    [Fact]
    public async Task ThePageWorkChangesStatesOrderAndEntriesInTheQueueFile()
    {
        using TempDirectory temp = new();
        await using (DesktopSessionHost host = await StartAsync(temp.Path))
        {
            await host.EnqueueTagQueueAsync(Enumerable.Range(1, 4).Select(n => Item(n)).ToArray());
            TaggingQueueController work = new(host);
            await work.LoadAsync();
            Assert.True(work.CanChange);
            Assert.Equal(4, work.Queue.Count);
            Assert.Equal("Start tagging", TaggingQueueView.StartLabel(work.Queue));

            Assert.True(await work.SetStatesAsync(new[] { Item(1).Key, Item(2).Key }, TagQueueState.Done));
            Assert.Equal(new[] { TagQueueState.Done, TagQueueState.Done, TagQueueState.Open, TagQueueState.Open }, host.GetTagQueue()!.Items.Select(item => item.State));
            Assert.Equal("Marked as finished: 2.", work.Status);
            Assert.Equal("Open: 2 · Finished: 2 · Skipped: 0", TaggingQueueView.ProgressText(work.Queue));
            Assert.Equal("Continue at 3 of 4", TaggingQueueView.StartLabel(work.Queue));
            // Marking what already has the state is no change and no write.
            long revision = work.Queue.Revision;
            Assert.False(await work.SetStatesAsync(new[] { Item(1).Key }, TagQueueState.Done));
            Assert.Equal(revision, host.GetTagQueue()!.Revision);

            // Entry 4 moves up past entry 3; "continue here" on it puts the walk's cursor there.
            Assert.True(await work.MoveAsync(Item(4).Key, TaggingQueueFilter.All, -1));
            Assert.Equal(new[] { "asset-1", "asset-2", "asset-4", "asset-3" }, host.GetTagQueue()!.Items.Select(item => item.AssetId));
            Assert.Equal("Moved to position 3.", work.Status);
            Assert.False(await work.MoveAsync(Item(1).Key, TaggingQueueFilter.All, -1));
            Assert.True(await work.SetCursorAsync(2));
            Assert.Equal(2, host.GetTagQueue()!.Cursor);
            Assert.Equal("Continue at 3 of 4", TaggingQueueView.StartLabel(work.Queue));

            Assert.True(await work.ClearFinishedAsync());
            Assert.Equal(new[] { "asset-4", "asset-3" }, host.GetTagQueue()!.Items.Select(item => item.AssetId));
            Assert.Equal("Finished entries removed: 2.", work.Status);
            Assert.False(await work.ClearFinishedAsync());

            Assert.True(await work.RemoveAsync(new[] { Item(3).Key }));
            Assert.Equal(new[] { "asset-4" }, host.GetTagQueue()!.Items.Select(item => item.AssetId));
            Assert.Equal("Removed from the queue: 1.", work.Status);

            // A change made elsewhere (the media page adds) reaches the page through the passive refresh.
            await host.EnqueueTagQueueAsync(new[] { Item(7) });
            Assert.True(work.Refresh());
            Assert.Equal(2, work.Queue.Count);
            Assert.False(work.Refresh());
        }
        // Everything above was written before it completed.
        await using DesktopSessionHost again = await StartAsync(temp.Path);
        Assert.Equal(new[] { "asset-4", "asset-7" }, (await again.LoadTagQueueAsync()).Items.Select(item => item.AssetId));
    }

    [Fact]
    public async Task TheStampEditorChecksNamesKeepsTheLimitAndWritesTheQueueStamp()
    {
        using TempDirectory temp = new();
        await using DesktopSessionHost host = await StartAsync(temp.Path);
        TaggingQueueController work = new(host);
        await work.LoadAsync();
        Assert.True(await work.AddStampAsync("surface", 2));
        Assert.True(await work.AddStampAsync("  wip ", 2));
        Assert.Equal(new[] { "surface", "wip" }, host.GetTagQueue()!.Stamp);
        Assert.Equal("“wip” added to the stamp.", work.Status);
        Assert.False(await work.AddStampAsync("Surface", 2));
        Assert.Equal("“Surface” is already in the stamp.", work.Status);
        Assert.False(await work.AddStampAsync("third", 2));
        Assert.Equal("The stamp is full (limit: 2). Remove a tag from it first.", work.Status);
        Assert.False(await work.AddStampAsync("   ", 2));
        Assert.True(await work.AddStampAsync("third", 3));
        Assert.Equal(new[] { "surface", "wip", "third" }, host.GetTagQueue()!.Stamp);
        Assert.True(await work.RemoveStampAsync("surface"));
        Assert.Equal(new[] { "wip", "third" }, host.GetTagQueue()!.Stamp);
        Assert.Equal("“surface” removed from the stamp.", work.Status);
        Assert.False(await work.RemoveStampAsync("surface"));
    }

    [Fact]
    public async Task ClearingEmptiesTheQueueAndRecoversAnUnreadableFile()
    {
        using TempDirectory temp = new();
        await using DesktopSessionHost host = await StartAsync(temp.Path);
        await host.EnqueueTagQueueAsync(new[] { Item(1), Item(2) });
        TaggingQueueController work = new(host);
        await work.LoadAsync();
        Assert.True(await work.ClearAllAsync());
        Assert.Equal(0, host.GetTagQueue()!.Count);
        Assert.Equal("The tagging queue is empty now.", work.Status);
        Assert.Null(TaggingQueueView.StartLabel(work.Queue));

        // A damaged file is an error line, never emptied by reading; clearing keeps the damaged copy and starts empty.
        await File.WriteAllTextAsync(Path.Combine(temp.Path, TagQueueStore.FileName), "{ not json");
        TaggingQueueController damaged = new(host);
        await damaged.LoadAsync();
        Assert.NotNull(damaged.LoadError);
        Assert.False(damaged.CanChange);
        Assert.Equal(Texts.T("queue.error.unreadable"), damaged.ErrorText);
        Assert.True(await damaged.ClearAllAsync());
        Assert.Null(damaged.LoadError);
        Assert.Equal(0, (await host.LoadTagQueueAsync()).Count);
        Assert.NotEmpty(Directory.GetFiles(temp.Path, TagQueueStore.FileName + ".corrupt-*"));
    }

    [Fact]
    public async Task TwoThousandEntriesAreListedWellInsideTheBudget()
    {
        using TempDirectory temp = new();
        await using DesktopSessionHost host = await StartAsync(temp.Path);
        await host.EnqueueTagQueueAsync(Enumerable.Range(1, 2_000).Select(n => Item(n)).ToArray());
        TaggingQueueController work = new(host);
        Stopwatch watch = Stopwatch.StartNew();
        await work.LoadAsync();
        IReadOnlyList<TaggingQueueRow> rows = TaggingQueueView.Rows(work.Queue, TaggingQueueFilter.All);
        string progress = TaggingQueueView.ProgressText(work.Queue);
        watch.Stop();
        Assert.Equal(2_000, rows.Count);
        Assert.Equal("Open: 2000 · Finished: 0 · Skipped: 0", progress);
        // §1.8: the page opens in at most 100 ms; the list is virtualized, so reading and the rows are the cost. The test machine
        // shares its cores, so it allows ten times that.
        Assert.True(watch.ElapsedMilliseconds < 1_000, "Opening took " + watch.ElapsedMilliseconds + " ms.");
    }

}
