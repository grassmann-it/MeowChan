using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan.DesktopTests;

// The "where was it found" row of the Media search: the rules (MediaTextBreakdown, MediaTextCounts), the control that shows it
// (MediaTextBreakdownRow) and its texts in every bundled language. The page that owns the row is driven in MediaTextBreakdownPageTests.
[Collection("Localization")]
public sealed class MediaTextBreakdownTests : IDisposable
{
    public MediaTextBreakdownTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private const MediaTextFields Name = MediaTextFields.Name, Title = MediaTextFields.Title, Board = MediaTextFields.Board,
        Source = MediaTextFields.Source, Tag = MediaTextFields.Tag, All = MediaTextFields.All;

    private static MediaLibraryEntry Entry(string post, string? sha, MediaLibraryPresence presence = MediaLibraryPresence.Present) =>
        new(new ResourceId("4chan", "g", "1"), "thread", 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, post, 1, null, 1, null,
            "/synthetic", "asset-" + post, sha, sha == null ? null : 7, post, "original", post + ".webm", SavedMediaKind.Video, presence, false, false);

    private static MediaTextCounts Counts(int all = 0, int name = 0, int title = 0, int board = 0, int source = 0, int tag = 0, MediaTextFields scope = All, int inScope = 0) =>
        new(all, name, title, board, source, tag, scope, inScope);

    // ---- the counts are tiles ----------------------------------------------------------------------------------------

    [Fact]
    public void CountsAreTilesAndATileCountsOncePerFieldHoweverManyOccurrencesHit()
    {
        MediaLibraryEntry a = Entry("a", new string('a', 64)), b = Entry("b", new string('a', 64)), c = Entry("c", new string('c', 64)), loose = Entry("d", null);
        MediaTextTally[] tally =
        [
            new(a, Name), new(b, Title | Tag), // the same bytes: one tile, in three fields
            new(c, Title), new(loose, Name | Title),
        ];
        MediaTextCounts counts = MediaTextCounts.Of(tally, includeMissing: false, All, inScope: 3);
        Assert.Equal(3, counts.All); // a+b, c, loose
        Assert.Equal(2, counts.Name); // a+b, loose
        Assert.Equal(3, counts.Title); // a+b, c, loose
        Assert.Equal(1, counts.Tag);
        Assert.Equal(0, counts.Board);
        Assert.Equal(0, counts.Source);
        Assert.Equal(3, counts.InScope);
        Assert.Equal(counts.Title, counts.Of(Title));
        Assert.Equal(counts.All, counts.Of(All));
    }

    [Fact]
    public void AMissingOccurrenceCountsOnlyWhenThePageShowsMissingFiles()
    {
        MediaLibraryEntry present = Entry("a", new string('a', 64)), missing = Entry("m", null, MediaLibraryPresence.Missing);
        MediaTextTally[] tally = [new(present, Name), new(missing, Name)];
        Assert.Equal(1, MediaTextCounts.Of(tally, includeMissing: false, All, 1).All);
        Assert.Equal(2, MediaTextCounts.Of(tally, includeMissing: true, All, 2).All);
        Assert.Equal(MediaLibraryGrouping.CountTiles([present, missing], includeMissing: true), MediaTextCounts.Of(tally, true, All, 2).All);
    }

    [Fact]
    public void WordsOnlyMatchingAcrossFieldsGiveAllWithoutAnyField()
    {
        MediaTextCounts counts = Counts(all: 5);
        Assert.True(counts.OnlyAcrossFields);
        Assert.False(Counts(all: 5, tag: 1).OnlyAcrossFields);
        Assert.False(Counts().OnlyAcrossFields); // nothing found is not "across"
    }

    [Fact]
    public void ANarrowedScopeThatFindsNothingWhileTheTextIsElsewhereIsRecognized()
    {
        Assert.True(Counts(all: 12, tag: 0, title: 12, scope: Tag, inScope: 0).NothingInScope);
        Assert.False(Counts(all: 12, scope: Tag, inScope: 3).NothingInScope);
        Assert.False(Counts(all: 12, scope: All, inScope: 0).NothingInScope); // "All" has nowhere else to look
        Assert.False(Counts(all: 0, scope: Tag, inScope: 0).NothingInScope); // nothing anywhere: the empty state speaks
    }

    // ---- when the row is there -------------------------------------------------------------------------------------------

