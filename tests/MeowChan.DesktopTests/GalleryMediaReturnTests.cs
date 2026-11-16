using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

/// <summary>
/// Sweep S7 (16.11.2026) F4/F7/F8: a gallery opened on the Media page closes back to the page through the page's own return (restore now and
/// once more after the activation's rebuild), keyed on the way back the gallery named rather than on its origin control; the restore is an
/// observed run and an empty page after it still gets a focus. The real window (late presentation, focus on the tile) is the smoke's
/// MEDIA_PAGE chain; these tests pin the shell's wiring and the page's pieces without a window.
/// </summary>
[Collection("Booru exact navigation")]
public sealed class GalleryMediaReturnTests
{
    private const string Gallery = "Desktop/Shell/MainWindow.Gallery.cs";
    private const BindingFlags InstanceAny = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    // ---- F4/F7: the shell's wiring ----

    [Fact]
    public void AGalleryFromTheMediaPageTakesItsReturnPointBeforeTheViewerCoversThePage()
    {
        string start = Body(Gallery, "private async Task StartGalleryAsync");
        int capture = start.IndexOf("_galleryMediaReturn = CaptureGalleryMediaReturn(GalleryReturnTargetFor(origin, readerOrigin, originPage));", StringComparison.Ordinal);
        Assert.True(capture >= 0, "StartGalleryAsync must capture the Media return point.");
        Assert.True(capture < start.IndexOf("new GalleryViewer", StringComparison.Ordinal), "The point is taken while the page is still in front.");
        Assert.True(capture > start.IndexOf("await _galleryCleanupTask;", StringComparison.Ordinal), "The point belongs to this open, after the retired viewer.");

        string capturing = Body(Gallery, "private MediaLibraryReturnPoint? CaptureGalleryMediaReturn");
        Assert.Contains("target != GalleryReturnTarget.Media", capturing);
        Assert.Contains("_selectedPage != WorkspacePage.Media", capturing);
        Assert.Contains("CaptureGalleryReturnPoint()", capturing);
        Assert.DoesNotContain("EndPendingReturn", capturing); // would revoke a Booru handoff guard of this very open
    }

    [Fact]
    public void ClosingToMediaRestoresThroughThePageReturnKeyedOnTheReturnTarget()
    {
        string close = Body(Gallery, "private void CloseGalleryCore");
        // F7: no identity test against the grid; a tag-queue walk from the page's header has no origin control.
        Assert.DoesNotContain("ReferenceEquals(closedOrigin, _mediaPage?.Grid)", close);
        Assert.Contains("closingTarget == GalleryReturnTarget.Media && _mediaPage != null && _selectedPage == WorkspacePage.Media", close);
        // F4: the page's return (restore, wait for the rebuild and presentation, restore again), not a single posted FocusGrid.
        int ret = close.IndexOf("StartMediaPageReturn(mediaReturn, \"gallery.mediaReturn\")", StringComparison.Ordinal);
        Assert.True(ret >= 0);
        Assert.True(ret > close.IndexOf("NavigateWorkspace(_galleryOriginPage", StringComparison.Ordinal), "The restore follows the page activation.");
        Assert.True(ret > close.IndexOf("RefreshMediaPageUserData();", StringComparison.Ordinal), "The restore follows the own-state refresh it must outlast.");
        Assert.Contains("else _mediaPage.FocusGrid();", close); // no tile was shown: the grid keeps its plain focus
        // The point is the closing gallery's own and never outlives it.
        Assert.True(close.IndexOf("_galleryMediaReturn = null;", StringComparison.Ordinal) < close.IndexOf("_gallery = null;", StringComparison.Ordinal));
    }

    // ---- F8: the restore is observed ----

    [Fact]
    public void BothMediaReturnsStartTheRestoreAsAnObservedRun()
    {
        string start = Body(Gallery, "private void StartMediaPageReturn");
        Assert.Contains("MediaReturnTask = OpenActions.StartObserved(() => page.ReturnToAsync(point), operation);", start);
        string back = Body("Desktop/Shell/MainWindow.MediaReturn.cs", "private void ReturnToMedia");
        Assert.Contains("StartMediaPageReturn(point, \"media.return\")", back);
        Assert.DoesNotContain("MediaReturnTask = _mediaPage", back);
    }

