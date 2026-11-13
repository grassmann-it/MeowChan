using System.Globalization;
using System.Reflection;

namespace MeowChan.DesktopTests;

/// <summary>The Rules size limit is typed in MiB (1 MiB = 1,048,576 bytes), not in decimal megabytes, and never moves by a byte.</summary>
public sealed class RuleSizeUnitTests
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en");
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de");

    private static bool TryRead(string text, CultureInfo culture, out long bytes)
    {
        object?[] args = { text, culture, 0L };
        bool ok = (bool)typeof(RulesPage).GetMethod("TryReadMegabytes", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args)!;
        bytes = (long)args[2]!;
        return ok;
    }

    private static string Format(long bytes, CultureInfo culture) =>
        (string)typeof(RulesPage).GetMethod("FormatMegabytes", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { bytes, culture })!;

    [Theory]
    [InlineData("1", 1_048_576L)]
    [InlineData("10", 10_485_760L)]
    [InlineData("1.5", 1_572_864L)]
    [InlineData("0", 0L)]
    [InlineData("0.000001", 1L)]
    [InlineData("2.3", 2_411_725L)]
    public void InputIsMebibytesRoundedToWholeBytes(string text, long expected)
    {
        Assert.True(TryRead(text, En, out long bytes));
        Assert.Equal(expected, bytes);
    }

    [Fact]
    public void InputFollowsTheDecimalSeparatorOfTheLanguageAndRejectsNegativeValues()
    {
        Assert.True(TryRead("1,5", De, out long bytes));
        Assert.Equal(1_572_864L, bytes);
        Assert.False(TryRead("-1", En, out _));
        Assert.False(TryRead("not a number", En, out _));
    }

    [Theory]
    [InlineData(1_048_576L, "1", "1")]
    [InlineData(1_572_864L, "1.5", "1,5")]
    [InlineData(10_000_000L, "9.537", "9,537")]
    [InlineData(11_000_000L, "10.49", "10,49")]
    [InlineData(1L, "0.000001", "0,000001")]
    [InlineData(524L, "0.0005", "0,0005")]
    [InlineData(525L, "0.001", "0,001")]
    [InlineData(1_050_000L, "1.001", "1,001")]
    public void ExistingByteLimitsAreShownInMebibibytesInTheLanguageFormat(long bytes, string english, string german)
    {
        Assert.Equal(english, Format(bytes, En));
        Assert.Equal(german, Format(bytes, De));
    }

    [Fact]
    public void TheShownMebibytesHaveAtMostThreeDecimalsUnlessTheLimitIsTiny()
    {
        for (long bytes = 525; bytes < 8_000_000; bytes += 4_999)
        {
            string text = Format(bytes, En);
            Assert.True(!text.Contains('.') || text.Split('.')[1].Length <= 3, text);
        }
        Assert.Equal("0.000001", Format(1, En)); // A tiny limit never reads as 0, which would mean empty files only.
    }

    [Fact]
    public void OpeningAndSavingARuleNeverMovesItsLimitByAByte()
    {
        for (long bytes = 0; bytes < 4_194_304; bytes += 7_919)
            AssertRoundTrip(bytes);
        foreach (long bytes in new[] { 1L, 2L, 999_999L, 1_000_000L, 2_000_000L, 11_000_000L, 50_000_000L, 1_999_999_999L, 4_294_967_296L, 1L << 40, 10_000_000_000_001L })
            AssertRoundTrip(bytes);
    }

    private static bool TryReadExact(object? exact, string text, CultureInfo culture, out long bytes)
    {
        object?[] args = { exact, text, culture, 0L };
        bool ok = (bool)typeof(RulesPage).GetNestedType("ExactMebibytes", BindingFlags.NonPublic)!.GetMethod("TryRead", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args)!;
        bytes = (long)args[3]!;
        return ok;
    }

    private static object Exact(long bytes, CultureInfo culture) =>
        typeof(RulesPage).GetNestedType("ExactMebibytes", BindingFlags.NonPublic)!.GetMethod("Of", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { bytes, culture })!;

    // The display is rounded; the byte count survives because an untouched field keeps its exact value.
    private static void AssertRoundTrip(long bytes)
    {
        foreach (CultureInfo culture in new[] { En, De })
        {
            object exact = Exact(bytes, culture);
            string text = Format(bytes, culture);
            Assert.True(TryReadExact(exact, text, culture, out long back), text);
            Assert.Equal(bytes, back);
        }
    }

    [Fact]
    public void AnEditedFieldIsReadFromItsTextAndAFieldInAnotherNumberFormatIsNot()
    {
        object exact = Exact(11_000_000L, En);
        Assert.True(TryReadExact(exact, "10.49", En, out long unedited));
        Assert.Equal(11_000_000L, unedited);
        Assert.True(TryReadExact(exact, "10.5", En, out long edited));
        Assert.Equal(11_010_048L, edited);
        // The same text in another number format is a different number: no exact shortcut.
        Assert.True(TryReadExact(exact, "10,49", De, out long german));
        Assert.Equal(10_999_562L, german);
    }
}