    [Fact]
    public void TheRowIsThereOnlyForWordsThatHitSomewhere()
    {
        Assert.True(MediaTextBreakdown.IsShown(Counts(all: 1), hasWords: true));
        Assert.False(MediaTextBreakdown.IsShown(Counts(all: 1), hasWords: false)); // empty text, white space or ""
        Assert.False(MediaTextBreakdown.IsShown(Counts(all: 0), hasWords: true)); // the empty state applies
        Assert.False(MediaTextBreakdown.IsShown(null, hasWords: true));
    }

    // ---- the parts -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void UnderAllOnlyFieldsWithAHitAreShownAndOnlyAllIsOn()
    {
        IReadOnlyList<MediaBreakdownPart> parts = MediaTextBreakdown.Parts(Counts(all: 57, name: 12, tag: 3), All);
        Assert.Equal([new MediaBreakdownPart(All, 57, true), new MediaBreakdownPart(Name, 12, false), new MediaBreakdownPart(Tag, 3, false)], parts);
    }

    [Fact]
    public void ASelectedFieldWithoutAHitStaysAsZeroSoItCanBeUndone()
    {
        IReadOnlyList<MediaBreakdownPart> parts = MediaTextBreakdown.Parts(Counts(all: 57, name: 12, tag: 3), Title | Tag);
        Assert.Equal([new MediaBreakdownPart(All, 57, false), new MediaBreakdownPart(Name, 12, false), new MediaBreakdownPart(Title, 0, true),
            new MediaBreakdownPart(Tag, 3, true)], parts);
    }

    [Fact]
    public void ThePartsDoNotDependOnTheScopeOtherThanWhichOnesAreOn()
    {
        MediaTextCounts counts = Counts(all: 57, name: 12, title: 5, tag: 3);
        Assert.Equal(MediaTextBreakdown.Parts(counts, All).Select(part => (part.Field, part.Count)),
            MediaTextBreakdown.Parts(counts, Tag).Select(part => (part.Field, part.Count)));
    }

    // ---- the toggle rules -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(31, 2, 2)] // a field clicked while "All" is on selects only that field
    [InlineData(2, 16, 18)] // further fields toggle on
    [InlineData(18, 2, 16)] // and off
    [InlineData(2, 2, 31)] // the last one off: "All" again
    [InlineData(18, 31, 31)] // "All" always means the default
    [InlineData(2, 31, 31)]
    [InlineData(15, 16, 31)] // four of five plus the fifth: all five is "All"
    [InlineData(31, 31, 31)]
    public void TheClickedPartTogglesTheScopeAsTheRulesSay(int scope, int clicked, int expected) =>
        Assert.Equal((MediaTextFields)expected, MediaTextBreakdown.Toggle((MediaTextFields)scope, (MediaTextFields)clicked));

    [Fact]
    public void SelectedListsTheFieldsInRowOrderAndNothingForAll()
    {
        Assert.Empty(MediaTextBreakdown.Selected(All));
        Assert.Equal([Title, Tag], MediaTextBreakdown.Selected(Tag | Title));
        Assert.Equal(["name", "title", "board", "source", "tag"], MediaTextBreakdown.Fields.Select(MediaTextBreakdown.Key));
        Assert.Throws<ArgumentOutOfRangeException>(() => MediaTextBreakdown.Key(All));
    }

    [Fact]
    public void TheTextTheRowRepeatsIsTrimmedAndCutWithAnEllipsis()
    {
        Assert.Equal("cat", MediaTextBreakdown.TextLabel("  cat  "));
        Assert.Equal("", MediaTextBreakdown.TextLabel(null));
        string longest = new('x', MediaTextBreakdown.TextLabelLength);
        Assert.Equal(longest, MediaTextBreakdown.TextLabel(longest));
        Assert.Equal(longest + "…", MediaTextBreakdown.TextLabel(longest + "y"));
        // Never half of a surrogate pair at the cut.
        string pairs = new string('x', MediaTextBreakdown.TextLabelLength - 1) + "\U0001F431\U0001F431";
        string cut = MediaTextBreakdown.TextLabel(pairs);
        Assert.EndsWith("…", cut);
        Assert.DoesNotContain(cut, character => char.IsSurrogate(character));
    }

    // ---- the control ---------------------------------------------------------------------------------------------------------------

    private static MediaTextBreakdownView View(MediaTextFields scope, string? note = null, string? elsewhere = null, params MediaTextFields[] fields) =>
        new("Where “cat” was found",
            new[] { new MediaTextBreakdownView.Part(All, "All 57", scope == All) }.Concat(fields.Select(field =>
                new MediaTextBreakdownView.Part(field, "n in " + field, scope != All && (scope & field) != 0))).ToArray(),
            note, elsewhere, elsewhere == null ? null : "Search everywhere");

