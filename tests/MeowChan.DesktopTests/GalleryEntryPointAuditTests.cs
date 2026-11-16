using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.MediaActions;
using MeowChan.Gallery;
using MeowChan.Storage;

namespace MeowChan.DesktopTests;

/// <summary>
/// The entry point audit of the gallery viewer (16.11.2026, Tom: "shown but silently does nothing"). The real viewer is built without a window
/// and the real key goes to it through the gallery's key policy (<see cref="GalleryViewer.RouteKeyForTests"/>), so each test fails on the
/// handler as it was: a key whose command had no handler, a key consumed in silence, a row enabled while its click can only return.
/// </summary>
[Collection("Gallery viewer construction")]
public sealed class GalleryEntryPointAuditTests
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

    private static void WithViewer(Action<GalleryViewer> test) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
        test(new GalleryViewer());
    });

    private static KeyEventArgs Key(GalleryViewer viewer, Key key, KeyModifiers modifiers = KeyModifiers.None) =>
        new() { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers, Source = viewer };

    private static T Get<T>(GalleryViewer viewer, string field) => (T)typeof(GalleryViewer).GetField(field, Fields)!.GetValue(viewer)!;
    private static void Set(GalleryViewer viewer, string field, object? value) => typeof(GalleryViewer).GetField(field, Fields)!.SetValue(viewer, value);

    // A shown image without a post (a loose file): T, R and the tag key have nothing they can open for it.
    private static GalleryEntry LooseImage()
    {
        var asset = new MediaAssetReference("asset", "image", "original", "loose.png", "/fixture/loose.png", 7,
            new string('a', 64), "https://fixture.invalid/loose.png", "loose.png", true);
        var manifest = new MediaAssetSnapshot("fixture", MediaManifestStatus.Available, 1, "generation", [asset]);
        return new(new(new("fixture", "b", "42"), manifest.Directory, asset.AssetId), manifest, asset, null, null, false, true);
    }

    private static List<string> Explanations(GalleryViewer viewer)
    {
        List<string> said = new();
        viewer.MenuExplanationRequested += said.Add;
        return said;
    }

    // MISSING HANDLER: "Back to image" is offered for the gallery in the shortcut editor; a key bound to it did nothing.
    [Fact]
    public void AKeyBoundToBackToImageClosesThePostPanel() => WithViewer(viewer =>
    {
        ShortcutService.Publish(new ShortcutBindingsSection().Set("gallery.context-close", [ShortcutGesture.Parse("F9")]));
        try
        {
            viewer.Configure(contextOpen: true, showShortcutHelp: false, videoAutoplay: false, videoStartMuted: false, videoVolume: 50);
            Assert.True(Get<bool>(viewer, "_contextOpen"));
            KeyEventArgs press = Key(viewer, Avalonia.Input.Key.F9);
            viewer.RouteKeyForTests(press);
            Assert.True(press.Handled);
            Assert.False(Get<bool>(viewer, "_contextOpen"));
        }
        finally { ShortcutService.Publish(new ShortcutBindingsSection()); }
    });

    // SILENT NO-OP: D while the slideshow cannot start was consumed without a word; now the slideshow's own status says why.
    [Fact]
    public void TheSlideshowKeyThatCannotStartSaysWhy() => WithViewer(viewer =>
    {
        List<string> said = Explanations(viewer);
        KeyEventArgs press = Key(viewer, Avalonia.Input.Key.D);
        viewer.RouteKeyForTests(press);
        Assert.True(press.Handled);
        Assert.False(viewer.CanExecutePlaybackAction("gallery.slideshow-toggle"));
        Assert.False(string.IsNullOrWhiteSpace(Assert.Single(said)));
    });

    // SILENT NO-OP: T on a medium without a post; the menu entry is disabled with "no post", the key now says the same.
    [Fact]
    public void ShowInThreadOnAMediumWithoutAPostSaysSoAndOpensNothing() => WithViewer(viewer =>
    {
        List<string> said = Explanations(viewer);
        int opened = 0;
        viewer.ReadPostRequested += _ => opened++;
        Set(viewer, "_entry", LooseImage());
        KeyEventArgs press = Key(viewer, Avalonia.Input.Key.T);
        viewer.RouteKeyForTests(press);
        Assert.True(press.Handled);
        Assert.Equal(0, opened);
        Assert.Equal(MediaMenuReasons.NoPost, Assert.Single(said));
    });

    // SILENT NO-OP: the tag key while the medium's tags are unknown (the Tags row is disabled with that reason).
    [Fact]
    public void TheTagKeyWithoutTagsSaysWhy() => WithViewer(viewer =>
    {
        List<string> said = Explanations(viewer);
        Set(viewer, "_entry", LooseImage());
        KeyEventArgs press = Key(viewer, Avalonia.Input.Key.T, KeyModifiers.Control | KeyModifiers.Shift);
        viewer.RouteKeyForTests(press);
        Assert.True(press.Handled);
        Assert.Equal(MediaMenuReasons.TagsUnknown, Assert.Single(said));
    });

    // SILENT NO-OP: the "Rating: –" segment of the info line (and R) with neither stars nor tags available.
    [Fact]
    public void TheRatingSegmentWithNothingToOpenSaysWhy() => WithViewer(viewer =>
    {
        List<string> said = Explanations(viewer);
        Set(viewer, "_entry", LooseImage());
        Assert.False(viewer.CanRateAndTag);
        Get<Button>(viewer, "_ratingStatusButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(MediaMenuReasons.Unavailable, Assert.Single(said));
    });

    // Enabled row whose click returns early: "Save frame as image" while no video frame is ready.
    [Fact]
    public void TheSaveFrameRowIsEnabledOnlyWhileAFrameCanBeSaved() => WithViewer(viewer =>
    {
        Set(viewer, "_entry", LooseImage());
        Button saveFrame = Get<Button>(viewer, "_saveFrame");
        saveFrame.IsEnabled = true; // the state the row had for a video that was still loading or had failed
        typeof(GalleryViewer).GetMethod("RefreshOverflowItems", Fields)!.Invoke(viewer, null);
        Assert.False(saveFrame.IsEnabled);
    });

    // WRONG TARGET: the Menu key with the focus on a tool of the dock opened the media menu; the dock's own menu is the layout menu (GL3).
    [Fact]
    public void TheMenuKeyOnADockToolOpensTheLayoutMenuAndStaysTheMediaMenuElsewhere() => WithViewer(viewer =>
    {
        Assert.True(viewer.IsLayoutMenuKeySource(Get<Button>(viewer, "_fit")));
        Assert.True(viewer.IsLayoutMenuKeySource(viewer.OverflowButton));
        Assert.False(viewer.IsLayoutMenuKeySource(viewer));            // the picture: the media menu
        Assert.False(viewer.IsLayoutMenuKeySource(viewer.Filmstrip));  // a thumbnail: the shown medium's media menu
        Assert.False(viewer.IsLayoutMenuKeySource(null));
    });

    // Quote chips of the post panel present "Show quoted post" like the reader's quote buttons, so a key bound to it opens the focused chip.
    [Fact]
    public void QuoteChipsCarryTheShowQuotedPostCommand()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory != null && !File.Exists(Path.Combine(directory, "MeowChan.csproj"))) directory = Path.GetDirectoryName(directory);
        string viewer = File.ReadAllText(Path.Combine(directory!, "Desktop", "GalleryViewer.cs"));
        Assert.Contains("CommandBindings.BindExisting(chip, \"reader.open-quote\")", viewer);
        Assert.Contains("Matches(e, \"gallery.context-close\")", viewer);
    }
}
