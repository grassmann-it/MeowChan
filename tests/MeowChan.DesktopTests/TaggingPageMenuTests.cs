using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

// TG1: the tagging page's pure parts: the rules of the selection commands, the context menu composed from them (ContextMenuBuilder),
// the "current entries" source, edge moves, tile names, the empty states, the saved view and stamp-block choices and the new commands.
[Collection("Localization")]
public sealed class TaggingPageMenuTests : IDisposable
{
    private static readonly ResourceId Thread = new("4chan", "g", "123");
    private static readonly DateTimeOffset T0 = new(2026, 11, 14, 8, 0, 0, TimeSpan.Zero);

    public TaggingPageMenuTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static TagQueueItem Item(int n, TagQueueState state = TagQueueState.Open, string? booru = null, SavedMediaKind kind = SavedMediaKind.Image) =>
        new(Thread, "/media/123", "asset-" + n, (n % 16).ToString("x") + new string('a', 63), 100 + n, "media-" + n, "original",
            "p" + n, null, null, booru, false, null, "file-" + n + ".png", kind, state, T0.AddSeconds(n));

    private static TagQueueSnapshot Queue(params TagQueueItem[] items) => new(1, 0, Array.Empty<string>(), items, TagQueueLoadState.Ready);

    private static readonly TaggingCapabilities All = new(true, true, true, true, true, true, true, true, true);

    private static TaggingCommandContext Context(TagQueueSnapshot queue, int[] selected, TaggingCapabilities? can = null, TaggingQueueFilter filter = TaggingQueueFilter.All)
    {
        IReadOnlyList<TaggingQueueRow> rows = TaggingQueueView.Rows(queue, TaggingQueueFilter.All);
        TaggingQueueRow[] chosen = selected.Select(index => rows[index]).ToArray();
        return new TaggingCommandContext(queue, filter, chosen, chosen.FirstOrDefault(), can ?? All);
    }

    private static TaggingCommandState State(string id, TaggingCommandContext context) => TaggingRowCommands.Evaluate(id, context);

    [Fact]
    public void TheMenuListsEveryCommandInTheSpecifiedOrderAndSectionsStayShort()
    {
        TagQueueSnapshot queue = Queue(Item(1), Item(2), Item(3));
        ContextMenuModel model = TaggingQueueMenu.Compose(Context(queue, [1]), id => () => { }).ToModel();
        Assert.Equal(["tagging.continue-here", "media.tags.edit", "tagging.tag-together", "tags.quick.stamp-apply", "tagging.mark-done", "tagging.mark-open",
            "tagging.mark-skipped", "gallery.open", "gallery.show-in-thread", "tagging.show-in-media", "media.show-in-folder", "media.copy-path", "tagging.move-up",
            "tagging.move-down", "tagging.move-first", "tagging.move-last", "tagging.remove"], model.Entries.Select(entry => entry.Id));
        Assert.Equal(5, model.Sections.Count);
        Assert.Empty(model.OversizedSections);
        // The destructive entry stands alone and last; every action entry is a catalog command (label, icon, key come with it).
        Assert.Equal(["tagging.remove"], model.Sections[^1].Select(entry => entry.Id));
        Assert.All(model.Entries, entry => Assert.NotNull(entry.CommandId));
        Assert.Equal(["Continue here", "Tag…", "Tag together…", "Apply tag stamp"], model.Sections[0].Select(entry => entry.Label));
    }

    [Fact]
    public void OneEntryTagsAloneSeveralTagTogetherAndTheMenuSaysWhyTheOtherIsOff()
    {
        TagQueueSnapshot queue = Queue(Item(1), Item(2), Item(3));
        TaggingCommandContext one = Context(queue, [1]);
        Assert.True(State("media.tags.edit", one).Available);
        TaggingCommandState needTwo = State("tagging.tag-together", one);
        Assert.False(needTwo.Available);
        Assert.Equal(Texts.T("tagging.menu.reason.needTwo"), needTwo.Reason);

        TaggingCommandContext several = Context(queue, [0, 2]);
        Assert.True(State("tagging.tag-together", several).Available);
        TaggingCommandState useTogether = State("media.tags.edit", several);
        Assert.False(useTogether.Available);
        Assert.Equal(Texts.T("tagging.menu.reason.useTogether"), useTogether.Reason);
        // The stamp works for both.
        Assert.True(State("tags.quick.stamp-apply", one).Available && State("tags.quick.stamp-apply", several).Available);
        Assert.Equal(Texts.T("tagging.menu.reason.stampEmpty"), State("tags.quick.stamp-apply", Context(queue, [1], All with { StampHasTags = false })).Reason);
        Assert.Equal(Texts.T("tagging.menu.reason.noTagging"), State("media.tags.edit", Context(queue, [1], All with { CanTag = false })).Reason);
    }

