using System;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Gallery;

namespace MeowChan.Desktop;

// Internal presentation boundary. Production uses only GifFrameDecoder's verified factory;
// controlled fixtures can fail actual opening/retirement without a native-codec bypass in UI.
internal interface IGifFrameDecoder : IAsyncDisposable
{
    GalleryImageInfo Info { get; }
    int FrameCount { get; }
    int RepetitionCount { get; }
    int DurationMs(int index);
    Task<GifDecodedFrame> DecodeAsync(int index, CancellationToken cancellationToken);
}

/// <summary>A completed task is not proof that this ownership unit successfully retired.</summary>
internal sealed class GifRetirementException : Exception
{
    internal object Owner { get; }
    internal GifRetirementException(object owner, Exception error)
        : base("GIF cleanup did not establish resource release; the owner is retained.", error) => Owner = owner;
}
