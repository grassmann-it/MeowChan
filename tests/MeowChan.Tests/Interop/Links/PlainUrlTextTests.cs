using System.Text;
using MeowChan.Interop.Links;

namespace MeowChan.Tests.Interop.Links;

public sealed class PlainUrlTextTests
{
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
    // Exact synthetic fixture attestation, deliberately not a production host/query allowlist.
    private static PlainUrlText For(params string[] publicFixtures) =>
        new(value => publicFixtures.Contains(value, StringComparer.Ordinal));

    [Fact]
    public void PreservesOriginalCommasUnicodeFragmentsAndQueries()
    {
        const string url = "https://example.test/猫,a?public=一,二#見出し";
        PlainUrlText core = For(url);
        LinkTextResult parsed = core.Parse(Bytes("\uFEFF" + url + "\r\n"));
        PassiveLink link = Assert.Single(parsed.Links);
        Assert.Equal(url, link.Text);
        Assert.Equal(PassiveLinkKind.Unknown, link.Kind);
        Assert.True(link.RequiresExplicitConfirmation);
        Assert.Empty(parsed.Issues);
        Assert.Equal(url + "\n", Encoding.UTF8.GetString(core.Export(Bytes(url)).Utf8));
    }

    [Fact]
    public void FirstExactDuplicateWinsWithoutUriNormalization()
    {
        const string a = "https://example.test/a";
        const string b = "https://example.test/%61";
        LinkTextResult result = For(a, b).Parse(Bytes(a + "\n" + a + "\r" + b));
        Assert.Equal(new[] { a, b }, result.Links.Select(x => x.Text));
        Assert.Equal(new[] { 1, 3 }, result.Links.Select(x => x.Line));
        Assert.Equal(new LinkIssue(2, LinkIssueCode.Duplicate), Assert.Single(result.Issues));
    }

    [Theory]
    [InlineData("file:///tmp/private")]
    [InlineData("javascript:alert(1)")]
    [InlineData("# comment")]
    [InlineData("autoStart=true")]
    [InlineData(" https://example.test/")]
    [InlineData("https://example.test/ a")]
    [InlineData("https://example.test/%0aAuthorization")]
    [InlineData("https://example.test/%")]
    [InlineData("https://example.test/\\x")]
    [InlineData("https://example.test/\tsecret")]
    public void InvalidInputNeverReachesPolicy(string value)
    {
        int calls = 0;
        PlainUrlText core = new(_ => { calls++; return true; });
        LinkTextResult result = core.Parse(Bytes(value));
        Assert.Empty(result.Links);
        Assert.Equal(LinkIssueCode.InvalidUrl, Assert.Single(result.Issues).Code);
        Assert.Equal(0, calls);
        Assert.Empty(core.Export(Bytes(value)).Utf8);
    }

    [Theory]
    [InlineData("https://user:SECRET@example.test/a", LinkIssueCode.Credentials)]
    [InlineData("https://@example.test/a", LinkIssueCode.Credentials)]
    [InlineData("https://example.test/a?token=SECRET", LinkIssueCode.SafetyUnknown)]
    [InlineData("https://example.test/a?X-Amz-Signature=SECRET", LinkIssueCode.SafetyUnknown)]
    [InlineData("https://example.test/a?opaque=SECRET", LinkIssueCode.SafetyUnknown)]
    [InlineData("https://example.test/private/SECRET", LinkIssueCode.SafetyUnknown)]
    [InlineData("https://example.test/a#SECRET", LinkIssueCode.SafetyUnknown)]
    [InlineData("http://127.0.0.1/private", LinkIssueCode.SafetyUnknown)]
    public void DefaultFailsClosedAndNeverReturnsRejectedText(string input, object code)
    {
        PlainUrlText core = new();
        LinkTextResult result = core.Parse(Bytes(input));
        Assert.Empty(result.Links);
        Assert.Equal(new LinkIssue(1, (LinkIssueCode)code), Assert.Single(result.Issues));
        Assert.DoesNotContain("SECRET", result.ToString());
        Assert.DoesNotContain(input, result.ToString());
        Assert.Empty(core.Export(Bytes(input)).Utf8);
    }

    [Fact]
    public void DoesNotStripQueryAndInventPublicIdentity()
    {
        const string publicUrl = "https://example.test/post/1";
        LinkTextExport result = For(publicUrl).Export(Bytes(publicUrl + "?opaque=SECRET\n" + publicUrl));
        Assert.Equal(publicUrl + "\n", Encoding.UTF8.GetString(result.Utf8));
        Assert.Equal(new LinkIssue(1, LinkIssueCode.SafetyUnknown), Assert.Single(result.Issues));
    }

    [Fact]
    public void CredentialsCannotBeOverriddenByAttestation()
    {
        const string input = "https://user:SECRET@example.test/a";
        Assert.Empty(For(input).Parse(Bytes(input)).Links);
    }

    [Fact]
    public void ClassificationIsSeparateAndExceptionsAreSanitized()
    {
        const string input = "https://example.test/a";
        PlainUrlText core = new(_ => true, _ => throw new Exception("SECRET"));
        LinkTextResult result = core.Parse(Bytes(input));
        Assert.Equal(PassiveLinkKind.Unknown, Assert.Single(result.Links).Kind);
        Assert.Equal(LinkIssueCode.ClassificationUnavailable, Assert.Single(result.Issues).Code);
        Assert.DoesNotContain("SECRET", result.ToString());
        PlainUrlText unsafeCore = new(_ => throw new Exception("SECRET"), _ => throw new Exception());
        Assert.Equal(LinkIssueCode.SafetyUnknown, Assert.Single(unsafeCore.Parse(Bytes(input)).Issues).Code);
        Assert.Empty(unsafeCore.Export(Bytes(input)).Utf8);
    }

    [Fact]
    public void BudgetsAndStrictUtf8FailWithoutPartialSuccess()
    {
        PlainUrlText core = new(_ => true);
        Assert.Equal(LinkIssueCode.ByteLimit, Assert.Single(core.Parse(new byte[PlainUrlText.MaxBytes + 1]).Issues).Code);
        foreach (byte[] invalid in new[] { new byte[] { 0xc0, 0xaf }, new byte[] { 0xed, 0xa0, 0x80 }, new byte[] { 0xff } })
        {
            Assert.Empty(core.Parse(invalid).Links);
            Assert.Equal(LinkIssueCode.InvalidUtf8, Assert.Single(core.Parse(invalid).Issues).Code);
        }
        string many = string.Concat(Enumerable.Repeat("https://example.test/a\n", PlainUrlText.MaxLines + 1));
        Assert.Empty(core.Parse(Bytes(many)).Links);
        Assert.Equal(LinkIssueCode.LineLimit, Assert.Single(core.Parse(Bytes(many)).Issues).Code);
        Assert.Equal(LinkIssueCode.LineLength, Assert.Single(core.Parse(Bytes("https://example.test/" + new string('a', PlainUrlText.MaxLineCharacters))).Issues).Code);
    }

    [Fact]
    public void EmptyInteriorLineIsNotSilentlyIgnored()
    {
        const string url = "https://example.test/a";
        LinkTextResult result = For(url).Parse(Bytes(url + "\n\n"));
        Assert.Single(result.Links);
        Assert.Equal(new LinkIssue(2, LinkIssueCode.InvalidUrl), Assert.Single(result.Issues));
        Assert.Empty(For(url).Parse(Array.Empty<byte>()).Issues);
    }
}
