using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using MeowChan.Desktop;

namespace MeowChan;

public partial class MainWindow
{
    private bool _admissionArchiveOpened;

    private Task ShowBoardArchiveAsync(BoardArchiveLocation initial, IEnumerable<BoardArchiveLocation> additional)
    {
        BoardArchiveLocation[] boards = additional.Concat(BoardsList
                .Select(row => _session.FindBoardArchive(row.Url, fromBoard: true)).OfType<BoardArchiveLocation>())
            .Prepend(initial).DistinctBy(board => board.Uri).ToArray();
        var window = new BoardArchiveWindow(boards, _session.ReadBoardArchiveAsync, _session.ReadBoardArchiveThreadAsync,
            thread => _session.AddAsync(thread.Url), thread => OpenTarget(thread.Url));
        return window.ShowDialog(this);
    }

    private async Task OpenBoardArchiveAsync(BoardArchiveLocation archive, Control origin)
    {
        if (!_restoreCompleted || _isClosing || _importing || _applyingPreferences) return;
        Action restore = CaptureDownloadSelectionOrigin(origin);
        _importing = true;
        try
        {
            UpdateInteractionState();
            await ShowBoardArchiveAsync(archive, Array.Empty<BoardArchiveLocation>());
        }
        finally
        {
            _importing = false;
            if (!_isClosing)
            {
                UpdateInteractionState();
                restore();
            }
        }
    }
}
