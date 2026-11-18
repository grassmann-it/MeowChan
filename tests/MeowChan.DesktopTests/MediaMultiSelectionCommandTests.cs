using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.MediaActions;
using MeowChan.Gallery;
using Avalonia.Input;

namespace MeowChan.DesktopTests;

// KB-06 / MD-01 (UX review 17.11.2026): the Media page's one-medium commands with several selected tiles are never silent, and a right-click
// outside the selection targets the clicked tile alone. Headless over the tile Enter fixture (no layout, no window).
[Collection("Booru exact navigation")]
public sealed class MediaMultiSelectionCommandTests
{
    private static List<(string Id, MediaLibraryEntry Entry)> RecordActions(MediaTileEnterTests.Fixture fixture)
    {
        List<(string, MediaLibraryEntry)> calls = new();
        fixture.Page.ExecuteMediaMenuActionAsync = (id, entry, _) => { calls.Add((id, entry)); return Task.CompletedTask; };
        return calls;
    }

    [Theory]
    [InlineData("media.details")]
    [InlineData("media.show-in-folder")]
    [InlineData("media.copy-path")]
    public void AOneFileCommandWithSeveralSelectedSaysItNeedsASingleItem(string id) => MediaTileEnterTests.Run(fixture =>
    {
        var calls = RecordActions(fixture);
        fixture.Grid.Selection.Select(0);
        fixture.Grid.Selection.Select(2);
        fixture.Page.RunMediaCommandByShortcutAsync(id).GetAwaiter().GetResult();
        Assert.Empty(calls);
        Assert.Equal(MediaMenuReasons.SeveralSelected, fixture.SelectionNote);
    });

    [Fact]
    public void ARatingKeyWithSeveralSelectedRatesEverySelectedSavedTileWhoseRatingIsKnown() => MediaTileEnterTests.Run(fixture =>
    {
        var calls = RecordActions(fixture);
        MediaLibraryEntryState known = new(MediaLibraryFlag.No, true, null, false, [], [], null);
        MediaLibraryTileRow[] rows = fixture.Rows.Select(row => row with { StateOf = _ => known }).ToArray();
        fixture.Grid.ItemsSource = rows;
        foreach (int index in new[] { 0, 2, MediaTileEnterTests.Fixture.MissingIndex }) fixture.Grid.Selection.Select(index);
        fixture.Page.RunMediaCommandByShortcutAsync("media.rating.set-4").GetAwaiter().GetResult();
        // The missing file is left alone; the two saved ones get the rating, in page order.
        Assert.Equal(new[] { ("media.rating.set-4", fixture.Entries[0]), ("media.rating.set-4", fixture.Entries[2]) }, calls);
        Assert.Null(fixture.SelectionNote);
    });

    [Fact]
    public void RatingEveryFrozenTargetSurvivesThePassiveRowReplacementCausedByItsFirstWrite() => MediaTileEnterTests.Run(fixture =>
    {
        MediaLibraryEntryState known = new(MediaLibraryFlag.No, true, null, false, [], [], null);
        MediaLibraryTileRow[] rows = fixture.Rows.Select(row => row with { StateOf = _ => known }).ToArray();
        fixture.Grid.ItemsSource = rows;
        fixture.Grid.Selection.Select(2);
        fixture.Grid.Selection.Select(0);
        List<MediaLibraryEntry> rated = new();
        fixture.Page.ExecuteMediaMenuActionAsync = (_, entry, current) =>
        {
            Assert.True(current());
            rated.Add(entry);
            fixture.Grid.ItemsSource = rows.Select(row => row with { }).ToArray();
            Assert.True(current());
            return Task.CompletedTask;
        };
        fixture.Page.RunMediaCommandByShortcutAsync("media.rating.set-4").GetAwaiter().GetResult();
        Assert.Equal(new[] { fixture.Entries[0], fixture.Entries[2] }, rated);
    });

    [Fact]
    public void ARatingKeyWithSeveralSelectedThatNoneCanTakeSaysSo() => MediaTileEnterTests.Run(fixture =>
    {
        var calls = RecordActions(fixture);
        fixture.Grid.Selection.Select(0);
        fixture.Grid.Selection.Select(2); // the fixture rows carry no state: the ratings are not known
        fixture.Page.RunMediaCommandByShortcutAsync("media.rating.clear").GetAwaiter().GetResult();
        Assert.Empty(calls);
        Assert.Equal(MediaMenuReasons.Unavailable, fixture.SelectionNote);
    });

