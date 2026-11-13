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
    [InlineData(1023.4d, "1023 B/s")]
    [InlineData(1023.6d, "1 KiB/s")]
    [InlineData(65536d, "64 KiB/s")]
    [InlineData(2516582.4d, "2.4 MiB/s")]
    public void RateAddsPerSecond(double rate, string expected) => Assert.Equal(expected, ByteUnits.Rate(rate, En));

    [Fact]
    public void NegativeAndNonFiniteValuesReadAsZero()
    {
        Assert.Equal("0 B", ByteUnits.Size(-5, En));
        Assert.Equal("0 B/s", ByteUnits.Rate(double.NaN, En));
        Assert.Equal("0 B/s", ByteUnits.Rate(double.PositiveInfinity, En));
        Assert.Equal("0 B/s", ByteUnits.Rate(-1, En));
    }
}
