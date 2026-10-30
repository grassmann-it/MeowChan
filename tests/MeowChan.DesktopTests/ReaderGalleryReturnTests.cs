using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;

namespace MeowChan.DesktopTests;

public sealed class ReaderGalleryReturnTests
{
    [Fact]
    public void CoveredReaderMasksCannotInterceptGalleryAndRestoreOnReturn()
    {
        Border pagination = new();
        StackPanel reader = new(); reader.Children.Add(pagination);
        Border mask = Mask(pagination);
        ReaderGalleryAdornerCoverage coverage = new();
        coverage.Update(reader, null, [mask]);
        Assert.False(mask.IsVisible);
        coverage.Update(reader, null, [mask]); // Repeated layouts do not compound ownership.
        coverage.Clear();
        Assert.True(mask.IsVisible);
        coverage.Clear();
        Assert.True(mask.IsVisible);
    }

    [Fact]
    public void ArchiveMasksAreCoveredButGalleryAndNotificationAdornersAreNot()
    {
        StackPanel reader = new(), archive = new(), gallery = new();
        Border archivedButton = new(), galleryButton = new();
        archive.Children.Add(archivedButton); gallery.Children.Add(galleryButton);
        Border archiveMask = Mask(archivedButton), galleryMask = Mask(galleryButton), notification = new();
        ReaderGalleryAdornerCoverage coverage = new();
        coverage.Update(reader, archive, [archiveMask, galleryMask, notification]);
        Assert.False(archiveMask.IsVisible);
        Assert.True(galleryMask.IsVisible);
        Assert.True(notification.IsVisible);
        coverage.Clear();
        Assert.True(archiveMask.IsVisible);
    }

    [Fact]
    public void ReturnRestoresPriorVisibilitySourceAndPicksUpLateAdorners()
    {
        Border button = new(); StackPanel reader = new(); reader.Children.Add(button);
        Border alreadyHidden = Mask(button), styled = Mask(button), late = Mask(button);
        alreadyHidden.IsVisible = false;
        styled.SetValue(Visual.IsVisibleProperty, true, BindingPriority.Style);
        ReaderGalleryAdornerCoverage coverage = new();
        coverage.Update(reader, null, [alreadyHidden, styled]);
        // Underlying state can change while the overlay owns visibility.
        styled.SetValue(Visual.IsVisibleProperty, false, BindingPriority.Style);
        coverage.Update(reader, null, [alreadyHidden, styled, late]);
        Assert.False(late.IsVisible);
        coverage.Clear();
        Assert.False(alreadyHidden.IsVisible);
        Assert.False(styled.IsVisible);
        Assert.True(late.IsVisible);
        styled.SetValue(Visual.IsVisibleProperty, true, BindingPriority.Style);
        Assert.True(styled.IsVisible); // No local-value replacement of the style.
        coverage.Update(reader, null, [late]);
        Assert.False(late.IsVisible);
        coverage.Clear();
        Assert.True(late.IsVisible);
    }

    private static Border Mask(Control owner)
    {
        Border mask = new();
        mask.SetValue(AdornerLayer.AdornedElementProperty, owner);
        return mask;
    }
}
