using System.Globalization;
using MeowChan.Tags;

namespace MeowChan.Tests.Tags;

public sealed class PrivateTagSetTests
{
    [Fact]
    public void EmptyHasNoMembersAndMissingRemovalIsSuccessfulNoOp()
    {
        PrivateTagSet empty = PrivateTagSet.Empty;
        PrivateTagName name = Name("cat");

        Assert.Equal(0, empty.Count);
        Assert.Empty(empty.Items);
        Assert.False(empty.Contains(name));
        Assert.False(empty.Contains(null));
        Assert.Same(empty, Remove(empty, name));
    }

    [Fact]
    public void DuplicateAddPreservesFirstNameObjectDisplayAndPosition()
    {
        PrivateTagName first = Name("cat");
        PrivateTagName collision = Name("CAT");
        PrivateTagSet original = Add(Add(PrivateTagSet.Empty, first), Name("dog"));

        PrivateTagSet duplicate = Add(original, collision);

        Assert.Same(original, duplicate);
        Assert.True(duplicate.Contains(collision));
        Assert.Same(first, duplicate.Items[0]);
        Assert.Equal(new[] { "cat", "dog" }, Displays(duplicate));
        Assert.Same(duplicate, Add(duplicate, first));
    }

    [Theory]
    [InlineData("  A\u030A b  ", "\u00E5 B", "\u00C5 b")]
    [InlineData("caf\u00E9", "CAFE\u0301", "caf\u00E9")]
    [InlineData("\u03C2", "\u03C3", "\u03C2")]
    [InlineData("\U00010428", "\U00010400", "\U00010428")]
    public void UnicodeKeyCollisionsRetainFirstDisplayAndUseSameKeyForRemoval(
        string firstInput, string equivalentInput, string firstDisplay)
    {
        PrivateTagName first = Name(firstInput);
        PrivateTagName equivalent = Name(equivalentInput);
        Assert.Equal(first.Key, equivalent.Key);
        PrivateTagSet set = Add(PrivateTagSet.Empty, first);

        Assert.Same(set, Add(set, equivalent));
        Assert.Single(set.Items);
        Assert.Equal(firstDisplay, set.Items[0].Display);
        Assert.Same(first, set.Items[0]);
        Assert.True(set.Contains(equivalent));
        Assert.Same(PrivateTagSet.Empty, Remove(set, equivalent));
        Assert.Equal(firstDisplay, set.Items[0].Display);
    }

    [Theory]
    [InlineData("stra\u00DFe", "STRASSE")]
    [InlineData("stra\u00DFe", "STRA\u1E9EE")]
    [InlineData("i", "\u0131")]
    [InlineData("i", "\u0130")]
    [InlineData("cat toy", "cat  toy")]
    [InlineData("A", "\u0410")]
    [InlineData("\u2764", "\u2764\uFE0F")]
    public void MembershipUsesExactlyExistingOrdinalKeysWithoutAdditionalFolding(
        string leftInput, string rightInput)
    {
        PrivateTagName left = Name(leftInput);
        PrivateTagName right = Name(rightInput);
        bool sameKey = string.Equals(left.Key, right.Key, StringComparison.Ordinal);
        PrivateTagSet one = Add(PrivateTagSet.Empty, left);

        Assert.Equal(sameKey, one.Contains(right));
        PrivateTagSet both = Add(one, right);
        Assert.Equal(sameKey ? 1 : 2, both.Count);
        Assert.Same(left, both.Items[0]);
        if (sameKey)
            Assert.Same(one, both);
        else
            Assert.Same(right, both.Items[1]);

        PrivateTagSet removed = Remove(both, right);
        Assert.False(removed.Contains(right));
        Assert.Equal(!sameKey, removed.Contains(left));
        Assert.Equal(sameKey ? 0 : 1, removed.Count);
    }

    [Theory]
    [InlineData("tr-TR")]
    [InlineData("de-DE")]
    public void MembershipAndAdmissionOrderDoNotDependOnCurrentCulture(string cultureName)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            PrivateTagSet set = Add(Add(PrivateTagSet.Empty, Name("i")), Name("z"));
            Assert.Same(set, Add(set, Name("I")));
            set = Add(set, Name("\u0130"));
            set = Add(set, Name("\u0131"));