    [Fact]
    public void TheRowShowsOneToggleButtonPerPartNamedByItsSentence()
    {
        MediaTextBreakdownRow row = new();
        row.Show(View(All, null, null, Name, Tag));
        ToggleButton[] buttons = row.PartButtons.ToArray();
        Assert.Equal([All, Name, Tag], buttons.Select(MediaTextBreakdownRow.FieldOf));
        Assert.Equal(["All 57", "n in Name", "n in Tag"], buttons.Select(button => (string)button.Content!));
        Assert.Equal(buttons.Select(button => (string)button.Content!), buttons.Select(button => Avalonia.Automation.AutomationProperties.GetName(button)));
        Assert.Equal([true, false, false], buttons.Select(button => button.IsChecked == true));
        Assert.Equal("Where “cat” was found", Avalonia.Automation.AutomationProperties.GetName(row));
    }

    [Fact]
    public void AChangeKeepsTheButtonsOfThePartsThatStayAndFollowsTheChoiceNotTheClick()
    {
        MediaTextBreakdownRow row = new();
        row.Show(View(All, null, null, Name, Tag));
        ToggleButton all = row.PartButtons[0], name = row.PartButtons[1];
        // A click toggles the button itself; the page's decision (here: the scope stays "All") puts it right again.
        name.IsChecked = true;
        row.Show(View(All, null, null, Name, Tag));
        Assert.False(name.IsChecked);
        row.Show(View(Name, null, null, Name, Tag, MediaTextFields.Title));
        Assert.Same(all, row.PartButtons[0]);
        Assert.Same(name, row.PartButtons[1]);
        Assert.Equal(new[] { All, Name, Tag, MediaTextFields.Title }.Order(), row.PartButtons.Select(MediaTextBreakdownRow.FieldOf).Order());
        Assert.True(name.IsChecked);
        Assert.False(all.IsChecked);
        row.Show(View(All, null, null, Name));
        Assert.Equal([All, Name], row.PartButtons.Select(MediaTextBreakdownRow.FieldOf));
    }

    [Fact]
    public void AClickOnAPartIsReportedWithItsFieldAndTheNoteAndTheButtonShowOnlyWhenGiven()
    {
        MediaTextBreakdownRow row = new();
        List<MediaTextFields> invoked = new();
        int everywhere = 0;
        row.PartInvoked += invoked.Add;
        row.EverywhereInvoked += (_, _) => everywhere++;
        row.Show(View(All, "The words only match across several fields.", null, Title));
        row.PartButtons[1].RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        row.PartButtons[0].RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Assert.Equal([Title, All], invoked);
        Assert.Equal("The words only match across several fields.", row.NoteText);
        Assert.Null(row.ElsewhereText);
        Assert.False(row.EverywhereButton.IsEffectivelyVisible && row.ElsewhereText != null);
        row.Show(View(Tag, null, "Nothing in tags · 12 elsewhere", Tag));
        Assert.Null(row.NoteText);
        Assert.Equal("Nothing in tags · 12 elsewhere", row.ElsewhereText);
        Assert.Equal("Search everywhere", row.EverywhereButton.Content);
        row.EverywhereButton.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Assert.Equal(1, everywhere);
    }

    [Fact]
    public void TheRowIsOneTabStopThatStaysOnAPartThatIsStillThere()
    {
        MediaTextBreakdownRow row = new();
        row.Show(View(All, null, null, Name, Tag));
        Assert.Equal(All, row.TabStopField);
        Assert.Equal([true, false, false], row.PartButtons.Select(button => Avalonia.Input.KeyboardNavigation.GetIsTabStop(button)));
        // The focus is on "Tag" (the arrow keys moved it there): that part is the stop now.
        row.MarkCurrent(Tag);
        Assert.Equal(Tag, row.TabStopField);
        Assert.Equal([false, false, true], row.PartButtons.Select(button => Avalonia.Input.KeyboardNavigation.GetIsTabStop(button)));
        row.Show(View(Tag, null, null, Name, Tag));
        Assert.Equal(Tag, row.TabStopField);
        // The part went away: "All" takes the stop back.
        row.Show(View(All, null, null, Name));
        Assert.Equal(All, row.TabStopField);
        Assert.Equal([true, false], row.PartButtons.Select(button => Avalonia.Input.KeyboardNavigation.GetIsTabStop(button)));
    }

