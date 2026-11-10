using System;
using System.Threading;

namespace MeowChan.Desktop;

internal sealed partial class DesktopSessionHost
{
    // A read milestone, never an authority: the startup owner has opened the catalog (schema and
    // migration are done) and no native media transfer recovery is pending. It grants no write,
    // engine or command readiness; those stay bound to Ready and profile admission.
    private int _catalogReadable;
    internal bool CatalogReadable => Volatile.Read(ref _catalogReadable) == 1
        && State is DesktopSessionState.Starting or DesktopSessionState.Ready;
    internal event Action? CatalogReadableChanged;
    internal Func<CancellationToken, System.Threading.Tasks.Task>? AfterCatalogReadableForTests { get; set; }

    private void PublishCatalogReadable()
    {
        lock (_lifecycle)
        {
            if (_state != DesktopSessionState.Starting) return; // Stop or failure won: publish nothing late.
            Volatile.Write(ref _catalogReadable, 1);
        }
        foreach (Delegate observer in CatalogReadableChanged?.GetInvocationList() ?? Array.Empty<Delegate>())
            try { ((Action)observer)(); } catch (Exception error) { _logger.Debug(error, "Catalog readable observer failed."); }
    }
}
