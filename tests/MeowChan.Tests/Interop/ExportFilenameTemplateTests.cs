using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MeowChan.Interop;

namespace MeowChan.Tests.Interop;

public sealed class ExportFilenameTemplateTests
{
    private static readonly string Hash = new('a', 64);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AnEmptyTemplateIsOffAndNeverRenders(string? template)
    {
        ExportFilenamePreview preview = ExportFilenameTemplate.Preview(template);
        Assert.True(preview.Check.Ok);
        Assert.Empty(preview.Lines);
        Assert.False(ExportFilenameTemplate.IsActive(template));
        Assert.Null(ExportFilenameTemplate.TryRender(template, "id", "src", "1", "original", Hash, "jpg"));
    }

    [Fact]
    public void AValidTemplateShowsThreeSamplesEachWithAStableIdentifierBeforeTheExtension()
    {
        ExportFilenamePreview preview = ExportFilenameTemplate.Preview("{source}_{post}.{ext}");
        Assert.True(preview.Check.Ok);
        Assert.Equal(new[] { "photo.jpg", "IMG_0042.png", "clip.webm" }, preview.Lines.Select(line => line.Original).ToArray());
        Assert.StartsWith("4chan_123456789~", preview.Lines[0].Result);
        Assert.EndsWith(".jpg", preview.Lines[0].Result);
        Assert.StartsWith("danbooru_7654321~", preview.Lines[1].Result);
        Assert.EndsWith(".webm", preview.Lines[2].Result);
        Assert.Equal(3, preview.Lines.Select(line => line.Result).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData("{bogus}.{ext}", ExportFilenameTemplateProblem.InvalidTemplate)]
    [InlineData("{source.{ext}", ExportFilenameTemplateProblem.InvalidTemplate)]
    [InlineData("}{ext}", ExportFilenameTemplateProblem.InvalidTemplate)]
    [InlineData("{source}", ExportFilenameTemplateProblem.MustEndWithExtension)]
    [InlineData("{source}.jpg", ExportFilenameTemplateProblem.MustEndWithExtension)]
    [InlineData("../{source}.{ext}", ExportFilenameTemplateProblem.UnsafeName)]
    [InlineData("a/b.{ext}", ExportFilenameTemplateProblem.UnsafeName)]
    [InlineData("a:b.{ext}", ExportFilenameTemplateProblem.UnsafeName)]
    [InlineData("CON.{ext}", ExportFilenameTemplateProblem.UnsafeName)]
    [InlineData("{hash}_{hash}.{ext}", ExportFilenameTemplateProblem.NameTooLong)]
    public void ProblemsAreNamedForTheInlineMessage(string template, ExportFilenameTemplateProblem expected)
    {
        ExportFilenameTemplateCheck check = ExportFilenameTemplate.Check(template);
        Assert.Equal(expected, check.Problem);
        Assert.False(check.Ok);
        Assert.Null(ExportFilenameTemplate.TryRender(template, "id", "src", "1", "original", Hash, "jpg"));
    }

    [Fact]
    public void ATemplateLongerThanTheCeilingIsRefused()
    {
        string template = new string('a', ExportFilenameTemplate.MaxTemplateLength) + ".{ext}";
        Assert.Equal(ExportFilenameTemplateProblem.TooLong, ExportFilenameTemplate.Check(template).Problem);
        string atCeiling = new string('a', ExportFilenameTemplate.MaxTemplateLength - ExportFilenameTemplate.ExtensionEnding.Length) + ExportFilenameTemplate.ExtensionEnding;
        Assert.Equal(ExportFilenameTemplate.MaxTemplateLength, atCeiling.Length);
        Assert.True(ExportFilenameTemplate.Check(atCeiling).Ok);
    }

    [Fact]
    public void RenderingUsesTheItemIdentityForTheSuffixAndLowercasesTheHash()
    {
        string upper = new('A', 64);
        string name = ExportFilenameTemplate.TryRender("{role}-{hash}.{ext}", "identity-1", "src", "1", "original", upper, "png")!;
        string suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("identity-1"))).ToLowerInvariant();
        Assert.Equal("original-" + new string('a', 64) + "~" + suffix + ".png", name);
        Assert.Equal(name, ExportFilenameTemplate.TryRender("{role}-{hash}.{ext}", "identity-1", "other", "2", "original", upper, "png"));
        Assert.NotEqual(name, ExportFilenameTemplate.TryRender("{role}-{hash}.{ext}", "identity-2", "src", "1", "original", upper, "png"));
    }

    [Fact]
    public void ValuesThatAreNotSafeNameParts_FallBackInsteadOfThrowing()
    {
        Assert.Null(ExportFilenameTemplate.TryRender("{source}.{ext}", "id", "CON", "1", "original", Hash, "jpg"));
        Assert.Null(ExportFilenameTemplate.TryRender("{source}.{ext}", "id", "src", "1", "original", Hash, "j-g"));
        Assert.Null(ExportFilenameTemplate.TryRender("{source}.{ext}", "id", "src", "1", "original", "short", "jpg"));
    }
}
