using MeowChan.Tags;

namespace MeowChan.Tests.Tags;

// Existing pure tag primitives only. Scope selection is not tag-query evaluation;
// include/exclude and AND matching need a product consumer, not a test-only matcher.
public sealed class PrivateTagQueryBoundaryRegressionTests
{
    private static readonly PrivateThreadTagId Cat = new(new Guid("00000000-0000-0000-0000-000000000211"));
    private static readonly PrivateThreadTagId Dog = new(new Guid("00000000-0000-0000-0000-000000000212"));
    private static readonly ResourceId First = new("synthetic-source", "board", "100");
    private static readonly ResourceId Second = new("synthetic-source", "board", "101");

    [Fact]
    public void CompositionPrecedesTheNormalizedScalarLimit()
    {
        // 256 raw scalars become exactly 128 NFC scalars. Counting the input first
        // would reject a valid name; changing NFC to NFD would also break this boundary.
        string raw = string.Concat(Enumerable.Repeat("a\u030A", PrivateTagName.MaximumScalars));
        string expected = new('\u00E5', PrivateTagName.MaximumScalars);
        PrivateTagName name = Name(raw);

        Assert.Equal(expected, name.Display);
        Assert.Equal(new string('\u00C5', PrivateTagName.MaximumScalars), name.Key);
        Assert.Equal(Name(expected), name);
        Reject(raw + "a", TagNameError.TooManyScalars);
    }

    [Fact]
    public void CanonicalDecompositionCanExpandPastTheNormalizedScalarLimit()
    {
        // U+0344 is excluded from canonical composition: each scalar becomes
        // U+0308 U+0301 even in NFC. Raw-length-only validation would miss overflow.
        string raw = new('\u0344', PrivateTagName.MaximumScalars / 2);
        PrivateTagName boundary = Name(raw);

        Assert.Equal(string.Concat(Enumerable.Repeat("\u0308\u0301", PrivateTagName.MaximumScalars / 2)),
            boundary.Display);
        Assert.Equal(PrivateTagName.MaximumScalars, boundary.Display.Length);
        Reject(raw + "\u0344", TagNameError.TooManyScalars);
    }

    [Fact]
    public void RawUtf16CapStillAppliesWhenAllButOneCharacterWouldBeTrimmed()
    {
        string atCap = new string(' ', PrivateTagName.MaximumUtf16CodeUnits - 1) + "a";

        Assert.Equal("a", Name(atCap).Display);
        Reject(" " + atCap, TagNameError.InputTooLong);
    }

    [Theory]
    [InlineData("\u212B", "A\u030A", "\u00C5")]
    [InlineData("a\u0315\u0300", "\u00E0\u0315", "\u00E0\u0315")]
    [InlineData("\u1100\u1161", "\uAC00", "\uAC00")]
    public void CanonicalAliasesReorderingAndHangulCompositionShareOneMembership(
        string raw, string equivalent, string expectedDisplay)
    {
        PrivateTagName first = Name(raw);
        PrivateTagName alias = Name(equivalent);
        Assert.Equal(expectedDisplay, first.Display);
        Assert.Equal(first.Key, alias.Key);
        Assert.Equal(first.GetHashCode(), alias.GetHashCode());
        PrivateTagSet original = Add(PrivateTagSet.Empty, first);

        Assert.Same(original, Add(original, alias));
        Assert.Same(first, Assert.Single(original.Items));
        Assert.True(original.Contains(alias));
        Assert.True(original.TryRemove(alias, out PrivateTagSet removed, out PrivateTagSetError error));
        Assert.Equal(PrivateTagSetError.None, error);
        Assert.Same(PrivateTagSet.Empty, removed);
        Assert.Equal(expectedDisplay, Assert.Single(original.Items).Display);
    }

    [Theory]
    [InlineData("\u2460", "1")]
    [InlineData("\uFF11", "1")]
    [InlineData("\u2167", "VIII")]
    public void CompatibilityEquivalentNamesAreNotFoldedByNfcMembership(string raw, string compatibilityForm)
    {
        PrivateTagName first = Name(raw);
        PrivateTagName other = Name(compatibilityForm);
        Assert.Equal(raw, first.Display);
        Assert.NotEqual(first.Key, other.Key);
        PrivateTagSet one = Add(PrivateTagSet.Empty, first);

        Assert.False(one.Contains(other));
        PrivateTagSet two = Add(one, other);
        Assert.Equal(2, two.Count);
        Assert.Same(first, two.Items[0]);
        Assert.Same(other, two.Items[1]);
        Assert.True(two.TryRemove(other, out PrivateTagSet removed, out PrivateTagSetError error));
        Assert.Equal(PrivateTagSetError.None, error);
        Assert.Same(first, Assert.Single(removed.Items));
    }