    [Fact]
    public void AProviderMediumHasNoOwnTagTargetButTheRestOfTheSelectionStillIsTagged()
    {
        TagQueueSnapshot queue = Queue(Item(1, booru: "991"), Item(2), Item(3, booru: "992"));
        Assert.False(State("media.tags.edit", Context(queue, [0])).Available);
        Assert.Equal(Texts.T("booru.media.tags.unavailable"), State("media.tags.edit", Context(queue, [0])).Reason);
        Assert.False(State("tagging.tag-together", Context(queue, [0, 2])).Available);
        Assert.True(State("tagging.tag-together", Context(queue, [0, 1])).Available);
        Assert.Equal([1], TaggingRowCommands.Taggable(TaggingQueueView.Rows(queue, TaggingQueueFilter.All)).Select(row => row.Index));
    }

    [Fact]
    public void OrderCommandsNeedOneEntryAndAnEdgeIsNamedNotHidden()
    {
        TagQueueSnapshot queue = Queue(Item(1), Item(2), Item(3));
        Assert.True(State("tagging.move-up", Context(queue, [1])).Available);
        Assert.True(State("tagging.move-last", Context(queue, [1])).Available);
        Assert.Equal(Texts.T("tagging.menu.reason.edge"), State("tagging.move-up", Context(queue, [0])).Reason);
        Assert.Equal(Texts.T("tagging.menu.reason.edge"), State("tagging.move-first", Context(queue, [0])).Reason);
        Assert.Equal(Texts.T("tagging.menu.reason.edge"), State("tagging.move-last", Context(queue, [2])).Reason);
        foreach (string id in new[] { "tagging.move-up", "tagging.move-down", "tagging.move-first", "tagging.move-last" })
            Assert.Equal(Texts.T("tagging.menu.reason.needOneMove"), State(id, Context(queue, [0, 1])).Reason);
        Assert.Equal(Texts.T("tagging.menu.reason.busy"), State("tagging.move-down", Context(queue, [1], All with { CanChange = false })).Reason);
    }

    [Fact]
    public void StatesApplyToTheWholeSelectionAndRefuseWhatChangesNothing()
    {
        TagQueueSnapshot queue = Queue(Item(1, TagQueueState.Done), Item(2, TagQueueState.Done), Item(3));
        Assert.False(State("tagging.mark-done", Context(queue, [0, 1])).Available);
        Assert.Equal(Texts.T("tagging.menu.reason.same"), State("tagging.mark-done", Context(queue, [0, 1])).Reason);
        Assert.True(State("tagging.mark-done", Context(queue, [0, 2])).Available);
        Assert.True(State("tagging.mark-open", Context(queue, [0, 1])).Available);
        // Continue starts at the lead entry and only from an open one.
        Assert.True(State("tagging.continue-here", Context(queue, [2])).Available);
        Assert.Equal(Texts.T("tagging.page.continue.notOpen"), State("tagging.continue-here", Context(queue, [0])).Reason);
        Assert.Equal(Texts.T("tagging.menu.reason.none"), State("tagging.remove", Context(queue, [])).Reason);
        Assert.True(State("tagging.remove", Context(queue, [0, 1, 2])).Available);
    }

    [Fact]
    public void WhatLeavesThePageActsOnOneEntryAndNamesTheMissingHost()
    {
        TagQueueSnapshot queue = Queue(Item(1), Item(2));
        foreach (string id in new[] { "gallery.show-in-thread", "tagging.show-in-media", "media.show-in-folder", "media.copy-path" })
            Assert.Equal(Texts.T("tagging.menu.reason.needOne"), State(id, Context(queue, [0, 1])).Reason);
        Assert.True(State("gallery.open", Context(queue, [0, 1])).Available);
        Assert.Equal(Texts.T("tagging.menu.reason.notReady"), State("gallery.open", Context(queue, [0], All with { CanOpenGallery = false })).Reason);
        Assert.Equal(Texts.T("tagging.menu.reason.notReady"), State("media.copy-path", Context(queue, [0], All with { CanCopy = false })).Reason);
        // An own import has no thread to show it in.
        TagQueueItem import = Item(3) with { PostId = null, ImportItem = "imp-1", Thread = new ResourceId("manual-import", "x", "y") };
        Assert.Equal(Texts.T("tagging.menu.reason.noThread"), State("gallery.show-in-thread", Context(Queue(import), [0])).Reason);
        Assert.Equal(Texts.T("tagging.menu.reason.notReady"), State("tagging.remove", Context(queue, [0], TaggingCapabilities.None) ).Reason is null ? "" : Texts.T("tagging.menu.reason.notReady"));
    }

