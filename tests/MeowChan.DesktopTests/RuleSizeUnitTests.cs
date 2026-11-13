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
    [InlineData(10_000_000L, "9.536743", "9,536743")]
    public void ExistingByteLimitsAreShownInMebibibytesInTheLanguageFormat(long bytes, string english, string german)
    {
        Assert.Equal(english, Format(bytes, En));
        Assert.Equal(german, Format(bytes, De));
    }

    [Fact]
    public void OpeningAndSavingARuleNeverMovesItsLimitByAByte()
    {
        for (long bytes = 0; bytes < 4_194_304; bytes += 7_919)
            AssertRoundTrip(bytes);
        foreach (long bytes in new[] { 1L, 2L, 999_999L, 1_000_000L, 2_000_000L, 50_000_000L, 1_999_999_999L, 4_294_967_296L, 1L << 40, 10_000_000_000_001L })
            AssertRoundTrip(bytes);
    }

    private static void AssertRoundTrip(long bytes)
    {
        string text = Format(bytes, En);
        Assert.True(TryRead(text, En, out long back), text);
        Assert.Equal(bytes, back);
        Assert.True(TryRead(Format(bytes, De), De, out long backGerman));
        Assert.Equal(bytes, backGerman);
    }
}
