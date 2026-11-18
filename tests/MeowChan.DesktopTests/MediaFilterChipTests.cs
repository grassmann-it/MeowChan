using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using Ursa.Controls;

namespace MeowChan.DesktopTests;

// M2: one chip model for every filter value (WorkspaceChips.CreateTermChip), the term set's rules (MediaFilterTermSet) and the
// chip labels in all twelve languages. The media page itself needs the asset loader; its chip row is driven in the media smokes.
[Collection("Localization")]
public sealed class MediaFilterChipTests : IDisposable
{
    private static readonly Guid Rpt = Guid.Parse("00000000-0000-0000-0000-0000000000b1");

    public MediaFilterChipTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static MediaFilterTerm Type(MediaLibraryKindFilter kind, bool exclude = false) => MediaFilterTerm.Type(kind, exclude);

    // ---- term set <-> filter ----------------------------------------------------------------------------------------

    private static readonly MediaFilterTerm[][] RoundTripSets =
    [
        [MediaFilterTerm.Tag(Rpt), Type(MediaLibraryKindFilter.Video, exclude: true)],
        [Type(MediaLibraryKindFilter.Image), Type(MediaLibraryKindFilter.Gif), MediaFilterTerm.Source("4chan", exclude: true)],
        [MediaFilterTerm.Board("4chan", "g"), MediaFilterTerm.Board("4chan", "b", exclude: true), MediaFilterTerm.Rating(3)],
        [Type(MediaLibraryKindFilter.Video), MediaFilterTerm.Sound(MediaLibrarySoundFilter.WithSound), MediaFilterTerm.Flag(MediaFilterField.Favorite)],
        [MediaFilterTerm.Source("8kun"), MediaFilterTerm.Board("4chan", "g", exclude: true), MediaFilterTerm.Flag(MediaFilterField.Untagged)],
    ];

    [Theory]
    [InlineData(0), InlineData(1), InlineData(2), InlineData(3), InlineData(4)]
    public void ATermSetAndItsFilterGoBackAndForthUnchanged(int set)
    {
        MediaFilterTerm[] terms = RoundTripSets[set];
        MediaLibraryFilter filter = MediaFilterTerms.ToFilter(terms);
        IReadOnlyList<MediaFilterTerm> back = MediaFilterTerms.FromFilter(filter);
        Assert.Equal(filter, MediaFilterTerms.ToFilter(back));
        Assert.Equal(MediaFilterTermSet.Ordered(terms).Select(MediaFilterTermSet.IdentityKey).Order(),
            MediaFilterTermSet.Ordered(back).Select(MediaFilterTermSet.IdentityKey).Order());
    }

    [Fact]
    public void ExcludedTypesSourcesAndBoardsReachTheFilter()
    {
        MediaLibraryFilter filter = MediaFilterTerms.ToFilter([Type(MediaLibraryKindFilter.Video, exclude: true), MediaFilterTerm.Source("4chan", exclude: true),
            MediaFilterTerm.Board("8kun", "b", exclude: true)]);
        Assert.Equal(new[] { MediaLibraryKindFilter.Image, MediaLibraryKindFilter.Gif, MediaLibraryKindFilter.Other }, filter.Kinds.Order());
        Assert.Equal(new[] { "4chan" }, filter.ExcludeSources);
        Assert.Equal(new[] { new MediaLibraryBoardKey("8kun", "b") }, filter.ExcludeBoards);
    }

    [Fact]
    public void TogglingSwitchesOnlyNegatableTermsAndTwiceIsTheSameFilter()
    {
        IReadOnlyList<MediaFilterTerm> terms = [MediaFilterTerm.Tag(Rpt), Type(MediaLibraryKindFilter.Image), MediaFilterTerm.Source("4chan"), MediaFilterTerm.Rating(2)];
        foreach (MediaFilterTerm term in terms)
        {
            IReadOnlyList<MediaFilterTerm> once = MediaFilterTermSet.Toggled(terms, term);
            if (!MediaFilterTerm.CanExclude(term.Field)) { Assert.Same(terms, once); continue; }
            Assert.True(once.Single(other => MediaFilterTermSet.Identity(other) == term).Exclude, term.Field.ToString());
            Assert.Equal(MediaFilterTerms.ToFilter(terms), MediaFilterTerms.ToFilter(MediaFilterTermSet.Toggled(once, term)));
        }
        // The switched value moves behind its new polarity, like the tag chips did.
        IReadOnlyList<MediaFilterTerm> tags = [MediaFilterTerm.Tag(Rpt), MediaFilterTerm.Tag(Guid.NewGuid(), exclude: true)];
        Assert.Equal(MediaFilterTermSet.IdentityKey(tags[0]), MediaFilterTermSet.IdentityKey(MediaFilterTermSet.Ordered(MediaFilterTermSet.Toggled(tags, tags[0]))[^1]));
    }

