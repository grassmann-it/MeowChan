using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace MeowChan;

public partial class MainWindow
{
    private Size? _captionRestoreSize;
    private string? _captionRestoreMode;
    private bool _captionRestorePending;
    private int _captionRestoreQueuedGeneration = -1;
    private Size _captionRestoreFromSize;
    private int _captionRestoreGeneration;

    private void CompleteCaptionRestore()
    {
        ++_captionRestoreGeneration;
        if (WindowState != WindowState.Normal)
        {
            _captionRestorePending = false;
            return;
        }
        ContinueCaptionRestore();
    }

    private void ContinueCaptionRestore()
    {
        if (!_captionRestorePending || WindowState != WindowState.Normal || _isClosing
            || _captionRestoreSize is not Size size) return;
        if (_config.WorkspaceMode != _captionRestoreMode
            || (Bounds.Size != size && Bounds.Size != _captionRestoreFromSize)
            || (Width != size.Width && Width != _captionRestoreFromSize.Width)
            || (Height != size.Height && Height != _captionRestoreFromSize.Height))
        {
            // An unrelated normal resize or mode change supersedes this restore.
            _captionRestorePending = false;
            return;
        }
        int generation = _captionRestoreGeneration;
        if (_captionRestoreQueuedGeneration == generation) return;
        _captionRestoreQueuedGeneration = generation;
        // X11 acknowledges Normal before its queued ConfigureNotify is drained.
        // The old maximized size can arrive even after one correct normal frame;
        // watch that exact stale size until a new state/resize supersedes restore.
        Dispatcher.UIThread.Post(() =>
        {
            if (_captionRestoreQueuedGeneration == generation)
                _captionRestoreQueuedGeneration = -1;
            if (_isClosing || generation != _captionRestoreGeneration || !_captionRestorePending
                || WindowState != WindowState.Normal || _config.WorkspaceMode != _captionRestoreMode
                || Bounds.Size != _captionRestoreFromSize
                || Width != _captionRestoreFromSize.Width || Height != _captionRestoreFromSize.Height) return;
            Width = size.Width;
            Height = size.Height;
        }, DispatcherPriority.Background);
    }
}
