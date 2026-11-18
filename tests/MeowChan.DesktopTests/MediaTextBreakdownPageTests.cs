using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

// The "where was it found" row on a constructed MediaPage (no window, profile or asset): row visibility, the toggle rules, the counts with a
// page-side term, the scope that survives an empty box and goes with the resets, the scoped chip and the live summary. The library is four
// threads: "cat" is in a thread title (a), in a file name (b) and as a tag of a thread ("cats", c); d has no hit.
[Collection("Booru exact navigation")]
public sealed class MediaTextBreakdownPageTests
{
    private const BindingFlags InstanceNonPublic = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Guid Cats = Guid.Parse("00000000-0000-0000-0000-0000000000c1");
    private static readonly DateTimeOffset Day = DateTimeOffset.UnixEpoch;

    private static MediaLibraryEntry Entry(string thread, string title, string name, char hash) =>
        new(new ResourceId("4chan", "g", thread), title, 1, Day, Day, thread, long.Parse(thread), null, 0, "att-v1:" + new string(hash, 64),
            "/dir/" + thread, "asset-" + thread, new string(hash, 64), 1, "m" + thread, "original", name, SavedMediaKind.Video,
            MediaLibraryPresence.Present, false, false);

    private static readonly MediaLibraryEntry A = Entry("1", "Cat thread", "x1.webm", 'a'), B = Entry("2", "Dogs", "cat2.webm", 'b'),
        C = Entry("3", "Birds", "z.webm", 'c'), D = Entry("4", "Fish", "f.webm", 'd');

    private static MediaLibraryUserData Data()
    {
        Assert.True(PrivateTagName.TryCreate("cats", out PrivateTagName? name, out _));
        return new(new PrivateTagStoreSnapshot(1, "fp", [new PrivateTagDefinition(new TagId(Cats), name!)], [new PrivateThreadTagAssignment(new TagId(Cats), C.Thread)], []),
            [], []);
    }

    private sealed class Fixture : IDisposable
    {
        internal MediaPage Page { get; } = new(_ => throw new InvalidOperationException("The fixture must not open archive data."));

        internal Fixture()
        {
            Page.IsVisible = true;
            // Private base members are not returned by reflection on the derived MediaPage type.
            typeof(Avalonia.Visual).GetMethod("UpdateIsEffectivelyVisible", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(Page, [true]);
            Page.RestoreLayout(MediaPageLayout.Standard with { Panel = false }); // the media panel's cursor needs the platform's cursor factory
            Set("_snapshot", new MediaLibrarySnapshot([A, B, C, D], [], [], complete: true));
            Set("_userData", Data());
            Present();
        }

        internal void Set(string field, object? value) => typeof(MediaPage).GetField(field, InstanceNonPublic)!.SetValue(Page, value);
        internal object? Get(string field) => typeof(MediaPage).GetField(field, InstanceNonPublic)!.GetValue(Page);

        internal void Present() => typeof(MediaPage).GetMethod("Present", InstanceNonPublic)!.Invoke(Page, [false]); // a query presentation (keepView: false)
        internal void FilterEdited() => typeof(MediaPage).GetMethod("FilterEdited", InstanceNonPublic)!.Invoke(Page, null);

        internal void Type(string text)
        {
            Page.NameFilter.Text = text;
            Page.NameFilter.FlushQuery();
            Dispatcher.UIThread.RunJobs();
            FilterEdited(); // whatever the field's debounce did, the page filters for the text now
        }

        internal string? ChipLabel(MediaFilterTerm term)
        {
            object? label = typeof(MediaPage).GetMethod("TermLabel", InstanceNonPublic)!.Invoke(Page, [term]);
            return label == null ? null : (string?)label.GetType().GetField("Item1")!.GetValue(label);
        }

        internal string[] Shown() => Page.ShownEntries.Select(entry => entry.Thread.Thread!).Order().ToArray();
        internal string[] Parts() => Page.Breakdown.PartButtons.Select(button => (string)button.Content!).ToArray();
        internal void Click(MediaTextFields field)
        {
            ToggleButton button = Page.Breakdown.PartButtons.Single(part => MediaTextBreakdownRow.FieldOf(part) == field);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }

        public void Dispose() => Page.EndLifetime();
    }

    private static void OnDispatcher(Action test) => BooruComponentDispatcher.Run(test);

