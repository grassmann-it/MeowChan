using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using MeowChan.PluginHost.Http;

namespace MeowChan.Desktop.Booru;

internal readonly record struct BooruGallerySampleLimits(int Bytes, long CacheBytes, int MaximumPixels, int MaximumWidth, int TimeoutSeconds)
{
    internal static BooruGallerySampleLimits FromSettings(int? sampleMiB = null, int? cacheMiB = null,
        int? maximumPixels = null, int? maximumWidth = null, int? timeoutSeconds = null) => new(
        Math.Clamp(sampleMiB ?? Config.BooruGallerySampleMiBDefault,
            Config.BooruGallerySampleMiBMinimum, Config.BooruGallerySampleMiBMaximum) * 1024 * 1024,
        Math.Clamp(cacheMiB ?? Config.BooruGalleryCacheMiBDefault,
            Config.BooruGalleryCacheMiBMinimum, Config.BooruGalleryCacheMiBMaximum) * 1024L * 1024,
        Math.Clamp(maximumPixels ?? Config.BooruGalleryMaximumPixelsDefault,
            Config.BooruGalleryMaximumPixelsMinimum, Config.BooruGalleryMaximumPixelsMaximum),
        Math.Clamp(maximumWidth ?? Config.BooruGalleryMaximumWidthDefault,
            Config.BooruGalleryMaximumWidthMinimum, Config.BooruGalleryMaximumWidthMaximum),
        Math.Clamp(timeoutSeconds ?? Config.BooruGalleryTimeoutSecondsDefault,
            Config.BooruGalleryTimeoutSecondsMinimum, Config.BooruGalleryTimeoutSecondsMaximum));
}

/// <summary>The sample of the active post. Its viewer owns the preview and decides when viewing permits a request.</summary>
internal sealed class BooruGallerySampleLoader
{
    internal static readonly TimeSpan DefaultDelay = TimeSpan.FromMilliseconds(200);

    private Action? _cancel;
    internal BooruGallerySampleState State { get; private set; } = BooruGallerySampleState.NotOffered;
    internal BooruGallerySampleLimits Limits { get; private set; }

    internal void Reset(bool offered)
    {
        Pause();
        State = offered ? BooruGallerySampleState.Deferred : BooruGallerySampleState.NotOffered;
    }

    internal void Pause()
    {
        if (State != BooruGallerySampleState.Loading) return;
        _cancel?.Invoke();
        _cancel = null;
        State = BooruGallerySampleState.Deferred;
    }

    internal void Start(IBooruSamplePort? port, BooruPostView post, Func<BooruGallerySampleLimits>? settings,
        TimeSpan delay, Func<byte[], int, int, (IImage? Image, BooruGallerySampleState State)> decode,
        Func<IImage, bool> adopt, Action changed)
    {
        if (State != BooruGallerySampleState.Deferred || port is null) return;
        Limits = settings?.Invoke() ?? BooruGallerySampleLimits.FromSettings();
        State = BooruGallerySampleState.Loading;
        BooruGallerySampleLimits limits = Limits;
        OpenActions.Start(() => LoadAsync(port, post, limits, delay, decode, adopt, changed), "booru.gallery.sample");
    }

    private async Task LoadAsync(IBooruSamplePort port, BooruPostView post, BooruGallerySampleLimits limits,
        TimeSpan delay, Func<byte[], int, int, (IImage? Image, BooruGallerySampleState State)> decode,
        Func<IImage, bool> adopt, Action changed)
    {
        using CancellationTokenSource cancellation = new();
        _cancel = () => { try { cancellation.Cancel(); } catch (ObjectDisposedException) { } };
        CancellationToken token = cancellation.Token;
        IImage? image = null;
        try
        {
            BooruGallerySampleState state;
            try
            {
                await Task.Delay(delay, token);
                token.ThrowIfCancellationRequested();
                BooruSampleResult result = await port.LoadSampleAsync(post, limits.Bytes, limits.CacheBytes,
                    TimeSpan.FromSeconds(limits.TimeoutSeconds), token);
                state = result.TimedOut ? BooruGallerySampleState.TimedOut : BooruGallerySampleState.Failed;
                if (result.Bytes is { Length: > 0 } bytes && bytes.Length <= limits.Bytes)
                    (image, state) = await Task.Run(() => decode(bytes, limits.MaximumWidth, limits.MaximumPixels), token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Serilog.Log.Warning(exception, "A Danbooru gallery sample could not be shown");
                state = BooruGallerySampleState.Failed;
            }
            if (Dispatcher.UIThread.CheckAccess()) Publish();
            else await Dispatcher.UIThread.InvokeAsync(Publish);

            void Publish()
            {
                if (token.IsCancellationRequested) return;
                _cancel = null;
                if (image is not null)
                {
                    if (adopt(image)) image = null;
                    else state = BooruGallerySampleState.Deferred;
                }
                State = state;
                changed();
            }
        }
        finally { (image as IDisposable)?.Dispose(); }
    }
}
