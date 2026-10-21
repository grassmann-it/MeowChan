using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace MeowChan;

public partial class MainWindow
{
    private Size? _captionRestoreSize;
    private string? _captionRestoreMode;
    private bool _captionRestorePending;
    private Size _captionRestoreFromSize;
    private int _captionRestoreGeneration;

    private void CompleteCaptionRestore()
    {
        int generation = ++_captionRestoreGeneration;
        if (WindowState != WindowState.Normal) return;
        Size? restoreSize = _captionRestorePending ? _captionRestoreSize : null;
        _captionRestorePending = false;
        _captionRestoreSize = null;
        if (restoreSize is not Size size) return;
        string? mode = _captionRestoreMode;
        Size maximizedSize = _captionRestoreFromSize;
        // Let the native restore and managed decoration layout run first. In the
        // custom-caption path X11 can otherwise retain the maximized client size.
        Dispatcher.UIThread.Post(() =>
        {
            if (_isClosing || generation != _captionRestoreGeneration || WindowState != WindowState.Normal
                || _config.WorkspaceMode != mode || Bounds.Size != maximizedSize
                || Width != maximizedSize.Width || Height != maximizedSize.Height) return;
            Width = size.Width;
            Height = size.Height;
        }, DispatcherPriority.Background);
    }
}