    [Fact]
    public void ARightClickOutsideTheSelectionSelectsTheClickedTileAloneAndInsideKeepsIt() => MediaTileEnterTests.Run(fixture =>
    {
        System.Reflection.MethodInfo target = typeof(MediaPage).GetMethod("TargetForMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        fixture.Grid.Selection.Select(0);
        fixture.Grid.Selection.Select(2);
        // Inside: the selection stays (the menu then acts on all of it).
        Assert.Same(fixture.Rows[2], target.Invoke(fixture.Page, [fixture.Rows[2]]));
        Assert.Equal(new[] { 0, 2 }, fixture.Grid.Selection.SelectedIndexes.Order().ToArray());
        // Outside: the clicked tile becomes the selection (as Archive and Downloads do), so every entry acts on it.
        Assert.Same(fixture.Rows[1], target.Invoke(fixture.Page, [fixture.Rows[1]]));
        Assert.Equal(new[] { 1 }, fixture.Grid.Selection.SelectedIndexes.ToArray());
        // Empty grid space keeps whatever is selected.
        Assert.Null(target.Invoke(fixture.Page, [null]));
        Assert.Equal(new[] { 1 }, fixture.Grid.Selection.SelectedIndexes.ToArray());
    });

    [Theory]
    [InlineData(Key.T, KeyModifiers.None)]
    [InlineData(Key.R, KeyModifiers.Control)]
    [InlineData(Key.I, KeyModifiers.None)]
    [InlineData(Key.O, KeyModifiers.Control)]
    [InlineData(Key.C, KeyModifiers.Control | KeyModifiers.Shift)]
    public void RoutedSingleTargetShortcutsExplainTheMultiSelection(Key key, KeyModifiers modifiers) => MediaTileEnterTests.Run(fixture =>
    {
        var calls = RecordActions(fixture);
        fixture.Grid.Selection.Select(0);
        fixture.Grid.Selection.Select(2);
        fixture.Press(fixture.Container(0), key, modifiers);
        Assert.True(fixture.LastHandled);
        Assert.Empty(calls);
        Assert.Equal(MediaMenuReasons.SeveralSelected, fixture.SelectionNote);
        Assert.Equal(2, fixture.Grid.Selection.Count);
    });

    [Theory]
    [InlineData(Key.Q, KeyModifiers.None)]
    [InlineData(Key.Delete, KeyModifiers.None)]
    [InlineData(Key.R, KeyModifiers.None)]
    [InlineData(Key.T, KeyModifiers.Control | KeyModifiers.Shift)]
    [InlineData(Key.F, KeyModifiers.None)]
    public void UnavailableBatchShortcutsGiveAVisibleHint(Key key, KeyModifiers modifiers) => MediaTileEnterTests.Run(fixture =>
    {
        fixture.Grid.Selection.Select(0);
        fixture.Grid.Selection.Select(2);
        fixture.Press(fixture.Container(0), key, modifiers);
        Assert.True(fixture.LastHandled);
        Assert.Equal(MediaMenuReasons.Unavailable, fixture.SelectionNote);
        Assert.Equal(2, fixture.Grid.Selection.Count);
    });

    [Theory]
    [InlineData("media.details")]
    [InlineData("media.export-original")]
    [InlineData("media.rating.set-4")]
    public void MissingHostActionsStillExplainRatherThanSwallowTheShortcut(string id) => MediaTileEnterTests.Run(fixture =>
    {
        fixture.Grid.Selection.Select(0);
        fixture.Grid.Selection.Select(2);
        fixture.Page.RunMediaCommandByShortcutAsync(id).GetAwaiter().GetResult();
        Assert.Equal(id == "media.details" ? MediaMenuReasons.SeveralSelected : MediaMenuReasons.Unavailable, fixture.SelectionNote);
    });

    [Fact]
    public void RightClickTargetReplacementDoesNotPublishAnEmptySelection() => MediaTileEnterTests.Run(fixture =>
    {
        System.Reflection.MethodInfo target = typeof(MediaPage).GetMethod("TargetForMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        fixture.Grid.Selection.Select(0);
        fixture.Grid.Selection.Select(2);
        List<int> observedCounts = new();
        fixture.Grid.SelectionChanged += (_, _) => observedCounts.Add(fixture.Grid.Selection.Count);

        target.Invoke(fixture.Page, [fixture.Rows[1]]);

        Assert.DoesNotContain(0, observedCounts);
        Assert.Equal(new[] { 1 }, fixture.Grid.Selection.SelectedIndexes.ToArray());
    });

    [Fact]
    public void TheMenuOfASeveralSelectionTalksAboutTheSelection()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
        string note = Texts.T("media.menu.note.selection", 3);
        Assert.Contains("all selected items (3)", note);
        Assert.DoesNotContain("the item you clicked", note);
    }

    [Theory]
    [InlineData(0, 8)]
    [InlineData(7, 8)]
    [InlineData(10, 88)]
    [InlineData(144, 888)]
    [InlineData(1000, 8888)]
    [InlineData(250_000, 8888)]
    public void TheCounterReservesTheDigitsOfTheShownTileCountUpToFour(int shown, int sample) =>
        Assert.Equal(sample, MediaPage.CounterReserveSample(shown));
}