    [Fact]
    public void FieldsThatHoldOneValueReplaceItAndSourcesAndBoardsStayOneFilter()
    {
        IReadOnlyList<MediaFilterTerm> terms = MediaFilterTermSet.With([MediaFilterTerm.Rating(2)], MediaFilterTerm.Rating(4));
        Assert.Equal([MediaFilterTerm.Rating(4)], terms);
        terms = MediaFilterTermSet.With([MediaFilterTerm.Board("4chan", "g")], MediaFilterTerm.Source("8kun"));
        Assert.Equal([MediaFilterTerm.Source("8kun")], terms); // another source drops the board of the old one
        terms = MediaFilterTermSet.With([MediaFilterTerm.Source("4chan"), MediaFilterTerm.Board("4chan", "g")], MediaFilterTerm.Source("4chan", exclude: true));
        Assert.Equal([MediaFilterTerm.Source("4chan", exclude: true)], terms); // excluding the source drops its board
        terms = MediaFilterTermSet.With([MediaFilterTerm.Source("8kun")], MediaFilterTerm.Board("4chan", "g"));
        Assert.Equal([MediaFilterTerm.Board("4chan", "g")], terms); // a board names its source
        Assert.Equal("board:4chan/g", MediaFilterTermSet.IdentityKey(MediaFilterTerm.Board("4chan", "g", exclude: true)));
        // Adding a value of the other polarity replaces it; a field that cannot exclude is added as an inclusion.
        Assert.Equal([Type(MediaLibraryKindFilter.Gif, exclude: true)], MediaFilterTermSet.With([Type(MediaLibraryKindFilter.Gif)], Type(MediaLibraryKindFilter.Gif, exclude: true)));
        Assert.Equal([MediaFilterTerm.Flag(MediaFilterField.Missing)], MediaFilterTermSet.With([], MediaFilterTerm.Flag(MediaFilterField.Missing) with { Exclude = true }));
    }

    [Fact]
    public void ASoundImpliesVideosAndATypeWithoutVideosDropsIt()
    {
        MediaFilterTerm with = MediaFilterTerm.Sound(MediaLibrarySoundFilter.WithSound);
        Assert.Equal([with, Type(MediaLibraryKindFilter.Video)], MediaFilterTermSet.With([], with));
        Assert.Equal([with, Type(MediaLibraryKindFilter.Video)], MediaFilterTermSet.With([Type(MediaLibraryKindFilter.Image)], with));
        // Videos among several shown types are enough.
        IReadOnlyList<MediaFilterTerm> both = [Type(MediaLibraryKindFilter.Image), Type(MediaLibraryKindFilter.Video)];
        Assert.Equal(both.Append(with), MediaFilterTermSet.With(both, with));
        IReadOnlyList<MediaFilterTerm> sound = MediaFilterTermSet.With([], with);
        Assert.DoesNotContain(MediaFilterTermSet.WithTypes(sound, MediaLibraryKindFilter.Image), term => term.Field == MediaFilterField.Sound);
        Assert.DoesNotContain(MediaFilterTermSet.WithTypes(sound), term => term.Field == MediaFilterField.Sound); // "All types" drops it, as before
        Assert.DoesNotContain(MediaFilterTermSet.Toggled(sound, Type(MediaLibraryKindFilter.Video)), term => term.Field == MediaFilterField.Sound);
        Assert.Contains(MediaFilterTermSet.With(sound, Type(MediaLibraryKindFilter.Gif)), term => term.Field == MediaFilterField.Sound);
    }

