using System;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MeowChan.Interop;

namespace MeowChan.Tests.Interop;

public sealed class SafeFilenameTemplateTests
{
    private static readonly string Hash = new('A', 64);
    private static SafeFilenameItem Item(string id = "stable-1", string source = "source", string post = "123",
        string role = "original", string? hash = null, string ext = "jpg") =>
        new(id, new(source, post, role, hash ?? Hash, ext));
    private static SafeFilenameResult Run(string template, params SafeFilenameItem[] items) =>
        SafeFilenameTemplate.Assign(template, items.ToImmutableArray());

    [Fact]
    public void ExactTokensAndMeasuredHashArePreservedWithFullStableSuffix()
    {
        var result = Run("{source}_{post}_{role}_{hash}.{ext}", Item());
        string suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("stable-1"))).ToLowerInvariant();
        Assert.True(result.Success);
        Assert.Equal("source_123_original_" + Hash + "~" + suffix + ".jpg", result.Inventory["stable-1"]);
    }

    [Theory]
    [InlineData("{unknown}")]
    [InlineData("{Source}")]
    [InlineData("{post.ToString()}")]
    [InlineData("{{post}}")]
    [InlineData("{post")]
    [InlineData("post}")]
    [InlineData("{}")]
    public void ExpressionsAndUnknownSyntaxReject(string template)
    {
        Assert.Equal(SafeFilenameError.InvalidTemplate, Run(template, Item()).Error);
    }

    [Theory]
    [InlineData("../a")]
    [InlineData("/absolute")]
    [InlineData("C:\\absolute")]
    [InlineData("a\\b")]
    [InlineData("a:b")]
    [InlineData("a..b")]
    [InlineData("a.")]
    [InlineData("a ")]
    [InlineData("a\u0000b")]
    [InlineData("a\u007fb")]
    [InlineData("a\u202eb")]
    [InlineData("a\u2066b")]
    [InlineData("a?b")]
    [InlineData("")]
    public void UnsafeLeavesRejectBeforeSuffix(string template)
    {
        var result = Run(template, Item());
        Assert.Equal(SafeFilenameError.UnsafeName, result.Error);
        Assert.Empty(result.Inventory);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con.jpg")]
    [InlineData("PRN.txt")]
    [InlineData("AUX")]
    [InlineData("nul.data")]
    [InlineData("COM1.jpg")]
    [InlineData("LPT9")]
    [InlineData("COM¹.txt")]
    [InlineData("LPT²")]
    [InlineData("CON .jpg")]
    [InlineData("CONOUT$")]
    public void WindowsReservedNamesAreNotRepairedBySuffix(string template) =>
        Assert.Equal(SafeFilenameError.UnsafeName, Run(template, Item()).Error);

    [Fact]
    public void CaseUnicodeSameBytesAndDifferentExtensionsNeverMergeIdentity()
    {
        var items = new[] { Item("a", "Photo"), Item("b", "photo"),
            Item("c", "é"), Item("d", "e\u0301"), Item("e", "photo", ext: "png") };
        var forward = Run("{source}.{ext}", items);
        var reverse = Run("{source}.{ext}", items.Reverse().ToArray());
        Assert.True(forward.Success);
        Assert.Equal(forward.Inventory.ToArray(), reverse.Inventory.ToArray());
        Assert.Equal(5, forward.Inventory.Values.Select(n => n.Normalize(NormalizationForm.FormC))
            .Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var item in items)
            Assert.Equal(Run("{source}.{ext}", item).Inventory[item.ItemId], forward.Inventory[item.ItemId]);
        Assert.Equal(new[] { "a", "b", "c", "d", "e" }, forward.Inventory.Keys.ToArray());
    }

    [Fact]
    public void ExactBaseCollisionsStillHaveOneInventoryEntryPerId()
    {
        var result = Run("same.jpg", Item("A"), Item("a"));
        Assert.True(result.Success);
        Assert.Equal(2, result.Inventory.Count);
        Assert.NotEqual(result.Inventory["A"], result.Inventory["a"]);
    }

    [Fact]
    public void StrictUnicodeRejectsLoneSurrogatesInEveryInputClass()
    {
        foreach (var bad in new[] { "\ud800", "\udfff" })
        {
            Assert.Equal(SafeFilenameError.InvalidUnicode, Run(bad, Item()).Error);
            Assert.Equal(SafeFilenameError.InvalidUnicode, Run("x", Item(bad)).Error);
            Assert.Equal(SafeFilenameError.InvalidUnicode, Run("x", Item(source: bad)).Error);
        }
        Assert.True(Run("{post}.jpg", Item(post: "😀")).Success);
    }

    [Fact]
    public void NormalizationInvalidUnicodeReturnsTypedEmptyResultWithoutRawDiagnostics()
    {
        // ICU rejects U+FFFE during Normalize, although strict UTF-8 accepts it.
        // A valid earlier item must not escape as a partial inventory on late failure.
        foreach (var result in new[]
        {
            Run("private\uFFFE.{ext}", Item()),
            Run("{source}.{ext}", Item("a"), Item("z-secret", source: "private\uFFFE")),
            Run("{post}.{ext}", Item(post: "private\uFFFE")),
            Run("{role}.{ext}", Item(role: "private\uFFFE"))
        })
        {
            Assert.Equal(SafeFilenameError.InvalidUnicode, result.Error);
            Assert.Empty(result.Inventory);
            Assert.DoesNotContain("private", result.ToString());
            Assert.DoesNotContain("z-secret", result.ToString());
        }
    }

    [Theory]
    [InlineData("\U000E0061")]
    [InlineData("\U000E0001")]
    [InlineData("\U0001BCA0")]
    public void SupplementaryFormatScalarsRejectInLiteralsAndAllTextValues(string format)
    {
        string value = "private" + format;
        var literal = Run(value + ".{ext}", Item());
        Assert.Equal(SafeFilenameError.UnsafeName, literal.Error);
        Assert.Empty(literal.Inventory);
        Assert.Equal(SafeFilenameError.UnsafeName, Run(value + ".{ext}").Error);
        foreach (var item in new[] { Item("z", source: value), Item("z", post: value), Item("z", role: value) })
        {
            foreach (string template in new[] { "{source}_{post}_{role}.{ext}", "unused-values.jpg" })
            {
                var result = Run(template, Item("a"), item);
                Assert.Equal(SafeFilenameError.InvalidValue, result.Error);
                Assert.Empty(result.Inventory);
                Assert.DoesNotContain("private", result.ToString());
            }
        }
    }

    [Theory]
    [InlineData("\U0001F600")]
    [InlineData("\U00010400")]
    public void SupplementaryNonFormatScalarsRemainUnchanged(string scalar)
    {
        var result = Run("{source}_{post}_{role}.{ext}",
            Item(source: scalar, post: scalar, role: scalar));
        Assert.True(result.Success);
        Assert.StartsWith(scalar + "_" + scalar + "_" + scalar + "~", result.Inventory["stable-1"]);
        Assert.True(Run("photo" + scalar + ".{ext}", Item()).Success);
    }

    [Theory]
    [InlineData("../jpg")]
    [InlineData(".jpg")]
    [InlineData("j pé")]
    [InlineData("")]
    [InlineData("abcdefghijklmnopq")]
    public void ExtensionsAreBoundedAscii(string ext) =>
        Assert.Equal(SafeFilenameError.InvalidExtension, Run("x", Item(ext: ext)).Error);

    [Fact]
    public void InvalidUnusedValuesRejectWithoutDiagnosticsOrPartialInventory()
    {
        var result = Run("constant.jpg", Item("first"), Item("secret-id", source: "Bearer:secret"));
        Assert.Equal(SafeFilenameError.InvalidValue, result.Error);
        Assert.Empty(result.Inventory);
        Assert.DoesNotContain("secret", result.ToString());
        Assert.Equal(SafeFilenameError.InvalidHash, Run("x", Item(hash: "claim")).Error);
        Assert.Equal(SafeFilenameError.InvalidHash, Run("x", Item(hash: new string('g', 64))).Error);
    }

    [Fact]
    public void BudgetsAreUtf8AndNeverTruncate()
    {
        Assert.True(Run(new string('x', 175), Item()).Success); // 175 + 65 suffix
        Assert.Equal(SafeFilenameError.OutputTooLarge, Run(new string('x', 176), Item()).Error);
        Assert.True(Run(new string('é', 87) + "x", Item()).Success);
        Assert.Equal(SafeFilenameError.OutputTooLarge, Run(new string('é', 88), Item()).Error);
        Assert.Equal(SafeFilenameError.TemplateTooLarge, Run(new string('x', 4097), Item()).Error);
        Assert.Equal(SafeFilenameError.TemplateTooLarge, Run(new string('é', 2049), Item()).Error);
        Assert.Equal(SafeFilenameError.InvalidInput, Run("x", Item(new string('x', 4097))).Error);
        Assert.Equal(SafeFilenameError.DuplicateItemId, Run("x", Item(), Item()).Error);
        var thousand = Enumerable.Range(0, 1000).Select(i => Item(i.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray();
        Assert.True(Run("x", thousand).Success);
        Assert.Equal(SafeFilenameError.TooManyItems, Run("x", thousand.Append(Item("extra")).ToArray()).Error);
    }

    [Fact]
    public void EmptyAndDefaultBatchesHaveExplicitBehavior()
    {
        Assert.True(Run("{source}.{ext}").Success);
        Assert.Equal(SafeFilenameError.UnsafeName, Run("../x").Error);
        Assert.Equal(SafeFilenameError.InvalidInput, SafeFilenameTemplate.Assign("x", default).Error);
        Assert.Equal(SafeFilenameError.InvalidInput, Run(null!, Item()).Error);
        Assert.Equal(SafeFilenameError.InvalidInput, Run("x", new SafeFilenameItem[] { null! }).Error);
    }
}
