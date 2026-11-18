using System;
using System.Threading.Tasks;

namespace MeowChan;

public partial class ArchivePage
{
    private bool CanChangeExportChoice => ExportRequestIsCurrent(_exportRequest) && !_busy && !IsFilenameSearchOpen
        && !_exportRunning && !_exportPicking && !_urlTextExportBusy && !IsArchiveCleanupOpen && _openReader != null;

    private ResourceId? FrozenLinkListThread => _exportSessionMembers.Length == 1
        && _exportSessionMembers[0] is ResourceId id && !string.IsNullOrWhiteSpace(id.Source)
        && !string.IsNullOrWhiteSpace(id.Board) && !string.IsNullOrWhiteSpace(id.Thread) ? id : null;

    // Return the entry's exact frozen object, never the first match of an all-results request.
    // A stale or mismatched single-thread draft must not reach the consent window either.
    internal ResourceId? LinkListReviewThread => !_exportAllResults && FrozenLinkListThread is ResourceId id
        && _exportThread == id && (_exportMembers == null || _exportMembers.Length == 1 && _exportMembers[0] == id)
        ? id : null;

    // The callbacks are the existing scope entry and dialog visibility boundary. Keeping the
    // request/session checks here also lets deterministic tests exercise the real page state
    // without opening a dialog, touching a profile or running destination/history I/O.
    internal async Task<bool> TryChooseLinkListAsync(Func<Task> switchToSelection, Func<bool> dialogIsVisible)
    {
        ResourceId[] session = _exportSessionMembers;
        ResourceId? frozen = FrozenLinkListThread;
        if (!CanChangeExportChoice || !dialogIsVisible() || frozen == null) return false;
        int request = _exportRequest;
        if (_exportAllResults)
        {
            // OpenExportCoreAsync accepts a switch (and increments its request) synchronously,
            // before its first await. A completed task alone does not prove acceptance.
            Task switching = switchToSelection();
            int switchedRequest = _exportRequest;
            await switching;
            if (switchedRequest != unchecked(request + 1) || _exportLookupCancellation?.IsCancellationRequested == true) return false;
            request = switchedRequest;
        }
        if (request != _exportRequest || !ReferenceEquals(session, _exportSessionMembers)
            || !CanChangeExportChoice || !dialogIsVisible() || _exportLoading || _exportResolving
            || LinkListReviewThread != frozen) return false;
        _exportFormat = ExportFormat.LinkList;
        return true;
    }
}
