using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

// TG-01/TG-03/TG-04 (UX review 17.11.2026): the tag fields complete the token under the caret, not the last word; a completion
// replaces only that token (or the words of a name with spaces already typed), and the one real match is preselected for Enter.
[Collection("Localization")]
public sealed class TagCaretCompletionTests : IDisposable
{
    public TagCaretCompletionTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static (int Start, int End, string Typed)? At(string text, int start, int? end = null) =>
        TagEntryParser.TokenAt(text, start, end ?? start) is { } token ? (token.Start, token.End, token.Typed) : null;

    [Fact]
    public void TheTokenIsTheOneUnderTheCaretInSpaceMode()
    {
        const string text = "hot inn blonde";
        Assert.Equal((0, 3, "hot"), At(text, 0));            // the very start: the first token
        Assert.Equal((0, 0, ""), At(" hot", 0));             // before leading space: nothing there
        Assert.Equal((0, 3, "hot"), At(text, 2));            // inside the first
        Assert.Equal((0, 3, "hot"), At(text, 3));            // right after it
        Assert.Equal((4, 4, ""), At(text, 4));               // right after a separator: empty
        Assert.Equal((4, 7, "inn"), At(text, 6));            // a middle token
        Assert.Equal((4, 7, "inn"), At(text, 7));
        Assert.Equal((8, 14, "blonde"), At(text, 14));       // the last one
        Assert.False(TagEntryParser.TokenAt(text, 6, 6)!.Hard);
        Assert.Equal((0, 0, ""), At("", 0));
        Assert.Equal((5, 5, ""), At("a b, ", 5));           // after a trailing hard separator
    }

    [Fact]
    public void QuotedAndUnclosedTokensKeepTheirQuotesInTheRange()
    {
        Assert.Equal((2, 12, "big tits"), At("x \"big tits\" y", 6));
        Assert.Equal((2, 12, "big tits"), At("x \"big tits\" y", 12)); // right after the closing quote
        Assert.True(TagEntryParser.TokenAt("x \"big tits\" y", 6, 6)!.Quoted);
        Assert.Equal((2, 8, "big t"), At("x \"big t", 8));            // unclosed: runs to the end
        Assert.Equal((2, 8, "big t"), At("x \"big t", 3));            // right after the opening quote
        Assert.Equal((0, 5, "a\"b"), At("\"a\\\"b", 5));               // escapes removed
        Assert.Equal((0, 3, "abc"), At("abc\"d\"", 3));                // a word ends at a quote
    }

    [Fact]
    public void WithAHardSeparatorThePartIsTheTokenAndItsSpacesBelongToTheName()
    {
        TagEntryToken token = TagEntryParser.TokenAt("foo, big ti", 11, 11)!;
        Assert.Equal((5, 11, "big ti", true), (token.Start, token.End, token.Typed, token.Hard));
        Assert.Equal((5, 11, "big ti"), At("foo, big ti", 8));      // a caret inside the part, after its space
        Assert.Equal((4, 4, ""), At("foo, big ti", 4));             // right after the comma
        Assert.Equal((5, 5, ""), At("foo, big ti", 5));             // after the comma and its space
        Assert.Equal((0, 3, "foo"), At("foo, big ti", 2));
        Assert.Equal((5, 11, "big ti"), At("foo, big ti , bar", 11));
        Assert.Equal((5, 5, ""), At("a;b ;c", 5));
    }

    [Fact]
    public void ASelectionInsideOneTokenKeepsItAndAWiderOneHasNoToken()
    {
        const string text = "hot inn blonde";
        Assert.Equal((4, 7, "inn"), At(text, 4, 7));   // the whole word selected (a double click)
        Assert.Equal((4, 7, "inn"), At(text, 5, 6));
        Assert.Equal((4, 7, "inn"), At(text, 6, 5));   // a backwards selection
        Assert.Null(At(text, 2, 6));                    // over two tokens: no list
        Assert.Null(At(text, 0, 14));
        Assert.Equal((1, 1, ""), At("a  b", 1, 2));     // only spaces selected: an empty token
    }

