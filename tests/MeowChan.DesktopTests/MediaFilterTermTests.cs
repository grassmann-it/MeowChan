using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Gallery;

namespace MeowChan.DesktopTests;

// M2 groundwork: the filter terms of the media page and their pure translation into the core filter (and back). No controls involved.
public sealed class MediaFilterTermTests
{
    private static readonly Guid Rpt = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Cute = Guid.Parse("00000000-0000-0000-0000-0000000000a2");

    [Fact]
    public void NoTermsIsTheEmptyFilter()
    {
        Assert.True(MediaFilterTerms.ToFilter([]).IsEmpty);
        Assert.Empty(MediaFilterTerms.FromFilter(MediaLibraryFilter.None));
        Assert.False(MediaFilterTerms.IncludesMissing([]));
    }

    [Fact]
    public void OnlyTagTypeSourceBoardQueueAndSourceTagCanExclude()
    {
        foreach (MediaFilterField field in Enum.GetValues<MediaFilterField>())
            Assert.Equal(field is MediaFilterField.Tag or MediaFilterField.Type or MediaFilterField.Source or MediaFilterField.Board
                or MediaFilterField.Queue or MediaFilterField.SourceTag, MediaFilterTerm.CanExclude(field));
        Assert.True(MediaFilterTerm.Type(MediaLibraryKindFilter.Video).Toggled().Exclude);
        Assert.False(MediaFilterTerm.Type(MediaLibraryKindFilter.Video).Toggled().Toggled().Exclude);
        MediaFilterTerm favorite = MediaFilterTerm.Flag(MediaFilterField.Favorite);
        Assert.Equal(favorite, favorite.Toggled()); // nothing to switch: the term stays
        Assert.Throws<ArgumentException>(() => MediaFilterTerms.ToFilter([favorite with { Exclude = true }]));
        Assert.Throws<ArgumentException>(() => MediaFilterTerms.ToFilter([MediaFilterTerm.Rating(3) with { Exclude = true }]));
        Assert.Throws<ArgumentException>(() => MediaFilterTerms.ToFilter([MediaFilterTerm.Name("x") with { Exclude = true }]));
    }

    [Fact]
    public void EveryFieldHasAQueryNameThatReadsBack()
    {
        string[] keys = [.. Enum.GetValues<MediaFilterField>().Select(MediaFilterTerm.KeyOf)];
        Assert.Equal(keys.Length, keys.Distinct().Count());
        foreach (MediaFilterField field in Enum.GetValues<MediaFilterField>())
        {
            Assert.Equal(field, MediaFilterTerm.FieldOf(MediaFilterTerm.KeyOf(field)));
            Assert.Equal(field, MediaFilterTerm.FieldOf(" " + MediaFilterTerm.KeyOf(field).ToUpperInvariant() + " "));
        }
        Assert.Equal(["tag", "type", "sound", "source", "board", "rating", "fav", "missing", "untagged", "thread", "name", "queue", "booru", "in"], keys);
        Assert.Null(MediaFilterTerm.FieldOf("typ"));
        Assert.Null(MediaFilterTerm.FieldOf(null));
    }

    [Fact]
    public void TagTermsBecomeTheIncludeAndExcludeListsWithTheModeOnlyMattering()
    {
        MediaLibraryFilter filter = MediaFilterTerms.ToFilter([MediaFilterTerm.Tag(Rpt), MediaFilterTerm.Tag(Cute, exclude: true)], ArchiveTagMatch.Any);
        Assert.Equal([Rpt], filter.Tags!.Include);
        Assert.Equal([Cute], filter.Tags.Exclude);
        // With fewer than two included tags both modes are the same filter.
        Assert.Equal(filter, MediaFilterTerms.ToFilter([MediaFilterTerm.Tag(Rpt), MediaFilterTerm.Tag(Cute, exclude: true)], ArchiveTagMatch.All));
        Assert.Equal(ArchiveTagMatch.Any, MediaFilterTerms.ToFilter([MediaFilterTerm.Tag(Rpt), MediaFilterTerm.Tag(Cute)], ArchiveTagMatch.Any).Tags!.Match);
        Assert.Throws<ArgumentException>(() => MediaFilterTerms.ToFilter([MediaFilterTerm.Tag(Rpt), MediaFilterTerm.Tag(Rpt, exclude: true)]));
        Assert.Throws<ArgumentException>(() => MediaFilterTerms.ToFilter([new MediaFilterTerm(MediaFilterField.Tag, "not-a-guid")]));
        Assert.Throws<ArgumentException>(() => MediaFilterTerms.ToFilter([MediaFilterTerm.Tag(Guid.Empty)]));
    }

