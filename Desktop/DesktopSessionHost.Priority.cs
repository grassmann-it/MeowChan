using System.Threading.Tasks;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.PluginHost.Sources;

namespace MeowChan.Desktop;

internal sealed partial class DesktopSessionHost
{
    internal Task<bool> SetUserThreadPriorityAsync(long watchId, bool high)
        => RunInteractiveAsync(nameof(SetUserThreadPriorityAsync), watchId.ToString(), async () =>
        {
            await _commands.WaitAsync().ConfigureAwait(false);
            try
            {
                EnsureReady();
                IThread? thread;
                lock (_projection) _threadHandles.TryGetValue(watchId, out thread);
                if (thread == null || Projection.IsStale || thread.Board.ImageBoard is PluginSourceImageBoard { Enabled: false }) return false;
                return _downloadQueue.SetCurrentUserPriority(thread, high ? UserDownloadPriority.High : UserDownloadPriority.Normal);
            }
            finally { _commands.Release(); SignalChanged(); }
        });
}
