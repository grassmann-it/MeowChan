using System;
using System.Threading;
using System.Threading.Tasks;

namespace MeowChan.Desktop;

/// <summary>Transfers a source to work that outlives its launching method. Cancellation is not completion.</summary>
internal static class CancellationWork
{
    internal static async Task RunAsync(CancellationTokenSource source, Func<CancellationToken, Task> work, Action release)
    {
        using (source)
        {
            try { await work(source.Token); }
            finally { release(); }
        }
    }
}