    [Fact]
    public void TypeTermsGiveASetWithExcludedTypesTakenOutOfIncludedOrOutOfAll()
    {
        Assert.Equal([MediaLibraryKindFilter.Video], MediaFilterTerms.ToFilter([MediaFilterTerm.Type(MediaLibraryKindFilter.Video)]).Kinds);
        Assert.Equal([MediaLibraryKindFilter.Image, MediaLibraryKindFilter.Video],
            MediaFilterTerms.ToFilter([MediaFilterTerm.Type(MediaLibraryKindFilter.Image), MediaFilterTerm.Type(MediaLibraryKindFilter.Video)]).Kinds.Order());
        // Only exclusions: every other type. Included and excluded: the included ones that are not excluded.
        Assert.Equal([MediaLibraryKindFilter.Image, MediaLibraryKindFilter.Gif, MediaLibraryKindFilter.Other],
            MediaFilterTerms.ToFilter([MediaFilterTerm.Type(MediaLibraryKindFilter.Video, exclude: true)]).Kinds.Order());
        Assert.Equal([MediaLibraryKindFilter.Image], MediaFilterTerms.ToFilter([MediaFilterTerm.Type(MediaLibraryKindFilter.Image), MediaFilterTerm.Type(MediaLibraryKindFilter.Gif),
            MediaFilterTerm.Type(MediaLibraryKindFilter.Gif, exclude: true)]).Kinds);
        // Nothing left is refused, not read as "all".
        Assert.Throws<ArgumentException>(() => MediaFilterTerms.ToFilter([MediaFilterTerm.Type(MediaLibraryKindFilter.Video), MediaFilterTerm.Type(MediaLibraryKindFilter.Video, exclude: true)]));
        Assert.Throws<ArgumentException>(() => MediaFilterTerms.ToFilter(
            [.. new[] { MediaLibraryKindFilter.Image, MediaLibraryKindFilter.Gif, MediaLibraryKindFilter.Video, MediaLibraryKindFilter.Other }.Select(kind => MediaFilterTerm.Type(kind, exclude: true))]));
        Assert.Throws<ArgumentException>(() => MediaFilterTerms.ToFilter([new MediaFilterTerm(MediaFilterField.Type, "any")]));
        Assert.Throws<ArgumentOutOfRangeException>(() => MediaFilterTerm.Type(MediaLibraryKindFilter.Any));
    }