    [Fact]
    public void TheRowIsThereForWordsThatHitAndNamesWhereTheyWereFound() => OnDispatcher(() =>
    {
        using Fixture f = new();
        Assert.False(f.Page.Breakdown.IsVisible); // no text
        f.Type("cat");
        Assert.True(f.Page.Breakdown.IsVisible);
        Assert.Equal(["All 3", "1 in a file name", "1 in a thread title", "1 as tag"], f.Parts());
        Assert.Equal(["1", "2", "3"], f.Shown());
        Assert.Equal("Where “cat” was found", Avalonia.Automation.AutomationProperties.GetName(f.Page.Breakdown));
        Assert.Equal(MediaTextFields.All, f.Page.TextScope);
        Assert.Equal([true, false, false, false], f.Page.Breakdown.PartButtons.Select(button => button.IsChecked == true));
    });

    [Fact]
    public void NoWordsAndNoHitsMeanNoRow() => OnDispatcher(() =>
    {
        using Fixture f = new();
        f.Type("   ");
        Assert.False(f.Page.Breakdown.IsVisible);
        f.Type("\"\"");
        Assert.False(f.Page.Breakdown.IsVisible);
        f.Type("zebra"); // nothing anywhere: the empty state speaks
        Assert.False(f.Page.Breakdown.IsVisible);
        Assert.Empty(f.Shown());
        f.Type("cat");
        Assert.True(f.Page.Breakdown.IsVisible);
        f.Type(string.Empty);
        Assert.False(f.Page.Breakdown.IsVisible);
    });

    [Fact]
    public void AFieldClickedUnderAllSelectsOnlyItAndTheGridFollowsWhileTheCountsStay() => OnDispatcher(() =>
    {
        using Fixture f = new();
        f.Type("cat");
        f.Click(MediaTextFields.Tag);
        Assert.Equal(MediaTextFields.Tag, f.Page.TextScope);
        Assert.Equal(["3"], f.Shown());
        Assert.Equal(["All 3", "1 in a file name", "1 in a thread title", "1 as tag"], f.Parts()); // the parts do not shrink with the scope
        Assert.Equal([false, false, false, true], f.Page.Breakdown.PartButtons.Select(button => button.IsChecked == true));
        f.Click(MediaTextFields.Title); // further fields toggle on
        Assert.Equal(MediaTextFields.Title | MediaTextFields.Tag, f.Page.TextScope);
        Assert.Equal(["1", "3"], f.Shown());
        f.Click(MediaTextFields.Title);
        f.Click(MediaTextFields.Tag); // the last one off: "All" again
        Assert.Equal(MediaTextFields.All, f.Page.TextScope);
        Assert.Equal(["1", "2", "3"], f.Shown());
        f.Click(MediaTextFields.Name);
        f.Click(MediaTextFields.All); // "All" is always the default
        Assert.Equal(MediaTextFields.All, f.Page.TextScope);
    });

    [Fact]
    public void TheScopeShowsInTheChipAndTheNameChipAndFollowsLanguage() => OnDispatcher(() =>
    {
        using Fixture f = new();
        f.Type("cat");
        f.Click(MediaTextFields.Tag);
        SearchScopeChip chip = f.Page.NameFilter.ScopeChip!;
        Assert.Equal("Tags", chip.LabelOverride);
        Assert.Equal([MediaFilterTerm.In(MediaTextFields.Tag), MediaFilterTerm.Name("cat")], f.Page.FilterTerms);
        Assert.Equal("“cat” in tags", f.ChipLabel(MediaFilterTerm.Name("cat")));
        Assert.Null(f.ChipLabel(MediaFilterTerm.In(MediaTextFields.Tag))); // the scope is the name chip's wording, not a chip of its own
        f.Click(MediaTextFields.Title);
        Assert.Equal("2 fields", chip.LabelOverride);
        Assert.Equal("“cat” in thread titles, tags", f.ChipLabel(MediaFilterTerm.Name("cat")));
        f.Click(MediaTextFields.Title);
        f.Click(MediaTextFields.Tag);
        Assert.Null(chip.LabelOverride);
        Assert.Equal([MediaFilterTerm.Name("cat")], f.Page.FilterTerms); // "All" writes nothing
        Assert.Equal("Search: “cat”", f.ChipLabel(MediaFilterTerm.Name("cat")));
    });

    [Fact]
    public void TheScopeSurvivesAnEmptyBoxAndGoesWithTheResetsAndTheNameChip() => OnDispatcher(() =>
    {
        using Fixture f = new();
        f.Type("cat");
        f.Click(MediaTextFields.Tag);
        f.Type(string.Empty);
        Assert.False(f.Page.Breakdown.IsVisible);
        Assert.Equal(MediaTextFields.Tag, f.Page.TextScope);
        Assert.Empty(f.Page.FilterTerms); // no text, no scope term
        f.Type("cat");
        Assert.Equal(["3"], f.Shown()); // the scope came back with the words
        // Reset filters.
        f.Page.ResetFilters();
        Assert.Equal(MediaTextFields.All, f.Page.TextScope);
        Assert.Null(f.Page.NameFilter.ScopeChip!.LabelOverride);
        // The clear mark of the field.
        f.Type("cat");
        f.Click(MediaTextFields.Name);
        f.Page.NameFilter.ClearButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(MediaTextFields.All, f.Page.TextScope);
        // Removing the name chip.
        f.Type("cat");
        f.Click(MediaTextFields.Name);
        typeof(MediaPage).GetMethod("RemoveFilterTerm", InstanceNonPublic)!.Invoke(f.Page, [MediaFilterTerm.Name("cat")]);
        Assert.Equal(MediaTextFields.All, f.Page.TextScope);
        Assert.Equal(string.Empty, f.Page.NameFilter.Text);
    });