            Assert.Equal(new[] { "i", "z", "\u0130", "\u0131" }, Displays(set));
            Assert.True(set.Contains(Name("I")));
            Assert.Equal(new[] { "z", "\u0130", "\u0131" }, Displays(Remove(set, Name("I"))));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void IndependentBranchesAndPreviouslyExposedSnapshotsNeverChange()
    {
        PrivateTagName cat = Name("cat");
        PrivateTagName dog = Name("dog");
        PrivateTagSet original = Add(Add(PrivateTagSet.Empty, cat), dog);
        IReadOnlyList<PrivateTagName> snapshot = original.Items;
        PrivateTagSet added = Add(original, Name("bird"));
        PrivateTagSet removed = Remove(original, Name("CAT"));

        Assert.Equal(new[] { "cat", "dog" }, snapshot.Select(name => name.Display).ToArray());
        Assert.Same(cat, snapshot[0]);
        Assert.Same(dog, snapshot[1]);
        Assert.Equal(new[] { "cat", "dog", "bird" }, Displays(added));
        Assert.Equal(new[] { "dog" }, Displays(removed));
        Assert.Equal(new[] { "cat", "dog" }, Displays(original));
        Assert.Empty(PrivateTagSet.Empty.Items);
    }

    [Fact]
    public void ItemsDoNotExposeAMutableArrayOrListEvenThroughInterfaceCasts()
    {
        PrivateTagName cat = Name("cat");
        PrivateTagSet set = Add(PrivateTagSet.Empty, cat);
        Assert.False(set.Items is PrivateTagName[]);
        IList<PrivateTagName> list = Assert.IsAssignableFrom<IList<PrivateTagName>>(set.Items);

        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => { list[0] = Name("dog"); });
        Assert.Throws<NotSupportedException>(() => list.Add(Name("dog")));
        Assert.Throws<NotSupportedException>(() => list.RemoveAt(0));
        Assert.Throws<NotSupportedException>(() => list.Clear());
        Assert.Same(cat, Assert.Single(set.Items));
        Assert.Same(set, Add(set, Name("CAT")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void RemovingFirstMiddleOrLastPreservesOrderAndIsIdempotent(int index)
    {
        PrivateTagName[] names = [Name("cat"), Name("dog"), Name("bird")];
        PrivateTagSet original = PrivateTagSet.Empty;
        foreach (PrivateTagName name in names) original = Add(original, name);

        PrivateTagSet removed = Remove(original, Name(names[index].Key));

        Assert.Equal(names.Where((_, position) => position != index).Select(name => name.Display).ToArray(),
            Displays(removed));
        Assert.False(removed.Contains(names[index]));
        Assert.Same(removed, Remove(removed, names[index]));
        Assert.Equal(new[] { "cat", "dog", "bird" }, Displays(original));
    }

    [Fact]
    public void RemovalDoesNotRememberDeletedDisplayOrChangeOtherSets()
    {
        PrivateTagSet original = Add(Add(PrivateTagSet.Empty, Name("cat")), Name("dog"));
        PrivateTagSet removed = Remove(original, Name("CAT"));
        PrivateTagSet readded = Add(removed, Name("CAT"));

        Assert.Equal(new[] { "dog", "CAT" }, Displays(readded));
        Assert.Equal(new[] { "cat", "dog" }, Displays(original));
        Assert.Equal(new[] { "dog" }, Displays(removed));
    }

    [Fact]
    public void NullInputIsRejectedWithConstantCodesAndOriginalResult()
    {
        PrivateTagSet[] sets = [PrivateTagSet.Empty, Add(PrivateTagSet.Empty, Name("kept"))];
        foreach (PrivateTagSet set in sets)
        {
            string[] before = Displays(set);
            Assert.Null(Record.Exception(() => set.TryAdd(null, out _, out _)));
            Assert.False(set.TryAdd(null, out PrivateTagSet addResult, out PrivateTagSetError addError));
            Assert.Same(set, addResult);
            Assert.Equal(PrivateTagSetError.NullName, addError);
            Assert.False(set.TryRemove(null, out PrivateTagSet removeResult, out PrivateTagSetError removeError));
            Assert.Same(set, removeResult);
            Assert.Equal(PrivateTagSetError.NullName, removeError);
            Assert.False(set.Contains(null));
            Assert.Equal(before, Displays(set));
        }
    }

    [Fact]
    public void RejectedRawNamesCannotEnterSetAndDoNotEchoInput()
    {
        // Keep malformed UTF-16 in method data, not in serialized attribute metadata.
        string?[] candidates =
        [
            null, "", " \u00A0 ", "SECRET\n", "SECRET\u202E", "\uD800SECRET",
            new string('S', PrivateTagName.MaximumScalars + 1),
            new string('S', PrivateTagName.MaximumUtf16CodeUnits + 1)
        ];
        PrivateTagSet original = Add(PrivateTagSet.Empty, Name("kept"));
        foreach (string? raw in candidates)
        {
            // Raw strings have exactly one validation boundary: the existing name value type.
            Assert.Null(Record.Exception(() => PrivateTagName.TryCreate(raw, out _, out _)));
            Assert.False(PrivateTagName.TryCreate(raw, out PrivateTagName? invalid, out TagNameError nameError));
            Assert.Null(invalid);
            Assert.NotEqual(TagNameError.None, nameError);

            Assert.Null(Record.Exception(() => original.TryAdd(invalid, out _, out _)));
            Assert.False(original.TryAdd(invalid, out PrivateTagSet result, out PrivateTagSetError error));
            Assert.Same(original, result);
            Assert.Equal(PrivateTagSetError.NullName, error);
            Assert.Equal(new[] { "kept" }, Displays(original));
        }
    }

    [Fact]
    public void CapacityRejectsOnlyNewKeysWithoutChangingOriginalAndRemovalFreesRoom()
    {
        PrivateTagSet full = PrivateTagSet.Empty;
        for (int index = 0; index < PrivateTagSet.MaximumCount; index++)
            full = Add(full, Name("tag-" + index.ToString(CultureInfo.InvariantCulture)));
        string[] before = Displays(full);
        IReadOnlyList<PrivateTagName> snapshot = full.Items;
        PrivateTagName overflow = Name("SECRET-overflow");

        Assert.Equal(PrivateTagSet.MaximumCount, full.Count);
        Assert.Same(full, Add(full, Name("TAG-0")));
        Assert.False(full.TryAdd(overflow, out PrivateTagSet rejected, out PrivateTagSetError error));
        Assert.Same(full, rejected);
        Assert.Equal(PrivateTagSetError.CapacityExceeded, error);
        Assert.False(full.Contains(overflow));
        Assert.Equal(before, Displays(full));
        Assert.Same(snapshot, full.Items);
        Assert.False(full.TryAdd(null, out PrivateTagSet invalid, out PrivateTagSetError invalidError));
        Assert.Same(full, invalid);
        Assert.Equal(PrivateTagSetError.NullName, invalidError);
        Assert.Same(full, Remove(full, overflow));

        PrivateTagSet smaller = Remove(full, Name("TAG-0"));
        PrivateTagSet replaced = Add(smaller, overflow);
        Assert.Equal(PrivateTagSet.MaximumCount - 1, smaller.Count);
        Assert.Equal(PrivateTagSet.MaximumCount, replaced.Count);
        Assert.Same(overflow, replaced.Items[replaced.Count - 1]);
        Assert.False(replaced.Contains(Name("tag-0")));
        Assert.Equal(before, Displays(full));
        // Another rejection is still atomic; no eviction, truncation or partially filled result.
        Assert.False(full.TryAdd(overflow, out PrivateTagSet retried, out PrivateTagSetError retryError));
        Assert.Same(full, retried);
        Assert.Equal(PrivateTagSetError.CapacityExceeded, retryError);
    }

    private static string[] Displays(PrivateTagSet set) => set.Items.Select(name => name.Display).ToArray();

    private static PrivateTagName Name(string candidate)
    {
        Assert.True(PrivateTagName.TryCreate(candidate, out PrivateTagName? name, out TagNameError error));
        Assert.Equal(TagNameError.None, error);
        return name!;
    }

    private static PrivateTagSet Add(PrivateTagSet set, PrivateTagName name)
    {
        Assert.True(set.TryAdd(name, out PrivateTagSet result, out PrivateTagSetError error));
        Assert.Equal(PrivateTagSetError.None, error);
        return result;
    }

    private static PrivateTagSet Remove(PrivateTagSet set, PrivateTagName name)
    {
        Assert.True(set.TryRemove(name, out PrivateTagSet result, out PrivateTagSetError error));
        Assert.Equal(PrivateTagSetError.None, error);
        return result;
    }
}
