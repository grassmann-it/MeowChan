using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Platform;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

// TQ1-TQ6 (UX review of the tagging page, 21.11.2026): nothing is selected when the page opens, the progress and start texts say what they
// count, "Without tags" is a fifth filter value behind the four that existed (the saved indexes stay), and the Type and Group columns stand
// only while the shown rows differ in them.
[Collection("Gallery viewer construction")]
public sealed class TaggingQuickfixTests : IDisposable
{
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly DateTimeOffset T0 = new(2026, 11, 21, 8, 0, 0, TimeSpan.Zero);

    public TaggingQuickfixTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static TagQueueItem Item(int n, TagQueueState state = TagQueueState.Open, SavedMediaKind kind = SavedMediaKind.Image) =>
        new(new ResourceId("4chan", "g", "123"), "/media/123", "asset-" + n, (n % 16).ToString("x") + new string('a', 63), 100 + n, "media-" + n,
            "original", "p" + n, null, null, null, false, null, "file-" + n + ".png", kind, state, T0.AddSeconds(n));

    private static TagQueueSnapshot Queue(int cursor, params TagQueueItem[] items) =>
        new(1, cursor, Array.Empty<string>(), items, TagQueueLoadState.Ready);

    private static void WithPage(Action<TaggingPage> test) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
        TokenApplication.Bind();
        Texts.Initialize();
        Texts.SetLanguage("en");
        using TempDirectory temp = new();
        DesktopSessionHost host = new(temp.Path, Array.Empty<IImageBoard>());
        TaggingPage page = new(host);
        page.Styles.Add(new StyleInclude(new Uri("avares://MeowChan/"))
            { Source = new Uri("avares://MeowChan/Desktop/WorkspaceStyles.axaml") });
        try { test(page); }
        finally { page.End(); host.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    });

    private static void Render(TaggingPage page, TagQueueSnapshot snapshot, TaggingQueueFilter filter = TaggingQueueFilter.All)
    {
        object work = typeof(TaggingPage).GetField("_queueWork", Private)!.GetValue(page)!;
        foreach ((string name, object? value) in new (string, object?)[] { ("Queue", snapshot), ("Loaded", true), ("Loading", false), ("LoadError", null) })
            typeof(TaggingQueueController).GetProperty(name, Private)!.SetValue(work, value);
        page.Filter = filter;
        typeof(TaggingPage).GetMethod("Show", Private)!.Invoke(page, null);
    }

    [Fact]
    public void TheFilterKeepsItsFourSavedIndexesAndAppendsWithoutTags()
    {
        Assert.Equal(new[] { "All", "Open", "Done", "Skipped", "Untagged" }, Enum.GetNames<TaggingQueueFilter>());
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, Enum.GetValues<TaggingQueueFilter>().Select(value => (int)value));
        Assert.Equal("Without tags", TaggingQueueView.FilterText(TaggingQueueFilter.Untagged));
        Assert.Equal("Ohne Tags", WithLanguage("de", () => TaggingQueueView.FilterText(TaggingQueueFilter.Untagged)));
        // It narrows by tags, not by state: every state shows.
        foreach (TagQueueState state in Enum.GetValues<TagQueueState>())
            Assert.True(TaggingQueueView.Shows(TaggingQueueFilter.Untagged, state));
    }

    [Fact]
    public void ThePageFilterBoxHoldsFiveValuesAndRoundTripsEachOfThem() => WithPage(page =>
    {
        Assert.Equal(5, page.FilterBox.ItemCount);
        foreach (TaggingQueueFilter filter in Enum.GetValues<TaggingQueueFilter>())
        {
            page.Filter = filter;
            Assert.Equal((int)filter, page.FilterBox.SelectedIndex);
            Assert.Equal(filter, page.Filter);
        }
    });

    [Fact]
    public void AnUnknownTagCountIsNeverTakenForNoTagsSoWithoutTagsStaysEmptyUntilTheStoreIsKnown() => WithPage(page =>
    {
        Render(page, Queue(0, Item(1), Item(2), Item(3)));
        Assert.Equal(3, page.Rows.Count);
        Assert.Null(page.TagCountOf(Item(1))); // no tag source in this page: unknown, not zero
        Render(page, Queue(0, Item(1), Item(2), Item(3)), TaggingQueueFilter.Untagged);
        Assert.Empty(page.Rows);
        Assert.Equal(TaggingEmptyKind.FilterNoHits, page.EmptyKind);
    });

    [Fact]
    public void OpeningThePageSelectsNothingAndKeepsTheSelectionBarAway() => WithPage(page =>
    {
        Render(page, Queue(0, Item(1), Item(2), Item(3)));
        page.FocusOnOpen();
        Assert.Equal(0, page.SelectedCount);
        Assert.False(page.SelectionBar.IsVisible);
        // A real selection raises the bar again.
        page.List.SelectedIndex = 1;
        Assert.Equal(1, page.SelectedCount);
        Assert.True(page.SelectionBar.IsVisible);
        page.ClearSelection();
        Assert.False(page.SelectionBar.IsVisible);
    });

    [Fact]
    public void ProgressAndStartSayWhatTheyCountAndTheLongSentenceIsTheTooltip() => WithPage(page =>
    {
        TagQueueSnapshot queue = Queue(2, Item(1, TagQueueState.Done), Item(2, TagQueueState.Skipped), Item(3), Item(4), Item(5));
        Assert.Equal("2 of 5 done", TaggingQueueView.DoneText(queue, null, false));
        Assert.Equal("Continue tagging (no. 3)", TaggingQueueView.StartLabel(queue));
        Render(page, queue);
        Assert.Equal("2 of 5 done", page.ProgressText.Text);
        Assert.Equal("Open: 3 · Finished: 1 · Skipped: 1", ToolTip.GetTip(page.ProgressText));
        Assert.Equal("2 von 5 erledigt", WithLanguage("de", () => TaggingQueueView.DoneText(queue, null, false)));
        Assert.Equal("Weiter taggen (Nr. 3)", WithLanguage("de", () => TaggingQueueView.StartLabel(queue)));
    });

    [Fact]
    public void ABrowseHintStandsWithTheWalkOnlyWhenOneIsUnderWay() => WithPage(page =>
    {
        Render(page, Queue(0, Item(1), Item(2)));
        Assert.False(page.BrowseHint.IsVisible); // fresh queue: nothing was shown yet
        Render(page, Queue(1, Item(1), Item(2), Item(3)));
        Assert.Contains("nothing as finished", page.BrowseHint.Text, StringComparison.Ordinal);
    });

    [Fact]
    public void TypeAndGroupColumnsStandOnlyWhileTheShownRowsDiffer() => WithPage(page =>
    {
        Render(page, Queue(0, Item(1), Item(2)));
        Assert.False(HeaderVisible(page, 2)); // type
        Assert.False(HeaderVisible(page, 5)); // group
        Assert.True(HeaderVisible(page, 3)); // file stays
        Render(page, Queue(0, Item(1), Item(2, kind: SavedMediaKind.Video)));
        Assert.True(HeaderVisible(page, 2));
        Assert.False(HeaderVisible(page, 5));
        TagQueueGroup group = new("g1", "Concert", 0, Array.Empty<string>());
        Render(page, new TagQueueSnapshot(1, 0, Array.Empty<string>(), new[] { Item(1) with { GroupId = "g1" }, Item(2) }, TagQueueLoadState.Ready, new[] { group }));
        Assert.True(HeaderVisible(page, 5));
    });

    private static bool HeaderVisible(TaggingPage page, int column) =>
        page.ColumnHeader.Children.OfType<Control>().Single(child => Grid.GetColumn(child) == column).IsVisible;

    private static T WithLanguage<T>(string code, Func<T> read)
    {
        Texts.SetLanguage(code);
        try { return read(); }
        finally { Texts.SetLanguage("en"); }
    }
}