    private static Avalonia.Input.KeyEventArgs EscapeKey(Avalonia.Input.KeyModifiers modifiers = Avalonia.Input.KeyModifiers.None) =>
        new() { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Escape, KeyModifiers = modifiers };

    [Fact]
    public void EscapeInTheSearchFieldResetsTheTextAndTheScopeTogetherAndKeepsTheOtherChips() => OnDispatcher(() =>
    {
        using Fixture f = new();
        f.Type("cat");
        f.Click(MediaTextFields.Tag);
        Assert.Null(f.Page.AddFilterTerm(MediaFilterTerm.Source("4chan")));
        Assert.Equal(["3"], f.Shown());
        Avalonia.Input.KeyEventArgs escape = EscapeKey();
        f.Page.NameFilter.RaiseEvent(escape);
        Assert.True(escape.Handled); // the first Escape stays in the field: the surface around it stays open
        Assert.Equal(string.Empty, f.Page.NameFilter.Text ?? string.Empty);
        Assert.Equal(MediaTextFields.All, f.Page.TextScope);
        Assert.Null(f.Page.NameFilter.ScopeChip!.LabelOverride);
        Assert.Contains(MediaFilterTerm.Source("4chan"), f.Page.FilterTerms); // other chips stay
        Assert.DoesNotContain(f.Page.FilterTerms, term => term.Field is MediaFilterField.Name or MediaFilterField.In);
        // Typing again starts from "All", not from the old scope.
        f.Type("cat");
        Assert.Equal(MediaTextFields.All, f.Page.TextScope);
        Assert.Equal(["1", "2", "3"], f.Shown());
    });

    [Fact]
    public void EscapeInAnEmptyFieldWithAScopeResetsTheScopeThenLeavesAndAModifiedEscapeIsLeftAlone() => OnDispatcher(() =>
    {
        using Fixture f = new();
        f.Type("cat");
        f.Click(MediaTextFields.Title);
        f.Page.NameFilter.Text = string.Empty;
        f.Page.NameFilter.FlushQuery();
        Dispatcher.UIThread.RunJobs();
        f.FilterEdited();
        Assert.Equal(MediaTextFields.Title, f.Page.TextScope); // the scope waits for words
        Avalonia.Input.KeyEventArgs modified = EscapeKey(Avalonia.Input.KeyModifiers.Shift);
        f.Page.NameFilter.RaiseEvent(modified);
        Assert.Equal(MediaTextFields.Title, f.Page.TextScope);
        Avalonia.Input.KeyEventArgs escape = EscapeKey();
        f.Page.NameFilter.RaiseEvent(escape);
        Assert.Equal(MediaTextFields.All, f.Page.TextScope);
        Assert.True(escape.Handled); // resetting the scope was the Escape's work: the field is not left by it
        Avalonia.Input.KeyEventArgs second = EscapeKey();
        f.Page.NameFilter.RaiseEvent(second);
        Assert.False(second.Handled); // nothing left to reset: the empty field's Escape goes on to leave it
    });

    [Fact]
    public void ANarrowedScopeThatFindsNothingOffersToSearchEverywhere() => OnDispatcher(() =>
    {
        using Fixture f = new();
        f.Type("birds"); // only in a thread title
        Assert.Equal(["All 1", "1 in a thread title"], f.Parts());
        f.Page.SetTextScope(MediaTextFields.Tag);
        Assert.Empty(f.Shown());
        Assert.Equal("Nothing in tags · 1 elsewhere", f.Page.Breakdown.ElsewhereText);
        Assert.Equal(["All 1", "1 in a thread title", "0 as tag"], f.Parts()); // the selected field stays, as 0, so it can be undone
        f.Page.Breakdown.EverywhereButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(MediaTextFields.All, f.Page.TextScope);
        Assert.Equal(["3"], f.Shown());
        Assert.Null(f.Page.Breakdown.ElsewhereText);
    });