    [Fact]
    public void ShownKindsCountWhatTheTypeComboSummarizes()
    {
        Assert.Null(MediaFilterTermSet.ShownKinds([]));
        Assert.Equal(2, MediaFilterTermSet.ShownKinds([Type(MediaLibraryKindFilter.Image), Type(MediaLibraryKindFilter.Gif)])!.Count);
        Assert.Equal(3, MediaFilterTermSet.ShownKinds([Type(MediaLibraryKindFilter.Video, exclude: true)])!.Count);
        Assert.Empty(MediaFilterTermSet.ShownKinds([Type(MediaLibraryKindFilter.Image), Type(MediaLibraryKindFilter.Image, exclude: true)])!);
        Assert.Equal("2 types", Texts.Count("media.filter.kind.count", 2));
        Assert.Equal("1 type", Texts.Count("media.filter.kind.count", 1));
        Texts.SetLanguage("de");
        Assert.Equal("2 Typen", Texts.Count("media.filter.kind.count", 2));
        Texts.SetLanguage("ru");
        Assert.Equal("3 типа", Texts.Count("media.filter.kind.count", 3));
        Texts.SetLanguage("pl");
        Assert.Equal("5 typów", Texts.Count("media.filter.kind.count", 5));
    }

    [Fact]
    public void ChipsKeepTheOrderTheyHadBeforeTheTermModel()
    {
        MediaFilterTerm[] terms =
        [
            MediaFilterTerm.Flag(MediaFilterField.Missing), MediaFilterTerm.Rating(2), MediaFilterTerm.Board("4chan", "g"), MediaFilterTerm.Source("8kun", exclude: true),
            Type(MediaLibraryKindFilter.Image), MediaFilterTerm.Name("r-6"), MediaFilterTerm.Flag(MediaFilterField.Untagged), MediaFilterTerm.Tag(Rpt, exclude: true),
            MediaFilterTerm.Tag(Guid.NewGuid()), MediaFilterTerm.Flag(MediaFilterField.Favorite), MediaFilterTerm.Sound(MediaLibrarySoundFilter.WithoutSound)
        ];
        Assert.Equal(new[] { MediaFilterField.Tag, MediaFilterField.Tag, MediaFilterField.Untagged, MediaFilterField.Name, MediaFilterField.Type, MediaFilterField.Sound,
            MediaFilterField.Source, MediaFilterField.Board, MediaFilterField.Rating, MediaFilterField.Favorite, MediaFilterField.Missing },
            MediaFilterTermSet.Ordered(terms).Select(term => term.Field));
        Assert.False(MediaFilterTermSet.Ordered(terms)[0].Exclude); // included tags first
    }

    // ---- the chip ---------------------------------------------------------------------------------------------------

    private static TappedEventArgs Tap(object source) => new(InputElement.TappedEvent,
        new PointerEventArgs(InputElement.PointerReleasedEvent, source, new Pointer(1, PointerType.Mouse, true), null, default, 0,
            PointerPointProperties.None, KeyModifiers.None)) { Source = source };

