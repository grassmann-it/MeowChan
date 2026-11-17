using System.Collections;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;

namespace MeowChan.DesktopTests;

[Collection("Avalonia global state")]
public sealed class PointerFocusReclaimOwnerLifetimeTests
{
    [Fact]
    public void StoppingTheViewerRemovesReclaimFromBothOverlayRoots() => WithControls(() =>
    {
        GalleryViewer viewer = new();
        Control native = Field<Control>(viewer, "_nativeVideoOverlay");
        Assert.Equal(1, ReclaimHandlers(viewer));
        Assert.Equal(1, ReclaimHandlers(native));
        viewer.StopAsync().GetAwaiter().GetResult();
        viewer.StopAsync().GetAwaiter().GetResult();
        Assert.Equal(0, ReclaimHandlers(viewer));
        Assert.Equal(0, ReclaimHandlers(native));
    });

    [Fact]
    public void DisposingThePlayerRemovesReclaimFromItsTransport() => WithControls(() =>
    {
        GalleryVideoPlayer player = new();
        Control transport = Field<Control>(player, "_transportPanel");
        Assert.Equal(1, ReclaimHandlers(transport));
        Settle(player.DisposeAsync().AsTask());
        Settle(player.DisposeAsync().AsTask());
        Assert.Equal(0, ReclaimHandlers(transport));
    });

    private static void Settle(Task task)
    {
        if (!task.IsCompleted)
        {
            Avalonia.Threading.DispatcherFrame frame = new();
            task.GetAwaiter().OnCompleted(() => Avalonia.Threading.Dispatcher.UIThread.Post(() => frame.Continue = false));
            Avalonia.Threading.Dispatcher.UIThread.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }

    private static void WithControls(Action test) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
        TokenApplication.Bind();
        test();
    });

    private static T Field<T>(object owner, string name) => (T)owner.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

    // Inspect the real routed-event store. Other PointerReleased handlers on the
    // same control must survive disposal, so count only the reclaim's delegates.
    private static int ReclaimHandlers(Control scope)
    {
        var events = (IDictionary?)typeof(Interactive).GetField("_eventHandlers", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(scope);
        if (events?[InputElement.PointerReleasedEvent] is not IEnumerable handlers) return 0;
        return handlers.Cast<object>().Count(subscription =>
            ((Delegate)subscription.GetType().GetProperty("Handler")!.GetValue(subscription)!).Target is PointerFocusReclaim);
    }
}
