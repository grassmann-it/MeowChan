using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.Http;

/// <summary>
/// Preview bytes of a booru post-search source (the small provider thumbnail shown next to a search result).
/// Kept as the public, previews-only face of the internal remote-image path (<c>BooruRemoteImageHttp</c>), which also serves
/// post samples and holds all rules: declared <see cref="HttpHostPurpose.PreviewMedia"/> hosts only, bodyless https GET,
/// no redirects, no credentials, at most <see cref="MaximumPreviewBytes"/> per preview body, an image Content-Type and
/// signature check, a 15 s response deadline, one concurrency gate and a media fence after a provider 429/503 shared with the
/// samples, and an in-memory cache bounded by bytes. Nothing is stored on disk. Any failure is "no preview" (null) and the
/// caller shows a placeholder.
/// </summary>
public sealed class BooruPreviewHttp : IDisposable
{
    public const int MaximumPreviewBytes = BooruRemoteImageHttp.MaximumPreviewBytes;
    /// <summary>The default of the shared preview/sample request gate (a setting from 1 to 4).</summary>
    public const int MaximumConcurrentRequests = BooruRemoteImageLimits.DefaultConcurrentRequests;
    /// <summary>Kept for compiled consumers only: the preview cache is now bounded by bytes, not by entries.</summary>
    public const int MaximumCachedPreviews = 120;
    public static readonly TimeSpan ResponseTimeout = BooruRemoteImageHttp.PreviewResponseTimeout;

    /// <param name="transport">Borrowed transport; it must not follow redirects and must not attach credentials.</param>
    /// <param name="source">The post-search source whose declared preview hosts form the allowlist.</param>
    /// <param name="productVersion">Product version for the User-Agent; null uses <see cref="BooruUserAgent.ProductVersion"/>.</param>
    /// <param name="timeProvider">Time provider for the response deadline; null uses <see cref="TimeProvider.System"/>.</param>
    public BooruPreviewHttp(HttpClient transport, SourceDescription source, string? productVersion = null,
        TimeProvider? timeProvider = null)
        : this(transport, source, null, productVersion, timeProvider, null)
    {
    }

    /// <summary>Host wiring with the user's image limits and provider cooldown maximum (Booru B5 settings).</summary>
    internal BooruPreviewHttp(HttpClient transport, SourceDescription source, BooruRemoteImageLimits? limits,
        string? productVersion = null, TimeProvider? timeProvider = null, Func<TimeSpan>? maximumCooldownProvider = null)
    {
        Images = new BooruRemoteImageHttp(transport, source, limits, productVersion, timeProvider, maximumCooldownProvider);
    }

    /// <summary>The shared remote-image path behind this wrapper; samples use it with <see cref="BooruMediaRole.Sample"/>.</summary>
    internal BooruRemoteImageHttp Images { get; }

    /// <summary>Number of preview and sample requests that reached the transport (diagnostics and tests).</summary>
    public int RequestCount => Images.RequestCount;

    /// <summary>True when <paramref name="url"/> may be requested at all.</summary>
    public bool IsAllowed(Uri? url) => Images.IsAllowed(BooruMediaRole.Preview, url);

    /// <summary>
    /// The preview bytes, or null when the URL is not allowed or the provider did not deliver a small image.
    /// Only the caller's cancellation is thrown.
    /// </summary>
    public async Task<byte[]?> LoadAsync(Uri? url, CancellationToken cancellationToken = default)
    {
        BooruRemoteImageResult result = await Images.LoadAsync(BooruMediaRole.Preview, url, cancellationToken).ConfigureAwait(false);
        return result.Status == BooruRemoteImageStatus.Loaded ? result.Bytes : null;
    }

    /// <summary>JPEG, PNG, GIF or WebP signature; anything else never reaches an image decoder.</summary>
    public static bool LooksLikeImage(ReadOnlySpan<byte> bytes) => BooruRemoteImageHttp.LooksLikeImage(bytes);

    /// <summary>
    /// Idempotent. Queued and running previews and samples are aborted and return null without caching; later loads never
    /// reach the transport. The borrowed transport is not disposed.
    /// </summary>
    public void Dispose() => Images.Dispose();
}