    [Fact]
    public void ReplaceKeepsEverythingAroundTheTokenAndQuotesOnlyInSpaceMode()
    {
        // Earlier and later tags stay byte for byte; the caret is right behind the inserted name.
        Assert.Equal(("hot innocent blonde", 12), TagEntryParser.Replace("hot inn blonde", 4, 7, "innocent", false));
        Assert.Equal(("hot \"big tits\" blonde", 14), TagEntryParser.Replace("hot inn blonde", 4, 7, "big tits", false));
        Assert.Equal(("innocent  blonde,x", 8), TagEntryParser.Replace("inn  blonde,x", 0, 3, "innocent", false));
        // Hard-separator mode: spaces belong to the name, never quoted; a comma or quote in the name is.
        Assert.Equal(("foo, big tits", 13), TagEntryParser.Replace("foo, big ti", 5, 11, "big tits", true));
        Assert.Equal(("foo, \"a,b\"", 10), TagEntryParser.Replace("foo, a", 5, 6, "a,b", true));
        Assert.Equal(("foo, big tits , bar", 13), TagEntryParser.Replace("foo, big ti , bar", 5, 11, "big tits", true));
        // A token that the rest of the draft follows without a separator gets exactly one.
        Assert.Equal(("x innocent \"q\"", 11), TagEntryParser.Replace("x inn\"q\"", 2, 5, "innocent", false));
        Assert.Equal(("x \"big tits\" y", 13), TagEntryParser.Replace("x \"big t\"y", 2, 9, "big tits", false));
        // A name that becomes the whole draft stays bare: as a whole it is one tag.
        Assert.Equal(("innocent", 8), TagEntryParser.Replace("inn", 0, 3, "innocent", false));
        Assert.Equal(("big tits", 8), TagEntryParser.Replace(" big ti", 1, 7, "big tits", false));
        Assert.True(TagEntryParser.NamesMore(" blonde"));
        Assert.False(TagEntryParser.NamesMore(" , ;"));
    }

