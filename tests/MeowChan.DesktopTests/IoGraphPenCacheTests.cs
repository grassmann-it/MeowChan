using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Stats;

namespace MeowChan.DesktopTests;

/// <summary>Exercises the exact pen lookup used by Render, without a renderer, bitmap or window.</summary>
[Collection("Theme logo")]
public sealed class IoGraphPenCacheTests
{
    private static IoGraph Graph() => new()
    {
        Stroke = new SolidColorBrush(Colors.Red),
        SecondaryStroke = new SolidColorBrush(Colors.Blue),
        GuideStroke = new SolidColorBrush(Colors.Gray)
    };

    [Fact]
    public void UnchangedPropertiesReuseEveryPenAndTheDashStyle()
    {
        IoGraph graph = Graph();
        var first = graph.RenderPens;
        for (int i = 0; i < 1000; i++) AssertSamePens(first, graph.RenderPens);
        graph.Stroke = graph.Stroke;
        graph.SecondaryStroke = graph.SecondaryStroke;
        graph.GuideStroke = graph.GuideStroke;
        graph.LineThickness = graph.LineThickness;
        AssertSamePens(first, graph.RenderPens);
    }

    [Fact]
    public void ControlsDoNotShareMutablePensOrDashStylesEvenWithTheSameBrushes()
    {
        IoGraph first = Graph();
        IoGraph second = new()
        {
            Stroke = first.Stroke, SecondaryStroke = first.SecondaryStroke, GuideStroke = first.GuideStroke
        };
        AssertRebuilt(first.RenderPens, second.RenderPens);
        Assert.Same(first.Stroke, second.RenderPens.Primary.Brush);
        first.LineThickness = 4;
        Assert.Equal(1.5, second.RenderPens.Primary.Thickness);
    }

    [Theory]
    [InlineData(nameof(IoGraph.Stroke))]
    [InlineData(nameof(IoGraph.SecondaryStroke))]
    [InlineData(nameof(IoGraph.GuideStroke))]
    public void ReplacingAStrokeRebuildsTheCacheAndUsesTheNewColour(string property)
    {
        IoGraph graph = Graph();
        var before = graph.RenderPens;
        SolidColorBrush replacement = new(Colors.Green);
        switch (property)
        {
            case nameof(IoGraph.Stroke): graph.Stroke = replacement; break;
            case nameof(IoGraph.SecondaryStroke): graph.SecondaryStroke = replacement; break;
            case nameof(IoGraph.GuideStroke): graph.GuideStroke = replacement; break;
        }
        var after = graph.RenderPens;
        AssertRebuilt(before, after);
        Pen changed = property switch
        {
            nameof(IoGraph.Stroke) => after.Primary,
            nameof(IoGraph.SecondaryStroke) => after.Secondary,
            _ => after.Guide!
        };
        Assert.Same(replacement, changed.Brush);
        Assert.Equal(Colors.Green, Assert.IsAssignableFrom<ISolidColorBrush>(changed.ToImmutable().Brush).Color);
        AssertSamePens(after, graph.RenderPens);
    }

    [Fact]
    public void SecondaryFallbackPrecedenceSurvivesReplacementAndRemoval()
    {
        IoGraph graph = new() { Stroke = Brushes.Red };
        var primaryFallback = graph.RenderPens;
        Assert.Same(Brushes.Red, primaryFallback.Secondary.Brush);
        graph.Stroke = Brushes.Green;
        var newPrimary = graph.RenderPens;
        Assert.NotSame(primaryFallback.Secondary, newPrimary.Secondary);
        Assert.Same(Brushes.Green, newPrimary.Secondary.Brush);

        graph.DiskStroke = Brushes.Blue;
        var diskFallback = graph.RenderPens;
        Assert.NotSame(newPrimary.Secondary, diskFallback.Secondary);
        Assert.Same(Brushes.Blue, diskFallback.Secondary.Brush);
        graph.SecondaryStroke = Brushes.Orange;
        var explicitSecondary = graph.RenderPens;
        Assert.NotSame(diskFallback.Secondary, explicitSecondary.Secondary);
        Assert.Same(Brushes.Orange, explicitSecondary.Secondary.Brush);

        // DiskStroke remains a cache input even while an explicit secondary brush masks it.
        graph.DiskStroke = Brushes.Purple;
        var maskedDiskChange = graph.RenderPens;
        Assert.NotSame(explicitSecondary.Secondary, maskedDiskChange.Secondary);
        Assert.Same(Brushes.Orange, maskedDiskChange.Secondary.Brush);
        graph.SecondaryStroke = null;
        Assert.Same(Brushes.Purple, graph.RenderPens.Secondary.Brush);
        graph.DiskStroke = null;
        Assert.Same(Brushes.Green, graph.RenderPens.Secondary.Brush);
        graph.Stroke = null;
        Assert.Null(graph.RenderPens.Baseline.Brush);
        Assert.Null(graph.RenderPens.Primary.Brush);
        Assert.Null(graph.RenderPens.Secondary.Brush);
    }

