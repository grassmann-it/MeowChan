using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MeowChan.Interop;

namespace MeowChan.Tests.Interop;

/// <summary>Independent byte/batch boundary coverage for SafeFilenameTemplate.</summary>
public sealed class SafeFilenameTemplateBoundaryTests
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string MeasuredHash = new('a', 64);

    private static SafeFilenameItem Item(string itemId, string source = "synthetic") =>
        new(itemId, new(source, "123", "original", MeasuredHash, "jpg"));

    private static SafeFilenameResult Run(string template, params SafeFilenameItem[] items) =>
        SafeFilenameTemplate.Assign(template, items.ToImmutableArray());

    private static string ItemIdSuffix(string itemId) => "~" + Convert.ToHexString(
        SHA256.HashData(StrictUtf8.GetBytes(itemId))).ToLowerInvariant();

    [Fact]
    public void ExactUtf8NameBudgetIncludesFullSha256ItemIdSuffix()
    {
        // 43 four-byte emoji (172) + é (2) + x (1) leave exactly 65 bytes for the suffix.
        string prefix = string.Concat(Enumerable.Repeat("\U0001F600", 43)) + "éx";
        string itemId = "utf8-boundary";
        Assert.Equal(175, StrictUtf8.GetByteCount(prefix));

        var result = Run("{source}", Item(itemId, prefix));
        string expected = prefix + ItemIdSuffix(itemId);

        Assert.True(result.Success);
        Assert.Equal(expected, result.Inventory[itemId]);
        Assert.Equal(SafeFilenameTemplate.MaxNameBytes, StrictUtf8.GetByteCount(result.Inventory[itemId]));
    }

    [Fact]
    public void MaximumBatchKeepsOrdinalAssignmentAcrossPermutations()
    {
        string[] ids = Enumerable.Range(0, SafeFilenameTemplate.MaxItems)
            .Select(i => i.ToString(CultureInfo.InvariantCulture))
            .ToArray();
        SafeFilenameItem[] items = ids.Select(id => Item(id)).ToArray();
        SafeFilenameItem[] reverse = items.Reverse().ToArray();
        SafeFilenameItem[] interleaved = Enumerable.Range(0, ids.Length)
            .Select(i => items[(i * 37) % items.Length])
            .ToArray();

        var forward = Run("same.jpg", items);
        var reversed = Run("same.jpg", reverse);
        var permuted = Run("same.jpg", interleaved);
        string[] ordinalKeys = ids.OrderBy(id => id, StringComparer.Ordinal).ToArray();

        Assert.True(forward.Success);
        Assert.Equal(SafeFilenameTemplate.MaxItems, forward.Inventory.Count);
        Assert.Equal(forward.Inventory.ToArray(), reversed.Inventory.ToArray());
        Assert.Equal(forward.Inventory.ToArray(), permuted.Inventory.ToArray());
        Assert.Equal(ordinalKeys, forward.Inventory.Keys.ToArray());

        foreach (string id in ids)
            Assert.Equal("same" + ItemIdSuffix(id) + ".jpg", forward.Inventory[id]);
        Assert.Equal(ids.Length, forward.Inventory.Values.Distinct(StringComparer.Ordinal).Count());

        var tooMany = Run("same.jpg", items.Append(Item("overflow")).ToArray());
        Assert.Equal(SafeFilenameError.TooManyItems, tooMany.Error);
        Assert.Empty(tooMany.Inventory);
    }

    [Fact]
    public void ExactTemplateAndItemIdUtf8ByteLimitsAreAccepted()
    {
        Assert.True(Run(new string('x', SafeFilenameTemplate.MaxTemplateBytes)).Success);
        Assert.True(Run(new string('é', SafeFilenameTemplate.MaxTemplateBytes / 2)).Success);

        string asciiId = new('x', SafeFilenameTemplate.MaxTemplateBytes);
        string supplementaryId = string.Concat(Enumerable.Repeat(
            "\U0001F600", SafeFilenameTemplate.MaxTemplateBytes / 4));
        foreach (string itemId in new[] { asciiId, supplementaryId })
        {
            var result = Run("item", Item(itemId));
            string expected = "item" + ItemIdSuffix(itemId);

            Assert.True(result.Success);
            Assert.Equal(expected, result.Inventory[itemId]);
        }
    }

    [Fact]
    public void LateOutputOverflowReturnsNoPartialInventory()
    {
        const string earlierId = "0-earlier-ordinal-sentinel";
        const string lateId = "z-late-output-sentinel";
        const string earlierSource = "earlier-output-sentinel";
        var result = Run("{source}",
            Item(earlierId, earlierSource),
            Item(lateId, new string('x', 176)));

        Assert.Equal(SafeFilenameError.OutputTooLarge, result.Error);
        Assert.Empty(result.Inventory);
        Assert.False(result.Inventory.ContainsKey(earlierId));
        Assert.False(result.Inventory.ContainsKey(lateId));
        Assert.Empty(result.Inventory.Values);
        Assert.DoesNotContain(earlierSource, string.Concat(result.Inventory.Values),
            StringComparison.Ordinal);
    }
}