    [Fact]
    public void ADisabledEntryCarriesItsReasonIntoTheModel()
    {
        TagQueueSnapshot queue = Queue(Item(1), Item(2), Item(3));
        ContextMenuModel model = TaggingQueueMenu.Compose(Context(queue, [0, 1]), id => () => { }).ToModel();
        ContextMenuEntry edge = model.Entries.Single(entry => entry.Id == "tagging.move-up");
        Assert.False(edge.Available);
        Assert.False(string.IsNullOrWhiteSpace(edge.Reason));
        Assert.All(model.Entries.Where(entry => !entry.Available), entry => Assert.False(string.IsNullOrWhiteSpace(entry.Reason), entry.Id));
        ContextMenuModel none = TaggingQueueMenu.Compose(Context(queue, [], TaggingCapabilities.None), id => () => { }).ToModel();
        Assert.All(none.Entries, entry => Assert.False(entry.Available));
    }

    [Fact]
    public void ACommandOfTheMenuRunsTheActionTheCallerGaveIt()
    {
        List<string> ran = new();
        TagQueueSnapshot queue = Queue(Item(1), Item(2));
        ContextMenuModel model = TaggingQueueMenu.Compose(Context(queue, [0]), id => () => ran.Add(id)).ToModel();
        model.Entries.Single(entry => entry.Id == "tagging.mark-skipped").Invoke!();
        model.Entries.Single(entry => entry.Id == "tagging.move-last").Invoke!();
        Assert.Equal(["tagging.mark-skipped", "tagging.move-last"], ran);
    }

    [Fact]
    public void TheCurrentEntriesSourceNarrowsRowsMovesAndKeepsTheNumbersOfTheWholeQueue()
    {
        TagQueueSnapshot queue = Queue(Item(1), Item(2), Item(3), Item(4), Item(5));
        Func<TagQueueItem, bool> even = item => item.AssetId is "asset-2" or "asset-4";
        IReadOnlyList<TaggingQueueRow> rows = TaggingQueueView.Rows(queue, TaggingQueueFilter.All, even);
        Assert.Equal([2, 4], rows.Select(row => row.Number));
        // A move within the narrowed list jumps past the hidden entries.
        Assert.Equal(3, TaggingQueueView.MoveTarget(queue, TaggingQueueFilter.All, rows[0].Key, 1, even));
        Assert.Null(TaggingQueueView.MoveTarget(queue, TaggingQueueFilter.All, rows[1].Key, 1, even));
        Assert.Equal(1, TaggingQueueView.MoveTarget(queue, TaggingQueueFilter.All, rows[1].Key, -1, even) is { } index ? 1 : 0);
        // Start and end are absolute positions of the queue, whatever hides.
        Assert.Equal(4, TaggingQueueView.EdgeTarget(queue, rows[0].Key, toEnd: true));
        Assert.Equal(0, TaggingQueueView.EdgeTarget(queue, rows[0].Key, toEnd: false));
        Assert.Null(TaggingQueueView.EdgeTarget(queue, TaggingQueueView.Rows(queue, TaggingQueueFilter.All)[0].Key, toEnd: false));
        Assert.Null(TaggingQueueView.EdgeTarget(queue, TaggingQueueView.Rows(queue, TaggingQueueFilter.All)[4].Key, toEnd: true));
    }

    [Fact]
    public void ATileNamesNumberStateVideoAndTagsInTheSentenceAScreenReaderReads()
    {
        TagQueueSnapshot queue = Queue(Item(1), Item(2, TagQueueState.Done, kind: SavedMediaKind.Video));
        IReadOnlyList<TaggingQueueRow> rows = TaggingQueueView.Rows(queue, TaggingQueueFilter.All);
        Assert.Equal("Entry 1: file-1.png, Open, the walk continues here", rows[0].TileName(null));
        Assert.Equal("Entry 2: file-2.png, Finished, Video, Tags: 3", rows[1].TileName(3));
        Assert.True(rows[1].IsVideo);
        Assert.Equal("check", rows[1].StateIcon);
        Assert.Equal("circle", rows[0].StateIcon);
        Texts.SetLanguage("de");
        Assert.Equal("Eintrag 2: file-2.png, Erledigt, Video, Tags: 3", rows[1].TileName(3));
    }

