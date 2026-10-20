using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Admission;

namespace MeowChan.Desktop;

internal sealed partial class DesktopSessionHost
{
    internal BoardAdmissionPolicy GetBoardAdmissionPolicy(string url) => _registry.GetBoardAdmissionPolicy(url);
    internal BoardAdmissionTarget? ClassifyAdmission(string url) => _registry.ClassifyAdmission(url);
    internal Task<BoardAdmissionPreview> PreviewBoardAdmissionAsync(string url, CancellationToken token = default)
    {
        EnsureReady();
        return _registry.PreviewBoardAdmissionAsync(url, token);
    }
    internal async Task<DesktopAdmissionResult> ApplyBoardAdmissionAsync(string url, BoardAdmissionPolicy policy, IReadOnlyCollection<string>? reactivateThreadUrls = null)
    {
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureReady();
            AdmissionResult result = await _registry.ApplyBoardAdmissionAsync(url, policy, reactivateThreadUrls).ConfigureAwait(false);
            GetView();
            long? watchId = null;
            lock (_projection)
                if (result.Board != null && _boards.TryGetValue(result.Board, out WatchIdentity? identity)) watchId = identity.Id;
            return new(result.Status, watchId);
        }
        finally { _commands.Release(); }
    }
}
