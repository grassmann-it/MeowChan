using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Motion;
using MeowChan.Desktop.WorkViews;
using MeowChan.Gallery;
using MeowChan.Storage;
using MeowChan.Tags;
using MeowChan.Testing;
using SkiaSharp;

namespace MeowChan.DesktopTests;

[Collection("Avalonia global state")]
public sealed class GalleryControlsHandleTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly string[] Languages = ["en", "de", "cs", "es", "fr", "it", "ja", "ko", "pl", "pt-BR", "ru", "zh-Hans"];

    [Theory]
    [InlineData(false, false, false, false, false, false, false)]
    [InlineData(false, false, false, true, false, false, false)]
    [InlineData(false, false, true, false, false, false, false)]
    [InlineData(false, false, true, true, false, false, false)]
    [InlineData(false, true, false, false, false, false, false)]
    [InlineData(false, true, false, true, false, false, false)]
    [InlineData(false, true, true, false, false, false, false)]
    [InlineData(false, true, true, true, false, false, false)]
    [InlineData(true, false, false, false, false, false, false)]
    [InlineData(true, false, false, true, false, false, false)]
    [InlineData(true, false, true, false, true, false, true)]
    [InlineData(true, false, true, true, true, false, true)]
    [InlineData(true, true, false, false, true, false, false)]
    [InlineData(true, true, false, true, true, true, true)]
    [InlineData(true, true, true, false, true, false, true)]
    [InlineData(true, true, true, true, true, true, true)]
    public void ResolvesPresenceArtworkAndHitOwnership(bool overlay, bool hidden, bool pressed, bool motionOrFocus,
        bool present, bool artwork, bool hitTest) =>
        Assert.Equal(new GalleryControlsHandle(present, artwork, hitTest),
            GalleryControlsHandle.Resolve(overlay, hidden, pressed, motionOrFocus));

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void MouseMotionLeavesTheVisibleFooterAndItsNavigationDirectlyReachable(double scaling) => WithGallery((viewer, host, _) =>
    {
        Control footer = Find<Control>(viewer, "GalleryOverlayFooter");
        Button handle = Find<Button>(viewer, "GalleryControlsEdgeIndicator");
        Button next = Find<Button>(viewer, "GalleryNext");
        HideAfterIdle(viewer);
        Layout(host);
        Rect hiddenHandle = BoundsIn(handle, host);
        Assert.InRange(hiddenHandle.Width, 43.0, 45.0);
        Assert.InRange(hiddenHandle.Height, 43.0, 45.0);
        Assert.InRange(Math.Abs(hiddenHandle.Center.X - BoundsIn(viewer, host).Center.X), 0, 1);
        Assert.False(footer.IsEffectivelyVisible);

        MoveInPicture(host);
        Assert.True(footer.IsEffectivelyVisible);
        Rect overlap = hiddenHandle.Intersect(BoundsIn(footer, host));
        Assert.True(overlap.Width > 1 && overlap.Height > 1);
        host.MouseMove(overlap.Center);
        Layout(host);
        Visual? hit = host.InputHitTest(overlap.Center) as Visual;
        int handleClicks = 0;
        handle.AddHandler(Button.ClickEvent, (_, _) => handleClicks++, RoutingStrategies.Bubble, handledEventsToo: true);
        host.MouseDown(overlap.Center, MouseButton.Left);
        host.MouseUp(overlap.Center, MouseButton.Left);
        Assert.False(Hits(hit, handle), $"Actual hit at {overlap.Center} reached the reveal handle above the visible footer. "
            + $"Handle={hiddenHandle}, footer={BoundsIn(footer, host)}, next={BoundsIn(next, host)}, hit={hit}, clicks={handleClicks}.");
        Assert.Equal(0, handleClicks);
        Assert.False(handle.IsEffectivelyVisible);
        Assert.False(handle.IsHitTestVisible);

        Point nextPoint = BoundsIn(next, host).Center;
        host.MouseMove(nextPoint);
        Assert.True(Hits(host.InputHitTest(nextPoint) as Visual, next));
        List<int> moves = [];
        viewer.MoveRequested += moves.Add;
        host.MouseDown(nextPoint, MouseButton.Left);
        Invoke(viewer, "OnIndicatorExpiry", viewer, EventArgs.Empty);
        AssertHandleOutOfTheWay(viewer);
        host.MouseUp(nextPoint, MouseButton.Left);
        Assert.Equal([1], moves);
    }, scaling);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BottomFilmstripFollowsTheFooterWithoutAddingARevealTarget(bool overlay) => WithGallery((viewer, host, _) =>
    {
        viewer.ConfigurePlayback(new GalleryPlaybackPreferences(OverlayControls: overlay, AutoHideControls: overlay));
        viewer.SetLayoutChoice(GalleryChromeMode.View, GalleryLayoutChoice.Default with { Filmstrip = true, FilmstripDock = GalleryFilmstripDock.Bottom });
        HideAfterIdle(viewer);
        Layout(host);
        Assert.Equal(!overlay, viewer.Filmstrip.IsEffectivelyVisible);

        MoveInPicture(host);
        Assert.True(viewer.Filmstrip.IsEffectivelyVisible);
        Assert.Equal(overlay, Find<Control>(viewer, "GalleryOverlayFooter").IsEffectivelyVisible);
        AssertHandleOutOfTheWay(viewer);
    });

    [Fact]
    public void TaggingKeepsItsDecisionStripAndNavigationAccessibleDuringMotion() => WithGallery((viewer, host, entry) =>
    {
        TaggingHost owner = new(entry);
        var key = new TagQueueKey(entry.Key.Thread, entry.Manifest.Directory, entry.Asset.AssetId, null, null, null, null);
        using var walk = new TaggingWalk([new TaggingWalkItem(entry.EntryKey, key, entry.Asset.RequestedFileName)], 0, owner.Current, owner);
        viewer.StartTaggingMode(walk, owner);
        try
        {
            HideAfterIdle(viewer);
            MoveInPicture(host);
            Assert.True(viewer.TaggingStrip.IsEffectivelyVisible);
            Assert.True(Find<Control>(viewer, "GalleryOverlayFooter").IsEffectivelyVisible);
            AssertHandleOutOfTheWay(viewer);
        }
        finally { viewer.EndTaggingMode(); }
    });

    [Fact]
    public void TabStillRevealsTheFooterAfterTheHiddenHandleReceivesFocus() => WithGallery((viewer, host, _) =>
    {
        HideAfterIdle(viewer);
        Layout(host);
        Button handle = Find<Button>(viewer, "GalleryControlsEdgeIndicator");
        Assert.True(handle.IsEffectivelyVisible);
        Assert.False(handle.IsHitTestVisible);
        Assert.Equal(0, Chevron(handle).Opacity);

        Assert.True(handle.Focus(NavigationMethod.Tab));
        Assert.Same(handle, host.FocusManager!.GetFocusedElement());
        Assert.True(handle.IsHitTestVisible);
        Assert.Equal(1, Chevron(handle).Opacity);
        host.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
        host.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.None);
        Layout(host);
        Control footer = Find<Control>(viewer, "GalleryOverlayFooter");
        Assert.True(footer.IsEffectivelyVisible);
        Assert.NotSame(handle, host.FocusManager.GetFocusedElement());
        AssertHandleOutOfTheWay(viewer);
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void OnlyTheHandlePressKeepsItsTargetUntilReleaseOrCaptureLoss(bool touch, bool cancel) => WithGallery((viewer, host, _) =>
    {
        HideAfterIdle(viewer);
        Layout(host);
        Button handle = Find<Button>(viewer, "GalleryControlsEdgeIndicator");
        Control footer = Find<Control>(viewer, "GalleryOverlayFooter");
        Assert.True(handle.Focus(NavigationMethod.Tab));
        Layout(host);
        Assert.True(handle.IsHitTestVisible);
        Point point = BoundsIn(handle, host).Center;
        Visual? hit = host.InputHitTest(point) as Visual;
        Assert.True(Hits(hit, handle), $"Focused handle at {point} received {hit}.");

        int clicks = 0;
        handle.AddHandler(Button.ClickEvent, (_, _) => clicks++, RoutingStrategies.Bubble, handledEventsToo: true);
        using IHeadlessTouchPointer? contact = touch ? host.TouchBegin(point) : null;
        if (!touch)
        {
            host.MouseMove(point);
            Assert.False(footer.IsEffectivelyVisible);
            host.MouseDown(point, MouseButton.Left);
        }
        IPointer pointer = Assert.IsAssignableFrom<IPointer>(Field("_lastPointer").GetValue(viewer));
        Assert.True(handle.IsPressed);
        Assert.True(Hits(pointer.Captured as Visual, handle));
        Assert.False(footer.IsEffectivelyVisible);

        // A separate reveal may expose the footer before the captured press ends.
        Invoke(viewer, "RevealOverlayControls");
        Invoke(viewer, "OnIndicatorExpiry", viewer, EventArgs.Empty);
        Layout(host);
        Assert.True(footer.IsEffectivelyVisible);
        Assert.True(handle.IsEffectivelyVisible);
        Assert.True(handle.IsHitTestVisible);
        Assert.Equal(0, Chevron(handle).Opacity);

        if (cancel) pointer.Capture(null);
        if (contact != null) host.TouchEnd(contact, point);
        else host.MouseUp(point, MouseButton.Left);
        Layout(host);
        Assert.Equal(cancel ? 0 : 1, clicks);
        AssertHandleOutOfTheWay(viewer);
        Assert.False(Hits(host.InputHitTest(point) as Visual, handle));
    });

    [Fact]
    public void TouchingTheIdleBottomEdgeRevealsTheFooterWithoutClickingTheHandle() => WithGallery((viewer, host, _) =>
    {
        HideAfterIdle(viewer);
        Layout(host);
        Button handle = Find<Button>(viewer, "GalleryControlsEdgeIndicator");
        Assert.False(handle.IsHitTestVisible);
        Point edge = viewer.TranslatePoint(new Point(viewer.Bounds.Width / 2, viewer.Bounds.Height - 12), host)!.Value;
        int clicks = 0;
        handle.AddHandler(Button.ClickEvent, (_, _) => clicks++, RoutingStrategies.Bubble, handledEventsToo: true);
        using IHeadlessTouchPointer contact = host.TouchBegin(edge);
        host.TouchEnd(contact, edge);
        Layout(host);
        Assert.True(Find<Control>(viewer, "GalleryOverlayFooter").IsEffectivelyVisible);
        Assert.Equal(0, clicks);
        AssertHandleOutOfTheWay(viewer);
    });

    [Fact]
    public void SeparateVideoOverlayRootRoutesMotionAndKeysToTheSameHandleState() => WithGallery((viewer, owner, _) =>
    {
        var surface = Assert.IsType<NativeVideoOverlayRoot>(Field("_nativeVideoOverlay").GetValue(viewer));
        Window overlay = new() { Width = 800, Height = 600, Content = surface, Classes = { MotionPolicy.OffClass } };
        try
        {
            overlay.Show(owner);
            overlay.PlatformImpl!.Activated?.Invoke();
            // Exercise the actual separately hosted gallery root on Linux without requiring a Windows video surface.
            Field("_nativeOverlayActive").SetValue(viewer, true);
            Invoke(viewer, "ApplyOverlayPlacement");
            Layout(owner);
            Layout(overlay);
            Button handle = Find<Button>(surface, "GalleryControlsEdgeIndicator");
            Control footer = Find<Control>(surface, "GalleryOverlayFooter");
            Assert.Same(overlay, TopLevel.GetTopLevel(handle));
            Assert.Null(handle.TranslatePoint(default, owner));

            HideAfterIdle(viewer);
            Layout(overlay);
            Assert.True(handle.IsEffectivelyVisible);
            Assert.False(footer.IsEffectivelyVisible);
            MoveInPicture(overlay);
            Assert.True(footer.IsEffectivelyVisible);
            Assert.False(handle.IsEffectivelyVisible);
            Assert.False(handle.IsHitTestVisible);

            HideAfterIdle(viewer);
            Assert.True(surface.Focus(NavigationMethod.Tab));
            overlay.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            overlay.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            Layout(overlay);
            Assert.True(footer.IsEffectivelyVisible);
            Assert.False(handle.IsHitTestVisible);
        }
        finally
        {
            Field("_nativeOverlayActive").SetValue(viewer, false);
            Invoke(viewer, "ApplyOverlayPlacement");
            Layout(owner);
            overlay.Close();
        }
    });

    [Fact]
    public void FocusedHandleRefreshesItsTooltipAndAutomationNameAcrossAllLanguages() => WithGallery((viewer, host, _) =>
    {
        Texts.Initialize();
        HideAfterIdle(viewer);
        Layout(host);
        Button handle = Find<Button>(viewer, "GalleryControlsEdgeIndicator");
        Assert.True(handle.Focus(NavigationMethod.Tab));
        Layout(host);
        const string key = "gallery.overlay.showControls";
        Texts.SetLanguage("en");
        string english = Texts.T(key);
        ToolTip.SetShowDelay(handle, 0);
        host.MouseMove(BoundsIn(handle, host).Center);
        Layout(host);
        var tip = Assert.IsType<ToolTip>(ToolTip.GetTip(handle));
        Assert.True(ToolTip.GetIsOpen(handle));
        foreach (string code in Languages)
        {
            Texts.SetLanguage(code);
            Layout(host);
            Assert.Equal(code, Texts.Language);
            string name = Texts.T(key);
            if (code != "en") Assert.NotEqual(english, name);
            Assert.Equal(name, AutomationProperties.GetName(handle));
            Assert.Equal(name, AutomationProperties.GetHelpText(handle));
            Assert.Same(tip, ToolTip.GetTip(handle));
            Assert.True(ToolTip.GetIsOpen(handle));
            Popup tooltip = Assert.Single(host.OpenedPopups, popup => ReferenceEquals(handle, popup.PlacementTarget));
            Assert.Same(tip, tooltip.Child);
            Assert.Equal(name, tip.Content);
            Assert.Same(handle, host.FocusManager!.GetFocusedElement());
        }
    });

    private static void MoveInPicture(Window host)
    {
        host.MouseMove(new Point(350, 250));
        host.MouseMove(new Point(380, 270));
        Layout(host);
    }

    private static void AssertHandleOutOfTheWay(GalleryViewer viewer)
    {
        Button handle = Find<Button>(viewer, "GalleryControlsEdgeIndicator");
        Assert.False(handle.IsEffectivelyVisible);
        Assert.False(handle.IsHitTestVisible);
        Assert.Equal(0, Chevron(handle).Opacity);
    }

    private static Avalonia.Controls.Shapes.Path Chevron(Button handle) =>
        Assert.Single(handle.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>());

    private static void WithGallery(Action<GalleryViewer, Window, GalleryEntry> test, double scaling = 1) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        TransientPopupPolicy.Install();
        BooruComponentDispatcher.Wait(Exercise());

        async Task<bool> Exercise()
        {
            string directory = TemporaryWorkspacePaths.CreateDirectory("gallery-controls-handle-");
            try
            {
                await using var viewer = new GalleryViewer();
                viewer.ApplyWorkView(WorkView.Advanced);
                viewer.ConfigurePlayback(new GalleryPlaybackPreferences(OverlayControls: true, AutoHideControls: true));
                viewer.SetPosition("2 of 21", true, true);
                Window host = new() { Width = 1280, Height = 800, Content = viewer, Classes = { MotionPolicy.OffClass } };
                try
                {
                    host.SetRenderScaling(scaling);
                    host.Show();
                    host.PlatformImpl!.Activated?.Invoke();
                    Layout(host);
                    Assert.True(host.IsActive);
                    GalleryEntry entry = ImageEntry(directory);
                    await viewer.ShowAsync(entry, [entry], 1, CancellationToken.None);
                    Layout(host);
                    Assert.IsAssignableFrom<Bitmap>(Find<Image>(viewer, "GalleryImage").Source);
                    test(viewer, host, entry);
                }
                finally { host.Close(); }
            }
            finally { Directory.Delete(directory, recursive: true); }
            return true;
        }
    });

    private static GalleryEntry ImageEntry(string directory)
    {
        const string name = "picture.png";
        using SKBitmap bitmap = new(320, 180);
        bitmap.Erase(SKColors.SteelBlue);
        using SKData png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        byte[] bytes = png.ToArray();
        string path = Path.Combine(directory, name);
        File.WriteAllBytes(path, bytes);
        MediaAssetReference asset = new("picture", "picture", "original", name, path, bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)), "", name, true);
        MediaAssetSnapshot manifest = new(directory, MediaManifestStatus.Available, 1, "fixture", [asset]);
        return new(new(new("fixture", "g", "42"), directory, asset.AssetId), manifest, asset, null, null, false, false);
    }

    private static void HideAfterIdle(GalleryViewer viewer)
    {
        Field("_lastOverlayActivity").SetValue(viewer, Stopwatch.GetTimestamp() - 60 * Stopwatch.Frequency);
        Invoke(viewer, "OnViewerInteractionTick");
    }

    private static FieldInfo Field(string name) => typeof(GalleryViewer).GetField(name, Private)!;
    private static void Invoke(GalleryViewer viewer, string method, params object[] args) =>
        typeof(GalleryViewer).GetMethod(method, Private)!.Invoke(viewer, args);

    private static void Layout(Window host)
    {
        Dispatcher.UIThread.RunJobs();
        host.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static T Find<T>(Control root, string name) where T : Control =>
        Assert.Single(root.GetVisualDescendants().OfType<T>(), control => control.Name == name);

    private static Rect BoundsIn(Control control, Window host) => new(control.TranslatePoint(default, host)!.Value, control.Bounds.Size);

    private static bool Hits(Visual? hit, Control target) => hit?.GetSelfAndVisualAncestors().Contains(target) == true;

    private sealed class TaggingHost(GalleryEntry entry) : IGalleryTagPanelOwner, ITaggingQueueWriter
    {
        public IReadOnlyList<GalleryEntry> Entries { get; } = [entry];
        public GalleryTagPanelPosition? Position => new(1, 1, false, Queue: true);
        public TagRecentList Recent { get; } = new();
        public QuickTagOptions Options => QuickTagOptions.Default;
        public TagContextOptions ContextOptions => TagContextOptions.Default;
        public TagQueueSnapshot Current { get; } = new(1, 0, [], [], TagQueueLoadState.Ready);
        public MediaTagSubject? SubjectOf(GalleryEntry selected, out string? unavailableReason)
        {
            unavailableReason = "Fixture has no tag store.";
            return null;
        }
        public IQuickTagTarget? OpenTarget(GalleryEntry selected, out string? unavailableReason)
        {
            SubjectOf(selected, out unavailableReason);
            return null;
        }
        public IQuickTagTarget? OpenSelectionTarget(IReadOnlyList<MediaTagSubject> subjects, out string? unavailableReason) =>
            OpenTarget(Entries[0], out unavailableReason);
        public BooruCatalogSnapshot? ReadSourceTags() => null;
        public GalleryThreadTagModel OpenThreadTags(ResourceId thread) => throw new NotSupportedException();
        public void OpenAdvanced(GalleryEntry selected) => throw new NotSupportedException();
        public Task<TagQueueDecision?> DecideAsync(TagQueueKey key, TagQueueState state, CancellationToken token) => throw new NotSupportedException();
        public Task<TagQueueDecision?> UndoAsync(TagQueueDecision decision, CancellationToken token) => throw new NotSupportedException();
    }
}
