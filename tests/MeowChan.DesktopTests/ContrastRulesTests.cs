using MeowChan.Desktop.Theming;

namespace MeowChan.DesktopTests;

// Pure arithmetic and contract tests; no Avalonia application, decoder or external palette files.
public sealed class ContrastRulesTests
{
    private static ContrastPalette Palette(params (string Key, string Color)[] colors) =>
        new(ContrastTheme.Dark, colors.ToDictionary(color => color.Key, color => color.Color, StringComparer.Ordinal));

    [Theory]
    [InlineData("#000000", 0.0)]
    [InlineData("#FFFFFF", 1.0)]
    [InlineData("#ff0000", 0.2126)]
    [InlineData("#00FF00", 0.7152)]
    [InlineData("#0000FF", 0.0722)]
    public void LuminanceHasTheOriginalChannelWeights(string hex, double expected) =>
        Assert.Equal(expected, ContrastRules.Luminance(hex), 14);

    [Fact]
    public void SrgbBreakpointKeepsItsLinearAndPowerBranches()
    {
        double low = 10 / 255d, high = 11 / 255d;
        Assert.Equal(low / 12.92, ContrastRules.Luminance("#0A0A0A"), 14);
        Assert.Equal(Math.Pow((high + 0.055) / 1.055, 2.4), ContrastRules.Luminance("#0B0B0B"), 14);
    }

    [Fact]
    public void ContrastIsSymmetricWithOneAndTwentyOneAsEndpoints()
    {
        Assert.Equal(1, ContrastRules.Contrast("#123456", "#123456"));
        Assert.Equal(21, ContrastRules.Contrast("#FFFFFF", "#000000"));
        Assert.Equal(ContrastRules.Contrast("#123456", "#ABCDEF"), ContrastRules.Contrast("#ABCDEF", "#123456"));
        Assert.Equal(0, ContrastRules.DeltaE("#123456", "#123456"));
        Assert.Equal(100, ContrastRules.DeltaE("#FFFFFF", "#000000"), 4);
    }

    [Fact]
    public void ExactMinimumPassesButTheNextRepresentableMinimumFails()
    {
        var palette = Palette(("fg", "#777777"), ("bg", "#FFFFFF"));
        double ratio = ContrastRules.Contrast("#777777", "#FFFFFF");
        Assert.Empty(ContrastRules.Check(palette, new[] { new ContrastRule("fg", "bg", ratio) }));
        var rule = new ContrastRule("fg", "bg", Math.BitIncrement(ratio));
        ContrastFailure failure = Assert.Single(ContrastRules.Check(palette, new[] { rule }));
        Assert.Equal(rule, failure.Rule);
        Assert.Equal(ContrastFailureKind.InsufficientContrast, failure.Kind);
        Assert.Equal(ratio, failure.Ratio);
        Assert.Null(failure.Key);
        // This ratio rounds to 4.5 at one decimal, but is still below the text contract.
        Assert.True(ratio < 4.5);
        Assert.NotEmpty(ContrastRules.Check(palette, new[] { new ContrastRule("fg", "bg", 4.5) }));
        Assert.Empty(ContrastRules.Check(palette, new[] { new ContrastRule("fg", "bg", 3.0) }));
    }

