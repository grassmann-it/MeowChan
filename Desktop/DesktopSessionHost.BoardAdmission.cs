using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Admission;
using MeowChan.Persistence;

namespace MeowChan.Desktop;

internal sealed partial class DesktopSessionHost
{
    internal BoardAdmissionPolicy GetBoardAdmissionPolicy(string url) => _registry.GetBoardAdmissionPolicy(url);
    // Background compatibility entry point. UI callers use ClassifyAdmissionAsync.
    internal BoardAdmissionTarget? ClassifyAdmission(string url) => _registry.ClassifyAdmission(url);
    internal Task<BoardAdmissionTarget?> ClassifyAdmissionAsync(string url)
        => RunInteractiveAsync<BoardAdmissionTarget?>(nameof(ClassifyAdmissionAsync), url,
            () => Task.FromResult(_registry.ClassifyAdmission(url)), captureAfter: false);
    internal Task<BoardAdmissionPreview> PreviewBoardAdmissionAsync(string url, CancellationToken token = default)
        => RunInteractiveAsync(nameof(PreviewBoardAdmissionAsync), url, () => PreviewBoardAdmissionAdmittedCoreAsync(url, token), captureAfter: false);

    private Task<BoardAdmissionPreview> PreviewBoardAdmissionAdmittedCoreAsync(string url, CancellationToken token = default)
    {
        EnsureReady();
        return _registry.PreviewBoardAdmissionAsync(url, token);
    }
    internal Task<DesktopAdmissionResult> ApplyBoardAdmissionAsync(string url, BoardAdmissionPolicy policy, IReadOnlyCollection<string>? reactivateThreadUrls = null)
    {
        string[]? frozen = reactivateThreadUrls?.ToArray();
        return RunInteractiveWithAdmissionAsync(nameof(ApplyBoardAdmissionAsync), url.Trim(), admission => ApplyBoardAdmissionAdmittedCoreAsync(url, policy, frozen, admission));
    }

    private async Task<DesktopAdmissionResult> ApplyBoardAdmissionAdmittedCoreAsync(string url, BoardAdmissionPolicy policy, IReadOnlyCollection<string>? reactivateThreadUrls, ProfileAdmissionToken admission)
    {
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureReady();
            AdmissionResult result = await _registry.ApplyBoardAdmissionAdmittedAsync(url, policy, reactivateThreadUrls, admission).ConfigureAwait(false);
            await CaptureProjectionAsync().ConfigureAwait(false);
            long? watchId = null;
            lock (_projection)
                if (result.Board != null && _boards.TryGetValue(result.Board, out WatchIdentity? identity)) watchId = identity.Id;
            return new(result.Status, watchId);
        }
        finally { _commands.Release(); }
    }
}