    private static void Key(Control control, Key key) => control.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });

    [Fact]
    public void ANegatableChipSwitchesByItsToggleItsBodyEnterAndItsMenuAndRemovesByDelete()
    {
        int removed = 0, toggled = 0;
        ClosableTag chip = WorkspaceChips.CreateTermChip("Type: Videos", negatable: true, exclude: false, () => removed++, () => toggled++, "Videos",
            menu: FilterChipMenuOptions.Plain);
        Assert.Equal(new[] { "chip", "chip-filter" }, chip.Classes);
        Assert.Equal("Type: Videos", WorkspaceChips.TermChipLabel(chip));
        Assert.Equal("Type: Videos", AutomationProperties.GetName(chip));
        Assert.Equal(Texts.T("media.filter.chip.help"), AutomationProperties.GetHelpText(chip));
        Button toggle = WorkspaceChips.TermChipToggle(chip)!;
        Assert.Equal(WorkspaceChips.TermToggleName, toggle.Name);
        Assert.Equal((20d, 20d), (toggle.Width, toggle.Height));
        Assert.Equal(Texts.T("tags.filter.toExclude", "Videos"), AutomationProperties.GetName(toggle));

        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal((0, 1), (removed, toggled));
        // E4: a click on the body switches; a tap that comes from the toggle (or the close mark, also a button) does not again.
        chip.RaiseEvent(Tap(((StackPanel)chip.Content!).Children[1]));
        Assert.Equal((0, 2), (removed, toggled));
        Assert.False(WorkspaceChips.TapSwitches(toggle, chip));
        Assert.True(WorkspaceChips.TapSwitches(((StackPanel)chip.Content!).Children[1], chip));
        Key(chip, Avalonia.Input.Key.Enter);
        Key(chip, Avalonia.Input.Key.Space);
        Assert.Equal((0, 4), (removed, toggled));
        Key(chip, Avalonia.Input.Key.Delete);
        chip.Command!.Execute(null);
        Assert.Equal((2, 4), (removed, toggled));

        // The menu (KM1/KM2 builder, opened by the chip's ContextMenuHost): the switch says what it will do, then Remove filter.
        ContextMenuEntry[] entries = WorkspaceChips.MenuModelOf(chip)!.Entries.ToArray();
        Assert.Equal(new[] { "tags.filter-toggle", "archive.clear-filter" }, entries.Select(entry => entry.CommandId));
        Assert.Equal(Texts.T("tags.filter.toExclude", "Videos"), entries[0].Label);
        entries[0].Invoke!();
        entries[1].Invoke!();
        Assert.Equal((3, 5), (removed, toggled));
    }

    // Wave-3 smoke regression (media-page): the label was the chip's content first and then moved into the switch panel, so the
    // chip cleared the label's logical parent after the panel had taken it and adding the chip to the page threw.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EveryPartOfATermChipHasItsLogicalParent(bool negatable)
    {
        ClosableTag chip = WorkspaceChips.CreateTermChip("Type: Images", negatable, exclude: false, () => { }, negatable ? () => { } : null, "Images", menu: FilterChipMenuOptions.Plain);
        Control content = Assert.IsAssignableFrom<Control>(chip.Content);
        Assert.Same(chip, content.Parent);
        if (content is StackPanel panel)
        {
            Assert.True(negatable);
            Assert.Equal(2, panel.Children.Count);
            Assert.All(panel.Children, child => Assert.Same(panel, child.Parent));
        }
        else Assert.False(negatable);
    }

    [Fact]
    public void AnExcludedChipSaysSoAndItsMenuOffersEdit()
    {
        int edited = 0;
        ClosableTag chip = WorkspaceChips.CreateTermChip("Without board: /g/", negatable: true, exclude: true, () => { }, () => { }, "/g/",
            menu: new FilterChipMenuOptions { Edit = () => edited++ });
        Assert.Contains("chip-exclude", chip.Classes);
        Assert.Equal(Texts.T("tags.filter.toInclude", "/g/"), AutomationProperties.GetName(WorkspaceChips.TermChipToggle(chip)!));
        ContextMenuEntry[] entries = WorkspaceChips.MenuModelOf(chip)!.Entries.ToArray();
        Assert.Equal(Texts.T("tags.filter.toInclude", "/g/"), entries.Single(entry => entry.CommandId == "tags.filter-toggle").Label);
        entries.Single(entry => entry.CommandId == "media.filter.edit").Invoke!();
        Assert.Equal(1, edited);
    }

    [Fact]
    public void APlainChipOnlyRemovesAndKeepsTheArchiveLook()
    {
        int removed = 0;
        ClosableTag chip = WorkspaceChips.CreateTermChip("Rating: at least 3 of 5", negatable: false, exclude: false, () => removed++, menu: FilterChipMenuOptions.Plain);
        Assert.IsType<TextBlock>(chip.Content); // the archive's plain chips read their text from the content directly
        Assert.Null(WorkspaceChips.TermChipToggle(chip));
        Assert.Equal(Texts.T("archive.removeFilter", "Rating: at least 3 of 5"), AutomationProperties.GetName(chip));
        chip.RaiseEvent(Tap(chip.Content!));
        Assert.Equal(0, removed); // the body of a plain chip does nothing
        foreach (Key key in new[] { Avalonia.Input.Key.Enter, Avalonia.Input.Key.Space, Avalonia.Input.Key.Delete, Avalonia.Input.Key.Back }) Key(chip, key);
        Assert.Equal(4, removed);
        Assert.Equal(new[] { "archive.clear-filter" }, WorkspaceChips.MenuModelOf(chip)!.Entries.Select(entry => entry.CommandId));
        // Without a menu (the archive's plain chips) nothing is added.
        Assert.False(WorkspaceChips.HasMenu(ArchivePage.CreateFilterTag("Source: 4chan", () => { })));
    }

    [Fact]
    public void TheArchiveTagChipIsTheSameTermChipAndItsBodySwitchesToo()
    {
        Guid id = Guid.NewGuid();
        int toggled = 0;
        ClosableTag chip = ArchivePage.CreateTagFilterChip("Tag: surface", "surface", id, exclude: false, () => { }, () => toggled++);
        Assert.Equal(id, chip.Tag);
        Assert.True(WorkspaceChips.HasMenu(chip));
        chip.RaiseEvent(Tap(((StackPanel)chip.Content!).Children[1]));
        Assert.Equal(1, toggled);
    }

    // ---- labels in twelve languages ---------------------------------------------------------------------------------

    public static TheoryData<string> Languages() => new() { "en", "de", "es", "fr", "ru", "zh-Hans", "ja", "ko", "pt-BR", "cs", "it", "pl" };

    // A rough width of a chip at the body size (13 px Inter: about 7 dip per Latin or Cyrillic letter, 13 per CJK/Hangul sign) plus
    // its frame: 2×8 padding, the 16 dip close mark with 6 dip spacing and, for a negatable chip, the 20 dip switch with 4 dip spacing.
    internal static double EstimatedChipWidth(string label, bool negatable) =>
        label.Sum(letter => letter >= '⺀' ? 13d : 7d) + 16 + 16 + 6 + (negatable ? 24 : 0);

    [Theory]
    [MemberData(nameof(Languages))]
    public void EveryChipLabelIsTranslatedAndFitsTheChipWidth(string language)
    {
        Texts.SetLanguage(language);
        string board = Texts.T("media.filter.boardOfSource", "g", "4chan");
        List<(string Label, bool Negatable)> labels =
        [
            (Texts.T("tags.filter.includeChip", "surface"), true), (Texts.T("tags.filter.excludeChip", "surface"), true),
            (Texts.T("archive.sourceChip", "4chan"), true), (Texts.T("media.filter.sourceExcludeChip", "4chan"), true),
            (Texts.T("archive.boardChip", board), true), (Texts.T("media.filter.boardExcludeChip", board), true),
            (Texts.T("media.filter.ratingChip", 5), false), (Texts.T("media.filter.favorites"), false), (Texts.T("media.filter.withoutTags"), false),
            (Texts.T("media.filter.missingChip"), false), (Texts.T("media.filter.sound.with"), false), (Texts.T("media.filter.sound.without"), false),
            (Texts.T("tags.filter.anyChip"), false), (Texts.T("media.filter.nameChip", "r-6"), false),
        ];
        foreach (string kind in new[] { "image", "gif", "video", "other" })
        {
            labels.Add((Texts.T("media.filter.kindChip", Texts.T("media.filter.kind." + kind)), true));
            labels.Add((Texts.T("media.filter.kindExcludeChip", Texts.T("media.filter.kind." + kind)), true));
        }
        foreach ((string label, bool negatable) in labels)
        {
            Assert.False(string.IsNullOrWhiteSpace(label));
            Assert.True(EstimatedChipWidth(label, negatable) <= 320, $"{language}: “{label}” needs about {EstimatedChipWidth(label, negatable)} dip");
        }
        foreach (string key in new[] { "media.filter.kindExcludeChip", "media.filter.sourceExcludeChip", "media.filter.boardExcludeChip", "media.filter.add.noMatch",
            "media.filter.add.cannotExclude", "media.filter.combo.excluded" })
            Assert.Contains("4chan", Texts.T(key, "4chan"));
        Assert.Contains("7", Texts.Count("media.filter.kind.count", 7));
        // "+ Filter" stays a short chip; the picker's texts exist.
        Assert.True(EstimatedChipWidth(Texts.T("media.filter.addChip"), negatable: false) <= 160, language + ": the + Filter chip is too wide");
        foreach (string key in new[] { "media.filter.add", "media.filter.add.help", "media.filter.add.search", "media.filter.add.hint", "media.filter.add.allUsed",
            "media.filter.add.refused", "media.filter.group.tag", "media.filter.group.flags", "media.filter.chip.help", "media.filter.chip.remove", "media.filter.chip.edit" })
            Assert.False(string.IsNullOrWhiteSpace(Texts.T(key)) || Texts.T(key) == key, language + " " + key);
        // An excluded value says so differently from the included one.
        Assert.NotEqual(Texts.T("media.filter.kindChip", "x"), Texts.T("media.filter.kindExcludeChip", "x"));
    }
}