    [Fact]
    public void EmptyPaletteAndMissingBackdropAreNotSilentlySkipped()
    {
        ContrastFailure[] missing = ContrastRules.Check(Palette(), new[] { new ContrastRule("fg", "bg", 3) }).ToArray();
        Assert.Equal(new[] { "fg", "bg" }, missing.Select(failure => failure.Key));
        Assert.All(missing, failure =>
        {
            Assert.Equal(ContrastFailureKind.MissingColor, failure.Kind);
            Assert.Null(failure.Ratio);
        });
        ContrastFailure backdrop = Assert.Single(ContrastRules.Check(Palette(("fg", "#FFFFFF"), ("bg", "#80000000")),
            new[] { new ContrastRule("fg", "bg", 3, "under") }));
        Assert.Equal("under", backdrop.Key);
        Assert.Equal(ContrastFailureKind.MissingColor, backdrop.Kind);
        Assert.NotEmpty(ContrastRules.Check(Palette()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("#FFF")]
    [InlineData("FFFFFF")]
    [InlineData("#GG0000")]
    [InlineData(" #FFFFFF")]
    [InlineData("#FFFFFF ")]
    [InlineData("#80FFFFFF")]
    [InlineData(null)]
    public void InvalidOrUncompositedAlphaColoursFailClosed(string? invalid)
    {
        ContrastFailure failure = Assert.Single(ContrastRules.Check(Palette(("fg", invalid!), ("bg", "#000000")),
            new[] { new ContrastRule("fg", "bg", 3) }));
        Assert.Equal(ContrastFailureKind.InvalidColor, failure.Kind);
        Assert.Equal("fg", failure.Key);
        Assert.Null(failure.Ratio);
        Assert.Throws<ArgumentException>(() => ContrastRules.Luminance(invalid!));
    }

    [Fact]
    public void AProblematicPairDoesNotStopOtherFailuresFromBeingReported()
    {
        var rules = new[] { new ContrastRule("missing", "bg", 3), new ContrastRule("bad", "bg", 3), new ContrastRule("fg", "bg", 4.5) };
        var palette = Palette(("bad", "not a colour"), ("fg", "#FFFFFF"), ("bg", "#FFFFFF"));
        Assert.Equal(new[] { ContrastFailureKind.MissingColor, ContrastFailureKind.InvalidColor, ContrastFailureKind.InsufficientContrast },
            ContrastRules.Check(palette, rules).Select(failure => failure.Kind));
    }

    [Theory]
    [InlineData(0, "#000103")]
    [InlineData(1, "#010204")]
    [InlineData(0.5, "#000204")]
    public void BlendPreservesSrgbChannelRoundingToEven(double opacity, string expected) =>
        Assert.Equal(expected, ContrastRules.Blend("#010204", "#000103", opacity));

    [Theory]
    [InlineData("#00000000", "#FFFFFF")]
    [InlineData("#FF000000", "#000000")]
    [InlineData("#80000000", "#7F7F7F")]
    [InlineData("#40000000", "#BFBFBF")]
    [InlineData("#80FF0000", "#FF7F7F")]
    [InlineData("#123456", "#123456")]
    public void EncodedAlphaIsExplicitlyCompositedInSrgb(string scrim, string expected)
    {
        Assert.Equal(expected, ContrastRules.CompositeOver(scrim, "#FFFFFF"));
        var palette = Palette(("fg", "#FFFFFF"), ("scrim", scrim), ("under", "#FFFFFF"));
        double ratio = ContrastRules.Contrast("#FFFFFF", expected);
        Assert.Empty(ContrastRules.Check(palette, new[] { new ContrastRule("fg", "scrim", ratio, "under") }));
        if (ratio < 21)
        {
            ContrastFailure failure = Assert.Single(ContrastRules.Check(palette,
                new[] { new ContrastRule("fg", "scrim", Math.BitIncrement(ratio), "under") }));
            Assert.Equal(ratio, failure.Ratio);
        }
    }

    [Theory]
    [InlineData("scrim", "#GG000000")]
    [InlineData("under", "#80000000")]
    public void MalformedScrimAndNonOpaqueBackdropFailClosed(string key, string value)
    {
        var colors = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["fg"] = "#FFFFFF", ["scrim"] = "#80000000", ["under"] = "#FFFFFF"
        };
        colors[key] = value;
        ContrastFailure failure = Assert.Single(ContrastRules.Check(new(ContrastTheme.Dark, colors),
            new[] { new ContrastRule("fg", "scrim", 3.0, "under") }));
        Assert.Equal(ContrastFailureKind.InvalidColor, failure.Kind);
        Assert.Equal(key, failure.Key);
        Assert.Null(failure.Ratio);
    }

    [Fact]
    public void GroupOpacityBlendsBothColoursAgainstTheSameUnderlay()
    {
        double expected = ContrastRules.Contrast("#2E2E2E", "#DADADA");
        Assert.Equal(expected, ContrastRules.CompositeContrast("#1A1A1A", "#FFFFFF", 0.75, "#696969"));
        double nested = ContrastRules.Contrast(ContrastRules.Blend("#1A1A1A", "#DADADA", 0.75), "#DADADA");
        Assert.NotEqual(nested, expected);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidOpacityCannotProduceAPassingRatio(double opacity) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ContrastRules.Blend("#000000", "#FFFFFF", opacity));

    [Theory]
    [InlineData(0.99)]
    [InlineData(21.01)]
    [InlineData(double.NaN)]
    public void InvalidMinimumIsAProgrammingError(double minimum) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ContrastRules.Check(Palette(("fg", "#000000"), ("bg", "#FFFFFF")),
            new[] { new ContrastRule("fg", "bg", minimum) }));

    [Fact]
    public void RulesKeepTheirThemeSpecificMinimaAndGraphicVsTextContracts()
    {
        foreach (ContrastTheme theme in Enum.GetValues<ContrastTheme>())
        {
            var rules = ContrastRules.For(theme);
            Assert.Equal(rules.Count, rules.Distinct().Count());
            Assert.Contains(new ContrastRule("Workspace.OnAccent", "Workspace.Accent", 4.5), rules);
            Assert.Contains(new ContrastRule("Workspace.AccentHover", "Workspace.AccentSurface", 3.0), rules);
            Assert.DoesNotContain(rules, rule => rule.Foreground == "Workspace.Muted" && rule.Background == "Workspace.ReaderSpoiler");
            Assert.Contains(new ContrastRule("CheckBoxGlyphFill", "CheckBoxCheckedPressedBackground", 4.5), rules);
            Assert.Contains(new ContrastRule("Workspace.Progress", "Workspace.ProgressTrack", theme == ContrastTheme.Dark ? 3.5 : 3.4), rules);
            Assert.Contains(new ContrastRule("Workspace.Panel", "Workspace.Scrim", theme == ContrastTheme.Dark ? 1.1 : 3.0, "Workspace.Background"), rules);
        }
        Assert.Contains(new ContrastRule("Workspace.ProgressTrack", "Workspace.Hover", 1.3), ContrastRules.For(ContrastTheme.Dark));
        Assert.Contains(new ContrastRule("Workspace.ProgressTrack", "Workspace.Hover", 2.4), ContrastRules.For(ContrastTheme.Light));
        Assert.Contains(new ContrastRule("Workspace.Text", "Workspace.Hover", 10.7), ContrastRules.For(ContrastTheme.Light));
        Assert.DoesNotContain(ContrastRules.For(ContrastTheme.Dark), rule => rule.Minimum == 10.7);
        Assert.Throws<ArgumentOutOfRangeException>(() => ContrastRules.For((ContrastTheme)42));
    }
}
