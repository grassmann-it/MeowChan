using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MeowChan;

public partial class ArchivePage
{
    internal async Task ReleaseMediaForPermanentDeletionAsync(IReadOnlyList<ResourceId> ids)
    {
        if (_selectedThread == null || !ids.Contains(_selectedThread)) return;
        ++_contentRequest;
        _contentCancellation?.Cancel();
        CancelMediaLoad(); ClosePreview();
        _thread = null; _selectedThread = null;
        _assets = System.Array.Empty<MeowChan.Desktop.ArchiveAssetRow>();
        _showReader = false; _busy = false;
        RenderThread(); UpdateLayoutMode();
        try { await MediaLoadTask; }
        catch (System.OperationCanceledException) { }
    }
}