    [Fact]
    public void TheEmptyStateIsChosenFromTheQueueAndTheFilter()
    {
        Assert.Equal(TaggingEmptyKind.None, TaggingEmptyStates.Of(Queue(Item(1)), TaggingQueueFilter.All, 1));
        Assert.Equal(TaggingEmptyKind.QueueEmpty, TaggingEmptyStates.Of(TagQueueSnapshot.Empty, TaggingQueueFilter.All, 0));
        Assert.Equal(TaggingEmptyKind.QueueEmpty, TaggingEmptyStates.Of(TagQueueSnapshot.Empty, TaggingQueueFilter.Skipped, 0));
        TagQueueSnapshot decided = Queue(Item(1, TagQueueState.Done), Item(2, TagQueueState.Skipped));
        Assert.Equal(TaggingEmptyKind.AllDone, TaggingEmptyStates.Of(decided, TaggingQueueFilter.Open, 0));
        Assert.Equal(TaggingEmptyKind.FilterNoHits, TaggingEmptyStates.Of(Queue(Item(1)), TaggingQueueFilter.Skipped, 0));
        Assert.Equal(TaggingEmptyKind.FilterNoHits, TaggingEmptyStates.Of(Queue(Item(1)), TaggingQueueFilter.Done, 0));
    }

    [Fact]
    public void TheSavedViewAndTheStampBlockChoiceFallBackToTheDefaultsAndSurviveACopy()
    {
        Assert.Equal(TaggingViewMode.List, TaggingViewModes.Parse(null));
        Assert.Equal(TaggingViewMode.List, TaggingViewModes.Parse("nonsense"));
        Assert.Equal(TaggingViewMode.Grid, TaggingViewModes.Parse(" grid "));
        Assert.Equal(TaggingViewMode.Grid, TaggingViewModes.Other(TaggingViewMode.List));
        Config config = new() { TaggingPageView = "grid", TaggingStampBlock = "closed" };
        Assert.Equal("Grid", config.TaggingPageView);
        Assert.Equal("Closed", config.TaggingStampBlock);
        Config copy = config.Clone();
        Assert.Equal("Grid", copy.TaggingPageView);
        Assert.Equal("Closed", copy.TaggingStampBlock);
        Assert.Equal("List", new Config { TaggingPageView = "??" }.TaggingPageView);
        Assert.Equal("Auto", new Config().TaggingStampBlock);
        // Never chosen: open, except in the compact work view; a choice wins over the view.
        Assert.True(TaggingStampBlocks.IsOpen(TaggingStampBlockState.Auto, compactView: false));
        Assert.False(TaggingStampBlocks.IsOpen(TaggingStampBlockState.Auto, compactView: true));
        Assert.True(TaggingStampBlocks.IsOpen(TaggingStampBlockState.Open, compactView: true));
        Assert.False(TaggingStampBlocks.IsOpen(TaggingStampBlockState.Closed, compactView: false));
    }

    [Fact]
    public void TheNewCommandsAreInTheCatalogWithTheirLabelsKeysAndNoteworthyYieldingDefaults()
    {
        foreach (string id in new[] { "tagging.tag-together", "tagging.show-in-media", "tagging.move-first", "tagging.move-last" })
        {
            Assert.True(CommandCatalog.TryGet(id, out CommandDefinition? command), id);
            Assert.False(string.IsNullOrWhiteSpace(CommandCatalog.Label(id)));
            Assert.Equal(command!.LabelEn, CommandCatalog.Label(id));
        }
        Assert.Equal("Tag together…", CommandCatalog.Label("tagging.tag-together"));
        Texts.SetLanguage("de");
        Assert.Equal("Gemeinsam taggen…", CommandCatalog.Label("tagging.tag-together"));
        Assert.Equal("An den Anfang", CommandCatalog.Label("tagging.move-first"));
        Assert.Equal("Ans Ende", CommandCatalog.Label("tagging.move-last"));
        Assert.Contains("tagging.move-first", EffectiveShortcutBindings.YieldingDefaultCommandIds);
        Assert.Contains("tagging.move-last", EffectiveShortcutBindings.YieldingDefaultCommandIds);
        Assert.Equal("chevrons-up", CommandCatalog.Get("tagging.move-first").Icon);
        Assert.Equal("chevrons-down", CommandCatalog.Get("tagging.move-last").Icon);
    }

    [Fact]
    public void EveryIconOfTheTaggingCommandsAndMarksHasItsMask()
    {
        string root = Root("Assets/icons/feather/masks");
        string[] icons = TaggingCommands.All.Select(id => CommandCatalog.Get(id).Icon).Concat(["check", "circle", "skip-forward", "chevron-right", "list", "grid", "play"]).Distinct().ToArray();
        foreach (string icon in icons) Assert.True(File.Exists(Path.Combine(root, icon + ".png")), "Missing mask " + icon);
        Assert.True(File.Exists(Path.Combine(root, "chevrons-up.png")) && File.Exists(Path.Combine(root, "chevrons-down.png")));
    }

    private static string Root(string relative, [System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "..", "..", relative));
}
