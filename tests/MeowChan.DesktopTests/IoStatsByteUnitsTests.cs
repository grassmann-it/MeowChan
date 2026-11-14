using System.Globalization;
using MeowChan.Desktop.Stats;

namespace MeowChan.DesktopTests;

public sealed class IoStatsByteUnitsTests
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en");
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de");

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1L, "1 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1 KiB")]
    [InlineData(1536L, "2 KiB")]
    [InlineData(1048063L, "1023 KiB")]
    [InlineData(1048064L, "1.0 MiB")]   // 1023.5 KiB rounds up: carried, never "1024 KiB"
    [InlineData(1048576L, "1.0 MiB")]
    [InlineData(2516582L, "2.4 MiB")]
    [InlineData(1073741823L, "1.0 GiB")] // 1023.99.. MiB carries into GiB
    [InlineData(1073741824L, "1.0 GiB")]
    [InlineData(1288490189L, "1.2 GiB")]
    [InlineData(1099511627776L, "1.0 TiB")]
    [InlineData(5L * 1099511627776L, "5.0 TiB")]
    [InlineData(5000L * 1099511627776L, "5000.0 TiB")] // TiB is the last unit: overflow stays there
    public void SizeUsesBinaryUnitsAndCarriesAtTheBoundary(long bytes, string expected) =>
        Assert.Equal(expected, ByteUnits.Size(bytes, En));

    [Fact]
    public void GermanUsesTheDecimalCommaAndTheSameUnits()
    {
        Assert.Equal("2,4 MiB", ByteUnits.Size(2516582, De));
        Assert.Equal("1,2 GiB", ByteUnits.Size(1288490189, De));
        Assert.Equal("1023 B", ByteUnits.Size(1023, De));
    }

    [Theory]
    [InlineData(0d, "0 B/s")]
    [InlineData(0.4d, "0 B/s")]
    [InlineData(0.5d, "1 B/s")]
    [InlineData(999.49d, "999 B/s")]
    [InlineData(999.5d, "0.98 KiB/s")]
    [InlineData(1000d, "0.98 KiB/s")]
    [InlineData(1023d, "1 KiB/s")]
    [InlineData(1023.4d, "1 KiB/s")]
    [InlineData(1023.6d, "1 KiB/s")]
    [InlineData(1024d, "1 KiB/s")]
    [InlineData(65536d, "64 KiB/s")]
    [InlineData(127d * 1024, "127 KiB/s")]
    [InlineData(1.27d * 1048576, "1.27 MiB/s")]
    [InlineData(12.7d * 1048576, "12.7 MiB/s")]
    [InlineData(2516582.4d, "2.4 MiB/s")]
    [InlineData(80d * 1048576, "80 MiB/s")]
    public void RateUsesAtMostThreeVisibleDigitsWithoutTrailingZeros(double rate, string expected) =>
        Assert.Equal(expected, ByteUnits.Rate(rate, En));

    [Theory]
    [InlineData(9.9949d, "9.99 KiB/s")]
    [InlineData(9.995d, "10 KiB/s")]
    [InlineData(10d, "10 KiB/s")]
    [InlineData(99.9499d, "99.9 KiB/s")]
    [InlineData(99.95d, "100 KiB/s")]
    [InlineData(100d, "100 KiB/s")]
    [InlineData(999.4999d, "999 KiB/s")]
    [InlineData(999.5d, "0.98 MiB/s")]
    [InlineData(1000d, "0.98 MiB/s")]
    [InlineData(1023d, "1 MiB/s")]
    [InlineData(1024d, "1 MiB/s")]
    public void RateCarriesAtPrecisionAndUnitBoundaries(double kibibytesPerSecond, string expected) =>
        Assert.Equal(expected, ByteUnits.Rate(kibibytesPerSecond * 1024, En));

    [Fact]
    public void AllRateUnitsShareTheSamePrecisionAndBinaryCarry()
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB", "ZiB", "YiB"];
        double factor = 1;
        for (int unit = 0; unit < units.Length; unit++, factor *= 1024)
        {
            Assert.Equal("999 " + units[unit] + "/s", ByteUnits.Rate(999.49 * factor, En));
            if (unit > 0)
            {
                Assert.Equal("1.27 " + units[unit] + "/s", ByteUnits.Rate(1.27 * factor, En));
                Assert.Equal("9.99 " + units[unit] + "/s", ByteUnits.Rate(9.9949 * factor, En));
                Assert.Equal("10 " + units[unit] + "/s", ByteUnits.Rate(9.995 * factor, En));
                Assert.Equal("99.9 " + units[unit] + "/s", ByteUnits.Rate(99.9499 * factor, En));
                Assert.Equal("100 " + units[unit] + "/s", ByteUnits.Rate(99.95 * factor, En));
            }
            string carried = unit + 1 < units.Length ? "0.98 " + units[unit + 1] + "/s" : ">999 YiB/s";
            Assert.Equal(carried, ByteUnits.Rate(999.5 * factor, En));
            Assert.Equal(carried, ByteUnits.Rate(1000 * factor, En));
        }
    }

    [Fact]
    public void ExplicitCultureUsesDecimalSeparatorsWithoutGrouping()
    {
        Assert.Equal("1,27 MiB/s", ByteUnits.Rate(1.27 * 1048576, De));
        Assert.Equal("12,7 MiB/s", ByteUnits.Rate(12.7 * 1048576, De));
        Assert.Equal("127 KiB/s", ByteUnits.Rate(127 * 1024, De));
        Assert.Equal("0,98 KiB/s", ByteUnits.Rate(1000, De));
    }

    [Theory]
    [InlineData(double.MaxValue)]
    [InlineData(1e100)]
    [InlineData(1e30)]
    public void FiniteOverflowIsExplicitAndBounded(double rate)
    {
        Assert.Equal(">999 YiB/s", ByteUnits.Rate(rate, En));
        Assert.Equal(">999 YiB/s", ByteUnits.Rate(rate, De));
    }

    [Fact]
    public void EveryFiniteExponentHasBoundedFixedPointTextWithAtMostThreeDigits()
    {
        for (int exponent = -1074; exponent <= 1023; exponent++)
        {
            double value = Math.ScaleB(1, exponent);
            foreach (CultureInfo culture in new[] { En, De })
            {
                string text = ByteUnits.Rate(value, culture);
                string number = text.Split(' ')[0];
                Assert.InRange(number.Count(char.IsDigit), 1, 3);
                Assert.InRange(text.Length, 5, 12);
                Assert.DoesNotContain("e", number, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(culture.NumberFormat.NumberGroupSeparator, number);
            }
        }
    }

    [Fact]
    public void NegativeAndNonFiniteValuesReadAsZero()
    {
        Assert.Equal("0 B", ByteUnits.Size(-5, En));
        Assert.Equal("0 B/s", ByteUnits.Rate(double.NaN, En));
        Assert.Equal("0 B/s", ByteUnits.Rate(double.PositiveInfinity, En));
        Assert.Equal("0 B/s", ByteUnits.Rate(double.NegativeInfinity, En));
        Assert.Equal("0 B/s", ByteUnits.Rate(double.MinValue, En));
        Assert.Equal("0 B/s", ByteUnits.Rate(-1, En));
    }
}