    [Fact]
    public void RemovingAndRestoringTheGuideDoesNotKeepTheOldBrush()
    {
        IoGraph graph = Graph();
        var before = graph.RenderPens;
        graph.GuideStroke = null;
        var withoutGuide = graph.RenderPens;
        Assert.NotSame(before.Baseline, withoutGuide.Baseline);
        Assert.Null(withoutGuide.Guide);
        AssertSamePens(withoutGuide, graph.RenderPens);
        graph.GuideStroke = Brushes.Green;
        Assert.Same(Brushes.Green, graph.RenderPens.Guide!.Brush);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.5)]
    [InlineData(3)]
    public void ThicknessChangesRebuildWithoutChangingBaselineGuideOrDashDimensions(double thickness)
    {
        IoGraph graph = Graph();
        var before = graph.RenderPens;
        graph.LineThickness = thickness;
        var after = graph.RenderPens;
        AssertRebuilt(before, after);
        Assert.Equal(1, after.Baseline.Thickness);
        Assert.Equal(0.5, after.Guide!.Thickness);
        Assert.Equal(thickness, after.Primary.Thickness);
        Assert.Equal(thickness, after.Secondary.Thickness);
        Assert.Null(after.Baseline.DashStyle);
        Assert.Null(after.Primary.DashStyle);
        Assert.Null(after.Guide.DashStyle);
        Assert.Equal(new[] { 3d, 2d }, after.Secondary.DashStyle!.Dashes);
        Assert.Equal(0, after.Secondary.DashStyle.Offset);
        // Constructors keep the original cap/join/miter defaults for every cached pen.
        Pen original = new(graph.Stroke, thickness);
        Assert.Equal(original.LineCap, after.Primary.LineCap);
        Assert.Equal(original.LineJoin, after.Primary.LineJoin);
        Assert.Equal(original.MiterLimit, after.Primary.MiterLimit);
        Assert.Equal(original.LineCap, after.Secondary.LineCap);
        Assert.Equal(original.LineJoin, after.Secondary.LineJoin);
        Assert.Equal(original.MiterLimit, after.Secondary.MiterLimit);
        AssertSamePens(after, graph.RenderPens);
    }

    [Fact]
    public void ThemeChangesRebuildEvenIfTheBrushReferencesStayTheSame() => OnOwnedDispatcher(() =>
    {
        IoGraph graph = Graph();
        ThemeVariantScope scope = new() { RequestedThemeVariant = ThemeVariant.Light, Child = graph };
        Assert.Equal(ThemeVariant.Light, graph.ActualThemeVariant);
        var light = graph.RenderPens;
        scope.RequestedThemeVariant = ThemeVariant.Dark;
        Assert.Equal(ThemeVariant.Dark, graph.ActualThemeVariant);
        var dark = graph.RenderPens;
        AssertRebuilt(light, dark);
        Assert.Same(light.Primary.Brush, dark.Primary.Brush);
        AssertSamePens(dark, graph.RenderPens);
        graph.Stroke = Brushes.Green; // subsequent dynamic-resource colour replacement
        var newColour = graph.RenderPens;
        AssertRebuilt(dark, newColour);
        Assert.Same(Brushes.Green, newColour.Primary.Brush);
        scope.RequestedThemeVariant = ThemeVariant.Light;
        Assert.Equal(ThemeVariant.Light, graph.ActualThemeVariant);
        AssertRebuilt(newColour, graph.RenderPens);
    });

    // The same nonparallel, windowless dispatcher seam used by ThemeLogoTests.
    private static void OnOwnedDispatcher(Action test)
    {
        FieldInfo primary = typeof(Dispatcher).GetField("s_uiThread", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Avalonia primary dispatcher slot is missing.");
        object? previous = primary.GetValue(null);
        Dispatcher owner = Dispatcher.CurrentDispatcher;
        try
        {
            primary.SetValue(null, owner);
            owner.Invoke(test, DispatcherPriority.Send);
        }
        finally { primary.SetValue(null, previous); }
    }

    [Fact]
    public void DashValueAndLengthChangesRebuildButEqualPatternsReuseTheCache()
    {
        IoGraphPenCache cache = new();
        List<double> pattern = [3, 2];
        var first = cache.Get(Brushes.Red, Brushes.Blue, Brushes.Gray, 1.5, pattern, ThemeVariant.Light);
        Assert.IsType<ImmutableDashStyle>(first.Secondary.DashStyle);
        AssertSamePens(first, cache.Get(Brushes.Red, Brushes.Blue, Brushes.Gray, 1.5, new[] { 3d, 2d }, ThemeVariant.Light));

        pattern[0] = 4;
        var changedValue = cache.Get(Brushes.Red, Brushes.Blue, Brushes.Gray, 1.5, pattern, ThemeVariant.Light);
        AssertRebuilt(first, changedValue);
        Assert.Equal(new[] { 3d, 2d }, first.Secondary.DashStyle!.Dashes); // owned snapshot, not the caller's list
        Assert.Equal(new[] { 4d, 2d }, changedValue.Secondary.DashStyle!.Dashes);
        pattern.Add(1);
        var changedLength = cache.Get(Brushes.Red, Brushes.Blue, Brushes.Gray, 1.5, pattern, ThemeVariant.Light);
        AssertRebuilt(changedValue, changedLength);
        Assert.Equal(new[] { 4d, 2d, 1d }, changedLength.Secondary.DashStyle!.Dashes);
        AssertSamePens(changedLength, cache.Get(Brushes.Red, Brushes.Blue, Brushes.Gray, 1.5, pattern, ThemeVariant.Light));
    }

    [Fact]
    public void MutableColoursOpacityAndGradientStopsRemainLiveWithoutFreezingBrushes()
    {
        SolidColorBrush stroke = new(Colors.Red), guide = new(Colors.Gray);
        LinearGradientBrush secondary = new()
        {
            GradientStops = new GradientStops { new(Colors.Blue, 0), new(Colors.White, 1) }
        };
        IoGraph graph = new() { Stroke = stroke, SecondaryStroke = secondary, GuideStroke = guide };
        var before = graph.RenderPens;
        var oldSnapshot = before.Primary.ToImmutable();
        stroke.Color = Colors.Green;
        stroke.Opacity = 0.4;
        guide.Color = Colors.Purple;
        secondary.GradientStops[0].Color = Colors.Orange;
        secondary.Opacity = 0.7;
        var after = graph.RenderPens;
        AssertSamePens(before, after); // live mutable brushes need no replacement pens or extra event subscriptions
        Assert.Equal(Colors.Red, Assert.IsAssignableFrom<ISolidColorBrush>(oldSnapshot.Brush).Color);
        Assert.Equal(Colors.Green, Assert.IsAssignableFrom<ISolidColorBrush>(after.Primary.ToImmutable().Brush).Color);
        Assert.Equal(Colors.Green, Assert.IsAssignableFrom<ISolidColorBrush>(after.Baseline.ToImmutable().Brush).Color);
        Assert.Equal(0.4, after.Primary.ToImmutable().Brush!.Opacity);
        Assert.Equal(Colors.Purple, Assert.IsAssignableFrom<ISolidColorBrush>(after.Guide!.ToImmutable().Brush).Color);
        var gradient = Assert.IsAssignableFrom<ILinearGradientBrush>(after.Secondary.ToImmutable().Brush);
        Assert.Equal(Colors.Orange, gradient.GradientStops[0].Color);
        Assert.Equal(0.7, gradient.Opacity);
    }

    [Fact]
    public void FillSamplesAndSourceChangesDoNotRebuildPensOrChangeDirectionalLayout()
    {
        IoGraph graph = Graph();
        graph.Source = IoGraphSource.DiskIo;
        IoSeries series = new(3, 3, [0, 0, 900_000], [0, 0, 800_000], [0, 0, 100_000], [0, 0, 300_000], [0, 0, 0]);
        graph.SetSeries(series);
        Size size = new(130, 17);
        var layout = graph.Layout(size);
        var pens = graph.RenderPens;
        graph.Fill = Brushes.Orange;
        graph.SetSeries(series);
        AssertSamePens(pens, graph.RenderPens);
        var after = graph.Layout(size);
        Assert.Equal(layout.Primary, after.Primary);
        Assert.Equal(layout.Secondary, after.Secondary);
        Assert.Equal(300_000, graph.PrimaryTrafficPeak); // disk read is still the solid, filled primary
        Assert.Equal(100_000, graph.SecondaryTrafficPeak);
        graph.Source = IoGraphSource.NetworkIo;
        graph.SetSeries(series);
        AssertSamePens(pens, graph.RenderPens);
        Assert.Equal(900_000, graph.PrimaryTrafficPeak);
        Assert.Equal(800_000, graph.SecondaryTrafficPeak);
    }

    private static void AssertSamePens(
        (Pen Baseline, Pen Primary, Pen Secondary, Pen? Guide) expected,
        (Pen Baseline, Pen Primary, Pen Secondary, Pen? Guide) actual)
    {
        Assert.Same(expected.Baseline, actual.Baseline);
        Assert.Same(expected.Primary, actual.Primary);
        Assert.Same(expected.Secondary, actual.Secondary);
        Assert.Same(expected.Guide, actual.Guide);
        Assert.Same(expected.Secondary.DashStyle, actual.Secondary.DashStyle);
    }

    private static void AssertRebuilt(
        (Pen Baseline, Pen Primary, Pen Secondary, Pen? Guide) before,
        (Pen Baseline, Pen Primary, Pen Secondary, Pen? Guide) after)
    {
        Assert.NotSame(before.Baseline, after.Baseline);
        Assert.NotSame(before.Primary, after.Primary);
        Assert.NotSame(before.Secondary, after.Secondary);
        Assert.NotSame(before.Guide, after.Guide);
        Assert.NotSame(before.Secondary.DashStyle, after.Secondary.DashStyle);
    }
}
