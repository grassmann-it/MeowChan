using System.Collections;
using System.Globalization;
using System.Text;
using MeowChan.Tags;

namespace MeowChan.Tests.Tags;

public sealed class PrivateTagSetAdversarialRegressionTests
{
    [Theory]
    [InlineData("\u212A", "k", "K", "K")]
    [InlineData("\u017F", "s", "\u017F", "S")]
    [InlineData("\u00B5", "\u03BC", "\u00B5", "\u039C")]
    public void AdditionalCanonicalAndSimpleCaseCollisionsSurviveRemoveAndReadmission(
        string raw, string aliasText, string display, string key)
    {
        PrivateTagName first = Name(raw);
        PrivateTagName alias = Name(aliasText);
        Assert.Equal(display, first.Display);
        Assert.Equal(key, first.Key);
        Assert.Equal(key, alias.Key);
        Assert.True(first.Equals((object)alias));
        Assert.True(first == alias);
        Assert.False(first != alias);
        Assert.Equal(first.GetHashCode(), alias.GetHashCode());

        PrivateTagName sentinel = Name("sentinel");
        PrivateTagSet original = Add(Add(PrivateTagSet.Empty, first), sentinel);
        Assert.Same(original, Add(original, alias));
        PrivateTagSet removed = Remove(original, alias);
        PrivateTagSet readded = Add(removed, alias);
        Assert.Same(sentinel, readded.Items[0]);
        Assert.Same(alias, readded.Items[1]);
        Assert.Same(first, original.Items[0]);
        Assert.False(removed.Contains(first));
    }

    [Fact]
    public void ActiveEnumeratorAndNonGenericMutationAttemptsCannotChangeBranchedSnapshots()
    {
        PrivateTagName first = Name("\u00B5");
        PrivateTagName second = Name("second");
        PrivateTagSet original = Add(Add(PrivateTagSet.Empty, first), second);
        using IEnumerator<PrivateTagName> enumerator = original.Items.GetEnumerator();
        Assert.True(enumerator.MoveNext());
        Assert.Same(first, enumerator.Current);

        PrivateTagSet branch = Add(Remove(original, Name("\u03BC")), Name("third"));
        IList view = Assert.IsAssignableFrom<IList>(original.Items);
        Assert.True(view.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => view.Insert(0, second));
        Assert.Throws<NotSupportedException>(() => { view[0] = second; });
        Assert.Throws<NotSupportedException>(() => view.Remove(first));
        Assert.True(enumerator.MoveNext());
        Assert.Same(second, enumerator.Current);
        Assert.False(enumerator.MoveNext());
        Assert.Equal(new[] { first, second }, original.Items.ToArray());
        Assert.Equal(new[] { "second", "third" }, branch.Items.Select(item => item.Display).ToArray());
    }

    [Theory]
    [InlineData(127, 511)]
    [InlineData(128, 512)]
    public void MixedWidthNamesReachExactUtf8BoundaryWithoutCountingUtf16AsScalars(
        int supplementaryCount, int expectedBytes)
    {
        // 127 supplementary scalars plus one BMP scalar = 128 scalars / 511 bytes;
        // 128 supplementary scalars = 128 scalars / 512 bytes.
        string text = string.Concat(Enumerable.Repeat("\U0001F600", supplementaryCount))
            + (supplementaryCount == 127 ? "\u4E00" : "");
        PrivateTagName boundary = Name(text);
        Assert.Equal(128, boundary.Display.EnumerateRunes().Count());
        Assert.Equal(expectedBytes, Encoding.UTF8.GetByteCount(boundary.Display));
        Assert.Equal(supplementaryCount * 2 + (supplementaryCount == 127 ? 1 : 0),
            boundary.Display.Length);
        Assert.Same(boundary, Assert.Single(Add(PrivateTagSet.Empty, boundary).Items));
        Reject(text + "a", TagNameError.TooManyScalars);
    }

    [Fact]
    public void RawCapAndPreNormalizationInspectionHaveDeterministicErrorPrecedence()
    {
        string prefix = new('a', PrivateTagName.MaximumUtf16CodeUnits - 1);
        Reject(prefix + "\uD800", TagNameError.InvalidUnicode);
        Reject(prefix + "\uD800a", TagNameError.InputTooLong);
        Reject(prefix + "\n", TagNameError.InvalidCharacter);
        Reject(prefix + "\na", TagNameError.InputTooLong);
        Reject(new string('a', PrivateTagName.MaximumScalars + 1) + "\u2060",
            TagNameError.InvalidCharacter);
        Reject(new string('a', PrivateTagName.MaximumScalars + 1) + "\uDC00",
            TagNameError.InvalidUnicode);
    }

    [Fact]
    public void FullSetChecksUnicodeCollisionAtLastSlotBeforeCapacityAndPreservesOldSnapshot()
    {
        PrivateTagSet full = PrivateTagSet.Empty;
        for (int i = 0; i < PrivateTagSet.MaximumCount - 1; i++)
            full = Add(full, Name("bounded-" + i.ToString(CultureInfo.InvariantCulture)));
        PrivateTagName last = Name("\u00B5");
        full = Add(full, last);
        IReadOnlyList<PrivateTagName> snapshot = full.Items;
        Assert.Equal(1024, full.Count);
        Assert.Same(full, Add(full, Name("\u03BC")));
        Assert.Same(last, snapshot[1023]);
        PrivateTagName overflow = Name("overflow");
        Assert.False(full.TryAdd(overflow, out PrivateTagSet rejected, out PrivateTagSetError error));
        Assert.Equal(PrivateTagSetError.CapacityExceeded, error);
        Assert.Same(full, rejected);
        PrivateTagSet smaller = Remove(full, Name("\u039C"));
        Assert.Equal(1023, smaller.Count);
        PrivateTagSet replaced = Add(smaller, overflow);
        Assert.Equal(1024, replaced.Count);
        Assert.Same(overflow, replaced.Items[1023]);
        Assert.False(replaced.Contains(last));
        Assert.Same(snapshot, full.Items);
        Assert.Same(last, snapshot[1023]);
        Assert.True(full.Contains(Name("\u03BC")));
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

    private static PrivateTagSet Remove(PrivateTagSet set, PrivateTagName name)
    {
        Assert.True(set.TryRemove(name, out PrivateTagSet result, out PrivateTagSetError error));
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