    [Fact]
    public void EscapeInTheRowAsksTheOwnerForTheSearchField()
    {
        MediaTextBreakdownRow row = new();
        row.Show(View(All, null, null, Name));
        int escapes = 0;
        row.EscapeRequested += (_, _) => escapes++;
        Avalonia.Input.KeyEventArgs key = new() { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Escape, KeyModifiers = Avalonia.Input.KeyModifiers.None };
        row.RaiseEvent(key);
        Assert.True(key.Handled);
        Assert.Equal(1, escapes);
        Avalonia.Input.KeyEventArgs other = new() { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.A, KeyModifiers = Avalonia.Input.KeyModifiers.None };
        row.RaiseEvent(other);
        Assert.False(other.Handled);
        Assert.Equal(1, escapes);
    }

    // ---- the texts -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ThePartSentencesReadAsWholeSentencesInEnglishAndGerman()
    {
        Assert.Equal("All 57", Texts.Count("media.breakdown.all", 57));
        Assert.Equal("12 in file names", Texts.Count("media.breakdown.field.name", 12));
        Assert.Equal("1 in a file name", Texts.Count("media.breakdown.field.name", 1));
        Assert.Equal("1 in a thread title", Texts.Count("media.breakdown.field.title", 1));
        Assert.Equal("3 as tag", Texts.Count("media.breakdown.field.tag", 3));
        Assert.Equal("1 as tag", Texts.Count("media.breakdown.field.tag", 1));
        Assert.Equal("“cat” in tags, thread titles", Texts.T("media.filter.nameChipScoped", "cat", "tags, thread titles"));
        Assert.Equal("3 fields", Texts.Count("search.scope.media.some", 3));
        Texts.SetLanguage("de");
        Assert.Equal("Alle 57", Texts.Count("media.breakdown.all", 57));
        Assert.Equal("12 in Dateinamen", Texts.Count("media.breakdown.field.name", 12));
        Assert.Equal("1 in einem Dateinamen", Texts.Count("media.breakdown.field.name", 1));
        Assert.Equal("1 in einer Quelle", Texts.Count("media.breakdown.field.source", 1));
        Assert.Equal("3 als Tag", Texts.Count("media.breakdown.field.tag", 3));
        Assert.Equal("„cat“ in Tags, Thread-Titel", Texts.T("media.filter.nameChipScoped", "cat", "Tags, Thread-Titel"));
        Assert.Equal("Wo „cat“ gefunden wurde", Texts.T("media.breakdown.name", "cat"));
    }

    [Fact]
    public void TheSingularFormsExistWhereTheLanguageNeedsThemAndPluralFormsWhereTheyDiffer()
    {
        Texts.SetLanguage("ru");
        Assert.Equal("1 в имени файла", Texts.Count("media.breakdown.field.name", 1));
        Assert.Equal("12 в именах файлов", Texts.Count("media.breakdown.field.name", 12));
        Assert.Equal("2 поля", Texts.Count("search.scope.media.some", 2));
        Assert.Equal("5 полей", Texts.Count("search.scope.media.some", 5));
        Texts.SetLanguage("pl");
        Assert.Equal("1 w nazwie pliku", Texts.Count("media.breakdown.field.name", 1));
        Assert.Equal("3 pola", Texts.Count("search.scope.media.some", 3));
        Texts.SetLanguage("ja");
        Assert.Equal("ファイル名に 1 件", Texts.Count("media.breakdown.field.name", 1));
    }

    [Fact]
    public void EveryLanguageHasEveryFieldSentenceAndScopeName()
    {
        string[] keys = MediaTextBreakdown.Fields.SelectMany(field => new[] { "media.breakdown.field." + MediaTextBreakdown.Key(field),
            "media.scope." + MediaTextBreakdown.Key(field) }).Concat(["media.breakdown.name", "media.breakdown.all", "media.breakdown.across",
            "media.breakdown.elsewhere", "media.breakdown.everywhere", "media.breakdown.summary", "media.filter.nameChipScoped", "search.scope.media.some"]).ToArray();
        foreach (string language in new[] { "en", "de" }.Concat(Texts.BundledPackCodes()))
        {
            Texts.SetLanguage(language);
            foreach (string key in keys)
            {
                string text = Texts.Count(key, 3);
                Assert.False(string.IsNullOrWhiteSpace(text) || text == key, language + " " + key);
            }
        }
    }
}
