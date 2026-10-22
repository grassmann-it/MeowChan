using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Sources;
using MeowChan.PluginSdk;

namespace MeowChan.Desktop.Plugins;

/// <summary>A metadata-only operation with no registry, capture, archive or original-media dependencies.</summary>
internal static class PluginProbe
{
    internal static async Task<object> ReadAsync(PluginDescription description, string url, IPluginWorkerClient worker,
        PluginOperationContext operation, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) throw new ArgumentException(Localization.Texts.T("plugins.invalidUrl"));
        SourceDescription? selected = null;
        SourceResourceKind kind = default; string board = string.Empty; string? thread = null;
        foreach (SourceDescription source in description.Sources)
        {
            PluginSourceRouter router = new(source);
            if (!router.TryResolve(uri, out SourceResourceKind foundKind, out string foundBoard, out string? foundThread, out _)) continue;
            if (selected != null) throw new ArgumentException("The URL is ambiguous between plugin sources.");
            selected = source; kind = foundKind; board = foundBoard; thread = foundThread;
        }
        if (selected == null) throw new ArgumentException(Localization.Texts.T("plugins.invalidUrl"));
        return kind == SourceResourceKind.Thread
            ? await worker.ReadThreadAsync(new(new(selected.SourceId, board, thread!)), operation, cancellationToken)
            : await worker.ReadCatalogAsync(new(new(selected.SourceId, board)), operation, cancellationToken);
    }
}
