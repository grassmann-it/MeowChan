using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;

namespace MeowChan.DesktopTests;

public sealed class ReaderGalleryAdornerLifetimeTests
{
    [Fact]
    public void AnimationPriorityReturnsARealRestorationHandleUnlikeLocalValue()
    {
        Border local = new();
        Assert.Null(local.SetValue(Visual.IsVisibleProperty, false, BindingPriority.LocalValue));
        Assert.False(local.IsVisible);

        Border animated = new();
        using (IDisposable? visibility = animated.SetValue(Visual.IsVisibleProperty, false, BindingPriority.Animation))
        {
            Assert.NotNull(visibility);
            Assert.False(animated.IsVisible);
        }
        Assert.True(animated.IsVisible);
    }

    [Fact]
    public void CoverageRetainsItsOverrideAcrossLayoutsAndReleasesOnlyItsOwnHandle()
    {
        Border button = new();
        StackPanel reader = new();
        reader.Children.Add(button);
        Border mask = new() { IsVisible = false };
        mask.SetValue(AdornerLayer.AdornedElementProperty, button);
        using (IDisposable? original = mask.SetValue(Visual.IsVisibleProperty, true, BindingPriority.Animation))
        {
            Assert.NotNull(original);
            Assert.True(mask.IsVisible);
            ReaderGalleryAdornerCoverage coverage = new();
            try
            {
                for (int layout = 0; layout < 3; layout++)
                {
                    coverage.Update(reader, null, [mask]);
                    Assert.False(mask.IsVisible); // The restoration handle must outlive Update.
                }
                coverage.Clear();
                Assert.True(mask.IsVisible); // The earlier animation override is still owned by its caller.
                coverage.Clear();
                Assert.True(mask.IsVisible);
            }
            finally
            {
                coverage.Clear();
            }
        }
        Assert.False(mask.IsVisible); // The underlying local value was never replaced.
    }
}