    [Fact]
    public void SoundNeedsVideosAndNeverBothWays()
    {
        Assert.Equal(MediaLibrarySoundFilter.WithoutSound, MediaFilterTerms.ToFilter([MediaFilterTerm.Sound(MediaLibrarySoundFilter.WithoutSound)]).Sound);
        Assert.Equal(MediaLibrarySoundFilter.WithSound, MediaFilterTerms.ToFilter([MediaFilterTerm.Sound(MediaLibrarySoundFilter.WithSound), MediaFilterTerm.Type(MediaLibraryKindFilter.Video)]).Sound);
        Assert.Throws<ArgumentException>(() => MediaFilterTerms.ToFilter([MediaFilterTerm.Sound(MediaLibrarySoundFilter.WithSound), MediaFilterTerm.Sound(MediaLibrarySoundFilter.WithoutSound)]));
        Assert.Throws<ArgumentException>(() => MediaFilterTerms.ToFilter([MediaFilterTerm.Sound(MediaLibrarySoundFilter.WithSound), MediaFilterTerm.Type(MediaLibraryKindFilter.Image)]));
        Assert.Throws<ArgumentException>(() => MediaFilterTerms.ToFilter([MediaFilterTerm.Sound(MediaLibrarySoundFilter.WithSound), MediaFilterTerm.Type(MediaLibraryKindFilter.Video, exclude: true)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => MediaFilterTerm.Sound(MediaLibrarySoundFilter.Any));
    }

    [Fact]
    public void SourceAndBoardTermsIncludeOneAndExcludeAny()
    {
        MediaLibraryFilter filter = MediaFilterTerms.ToFilter([MediaFilterTerm.Board("4chan", "g"), MediaFilterTerm.Source("danbooru", exclude: true),
            MediaFilterTerm.Source("other", exclude: true), MediaFilterTerm.Board("4chan", "wsg", exclude: true)]);
        Assert.Equal(("4chan", "g"), (filter.Source, filter.Board)); // the board names its source
        Assert.Equal(["danbooru", "other"], filter.ExcludeSources.Order(StringComparer.Ordinal));
        Assert.Equal([new MediaLibraryBoardKey("4chan", "wsg")], filter.ExcludeBoards);
        // A source term for the board's own source is fine (the control sets both); another source cannot hold with it.
        Assert.Equal("g", MediaFilterTerms.ToFilter([MediaFilterTerm.Source("4chan"), MediaFilterTerm.Board("4chan", "g")]).Board);
        Assert.Throws<ArgumentException>(() => MediaFilterTerms.ToFilter([MediaFilterTerm.Source("other"), MediaFilterTerm.Board("4chan", "g")]));
        Assert.Throws<ArgumentException>(() => MediaFilterTerms.ToFilter([MediaFilterTerm.Source("a"), MediaFilterTerm.Source("b")]));
        Assert.Throws<ArgumentException>(() => MediaFilterTerms.ToFilter([MediaFilterTerm.Board("s", "a"), MediaFilterTerm.Board("s", "b")]));
        Assert.Throws<ArgumentException>(() => MediaFilterTerms.ToFilter([new MediaFilterTerm(MediaFilterField.Board, "g")])); // no source
        Assert.Throws<ArgumentException>(() => MediaFilterTerms.ToFilter([MediaFilterTerm.Source("")]));
    }

    [Fact]
    public void RatingNameThreadAndFlagsReadTheirValues()
    {
        ResourceId thread = new("4chan", "g", "123");
        MediaLibraryFilter filter = MediaFilterTerms.ToFilter([MediaFilterTerm.Rating(2), MediaFilterTerm.Rating(4), MediaFilterTerm.Name("  cat "),
            MediaFilterTerm.InThread(thread), MediaFilterTerm.Flag(MediaFilterField.Favorite), MediaFilterTerm.Flag(MediaFilterField.Untagged),
            MediaFilterTerm.Flag(MediaFilterField.Missing)]);
        Assert.Equal(4, filter.MinimumRating); // all must hold: the highest
        Assert.Equal("cat", filter.NameContains);
        Assert.Equal(thread, filter.Thread);
        Assert.True(filter.FavoritesOnly);
        Assert.True(filter.WithoutMediaTags);
        Assert.True(MediaFilterTerms.IncludesMissing([MediaFilterTerm.Flag(MediaFilterField.Missing)]));
        Assert.True(MediaFilterTerms.ToFilter([MediaFilterTerm.Flag(MediaFilterField.Missing)]).IsEmpty); // a presentation flag, not a filter part
        foreach (string bad in new[] { "0", "6", "x", "", "-1", "3.5" })
            Assert.Throws<ArgumentException>(() => MediaFilterTerms.ToFilter([new MediaFilterTerm(MediaFilterField.Rating, bad)]));
        Assert.Throws<ArgumentException>(() => MediaFilterTerms.ToFilter([MediaFilterTerm.Name("  ")]));
        Assert.Throws<ArgumentException>(() => MediaFilterTerms.ToFilter([MediaFilterTerm.Name("a"), MediaFilterTerm.Name("b")]));
        Assert.Throws<ArgumentException>(() => MediaFilterTerms.ToFilter([MediaFilterTerm.InThread(thread), MediaFilterTerm.InThread(thread with { Thread = "9" })]));
        Assert.Throws<ArgumentException>(() => MediaFilterTerms.ToFilter([new MediaFilterTerm(MediaFilterField.Thread, "123")])); // no resource
        Assert.Throws<ArgumentException>(() => MediaFilterTerms.ToFilter([MediaFilterTerm.Name(new string('x', MediaLibraryFilter.MaximumNameLength + 1))]));
        Assert.Throws<ArgumentException>(() => MediaFilterTerms.ToFilter([null!]));
    }

    [Fact]
    public void AFilterWritesItsTermsAndTheTermsGiveTheSameFilterBack()
    {
        ResourceId thread = new("4chan", "g", "123");
        MediaLibraryFilter[] filters =
        [
            MediaLibraryFilter.None,
            new(nameContains: "cat"),
            new(new ArchiveTagFilter([Rpt, Cute], [], ArchiveTagMatch.Any), kinds: [MediaLibraryKindFilter.Video], sound: MediaLibrarySoundFilter.WithoutSound),
            new(new ArchiveTagFilter([Rpt], [Cute]), kinds: MediaLibraryFilter.AllKindsExcept(MediaLibraryKindFilter.Video), minimumRating: 3, favoritesOnly: true),
            new(kinds: [MediaLibraryKindFilter.Image, MediaLibraryKindFilter.Video], source: "4chan", excludeSources: ["danbooru"]),
            new(source: "4chan", board: "g", excludeBoards: [new MediaLibraryBoardKey("4chan", "wsg"), new MediaLibraryBoardKey("other", "g")], withoutMediaTags: true, thread: thread),
            new(kinds: MediaLibraryFilter.AllKindsExcept(MediaLibraryKindFilter.Image, MediaLibraryKindFilter.Gif)),
            new(excludeSources: ["a", "b"], excludeBoards: [new MediaLibraryBoardKey("s", "x")]),
            new(nameContains: "cat girl", textFields: MediaTextFields.Tag | MediaTextFields.Title),
            new(source: "4chan", nameContains: "\"cat girl\"", textFields: MediaTextFields.Name)
        ];
        foreach (MediaLibraryFilter filter in filters)
        {
            foreach (bool missing in new[] { false, true })
            {
                IReadOnlyList<MediaFilterTerm> terms = MediaFilterTerms.FromFilter(filter, missing);
                Assert.Equal(filter, MediaFilterTerms.ToFilter(terms, filter.Tags?.Match ?? ArchiveTagMatch.All));
                Assert.Equal(missing, MediaFilterTerms.IncludesMissing(terms));
                Assert.Equal(terms, MediaFilterTerms.FromFilter(MediaFilterTerms.ToFilter(terms, filter.Tags?.Match ?? ArchiveTagMatch.All), missing)); // stable
            }
        }
        // "Without video" reads as one excluded type, not as three included ones; two included types stay two.
        Assert.Equal([MediaFilterTerm.Type(MediaLibraryKindFilter.Video, exclude: true)],
            MediaFilterTerms.FromFilter(new MediaLibraryFilter(kinds: MediaLibraryFilter.AllKindsExcept(MediaLibraryKindFilter.Video))));
        Assert.Equal(2, MediaFilterTerms.FromFilter(new MediaLibraryFilter(kinds: [MediaLibraryKindFilter.Image, MediaLibraryKindFilter.Video])).Count);
    }

    // The text scope (in:) is a term of its own: one per set, written before the name, the same filter back.
    [Fact]
    public void TheTextScopeIsOneTermThatReplacesItselfAndReachesTheFilter()
    {
        MediaFilterTerm tags = MediaFilterTerm.In(MediaTextFields.Tag), both = MediaFilterTerm.In(MediaTextFields.Tag | MediaTextFields.Title);
        // The value is canonical: the English keys in the order of the fields, whatever order the flags were combined in.
        Assert.Equal("title,tag", both.Value);
        Assert.Equal(both, MediaFilterTerm.In(MediaTextFields.Title | MediaTextFields.Tag));
        Assert.Equal("all", MediaFilterTerm.In(MediaTextFields.All).Value);
        Assert.Equal("name,title,board,source,tag", string.Join(',', MediaFilterTerm.TextFieldOrder.Select(MediaFilterTerm.TextFieldKey)));
        Assert.Throws<ArgumentOutOfRangeException>(() => MediaFilterTerm.In(MediaTextFields.None));
        Assert.False(MediaFilterTerm.CanExclude(MediaFilterField.In));
        Assert.Throws<ArgumentException>(() => MediaFilterTerms.ToFilter([tags with { Exclude = true }, MediaFilterTerm.Name("x")]));

        // With keeps one scope, like the name.
        Assert.Equal([both], MediaFilterTermSet.With([tags], both));
        IReadOnlyList<MediaFilterTerm> set = MediaFilterTermSet.With(MediaFilterTermSet.With([MediaFilterTerm.Rating(2)], tags), both);
        Assert.Equal([MediaFilterTerm.Rating(2), both], set);
        Assert.Equal("in:title,tag", MediaFilterTermSet.IdentityKey(both));
        // It stands beside the name in the chip order, before it, and keeps the set's order otherwise.
        IReadOnlyList<MediaFilterTerm> ordered = MediaFilterTermSet.Ordered([MediaFilterTerm.Name("cat"), MediaFilterTerm.Rating(2), MediaFilterTerm.Type(MediaLibraryKindFilter.Video), both]);
        Assert.Equal([MediaFilterTerm.Name("cat"), both, MediaFilterTerm.Type(MediaLibraryKindFilter.Video), MediaFilterTerm.Rating(2)], ordered);
        Assert.Equal([both, MediaFilterTerm.Name("cat")], MediaFilterTermSet.Ordered([both, MediaFilterTerm.Name("cat")]));

        // To the filter and back; "All" is the default and no term.
        MediaLibraryFilter filter = MediaFilterTerms.ToFilter([both, MediaFilterTerm.Name("cat")]);
        Assert.Equal(MediaTextFields.Title | MediaTextFields.Tag, filter.TextFields);
        Assert.Equal([both, MediaFilterTerm.Name("cat")], MediaFilterTerms.FromFilter(filter));
        Assert.Equal(MediaTextFields.All, MediaFilterTerms.ToFilter([MediaFilterTerm.In(MediaTextFields.All), MediaFilterTerm.Name("cat")]).TextFields);
        Assert.Equal([MediaFilterTerm.Name("cat")], MediaFilterTerms.FromFilter(MediaFilterTerms.ToFilter([MediaFilterTerm.Name("cat")])));
        // Without a word the scope is forgotten, as in the filter.
        Assert.True(MediaFilterTerms.ToFilter([both]).IsEmpty);
        Assert.Empty(MediaFilterTerms.FromFilter(MediaFilterTerms.ToFilter([both])));
        // Two different scopes are no filter; an unreadable value is refused.
        Assert.Throws<ArgumentException>(() => MediaFilterTerms.ToFilter([both, tags, MediaFilterTerm.Name("x")]));
        Assert.Throws<ArgumentException>(() => MediaFilterTerms.ToFilter([new MediaFilterTerm(MediaFilterField.In, "bogus"), MediaFilterTerm.Name("x")]));
        Assert.Equal(tags.Value, "tag");
    }

    [Theory]
    [InlineData("tag", 16), InlineData("Tag,TITLE", 18), InlineData(" name , board ", 5)]
    [InlineData("all", 31), InlineData("name,all", 31), InlineData("source,source", 8)]
    public void ScopeValuesReadEnglishKeysInAnyOrderAndCase(string value, int expected) => Assert.Equal((MediaTextFields)expected, MediaFilterTerm.TextFieldsOf(value));

    [Theory]
    [InlineData(""), InlineData(" "), InlineData(","), InlineData("bogus"), InlineData("tag,bogus"), InlineData("none"), InlineData("tags")]
    public void ScopeValuesThatAreNotKeysAreNone(string value) => Assert.Null(MediaFilterTerm.TextFieldsOf(value));
}
