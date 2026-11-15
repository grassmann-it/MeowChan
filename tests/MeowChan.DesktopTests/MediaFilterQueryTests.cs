using System.Globalization;
using System.Text.Json;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan.DesktopTests;

// M3: the query syntax of the media search field (MediaFilterQuery): tokens, exclusion, quotes, values that are not understood stay
// text, the way back to text, the aliases of every language pack and the application onto a term set. All pure: no controls.
[Collection("Localization")]
public sealed class MediaFilterQueryTests : IDisposable
{
    private static readonly Guid Cats = Guid.Parse("00000000-0000-0000-0000-0000000000c1");
    private static readonly Guid Two = Guid.Parse("00000000-0000-0000-0000-0000000000c2");
    private static readonly Guid Wip = Guid.Parse("00000000-0000-0000-0000-0000000000c3");
    private static readonly Dictionary<string, Guid> Tags = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cats"] = Cats, ["two words"] = Two, ["wip"] = Wip, ["say \"hi\""] = Guid.Parse("00000000-0000-0000-0000-0000000000c4"),
        ["fav"] = Guid.Parse("00000000-0000-0000-0000-0000000000c5"), ["日本語"] = Guid.Parse("00000000-0000-0000-0000-0000000000c6")
    };
    private static readonly string[] Sources = ["4chan", "8kun", "booru"];
    private static readonly (string Source, string Board)[] Boards = [("4chan", "g"), ("4chan", "b"), ("8kun", "g"), ("booru", "all")];
    private static readonly ResourceId[] Threads = [new("4chan", "g", "1234"), new("8kun", "g", "1234"), new("4chan", "b", "99")];

    public MediaFilterQueryTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static readonly Dictionary<string, MediaTextFields> ScopeWords = new()
    {
        ["titel"] = MediaTextFields.Title, ["threadtitel"] = MediaTextFields.Title, ["schlagwort"] = MediaTextFields.Tag, ["datei"] = MediaTextFields.Name
    };

    private static MediaFilterQueryVocabulary Vocab(IReadOnlyDictionary<string, MediaFilterField>? aliases = null) => new()
    {
        Aliases = aliases ?? new Dictionary<string, MediaFilterField>(),
        ScopeAliases = ScopeWords,
        FindTag = name => Tags.TryGetValue(name, out Guid id) ? id : null,
        TagName = id => Tags.FirstOrDefault(pair => pair.Value == id).Key,
        FindSource = text => Sources.FirstOrDefault(source => string.Equals(source, text, StringComparison.OrdinalIgnoreCase)),
        FindBoards = board => Boards.Where(pair => string.Equals(pair.Board, board, StringComparison.OrdinalIgnoreCase)).ToArray(),
        FindThreads = id => Threads.Where(thread => thread.Thread == id).ToArray()
    };

    private static MediaFilterQueryResult Parse(string text, IReadOnlyDictionary<string, MediaFilterField>? aliases = null) => MediaFilterQuery.Parse(text, Vocab(aliases));
    private static MediaFilterTerm Type(MediaLibraryKindFilter kind, bool exclude = false) => MediaFilterTerm.Type(kind, exclude);

    // ---- parsing ----------------------------------------------------------------------------------------------------

    [Fact]
    public void TheExampleOfTheBriefBecomesFourTermsAndAnExclusion()
    {
        MediaFilterQueryResult result = Parse("type:video -tag:wip source:4chan board:g");
        Assert.Equal([Type(MediaLibraryKindFilter.Video), MediaFilterTerm.Tag(Wip, exclude: true), MediaFilterTerm.Source("4chan"), MediaFilterTerm.Board("4chan", "g")],
            result.Terms);
        Assert.Equal(string.Empty, result.FreeText);
        Assert.Empty(result.Problems);
    }

    // Review 15.11.2026: "/kot/" is a pseudo-tag in thread titles, not a board shorthand. It stays free text, slashes included, and the
    // free text searches file names, thread titles, boards and sources (MediaLibraryFilter.TextWords).
    [Fact]
    public void ASlashedTitleTagIsLiteralFreeTextNextToFilterTokens()
    {
        MediaFilterQueryResult alone = Parse("/kot/");
        Assert.Empty(alone.Terms);
        Assert.Empty(alone.Problems);
        Assert.Equal("/kot/", alone.FreeText);
        Assert.Equal("/kot/", Assert.Single(alone.Tokens).Text);

        MediaFilterQueryResult mixed = Parse("/kot/ type:video foo");
        Assert.Equal([Type(MediaLibraryKindFilter.Video)], mixed.Terms);
        Assert.Empty(mixed.Problems);
        Assert.Equal("/kot/ foo", mixed.FreeText);
        // "/g/" names a board that exists, and still is not a board term.
        Assert.Equal("/g/", Parse("/g/").FreeText);
        Assert.Empty(Parse("/g/ -/b/ 4chan/g/").Terms);
        // Enter keeps the text for the field and adds only the term.
        MediaFilterQueryApplication applied = MediaFilterQuery.Apply([], mixed, (_, _) => null);
        Assert.Equal([Type(MediaLibraryKindFilter.Video)], applied.Terms);
        Assert.Equal("/kot/ foo", applied.Remainder);
        // The text reaches the filter as its words, slashes included.
        Assert.Equal(new[] { "/kot/", "foo" }, MediaFilterTerms.ToFilter(applied.Terms.Append(MediaFilterTerm.Name(applied.Remainder))).TextWords);
    }

    [Fact]
    public void QuotedFreeTextWithSpacesAndTheOtherTokensStillParse()
    {
        MediaFilterQueryResult result = Parse("\"/kot/ general\" board:b tag:\"two words\" -type:gif fav rating:>=3 thread:99 bar");
        Assert.Equal([MediaFilterTerm.Board("4chan", "b"), MediaFilterTerm.Tag(Two), Type(MediaLibraryKindFilter.Gif, exclude: true),
            MediaFilterTerm.Flag(MediaFilterField.Favorite), MediaFilterTerm.Rating(3), MediaFilterTerm.InThread(new ResourceId("4chan", "b", "99"))], result.Terms);
        Assert.Empty(result.Problems);
        // The phrase keeps its quotes in the text, so it stays one word after Enter (review 15.11.2026, finding 1).
        Assert.Equal("\"/kot/ general\" bar", result.FreeText);
        Assert.Equal(new[] { "/kot/ general", "bar" }, new MediaLibraryFilter(nameContains: result.FreeText).TextWords);
        // In the live field (before Enter) the quoted run is one word of the text filter as well.
        Assert.Equal(new[] { "/kot/ general", "bar" }, new MediaLibraryFilter(nameContains: "\"/kot/ general\" bar").TextWords);
        // The way back: the text is written as free text and reads back as the same terms.
        IReadOnlyList<MediaFilterTerm> terms = [MediaFilterTerm.Board("4chan", "b"), MediaFilterTerm.Name("/kot/ foo")];
        string text = MediaFilterQuery.Serialize(terms, Vocab());
        Assert.Equal("board:b /kot/ foo", text);
        Assert.Equal(terms, Parse(text).AllTerms);
    }

    // Review 15.11.2026, finding 1: Enter used to rewrite "cat girl" to cat girl, which are two words (cat in the file name, girl in the title).
    [Fact]
    public void AQuotedPhraseStaysOneWordThroughFreeTextEnterAndTheWayBackToText()
    {
        MediaFilterQueryResult phrase = Parse("\"cat girl\"");
        Assert.Equal("\"cat girl\"", phrase.FreeText);
        Assert.Equal(new[] { "cat girl" }, MediaLibraryFilter.WordsOf(phrase.FreeText));
        Assert.Equal(new[] { "cat girl" }, new MediaLibraryFilter(nameContains: phrase.FreeText).TextWords);

        // Enter leaves the quoted phrase in the field, next to the remainder of the other tokens.
        MediaFilterQueryApplication applied = MediaFilterQuery.Apply([], Parse("tag:cats \"a b\""), AdmitLikeThePage);
        Assert.Equal([MediaFilterTerm.Tag(Cats)], applied.Terms);
        Assert.Equal("\"a b\"", applied.Remainder);
        Assert.Equal(new[] { "a b" }, MediaLibraryFilter.WordsOf(applied.Remainder));

        // Serialize and parse again: the phrase is the same one word.
        IReadOnlyList<MediaFilterTerm> terms = [MediaFilterTerm.Tag(Cats), MediaFilterTerm.Name("\"cat girl\" boy")];
        MediaFilterQueryResult back = Parse(MediaFilterQuery.Serialize(terms, Vocab()));
        Assert.Equal(terms, back.AllTerms);
        Assert.Equal(new[] { "cat girl", "boy" }, MediaLibraryFilter.WordsOf(back.FreeText));

        // A quote glued to a word makes one word of the whole run, as WordsOf reads it.
        MediaFilterQueryResult glued = Parse("foo\"b c\"");
        Assert.Equal(new[] { "foob c" }, MediaLibraryFilter.WordsOf(glued.FreeText));
        // Quotes and backslashes inside a phrase are escaped again; a quoted single word loses its quotes.
        Assert.Equal(new[] { "say \"hi\" now" }, MediaLibraryFilter.WordsOf(Parse("\"say \\\"hi\\\" now\"").FreeText));
        Assert.Equal("cat", Parse("\"cat\"").FreeText);
    }

    // Review 15.11.2026, finding 10: an empty phrase is no text: no term, no problem, nothing left in the field.
    [Fact]
    public void AnEmptyPhraseIsNoText()
    {
        Assert.Equal(string.Empty, Parse("\"\"").FreeText);
        Assert.Empty(Parse("\"\"").AllTerms);
        Assert.Equal("cat", Parse("\"\" cat").FreeText);
        MediaFilterQueryApplication applied = MediaFilterQuery.Apply([], Parse("type:video \"\""), AdmitLikeThePage);
        Assert.Equal([Type(MediaLibraryKindFilter.Video)], applied.Terms);
        Assert.Equal(string.Empty, applied.Remainder);
        Assert.False(MediaLibraryFilter.HasText("\"\""));
    }

    [Fact]
    public void EveryFieldReadsItsValue()
    {
        Assert.Equal([MediaFilterTerm.Tag(Cats)], Parse("tag:cats").Terms);
        Assert.Equal([Type(MediaLibraryKindFilter.Gif)], Parse("type:gif").Terms);
        Assert.Equal([MediaFilterTerm.Sound(MediaLibrarySoundFilter.WithSound)], Parse("sound:with").Terms);
        Assert.Equal([MediaFilterTerm.Sound(MediaLibrarySoundFilter.WithoutSound)], Parse("sound:none").Terms);
        Assert.Equal([MediaFilterTerm.Sound(MediaLibrarySoundFilter.WithoutSound)], Parse("sound:without").Terms);
        Assert.Equal([MediaFilterTerm.Source("8kun")], Parse("source:8kun").Terms);
        Assert.Equal([MediaFilterTerm.Board("booru", "all")], Parse("board:all").Terms);
        Assert.Equal([MediaFilterTerm.Rating(3)], Parse("rating:>=3").Terms);
        Assert.Equal([MediaFilterTerm.InThread(new ResourceId("4chan", "b", "99"))], Parse("thread:99").Terms);
        Assert.Equal([MediaFilterTerm.Flag(MediaFilterField.Favorite), MediaFilterTerm.Flag(MediaFilterField.Missing), MediaFilterTerm.Flag(MediaFilterField.Untagged)],
            Parse("fav missing untagged").Terms);
    }

    [Theory]
    [InlineData("rating:3", 3), InlineData("rating:>=3", 3), InlineData("rating:≥4", 4), InlineData("rating:2+", 2), InlineData("rating:>2", 3), InlineData("rating:>=1", 1), InlineData("rating:5", 5)]
    public void RatingsAreWrittenAsTheLeastRating(string text, int least) => Assert.Equal([MediaFilterTerm.Rating(least)], Parse(text).Terms);

    [Theory]
    [InlineData("rating:0"), InlineData("rating:6"), InlineData("rating:>5"), InlineData("rating:x"), InlineData("rating:>=")]
    public void RatingsOutsideOneToFiveStayText(string text)
    {
        MediaFilterQueryResult result = Parse(text);
        Assert.Empty(result.Terms);
        Assert.Equal(text, result.FreeText);
        Assert.Single(result.Problems);
    }

    [Fact]
    public void FieldNamesIgnoreCaseAndValuesFollowTheirOwnRules()
    {
        Assert.Equal([Type(MediaLibraryKindFilter.Video), MediaFilterTerm.Source("4chan"), MediaFilterTerm.Tag(Cats)], Parse("TYPE:Video Source:4CHAN TAG:Cats").Terms);
        Assert.Equal([MediaFilterTerm.Flag(MediaFilterField.Favorite)], Parse("FAV").Terms);
    }

    [Fact]
    public void ALeadingMinusExcludesOnlyWhereTheFieldCanExclude()
    {
        Assert.Equal([MediaFilterTerm.Tag(Cats, exclude: true), Type(MediaLibraryKindFilter.Video, exclude: true), MediaFilterTerm.Source("8kun", exclude: true),
            MediaFilterTerm.Board("4chan", "b", exclude: true)], Parse("-tag:cats -type:video -source:8kun -board:b").Terms);
        foreach (string text in new[] { "-sound:with", "-rating:3", "-fav", "-missing", "-untagged:yes", "-thread:99", "-name:x" })
        {
            MediaFilterQueryResult result = Parse(text);
            Assert.Empty(result.Terms);
            Assert.Equal(MediaFilterQueryProblem.CannotExclude, Assert.Single(result.Problems).Problem);
            Assert.Equal(text, result.FreeText);
        }
    }

    [Fact]
    public void ADoubleMinusIsOrdinaryTextNotAField()
    {
        MediaFilterQueryResult result = Parse("--tag:cats -");
        Assert.Empty(result.Terms);
        Assert.Empty(result.Problems);
        Assert.Equal("--tag:cats -", result.FreeText);
    }

    [Fact]
    public void QuotedValuesMayHoldSpacesAndEscapes()
    {
        Assert.Equal([MediaFilterTerm.Tag(Two)], Parse("tag:\"two words\"").Terms);
        Assert.Equal([MediaFilterTerm.Tag(Two, exclude: true), Type(MediaLibraryKindFilter.Image)], Parse("-tag:\"two words\" type:image").Terms);
        Assert.Equal([MediaFilterTerm.Tag(Guid.Parse("00000000-0000-0000-0000-0000000000c4"))], Parse("tag:\"say \\\"hi\\\"\"").Terms);
        Assert.Equal([MediaFilterTerm.Tag(Guid.Parse("00000000-0000-0000-0000-0000000000c6"))], Parse("tag:日本語").Terms);
        // An unterminated quote runs to the end instead of failing.
        Assert.Equal([MediaFilterTerm.Tag(Two)], Parse("tag:\"two words").Terms);
    }

    [Fact]
    public void ARemainingTextStaysTheFileName()
    {
        MediaFilterQueryResult result = Parse("holiday  type:video  photos");
        Assert.Equal([Type(MediaLibraryKindFilter.Video)], result.Terms);
        Assert.Equal("holiday photos", result.FreeText);
        // A bare quoted phrase is free text and stays one word (it keeps its quotes); quoting a token keeps a filter-looking word text.
        Assert.Equal("\"two words\"", Parse("\"two words\"").FreeText);
        MediaFilterQueryResult escaped = Parse("\"type:video\" \"fav\"");
        Assert.Empty(escaped.Terms);
        Assert.Equal("\"type:video\" \"fav\"", escaped.FreeText);
        Assert.Equal("a b", Parse("name:\"a b\"").FreeText);
        Assert.Equal("12:30 http://x.test", Parse("12:30 http://x.test").FreeText); // "12" is no field name; "http" is one, so the second token is reported but kept
    }

    [Fact]
    public void UnknownFieldsAndValuesStayTextWithAProblem()
    {
        MediaFilterQueryResult result = Parse("colour:red type:video tag:nothing source:nowhere board:zz thread:5 type:movie tag:");
        Assert.Equal([Type(MediaLibraryKindFilter.Video)], result.Terms);
        Assert.Equal("colour:red tag:nothing source:nowhere board:zz thread:5 type:movie tag:", result.FreeText);
        Assert.Equal([MediaFilterQueryProblem.UnknownField, MediaFilterQueryProblem.UnknownValue, MediaFilterQueryProblem.UnknownValue, MediaFilterQueryProblem.UnknownValue,
            MediaFilterQueryProblem.UnknownValue, MediaFilterQueryProblem.UnknownValue, MediaFilterQueryProblem.NeedsValue], result.Problems.Select(token => token.Problem!.Value));
    }

    [Fact]
    public void ABoardOnSeveralSourcesNeedsASourceBeforeItIsOne()
    {
        MediaFilterQueryResult alone = Parse("board:g");
        Assert.Empty(alone.Terms);
        Assert.Equal(MediaFilterQueryProblem.Ambiguous, Assert.Single(alone.Problems).Problem);
        Assert.Equal("board:g", alone.FreeText);
        Assert.Equal([MediaFilterTerm.Board("8kun", "g")], Parse("board:8kun/g").Terms);
        // The sources of the query narrow it, in either order; an excluded source does not.
        Assert.Equal([MediaFilterTerm.Source("4chan"), MediaFilterTerm.Board("4chan", "g")], Parse("source:4chan board:g").Terms);
        Assert.Equal([MediaFilterTerm.Board("8kun", "g", exclude: true), MediaFilterTerm.Source("8kun")], Parse("-board:g source:8kun").Terms);
        Assert.Equal(MediaFilterQueryProblem.Ambiguous, Assert.Single(Parse("-source:4chan board:g").Problems).Problem);
        // Threads: the same number on two boards is ambiguous until the thread names them.
        Assert.Equal(MediaFilterQueryProblem.Ambiguous, Assert.Single(Parse("thread:1234").Problems).Problem);
        Assert.Equal([MediaFilterTerm.InThread(new ResourceId("8kun", "g", "1234"))], Parse("thread:8kun/g/1234").Terms);
    }

    [Fact]
    public void FlagsTakeNoValueButMayBeSaidYes()
    {
        Assert.Equal([MediaFilterTerm.Flag(MediaFilterField.Favorite)], Parse("fav:yes").Terms);
        Assert.Equal([MediaFilterTerm.Flag(MediaFilterField.Missing)], Parse("missing:").Terms);
        Assert.Equal(MediaFilterQueryProblem.UnknownValue, Assert.Single(Parse("fav:maybe").Problems).Problem);
    }

    [Fact]
    public void AnEmptyOrBlankQueryHasNothing()
    {
        foreach (string text in new[] { "", "   ", "\t" })
        {
            MediaFilterQueryResult result = Parse(text);
            Assert.Empty(result.Tokens);
            Assert.Equal(string.Empty, result.FreeText);
        }
        Assert.Empty(MediaFilterQuery.Parse(null).Tokens);
    }

    [Fact]
    public void ParsingIsCultureNeutral()
    {
        System.Globalization.CultureInfo before = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            foreach (string name in new[] { "tr-TR", "de-DE", "ja-JP" })
            {
                System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo(name);
                // The Turkish dotted/dotless i must not break the English names ("TYPE" lower-cases to "type").
                Assert.Equal([Type(MediaLibraryKindFilter.Image), MediaFilterTerm.Rating(3), MediaFilterTerm.Flag(MediaFilterField.Untagged)], Parse("TYPE:IMAGE RATING:>=3 UNTAGGED").Terms);
            }
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = before; }
    }

    // ---- aliases -----------------------------------------------------------------------------------------------------

    [Fact]
    public void AnAliasIsAnotherNameOfAFieldAndTheEnglishNamesStayValid()
    {
        Dictionary<string, MediaFilterField> aliases = new() { ["typ"] = MediaFilterField.Type, ["favorit"] = MediaFilterField.Favorite, ["quelle"] = MediaFilterField.Source };
        Assert.Equal([Type(MediaLibraryKindFilter.Video), MediaFilterTerm.Flag(MediaFilterField.Favorite), MediaFilterTerm.Source("4chan"), Type(MediaLibraryKindFilter.Gif, exclude: true)],
            Parse("TYP:video favorit quelle:4chan -type:gif", aliases).Terms);
        Assert.Equal(MediaFilterQueryProblem.UnknownField, Assert.Single(Parse("typ:video").Problems).Problem); // without the alias it is no field
    }

    [Fact]
    public void AliasesAreReadFromAPackTextAndNeverShadowAnEnglishName()
    {
        Dictionary<string, string> texts = new() { ["media.query.alias.type"] = "Typ, Art", ["media.query.alias.tag"] = "tag; label", ["media.query.alias.fav"] = "type bad-word 123 favorit" };
        IReadOnlyDictionary<string, MediaFilterField> aliases = MediaFilterQueryVocabulary.AliasesFrom(key => texts.GetValueOrDefault(key));
        Assert.Equal(MediaFilterField.Type, aliases["typ"]);
        Assert.Equal(MediaFilterField.Type, aliases["art"]);
        Assert.Equal(MediaFilterField.Tag, aliases["label"]);
        Assert.False(aliases.ContainsKey("tag"));   // English names need no alias
        Assert.False(aliases.ContainsKey("type"));  // and cannot be taken by another field
        Assert.False(aliases.ContainsKey("bad-word")); // letters only
        Assert.False(aliases.ContainsKey("123"));
        Assert.Equal(MediaFilterField.Favorite, aliases["favorit"]);
    }

    public static TheoryData<string> PackCodes()
    {
        TheoryData<string> data = new() { "en", "de" };
        foreach (string code in Texts.BundledPackCodes()) data.Add(code);
        return data;
    }

    [Theory]
    [MemberData(nameof(PackCodes))]
    public void EveryLanguageNamesItsAliasesWithoutAClash(string code)
    {
        Texts.SetLanguage(code);
        Dictionary<string, MediaFilterField> seen = new(StringComparer.Ordinal);
        foreach (MediaFilterField field in Enum.GetValues<MediaFilterField>())
        {
            string key = "media.query.alias." + MediaFilterTerm.KeyOf(field);
            string text = Texts.T(key);
            Assert.False(string.IsNullOrWhiteSpace(text) || text == key, code + " lacks " + key);
            string[] words = text.Split([' ', ',', ';', '、'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.NotEmpty(words);
            foreach (string word in words)
            {
                Assert.True(MediaFilterQuery.IsFieldWord(word), code + " " + key + ": '" + word + "' is not letters only");
                Assert.Equal(word.ToLowerInvariant(), word);
                // Within a pack a word belongs to one field, and it is no English name of another field.
                Assert.True(!seen.TryGetValue(word, out MediaFilterField other) || other == field, code + ": '" + word + "' names two fields");
                seen[word] = field;
                Assert.True(MediaFilterTerm.FieldOf(word) is not { } english || english == field, code + ": '" + word + "' is another field's English name");
            }
        }
        IReadOnlyDictionary<string, MediaFilterField> aliases = MediaFilterQueryVocabulary.AliasesFrom(key => Texts.T(key));
        Assert.All(seen.Where(pair => MediaFilterTerm.FieldOf(pair.Key) == null), pair => Assert.Equal(pair.Value, aliases[pair.Key]));
        // The localized name of the type field works in a query, and so does the English one.
        MediaFilterQueryVocabulary vocabulary = Vocab(aliases);
        string typeAlias = Texts.T("media.query.alias.type").Split([' ', ',', ';', '、'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
        Assert.Equal([Type(MediaLibraryKindFilter.Video)], MediaFilterQuery.Parse(typeAlias + ":video", vocabulary).Terms);
        Assert.Equal([Type(MediaLibraryKindFilter.Video)], MediaFilterQuery.Parse("type:video", vocabulary).Terms);
    }

    // ---- where the text is looked for (in:) ------------------------------------------------------------------------

    private static MediaFilterTerm In(MediaTextFields fields) => MediaFilterTerm.In(fields);

    [Fact]
    public void InNamesTheFieldsTheTextIsLookedForIn()
    {
        MediaFilterQueryResult result = Parse("in:tag,title cat");
        Assert.Equal([In(MediaTextFields.Tag | MediaTextFields.Title)], result.Terms);
        Assert.Equal("cat", result.FreeText);
        Assert.Empty(result.Problems);
        Assert.Equal("title,tag", Assert.Single(result.Terms).Value); // the value is canonical, not as typed
        Assert.Equal([In(MediaTextFields.All)], Parse("in:all").Terms);
        Assert.Equal([In(MediaTextFields.Name)], Parse("IN:Name").Terms);
        Assert.Equal([In(MediaTextFields.Board | MediaTextFields.Source), Type(MediaLibraryKindFilter.Video)], Parse("in:source,board type:video").Terms);
        Assert.Equal([In(MediaTextFields.Tag)], Parse("in:\"tag\" cat girl").Terms);
        // The localized names work beside the English keys.
        Assert.Equal([In(MediaTextFields.Title | MediaTextFields.Tag | MediaTextFields.Name)], Parse("in:titel,schlagwort,datei").Terms);
        Assert.Equal([In(MediaTextFields.Title | MediaTextFields.Tag)], Parse("in:titel,tag").Terms);
        Assert.Equal([In(MediaTextFields.Title)], Parse("in:threadtitel").Terms);
        // And the aliases of the field itself.
        Assert.Equal([In(MediaTextFields.Tag)], Parse("wo:tag", new Dictionary<string, MediaFilterField> { ["wo"] = MediaFilterField.In }).Terms);
    }

    [Fact]
    public void InWithAnUnknownValueStaysTextWithAProblemAndCannotExclude()
    {
        foreach (string text in new[] { "in:bogus", "in:tag,bogus", "in:tags", "in:none", "in:,"})
        {
            MediaFilterQueryResult result = Parse(text + " cat");
            Assert.Empty(result.Terms);
            Assert.Equal(MediaFilterQueryProblem.UnknownValue, Assert.Single(result.Problems).Problem);
            Assert.Equal(text + " cat", result.FreeText); // kept as text, as every value that is not understood
        }
        Assert.Equal(MediaFilterQueryProblem.NeedsValue, Assert.Single(Parse("in:").Problems).Problem);
        MediaFilterQueryResult excluded = Parse("-in:tag cat");
        Assert.Empty(excluded.Terms);
        Assert.Equal(MediaFilterQueryProblem.CannotExclude, Assert.Single(excluded.Problems).Problem);
        Assert.Equal("-in:tag cat", excluded.FreeText);
        // Quoted, the token is plain text.
        MediaFilterQueryResult quoted = Parse("\"in:tag\" cat");
        Assert.Empty(quoted.Terms);
        Assert.Empty(quoted.Problems);
        Assert.Equal("\"in:tag\" cat", quoted.FreeText);
    }

    [Fact]
    public void InCombinesWithAPhraseAndAnEmptyPhraseAndOnlyTheLastOneCounts()
    {
        MediaFilterQueryResult phrase = Parse("in:tag \"cat girl\" dog");
        Assert.Equal([In(MediaTextFields.Tag)], phrase.Terms);
        Assert.Equal("\"cat girl\" dog", phrase.FreeText);
        Assert.Equal(new[] { "cat girl", "dog" }, MediaLibraryFilter.WordsOf(phrase.FreeText));
        MediaFilterQueryResult empty = Parse("in:tag \"\"");
        Assert.Equal([In(MediaTextFields.Tag)], empty.Terms);
        Assert.Equal(string.Empty, empty.FreeText);
        Assert.Equal([In(MediaTextFields.Tag)], empty.AllTerms); // the scope without text stays a term (the page keeps it for the next word)
        // One scope per filter: Enter keeps the last one.
        MediaFilterQueryApplication applied = MediaFilterQuery.Apply([], Parse("in:tag cat in:name,title"), AdmitLikeThePage);
        Assert.Equal([In(MediaTextFields.Name | MediaTextFields.Title)], applied.Terms);
        Assert.Equal("cat", applied.Remainder);
        Assert.Equal(2, applied.Added.Count);
        // Applied to a set that holds a scope, the new one replaces it.
        Assert.Equal([In(MediaTextFields.Tag)], MediaFilterQuery.Apply([In(MediaTextFields.Name)], Parse("in:tag"), AdmitLikeThePage).Terms);
    }

    [Fact]
    public void TheScopeIsWrittenDirectlyBeforeTheTextAndNeverForAll()
    {
        MediaFilterTerm cats = MediaFilterTerm.Tag(Cats);
        Assert.Equal("in:title,tag cat", MediaFilterQuery.Serialize([In(MediaTextFields.Tag | MediaTextFields.Title), MediaFilterTerm.Name("cat")], Vocab()));
        Assert.Equal("tag:cats type:video in:tag cat girl",
            MediaFilterQuery.Serialize([In(MediaTextFields.Tag), cats, Type(MediaLibraryKindFilter.Video), MediaFilterTerm.Name("cat girl")], Vocab()));
        Assert.Equal("tag:cats cat", MediaFilterQuery.Serialize([cats, In(MediaTextFields.All), MediaFilterTerm.Name("cat")], Vocab()));
        Assert.Equal("in:name", MediaFilterQuery.Serialize([In(MediaTextFields.Name)], Vocab()));
        Assert.Equal(string.Empty, MediaFilterQuery.Serialize([In(MediaTextFields.All)], Vocab()));
        // The same in every language: the keys are English.
        Texts.SetLanguage("de");
        Assert.Equal("in:title,tag cat", MediaFilterQuery.Serialize([In(MediaTextFields.Tag | MediaTextFields.Title), MediaFilterTerm.Name("cat")], Vocab()));
        // A text that reads like the token is written as name:"…".
        Assert.Equal("in:tag name:\"in:board cat\"", MediaFilterQuery.Serialize([In(MediaTextFields.Tag), MediaFilterTerm.Name("in:board cat")], Vocab()));
    }

    [Fact]
    public void EveryScopeNameInEveryLanguageIsAWordForOneFieldAndNoClash()
    {
        foreach (string code in new[] { "en", "de" }.Concat(Texts.BundledPackCodes()))
        {
            Texts.SetLanguage(code);
            Dictionary<string, MediaTextFields> seen = new(StringComparer.Ordinal);
            foreach (MediaTextFields field in MediaFilterTerm.TextFieldOrder)
            {
                string key = "media.query.scope.alias." + MediaFilterTerm.TextFieldKey(field);
                string text = Texts.T(key);
                Assert.False(string.IsNullOrWhiteSpace(text) || text == key, code + " lacks " + key);
                string[] words = text.Split([' ', ',', ';', '、'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                Assert.NotEmpty(words);
                foreach (string word in words)
                {
                    Assert.True(MediaFilterQuery.IsFieldWord(word), code + " " + key + ": '" + word + "' is not letters only");
                    Assert.Equal(word.ToLowerInvariant(), word);
                    Assert.True(!seen.TryGetValue(word, out MediaTextFields other) || other == field, code + ": '" + word + "' names two fields");
                    seen[word] = field;
                    Assert.True(MediaFilterTerm.TextFieldsOf(word) is not { } english || english == field, code + ": '" + word + "' is another field's English key or all");
                }
            }
            IReadOnlyDictionary<string, MediaTextFields> aliases = MediaFilterQueryVocabulary.ScopeAliasesFrom(key => Texts.T(key));
            Assert.All(seen.Where(pair => MediaFilterTerm.TextFieldsOf(pair.Key) == null), pair => Assert.Equal(pair.Value, aliases[pair.Key]));
            // The field's own name: present, letters only, and no other field's English name.
            string alias = Texts.T("media.query.alias.in");
            Assert.False(string.IsNullOrWhiteSpace(alias) || alias == "media.query.alias.in", code + " lacks media.query.alias.in");
            // Each field's first alias works in a query next to the English key.
            MediaFilterQueryVocabulary vocabulary = new() { ScopeAliases = aliases, Aliases = MediaFilterQueryVocabulary.AliasesFrom(key => Texts.T(key)) };
            foreach (MediaTextFields field in MediaFilterTerm.TextFieldOrder)
            {
                string first = Texts.T("media.query.scope.alias." + MediaFilterTerm.TextFieldKey(field)).Split([' ', ',', ';', '、'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
                Assert.Equal([In(field)], MediaFilterQuery.Parse("in:" + first, vocabulary).Terms);
                Assert.Equal([In(field)], MediaFilterQuery.Parse("in:" + MediaFilterTerm.TextFieldKey(field), vocabulary).Terms);
            }
            string field0 = alias.Split([' ', ',', ';', '、'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).First(word => MediaFilterTerm.FieldOf(word) == null);
            Assert.Equal([In(MediaTextFields.Tag)], MediaFilterQuery.Parse(field0 + ":tag", vocabulary).Terms);
        }
    }

    // ---- the way back to text ----------------------------------------------------------------------------------------

    [Fact]
    public void ATermSetIsWrittenInEnglishWithQuotesWhereNeeded()
    {
        MediaFilterTerm[] terms =
        [
            MediaFilterTerm.Tag(Cats), MediaFilterTerm.Tag(Two, exclude: true), Type(MediaLibraryKindFilter.Video), MediaFilterTerm.Sound(MediaLibrarySoundFilter.WithoutSound),
            MediaFilterTerm.Source("4chan"), MediaFilterTerm.Board("4chan", "g"), MediaFilterTerm.Rating(3), MediaFilterTerm.Flag(MediaFilterField.Favorite),
            MediaFilterTerm.Name("holiday photos")
        ];
        Assert.Equal("tag:cats -tag:\"two words\" type:video sound:none source:4chan board:g rating:>=3 fav holiday photos", MediaFilterQuery.Serialize(terms, Vocab()));
        Texts.SetLanguage("de");
        Assert.Equal("tag:cats -tag:\"two words\" type:video sound:none source:4chan board:g rating:>=3 fav holiday photos", MediaFilterQuery.Serialize(terms, Vocab()));
    }

    private static IEnumerable<object[]> RoundTripSetsCore()
    {
        yield return [new[] { MediaFilterTerm.Tag(Cats), MediaFilterTerm.Tag(Wip, exclude: true), Type(MediaLibraryKindFilter.Image), Type(MediaLibraryKindFilter.Gif) }];
        yield return [new[] { Type(MediaLibraryKindFilter.Video, exclude: true), MediaFilterTerm.Source("8kun", exclude: true), MediaFilterTerm.Board("4chan", "b", exclude: true) }];
        yield return [new[] { MediaFilterTerm.Source("4chan"), MediaFilterTerm.Board("4chan", "g"), MediaFilterTerm.Rating(5) }];
        yield return [new[] { MediaFilterTerm.Board("8kun", "g"), MediaFilterTerm.Board("4chan", "g", exclude: true) }]; // ambiguous board names: written in full
        yield return [new[] { MediaFilterTerm.Source("4chan"), MediaFilterTerm.Board("4chan", "g", exclude: true) }];
        yield return [new[] { MediaFilterTerm.Flag(MediaFilterField.Favorite), MediaFilterTerm.Flag(MediaFilterField.Missing), MediaFilterTerm.Flag(MediaFilterField.Untagged) }];
        yield return [new[] { Type(MediaLibraryKindFilter.Video), MediaFilterTerm.Sound(MediaLibrarySoundFilter.WithSound) }];
        yield return [new[] { MediaFilterTerm.InThread(new ResourceId("4chan", "b", "99")) }];
        yield return [new[] { MediaFilterTerm.InThread(new ResourceId("8kun", "g", "1234")) }]; // the number alone is ambiguous: written in full
        yield return [new[] { MediaFilterTerm.Tag(Guid.Parse("00000000-0000-0000-0000-0000000000c4")), MediaFilterTerm.Tag(Guid.Parse("00000000-0000-0000-0000-0000000000c6")) }];
        yield return [new[] { MediaFilterTerm.Name("two  spaces and \"quotes\" and back\\slash") }];
        yield return [new[] { MediaFilterTerm.Name("type:video") }]; // text that reads like a filter is written as name:"…"
        yield return [new[] { MediaFilterTerm.Name("fav") }];
        yield return [new[] { MediaFilterTerm.Name("-fav") }];
        yield return [new[] { MediaFilterTerm.Name("12:30 notes") }];
        yield return [new[] { MediaFilterTerm.Tag(Cats), MediaFilterTerm.Name("-draft") }];
        yield return [new[] { MediaFilterTerm.Tag(Cats), MediaFilterTerm.In(MediaTextFields.Title | MediaTextFields.Tag), MediaFilterTerm.Name("cat girl") }];
        yield return [new[] { MediaFilterTerm.Type(MediaLibraryKindFilter.Video), MediaFilterTerm.In(MediaTextFields.Board), MediaFilterTerm.Name("\"cat girl\" dog") }];
        yield return [new[] { MediaFilterTerm.In(MediaTextFields.Name) }]; // the scope of a box that is empty for now
        yield return [new[] { MediaFilterTerm.In(MediaTextFields.Source | MediaTextFields.Board), MediaFilterTerm.Name("in:tag") }];
        yield return [Array.Empty<MediaFilterTerm>()];
    }

    public static TheoryData<int> RoundTripSetNumbers()
    {
        TheoryData<int> data = new();
        for (int index = 0; index < RoundTripSetsCore().Count(); index++) data.Add(index);
        return data;
    }

    [Theory]
    [MemberData(nameof(RoundTripSetNumbers))]
    public void WritingATermSetAsTextAndReadingItGivesTheSameTerms(int set)
    {
        MediaFilterTerm[] terms = (MediaFilterTerm[])RoundTripSetsCore().ElementAt(set)[0];
        MediaFilterQueryVocabulary vocabulary = Vocab();
        string text = MediaFilterQuery.Serialize(terms, vocabulary);
        MediaFilterQueryResult back = MediaFilterQuery.Parse(text, vocabulary);
        Assert.Empty(back.Problems);
        Assert.Equal(terms, back.AllTerms, EqualityComparer<MediaFilterTerm>.Default);
        // And the text is stable: reading it and writing it again changes nothing.
        Assert.Equal(text, MediaFilterQuery.Serialize(back.AllTerms, vocabulary));
    }

    [Theory]
    [InlineData("type:video -tag:wip source:4chan board:g holiday")]
    [InlineData("-type:gif sound:with rating:>=2 fav name:\"a b\" tag:\"two words\"")]
    [InlineData("untagged missing -source:8kun -board:g source:4chan")]
    public void ReadingTextAndWritingItAgainReadsBackTheSame(string text)
    {
        MediaFilterQueryVocabulary vocabulary = Vocab();
        MediaFilterQueryResult first = MediaFilterQuery.Parse(text, vocabulary);
        MediaFilterQueryResult second = MediaFilterQuery.Parse(MediaFilterQuery.Serialize(first.AllTerms, vocabulary), vocabulary);
        Assert.Empty(first.Problems);
        Assert.Equal(first.AllTerms, second.AllTerms);
    }

    [Fact]
    public void AnUnknownTagIsWrittenByItsIdAndKeptAsTextWhenReadAgain()
    {
        Guid gone = Guid.Parse("00000000-0000-0000-0000-0000000000ff");
        string text = MediaFilterQuery.Serialize([MediaFilterTerm.Tag(gone)], Vocab());
        Assert.Equal("tag:" + gone.ToString("D"), text);
        Assert.Equal(MediaFilterQueryProblem.UnknownValue, Assert.Single(Parse(text).Problems).Problem);
    }

    [Fact]
    public void QuotingEscapesQuotesAndBackslashesOnly()
    {
        Assert.Equal("plain", MediaFilterQuery.Quote("plain"));
        Assert.Equal("\"a b\"", MediaFilterQuery.Quote("a b"));
        Assert.Equal("\"\"", MediaFilterQuery.Quote(""));
        Assert.Equal("\"say \\\"hi\\\"\"", MediaFilterQuery.Quote("say \"hi\""));
        Assert.Equal("\"a\\\\b\"", MediaFilterQuery.Quote("a\\b"));
        Assert.Equal("日本語", MediaFilterQuery.Quote("日本語"));
    }

    // ---- applying ---------------------------------------------------------------------------------------------------

    private static MediaFilterQueryProblem? AdmitLikeThePage(IReadOnlyList<MediaFilterTerm> terms, MediaFilterTerm term)
    {
        IReadOnlyList<MediaFilterTerm> next = MediaFilterTermSet.With(terms, term);
        try { _ = MediaFilterTerms.ToFilter(next); return null; }
        catch (ArgumentException) { return MediaFilterQueryProblem.Refused; }
    }

    [Fact]
    public void ApplyingAQueryAddsTermsInOrderAndLeavesTheNameText()
    {
        MediaFilterQueryApplication applied = MediaFilterQuery.Apply([MediaFilterTerm.Rating(2)],
            Parse("holiday type:video colour:red source:4chan board:g photos"), AdmitLikeThePage);
        Assert.Equal([MediaFilterTerm.Rating(2), Type(MediaLibraryKindFilter.Video), MediaFilterTerm.Source("4chan"), MediaFilterTerm.Board("4chan", "g")], applied.Terms);
        Assert.Equal(3, applied.Added.Count);
        Assert.Equal("holiday colour:red photos", applied.Remainder);
        Assert.Equal([("colour:red", MediaFilterQueryProblem.UnknownField)], applied.Kept);
    }

    [Fact]
    public void ATermThatDoesNotFitStaysTextAndTheOthersStillCount()
    {
        // Every type excluded leaves nothing: the last exclusion is refused and stays in the text.
        MediaFilterQueryApplication applied = MediaFilterQuery.Apply([], Parse("-type:image -type:gif -type:video -type:other fav"), AdmitLikeThePage);
        Assert.Equal([Type(MediaLibraryKindFilter.Image, true), Type(MediaLibraryKindFilter.Gif, true), Type(MediaLibraryKindFilter.Video, true), MediaFilterTerm.Flag(MediaFilterField.Favorite)],
            applied.Terms);
        Assert.Equal("-type:other", applied.Remainder);
        Assert.Equal([("-type:other", MediaFilterQueryProblem.Refused)], applied.Kept);
    }

    [Fact]
    public void ATermAlreadyInTheFilterIsConsumedAndAnOppositeOneSwitchesIt()
    {
        MediaFilterQueryApplication same = MediaFilterQuery.Apply([MediaFilterTerm.Tag(Cats)], Parse("tag:cats"), AdmitLikeThePage);
        Assert.Equal([MediaFilterTerm.Tag(Cats)], same.Terms);
        Assert.Empty(same.Added);
        Assert.Equal(string.Empty, same.Remainder);
        MediaFilterQueryApplication opposite = MediaFilterQuery.Apply([MediaFilterTerm.Tag(Cats)], Parse("-tag:cats"), AdmitLikeThePage);
        Assert.Equal([MediaFilterTerm.Tag(Cats, exclude: true)], opposite.Terms);
        // A sound implies videos, as it does for the combos.
        Assert.Equal([MediaFilterTerm.Sound(MediaLibrarySoundFilter.WithSound), Type(MediaLibraryKindFilter.Video)],
            MediaFilterQuery.Apply([], Parse("sound:with"), AdmitLikeThePage).Terms);
    }

    [Fact]
    public void TheTextOfAnApplicationIsAQueryThatLeavesNothingToConvertAgain()
    {
        MediaFilterQueryApplication applied = MediaFilterQuery.Apply([], Parse("type:video colour:red holiday"), AdmitLikeThePage);
        MediaFilterQueryResult again = Parse(applied.Remainder);
        Assert.Empty(again.Terms);
        Assert.Equal(applied.Remainder, again.FreeText);
    }

    // ---- texts ------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("cs")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("it")]
    [InlineData("ja")]
    [InlineData("ko")]
    [InlineData("pl")]
    [InlineData("pt-BR")]
    [InlineData("ru")]
    [InlineData("zh-Hans")]
    public void EveryShippedQueryTipIncludesExecutableQueueAndBooruExamplesWithoutFallback(string code)
    {
        const string key = "media.query.tip";
        string tip;
        if (code is "en" or "de")
            tip = (code == "en" ? BuiltInCatalog.English : BuiltInCatalog.German)[key];
        else
        {
            using Stream stream = typeof(Texts).Assembly.GetManifestResourceStream(Texts.BundledPackResourcePrefix + code + ".json")!;
            Assert.NotNull(stream);
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonProperty entry = Assert.Single(document.RootElement.GetProperty("strings").EnumerateObject(),
                property => property.NameEquals(key));
            tip = entry.Value.GetString()!;
        }

        Texts.SetLanguage(code);
        Assert.Equal(code, Texts.Language);
        Assert.Empty(Texts.Warnings);
        Assert.Equal(tip, Texts.T(key));
        // This hint has no format arguments; invalid or newly introduced placeholders must not hide behind T's fallback.
        Assert.Equal(tip, string.Format(CultureInfo.GetCultureInfo(code), tip, Array.Empty<object>()));
        Assert.Contains("tag:\"", tip); // Keep the existing quoted-value example.

        (string Text, MediaFilterTerm Term)[] examples =
        [
            ("queue:yes", MediaFilterTerm.InQueue()),
            ("queue:no", MediaFilterTerm.InQueue(false)),
            ("booru:tag", MediaFilterTerm.SourceTag("tag")),
            ("-booru:tag", MediaFilterTerm.SourceTag("tag", exclude: true))
        ];
        string[] listedExamples = tip.Split([',', '、'], StringSplitOptions.TrimEntries);
        foreach (var (example, term) in examples)
        {
            Assert.Contains(example, listedExamples);
            MediaFilterQueryResult parsed = Parse(example);
            Assert.Equal([term], parsed.Terms);
            Assert.Empty(parsed.Problems);
            Assert.Empty(parsed.FreeText);
        }
    }

    [Fact]
    public void TheQueryTextsExistInGermanAndEnglishWithTheirPlaceholders()
    {
        foreach (string code in new[] { "en", "de" })
        {
            Texts.SetLanguage(code);
            Assert.Contains("{0}", Texts.T("media.query.converted", "{0}"));
            Assert.NotEqual("media.query.tip", Texts.T("media.query.tip"));
            Assert.NotEqual("media.query.reason.unknownField", Texts.T("media.query.reason.unknownField"));
            Assert.NotEqual("media.filter.chip.copyText", Texts.T("media.filter.chip.copyText"));
        }
        Texts.SetLanguage("de");
        Assert.Equal("Filter als Text kopieren", Texts.T("media.filter.chip.copyText"));
        Assert.Equal("Aus dem Suchfeld übernommen: Typ: Video", Texts.T("media.query.converted", "Typ: Video"));
    }
}