    private sealed class Store : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly ProfileLease _lease;
        internal readonly PrivateTagStore Tags;
        internal Store(params string[] names)
        {
            _lease = ProfileLease.Acquire(_temp.Path);
            Tags = new PrivateTagStore(_temp.Path, _lease);
            foreach (string name in names) Tags.Define(name);
        }
        internal PrivateTagStoreSnapshot Snapshot => Tags.ReadSnapshot();
        public void Dispose() { _lease.Dispose(); _temp.Dispose(); }
    }

    private static IdentitySuggestion[] Rows(PrivateTagStoreSnapshot snapshot, string text, int? caret = null, int? end = null) =>
        TagSuggestionIndex.CombineEntry(snapshot, null, text, new HashSet<TagId>(), selectionStart: caret, selectionEnd: end ?? caret);

    [Fact]
    public void TheOneRealMatchIsPreselectedButNeverSeveralOrTheTypedNameItself()
    {
        using Store store = new("innocent", "inner", "hot", "blonde");
        PrivateTagStoreSnapshot snapshot = store.Snapshot;

        IdentitySuggestion[] inno = Rows(snapshot, "inno");
        Assert.Equal(new[] { "innocent", "inno" }, inno.Select(row => row.Value));
        Assert.True(inno[0].Preselect);
        Assert.False(inno[1].Preselect); // the "New tag" row is never preselected and never counts
        Assert.Equal(TagSuggestionIndex.CreateGroup, inno[1].Source);

        // Two real matches: nothing is chosen for the user, Enter adds the typed text.
        Assert.DoesNotContain(Rows(snapshot, "inn"), row => row.Preselect);
        // The typed name itself is no choice either.
        Assert.DoesNotContain(Rows(snapshot, "innocent"), row => row.Preselect);
        Assert.DoesNotContain(Rows(snapshot, "hot innocent"), row => row.Preselect);
    }

    [Fact]
    public void AMiddleTokenIsCompletedInPlaceAndTheCaretGoesBehindIt()
    {
        using Store store = new("innocent", "hot", "blonde");
        PrivateTagStoreSnapshot snapshot = store.Snapshot;

        IdentitySuggestion row = Assert.Single(Rows(snapshot, "hot inn blonde", 6));
        Assert.Equal("hot innocent blonde", row.Value);
        Assert.Equal(12, row.Caret);
        Assert.True(row.InPlace);   // another tag follows: accepting only edits the draft
        Assert.True(row.Preselect);
        // The first token, with others after it: no "New tag" row, completion in place.
        IdentitySuggestion first = Assert.Single(Rows(snapshot, "inn hot", 2));
        Assert.Equal(("innocent hot", 8, true), (first.Value, first.Caret!.Value, first.InPlace));
        // The last token, as before: the end of the draft, nothing follows.
        IdentitySuggestion last = Assert.Single(Rows(snapshot, "hot inn", 7));
        Assert.Equal(("hot innocent", 12, false), (last.Value, last.Caret!.Value, last.InPlace));
        // Caret right after a separator or a selection over two tokens: no list.
        Assert.Empty(Rows(snapshot, "hot inn blonde", 4));
        Assert.Empty(Rows(snapshot, "hot inn blonde", 0));   // the first token already is a name: nothing to complete in place
        Assert.Equal(new[] { "hot inn" }, Rows(snapshot, "ho inn", 0).Select(item => item.Value)); // at the very start: the first token
        Assert.Empty(Rows(snapshot, "hot inn blonde", 2, 6));
        // The completed draft keeps every tag.
        Assert.Equal(new[] { "hot", "innocent", "blonde" }, TagEntryParser.Parse(row.Value, snapshot).Items.Select(item => item.Name.Display));
    }

    [Fact]
    public void ANameWithSpacesReplacesTheWordsAlreadyTypedAndIsListedOnce()
    {
        using Store store = new("big tits", "hot");
        PrivateTagStoreSnapshot snapshot = store.Snapshot;

        // TG-04 case A: one row, not "big tits" and "big \"big tits\"".
        IdentitySuggestion whole = Assert.Single(Rows(snapshot, "big ti"));
        Assert.Equal("big tits", whole.Value);
        Assert.True(whole.Preselect);
        // Case B: the typed "big" is replaced, not repeated.
        IdentitySuggestion later = Assert.Single(Rows(snapshot, "hot big ti"));
        Assert.Equal("hot \"big tits\"", later.Value);
        Assert.Equal(new[] { "hot", "big tits" }, TagEntryParser.Parse(later.Value, snapshot).Items.Select(item => item.Name.Display));
        // Case C: hard-separator mode, the part is the token and the name stays unquoted.
        IdentitySuggestion comma = Assert.Single(Rows(snapshot, "foo, big ti"));
        Assert.Equal("foo, big tits", comma.Value);
        Assert.Equal(new[] { "foo", "big tits" }, TagEntryParser.Parse(comma.Value, snapshot).Items.Select(item => item.Name.Display));
        // An unclosed quote completes as a quoted name.
        Assert.Equal("hot \"big tits\"", Assert.Single(Rows(snapshot, "hot \"big t")).Value);
        // A word of the name alone still finds it (word start), written in place of that word.
        Assert.Equal("hot \"big tits\"", Assert.Single(Rows(snapshot, "hot ti")).Value);
        // In the middle: the words before the caret token are replaced, the tag after stays.
        IdentitySuggestion middle = Assert.Single(Rows(snapshot, "big ti hot", 6));
        Assert.Equal(("\"big tits\" hot", 10, true), (middle.Value, middle.Caret!.Value, middle.InPlace));
    }

    [Fact]
    public void OneTagIsNeverListedTwiceWhetherByNameOrByAlias()
    {
        using Store store = new("piss", "pee");
        PrivateTagStoreSnapshot before = store.Snapshot;
        store.Tags.Merge(before.Tags.Single(tag => tag.Name.Display == "piss").Id, [before.Tags.Single(tag => tag.Name.Display == "pee").Id]);
        PrivateTagStoreSnapshot snapshot = store.Snapshot;

        // "p" matches the tag piss and the alias pee → piss: one row for the tag.
        IdentitySuggestion[] rows = Rows(snapshot, "p");
        Assert.Single(rows, row => row.Value == "piss");
        Assert.Equal(new[] { "piss", "p" }, rows.Select(row => row.Value));
        IdentitySuggestion[] later = Rows(snapshot, "x p");
        Assert.Equal(new[] { "x piss" }, later.Select(row => row.Value));
    }
}
