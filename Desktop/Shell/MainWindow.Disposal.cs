using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace MeowChan;

public partial class MainWindow : IAsyncDisposable
{
    /// <summary>
    /// Uses the same shutdown transaction as window close. A veto, an already-running close,
    /// or retained native/profile ownership is not successful disposal and is reported as such.
    /// Must be called on the window's dispatcher, like Close itself.
    /// </summary>
    private Task? _ownedResourceDisposal;

    public ValueTask DisposeAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        return new(_ownedResourceDisposal ??= DisposeWindowAsync());
    }

    private async Task DisposeWindowAsync()
    {
        await CloseApplicationAsync();
        if (!_allowClose)
            throw new InvalidOperationException("Window disposal did not complete; shutdown was vetoed, is running, or retains ownership.");

        await DisposeAsyncCore();
        GC.SuppressFinalize(this);
    }

    protected virtual async ValueTask DisposeAsyncCore()
    {
        // Only after the native/profile ownership barrier. Most UI helpers were already
        // released by ordinary close; their idempotent Dispose also completes this explicit
        // owner contract if a future close path leaves a terminal reference populated.
        _sessionRefresh.Dispose();
        _trayIcon?.Dispose();
        _trayLogo?.Dispose();
        _trayHostLifetime?.Dispose();
        _trayStats?.Dispose();
        _headerIoMenu?.Dispose();
        _headerThemeMenu?.Dispose();
        _ioUpdate?.Dispose();
        _ioClaim?.Dispose();
        _mediaFocusChromeTexts?.Dispose();
        _tagQueueRegistration?.Dispose();
        _experimentalAdmissionFocusReturn?.Dispose();
        _workspaceRefresh?.Dispose();
        _readingHistoryFlyout?.Dispose();
        _booruPage?.Dispose();
        _taggingPage?.Dispose();
        _manualImportDialog?.Dispose();
        _manualImportDialog = null;
        foreach (IDisposable registration in _profileCommandRegistrations) registration.Dispose();
        _profileCommandRegistrations.Clear();
        _booruAddLifetime.Cancel();
        try { await Task.WhenAll(_booruAdmissionWork.ToArray()); }
        finally { _booruAddLifetime.Dispose(); }
        _galleryCancellation?.Dispose();
        if (_rulesPage != null) await _rulesPage.DisposeAsync();
        if (_gallery != null) await _gallery.DisposeAsync();
        if (_galleryScrubHost != null) await _galleryScrubHost.DisposeAsync();
    }
}
