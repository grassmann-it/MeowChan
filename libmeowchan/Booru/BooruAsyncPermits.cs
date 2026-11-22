using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace MeowChan.Booru;

/// <summary>Async permits with no native wait handle, usable until already-started work has drained.</summary>
internal sealed class BooruAsyncPermits
{
    private readonly Channel<bool> _tokens;

    internal BooruAsyncPermits(int count)
    {
        _tokens = Channel.CreateBounded<bool>(count);
        for (int index = 0; index < count; index++) Release();
    }

    internal async ValueTask WaitAsync(CancellationToken cancellationToken = default) =>
        _ = await _tokens.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

    internal void Release()
    {
        if (!_tokens.Writer.TryWrite(true)) throw new InvalidOperationException("A Booru permit was released twice.");
    }
}
