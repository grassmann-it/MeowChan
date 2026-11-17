using System.Reflection;
using Pointer = Avalonia.Input.Pointer;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MeowChan.Desktop;
using MeowChan.Gallery;

namespace MeowChan.DesktopTests;

// Reuse the real page/control fixture, not a duplicate click policy. Only state setup
// bypasses layout and decoding; pointer events still run the product's handlers.
internal static class MediaPanelClickTestSupport
{
    internal static void Run(Action<MediaTileEnterTests.Fixture, ReaderMediaSidebar> test) => MediaTileEnterTests.Run(fixture =>
    {
        fixture.ShowPanelOn(1);
        ReaderMediaSidebar panel = fixture.PanelView!;
        panel.Describe("Post 101", "1.jpg", "7 B", canOpen: true);
        panel.ShowPicture(new WriteableBitmap(new PixelSize(8, 8), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul),
            picture => picture.Dispose(), playable: false, double.PositiveInfinity, double.PositiveInfinity);
        test(fixture, panel);
    });

    internal static void Offer(MediaTileEnterTests.Fixture fixture, string mode, MediaLibraryTileRow? row)
    {
        ReaderInlineVideoOptions options = ReaderInlineVideoOptions.Default with
        {
            Enabled = mode != "Gallery", InPost = mode == "Post", SidebarAutoplay = false
        };
        // Exercise Gallery/default controller state too: ConfigurePanel normally forces
        // inline playback on, but cursor semantics must not depend on that normalization.
        fixture.Page.PanelVideoController.ConfigureAsync(options).GetAwaiter().GetResult();
        typeof(MediaPage).GetMethod("OfferPanelVideo", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Page, [row]);
    }

    internal static void Release(ReaderMediaSidebar panel, MouseButton button = MouseButton.Left)
    {
        PointerUpdateKind update = button == MouseButton.Right ? PointerUpdateKind.RightButtonReleased : PointerUpdateKind.LeftButtonReleased;
        panel.Frame.RaiseEvent(new PointerReleasedEventArgs(panel.Frame,
            new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true), panel.Frame, default, 0,
            new PointerPointProperties(RawInputModifiers.None, update), KeyModifiers.None, button)
            { RoutedEvent = InputElement.PointerReleasedEvent });
    }

    internal static void DoubleClick(ReaderMediaSidebar panel) => panel.Frame.RaiseEvent(
        new TappedEventArgs(InputElement.DoubleTappedEvent, new PointerEventArgs(InputElement.PointerReleasedEvent, panel.Frame,
            new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true), panel.Frame, default, 0,
            PointerPointProperties.None, KeyModifiers.None)));
}
