using System;
using System.Threading;
using System.Threading.Tasks;

namespace MeowChan.Desktop.Booru;

internal sealed partial class BooruSearchModel
{
    // The model holds one reference, and each operation keeps its token usable until it finishes.
    // Acquire under the model lock while the lifetime still belongs to the model.
    private sealed class RequestLifetime : IDisposable
    {
        private readonly CancellationTokenSource _source = new();
        private int _users = 1;

        internal CancellationToken Token => _source.Token;

        internal RequestLifetime Acquire()
        {
            Interlocked.Increment(ref _users);
            return this;
        }

        internal async Task RetireAsync()
        {
            try { await _source.CancelAsync().ConfigureAwait(false); }
            catch (AggregateException exception) { Serilog.Log.Warning(exception, "Booru request cancellation observer failed"); }
            finally { Dispose(); }
        }

        public void Dispose()
        {
            if (Interlocked.Decrement(ref _users) == 0) _source.Dispose();
        }
    }

    private RequestLifetime? AcquireModelLifetime()
    {
        lock (_gate) return _disposed ? null : _lifetime.Acquire();
    }

    private RequestLifetime? ReplaceRequest(bool needsRequest)
    {
        RequestLifetime? previous = _request;
        _request = needsRequest ? new RequestLifetime() : null;
        _requestId++;
        if (previous != null) OpenActions.Start(previous.RetireAsync, "booru.request.retire");
        return _request;
    }
}
