using System;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.PluginHost.Http;

namespace MeowChan.Desktop.Booru;

/// <summary>
/// The reduced-size image (sample) of one post for the gallery, through the same safe preview path as the thumbnails. Called only
/// for the post the person has open, never for a page ahead. An empty result means "no sample" (not offered, refused by the size limit or not
/// delivered); the gallery then keeps the preview and says so.
/// </summary>
internal interface IBooruSamplePort
{
    /// <param name="maximumBytes">The largest body accepted (the setting in MiB, as bytes).</param>
    /// <param name="cacheBytes">How many bytes of samples may be kept in memory (the setting in MiB, as bytes).</param>
    /// <param name="responseTimeout">How long to wait for the response.</param>
    Task<BooruSampleResult> LoadSampleAsync(BooruPostView post, int maximumBytes, long cacheBytes, TimeSpan responseTimeout,
        CancellationToken cancellationToken);
}
