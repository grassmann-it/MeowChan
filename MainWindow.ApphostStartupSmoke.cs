using System;
using System.Threading.Tasks;
using MeowChan.Desktop;

namespace MeowChan;

public partial class MainWindow
{
    internal async Task CompleteApphostStartupSmokeAsync(TimeSpan dwell)
    {
        await _restoreTask;
        if (!_restoreCompleted || _session.State != DesktopSessionState.Ready)
            throw new InvalidOperationException("The startup session did not become ready.");
        // Yield to the real UI dispatcher while the native window is visible.
        await Task.Delay(dwell);
        if (!IsVisible || _isClosing)
            throw new InvalidOperationException("The startup window closed before verification finished.");
        await CloseApplicationAsync();
        if (!_allowClose || IsVisible)
            throw new InvalidOperationException("The startup window did not complete normal shutdown.");
    }
}