    [Fact]
    public async Task AnObservedRunReportsAFailureUnderItsOperationAndCompletes()
    {
        List<(Exception Error, string Operation)> recovered = new();
        Task failing = OpenActions.StartObserved(() => Task.FromException(new InvalidOperationException("synthetic")), "media.return",
            (error, operation) => { recovered.Add((error, operation)); return true; });
        await failing;
        Assert.True(failing.IsCompletedSuccessfully);
        (Exception first, string name) = Assert.Single(recovered);
        Assert.IsType<InvalidOperationException>(first);
        Assert.Equal("media.return", name);

        // A synchronous throw of the open is the same failure; a cancellation (the page moved on) is none.
        Task throwing = OpenActions.StartObserved(() => throw new InvalidOperationException("sync"), "gallery.mediaReturn",
            (error, operation) => { recovered.Add((error, operation)); return true; });
        await throwing;
        Assert.Equal("gallery.mediaReturn", recovered[^1].Operation);
        bool reported = false;
        Task cancelled = OpenActions.StartObserved(() => Task.FromCanceled(new CancellationToken(true)), "media.return",
            (_, _) => reported = true);
        await cancelled;
        Assert.True(cancelled.IsCompletedSuccessfully);
        Assert.False(reported, "A cancellation must not be reported.");
        Assert.Equal(2, recovered.Count);
    }

    // ---- F8: an empty page after the return still has a focus ----

    [Fact]
    public void AReturnToAPageWithoutTilesFocusesTheEmptyStatesWayOut()
    {
        string restore = Body("Desktop/Views/Media/MediaPage.Return.cs", "private async Task<bool> RestoreAsync");
        int empty = restore.IndexOf("if (focus < 0)", StringComparison.Ordinal);
        Assert.True(empty >= 0);
        Assert.True(restore.IndexOf("EmptyReturnFocusTarget()?.Focus();", empty, StringComparison.Ordinal) > empty);
        string target = Member("Desktop/Views/Media/MediaPage.Return.cs", "internal Control? EmptyReturnFocusTarget()");
        Assert.Contains("_emptyActions.Children.Append(_nameFilter).Append(_refresh)", target);
    }

    [Fact]
    public void TheEmptyPageFallbackTakesTheFirstShownEnabledFocusableControl() => BooruComponentDispatcher.Run(() =>
    {
        Button hidden = new() { IsVisible = false }, disabled = new() { IsEnabled = false }, inert = new() { Focusable = false };
        Button reset = new(), downloads = new();
        TextBox search = new();
        Assert.Same(reset, MediaPage.PickReturnFocus([hidden, disabled, inert, reset, downloads, search], control => control.IsVisible));
        Assert.Same(search, MediaPage.PickReturnFocus([hidden, disabled, search], control => control.IsVisible));
        Assert.Null(MediaPage.PickReturnFocus([hidden, disabled, inert], control => control.IsVisible));
    });

    [Fact]
    public void AHiddenOrTilelessPageGivesNoGalleryReturnPoint() => BooruComponentDispatcher.Run(() =>
    {
        MediaPage page = new(_ => throw new InvalidOperationException("The fixture must not open archive data."));
        var lifetime = (CancellationTokenSource)typeof(MediaPage).GetField("_lifetime", InstanceAny)!.GetValue(page)!;
        try
        {
            page.IsVisible = false;
            Assert.Null(page.CaptureGalleryReturnPoint());
            page.IsVisible = true;
            MethodInfo update = typeof(Visual).GetMethod("UpdateIsEffectivelyVisible", InstanceAny)
                ?? throw new MissingMethodException(typeof(Visual).FullName, "UpdateIsEffectivelyVisible");
            update.Invoke(page, [true]);
            Assert.True(page.IsEffectivelyVisible);
            Assert.Null(page.CaptureGalleryReturnPoint()); // no tile: closing focuses the grid only
        }
        finally
        {
            try { page.EndLifetime(); }
            finally { lifetime.Dispose(); }
        }
    });

    // ---- helpers ----

    private static string Member(string file, string start)
    {
        string text = Source(file);
        int index = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(index >= 0, start + " is not in " + file);
        int semicolon = text.IndexOf(';', index), brace = text.IndexOf('{', index);
        return brace >= 0 && brace < semicolon ? Body(file, start) : text[index..(semicolon + 1)];
    }

    private static string Body(string file, string start)
    {
        string text = Source(file);
        int index = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(index >= 0, start + " is not in " + file);
        int open = text.IndexOf('{', index), depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0) return text[index..(i + 1)];
        }
        throw new InvalidOperationException("Unbalanced braces after " + start);
    }

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../.."));

    private static string Source(string relative) => File.ReadAllText(Path.Combine(Root(), relative));
}