    [Theory]
    [InlineData("\u00A0")]
    [InlineData("\u2007")]
    [InlineData("\u202F")]
    [InlineData("\u3000")]
    public void UnicodeSpaceIsTrimmedAtEdgesButNotCollapsedInsideTheName(string space)
    {
        string interior = "cat" + space + "toy";
        PrivateTagName name = Name(space + interior + space);

        Assert.Equal(interior, name.Display);
        Assert.Equal("CAT" + space + "TOY", name.Key);
        PrivateTagSet set = Add(PrivateTagSet.Empty, name);
        Assert.True(set.Contains(Name(interior)));
        Assert.False(set.Contains(Name("cat toy")));
        Assert.False(set.Contains(Name("cattoy")));
        Reject(space + space, TagNameError.Blank);
    }

    [Theory]
    [InlineData("-cat", "cat")]
    [InlineData("cat dog", "cat")]
    [InlineData("cat,dog", "dog")]
    [InlineData("cat|dog", "cat")]
    [InlineData("rating:g", "g")]
    public void QueryLookingPrivateNamesRemainOneLiteralMembership(string literal, string component)
    {
        // PrivateTagName/PrivateTagSet do not parse provider queries or infer
        // include/exclude, token splitting, OR, or conjunction from name text.
        PrivateTagName name = Name(literal);
        PrivateTagName componentName = Name(component);
        PrivateTagSet set = Add(PrivateTagSet.Empty, name);

        Assert.Equal(literal, Assert.Single(set.Items).Display);
        Assert.False(set.Contains(componentName));
        Assert.True(set.TryRemove(componentName, out PrivateTagSet unchanged, out PrivateTagSetError error));
        Assert.Equal(PrivateTagSetError.None, error);
        Assert.Same(set, unchanged);
        Assert.True(set.Contains(Name(literal)));
    }

    [Fact]
    public void SelectedScopeRetainsAllPositiveAssignmentsForOneExactTarget()
    {
        ThreadTagProjection state = Define(Define(new(), Cat, "cat"), Dog, "dog");
        state = Add(Add(Add(state, Cat, First), Dog, First), Cat, Second);

        Assert.True(state.TrySelectThreads([First], out ThreadTagSnapshot? snapshot,
            out ThreadTagError error, selected: true));
        Assert.Equal(ThreadTagError.None, error);
        Assert.Equal(new[] { Cat, Dog }, snapshot!.Assignments.Select(item => item.TagId).ToArray());
        Assert.All(snapshot.Assignments, item => Assert.Equal(First, item.Thread));
        Assert.Equal(new[] { Cat, Dog }, snapshot.Definitions.Select(item => item.Id).ToArray());
        Assert.DoesNotContain(snapshot.Assignments, item => item.Thread == Second);
    }

    [Fact]
    public void RemovingOneAssignmentExcludesOnlyThatTagAndLeavesTheOtherAssignment()
    {
        ThreadTagProjection state = Define(Define(new(), Cat, "cat"), Dog, "dog");
        state = Add(Add(state, Cat, First), Dog, First);

        Assert.True(state.TryRemove(Cat, First, state.Revision, out ThreadTagProjection afterRemove,
            out ThreadTagError error));
        Assert.Equal(ThreadTagError.None, error);
        Assert.True(afterRemove.TrySelectThreads([First], out ThreadTagSnapshot? snapshot,
            out error, selected: true));
        Assert.Equal(ThreadTagError.None, error);
        Assert.Equal(new[] { Dog }, snapshot!.Assignments.Select(item => item.TagId).ToArray());
        Assert.Equal(new[] { Dog }, snapshot.Definitions.Select(item => item.Id).ToArray());
        Assert.True(afterRemove.TrySelectThreads([Second], out ThreadTagSnapshot? other,
            out error, selected: true));
        Assert.Equal(ThreadTagError.None, error);
        Assert.Empty(other!.Assignments);
    }

    private static ThreadTagProjection Define(ThreadTagProjection state, PrivateThreadTagId id, string text)
    {
        Assert.True(state.TryDefine(id, Name(text), state.Revision,
            out ThreadTagProjection result, out ThreadTagError error));
        Assert.Equal(ThreadTagError.None, error);
        return result;
    }

    private static ThreadTagProjection Add(ThreadTagProjection state, PrivateThreadTagId id, ResourceId thread)
    {
        Assert.True(state.TryAdd(id, thread, state.Revision,
            out ThreadTagProjection result, out ThreadTagError error));
        Assert.Equal(ThreadTagError.None, error);
        return result;
    }

    private static PrivateTagName Name(string text)
    {
        Assert.True(PrivateTagName.TryCreate(text, out PrivateTagName? name, out TagNameError error));
        Assert.Equal(TagNameError.None, error);
        return Assert.IsType<PrivateTagName>(name);
    }

    private static PrivateTagSet Add(PrivateTagSet set, PrivateTagName name)
    {
        Assert.True(set.TryAdd(name, out PrivateTagSet result, out PrivateTagSetError error));
        Assert.Equal(PrivateTagSetError.None, error);
        return result;
    }

    private static void Reject(string text, TagNameError expected)
    {
        Assert.False(PrivateTagName.TryCreate(text, out PrivateTagName? name, out TagNameError error));
        Assert.Null(name);
        Assert.Equal(expected, error);
    }
}
