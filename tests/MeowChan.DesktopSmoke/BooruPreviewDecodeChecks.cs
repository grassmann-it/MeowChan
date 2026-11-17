using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Booru;
using MeowChan.PluginSdk;
using MeowChan.Tests.Shared;
using SkiaSharp;

namespace MeowChan.DesktopSmoke;

internal static class BooruPreviewDecodeChecks
{
    internal static async Task RunAsync(Window owner)
    {
        // The exact review boundary: tiny encoded PNG, 40 MP decoded, portrait 500x500 fit.
        byte[] bytes;
        using (SKBitmap source = new(5000, 8000))
        {
            source.Erase(SKColors.Blue);
            using SKImage image = SKImage.FromBitmap(source);
            using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
            bytes = data.ToArray();
        }
        bool worker = false;
        BooruPreviewDecoder decoder = new((stream, target) =>
        {
            worker = !Dispatcher.UIThread.CheckAccess();
            return GalleryImageDecoder.DecodeFit(stream, target);
        });
        var decoded = await decoder.DecodeAsync(bytes, new GalleryDecodeRequest.Fit(500, 500), CancellationToken.None);
        using (decoded.Bitmap)
        {
            Require(worker, "Booru codec ran on the UI thread.");
            Require(decoded.Bitmap.PixelSize.Width == 313 && decoded.Bitmap.PixelSize.Height == 500,
                "40 MP preview ignored its 500x500 fit target.");
            Require(decoded.Info.Width == 5000 && decoded.Info.Height == 8000, "Source geometry was lost.");
        }

        // A native codec cannot stop mid-call. Cancellation after it starts must reject its result.
        using ManualResetEventSlim release = new();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource cancellation = new();
        decoder = new((stream, target) =>
        {
            started.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Slow codec was not released.");
            return GalleryImageDecoder.DecodeFit(stream, target);
        });
        Task pending = decoder.DecodeAsync(bytes, new GalleryDecodeRequest.Fit(50, 50), cancellation.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            release.Set();
            try { await pending; throw new InvalidOperationException("Canceled pixels escaped the decoder."); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
        finally { release.Set(); }

        await CheckViewerCancellationAsync(owner, close: false);
        await CheckViewerCancellationAsync(owner, close: true);
        Console.WriteLine("SMOKE BOORU ASYNC DECODE PASS: 40 MP fit, worker thread, in-flight cancellation, rapid navigation, close and stale-result rejection.");
    }

    private static async Task CheckViewerCancellationAsync(Window owner, bool close)
    {
        BooruUiHarness h = new();
        h.Search.OnSearch = (query, page, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(
            BooruUiFixtures.Page(query, page, true, BooruUiFixtures.Post("801"), BooruUiFixtures.Post("802"))));
        using BooruWorkbenchModel model = new(h.Services);
        model.Search.SetInput("cat");
        await model.Search.SubmitAsync();
        await Program.WaitUntilAsync(() => model.Search.View.Rows.Count(row => row.Preview == BooruPreviewState.Ready) == 2);
        using ManualResetEventSlim release = new();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        Bitmap? stale = null;
        BooruPreviewDecoder decoder = new((stream, target) =>
        {
            Require(!Dispatcher.UIThread.CheckAccess(), "Slow decoder blocked the dispatcher.");
            if (Interlocked.Increment(ref calls) != 1) return GalleryImageDecoder.DecodeFit(stream, target);
            started.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Navigation/close blocked the dispatcher.");
            stale = GalleryImageDecoder.DecodeFit(stream, target);
            completed.TrySetResult();
            return stale;
        });
        BooruResultViewerWindow viewer = new(model.Search, "801", new(), () => { }, decoder);
        try
        {
            viewer.Show(owner);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Image image = viewer.GetVisualDescendants().OfType<Image>().Single(control => control.Name == "GalleryImage");
            // These dispatcher actions must complete while the worker is deliberately blocked.
            if (close) viewer.Close();
            else
            {
                Send(viewer, Key.Right);
                Send(viewer, Key.Left);
                Send(viewer, Key.Right);
                Require(viewer.CurrentPostId == "802", "Rapid navigation did not reach the current index.");
            }
            release.Set();
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (close)
            {
                await viewer.Cleanup;
                await SmokeDispatcher.SettleAsync();
                Require(!viewer.IsVisible && image.Source == null, "Close adopted canceled pixels.");
            }
            else
            {
                await Program.WaitUntilAsync(() => image.Source is Bitmap);
                await SmokeDispatcher.SettleAsync();
                Require(!ReferenceEquals(image.Source, stale), "Superseded index replaced the current image.");
            }
        }
        finally
        {
            release.Set();
            viewer.Close();
            await viewer.Cleanup;
            await viewer.DisposeAsync();
        }
    }

    private static void Send(Control source, Key key) => source.RaiseEvent(new KeyEventArgs
    { RoutedEvent = InputElement.KeyDownEvent, Source = source, Key = key });

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