    [Fact]
    public void WordsThatOnlyMatchAcrossFieldsShowAllAndTheNote() => OnDispatcher(() =>
    {
        using Fixture f = new();
        f.Type("dogs cat2"); // the title of B and the name of B: every word hits, but no single field holds both
        Assert.Equal(["All 1"], f.Parts());
        Assert.Equal("The words only match across several fields.", f.Page.Breakdown.NoteText);
        f.Type("cat"); // one word: no note
        Assert.Null(f.Page.Breakdown.NoteText);
    });

    [Fact]
    public void ThePageSideTermsNarrowTheCountsToo() => OnDispatcher(() =>
    {
        using Fixture f = new();
        // Only a is in the tagging queue: the row counts what the grid can show with that term.
        if (TagQueueKeys.Of(A) is not { } queued) throw new InvalidOperationException("The entry has no queue key.");
        f.Set("_queueKeys", new HashSet<TagQueueKey> { queued });
        Assert.Null(f.Page.AddFilterTerm(MediaFilterTerm.InQueue()));
        f.Type("cat");
        Assert.Equal(["1"], f.Shown());
        Assert.Equal(["All 1", "1 in a thread title"], f.Parts());
    });

    [Fact]
    public void ATokenWithoutATagTermIsNotRefusedAsTooManyTags() => OnDispatcher(() =>
    {
        // The tag-list guard of the query admission has nothing to check without a tag term (it once refused every type: or source: token).
        using Fixture f = new();
        f.Page.NameFilter.Text = "type:video cat";
        Assert.True(f.Page.SubmitQuery());
        Assert.Null(f.Page.QueryProblem);
        Assert.Equal("cat", f.Page.NameFilter.Text);
        Assert.Contains(MediaFilterTerm.Type(MediaLibraryKindFilter.Video), f.Page.FilterTerms);
    });

    [Fact]
    public void TheInTokenMovesIntoTheScopeAndTheTextStays() => OnDispatcher(() =>
    {
        using Fixture f = new();
        f.Page.NameFilter.Text = "in:tag cat";
        Assert.True(f.Page.SubmitQuery());
        Assert.Null(f.Page.QueryProblem);
        Assert.Equal(MediaTextFields.Tag, f.Page.TextScope);
        Assert.Equal("cat", f.Page.NameFilter.Text);
        Assert.Equal(["3"], f.Shown());
        Assert.Equal("in:tag cat", f.Page.FiltersAsText());
        Assert.Equal("Tags", f.Page.NameFilter.ScopeChip!.LabelOverride);
        // A problem keeps the token as text, and the scope is unchanged.
        f.Page.NameFilter.Text = "in:bogus cat";
        Assert.True(f.Page.SubmitQuery());
        Assert.Equal(MediaTextFields.Tag, f.Page.TextScope);
        Assert.Contains("in:bogus", f.Page.NameFilter.Text);
    });

    [Fact]
    public void TheSummaryIsSaidOnceAfterTheQuietMomentAndNotAgainForTheSameResult() => OnDispatcher(() =>
    {
        using Fixture f = new();
        f.Type("c");
        f.Type("ca");
        f.Type("cat");
        Assert.Null(f.Page.BreakdownAnnouncement); // typing says nothing while it goes on
        f.Page.FlushBreakdownAnnouncement();
        Assert.Equal("Where “cat” was found: All 3, 1 in a file name, 1 in a thread title, 1 as tag", f.Page.BreakdownAnnouncement);
        // The same result again changes nothing.
        f.Present();
        f.Page.FlushBreakdownAnnouncement();
        Assert.Equal("Where “cat” was found: All 3, 1 in a file name, 1 in a thread title, 1 as tag", f.Page.BreakdownAnnouncement);
        // Choosing a field changes what is on, not what was found: the summary is the same and says nothing again.
        f.Click(MediaTextFields.Tag);
        f.Page.FlushBreakdownAnnouncement();
        Assert.Equal("Where “cat” was found: All 3, 1 in a file name, 1 in a thread title, 1 as tag", f.Page.BreakdownAnnouncement);
        // The row goes: the summary is cleared, so the next one is heard again.
        f.Type(string.Empty);
        Assert.Null(f.Page.BreakdownAnnouncement);
    });

    [Fact]
    public void TheRowAndTheChipFollowTheLanguage() => OnDispatcher(() =>
    {
        using Fixture f = new();
        f.Type("cat");
        f.Click(MediaTextFields.Tag);
        Texts.SetLanguage("de");
        f.Page.GetType().GetMethod("LocalizeFilters", InstanceNonPublic)!.Invoke(f.Page, null);
        Assert.Equal("Alle 3", f.Parts()[0]);
        Assert.Equal("Tags", f.Page.NameFilter.ScopeChip!.LabelOverride);
        Assert.Equal("Wo „cat“ gefunden wurde", Avalonia.Automation.AutomationProperties.GetName(f.Page.Breakdown));
    });
}
