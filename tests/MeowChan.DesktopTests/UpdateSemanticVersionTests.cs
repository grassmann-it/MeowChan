using MeowChan.Desktop.Updates;

namespace MeowChan.DesktopTests;

public sealed class UpdateSemanticVersionTests
{
    [Fact]
    public void OfficialSemverPrereleaseOrderingIsRespected()
    {
        string[] versions = ["1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta",
            "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.0.1", "1.1.0", "2.0.0"];
        for (int index = 1; index < versions.Length; index++)
        {
            SemanticVersion older = SemanticVersion.Parse(versions[index - 1]);
            SemanticVersion newer = SemanticVersion.Parse(versions[index]);
            Assert.True(older.CompareTo(newer) < 0);
            Assert.True(newer.CompareTo(older) > 0);
        }
    }

    [Theory]
    [InlineData("v1.2.3+build.01", "1.2.3+other")]
    [InlineData("1.0.0-rc.1+build", "1.0.0-rc.1")]
    public void OptionalVAndBuildMetadataDoNotAffectPrecedenceOrHash(string left, string right)
    {
        SemanticVersion first = SemanticVersion.Parse(left);
        SemanticVersion second = SemanticVersion.Parse(right);
        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" 1.0.0")]
    [InlineData("1.0.0\n")]
    [InlineData("V1.0.0")]
    [InlineData("1.0")]
    [InlineData("01.0.0")]
    [InlineData("1.0.0-01")]
    [InlineData("1.0.0-alpha..1")]
    [InlineData("1.0.0+")]
    [InlineData("1.0.0-ä")]
    public void InvalidSemverIsRejected(string text) => Assert.False(SemanticVersion.TryParse(text, out _));

    [Fact]
    public void NumericIdentifiersDoNotOverflowAndUseNumericPrecedence()
    {
        Assert.True(SemanticVersion.Parse("999999999999999999999999999999999999.0.0")
            .CompareTo(SemanticVersion.Parse("99999999999999999999999999999999999.0.0")) > 0);
        Assert.True(SemanticVersion.Parse("1.0.0-999999999999999999999999999999999999")
            .CompareTo(SemanticVersion.Parse("1.0.0-99999999999999999999999999999999999")) > 0);
        Assert.True(SemanticVersion.Parse("1.0.0-10").CompareTo(SemanticVersion.Parse("1.0.0-2")) > 0);
        Assert.True(SemanticVersion.Parse("1.0.0-10").CompareTo(SemanticVersion.Parse("1.0.0-a")) < 0);
    }
}
